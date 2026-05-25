using Sutando.Skills;
using Sutando.Skills.Cloud;
using Sutando.Skills.Cloud.Google;
using Sutando.Skills.Cloud.OpenAI;
using Sutando.Skills.Cloud.Twitter;
using Sutando.Skills.Discovery;
using Sutando.Workspace;

namespace Sutando.Tests.Cli;

/// <summary>
/// Tests for the CLI's skill-host wiring path — verifies that the registry-building logic
/// used by <c>sutando skills</c> subcommands correctly combines filesystem discovery with
/// cloud-skill env-var-gated registration.
///
/// These tests drive the same two-step construction that <c>SkillsHost.BuildRegistry</c>
/// performs: <see cref="SkillDiscovery.Default"/> + <see cref="CloudSkillRegistration.Register"/>.
/// They are located in <c>tests/Cli/</c> as the canonical integration point for the skills
/// subsystem as exposed by the CLI host.
/// </summary>
public sealed class SkillsHostTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string? _previousEnv;
    private readonly WorkspaceDirectory _workspace;

    public SkillsHostTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "sutando-skillshost-" + Guid.NewGuid().ToString("N"));
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

    // ---------------------------------------------------------------------------
    // Helpers mirroring the SkillsHost.BuildRegistry two-step construction
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Simulate the full registry construction that <c>SkillsHost.BuildRegistry</c> performs:
    /// disk discovery followed by cloud-skill registration with the given env dict.
    /// </summary>
    private (SkillRegistry Registry, IReadOnlyList<string> DiskIds, IReadOnlyList<string> CloudIds)
        BuildRegistry(IReadOnlyDictionary<string, string> env)
    {
        var registry = new SkillRegistry();
        var discovered = SkillDiscovery.Default(_workspace).Discover();
        registry.Register(discovered);
        var diskIds = discovered.Select(d => d.Manifest.Id).ToList();
        var cloudIds = CloudSkillRegistration.Register(registry, env);
        return (registry, diskIds, cloudIds);
    }

    // ---------------------------------------------------------------------------
    // Cloud registration gating
    // ---------------------------------------------------------------------------

    [Fact]
    public void BuildRegistry_WithEmptyEnv_RegistersNoCloudSkills()
    {
        var (registry, _, cloudIds) = BuildRegistry(new Dictionary<string, string>());

        Assert.Empty(cloudIds);
        Assert.Null(registry.TryGet("gemini-tts"));
        Assert.Null(registry.TryGet("openai-tts"));
        Assert.Null(registry.TryGet("image-generation"));
        Assert.Null(registry.TryGet("x-twitter"));
    }

    [Fact]
    public void BuildRegistry_WithGeminiApiKey_RegistersGeminiCloudSkills()
    {
        var env = new Dictionary<string, string>
        {
            [GeminiTextToSpeechSkill.ApiKeyEnvVar] = "fake-key",
        };

        var (registry, _, cloudIds) = BuildRegistry(env);

        // Assert.Contains so this test stays green when Wave C/D add more Gemini-gated skills.
        Assert.Contains("gemini-tts", cloudIds);
        Assert.Contains("image-generation", cloudIds);
        Assert.NotNull(registry.TryGet("gemini-tts"));
        Assert.NotNull(registry.TryGet("image-generation"));
    }

    [Fact]
    public void BuildRegistry_WithOpenAiApiKey_RegistersOpenAiSkillOnly()
    {
        var env = new Dictionary<string, string>
        {
            [OpenAiTextToSpeechSkill.ApiKeyEnvVar] = "sk-fake",
        };

        var (registry, _, cloudIds) = BuildRegistry(env);

        Assert.Contains("openai-tts", cloudIds);
        Assert.DoesNotContain("gemini-tts", cloudIds);
        Assert.NotNull(registry.TryGet("openai-tts"));
        Assert.Null(registry.TryGet("gemini-tts"));
    }

    [Fact]
    public void BuildRegistry_WithAllCloudCreds_RegistersAllCurrentCloudSkills()
    {
        var env = new Dictionary<string, string>
        {
            [GeminiTextToSpeechSkill.ApiKeyEnvVar] = "fake-gemini",
            [OpenAiTextToSpeechSkill.ApiKeyEnvVar] = "sk-fake",
            [XTwitterSkill.ApiKeyEnvVar] = "ck",
            [XTwitterSkill.ApiSecretEnvVar] = "cs",
            [XTwitterSkill.AccessTokenEnvVar] = "at",
            [XTwitterSkill.AccessSecretEnvVar] = "ats",
        };

        var (registry, _, cloudIds) = BuildRegistry(env);

        // Assert.Contains is intentionally used so that Wave C/D (gmail, calendar, viral-video)
        // can add more skills without breaking this test.
        Assert.Contains("gemini-tts", cloudIds);
        Assert.Contains("openai-tts", cloudIds);
        Assert.Contains("image-generation", cloudIds);
        Assert.Contains("x-twitter", cloudIds);

        Assert.NotNull(registry.TryGet("gemini-tts"));
        Assert.NotNull(registry.TryGet("openai-tts"));
        Assert.NotNull(registry.TryGet("image-generation"));
        Assert.NotNull(registry.TryGet("x-twitter"));
    }

    // ---------------------------------------------------------------------------
    // Disk discovery integration
    // ---------------------------------------------------------------------------

    [Fact]
    public void BuildRegistry_WithDiskSkill_ReportsItInDiskIds()
    {
        // Stage a minimal skill manifest under <workspace>/skills/echo-test/skill.json.
        var skillDir = Path.Combine(_tempRoot, "skills", "echo-test");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "skill.json"), """
            {
              "id": "echo-test",
              "name": "Echo Test",
              "entry": "Sutando.Skills.Builtin.EchoSkill, Sutando.Skills",
              "runtime": "managed",
              "triggers": ["echo"]
            }
            """);

        var (registry, diskIds, cloudIds) = BuildRegistry(new Dictionary<string, string>());

        Assert.Contains("echo-test", diskIds);
        Assert.Empty(cloudIds);
        Assert.NotNull(registry.TryGet("echo-test"));
    }

    [Fact]
    public void BuildRegistry_DiskAndCloud_BothReportedSeparately()
    {
        // Stage one disk skill.
        var skillDir = Path.Combine(_tempRoot, "skills", "disk-skill");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "skill.json"), """
            {
              "id": "disk-skill",
              "name": "Disk Skill",
              "entry": "Sutando.Skills.Builtin.EchoSkill, Sutando.Skills",
              "runtime": "managed",
              "triggers": ["disk"]
            }
            """);

        // Supply Gemini key so at least one cloud skill registers.
        var env = new Dictionary<string, string>
        {
            [GeminiTextToSpeechSkill.ApiKeyEnvVar] = "fake-key",
        };

        var (_, diskIds, cloudIds) = BuildRegistry(env);

        Assert.Contains("disk-skill", diskIds);
        Assert.Contains("gemini-tts", cloudIds);
        // Disk and cloud ID lists don't bleed into each other.
        Assert.DoesNotContain("gemini-tts", diskIds);
        Assert.DoesNotContain("disk-skill", cloudIds);
    }
}
