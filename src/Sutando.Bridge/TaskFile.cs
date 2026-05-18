using System.Globalization;
using System.Text;

namespace Sutando.Bridge;

/// <summary>
/// Parses and serialises the line-based <c>key: value</c> task-file format.
/// </summary>
/// <remarks>
/// <para>Format (UTF-8):</para>
/// <code>
/// id: task-chat-1747500000
/// timestamp: 2026-05-18T12:34:56Z
/// task: free-form body that
///   may span multiple lines until the next known key.
/// source: chat
/// channel_id: local-chat
/// user_id: chat-local
/// access_tier: owner
/// priority: normal
/// </code>
/// <para>
/// The body of <c>task:</c> continues across newlines until the parser encounters a
/// known-key prefix at column 0. Whitespace-only lines inside the body are preserved.
/// </para>
/// </remarks>
public static class TaskFile
{
    private static readonly HashSet<string> KnownKeys = new(StringComparer.Ordinal)
    {
        "id", "timestamp", "task", "source", "channel_id", "user_id",
        "access_tier", "priority", "timeout_ms", "dm_on_timeout", "reply_to_message_id",
    };

    /// <summary>Parse the textual contents of a task file.</summary>
    /// <param name="content">Raw file content; UTF-8 line endings tolerated (LF or CRLF).</param>
    /// <returns>A populated <see cref="TaskEnvelope"/>.</returns>
    /// <exception cref="FormatException">A required field is missing or invalid.</exception>
    public static TaskEnvelope Parse(string content)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var meta = new Dictionary<string, string>(StringComparer.Ordinal);

        // Tokenise into (key, body) pairs. A known key at column 0 starts a new field;
        // every subsequent line up to the next known key is appended to the current body.
        string? currentKey = null;
        var currentValue = new StringBuilder();

        foreach (var rawLine in SplitLines(content))
        {
            var line = rawLine;
            var colonIdx = line.IndexOf(':');
            string? candidateKey = null;
            if (colonIdx > 0)
            {
                var maybeKey = line[..colonIdx].Trim();
                if (KnownKeys.Contains(maybeKey) || maybeKey.StartsWith("meta.", StringComparison.Ordinal))
                {
                    candidateKey = maybeKey;
                }
            }

            if (candidateKey is not null)
            {
                FlushPair(fields, meta, currentKey, currentValue);
                currentKey = candidateKey;
                currentValue.Clear();
                var value = line[(colonIdx + 1)..].TrimStart();
                currentValue.Append(value);
            }
            else if (currentKey is not null)
            {
                currentValue.Append('\n').Append(line);
            }
            // Anything before the first known key is ignored (preamble / comments).
        }
        FlushPair(fields, meta, currentKey, currentValue);

        string Required(string key) => fields.TryGetValue(key, out var v)
            ? v
            : throw new FormatException($"task-file: required field '{key}' is missing");

        var id = Required("id").Trim();
        var timestamp = ParseTimestamp(Required("timestamp"));
        var body = fields.TryGetValue("task", out var taskBody) ? taskBody : string.Empty;
        var source = ParseSource(Required("source"));
        var channelId = Required("channel_id").Trim();
        var userId = Required("user_id").Trim();
        var tier = ParseTier(Required("access_tier"));
        var priority = fields.TryGetValue("priority", out var prio)
            ? ParsePriority(prio)
            : TaskPriorities.DefaultFor(source, tier);

        TimeSpan? timeout = null;
        if (fields.TryGetValue("timeout_ms", out var t) && long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
        {
            timeout = TimeSpan.FromMilliseconds(ms);
        }

        var dmOnTimeout = fields.TryGetValue("dm_on_timeout", out var dm) && bool.TryParse(dm, out var dmFlag) && dmFlag;
        fields.TryGetValue("reply_to_message_id", out var replyTo);

        return new TaskEnvelope
        {
            Id = id,
            Timestamp = timestamp,
            Body = body.TrimEnd('\r', '\n'),
            Source = source,
            ChannelId = channelId,
            UserId = userId,
            AccessTier = tier,
            Priority = priority,
            Timeout = timeout,
            DmOnTimeout = dmOnTimeout,
            ReplyToMessageId = string.IsNullOrWhiteSpace(replyTo) ? null : replyTo!.Trim(),
            Meta = meta,
        };
    }

    /// <summary>Read and parse a file from disk.</summary>
    public static TaskEnvelope ParseFile(string path) => Parse(File.ReadAllText(path));

