using System.Globalization;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sutando.Bridge;
using Sutando.Workspace;

namespace Sutando.Channels.Discord;

/// <summary>
/// Discord adapter for Sutando. Listens for DMs and channel <c>@mention</c> messages, writes
/// task envelopes into <c>&lt;workspace&gt;/tasks/</c>, watches <c>&lt;workspace&gt;/results/</c>
/// for matching <c>task-dc-&lt;channelId&gt;-*.txt</c> files, parses the marker-prefixed body
/// via <see cref="ResultBody.Parse"/>, and delivers it back to the originating Discord channel.
/// </summary>
/// <remarks>
/// <para>
/// 3-tier access policy mirrors upstream's <c>discord-bridge.py</c>:
/// </para>
/// <list type="bullet">
///   <item><description>Sender id matches <see cref="DiscordChannelOptions.OwnerUserId"/> → <see cref="AccessTier.Owner"/> (full agent).</description></item>
///   <item><description>Member holds a role in <see cref="DiscordChannelOptions.TeamRoleIds"/> (channel context only) → <see cref="AccessTier.Team"/>.</description></item>
///   <item><description>Everyone else → <see cref="AccessTier.Other"/>.</description></item>
/// </list>
/// <para>
/// For non-Owner senders the channel injects the upstream in-band system-instructions block
/// (<c>===SUTANDO SYSTEM INSTRUCTIONS===</c>) into the task body so the executor routes the
/// work through a sandboxed <c>codex exec --sandbox read-only</c>. See <see cref="DiscordTaskBody"/>
/// for the exact format and the deviation note in <c>INTEGRATION-NOTES.md</c>.
/// </para>
/// </remarks>
public sealed class DiscordChannel : IChannel
{
    /// <inheritdoc/>
    public string Id => "discord";

    private readonly WorkspaceDirectory _workspace;
    private readonly DiscordChannelOptions _options;
    private readonly ILogger<DiscordChannel> _logger;
    private readonly OwnerActivity _ownerActivity;
    private readonly ResultFile _resultFile;
    private readonly TaskArchive _archive;

    private DiscordClient? _client;

