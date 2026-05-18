using Sutando.Skills;
using Sutando.Skills.Builtin;
using Sutando.Skills.Discovery;
using Sutando.Workspace;

namespace Sutando.Tests.Skills;

public sealed class SkillDiscoveryAndRegistryTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string? _previousEnv;
    private readonly WorkspaceDirectory _workspace;

    public SkillDiscoveryAndRegistryTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "sutando-skills-" + Guid.NewGuid().ToString("N"));
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
    public void Discover_ReturnsEmpty_WhenNoSkillsRootExists()
    {
        var discovery = SkillDiscovery.Default(_workspace);
        Assert.Empty(discovery.Discover());
    }

    [Fact]
    public void Discover_FindsSkillJsonInWorkspaceRoot()
    {
        WriteSkill("echo-on-disk", """
            {
              "id": "echo-on-disk",
              "name": "Echo (on disk)",
              "entry": "Sutando.Skills.Builtin.EchoSkill, Sutando.Skills",
              "triggers": ["echo"]
            }
            """);

        var discovery = SkillDiscovery.Default(_workspace);
        var found = discovery.Discover();

        Assert.Single(found);
        Assert.Equal("echo-on-disk", found[0].Manifest.Id);
        Assert.True(Directory.Exists(found[0].Root));
        Assert.True(File.Exists(found[0].ManifestPath));
    }

    [Fact]
    public void Discover_SkipsMalformedManifests_LogsButContinues()
    {
        var skillDir = Path.Combine(_workspace.Root.FullName, "skills", "broken");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "skill.json"), "{ not really json");

        WriteSkill("good", """
            { "id": "good", "name": "Good", "entry": "X.Y, X" }
            """);

        var discovery = SkillDiscovery.Default(_workspace);
        var found = discovery.Discover();

        Assert.Single(found);
        Assert.Equal("good", found[0].Manifest.Id);
    }

    [Fact]
    public void Registry_RegistersInstance_AndResolvesById()
    {
        var registry = new SkillRegistry();
        var skill = new EchoSkill();
        registry.RegisterInstance(skill);

        Assert.Same(skill, registry.Get("echo"));
    }

    [Fact]
    public void Registry_ResolvesByTrigger_CaseInsensitive()
    {
        var registry = new SkillRegistry();
        registry.RegisterInstance(new EchoSkill());

        var hitsLower = registry.ResolveByTrigger("ping");
        var hitsUpper = registry.ResolveByTrigger("PING");

        Assert.Single(hitsLower);
        Assert.Single(hitsUpper);
        Assert.Equal("echo", hitsLower[0].Manifest.Id);
    }

    [Fact]
    public async Task Registry_ManagedSkill_FromDiscovery_IsInstantiableAndCallable()
    {
        WriteSkill("echo", """
            {
              "id": "echo",
              "name": "Echo",
              "runtime": "managed",
              "entry": "Sutando.Skills.Builtin.EchoSkill, Sutando.Skills",
              "triggers": ["echo"]
            }
            """);

        var registry = new SkillRegistry();
        registry.Register(SkillDiscovery.Default(_workspace).Discover());

        var skill = registry.Get("echo");
        var ctx = new SkillContext(_workspace, _workspace.Root.FullName);
        var args = new Dictionary<string, string> { ["greet"] = "world" };
        var result = await skill.ExecuteAsync(ctx, args, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("greet=world", result.Body);
    }

    [Fact]
    public void Registry_TryGet_ReturnsNullForUnknownId()
    {
        var registry = new SkillRegistry();
        Assert.Null(registry.TryGet("does-not-exist"));
    }

    [Fact]
    public void Discover_RespectsPrecedence_WorkspaceShadowsUser()
    {
        var userRoot = Path.Combine(_tempRoot, "userhome", ".sutando", "skills");
        Directory.CreateDirectory(Path.Combine(userRoot, "shared"));
        File.WriteAllText(Path.Combine(userRoot, "shared", "skill.json"), """
            { "id": "shared", "name": "User Version", "entry": "U" }
            """);

        WriteSkill("shared", """
            { "id": "shared", "name": "Workspace Version", "entry": "W" }
            """);

        var discovery = new SkillDiscovery(new SkillDiscoveryOptions
        {
            WorkspaceSkillsRoot = Path.Combine(_workspace.Root.FullName, "skills"),
            UserSkillsRoot = userRoot,
        });

        var found = discovery.Discover();
        Assert.Single(found);
        Assert.Equal("Workspace Version", found[0].Manifest.Name);
    }

    private void WriteSkill(string id, string manifestJson)
    {
        var dir = Path.Combine(_workspace.Root.FullName, "skills", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "skill.json"), manifestJson);
    }
}
