using Sutando.Workspace;

namespace Sutando.Tests;

public sealed class CoreHeartbeatTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string? _previousEnv;
    private readonly WorkspaceDirectory _workspace;

    public CoreHeartbeatTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "sutando-hb-" + Guid.NewGuid().ToString("N"));
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
    public void WriteOnce_CreatesAliveFileWithExpectedFields()
    {
        using var hb = new CoreHeartbeat(_workspace);
        hb.SetStatus("running");
        hb.WriteOnce();

        Assert.True(hb.AliveFile.Exists);
        var payload = hb.Read();
        Assert.NotNull(payload);
        Assert.Equal("running", payload!.Status);
        Assert.Equal(Environment.ProcessId, payload.Pid);
        Assert.Equal(1, payload.SchemaVersion);
        Assert.True(payload.StartedAt > 0);
        Assert.True(payload.LastBeatAt >= payload.StartedAt);
        Assert.False(string.IsNullOrWhiteSpace(payload.Host));
    }

    [Fact]
    public void AliveFile_LivesUnderStateCoresHostname()
    {
        using var hb = new CoreHeartbeat(_workspace);
        Assert.Contains(Path.Combine("state", "cores"), hb.AliveFile.FullName);
        Assert.EndsWith(".alive", hb.AliveFile.Name);
    }

    [Fact]
    public async Task DisposeAsync_RemovesAliveFile()
    {
        await using (var hb = new CoreHeartbeat(_workspace))
        {
            hb.WriteOnce();
            Assert.True(hb.AliveFile.Exists);
        }
        // Re-stat — directory enumeration is more reliable than the stale FileInfo handle
        Assert.False(File.Exists(Path.Combine(_workspace.State.FullName, "cores", $"{Environment.MachineName.Split('.')[0]}.alive")));
    }

    [Fact]
    public void AnyCoreAlive_ReturnsTrueWhenFreshAliveFileExists()
    {
        using var hb = new CoreHeartbeat(_workspace);
        hb.WriteOnce();
        Assert.True(CoreHeartbeat.AnyCoreAlive(_workspace));
    }

    [Fact]
    public void AnyCoreAlive_ReturnsFalseWhenAliveFileIsStale()
    {
        using var hb = new CoreHeartbeat(_workspace);
        hb.WriteOnce();
        var stale = DateTime.UtcNow - CoreHeartbeat.AliveWindow - TimeSpan.FromSeconds(5);
        File.SetLastWriteTimeUtc(hb.AliveFile.FullName, stale);
        Assert.False(CoreHeartbeat.AnyCoreAlive(_workspace));
    }
}
