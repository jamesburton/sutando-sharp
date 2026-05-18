using System.Text.Json.Serialization;
using Sutando.Bridge;
using Sutando.Workspace;

namespace Sutando.Dashboard;

/// <summary>
/// Captures a point-in-time view of the workspace so the dashboard's initial render and the
/// <c>/snapshot</c> endpoint share a single code path.
/// </summary>
public sealed class DashboardSnapshot
{
    private readonly WorkspaceDirectory _workspace;
    private readonly CoreStatus _coreStatus;
    private readonly OwnerActivity _ownerActivity;

    /// <param name="workspace">Resolved workspace.</param>
    /// <param name="coreStatus">Reader for <c>core-status.json</c>.</param>
    /// <param name="ownerActivity">Reader for <c>state/last-owner-activity.json</c>.</param>
    public DashboardSnapshot(WorkspaceDirectory workspace, CoreStatus coreStatus, OwnerActivity ownerActivity)
    {
        _workspace = workspace;
        _coreStatus = coreStatus;
        _ownerActivity = ownerActivity;
    }

    /// <summary>Build a fresh snapshot from disk. Cheap — safe to call on every request.</summary>
    /// <returns>The captured snapshot.</returns>
    public DashboardSnapshotPayload Capture()
    {
        var coreStatus = _coreStatus.Read();
        var ownerActivity = _ownerActivity.Read();

        var heartbeats = new List<HeartbeatPayload>();
        var coresDir = new DirectoryInfo(Path.Combine(_workspace.State.FullName, "cores"));
        if (coresDir.Exists)
        {
            foreach (var file in coresDir.EnumerateFiles("*.alive"))
            {
                try
                {
                    var json = File.ReadAllText(file.FullName);
                    var payload = System.Text.Json.JsonSerializer.Deserialize<HeartbeatPayload>(json);
                    if (payload is not null)
                    {
                        heartbeats.Add(payload);
                    }
                }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
                {
                    // ignore unreadable beats — they show up as offline.
                }
            }
        }

        var pendingTasks = new List<TaskSummary>();
        var pendingCount = 0;
        if (_workspace.Tasks.Exists)
        {
            var sorted = _workspace.Tasks
                .EnumerateFiles("task-*.txt", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
            pendingCount = sorted.Count;
            foreach (var f in sorted.Take(10))
            {
                if (TryReadEnvelope(f.FullName, out var env))
                {
                    pendingTasks.Add(new TaskSummary
                    {
                        Id = env.Id,
                        Source = env.Source.ToString().ToLowerInvariant(),
                        Priority = env.Priority.ToString().ToLowerInvariant(),
                        Timestamp = env.Timestamp,
                        BodyPreview = Preview(env.Body),
                    });
                }
            }
        }

        return new DashboardSnapshotPayload
        {
            CoreStatus = coreStatus,
            OwnerActivity = ownerActivity,
            Heartbeats = heartbeats,
            PendingTaskCount = pendingCount,
            RecentTasks = pendingTasks,
            CapturedAt = DateTimeOffset.UtcNow,
        };
    }

    private static bool TryReadEnvelope(string path, out TaskEnvelope envelope)
    {
        try
        {
            envelope = TaskFile.ParseFile(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or FormatException)
        {
            envelope = null!;
            return false;
        }
    }

    private static string Preview(string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return string.Empty;
        }
        var firstLine = body.Split('\n', 2)[0];
        return firstLine.Length > 80 ? firstLine[..80] : firstLine;
    }
}

/// <summary>Payload returned by <see cref="DashboardSnapshot.Capture"/>.</summary>
public sealed record DashboardSnapshotPayload
{
    /// <summary>Latest parsed <c>core-status.json</c>; <see langword="null"/> if the agent has not written one yet.</summary>
    [JsonPropertyName("core_status")] public CoreStatusPayload? CoreStatus { get; init; }

    /// <summary>Most recent owner-activity record; <see langword="null"/> if none.</summary>
    [JsonPropertyName("owner_activity")] public OwnerActivityPayload? OwnerActivity { get; init; }

    /// <summary>All currently-known heartbeat files under <c>state/cores/</c>.</summary>
    [JsonPropertyName("heartbeats")] public required IReadOnlyList<HeartbeatPayload> Heartbeats { get; init; }

    /// <summary>Number of pending task files (top-level only, excluding <c>processed/</c> and <c>archive/</c>).</summary>
    [JsonPropertyName("pending_task_count")] public required int PendingTaskCount { get; init; }

    /// <summary>Last ten tasks, newest first.</summary>
    [JsonPropertyName("recent_tasks")] public required IReadOnlyList<TaskSummary> RecentTasks { get; init; }

    /// <summary>UTC timestamp this snapshot was captured.</summary>
    [JsonPropertyName("captured_at")] public required DateTimeOffset CapturedAt { get; init; }
}

/// <summary>Short representation of a task for the dashboard's "recent" list.</summary>
public sealed record TaskSummary
{
    /// <summary>Task identifier.</summary>
    [JsonPropertyName("id")] public required string Id { get; init; }

    /// <summary>Originating source.</summary>
    [JsonPropertyName("source")] public required string Source { get; init; }

    /// <summary>Dispatcher priority.</summary>
    [JsonPropertyName("priority")] public required string Priority { get; init; }

    /// <summary>Submission timestamp.</summary>
    [JsonPropertyName("timestamp")] public required DateTimeOffset Timestamp { get; init; }

    /// <summary>First 80 chars of the body.</summary>
    [JsonPropertyName("body_preview")] public required string BodyPreview { get; init; }
}
