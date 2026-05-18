using System.Globalization;
using Sutando.Bridge;
using Sutando.Channels.Telegram;
using Sutando.Workspace;

namespace Sutando.Tests.Channels;

/// <summary>
/// Offline tests for <see cref="TelegramChannel"/>. Drives the channel through a hand-written
/// <see cref="ITelegramGateway"/> fake so no Telegram credentials or network access are required.
/// Each test points the workspace at a temp directory and exercises one slice of the channel.
/// </summary>
public sealed class TelegramChannelTests : IDisposable
{
    private const long OwnerId = 11111;
    private const long VerifiedId = 22222;
    private const long TeamId = 33333;
    private const long StrangerId = 99999;

    private readonly string _tempRoot;
    private readonly string? _previousEnv;
    private readonly WorkspaceDirectory _workspace;

    public TelegramChannelTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "sutando-tg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _previousEnv = Environment.GetEnvironmentVariable(WorkspaceDirectory.EnvVar);
        Environment.SetEnvironmentVariable(WorkspaceDirectory.EnvVar, _tempRoot);
        _workspace = WorkspaceDirectory.Resolve();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(WorkspaceDirectory.EnvVar, _previousEnv);
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    // ---- inbound dispatch ----

    [Fact]
    public async Task OwnerMessage_WritesTaskWithOwnerTier_AndRecordsOwnerActivity()
    {
        var gateway = new FakeGateway();
        gateway.Enqueue(MakeUpdate(updateId: 1, chatId: 100, fromUserId: OwnerId, text: "hello agent"));

        await RunChannelUntilQuiescentAsync(gateway, OwnerOptions());

        var task = ReadOnlyTaskInWorkspace();
        Assert.Equal(TaskSource.Telegram, task.Source);
        Assert.Equal(AccessTier.Owner, task.AccessTier);
        Assert.Equal("100", task.ChannelId);
        Assert.Equal(OwnerId.ToString(CultureInfo.InvariantCulture), task.UserId);
        Assert.Equal("hello agent", task.Body);
        Assert.StartsWith("task-tg-100-", task.Id, StringComparison.Ordinal);
        Assert.Equal(TaskPriority.Normal, task.Priority);
        Assert.Equal("1", task.ReplyToMessageId);

        // Owner activity was recorded so the proactive loop knows the human is at the keyboard.
        var activity = new OwnerActivity(_workspace).Read();
        Assert.NotNull(activity);
        Assert.Equal("telegram", activity!.Channel);
        Assert.Contains("hello agent", activity.Summary, StringComparison.Ordinal);

        // No decline ever sent for the owner.
        Assert.Empty(gateway.SentTexts);
    }

    [Fact]
    public async Task VerifiedMessage_WritesTaskWithVerifiedTier_AndNoOwnerActivity()
    {
        var gateway = new FakeGateway();
        gateway.Enqueue(MakeUpdate(updateId: 2, chatId: 200, fromUserId: VerifiedId, text: "ping"));

        await RunChannelUntilQuiescentAsync(gateway, OwnerOptions());

        var task = ReadOnlyTaskInWorkspace();
        Assert.Equal(AccessTier.Verified, task.AccessTier);
        Assert.Equal("ping", task.Body);
        Assert.Null(new OwnerActivity(_workspace).Read());
    }

    [Fact]
    public async Task TeamMessage_WritesTaskWithTeamTier_AndLowPriority()
    {
        var gateway = new FakeGateway();
        gateway.Enqueue(MakeUpdate(updateId: 3, chatId: 300, fromUserId: TeamId, text: "from team"));

        await RunChannelUntilQuiescentAsync(gateway, OwnerOptions());

        var task = ReadOnlyTaskInWorkspace();
        Assert.Equal(AccessTier.Team, task.AccessTier);
        // Default priority for non-owner/verified is low — per bridge-contract.md.
        Assert.Equal(TaskPriority.Low, task.Priority);
    }

    [Fact]
    public async Task UnverifiedMessage_WritesNoTask_SendsPoliteDecline()
    {
        var gateway = new FakeGateway();
        gateway.Enqueue(MakeUpdate(updateId: 4, chatId: 400, fromUserId: StrangerId, text: "let me in"));

        await RunChannelUntilQuiescentAsync(gateway, OwnerOptions());

        Assert.Empty(_workspace.Tasks.EnumerateFiles("task-*.txt", SearchOption.TopDirectoryOnly));
        var sent = Assert.Single(gateway.SentTexts);
        Assert.Equal(400, sent.ChatId);
        Assert.Equal(TelegramChannelOptions.DefaultUnverifiedDecline, sent.Text);
        Assert.Equal(4, sent.ReplyToMessageId);
    }

