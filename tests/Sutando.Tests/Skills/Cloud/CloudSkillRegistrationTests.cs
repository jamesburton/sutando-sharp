using Sutando.Skills;
using Sutando.Skills.Cloud;
using Sutando.Skills.Cloud.Common;
using Sutando.Skills.Cloud.Google;
using Sutando.Skills.Cloud.OpenAI;
using Sutando.Skills.Cloud.Orchestration;
using Sutando.Skills.Cloud.Twitter;

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
    public void Register_WithGeminiApiKey_RegistersEveryGeminiSkill()
    {
        // GEMINI_API_KEY unlocks every Gemini-family integration in one go — currently
        // gemini-tts and image-generation. New entries that key off the same env var should
        // appear here automatically; this test just asserts the set is non-empty and contains
        // both currently-shipped ids.
        var registry = new SkillRegistry();
        var registered = CloudSkillRegistration.Register(registry,
            new Dictionary<string, string> { [GeminiTextToSpeechSkill.ApiKeyEnvVar] = "fake-key" });

        Assert.Contains("gemini-tts", registered);
        Assert.Contains("image-generation", registered);
        Assert.Contains("make-viral-video", registered);
        Assert.IsType<GeminiTextToSpeechSkill>(registry.TryGet("gemini-tts"));
        Assert.IsType<GeminiImageGenerationSkill>(registry.TryGet("image-generation"));
        Assert.IsType<MakeViralVideoSkill>(registry.TryGet("make-viral-video"));
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
    public void Register_WithBothApiKeys_RegistersAllAvailableSkills()
    {
        var registry = new SkillRegistry();
        var registered = CloudSkillRegistration.Register(registry, new Dictionary<string, string>
        {
            [GeminiTextToSpeechSkill.ApiKeyEnvVar] = "fake-key",
            [OpenAiTextToSpeechSkill.ApiKeyEnvVar] = "sk-fake",
        });

        // Both provider keys present → every currently-shipped Cloud skill registers. New
        // entries gated on these env vars will lift the count here automatically.
        Assert.Contains("gemini-tts", registered);
        Assert.Contains("openai-tts", registered);
        Assert.Contains("image-generation", registered);
        Assert.Contains("make-viral-video", registered);
    }

    [Fact]
    public void Register_WithOnlyPartialTwitterCreds_DoesNotRegisterXTwitter()
    {
        // Three of the four OAuth1 env vars set, fourth missing — the skill should not appear.
        // Verifies the multi-env-var gating actually requires every entry, not just one.
        var registry = new SkillRegistry();
        var registered = CloudSkillRegistration.Register(registry, new Dictionary<string, string>
        {
            [XTwitterSkill.ApiKeyEnvVar] = "ck",
            [XTwitterSkill.ApiSecretEnvVar] = "cs",
            [XTwitterSkill.AccessTokenEnvVar] = "at",
            // AccessSecretEnvVar deliberately omitted
        });

        Assert.DoesNotContain("x-twitter", registered);
        Assert.Null(registry.TryGet("x-twitter"));
    }

    [Fact]
    public void Register_WithAllTwitterCreds_RegistersXTwitter()
    {
        var registry = new SkillRegistry();
        var registered = CloudSkillRegistration.Register(registry, new Dictionary<string, string>
        {
            [XTwitterSkill.ApiKeyEnvVar] = "ck",
            [XTwitterSkill.ApiSecretEnvVar] = "cs",
            [XTwitterSkill.AccessTokenEnvVar] = "at",
            [XTwitterSkill.AccessSecretEnvVar] = "ats",
        });

        Assert.Contains("x-twitter", registered);
        Assert.IsType<XTwitterSkill>(registry.TryGet("x-twitter"));
    }

    [Fact]
    public void Register_WithOnlyPartialGoogleOAuthCreds_DoesNotRegisterGmailOrCalendar()
    {
        // Two of the three OAuth2 env vars set, third missing — neither gmail nor calendar
        // should appear. Verifies multi-env-var gating for the shared OAuth helper vars.
        var registry = new SkillRegistry();
        var registered = CloudSkillRegistration.Register(registry, new Dictionary<string, string>
        {
            [GoogleOAuthHelper.ClientIdEnvVar] = "client-id",
            [GoogleOAuthHelper.ClientSecretEnvVar] = "client-secret",
            // RefreshTokenEnvVar deliberately omitted
        });

        Assert.DoesNotContain("gmail", registered);
        Assert.DoesNotContain("calendar", registered);
        Assert.Null(registry.TryGet("gmail"));
        Assert.Null(registry.TryGet("calendar"));
    }

    [Fact]
    public void Register_WithAllGoogleOAuthCreds_RegistersGmailAndCalendar()
    {
        var registry = new SkillRegistry();
        var registered = CloudSkillRegistration.Register(registry, new Dictionary<string, string>
        {
            [GoogleOAuthHelper.ClientIdEnvVar] = "client-id",
            [GoogleOAuthHelper.ClientSecretEnvVar] = "client-secret",
            [GoogleOAuthHelper.RefreshTokenEnvVar] = "refresh-token",
        });

        Assert.Contains("gmail", registered);
        Assert.Contains("calendar", registered);
        Assert.IsType<GmailSkill>(registry.TryGet("gmail"));
        Assert.IsType<CalendarSkill>(registry.TryGet("calendar"));
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
