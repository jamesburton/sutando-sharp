using Sutando.Bridge;
using Sutando.Channels.Cli;
using Sutando.Workspace;

namespace Sutando.Tests.Channels;

/// <summary>
/// Integration-style tests for <see cref="CliChatChannel"/>. Each test points the workspace
/// at a temp directory, fakes <c>Console.In</c> via <see cref="StringReader"/> and captures
/// <c>Console.Out</c> via <see cref="StringWriter"/>. A background "executor" task watches
/// the workspace tasks/ dir and writes a synthetic result so the channel can complete
/// without sleeping the wall-clock.
/// </summary>
public sealed class CliChatChannelTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string? _previousEnv;
    private readonly WorkspaceDirectory _workspace;

    public CliChatChannelTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "sutando-chat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _previousEnv = Environment.GetEnvironmentVariable(WorkspaceDirectory.EnvVar);
        Environment.SetEnvironmentVariable(WorkspaceDirectory.EnvVar, _tempRoot);
        _workspace = WorkspaceDirectory.Resolve();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(WorkspaceDirectory.EnvVar, _previousEnv);
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    [Fact]
    public async Task ExitCommand_TerminatesLoopCleanly()
    {
        var input = new StringReader(":exit\n");
        var output = new StringWriter();
        var channel = new CliChatChannel(_workspace, TestOptions(), input, output);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await channel.RunAsync(cts.Token);

        var text = output.ToString();
        Assert.Contains("sutando chat", text, StringComparison.Ordinal);
        Assert.Contains("bye.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuitCommand_TerminatesLoopCleanly()
    {
        var input = new StringReader(":quit\n");
        var output = new StringWriter();
        var channel = new CliChatChannel(_workspace, TestOptions(), input, output);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await channel.RunAsync(cts.Token);

        Assert.Contains("bye.", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyLines_AreSkipped_ThenExit()
    {
        var input = new StringReader("\n   \n:exit\n");
        var output = new StringWriter();
        var channel = new CliChatChannel(_workspace, TestOptions(), input, output);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await channel.RunAsync(cts.Token);

        // No task envelopes should have been written.
        Assert.Empty(_workspace.Tasks.EnumerateFiles("task-*.txt", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task EofExits_NoTrailingNewline()
    {
        // No newline at end — EOF immediately on first ReadLine.
        var input = new StringReader(string.Empty);
        var output = new StringWriter();
        var channel = new CliChatChannel(_workspace, TestOptions(), input, output);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await channel.RunAsync(cts.Token);

        Assert.Contains("(eof)", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UserInput_WritesTaskEnvelope_AndPrintsExecutorResult()
    {
        // Spin up a tiny background "executor" that watches tasks/ and writes a result.
        var resultFile = new ResultFile(_workspace.Results);
        using var executorCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var executor = Task.Run(async () =>
        {
            await using var watcher = new TaskWatcher(_workspace, rescanInterval: TimeSpan.FromMilliseconds(100));
            watcher.Start();
            var env = await watcher.Reader.ReadAsync(executorCts.Token);
            resultFile.Write(env.Id, "hello back, owner.");
            return env;
        }, executorCts.Token);

        var input = new StringReader("hello agent\n:exit\n");
        var output = new StringWriter();
        var channel = new CliChatChannel(_workspace, TestOptions(), input, output);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await channel.RunAsync(cts.Token);
        var picked = await executor;

        var text = output.ToString();
        Assert.Contains("(submitted: ", text, StringComparison.Ordinal);
        Assert.Contains("hello back, owner.", text, StringComparison.Ordinal);

        // The envelope the executor picked up matches our chat-channel shape.
        Assert.Equal(TaskSource.Chat, picked.Source);
        Assert.Equal("local-chat", picked.ChannelId);
        Assert.Equal(AccessTier.Owner, picked.AccessTier);
        Assert.Equal("hello agent", picked.Body);
    }

    [Fact]
    public async Task UserInput_RecordsOwnerActivity()
    {
        var resultFile = new ResultFile(_workspace.Results);
        using var executorCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var executor = Task.Run(async () =>
        {
            await using var watcher = new TaskWatcher(_workspace, rescanInterval: TimeSpan.FromMilliseconds(100));
            watcher.Start();
            var env = await watcher.Reader.ReadAsync(executorCts.Token);
            resultFile.Write(env.Id, "ok");
        }, executorCts.Token);

        // No :exit — EOF after the one message so OwnerActivity isn't overwritten by an exit command.
        var input = new StringReader("first message\n");
        var output = new StringWriter();
        var channel = new CliChatChannel(_workspace, TestOptions(), input, output);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await channel.RunAsync(cts.Token);
        await executor;

        var activity = new OwnerActivity(_workspace).Read();
        Assert.NotNull(activity);
        Assert.Equal("chat", activity!.Channel);
        Assert.Contains("first message", activity.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResultWithRepliedMarker_SkipsBodyPrint()
    {
        var resultFile = new ResultFile(_workspace.Results);
        using var executorCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var executor = Task.Run(async () =>
        {
            await using var watcher = new TaskWatcher(_workspace, rescanInterval: TimeSpan.FromMilliseconds(100));
            watcher.Start();
            var env = await watcher.Reader.ReadAsync(executorCts.Token);
            resultFile.WriteWithMarkers(env.Id, "secret body that should never show", alreadyReplied: true);
        }, executorCts.Token);

        var input = new StringReader("anything\n:exit\n");
        var output = new StringWriter();
        var channel = new CliChatChannel(_workspace, TestOptions(), input, output);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await channel.RunAsync(cts.Token);
        await executor;

        var text = output.ToString();
        Assert.Contains("[REPLIED]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret body that should never show", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResultWithNoSendMarker_SkipsBodyPrint()
    {
        var resultFile = new ResultFile(_workspace.Results);
        using var executorCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var executor = Task.Run(async () =>
        {
            await using var watcher = new TaskWatcher(_workspace, rescanInterval: TimeSpan.FromMilliseconds(100));
            watcher.Start();
            var env = await watcher.Reader.ReadAsync(executorCts.Token);
            resultFile.WriteWithMarkers(env.Id, "do not deliver", noSend: true);
        }, executorCts.Token);

        var input = new StringReader("anything\n:exit\n");
        var output = new StringWriter();
        var channel = new CliChatChannel(_workspace, TestOptions(), input, output);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await channel.RunAsync(cts.Token);
        await executor;

        var text = output.ToString();
        Assert.Contains("[no-send]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("do not deliver", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResultWithFileAttachments_ListsAttachmentPaths()
    {
        var resultFile = new ResultFile(_workspace.Results);
        using var executorCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var executor = Task.Run(async () =>
        {
            await using var watcher = new TaskWatcher(_workspace, rescanInterval: TimeSpan.FromMilliseconds(100));
            watcher.Start();
            var env = await watcher.Reader.ReadAsync(executorCts.Token);
            resultFile.WriteWithMarkers(env.Id, "see attachments",
                attachments: ["/tmp/a.png", "/tmp/b.pdf"]);
        }, executorCts.Token);

        var input = new StringReader("show me attachments\n:exit\n");
        var output = new StringWriter();
        var channel = new CliChatChannel(_workspace, TestOptions(), input, output);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await channel.RunAsync(cts.Token);
        await executor;

        var text = output.ToString();
        Assert.Contains("/tmp/a.png", text, StringComparison.Ordinal);
        Assert.Contains("/tmp/b.pdf", text, StringComparison.Ordinal);
        Assert.Contains("see attachments", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusCommand_WhenAgentHasWritten_RendersStatus()
    {
        var cs = new CoreStatus(_workspace);
        cs.SignalRunning("summarising email");

        var input = new StringReader(":status\n:exit\n");
        var output = new StringWriter();
        var channel = new CliChatChannel(_workspace, TestOptions(), input, output);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await channel.RunAsync(cts.Token);

        var text = output.ToString();
        Assert.Contains("core-status: running", text, StringComparison.Ordinal);
        Assert.Contains("summarising email", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusCommand_WhenAgentIdle_PrintsNoSignal()
    {
        var input = new StringReader(":status\n:exit\n");
        var output = new StringWriter();
        var channel = new CliChatChannel(_workspace, TestOptions(), input, output);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await channel.RunAsync(cts.Token);

        Assert.Contains("no signal", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TasksCommand_ListsPendingTaskFiles()
    {
        // Pre-seed two pending tasks.
        var envA = NewEnvelope("task-chat-aaa");
        var envB = NewEnvelope("task-chat-bbb");
        TaskFile.Write(_workspace.Tasks.FullName, envA);
        TaskFile.Write(_workspace.Tasks.FullName, envB);

        var input = new StringReader(":tasks\n:exit\n");
        var output = new StringWriter();
        var channel = new CliChatChannel(_workspace, TestOptions(), input, output);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await channel.RunAsync(cts.Token);

        var text = output.ToString();
        Assert.Contains("task-chat-aaa.txt", text, StringComparison.Ordinal);
        Assert.Contains("task-chat-bbb.txt", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TasksCommand_NoPending_PrintsEmptyMessage()
    {
        var input = new StringReader(":tasks\n:exit\n");
        var output = new StringWriter();
        var channel = new CliChatChannel(_workspace, TestOptions(), input, output);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await channel.RunAsync(cts.Token);

        Assert.Contains("no pending tasks", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShortTimeout_ReportsTimeoutAndExits()
    {
        // No executor — the result will never arrive. Use a tiny timeout.
        var options = new CliChatChannelOptions
        {
            Version = "test",
            ResultTimeout = TimeSpan.FromMilliseconds(200),
            PollInterval = TimeSpan.FromMilliseconds(50),
            FileWriteDebounce = TimeSpan.FromMilliseconds(10),
        };
        var input = new StringReader("will never be answered\n:exit\n");
        var output = new StringWriter();
        var channel = new CliChatChannel(_workspace, options, input, output);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await channel.RunAsync(cts.Token);

        Assert.Contains("timeout", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static CliChatChannelOptions TestOptions() => new()
    {
        Version = "test",
        // Tight intervals so tests stay snappy without depending on wall-clock waits.
        PollInterval = TimeSpan.FromMilliseconds(50),
        FileWriteDebounce = TimeSpan.FromMilliseconds(10),
        ResultTimeout = TimeSpan.FromSeconds(10),
    };

    private static TaskEnvelope NewEnvelope(string id) => new()
    {
        Id = id,
        Timestamp = DateTimeOffset.UtcNow,
        Body = "seed",
        Source = TaskSource.Chat,
        ChannelId = "local-chat",
        UserId = "chat-local",
        AccessTier = AccessTier.Owner,
        Priority = TaskPriority.Normal,
    };
}
