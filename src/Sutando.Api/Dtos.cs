using System.Text.Json.Serialization;

namespace Sutando.Api;

/// <summary>Request body for <c>POST /tasks</c>.</summary>
public sealed record SubmitTaskRequest
{
    /// <summary>The free-form task body (the work to perform). Required.</summary>
    [JsonPropertyName("body")] public string? Body { get; init; }

    /// <summary>Priority hint — <c>urgent</c>, <c>normal</c> (default), or <c>low</c>.</summary>
    [JsonPropertyName("priority")] public string? Priority { get; init; }

    /// <summary>Per-task wall-clock budget in milliseconds. <see langword="null"/> uses the executor default.</summary>
    [JsonPropertyName("timeout_ms")] public long? TimeoutMs { get; init; }

    /// <summary>Source-specific channel identifier; defaults to <c>api-default</c>.</summary>
    [JsonPropertyName("channel_id")] public string? ChannelId { get; init; }

    /// <summary>Originating user identifier; defaults to <c>api-client</c>.</summary>
    [JsonPropertyName("user_id")] public string? UserId { get; init; }
}

/// <summary>Response body for <c>POST /tasks</c>.</summary>
public sealed record SubmitTaskResponse
{
    /// <summary>Generated task identifier.</summary>
    [JsonPropertyName("id")] public required string Id { get; init; }

    /// <summary>Absolute path of the task file on disk — useful for debugging.</summary>
    [JsonPropertyName("path")] public required string Path { get; init; }
}

/// <summary>One entry in the <c>GET /tasks</c> manifest array.</summary>
public sealed record TaskManifestEntry
{
    /// <summary>Task identifier.</summary>
    [JsonPropertyName("id")] public required string Id { get; init; }

    /// <summary>Originating source (e.g. <c>api</c>, <c>chat</c>).</summary>
    [JsonPropertyName("source")] public required string Source { get; init; }

    /// <summary>Dispatcher priority.</summary>
    [JsonPropertyName("priority")] public required string Priority { get; init; }

    /// <summary>ISO-8601 timestamp the task was created.</summary>
    [JsonPropertyName("timestamp")] public required DateTimeOffset Timestamp { get; init; }

    /// <summary>First 80 chars of the task body — a one-line preview.</summary>
    [JsonPropertyName("body_preview")] public required string BodyPreview { get; init; }
}

/// <summary>Response body for <c>GET /tasks/{id}</c>.</summary>
public sealed record TaskDetailResponse
{
    /// <summary>The id that was looked up.</summary>
    [JsonPropertyName("id")] public required string Id { get; init; }

    /// <summary><c>pending</c> when no result has been written, <c>completed</c> otherwise.</summary>
    [JsonPropertyName("status")] public required string Status { get; init; }

    /// <summary>Task manifest — populated when the task file is found.</summary>
    [JsonPropertyName("task")] public TaskManifestEntry? Task { get; init; }

    /// <summary>Raw result body if available — caller is free to feed this to <c>ResultBody.Parse</c>.</summary>
    [JsonPropertyName("result")] public string? Result { get; init; }
}

/// <summary>Response body for <c>GET /healthz</c>.</summary>
public sealed record HealthResponse
{
    /// <summary>Always true when the endpoint responded.</summary>
    [JsonPropertyName("ok")] public required bool Ok { get; init; }

    /// <summary>Process uptime in seconds since the API started.</summary>
    [JsonPropertyName("uptime_seconds")] public required long UptimeSeconds { get; init; }

    /// <summary>Absolute path of the resolved workspace.</summary>
    [JsonPropertyName("workspace")] public required string Workspace { get; init; }
}

/// <summary>Generic JSON error shape returned by the API.</summary>
public sealed record ErrorResponse
{
    /// <summary>Short, lower-case error description.</summary>
    [JsonPropertyName("error")] public required string Error { get; init; }
}
