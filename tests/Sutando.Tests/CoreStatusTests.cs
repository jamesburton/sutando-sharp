using Sutando.Workspace;

namespace Sutando.Tests;

public sealed class CoreStatusTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string? _previousEnv;
    private readonly WorkspaceDirectory _workspace;

    public CoreStatusTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "sutando-cs-" + Guid.NewGuid().ToString("N"));
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
    public void SignalRunning_WritesStepAndRunningStatus()
    {
        var cs = new CoreStatus(_workspace);
        cs.SignalRunning("processing email");

        var payload = cs.Read();
        Assert.NotNull(payload);
        Assert.Equal(CoreStatus.Running, payload!.Status);
        Assert.Equal("processing email", payload.Step);
        Assert.True(payload.Ts > 0);
    }

    [Fact]
    public void SignalIdle_OmitsStep()
    {
        var cs = new CoreStatus(_workspace);
        cs.SignalRunning("something");
        cs.SignalIdle();

        var payload = cs.Read();
        Assert.NotNull(payload);
        Assert.Equal(CoreStatus.Idle, payload!.Status);
        Assert.Null(payload.Step);
    }

    [Fact]
    public void Read_ReturnsNullWhenFileMissing()
    {
        var cs = new CoreStatus(_workspace);
        Assert.Null(cs.Read());
    }

    [Fact]
    public void OwnerActivity_RecordTruncatesAt80Chars()
    {
        var oa = new OwnerActivity(_workspace);
        var longSummary = new string('x', 200);
        oa.Record("chat", longSummary);

        var payload = oa.Read();
        Assert.NotNull(payload);
        Assert.Equal(80, payload!.Summary.Length);
        Assert.Equal("chat", payload.Channel);
    }
}