    [Fact]
    public async Task PhotoMessage_DownloadsLargestVariant_PrependsFileMarker()
    {
        var gateway = new FakeGateway();
        gateway.Enqueue(MakeUpdate(updateId: 5, chatId: 500, fromUserId: OwnerId, text: "look at this", photoFileId: "photo-abc"));

        await RunChannelUntilQuiescentAsync(gateway, OwnerOptions());

        var task = ReadOnlyTaskInWorkspace();
        Assert.Contains("[file:", task.Body, StringComparison.Ordinal);
        Assert.Contains("look at this", task.Body, StringComparison.Ordinal);

        // The downloaded payload exists in the per-chat data dir.
        var chatDir = Path.Combine(_workspace.Data.FullName, "telegram", "500");
        var downloaded = Directory.EnumerateFiles(chatDir, "photo-abc*").ToList();
        Assert.Single(downloaded);
        Assert.Single(gateway.Downloads);
    }

    [Fact]
    public async Task PersistedOffset_IsSkipped_OnSecondRun()
    {
        // First run: process update 10.
        var gateway1 = new FakeGateway();
        gateway1.Enqueue(MakeUpdate(updateId: 10, chatId: 100, fromUserId: OwnerId, text: "first"));
        await RunChannelUntilQuiescentAsync(gateway1, OwnerOptions());

        // Reset task dir so the second run's assertion is unambiguous.
        foreach (var f in _workspace.Tasks.EnumerateFiles("task-*.txt"))
        {
            f.Delete();
        }

        // Second run: same id should NOT reappear in the queue (offset persisted), only id=11.
        var gateway2 = new FakeGateway();
        gateway2.Enqueue(MakeUpdate(updateId: 11, chatId: 100, fromUserId: OwnerId, text: "second"));
        await RunChannelUntilQuiescentAsync(gateway2, OwnerOptions());

        // The fake gateway records the offset it was asked for; the second run must ask for >= 11.
        Assert.NotEmpty(gateway2.GetUpdatesOffsets);
        Assert.Equal(11, gateway2.GetUpdatesOffsets[0]);

        var task = ReadOnlyTaskInWorkspace();
        Assert.Equal("second", task.Body);
    }

    // ---- outbound routing ----

    [Fact]
    public async Task ResultForTask_IsDeliveredToCorrectChat_AndArchived()
    {
        var gateway = new FakeGateway();
        var taskId = "task-tg-555-1234567890";
        new ResultFile(_workspace.Results).Write(taskId, "here is your answer");

        await RunChannelUntilQuiescentAsync(gateway, OwnerOptions());

        var sent = Assert.Single(gateway.SentTexts);
        Assert.Equal(555, sent.ChatId);
        Assert.Equal("here is your answer", sent.Text);

        // The result file moved into archive/YYYY-MM/.
        Assert.False(File.Exists(Path.Combine(_workspace.Results.FullName, taskId + ".txt")));
        var archived = _workspace.Results
            .EnumerateFiles(taskId + ".txt", SearchOption.AllDirectories)
            .ToList();
        Assert.Single(archived);
    }

    [Fact]
    public async Task ResultWithRepliedMarker_IsArchivedWithoutDelivery()
    {
        var gateway = new FakeGateway();
        var taskId = "task-tg-555-1";
        new ResultFile(_workspace.Results).WriteWithMarkers(taskId, "should not be sent", alreadyReplied: true);

        await RunChannelUntilQuiescentAsync(gateway, OwnerOptions());

        Assert.Empty(gateway.SentTexts);
        Assert.False(File.Exists(Path.Combine(_workspace.Results.FullName, taskId + ".txt")));
    }

    [Fact]
    public async Task ResultWithNoSendMarker_IsArchivedWithoutDelivery()
    {
        var gateway = new FakeGateway();
        var taskId = "task-tg-555-2";
        new ResultFile(_workspace.Results).WriteWithMarkers(taskId, "ignore me", noSend: true);

        await RunChannelUntilQuiescentAsync(gateway, OwnerOptions());

        Assert.Empty(gateway.SentTexts);
        Assert.False(File.Exists(Path.Combine(_workspace.Results.FullName, taskId + ".txt")));
    }

