using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sutando.Bridge;
using Sutando.Workspace;
using Telegram.Bot;

namespace Sutando.Channels.Telegram;

/// <summary>
/// Telegram long-polling channel. Mirrors <c>Sutando.Channels.Cli.CliChatChannel</c> in
/// shape (DI-friendly ctor, an options record, a single <c>RunAsync</c> that owns the loop)
/// but glues the workspace task/result bridge to a Telegram bot.
/// </summary>
/// <remarks>
/// <para>
/// Inbound: each <c>Message</c> arriving via long-poll is mapped to a
/// <see cref="TaskEnvelope"/> with <c>Source=Telegram</c>, an access tier picked from the
/// owner/verified/team allow-lists, and a body that includes <c>[file: ...]</c> markers for
/// any photo / voice / document attachments. Unverified senders never enqueue a task — they
/// receive a one-line polite decline inline.
/// </para>
/// <para>
/// Outbound: a background pump watches <c>&lt;workspace&gt;/results/</c> for files matching
/// <c>task-tg-*.txt</c>, parses the marker prefix, and dispatches per the contract:
/// <c>[REPLIED]</c>, <c>[no-send]</c>, <c>[deduped: ...]</c> skip delivery; <c>[file: ...]</c>
/// markers upload the referenced file as a document. Text bodies are chunked at Telegram's
/// 4096-character ceiling. Results route to the correct chat by parsing the task id
/// (<c>task-tg-&lt;chatId&gt;-&lt;ms&gt;</c>).
/// </para>
/// <para>
/// The Telegram SDK is hidden behind <see cref="ITelegramGateway"/> so tests can drive the
/// channel entirely offline with a hand-written fake.
/// </para>
/// </remarks>
public sealed class TelegramChannel : IChannel
{
    /// <summary>Prefix for Telegram-originated task ids — used by the per-chat result router.</summary>
    public const string TaskIdPrefix = "task-tg-";

    /// <summary>Hard limit on a single Telegram <c>SendMessage</c> body. Chunking happens at this boundary.</summary>
    internal const int TelegramTextLimit = 4096;

    /// <inheritdoc/>
    public string Id => "telegram";

    private readonly WorkspaceDirectory _workspace;
    private readonly TelegramChannelOptions _options;
    private readonly ITelegramGateway _gateway;
    private readonly ILogger<TelegramChannel> _logger;
    private readonly OwnerActivity _ownerActivity;
    private readonly TaskArchive _archive;
    private readonly TelegramUpdateOffsetStore? _offsetStore;

    // Tracks result-file paths we've already dispatched so the FSW + rescan loop is idempotent.
    private readonly HashSet<string> _deliveredPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _deliveredGate = new();

    /// <summary>Production ctor — wires the SDK-backed gateway over the supplied bot token.</summary>
    /// <param name="workspace">Resolved workspace; task envelopes are written here and results polled from here.</param>
    /// <param name="options">Telegram-specific tunables — token, allow-lists, intervals.</param>
    /// <param name="logger">Optional logger.</param>
    public TelegramChannel(
        WorkspaceDirectory workspace,
        TelegramChannelOptions options,
        ILogger<TelegramChannel>? logger = null)
        : this(workspace, options, new TelegramBotGateway(new TelegramBotClient(options.BotToken)), logger)
    {
    }

    /// <summary>Test ctor — accepts a pre-built <see cref="ITelegramGateway"/> for offline drives.</summary>
    /// <param name="workspace">Resolved workspace.</param>
    /// <param name="options">Telegram-specific tunables. The token is unused when a custom gateway is injected.</param>
    /// <param name="gateway">Custom gateway implementation; typically a fake in tests.</param>
    /// <param name="logger">Optional logger.</param>
    internal TelegramChannel(
        WorkspaceDirectory workspace,
        TelegramChannelOptions options,
        ITelegramGateway gateway,
        ILogger<TelegramChannel>? logger = null)
    {
        _workspace = workspace;
        _options = options;
        _gateway = gateway;
        _logger = logger ?? NullLogger<TelegramChannel>.Instance;
        _ownerActivity = new OwnerActivity(workspace);
        _archive = new TaskArchive(workspace);
        _offsetStore = string.IsNullOrEmpty(options.PersistenceFile)
            ? null
            : new TelegramUpdateOffsetStore(workspace, options.PersistenceFile);
    }