    /// <param name="workspace">Resolved workspace.</param>
    /// <param name="options">Required configuration — bot token, owner/team allow-list, channel allow-list.</param>
    /// <param name="logger">Optional logger; defaults to <see cref="NullLogger{T}"/>.</param>
    public DiscordChannel(
        WorkspaceDirectory workspace,
        DiscordChannelOptions options,
        ILogger<DiscordChannel>? logger = null)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(_options.BotToken))
        {
            throw new ArgumentException("DiscordChannelOptions.BotToken is required.", nameof(options));
        }
        _logger = logger ?? NullLogger<DiscordChannel>.Instance;
        _ownerActivity = new OwnerActivity(workspace);
        _resultFile = new ResultFile(workspace.Results);
        _archive = new TaskArchive(workspace);
    }

    /// <inheritdoc/>
    public async Task RunAsync(CancellationToken ct)
    {
        // Build the gateway client. We need MessageContents (privileged) to read inbound text,
        // GuildMembers (privileged) to enumerate roles for tier resolution, GuildMessages to
        // receive channel @mentions, and DirectMessages to receive DMs.
        var configuration = new DiscordConfiguration
        {
            Token = _options.BotToken,
            TokenType = TokenType.Bot,
            Intents = DiscordIntents.Guilds
                | DiscordIntents.GuildMessages
                | DiscordIntents.GuildMembers
                | DiscordIntents.MessageContents
                | DiscordIntents.DirectMessages,
            AutoReconnect = true,
            MinimumLogLevel = LogLevel.Warning,
        };

        _client = new DiscordClient(configuration);
        _client.MessageCreated += OnMessageCreatedAsync;

        await _client.ConnectAsync().ConfigureAwait(false);
        _logger.LogInformation("Discord channel connected.");

        // Outbound: watch results/ for files matching task-dc-* and route to the right channel.
        var pollerTask = Task.Run(() => PollResultsAsync(ct), ct);

        try
        {
            // Block until the host signals shutdown.
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected — caller asked us to stop.
        }
        finally
        {
            try
            {
                await _client.DisconnectAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DiscordClient.DisconnectAsync threw during shutdown — swallowing.");
            }
            try
            {
                await pollerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* expected */ }
            _client.Dispose();
            _client = null;
        }
    }

    /// <summary>Gateway entrypoint — every DM and guild message reaches us here.</summary>
    private async Task OnMessageCreatedAsync(DiscordClient sender, MessageCreateEventArgs e)
    {
        try
        {
            await HandleInboundAsync(e).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Never let an exception escape the event handler — DSharpPlus logs it but a thrown
            // exception aborts the dispatch loop on some versions. Swallow + log.
            _logger.LogError(ex, "DiscordChannel.OnMessageCreatedAsync failed");
        }
    }

    private async Task HandleInboundAsync(MessageCreateEventArgs e)
    {
        var message = e.Message;
        if (message is null || e.Author is null || _client is null)
        {
            return;
        }

        // Ignore self + other bots.
        if (e.Author.IsBot || e.Author.Id == _client.CurrentUser.Id)
        {
            return;
        }

        var channel = message.Channel;
        if (channel is null)
        {
            return;
        }

        var isDm = channel.IsPrivate;

        // Channel allow-list (DMs are always allowed). When non-empty, drop messages from
        // channels that aren't on the list.
        if (!isDm && _options.AllowedChannelIds.Count > 0
            && !_options.AllowedChannelIds.Contains(channel.Id))
        {
            return;
        }

        // In a guild channel we only respond to @mentions of the bot — mirrors upstream so the
        // bot doesn't reply to every message in a channel it's a member of.
        if (!isDm && !MessageMentionsBot(message, _client.CurrentUser))
        {
            return;
        }

        // Resolve tier. For guild messages we need the member's role ids.
        IReadOnlyCollection<ulong> roleIds = [];
        if (!isDm && e.Guild is not null)
        {
            try
            {
                var member = await e.Guild.GetMemberAsync(e.Author.Id).ConfigureAwait(false);
                if (member?.Roles is not null)
                {
                    roleIds = member.Roles.Select(r => r.Id).ToArray();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "GetMemberAsync failed for {UserId} — defaulting to no roles", e.Author.Id);
            }
        }

        var tier = DiscordTierResolver.Resolve(e.Author.Id, roleIds, isDm, _options);

        // Strip our own @mention from the content so the body is clean for the executor.
        var text = StripBotMention(message.Content ?? string.Empty, _client.CurrentUser.Id);

        // Download attachments under <workspace>/data/discord/<channelId>/<attachmentId>
        // and accumulate [file:] markers for the executor.
        var (attachmentNote, attachmentPaths) = await DownloadAttachmentsAsync(message, channel.Id).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(text) && attachmentPaths.Count == 0)
        {
            // Nothing to do — empty mention with no payload.
            return;
        }

        // Build the user-task text in the upstream format, then compose the full body
        // (appending the tier-specific in-band system instructions for non-owner senders).
        var username = e.Author.Username ?? e.Author.Id.ToString(CultureInfo.InvariantCulture);
        var userText = DiscordTaskBody.BuildUserText(username, text, attachmentNote, replyContext: string.Empty);

        var unixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // Task id pattern: task-dc-<channelId>-<unix-ms>. The dc-<channelId> prefix lets the
        // result poller route the body back without consulting the task envelope.
        var taskId = string.Create(
            CultureInfo.InvariantCulture,
            $"task-dc-{channel.Id}-{unixMs}");

        var body = DiscordTaskBody.Compose(userText, tier, taskId, _workspace.Results.FullName);

        // Tack attachment markers onto the END of the body so the executor sees them after the
        // user text (mirrors upstream behaviour where attachment_note is concatenated to
        // user_task_text in the same field).
        if (attachmentPaths.Count > 0)
        {
            var sb = new System.Text.StringBuilder(body);
            foreach (var path in attachmentPaths)
            {
                sb.Append("\n[file: ").Append(path).Append(']');
            }
            body = sb.ToString();
        }

        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["message_id"] = message.Id.ToString(CultureInfo.InvariantCulture),
        };
        if (!isDm && e.Guild is not null)
        {
            meta["guild_id"] = e.Guild.Id.ToString(CultureInfo.InvariantCulture);
        }

        var envelope = new TaskEnvelope
        {
            Id = taskId,
            Timestamp = DateTimeOffset.UtcNow,
            Body = body,
            Source = TaskSource.Discord,
            ChannelId = channel.Id.ToString(CultureInfo.InvariantCulture),
            UserId = e.Author.Id.ToString(CultureInfo.InvariantCulture),
            AccessTier = tier,
            Priority = TaskPriorities.DefaultFor(TaskSource.Discord, tier),
            ReplyToMessageId = message.Id.ToString(CultureInfo.InvariantCulture),
            Meta = meta,
        };

        // Record owner activity FIRST — even if the task-write somehow fails, the proactive
        // loop should know the human just typed.
        if (tier == AccessTier.Owner)
        {
            _ownerActivity.Record("discord", text);
        }

        TaskFile.Write(_workspace.Tasks.FullName, envelope);
    }

    /// <summary>Download every attachment on the message and return joined upstream-style note + the local paths.</summary>
    private async Task<(string Note, IReadOnlyList<string> Paths)> DownloadAttachmentsAsync(DiscordMessage message, ulong channelId)
    {
        if (message.Attachments is null || message.Attachments.Count == 0)
        {
            return (string.Empty, []);
        }

        var dir = Path.Combine(_workspace.Data.FullName, "discord", channelId.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);

        var notes = new System.Text.StringBuilder();
        var paths = new List<string>(message.Attachments.Count);
        using var http = new HttpClient();

        foreach (var att in message.Attachments)
        {
            try
            {
                var fileName = att.FileName ?? att.Id.ToString(CultureInfo.InvariantCulture);
                // Disambiguate with the attachment id; multiple uploads can share a filename.
                var local = Path.Combine(dir, att.Id.ToString(CultureInfo.InvariantCulture) + "_" + fileName);
                using (var response = await http.GetAsync(att.Url).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    await using var src = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    await using var dst = File.Create(local);
                    await src.CopyToAsync(dst).ConfigureAwait(false);
                }
                paths.Add(local);
                notes.Append("\n[File attached: ").Append(local).Append(']');
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Discord attachment download failed for {Url}", att.Url);
            }
        }

        return (notes.ToString(), paths);
    }

    /// <summary>True when the message contains an @mention of <paramref name="self"/>.</summary>
    private static bool MessageMentionsBot(DiscordMessage message, DiscordUser self)
    {
        if (message.MentionedUsers is null)
        {
            return false;
        }
        foreach (var u in message.MentionedUsers)
        {
            if (u is not null && u.Id == self.Id)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Remove <c>&lt;@id&gt;</c> and <c>&lt;@!id&gt;</c> tokens for the bot itself.</summary>
    private static string StripBotMention(string content, ulong botId)
    {
        if (string.IsNullOrEmpty(content) || botId == 0)
        {
            return content;
        }
        var idStr = botId.ToString(CultureInfo.InvariantCulture);
        var stripped = content
            .Replace($"<@{idStr}>", string.Empty, StringComparison.Ordinal)
            .Replace($"<@!{idStr}>", string.Empty, StringComparison.Ordinal);
        return stripped.Trim();
    }

    /// <summary>Outbound poller — watches <c>results/</c> for matching files and delivers them.</summary>
    private async Task PollResultsAsync(CancellationToken ct)
    {
        using var fsw = new FileSystemWatcher(_workspace.Results.FullName)
        {
            Filter = "task-dc-*.txt",
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
        };

        using var signal = new SemaphoreSlim(0, 1);
        void Pulse(object _, FileSystemEventArgs __)
        {
            try { signal.Release(); }
            catch (SemaphoreFullException) { /* already pulsed */ }
        }
        fsw.Created += Pulse;
        fsw.Changed += Pulse;
        fsw.Renamed += (s, e) => Pulse(s, e);

        // Initial sweep — pick up anything that landed before we armed the watcher.
        await DrainResultsAsync(ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            var delay = Task.Delay(_options.ResultPollInterval, ct);
            var signalled = signal.WaitAsync(ct);
            try
            {
                await Task.WhenAny(delay, signalled).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (ct.IsCancellationRequested)
            {
                return;
            }

            await DrainResultsAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Enumerate matching result files and deliver each one in turn.</summary>
    private async Task DrainResultsAsync(CancellationToken ct)
    {
        if (!_workspace.Results.Exists)
        {
            return;
        }

        IEnumerable<FileInfo> files;
        try
        {
            files = _workspace.Results.EnumerateFiles("task-dc-*.txt", SearchOption.TopDirectoryOnly);
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }

        foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc).ToList())
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }
            try
            {
                await DeliverResultAsync(file, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DiscordChannel: failed to deliver {File}", file.FullName);
            }
        }
    }

    /// <summary>Parse one result file, ship the body to Discord, archive on success.</summary>
    private async Task DeliverResultAsync(FileInfo file, CancellationToken ct)
    {
        var taskId = Path.GetFileNameWithoutExtension(file.Name);
        var channelId = ExtractChannelIdFromTaskId(taskId);
        if (channelId is null)
        {
            _logger.LogDebug("DiscordChannel: cannot extract channel-id from {Id} — leaving for another consumer", taskId);
            return;
        }

        string raw;
        try
        {
            raw = await File.ReadAllTextAsync(file.FullName, ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "DiscordChannel: result file {File} not yet readable", file.FullName);
            return;
        }

        var parsed = ResultBody.Parse(raw);

        if (parsed.ShouldSkipDelivery)
        {
            _logger.LogInformation("DiscordChannel: skipping delivery for {TaskId} (marker says skip)", taskId);
            _archive.Archive(taskId);
            return;
        }

        if (_client is null)
        {
            return;
        }

        // Note: `DiscordChannel` is both this class's name AND a DSharpPlus type. We always
        // fully-qualify when we mean the DSharpPlus entity below.
        DSharpPlus.Entities.DiscordChannel? channel;
        try
        {
            channel = await _client.GetChannelAsync(channelId.Value).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DiscordChannel: GetChannelAsync({ChannelId}) failed", channelId.Value);
            return;
        }

        if (channel is null)
        {
            _logger.LogWarning("DiscordChannel: channel {ChannelId} not found — dropping {TaskId}", channelId.Value, taskId);
            _archive.Archive(taskId);
            return;
        }

        var text = parsed.Text;
        var attachments = parsed.Attachments;

        // Guard: Discord rejects messages whose content is empty AND have no attachments
        // (400 Bad Request). If the parser strips everything down to nothing useful, treat
        // it as a no-op delivery and archive without sending.
        if (string.IsNullOrEmpty(text) && attachments.Count == 0)
        {
            _logger.LogInformation("DiscordChannel: result {TaskId} has empty body and no attachments — archiving without send", taskId);
            _archive.Archive(taskId);
            return;
        }

        // Send text in chunks (Discord's hard limit is 2000 chars per message). Attachments
        // ride on the first chunk so the user sees them next to the body. An attachments-only
        // result is allowed (empty content + one or more files).
        var chunks = ChunkAtDiscordLimit(text).ToList();
        if (chunks.Count == 0)
        {
            chunks.Add(string.Empty);
        }

        for (var i = 0; i < chunks.Count; i++)
        {
            var builder = new DiscordMessageBuilder().WithContent(chunks[i]);

            if (i == 0)
            {
                foreach (var path in attachments)
                {
                    if (!File.Exists(path))
                    {
                        _logger.LogWarning("DiscordChannel: attachment {Path} missing — skipping", path);
                        continue;
                    }
                    try
                    {
                        // The stream must outlive the SendMessageAsync call — DSharpPlus reads it
                        // synchronously inside the multipart-form serialiser. We open with the
                        // builder's ownership semantics: AddFile reads the stream during send,
                        // and the builder disposes it for us in 4.x.
                        var fs = File.OpenRead(path);
                        builder.AddFile(Path.GetFileName(path), fs);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "DiscordChannel: failed to open attachment {Path}", path);
                    }
                }
            }

            try
            {
                await channel.SendMessageAsync(builder).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DiscordChannel: SendMessageAsync failed for {TaskId} chunk {Index}", taskId, i);
                return;
            }
        }

        _archive.Archive(taskId);
    }

    /// <summary>Parse the embedded channel id from <c>task-dc-&lt;channelId&gt;-&lt;unix-ms&gt;</c>.</summary>
    internal static ulong? ExtractChannelIdFromTaskId(string taskId)
    {
        if (string.IsNullOrEmpty(taskId))
        {
            return null;
        }
        // Expected: task-dc-<channelId>-<unix-ms>. Split on '-' and pick segment [2].
        var parts = taskId.Split('-');
        if (parts.Length < 4 || !parts[0].Equals("task", StringComparison.Ordinal) || !parts[1].Equals("dc", StringComparison.Ordinal))
        {
            return null;
        }
        if (ulong.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            return id;
        }
        return null;
    }

    /// <summary>
    /// Split <paramref name="text"/> into <c>≤ 2000-char</c> chunks for Discord's per-message
    /// content limit, breaking on newline boundaries where possible.
    /// </summary>
    internal static IEnumerable<string> ChunkAtDiscordLimit(string text)
    {
        const int Limit = 2000;
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }
        var remaining = text;
        while (remaining.Length > Limit)
        {
            // Break on the last newline within the limit, fall back to a hard split if there
            // isn't one. The minus-one keeps the newline at the end of the chunk we emit.
            var split = remaining.LastIndexOf('\n', Limit - 1, Limit);
            if (split <= 0)
            {
                split = Limit;
            }
            yield return remaining[..split];
            remaining = remaining[split..].TrimStart('\n');
        }
        if (remaining.Length > 0)
        {
            yield return remaining;
        }
    }
}
