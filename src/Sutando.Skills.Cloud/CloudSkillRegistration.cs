using System.Collections.Generic;
using Sutando.Skills.Cloud.Google;
using Sutando.Skills.Cloud.OpenAI;
using Sutando.Skills.Cloud.Orchestration;
using Sutando.Skills.Cloud.Twitter;

namespace Sutando.Skills.Cloud;

/// <summary>
/// Entry point for registering the cloud-integration skills bundled in this assembly with a
/// <see cref="SkillRegistry"/>. Each skill declares the env vars it needs; only skills whose
/// requirements are met get registered. Missing-credential skills are silently skipped, which
/// means the agent gets a clean "trigger unknown" instead of a runtime explosion when the
/// integration was never configured.
/// </summary>
/// <remarks>
/// Call this from your host's startup once the <see cref="SkillRegistry"/> is constructed and
/// the on-disk discovery pass has completed — assembly registrations layer on top of disk
/// discoveries (the registry is order-agnostic; IDs collide only on duplicate registration).
/// </remarks>
public static class CloudSkillRegistration
{
    /// <summary>
    /// Register every cloud skill whose required environment variables are present in
    /// <paramref name="env"/>. Returns the IDs of the skills that were registered.
    /// </summary>
    /// <param name="registry">Target registry.</param>
    /// <param name="env">
    /// Environment lookup. Pass <see cref="System.Environment.GetEnvironmentVariables()"/>
    /// (wrapped as a dictionary) in production; pass a test dictionary in tests.
    /// </param>
    public static IReadOnlyList<string> Register(SkillRegistry registry, IReadOnlyDictionary<string, string> env)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(env);

        var registered = new List<string>();

        foreach (var entry in Entries)
        {
            if (entry.Required.All(name => env.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)))
            {
                var skill = entry.Factory();
                registry.RegisterInstance(skill);
                registered.Add(skill.Manifest.Id);
            }
        }

        return registered;
    }

    /// <summary>
    /// Convenience overload that reads from the current process environment.
    /// </summary>
    public static IReadOnlyList<string> Register(SkillRegistry registry) =>
        Register(registry, CurrentEnvironment());

    private static IReadOnlyDictionary<string, string> CurrentEnvironment()
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                dict[key] = value;
            }
        }
        return dict;
    }

    private sealed record Entry(IReadOnlyList<string> Required, Func<ISkill> Factory);

    // Static catalog of the cloud skills this assembly ships, in registration order. Append a
    // new line per skill once it lands — keep the list flat so it's easy to grep for "which
    // cloud skills exist?" and what env vars each one needs.
    private static readonly IReadOnlyList<Entry> Entries =
    [
        new Entry([GeminiTextToSpeechSkill.ApiKeyEnvVar], () => new GeminiTextToSpeechSkill()),
        new Entry([OpenAiTextToSpeechSkill.ApiKeyEnvVar], () => new OpenAiTextToSpeechSkill()),
        new Entry([GeminiImageGenerationSkill.ApiKeyEnvVar], () => new GeminiImageGenerationSkill()),
        new Entry(XTwitterSkill.RequiredEnvVars, () => new XTwitterSkill()),
        new Entry([GeminiImageGenerationSkill.ApiKeyEnvVar], () => new MakeViralVideoSkill()),
    ];
}
