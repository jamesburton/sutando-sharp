using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sutando.Workspace;

/// <summary>
/// Atomic writer/reader for <c>&lt;workspace&gt;/core-status.json</c> — the single-file
/// signal that says "what is the agent doing right now?". Dashboards and bots poll this;
/// channel adapters never write to it.
/// </summary>
public sealed class CoreStatus
{
    /// <summary>Status value when the agent is idle (no in-flight task).</summary>
    public const string Idle = "idle";

    /// <summary>Status value when the agent is actively working on something.</summary>
    public const string Running = "running";

    /// <summary>Status value when the agent reports a degraded state but is still alive.</summary>
    public const string Degraded = "degraded";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly WorkspaceDirectory _workspace;

    /// <param name="workspace">Resolved workspace.</param>
    public CoreStatus(WorkspaceDirectory workspace) => _workspace = workspace;

    /// <summary>Signal "agent is running task X".</summary>
    /// <param name="step">Short human-readable step description.</param>
    public void SignalRunning(string step) => Write(Running, step);

    /// <summary>Signal "agent is idle, ready for work".</summary>
    public void SignalIdle() => Write(Idle, step: null);

    /// <summary>Signal "agent is degraded but alive".</summary>
    /// <param name="reason">Short human-readable reason.</param>
    public void SignalDegraded(string reason) => Write(Degraded, reason);

    /// <summary>Read the current payload from disk; returns <see langword="null"/> if absent or unreadable.</summary>
    public CoreStatusPayload? Read()
    {
        var file = _workspace.CoreStatusFile;
        if (!file.Exists)
        {
            return null;
        }
        try
        {
            using var stream = file.OpenRead();
            return JsonSerializer.Deserialize<CoreStatusPayload>(stream, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _ = ex;
            return null;
        }
    }

    private void Write(string status, string? step)
    {
        var payload = new CoreStatusPayload
        {
            Status = status,
            Step = step,
            Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        WorkspaceDirectory.AtomicWrite(_workspace.CoreStatusFile.FullName, json);
    }
}

/// <summary>Payload schema for <c>core-status.json</c>.</summary>
public sealed record CoreStatusPayload
{
    /// <summary>Current status — one of <see cref="CoreStatus.Idle"/>, <see cref="CoreStatus.Running"/>, <see cref="CoreStatus.Degraded"/>.</summary>
    [JsonPropertyName("status")] public string Status { get; init; } = CoreStatus.Idle;

    /// <summary>Short description of the current step; omitted while idle.</summary>
    [JsonPropertyName("step")] public string? Step { get; init; }

    /// <summary>Unix epoch seconds when this signal was written.</summary>
    [JsonPropertyName("ts")] public long Ts { get; init; }
}
