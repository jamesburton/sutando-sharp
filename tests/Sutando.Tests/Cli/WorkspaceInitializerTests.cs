using Sutando.Workspace;

namespace Sutando.Tests.Cli;

/// <summary>
/// Tests for the <c>sutando init</c> wizard. The orchestrator is internal — surfaced to
/// this assembly via <c>[InternalsVisibleTo]</c> in the CLI project — and uses constructor-
/// injected I/O so every dependency (probe, stdout, stdin, dashboard launcher) is fakeable.
/// </summary>
public sealed class WorkspaceInitializerTests : IDisposable
{
    private readonly string _tempRoot;

    public WorkspaceInitializerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "sutando-init-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void CreateSubdirectories_CreatesStandardLayout()
    {
        var root = new DirectoryInfo(Path.Combine(_tempRoot, "ws"));
        var created = WorkspaceInitializer.CreateSubdirectories(root);

        Assert.True(root.Exists, "workspace root must exist");
        Assert.Equal(WorkspaceInitializer.StandardSubdirs.Length, created.Count);
        foreach (var sub in WorkspaceInitializer.StandardSubdirs)
        {
            var expected = Path.Combine(root.FullName, sub.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(Directory.Exists(expected), $"missing subdir: {expected}");
        }
    }

    [Fact]
    public void CreateSubdirectories_IsIdempotent_WhenInvokedTwice()
    {
        var root = new DirectoryInfo(Path.Combine(_tempRoot, "ws-idemp"));
        WorkspaceInitializer.CreateSubdirectories(root);
        var second = WorkspaceInitializer.CreateSubdirectories(root);
        Assert.Equal(WorkspaceInitializer.StandardSubdirs.Length, second.Count);
        Assert.All(second, d => Assert.True(d.Exists));
    }

    [Fact]
    public void WriteEnvExample_EmitsAllKnownVars_WhenAbsent()
    {
        var root = new DirectoryInfo(Path.Combine(_tempRoot, "ws-env"));
        root.Create();
        var (file, written) = WorkspaceInitializer.WriteEnvExample(root);

        Assert.True(written, "expected fresh write");
        Assert.True(file.Exists);

        var text = File.ReadAllText(file.FullName);
        foreach (var (name, _) in WorkspaceInitializer.KnownEnvVars)
        {
            Assert.Contains($"{name}=", text);
        }
    }

    [Fact]
    public void WriteEnvExample_DoesNotOverwriteExistingFile()
    {
        var root = new DirectoryInfo(Path.Combine(_tempRoot, "ws-env-keep"));
        root.Create();
        var path = Path.Combine(root.FullName, ".env.example");
        File.WriteAllText(path, "USER_EDITED=true\n");

        var (_, written) = WorkspaceInitializer.WriteEnvExample(root);

        Assert.False(written);
        Assert.Equal("USER_EDITED=true\n", File.ReadAllText(path));
    }

    [Fact]
    public async Task RunAsync_FullHappyPath_CreatesEverythingAndPassesAllProbes()
    {
        var root = Path.Combine(_tempRoot, "ws-happy");
        var probe = new FakeProbe(
            binaries: ["claude", "codex", "python3", "node", "bash"],
            reachable: true);
        var stdout = new StringWriter();
        var initializer = new WorkspaceInitializer(probe: probe, output: stdout, error: stdout);

        var (exit, result) = await initializer.RunAsync(new WorkspaceInitializerOptions
        {
            AssumeYes = true,
            WriteHeartbeat = false, // tested separately to keep this test fast + isolated
            WorkspaceOverride = root,
            // Override the probe URL so it goes through the (fake) probe and never hits the net.
            NetworkProbeUrl = "https://example.test/healthz",
        });

        Assert.Equal(0, exit);
        Assert.True(Directory.Exists(root));
        Assert.True(result.EnvExampleWritten);
        Assert.Contains(result.Checks, c => c.Label.Contains("claude on PATH", StringComparison.Ordinal) && c.Passed);
        Assert.Contains(result.Checks, c => c.Label.Contains("reachable: https://example.test/healthz", StringComparison.Ordinal) && c.Passed);
        Assert.Contains(result.Checks, c => c.Label == "workspace path resolved" && c.Passed);

        var transcript = stdout.ToString();
        Assert.Contains("sutando init will set up:", transcript);
        Assert.Contains("prerequisites:", transcript);
    }

    [Fact]
    public async Task RunAsync_MissingBinariesAreReportedButDoNotFail()
    {
        var root = Path.Combine(_tempRoot, "ws-no-bins");
        var probe = new FakeProbe(binaries: [], reachable: false);
        var stdout = new StringWriter();
        var initializer = new WorkspaceInitializer(probe: probe, output: stdout, error: stdout);

        var (exit, result) = await initializer.RunAsync(new WorkspaceInitializerOptions
        {
            AssumeYes = true,
            WriteHeartbeat = false,
            WorkspaceOverride = root,
            NetworkProbeUrl = "https://example.test/healthz",
        });

        Assert.Equal(0, exit);
        // Each binary check fails, but init still succeeds — they're informational.
        Assert.Contains(result.Checks, c => c.Label.Contains("claude on PATH", StringComparison.Ordinal) && !c.Passed);
        Assert.Contains(result.Checks, c => c.Label.Contains("reachable:", StringComparison.Ordinal) && !c.Passed);
    }

    [Fact]
    public async Task RunAsync_DeclinedConfirmation_ReturnsNonZero_AndCreatesNothing()
    {
        var root = Path.Combine(_tempRoot, "ws-decline");
        var probe = new FakeProbe(binaries: [], reachable: false);
        var stdout = new StringWriter();
        var initializer = new WorkspaceInitializer(
            probe: probe,
            output: stdout,
            error: stdout,
            // Simulate the user typing "n" at the prompt.
            readLine: () => "n");

        var (exit, _) = await initializer.RunAsync(new WorkspaceInitializerOptions
        {
            AssumeYes = false,
            WriteHeartbeat = false,
            WorkspaceOverride = root,
            NetworkProbeUrl = null,
        });

        Assert.NotEqual(0, exit);
        // Decline must not create the workspace.
        Assert.False(Directory.Exists(root), $"root must not be created when declined: {root}");
        Assert.Contains("aborted.", stdout.ToString());
    }

    [Fact]
    public async Task RunAsync_WhenLaunchDashboard_InvokesHook_AndDoesNotSpawnRealProcess()
    {
        var root = Path.Combine(_tempRoot, "ws-dash");
        var probe = new FakeProbe(binaries: [], reachable: true);
        var stdout = new StringWriter();
        string? launchedFile = null;
        string[]? launchedArgs = null;
        var initializer = new WorkspaceInitializer(
            probe: probe,
            output: stdout,
            error: stdout,
            launchDashboard: (file, args) => { launchedFile = file; launchedArgs = args; });

        var (exit, _) = await initializer.RunAsync(new WorkspaceInitializerOptions
        {
            AssumeYes = true,
            LaunchDashboard = true,
            WriteHeartbeat = false,
            WorkspaceOverride = root,
            NetworkProbeUrl = null,
        });

        Assert.Equal(0, exit);
        Assert.Equal("sutando", launchedFile);
        Assert.NotNull(launchedArgs);
        Assert.Equal(["dashboard"], launchedArgs);
    }

    [Fact]
    public void WriteHeartbeatBaseline_WritesAliveFile()
    {
        var root = new DirectoryInfo(Path.Combine(_tempRoot, "ws-hb"));
        WorkspaceInitializer.CreateSubdirectories(root);

        var ok = WorkspaceInitializer.WriteHeartbeatBaseline(root);

        Assert.True(ok);
        var coresDir = new DirectoryInfo(Path.Combine(root.FullName, "state", "cores"));
        Assert.True(coresDir.Exists);
        var alive = coresDir.EnumerateFiles("*.alive").ToList();
        Assert.NotEmpty(alive);
    }

    private sealed class FakeProbe(HashSet<string> binaries, bool reachable) : IPrerequisiteProbe
    {
        public FakeProbe(IEnumerable<string> binaries, bool reachable)
            : this(new HashSet<string>(binaries, StringComparer.Ordinal), reachable) { }

        public bool BinaryOnPath(string binary) => binaries.Contains(binary);

        public Task<bool> ReachableAsync(string url, TimeSpan timeout, CancellationToken ct)
            => Task.FromResult(reachable);
    }
}
