using Sutando.Skills;
using Sutando.Skills.Cloud;
using Sutando.Skills.Cloud.Google;
using Sutando.Skills.Cloud.OpenAI;

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
    public void Register_WithOpenAiApiKey_RegistersOpenAiTtsOnly()
    {
        var registry = new SkillRegistry();
        var registered = CloudSkillRegistration.Register(registry,
            new Dictionary<string, string> { [OpenAiTextToSpeechSkill.ApiKeyEnvVar] = "sk-fake" });

        Assert.Equal(["openai-tts"], registered);
        Assert.IsType<OpenAiTextToSpeechSkill>(registry.TryGet("openai-tts"));
        Assert.Null(registry.TryGet("gemini-tts"));
    }

    [Fact]
    public void Register_WithBothApiKeys_RegistersBothSkills()
    {
        var registry = new SkillRegistry();
        var registered = CloudSkillRegistration.Register(registry, new Dictionary<string, string>
        {
            [GeminiTextToSpeechSkill.ApiKeyEnvVar] = "fake-key",
            [OpenAiTextToSpeechSkill.ApiKeyEnvVar] = "sk-fake",
        });

        Assert.Contains("gemini-tts", registered);
        Assert.Contains("openai-tts", registered);
        Assert.Equal(2, registered.Count);
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
