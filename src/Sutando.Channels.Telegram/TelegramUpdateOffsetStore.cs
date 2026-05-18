using System.Text.Json;
using System.Text.Json.Serialization;
using Sutando.Workspace;

namespace Sutando.Channels.Telegram;

/// <summary>
/// Atomic JSON store for the Telegram long-poll offset. Persisted under the workspace so a
/// crashed / restarted bot doesn't reprocess history. Lives next to other channel state files
/// (e.g. <c>state/last-owner-activity.json</c>) so backups stay coherent.
/// </summary>
internal sealed class TelegramUpdateOffsetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;

    /// <param name="workspace">Resolved workspace — supplies the root directory.</param>
    /// <param name="relativePath">Workspace-relative path of the JSON file.</param>
    public TelegramUpdateOffsetStore(WorkspaceDirectory workspace, string relativePath)
    {
        // Treat the configured value as workspace-relative; absolute paths are honoured too.
        _path = Path.IsPathRooted(relativePath)
            ? relativePath
            : Path.Combine(workspace.Root.FullName, relativePath);
    }

    /// <summary>Read the persisted last-update id, or <see langword="null"/> if no file exists / parse fails.</summary>
    public int? Read()
    {
        if (!File.Exists(_path))
        {
            return null;
        }
        try
        {
            using var stream = File.OpenRead(_path);
            var payload = JsonSerializer.Deserialize<OffsetPayload>(stream, JsonOptions);
            return payload?.LastUpdateId;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _ = ex;
            return null;
        }
    }

    /// <summary>Atomically persist the last-update id.</summary>
    public void Write(int lastUpdateId)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var json = JsonSerializer.Serialize(new OffsetPayload { LastUpdateId = lastUpdateId }, JsonOptions);
        WorkspaceDirectory.AtomicWrite(_path, json);
    }

    /// <summary>Schema for the offset persistence file.</summary>
    private sealed record OffsetPayload
    {
        /// <summary>The most recently-processed Telegram update id.</summary>
        [JsonPropertyName("last_update_id")] public int LastUpdateId { get; init; }
    }
}