    [Fact]
    public async Task ResultWithDedupedMarker_IsArchivedWithoutDelivery()
    {
        var gateway = new FakeGateway();
        var taskId = "task-tg-555-3";
        new ResultFile(_workspace.Results).WriteWithMarkers(taskId, "dup", dedupedTo: "task-tg-555-1");

        await RunChannelUntilQuiescentAsync(gateway, OwnerOptions());

        Assert.Empty(gateway.SentTexts);
        Assert.False(File.Exists(Path.Combine(_workspace.Results.FullName, taskId + ".txt")));
    }

    [Fact]
    public async Task ResultWithFileMarker_UploadsAsDocument()
    {
        var gateway = new FakeGateway();
        var taskId = "task-tg-555-4";

        var attachmentPath = Path.Combine(_workspace.Data.FullName, "out.txt");
        Directory.CreateDirectory(_workspace.Data.FullName);
        await File.WriteAllTextAsync(attachmentPath, "payload");

        new ResultFile(_workspace.Results).WriteWithMarkers(taskId, "see attached", attachments: [attachmentPath]);

        await RunChannelUntilQuiescentAsync(gateway, OwnerOptions());

        var doc = Assert.Single(gateway.SentDocuments);
        Assert.Equal(555, doc.ChatId);
        Assert.Equal(attachmentPath, doc.Path);
        var text = Assert.Single(gateway.SentTexts);
        Assert.Equal("see attached", text.Text);
    }

    [Fact]
    public async Task LongResult_IsChunkedAtFourThousandCharacters()
    {
        var gateway = new FakeGateway();
        var taskId = "task-tg-555-5";
        // 5000 chars guarantees a split.
        var body = new string('x', 5000);
        new ResultFile(_workspace.Results).Write(taskId, body);

        await RunChannelUntilQuiescentAsync(gateway, OwnerOptions());

        Assert.Equal(2, gateway.SentTexts.Count);
        Assert.All(gateway.SentTexts, s => Assert.True(s.Text.Length <= 4096));
        Assert.Equal(5000, gateway.SentTexts.Sum(s => s.Text.Length));
    }

    [Fact]
    public async Task ResultWithUnparseableFileName_IsIgnoredSilently()
    {
        var gateway = new FakeGateway();
        // No trailing -<ms>, so no chat id can be extracted.
        var weird = Path.Combine(_workspace.Results.FullName, "task-tg-notanumber.txt");
        await File.WriteAllTextAsync(weird, "should not be delivered");

        await RunChannelUntilQuiescentAsync(gateway, OwnerOptions());

        Assert.Empty(gateway.SentTexts);
        // We didn't archive it (no parseable id) — the file is still where we wrote it.
        Assert.True(File.Exists(weird));
    }

    [Fact]
    public void TryParseChatIdFromTaskFileName_ParsesPositiveAndNegativeChatIds()
    {
        Assert.True(TelegramChannel.TryParseChatIdFromTaskFileName(
            "task-tg-12345-1700000000000.txt", out var id, out var chat));
        Assert.Equal("task-tg-12345-1700000000000", id);
        Assert.Equal(12345, chat);

        Assert.True(TelegramChannel.TryParseChatIdFromTaskFileName(
            "task-tg--100123-1700000000000.txt", out _, out chat));
        Assert.Equal(-100123, chat);

        Assert.False(TelegramChannel.TryParseChatIdFromTaskFileName("results.txt", out _, out _));
        Assert.False(TelegramChannel.TryParseChatIdFromTaskFileName("task-tg-bad.txt", out _, out _));
    }

    // ---- helpers ----