    /// <inheritdoc/>
    public async Task RunAsync(CancellationToken ct)
    {
        // Outbound pump runs concurrently with inbound long-poll: results may arrive for tasks
        // produced by other channels in this same workspace, or for proactive results addressed
        // to telegram, so we must always be ready to dispatch. We schedule each loop on its own
        // task so neither side can hot-spin the other off the CPU even if its awaits complete
        // synchronously.
        var inbound = Task.Run(() => RunInboundLoopAsync(ct), CancellationToken.None);
        var outbound = Task.Run(() => RunOutboundLoopAsync(ct), CancellationToken.None);
        try
        {
            await Task.WhenAll(inbound, outbound).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
    }

    // -------- inbound: telegram → task file --------

    private async Task RunInboundLoopAsync(CancellationToken ct)
    {
        // Pick up where we left off: a restart must not replay any previously-delivered update.
        int? offset = _offsetStore?.Read() is { } persisted ? persisted + 1 : null;

        while (!ct.IsCancellationRequested)
        {
            IReadOnlyList<TelegramInboundUpdate> batch;
            try
            {
                batch = await _gateway.GetUpdatesAsync(offset, _options.LongPollTimeout, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Transient network errors are common against Telegram. Log + back off briefly.
                _logger.LogWarning(ex, "telegram: GetUpdates failed — backing off");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                continue;
            }

            foreach (var update in batch)
            {
                offset = update.UpdateId + 1;
                try
                {
                    await DispatchInboundAsync(update, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Per-update failure must not stop the loop — log and advance the offset.
                    _logger.LogWarning(ex, "telegram: failed to dispatch update {UpdateId}", update.UpdateId);
                }

                // Persist offset as we go so a crash mid-batch doesn't lose progress on the
                // already-handled prefix.
                if (offset is { } off)
                {
                    _offsetStore?.Write(off - 1);
                }
            }
        }
    }

    private async Task DispatchInboundAsync(TelegramInboundUpdate update, CancellationToken ct)
    {
        var tier = ClassifyTier(update.FromUserId);

        // Unverified callers never produce a task — write a polite inline decline and return.
        if (tier == AccessTier.Unverified)
        {
            await _gateway.SendTextAsync(update.ChatId, _options.UnverifiedDecline, update.MessageId, ct).ConfigureAwait(false);
            return;
        }

        var body = await ComposeTaskBodyAsync(update, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
        {
            // Nothing for the executor to act on (e.g. a sticker we don't translate). Skip silently.
            return;
        }

        var envelope = new TaskEnvelope
        {
            Id = NewTaskId(update.ChatId),
            Timestamp = update.SentAt,
            Body = body,
            Source = TaskSource.Telegram,
            ChannelId = update.ChatId.ToString(CultureInfo.InvariantCulture),
            UserId = (update.FromUserId ?? 0).ToString(CultureInfo.InvariantCulture),
            AccessTier = tier,
            Priority = TaskPriorities.DefaultFor(TaskSource.Telegram, tier),
            ReplyToMessageId = update.MessageId.ToString(CultureInfo.InvariantCulture),
        };

        _ = TaskFile.Write(_workspace.Tasks.FullName, envelope);

        if (tier == AccessTier.Owner)
        {
            // The proactive loop watches owner activity to avoid talking over a present human.
            _ownerActivity.Record("telegram", body);
        }
    }

    private AccessTier ClassifyTier(long? userId)
    {
        if (userId is null)
        {
            return AccessTier.Unverified;
        }
        if (_options.OwnerUserId is { } owner && userId == owner)
        {
            return AccessTier.Owner;
        }
        if (_options.VerifiedUserIds.Contains(userId.Value))
        {
            return AccessTier.Verified;
        }
        if (_options.TeamUserIds.Contains(userId.Value))
        {
            return AccessTier.Team;
        }
        return AccessTier.Unverified;
    }

    private async Task<string> ComposeTaskBodyAsync(TelegramInboundUpdate update, CancellationToken ct)
    {
        // Body composition: any downloaded attachments become [file: <path>] marker lines at
        // the head, followed by the user's text (or empty). Mirrors the result-side convention
        // so the executor's prompt and the result pipeline share a single grammar.
        var markers = new List<string>();

        if (update.PhotoFileId is { Length: > 0 } photoId)
        {
            var path = await DownloadToDataDirAsync(update.ChatId, photoId, ".jpg", ct).ConfigureAwait(false);
            if (path is not null) { markers.Add($"[file: {path}]"); }
        }
        if (update.VoiceFileId is { Length: > 0 } voiceId)
        {
            var path = await DownloadToDataDirAsync(update.ChatId, voiceId, ".ogg", ct).ConfigureAwait(false);
            if (path is not null) { markers.Add($"[file: {path}]"); }
        }
        if (update.DocumentFileId is { Length: > 0 } docId)
        {
            var ext = !string.IsNullOrEmpty(update.DocumentFileName)
                ? (Path.GetExtension(update.DocumentFileName) is { Length: > 0 } e ? e : ".bin")
                : ".bin";
            var path = await DownloadToDataDirAsync(update.ChatId, docId, ext, ct).ConfigureAwait(false);
            if (path is not null) { markers.Add($"[file: {path}]"); }
        }

        if (markers.Count == 0)
        {
            return update.Text;
        }
        // Markers above, body underneath — preserves the parser semantics described in
        // ResultMarkers (consumers strip the leading markers and treat the rest as text).
        return string.Join('\n', markers) + (string.IsNullOrEmpty(update.Text) ? string.Empty : "\n" + update.Text);
    }

    private async Task<string?> DownloadToDataDirAsync(long chatId, string fileId, string extension, CancellationToken ct)
    {
        var safeExt = extension.StartsWith('.') ? extension : "." + extension;
        var dir = Path.Combine(_workspace.Data.FullName, "telegram", chatId.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileId + safeExt);
        try
        {
            await _gateway.DownloadFileAsync(fileId, path, ct).ConfigureAwait(false);
            return path;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            _logger.LogWarning(ex, "telegram: failed to download file {FileId} for chat {ChatId}", fileId, chatId);
            return null;
        }
    }

    private static string NewTaskId(long chatId)
    {
        var ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return $"{TaskIdPrefix}{chatId.ToString(CultureInfo.InvariantCulture)}-{ms}";
    }

    // -------- outbound: results/ → telegram --------

    private async Task RunOutboundLoopAsync(CancellationToken ct)
    {
        var resultsDir = _workspace.Results.FullName;
        Directory.CreateDirectory(resultsDir);

        // Arm the watcher BEFORE the initial scan so files written between the two are caught.
        using var signal = new SemaphoreSlim(0, 1);
        using var fsw = new FileSystemWatcher(resultsDir)
        {
            Filter = TaskIdPrefix + "*.txt",
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
        };
        void Pulse(object _, FileSystemEventArgs __)
        {
            // SemaphoreSlim throws if already at max — swallow the race; one wake-up is enough.
            try { signal.Release(); }
            catch (SemaphoreFullException) { /* already pulsed */ }
        }
        fsw.Created += Pulse;
        fsw.Changed += Pulse;
        fsw.Renamed += (s, e) => Pulse(s, e);

        // Initial drain: a result might already be sitting in the directory when we start.
        await ScanAndDeliverAsync(ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            var pollTask = Task.Delay(_options.ResultPollInterval, ct);
            var signalTask = signal.WaitAsync(ct);
            var winner = await Task.WhenAny(pollTask, signalTask).ConfigureAwait(false);
            try { await winner.ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            await ScanAndDeliverAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task ScanAndDeliverAsync(CancellationToken ct)
    {
        var resultsDir = _workspace.Results;
        if (!resultsDir.Exists)
        {
            return;
        }

        var candidates = resultsDir
            .EnumerateFiles(TaskIdPrefix + "*.txt", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f.LastWriteTimeUtc)
            .ToList();

        foreach (var f in candidates)
        {
            if (ct.IsCancellationRequested) { return; }

            lock (_deliveredGate)
            {
                if (_deliveredPaths.Contains(f.FullName))
                {
                    continue;
                }
            }

            try
            {
                await DeliverOneAsync(f, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "telegram: failed to deliver result {Path}", f.FullName);
            }

            lock (_deliveredGate)
            {
                _deliveredPaths.Add(f.FullName);
            }
        }
    }

    private async Task DeliverOneAsync(FileInfo file, CancellationToken ct)
    {
        if (!TryParseChatIdFromTaskFileName(file.Name, out var taskId, out var chatId))
        {
            // Filenames matching task-tg-*.txt but without a parseable chat id are skipped —
            // safer than guessing the wrong recipient.
            _logger.LogDebug("telegram: skipping unroutable result {Name}", file.Name);
            return;
        }

        // Wait briefly so a partially-flushed writer can finish, then read with shared access.
        if (_options.FileWriteDebounce > TimeSpan.Zero)
        {
            try { await Task.Delay(_options.FileWriteDebounce, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
        }

        string raw;
        try
        {
            raw = await ReadWithRetryAsync(file.FullName, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            // Race with an external archiver — nothing to deliver, mark as handled.
            return;
        }

        var parsed = ResultBody.Parse(raw);

        if (parsed.ShouldSkipDelivery)
        {
            // Markers tell us not to send anything. Still archive so we don't re-process.
            _archive.Archive(taskId, alsoResult: true);
            return;
        }

        foreach (var attachment in parsed.Attachments)
        {
            if (!File.Exists(attachment))
            {
                _logger.LogWarning("telegram: result references missing attachment {Path}", attachment);
                continue;
            }
            await _gateway.SendDocumentAsync(chatId, attachment, caption: null, ct).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(parsed.Text))
        {
            foreach (var chunk in ChunkText(parsed.Text, TelegramTextLimit))
            {
                await _gateway.SendTextAsync(chatId, chunk, replyToMessageId: null, ct).ConfigureAwait(false);
            }
        }

        // Archive both the task and the result; bridge-contract.md mandates month-partitioned
        // storage for completed work.
        _archive.Archive(taskId, alsoResult: true);
    }

    /// <summary>
    /// Parse a result-file name of the form <c>task-tg-&lt;chatId&gt;-&lt;ms&gt;.txt</c> and recover
    /// the task id and chat id. Defensive against unexpected shapes — returns <see langword="false"/>
    /// rather than throwing.
    /// </summary>
    internal static bool TryParseChatIdFromTaskFileName(string fileName, out string taskId, out long chatId)
    {
        taskId = string.Empty;
        chatId = 0;
        var name = fileName;
        if (name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }
        if (!name.StartsWith(TaskIdPrefix, StringComparison.Ordinal))
        {
            return false;
        }
        var rest = name[TaskIdPrefix.Length..];
        // Expect <chatId>-<ms>. The chat id may be negative (groups), so include leading '-'.
        var lastDash = rest.LastIndexOf('-');
        if (lastDash <= 0)
        {
            return false;
        }
        var chatStr = rest[..lastDash];
        if (!long.TryParse(chatStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out chatId))
        {
            return false;
        }
        taskId = name;
        return true;
    }

    /// <summary>Split <paramref name="text"/> into chunks no larger than <paramref name="limit"/> chars.</summary>
    /// <remarks>
    /// Prefers newline boundaries when one exists in the tail half of the window so we don't
    /// truncate mid-paragraph; surrogate-pair aware so we never split a code point.
    /// </remarks>
    internal static IEnumerable<string> ChunkText(string text, int limit)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }
        if (text.Length <= limit)
        {
            yield return text;
            yield break;
        }

        var pos = 0;
        while (pos < text.Length)
        {
            var remaining = text.Length - pos;
            if (remaining <= limit)
            {
                yield return text[pos..];
                yield break;
            }

            var end = pos + limit;

            // Don't split inside a surrogate pair: if `end-1` is a high surrogate, step back one.
            if (char.IsHighSurrogate(text[end - 1]))
            {
                end--;
            }

            // Prefer the last newline in the second half of the window — keeps prose tidy.
            var lookbackStart = pos + (limit / 2);
            var newline = text.LastIndexOf('\n', end - 1, end - lookbackStart);
            if (newline > pos)
            {
                end = newline + 1;
            }

            yield return text[pos..end];
            pos = end;
        }
    }

    private static async Task<string> ReadWithRetryAsync(string path, CancellationToken ct)
    {
        const int maxAttempts = 10;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            }
            catch (IOException) when (attempt < maxAttempts - 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), ct).ConfigureAwait(false);
            }
        }
        return await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
    }
}
