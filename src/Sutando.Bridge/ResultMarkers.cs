namespace Sutando.Bridge;

/// <summary>
/// Result-body protocol markers. The very first non-whitespace tokens in a result body
/// can be these markers — they change how the originating channel handles delivery.
/// </summary>
/// <remarks>
/// Direct port of the marker grammar from upstream <c>src/task-bridge.ts</c>.
/// </remarks>
public static class ResultMarkers
{
    /// <summary>Prefix marking that this task was deduped into another; e.g. <c>[deduped: task-123]</c>.</summary>
    public const string DedupedPrefix = "[deduped:";

    /// <summary>Indicates the channel should archive without delivering anything.</summary>
    public const string NoSend = "[no-send]";

    /// <summary>Indicates the result was already delivered via another path.</summary>
    public const string Replied = "[REPLIED]";

    /// <summary>File-attachment markers — each may appear on its own line at the head of the body.</summary>
    public static readonly string[] FileAttachmentPrefixes = ["[file:", "[send:", "[attach:"];
}

/// <summary>
/// Parsed view over a result-body's marker prefix. Built by <see cref="ResultBody.Parse"/>.
/// </summary>
public sealed record ResultBody
{
    /// <summary>The result text with markers stripped from the head.</summary>
    public required string Text { get; init; }

    /// <summary>Target task id if this result is a deduped pointer; <see langword="null"/> otherwise.</summary>
    public string? DedupedTo { get; init; }

    /// <summary>True if the <c>[no-send]</c> marker was present.</summary>
    public bool NoSend { get; init; }

    /// <summary>True if the <c>[REPLIED]</c> marker was present.</summary>
    public bool AlreadyReplied { get; init; }

    /// <summary>Absolute paths of any <c>[file:|send:|attach:]</c> attachments.</summary>
    public IReadOnlyList<string> Attachments { get; init; } = [];

    /// <summary>True if any of <see cref="NoSend"/>, <see cref="AlreadyReplied"/>, or <see cref="DedupedTo"/> tell the channel to skip delivery.</summary>
    public bool ShouldSkipDelivery => NoSend || AlreadyReplied || DedupedTo is not null;

    /// <summary>Parse a raw result body, extracting markers from the head.</summary>
    public static ResultBody Parse(string raw)
    {
        var text = raw ?? string.Empty;
        string? dedupedTo = null;
        var noSend = false;
        var replied = false;
        var attachments = new List<string>();

        while (true)
        {
            var trimmed = text.TrimStart();
            if (trimmed.Length == 0)
            {
                text = trimmed;
                break;
            }

            // [deduped: task-...]
            if (trimmed.StartsWith(ResultMarkers.DedupedPrefix, StringComparison.Ordinal))
            {
                var close = trimmed.IndexOf(']');
                if (close < 0) { break; }
                var inner = trimmed[(ResultMarkers.DedupedPrefix.Length)..close].Trim();
                dedupedTo = inner;
                text = AdvancePastMarker(trimmed, close);
                continue;
            }

            if (trimmed.StartsWith(ResultMarkers.NoSend, StringComparison.Ordinal))
            {
                noSend = true;
                text = AdvancePastMarker(trimmed, ResultMarkers.NoSend.Length - 1);
                continue;
            }

            if (trimmed.StartsWith(ResultMarkers.Replied, StringComparison.Ordinal))
            {
                replied = true;
                text = AdvancePastMarker(trimmed, ResultMarkers.Replied.Length - 1);
                continue;
            }

            var matchedAttachment = false;
            foreach (var prefix in ResultMarkers.FileAttachmentPrefixes)
            {
                if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
                {
                    var close = trimmed.IndexOf(']');
                    if (close < 0)
                    {
                        break;
                    }
                    var path = trimmed[(prefix.Length)..close].Trim();
                    if (path.Length > 0)
                    {
                        attachments.Add(path);
                    }
                    text = AdvancePastMarker(trimmed, close);
                    matchedAttachment = true;
                    break;
                }
            }
            if (matchedAttachment)
            {
                continue;
            }

            // No leading marker recognised — the remainder is the human-readable body.
            text = trimmed;
            break;
        }

        return new ResultBody
        {
            Text = text,
            DedupedTo = dedupedTo,
            NoSend = noSend,
            AlreadyReplied = replied,
            Attachments = attachments,
        };
    }

    private static string AdvancePastMarker(string trimmed, int closeIdx)
    {
        var rest = trimmed[(closeIdx + 1)..];
        // Eat a trailing newline so the next marker / body lines up cleanly.
        if (rest.Length > 0 && rest[0] == '\r') { rest = rest[1..]; }
        if (rest.Length > 0 && rest[0] == '\n') { rest = rest[1..]; }
        return rest;
    }
}