    /// <summary>
    /// Drive the channel until both the inbound queue is drained and the outbound results have
    /// been scanned. We cancel after a brief settle delay; the channel exits cleanly on cancel.
    /// </summary>
    private static async Task RunChannelUntilQuiescentAsync(FakeGateway gateway, TelegramChannelOptions options)
    {
        // Snug intervals so the test doesn't sit on wall-clock waits.
        var fastOptions = options with
        {
            ResultPollInterval = TimeSpan.FromMilliseconds(50),
            FileWriteDebounce = TimeSpan.FromMilliseconds(10),
            LongPollTimeout = TimeSpan.FromMilliseconds(50),
        };
        var ws = WorkspaceDirectory.Resolve();
        var channel = new TelegramChannel(ws, fastOptions, gateway);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        // Run until the gateway has been fully drained AND a couple of poll cycles have elapsed.
        var run = Task.Run(() => channel.RunAsync(cts.Token), cts.Token);

        // Wait for both sides to settle: gateway queue drained, results dir scanned.
        await gateway.QueueDrained.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        // Let the outbound loop see at least a few ticks after drain — first scan + at least
        // one poll iteration so files that lose the FSW race still get picked up.
        await Task.Delay(TimeSpan.FromMilliseconds(700)).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);
        try { await run.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected — TaskCanceledException is a subtype */ }
    }

    private TaskEnvelope ReadOnlyTaskInWorkspace()
    {
        var files = _workspace.Tasks.EnumerateFiles("task-*.txt", SearchOption.TopDirectoryOnly).ToList();
        var f = Assert.Single(files);
        return TaskFile.ParseFile(f.FullName);
    }

    private static TelegramChannelOptions OwnerOptions() => new()
    {
        BotToken = "test-token",
        OwnerUserId = OwnerId,
        VerifiedUserIds = [VerifiedId],
        TeamUserIds = [TeamId],
    };

    private static TelegramInboundUpdate MakeUpdate(
        int updateId,
        long chatId,
        long fromUserId,
        string text,
        string? photoFileId = null)
        => new()
        {
            UpdateId = updateId,
            MessageId = updateId, // 1:1 for simplicity
            ChatId = chatId,
            FromUserId = fromUserId,
            FromDisplayName = "tester",
            SentAt = DateTimeOffset.UtcNow,
            Text = text,
            PhotoFileId = photoFileId,
        };

    /// <summary>
    /// In-memory gateway double. Delivers queued updates on the next <c>GetUpdates</c> call,
    /// then blocks subsequent calls; records all outbound texts / documents / downloads so
    /// the test can assert against them.
    /// </summary>
    private sealed class FakeGateway : ITelegramGateway
    {
        private readonly Queue<TelegramInboundUpdate> _pending = new();
        private readonly Lock _gate = new();

        public List<int?> GetUpdatesOffsets { get; } = new();
        public List<(long ChatId, string Text, int? ReplyToMessageId)> SentTexts { get; } = new();
        public List<(long ChatId, string Path, string? Caption)> SentDocuments { get; } = new();
        public List<(string FileId, string Path)> Downloads { get; } = new();
        public TaskCompletionSource QueueDrained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Enqueue(TelegramInboundUpdate u)
        {
            lock (_gate) { _pending.Enqueue(u); }
        }

        public async Task<IReadOnlyList<TelegramInboundUpdate>> GetUpdatesAsync(int? offset, TimeSpan timeout, CancellationToken ct)
        {
            lock (_gate) { GetUpdatesOffsets.Add(offset); }

            List<TelegramInboundUpdate> batch;
            lock (_gate)
            {
                batch = new List<TelegramInboundUpdate>(_pending);
                _pending.Clear();
            }

            // First call after a drain wakes the test; subsequent polls return empty until
            // the test cancels.
            if (batch.Count == 0)
            {
                _ = QueueDrained.TrySetResult();
                // Honour the long-poll timeout so the inbound loop yields to the scheduler
                // instead of hot-spinning when there's nothing to deliver. Real Telegram
                // edges block here too.
                var honoured = timeout > TimeSpan.Zero ? timeout : TimeSpan.FromMilliseconds(20);
                try { await Task.Delay(honoured, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* expected */ }
            }
            else
            {
                // Even with payloads in hand, yield once so concurrent loops make progress.
                await Task.Yield();
            }

            return batch;
        }

        public Task SendTextAsync(long chatId, string text, int? replyToMessageId, CancellationToken ct)
        {
            lock (_gate) { SentTexts.Add((chatId, text, replyToMessageId)); }
            return Task.CompletedTask;
        }

        public Task SendDocumentAsync(long chatId, string path, string? caption, CancellationToken ct)
        {
            lock (_gate) { SentDocuments.Add((chatId, path, caption)); }
            return Task.CompletedTask;
        }

        public Task DownloadFileAsync(string fileId, string destinationPath, CancellationToken ct)
        {
            // Materialise an empty file so the channel's downstream "exists" probes succeed.
            var dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllBytes(destinationPath, []);
            lock (_gate) { Downloads.Add((fileId, destinationPath)); }
            return Task.CompletedTask;
        }
    }
}
