using Sutando.Skills;
using Sutando.Skills.Cloud;
using Sutando.Skills.Cloud.Google;

namespace Sutando.Tests.Skills.Cloud;

/// <summary>
/// Verifies <see cref="CloudSkillRegistration.Register(SkillRegistry, IReadOnlyDictionary{string, string})"/>
/// gates each cloud skill behind the env vars its manifest requires, and that present-and-valid
/// env vars land the skill in the registry under its canonical id.
/// </summary>
public sealed class CloudSkillRegistrationTests
{
    [Fact]
    public void Register_WithNoEnv_RegistersNothing()
    {
        var registry = new SkillRegistry();
        var registered = CloudSkillRegistration.Register(registry, new Dictionary<string, string>());

        Assert.Empty(registered);
        Assert.Null(registry.TryGet("gemini-tts"));
    }

    [Fact]
    public void Register_WithGeminiApiKey_RegistersGeminiTtsOnly()
    {
        var registry = new SkillRegistry();
        var registered = CloudSkillRegistration.Register(registry,
            new Dictionary<string, string> { [GeminiTextToSpeechSkill.ApiKeyEnvVar] = "fake-key" });

        Assert.Equal(["gemini-tts"], registered);
        var skill = registry.TryGet("gemini-tts");
        Assert.NotNull(skill);
        Assert.IsType<GeminiTextToSpeechSkill>(skill);
    }

    [Fact]
    public void Register_WithWhitespaceApiKey_TreatsAsMissing()
    {
        // A defensively-empty env value (`set GEMINI_API_KEY=`) shouldn't register the skill —
        // otherwise the agent would advertise a trigger that fails on first invocation.
        var registry = new SkillRegistry();
        var registered = CloudSkillRegistration.Register(registry,
            new Dictionary<string, string> { [GeminiTextToSpeechSkill.ApiKeyEnvVar] = "   " });

        Assert.Empty(registered);
        Assert.Null(registry.TryGet("gemini-tts"));
    }
}
