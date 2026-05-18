using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sutando.Bridge;
using Sutando.Workspace;

namespace Sutando.Phone;

/// <summary>
/// Persists per-call metadata to <c>&lt;workspace&gt;/data/phone/&lt;call-sid&gt;.json</c>.
/// Each file captures the call's lifecycle so the dashboard SignalR hub (future work) can
/// subscribe and surface the events.
/// </summary>
/// <remarks>
/// <para>
/// Writes are atomic-ish — we serialise to a temp file in the target directory and
/// <c>File.Move</c> over the final path. That avoids partial-file readers in the dashboard,
/// at the cost of one extra inode per write. <see cref="WorkspaceDirectory.Data"/> creates
/// the data root on first access.
/// </para>
/// <para>
/// The file format is snake_case JSON to align with the rest of the bridge contract
/// (<c>core-status.json</c>, <c>state/last-owner-activity.json</c>). Tool calls are appended
/// to the record as they happen; the record is rewritten on every change so a partial
/// read-mid-write is harmless.
/// </para>
/// </remarks>
public sealed class CallMetadataStore
{
    private static readonly JsonSerializerOptions WriteOptions = BuildWriteOptions();

    private readonly WorkspaceDirectory _workspace;
    private readonly ILogger<CallMetadataStore> _logger;

    /// <summary>Creates a new store.</summary>
    /// <param name="workspace">Workspace directory — <c>data/phone/</c> is created on demand under it.</param>
    /// <param name="logger">Optional logger.</param>
    public CallMetadataStore(WorkspaceDirectory workspace, ILogger<CallMetadataStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _workspace = workspace;
        _logger = logger ?? NullLogger<CallMetadataStore>.Instance;
    }

    /// <summary>Returns the on-disk path for the given call sid (the file may not yet exist).</summary>
    /// <param name="callSid">The Twilio call SID, sanitised by the caller.</param>
    /// <returns>An absolute path under <c>&lt;workspace&gt;/data/phone/</c>.</returns>
    public string PathFor(string callSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callSid);
        var root = _workspace.Data.CreateSubdirectory("phone");
        return Path.Combine(root.FullName, callSid + ".json");
    }

    /// <summary>
    /// Write the metadata record atomically. Existing files are overwritten so callers can
    /// rewrite the record on every state transition without worrying about stale partial reads.
    /// </summary>
    /// <param name="record">Record to persist; <see cref="CallMetadataRecord.CallSid"/> determines the file name.</param>
    public void Save(CallMetadataRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var path = PathFor(record.CallSid);
        // Temp file in the SAME directory so the rename is atomic on Windows (cross-volume
        // moves get demoted to copy-then-delete). Use a randomised name to avoid collisions
        // when multiple in-flight writes race on the same call sid.
        var tempPath = Path.Combine(
            Path.GetDirectoryName(path)!,
            $".{record.CallSid}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, record, WriteOptions);
            }
            File.Move(tempPath, path, overwrite: true);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to persist call metadata for {CallSid}.", record.CallSid);
            // Best-effort cleanup — leave the temp file behind only if we crashed mid-write and
            // the move never landed; the next save attempt cleans up via CreateNew failing.
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // already logged; swallow
            }
        }
    }

    private static JsonSerializerOptions BuildWriteOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }
}

/// <summary>
/// On-disk metadata captured per call. Field order matches a human-readable dashboard
/// view: identity → access → timing → tool activity.
/// </summary>
/// <param name="CallSid">Twilio's unique call identifier — primary key.</param>
/// <param name="From">The remote caller-id (E.164 or "anonymous").</param>
/// <param name="To">The number dialled (the Sutando Twilio number for inbound; the contact for outbound).</param>
/// <param name="Direction">"inbound" or "outbound".</param>
/// <param name="Tier">Resolved <see cref="AccessTier"/> after STIR rules applied.</param>
/// <param name="StirAttestation">Raw value of the Twilio <c>StirVerstat</c> form parameter.</param>
/// <param name="TierDowngraded">True iff STIR forced a drop from Owner to Verified.</param>
/// <param name="StartedAt">UTC start time.</param>
/// <param name="EndedAt">UTC end time. Null while the call is still active.</param>
/// <param name="DurationMs">Duration in milliseconds. Null while the call is still active.</param>
/// <param name="ToolCalls">List of tool invocations the model issued during the call.</param>
public sealed record CallMetadataRecord(
    string CallSid,
    string From,
    string To,
    string Direction,
    AccessTier Tier,
    string? StirAttestation,
    bool TierDowngraded,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt = null,
    long? DurationMs = null,
    IReadOnlyList<CallToolCall>? ToolCalls = null);

/// <summary>A single tool invocation captured during a call.</summary>
/// <param name="Name">Tool name (matches <c>RealtimeFunctionCall.Name</c>).</param>
/// <param name="At">UTC time the call was issued.</param>
/// <param name="ArgumentsPreview">First 240 chars of the JSON arguments. Truncated to keep the file bounded.</param>
public sealed record CallToolCall(string Name, DateTimeOffset At, string ArgumentsPreview);
