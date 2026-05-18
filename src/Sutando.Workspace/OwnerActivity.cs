using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sutando.Workspace;

/// <summary>
/// Atomic writer for <c>&lt;workspace&gt;/state/last-owner-activity.json</c> — used by the
/// proactive loop's "don't interrupt the human" rule. Every channel writes here when the
/// owner is active.
/// </summary>
public sealed class OwnerActivity
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly WorkspaceDirectory _workspace;

    private string ActivityFilePath => Path.Combine(_workspace.State.FullName, "last-owner-activity.json");

    /// <param name="workspace">Resolved workspace.</param>
    public OwnerActivity(WorkspaceDirectory workspace) => _workspace = workspace;

    /// <summary>Record that the owner was active on the given channel just now. Atomic.</summary>
    /// <param name="channel">Originating channel (<c>voice</c>, <c>chat</c>, <c>telegram</c>, <c>discord</c>, <c>phone</c>).</param>
    /// <param name="summary">First ~80 chars of the user's utterance — used for human review only.</param>
    public void Record(string channel, string summary)
    {
        var payload = new OwnerActivityPayload
        {
            Ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Channel = channel,
            Summary = summary.Length > 80 ? summary[..80] : summary,
        };
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        WorkspaceDirectory.AtomicWrite(ActivityFilePath, json);
    }

    /// <summary>Read the most recent owner-activity payload, if any.</summary>
    public OwnerActivityPayload? Read()
    {
        var file = new FileInfo(ActivityFilePath);
        if (!file.Exists)
        {
            return null;
        }
        try
        {
            using var stream = file.OpenRead();
            return JsonSerializer.Deserialize<OwnerActivityPayload>(stream, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _ = ex;
            return null;
        }
    }
}

/// <summary>Schema for <c>last-owner-activity.json</c>.</summary>
public sealed record OwnerActivityPayload
{
    /// <summary>Unix epoch seconds when the activity was recorded.</summary>
    [JsonPropertyName("ts")] public long Ts { get; init; }

    /// <summary>Channel name (<c>voice</c>, <c>chat</c>, etc).</summary>
    [JsonPropertyName("channel")] public string Channel { get; init; } = string.Empty;

    /// <summary>First ~80 chars of the user's most recent utterance on the channel.</summary>
    [JsonPropertyName("summary")] public string Summary { get; init; } = string.Empty;
}
