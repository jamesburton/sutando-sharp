using Sutando.Workspace;

namespace Sutando.Tests;

public sealed class WorkspaceDirectoryTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string? _previousEnv;

    public WorkspaceDirectoryTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "sutando-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _previousEnv = Environment.GetEnvironmentVariable(WorkspaceDirectory.EnvVar);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(WorkspaceDirectory.EnvVar, _previousEnv);
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // best effort
        }
    }

    [Fact]
    public void ResolvePath_PrefersEnvironmentVariable()
    {
        Environment.SetEnvironmentVariable(WorkspaceDirectory.EnvVar, _tempRoot);
        var resolved = WorkspaceDirectory.ResolvePath();
        Assert.Equal(_tempRoot, resolved);
    }

    [Fact]
    public void ResolvePath_ExpandsTildeInEnvValue()
    {
        Environment.SetEnvironmentVariable(WorkspaceDirectory.EnvVar, "~/sutando-test-tilde");
        var resolved = WorkspaceDirectory.ResolvePath();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(home, "sutando-test-tilde"), resolved);
    }

    [Fact]
    public void ResolvePath_FallsBackToDefaultWhenEnvUnset()
    {
        Environment.SetEnvironmentVariable(WorkspaceDirectory.EnvVar, null);
        var resolved = WorkspaceDirectory.ResolvePath();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(home, ".sutando", "workspace"), resolved);
    }

    [Fact]
    public void Resolve_CreatesRootDirectory()
    {
        var target = Path.Combine(_tempRoot, "workspace");
        Environment.SetEnvironmentVariable(WorkspaceDirectory.EnvVar, target);

        var workspace = WorkspaceDirectory.Resolve();

        Assert.True(workspace.Root.Exists);
        Assert.Equal(target, workspace.Root.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    [Fact]
    public void TasksAndResultsAreCreatedOnDemand()
    {
        var target = Path.Combine(_tempRoot, "workspace");
        Environment.SetEnvironmentVariable(WorkspaceDirectory.EnvVar, target);

        var workspace = WorkspaceDirectory.Resolve();
        Assert.True(workspace.Tasks.Exists);
        Assert.True(workspace.Results.Exists);
        Assert.True(workspace.State.Exists);
    }

    [Fact]
    public void MigrateLegacy_NoEvidence_DoesNothing()
    {
        var target = Path.Combine(_tempRoot, "workspace");
        Environment.SetEnvironmentVariable(WorkspaceDirectory.EnvVar, target);

        var legacy = Path.Combine(_tempRoot, "legacy-repo");
        Directory.CreateDirectory(legacy);

        var workspace = WorkspaceDirectory.Resolve(legacyFallback: legacy);
        Assert.False(workspace.MigrateLegacy(legacy));
    }

    [Fact]
    public void MigrateLegacy_WithEvidence_MovesDirectoriesOnce()
    {
        var target = Path.Combine(_tempRoot, "workspace");
        Environment.SetEnvironmentVariable(WorkspaceDirectory.EnvVar, target);

        var legacy = Path.Combine(_tempRoot, "legacy-repo");
        var legacyTasks = Path.Combine(legacy, "tasks");
        Directory.CreateDirectory(legacyTasks);
        File.WriteAllText(Path.Combine(legacyTasks, "task-12345.txt"), "id: task-12345\ntask: hello\n");

        var workspace = WorkspaceDirectory.Resolve(legacyFallback: legacy);

        Assert.True(Directory.Exists(Path.Combine(target, "tasks")));
        Assert.True(File.Exists(Path.Combine(target, "tasks", "task-12345.txt")));
        Assert.False(Directory.Exists(legacyTasks)); // moved, not copied

        // Idempotent second call: nothing more to do
        Assert.False(workspace.MigrateLegacy(legacy));
    }

    [Fact]
    public void AtomicWrite_NewFile_WritesContent()
    {
        var path = Path.Combine(_tempRoot, "atomic.txt");
        WorkspaceDirectory.AtomicWrite(path, "hello");
        Assert.Equal("hello", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void AtomicWrite_ReplacesExistingFile()
    {
        var path = Path.Combine(_tempRoot, "atomic.txt");
        File.WriteAllText(path, "original");
        WorkspaceDirectory.AtomicWrite(path, "replaced");
        Assert.Equal("replaced", File.ReadAllText(path));
    }
}
