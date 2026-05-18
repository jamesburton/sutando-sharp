namespace Sutando.Skills;

/// <summary>
/// Outcome of a skill invocation. Mirrors the shape of
/// <see cref="Sutando.Core.AgentResult"/> deliberately so skills can flow through the same
/// channel pipelines as agent tasks when needed.
/// </summary>
public sealed record SkillResult
{
    /// <summary>Human-readable body produced by the skill.</summary>
    public required string Body { get; init; }

    /// <summary>True if the skill ran successfully.</summary>
    public bool Success { get; init; } = true;

    /// <summary>Error message when <see cref="Success"/> is <see langword="false"/>.</summary>
    public string? Error { get; init; }

    /// <summary>Wall-clock duration of the invocation.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Absolute file paths produced by the skill (attachments, generated assets).</summary>
    public IReadOnlyList<string> Artifacts { get; init; } = [];

    /// <summary>Convenience builder for the common success case.</summary>
    public static SkillResult Ok(string body, TimeSpan duration, IReadOnlyList<string>? artifacts = null) =>
        new() { Body = body, Duration = duration, Artifacts = artifacts ?? [] };

    /// <summary>Convenience builder for failure.</summary>
    public static SkillResult Fail(string error, TimeSpan duration, string? body = null) =>
        new() { Body = body ?? error, Success = false, Error = error, Duration = duration };
}
