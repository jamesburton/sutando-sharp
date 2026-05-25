using System.Text.Json.Serialization;

namespace Sutando.Proactive;

/// <summary>
/// A single cron-driven trigger loaded from <c>crons.json</c>. Mirrors the upstream
/// <c>skills/schedule-crons/crons.example.json</c> schema: <see cref="Name"/>,
/// <see cref="Cron"/>, and exactly one of <see cref="Prompt"/> / <see cref="PromptSkill"/>.
/// </summary>
/// <param name="Name">Unique identifier; used to avoid duplicate scheduling.</param>
/// <param name="Cron">5-field cron expression (e.g. <c>*/5 * * * *</c>) interpreted in UTC.</param>
/// <param name="Prompt">Direct prompt text to hand to the executor when this entry fires. Mutually exclusive with <see cref="PromptSkill"/>.</param>
/// <param name="PromptSkill">Skill id to invoke when this entry fires (e.g. <c>morning-briefing</c>). Mutually exclusive with <see cref="Prompt"/>.</param>
public sealed record CronEntry(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("cron")] string Cron,
    [property: JsonPropertyName("prompt")] string? Prompt = null,
    [property: JsonPropertyName("prompt_skill")] string? PromptSkill = null)
{
    /// <summary>
    /// Validates the prompt-xor-prompt_skill invariant.
    /// </summary>
    /// <returns><see langword="true"/> if exactly one of <see cref="Prompt"/> / <see cref="PromptSkill"/> is non-empty.</returns>
    public bool IsValid() =>
        !string.IsNullOrWhiteSpace(Name) &&
        !string.IsNullOrWhiteSpace(Cron) &&
        (!string.IsNullOrWhiteSpace(Prompt) ^ !string.IsNullOrWhiteSpace(PromptSkill));
}
