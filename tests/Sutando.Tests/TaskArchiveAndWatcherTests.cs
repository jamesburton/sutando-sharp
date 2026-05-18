using System.Threading.Channels;
using Sutando.Bridge;
using Sutando.Workspace;

namespace Sutando.Tests;

public sealed class TaskArchiveAndWatcherTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string? _previousEnv;
    private readonly WorkspaceDirectory _workspace;

    public TaskArchiveAndWatcherTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "sutando-arch-" + Guid.NewGuid().ToString("N"));
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
    public void Archive_MovesTaskAndResult()
    {
        var envelope = NewEnvelope("task-archive-1");
        TaskFile.Write(_workspace.Tasks.FullName, envelope);
        new ResultFile(_workspace.Results).Write(envelope.Id, "done");

        var archive = new TaskArchive(_workspace);
        var moved = archive.Archive(envelope.Id);

        Assert.Equal(2, moved);
        Assert.False(File.Exists(Path.Combine(_workspace.Tasks.FullName, envelope.Id + ".txt")));
        Assert.False(File.Exists(Path.Combine(_workspace.Results.FullName, envelope.Id + ".txt")));
        Assert.NotNull(archive.FindArchivedTask(envelope.Id));
    }

    [Fact]
    public void Archive_MissingTask_ReturnsZero()
    {
        var archive = new TaskArchive(_workspace);
        var moved = archive.Archive("task-missing");
        Assert.Equal(0, moved);
    }

    [Fact]
    public async Task Watcher_EmitsExistingFilesOnStart()
    {
        var existing = NewEnvelope("task-watch-existing");
        TaskFile.Write(_workspace.Tasks.FullName, existing);

        await using var watcher = new TaskWatcher(_workspace);
        watcher.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var read = await watcher.Reader.ReadAsync(cts.Token);
        Assert.Equal(existing.Id, read.Id);
    }

    [Fact]
    public async Task Watcher_EmitsNewlyCreatedFile()
    {
        await using var watcher = new TaskWatcher(_workspace);
        watcher.Start();

        var created = NewEnvelope("task-watch-new");
        TaskFile.Write(_workspace.Tasks.FullName, created);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var read = await watcher.Reader.ReadAsync(cts.Token);
        Assert.Equal(created.Id, read.Id);
    }

    [Fact]
    public async Task Watcher_DoesNotEmitDuplicates()
    {
        var existing = NewEnvelope("task-watch-dup");
        TaskFile.Write(_workspace.Tasks.FullName, existing);

        await using var watcher = new TaskWatcher(_workspace, rescanInterval: TimeSpan.FromMilliseconds(100));
        watcher.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var first = await watcher.Reader.ReadAsync(cts.Token);
        Assert.Equal(existing.Id, first.Id);

        // Give the rescan loop several ticks to potentially re-emit the same file.
        await Task.Delay(500, cts.Token);

        // We expect no further reads — try with a tight timeout that should fail.
        using var quickTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await watcher.Reader.ReadAsync(quickTimeout.Token));
    }

    private static TaskEnvelope NewEnvelope(string id) => new()
    {
        Id = id,
        Timestamp = DateTimeOffset.UtcNow,
        Body = "hello",
        Source = TaskSource.Chat,
        ChannelId = "local-chat",
        UserId = "chat-local",
        AccessTier = AccessTier.Owner,
        Priority = TaskPriority.Normal,
    };
}