    /// <summary>Serialise an envelope back to the canonical line-based format. Round-trips with <see cref="Parse"/>.</summary>
    public static string Serialize(TaskEnvelope envelope)
    {
        var sb = new StringBuilder(256);
        sb.Append("id: ").Append(envelope.Id).Append('\n');
        sb.Append("timestamp: ").Append(envelope.Timestamp.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("task: ").Append(envelope.Body).Append('\n');
        sb.Append("source: ").Append(SerializeSource(envelope.Source)).Append('\n');
        sb.Append("channel_id: ").Append(envelope.ChannelId).Append('\n');
        sb.Append("user_id: ").Append(envelope.UserId).Append('\n');
        sb.Append("access_tier: ").Append(SerializeTier(envelope.AccessTier)).Append('\n');
        sb.Append("priority: ").Append(SerializePriority(envelope.Priority)).Append('\n');
        if (envelope.Timeout is { } timeout)
        {
            sb.Append("timeout_ms: ").Append(((long)timeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
        if (envelope.DmOnTimeout)
        {
            sb.Append("dm_on_timeout: true\n");
        }
        if (!string.IsNullOrEmpty(envelope.ReplyToMessageId))
        {
            sb.Append("reply_to_message_id: ").Append(envelope.ReplyToMessageId).Append('\n');
        }
        foreach (var (k, v) in envelope.Meta.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            sb.Append("meta.").Append(k).Append(": ").Append(v).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Write the canonical form of an envelope to <c>&lt;dir&gt;/&lt;id&gt;.txt</c>.</summary>
    /// <returns>The absolute path that was written.</returns>
    public static string Write(string directory, TaskEnvelope envelope)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, envelope.Id + ".txt");
        File.WriteAllText(path, Serialize(envelope));
        return path;
    }

    private static void FlushPair(IDictionary<string, string> fields, IDictionary<string, string> meta, string? key, StringBuilder value)
    {
        if (key is null)
        {
            return;
        }
        var raw = value.ToString();
        if (key.StartsWith("meta.", StringComparison.Ordinal))
        {
            meta[key[5..]] = raw.TrimEnd('\r', '\n').Trim();
        }
        else
        {
            // task body keeps its inner newlines; everything else trims.
            fields[key] = key == "task" ? raw : raw.Trim();
        }
    }

    private static IEnumerable<string> SplitLines(string content)
    {
        var start = 0;
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] != '\n')
            {
                continue;
            }
            var end = i;
            if (end > start && content[end - 1] == '\r')
            {
                end--;
            }
            yield return content[start..end];
            start = i + 1;
        }
        if (start < content.Length)
        {
            yield return content[start..];
        }
    }

    private static DateTimeOffset ParseTimestamp(string raw)
    {
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var ts))
        {
            return ts;
        }
        throw new FormatException($"task-file: timestamp '{raw}' is not a valid ISO-8601 datetime");
    }

    private static TaskSource ParseSource(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "voice" => TaskSource.Voice,
        "chat" => TaskSource.Chat,
        "telegram" => TaskSource.Telegram,
        "discord" => TaskSource.Discord,
        "phone" => TaskSource.Phone,
        "api" => TaskSource.Api,
        "cron" => TaskSource.Cron,
        "health" => TaskSource.Health,
        "proactive" => TaskSource.Proactive,
        _ => throw new FormatException($"task-file: unknown source '{raw}'"),
    };

    private static AccessTier ParseTier(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "owner" => AccessTier.Owner,
        "verified" => AccessTier.Verified,
        "team" => AccessTier.Team,
        "other" => AccessTier.Other,
        "unverified" => AccessTier.Unverified,
        _ => throw new FormatException($"task-file: unknown access_tier '{raw}'"),
    };

    private static TaskPriority ParsePriority(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "urgent" => TaskPriority.Urgent,
        "normal" => TaskPriority.Normal,
        "low" => TaskPriority.Low,
        _ => throw new FormatException($"task-file: unknown priority '{raw}'"),
    };

    private static string SerializeSource(TaskSource s) => s switch
    {
        TaskSource.Voice => "voice",
        TaskSource.Chat => "chat",
        TaskSource.Telegram => "telegram",
        TaskSource.Discord => "discord",
        TaskSource.Phone => "phone",
        TaskSource.Api => "api",
        TaskSource.Cron => "cron",
        TaskSource.Health => "health",
        TaskSource.Proactive => "proactive",
        _ => throw new ArgumentOutOfRangeException(nameof(s), s, null),
    };

    private static string SerializeTier(AccessTier t) => t switch
    {
        AccessTier.Owner => "owner",
        AccessTier.Verified => "verified",
        AccessTier.Team => "team",
        AccessTier.Other => "other",
        AccessTier.Unverified => "unverified",
        _ => throw new ArgumentOutOfRangeException(nameof(t), t, null),
    };

    private static string SerializePriority(TaskPriority p) => p switch
    {
        TaskPriority.Urgent => "urgent",
        TaskPriority.Normal => "normal",
        TaskPriority.Low => "low",
        _ => throw new ArgumentOutOfRangeException(nameof(p), p, null),
    };
}
