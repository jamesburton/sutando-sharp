using Microsoft.AspNetCore.SignalR;

namespace Sutando.Dashboard;

/// <summary>
/// SignalR hub for the dashboard's live updates.
/// </summary>
/// <remarks>
/// Clients connect to <c>/hub/status</c> and listen for three server-broadcast events:
/// <list type="bullet">
///   <item><description><c>core_status_changed</c> — payload: parsed <c>core-status.json</c>.</description></item>
///   <item><description><c>task_added</c> — payload: <see cref="TaskAddedPayload"/>.</description></item>
///   <item><description><c>result_added</c> — payload: <see cref="ResultAddedPayload"/>.</description></item>
/// </list>
/// The hub itself is empty — broadcasting happens from <see cref="WorkspaceBroadcaster"/>
/// via <c>IHubContext&lt;StatusHub&gt;</c>.
/// </remarks>
public sealed class StatusHub : Hub
{
}

/// <summary>Payload pushed to <c>task_added</c>.</summary>
public sealed record TaskAddedPayload
{
    /// <summary>Task identifier read from the envelope.</summary>
    public required string Id { get; init; }

    /// <summary>Absolute path of the newly written task file.</summary>
    public required string Path { get; init; }

    /// <summary>UTC timestamp of detection.</summary>
    public required DateTimeOffset DetectedAt { get; init; }
}

/// <summary>Payload pushed to <c>result_added</c>.</summary>
public sealed record ResultAddedPayload
{
    /// <summary>Task identifier whose result was written; <see langword="null"/> for proactive/question payloads.</summary>
    public required string Id { get; init; }

    /// <summary>Absolute path of the new result file.</summary>
    public required string Path { get; init; }

    /// <summary>UTC timestamp of detection.</summary>
    public required DateTimeOffset DetectedAt { get; init; }
}
