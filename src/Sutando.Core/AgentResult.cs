namespace Sutando.Core;

/// <summary>
/// The outcome of an <see cref="IAgentExecutor"/> running a task. Maps onto the bridge's
/// result-body grammar (see <see cref="Bridge.ResultMarkers"/>).
/// </summary>
public sealed record AgentResult
{
    /// <summary>Human-readable body text that will be delivered to the channel.</summary>
    public required string Body { get; init; }

    /// <summary>Wall-clock time the execution took.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>True if the executor hit the task's timeout budget and aborted.</summary>
    public bool TimedOut { get; init; }

    /// <summary>If non-null, the result is a dedupe pointer to another task's result.</summary>
    public string? DedupedTo { get; init; }

    /// <summary>True if the channel should archive without delivering anything.</summary>
    public bool NoSend { get; init; }

    /// <summary>True if delivery already happened via another path.</summary>
    public bool AlreadyReplied { get; init; }

    /// <summary>File paths to attach alongside the body.</summary>
    public IReadOnlyList<string> Attachments { get; init; } = [];

    /// <summary>True if the task failed and the body is an error report rather than a success body.</summary>
    public bool IsError { get; init; }

    /// <summary>Convenience builder for the common success case.</summary>
    public static AgentResult Ok(string body, TimeSpan duration) =>
        new() { Body = body, Duration = duration };

    /// <summary>Convenience builder for an error result.</summary>
    public static AgentResult Error(string message, TimeSpan duration) =>
        new() { Body = message, Duration = duration, IsError = true };

    /// <summary>Convenience builder for a timed-out execution.</summary>
    public static AgentResult Timeout(TimeSpan budget) =>
        new() { Body = $"timed out after {budget}", Duration = budget, TimedOut = true, IsError = true };

    /// <summary>Convenience builder for a deduped result.</summary>
    public static AgentResult Deduped(string targetTaskId, TimeSpan duration) =>
        new() { Body = string.Empty, Duration = duration, DedupedTo = targetTaskId };
}
