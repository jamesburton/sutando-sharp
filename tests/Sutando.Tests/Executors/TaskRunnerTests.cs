using System.Diagnostics;
using Sutando.Bridge;
using Sutando.Core;
using Sutando.Skills;
using Sutando.Skills.Builtin;
using Sutando.Workspace;

namespace Sutando.Tests.Executors;

public sealed class TaskRunnerTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string? _previousEnv;
    private readonly WorkspaceDirectory _workspace;

    public TaskRunnerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "sutando-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _previousEnv = Environment.GetEnvironmentVariable(WorkspaceDirectory.EnvVar);
        Environment.SetEnvironmentVariable(WorkspaceDirectory.EnvVar, _tempRoot);
        _workspace = WorkspaceDirectory.Resolve();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(WorkspaceDirectory.EnvVar, _previousEnv);
        try { Directory.Delete(_tempRoot, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task ProcessAsync_WritesResultAndArchivesTask()
    {
        var executor = new FakeExecutor(env => AgentResult.Ok("hello " + env.UserId, TimeSpan.FromMilliseconds(5)));
        await using var runner = new TaskRunner(_workspace, executor);

        var envelope = NewEnvelope("task-runner-1", body: "say hi");
        TaskFile.Write(_workspace.Tasks.FullName, envelope);

        await runner.ProcessAsync(envelope, CancellationToken.None);

        // The runner writes the result then archives it — so the live results/ dir is empty,
        // and the result body lives in results/archive/YYYY-MM/<id>.txt.
        Assert.False(File.Exists(Path.Combine(_workspace.Tasks.FullName, "task-runner-1.txt")));
        Assert.False(File.Exists(Path.Combine(_workspace.Results.FullName, "task-runner-1.txt")));

        Assert.NotNull(new TaskArchive(_workspace).FindArchivedTask("task-runner-1"));

        var archivedResult = new DirectoryInfo(Path.Combine(_workspace.Results.FullName, "archive"))
            .EnumerateFiles("task-runner-1.txt", SearchOption.AllDirectories)
            .Single();
        var parsed = ResultBody.Parse(File.ReadAllText(archivedResult.FullName));
        Assert.Equal("hello chat-local", parsed.Text);
    }

    [Fact]
    public async Task ProcessAsync_PreservesMarkers()
    {
        var executor = new FakeExecutor(_ => new AgentResult
        {
            Body = "you have new mail",
            Duration = TimeSpan.FromMilliseconds(10),
            Attachments = ["/tmp/inbox.txt"],
        });
        await using var runner = new TaskRunner(_workspace, executor);

        var envelope = NewEnvelope("task-runner-attach", body: "check inbox");
        TaskFile.Write(_workspace.Tasks.FullName, envelope);

        await runner.ProcessAsync(envelope, CancellationToken.None);

        // The archive moved the result alongside the task, so look in the month-partitioned archive.
        var archived = new DirectoryInfo(Path.Combine(_workspace.Results.FullName, "archive"))
            .EnumerateFiles("task-runner-attach.txt", SearchOption.AllDirectories)
            .Single();
        var raw = File.ReadAllText(archived.FullName);
        var parsed = ResultBody.Parse(raw);
        Assert.Single(parsed.Attachments, "/tmp/inbox.txt");
        Assert.Equal("you have new mail", parsed.Text);
    }

    [Fact]
    public async Task ProcessAsync_ExecutorThrowsWritesErrorResult()
    {
        var executor = new FakeExecutor(_ => throw new InvalidOperationException("kaboom"));
        await using var runner = new TaskRunner(_workspace, executor);

        var envelope = NewEnvelope("task-runner-err", body: "trigger crash");
        TaskFile.Write(_workspace.Tasks.FullName, envelope);

        await runner.ProcessAsync(envelope, CancellationToken.None);

        // The result file is still archived after a crash. Look in archive.
        var archived = new DirectoryInfo(Path.Combine(_workspace.Results.FullName, "archive"))
            .EnumerateFiles("task-runner-err.txt", SearchOption.AllDirectories)
            .Single();
        var body = File.ReadAllText(archived.FullName);
        Assert.Contains("executor crashed", body);
        Assert.Contains("kaboom", body);
    }

    [Fact]
    public async Task StartAsync_EndToEnd_NewTaskFlowsThroughExecutorToResultArchive()
    {
        var executor = new FakeExecutor(env => AgentResult.Ok("processed " + env.Id, TimeSpan.FromMilliseconds(2)));
        await using var watcher = new TaskWatcher(_workspace);
        await using var runner = new TaskRunner(_workspace, executor, watcher);

        var runnerTask = runner.StartAsync();

        var envelope = NewEnvelope("task-runner-e2e", body: "do work");
        TaskFile.Write(_workspace.Tasks.FullName, envelope);

        // Poll the archive for completion — runner is async.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (new TaskArchive(_workspace).FindArchivedTask("task-runner-e2e") is not null)
            {
                break;
            }
            await Task.Delay(50);
        }

        Assert.NotNull(new TaskArchive(_workspace).FindArchivedTask("task-runner-e2e"));
    }

    [Fact]
    public async Task ProcessAsync_WhenSkillMatchesTrigger_SkillHandlesAndExecutorIsBypassed()
    {
        // The executor is wired to throw so the test fails loudly if skill routing falls through
        // to the agent executor unexpectedly.
        var executor = new FakeExecutor(_ => throw new InvalidOperationException(
            "executor must NOT be called when a skill claims the task"));
        var registry = new SkillRegistry();
        registry.RegisterInstance(new EchoSkill());

        await using var runner = new TaskRunner(_workspace, executor, skills: registry);

        // First whitespace-delimited token is "echo" — the EchoSkill claims that trigger.
        var envelope = NewEnvelope("task-skill-hit", body: "echo hello world");
        TaskFile.Write(_workspace.Tasks.FullName, envelope);

        await runner.ProcessAsync(envelope, CancellationToken.None);

        var archived = new DirectoryInfo(Path.Combine(_workspace.Results.FullName, "archive"))
            .EnumerateFiles("task-skill-hit.txt", SearchOption.AllDirectories)
            .Single();
        var parsed = ResultBody.Parse(File.ReadAllText(archived.FullName));

        // EchoSkill prefixes "echo: " and serialises k=v of every arg supplied by the runner.
        Assert.StartsWith("echo: ", parsed.Text);
        Assert.Contains("task_id=task-skill-hit", parsed.Text);
        Assert.Contains("body=echo hello world", parsed.Text);
    }

    [Fact]
    public async Task ProcessAsync_WhenNoSkillMatches_ExecutorRunsAsBefore()
    {
        var executorCalled = false;
        var executor = new FakeExecutor(env =>
        {
            executorCalled = true;
            return AgentResult.Ok("agent ran " + env.Id, TimeSpan.FromMilliseconds(7));
        });
        // Registry is supplied but only knows "echo" / "ping"; the task's trigger is "unknown-verb".
        var registry = new SkillRegistry();
        registry.RegisterInstance(new EchoSkill());

        await using var runner = new TaskRunner(_workspace, executor, skills: registry);

        var envelope = NewEnvelope("task-skill-miss", body: "unknown-verb please do something");
        TaskFile.Write(_workspace.Tasks.FullName, envelope);

        await runner.ProcessAsync(envelope, CancellationToken.None);

        Assert.True(executorCalled, "executor must run when no skill claims the trigger");

        var archived = new DirectoryInfo(Path.Combine(_workspace.Results.FullName, "archive"))
            .EnumerateFiles("task-skill-miss.txt", SearchOption.AllDirectories)
            .Single();
        var parsed = ResultBody.Parse(File.ReadAllText(archived.FullName));
        Assert.Equal("agent ran task-skill-miss", parsed.Text);
    }

    [Theory]
    [InlineData("echo hello", "echo")]
    [InlineData("  echo hello", "echo")]
    [InlineData("ECHO HELLO", "echo")]
    [InlineData("echo\nthen more", "echo")]
    [InlineData("echo\tthen more", "echo")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("ONE_WORD", "one_word")]
    public void ExtractTrigger_HandlesEdgeCases(string body, string? expected)
    {
        Assert.Equal(expected, TaskRunner.ExtractTrigger(body));
    }

    private static TaskEnvelope NewEnvelope(string id, string body) => new()
    {
        Id = id,
        Timestamp = DateTimeOffset.UtcNow,
        Body = body,
        Source = TaskSource.Chat,
        ChannelId = "local-chat",
        UserId = "chat-local",
        AccessTier = AccessTier.Owner,
        Priority = TaskPriority.Normal,
    };

    private sealed class FakeExecutor(Func<TaskEnvelope, AgentResult> respond) : IAgentExecutor
    {
        public string Id => "fake";
        public Task<AgentResult> ExecuteAsync(TaskEnvelope task, CancellationToken ct) =>
            Task.FromResult(respond(task));
    }
}
