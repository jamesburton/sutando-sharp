using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using YamlDotNet.Serialization;

namespace Sutando.Notes;

/// <summary>
/// Reads and writes the leading <c>---\n…\n---\n</c> YAML frontmatter block of a markdown note.
/// </summary>
/// <remarks>
/// <para>
/// On the read path, the parser splits the file on the second <c>---</c> fence, parses the
/// fenced block as YAML via <see cref="YamlDotNet.Serialization.Deserializer"/>, and projects
/// the result into <see cref="IReadOnlyDictionary{TKey, TValue}"/> with <see cref="string"/>
/// keys. Scalars come through as their boxed CLR types (<see cref="string"/>, <see cref="long"/>,
/// <see cref="double"/>, <see cref="bool"/>); nested maps and sequences are projected
/// recursively so callers can walk arbitrary frontmatter without losing data.
/// </para>
/// <para>
/// On the write path, the dictionary is serialised back as YAML between two <c>---</c> fences
/// followed by the body. The block is omitted entirely when the frontmatter is empty so we
/// don't pollute notes that legitimately have no metadata.
/// </para>
/// </remarks>
public static class NoteFrontmatterParser
{
    /// <summary>Marker line used to delimit the YAML frontmatter block.</summary>
    public const string Fence = "---";

    private static readonly IDeserializer Deserializer = new DeserializerBuilder().Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .DisableAliases()
        .Build();

    /// <summary>
    /// Parse a complete note file into its frontmatter map and body string. If the file does
    /// not start with a frontmatter fence, returns an empty map and the entire file as the
    /// body.
    /// </summary>
    /// <param name="content">Full file content.</param>
    /// <returns>A tuple of (frontmatter map, body markdown).</returns>
    public static (IReadOnlyDictionary<string, object?> Frontmatter, string Body) Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (!TrySplit(content, out var yaml, out var body))
        {
            // Either no fences or only an opening fence — treat the whole file as body so we
            // never silently drop content. This keeps the parser non-destructive on files that
            // happen to start with --- for some other reason (e.g. a horizontal rule mid-doc).
            return (EmptyMap, content);
        }

        if (string.IsNullOrWhiteSpace(yaml))
        {
            return (EmptyMap, body);
        }

        object? parsed;
        try
        {
            parsed = Deserializer.Deserialize<object?>(new StringReader(yaml));
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            // Surface malformed YAML as FormatException for parity with SkillManifest.Parse —
            // call sites can catch a single exception type for "this file is broken".
            throw new FormatException($"note frontmatter: {ex.Message}", ex);
        }

        if (parsed is null)
        {
            return (EmptyMap, body);
        }

        if (parsed is not IDictionary<object, object?> rawMap)
        {
            throw new FormatException(
                $"note frontmatter: expected a YAML mapping at the top level, got {parsed.GetType().Name}");
        }

        return (NormaliseMap(rawMap), body);
    }

    /// <summary>
    /// Serialise <paramref name="frontmatter"/> + <paramref name="body"/> into the on-disk note
    /// format. When the frontmatter is empty, the fences are omitted entirely and only the body
    /// is returned.
    /// </summary>
    /// <param name="frontmatter">Frontmatter map. Keys are written in insertion order.</param>
    /// <param name="body">Markdown body. A trailing newline is added when absent.</param>
    /// <returns>Complete file content, ready to write.</returns>
    public static string Compose(IReadOnlyDictionary<string, object?> frontmatter, string body)
    {
        ArgumentNullException.ThrowIfNull(frontmatter);
        ArgumentNullException.ThrowIfNull(body);

        var trimmedBody = body.EndsWith('\n') ? body : body + "\n";

        if (frontmatter.Count == 0)
        {
            return trimmedBody;
        }

        // YamlDotNet's serializer expects an IDictionary<object, object> for consistent output.
        // We project our string-keyed map back into that shape so the round-trip preserves the
        // structure callers gave us.
        var raw = DenormaliseMap(frontmatter);
        var yaml = Serializer.Serialize(raw);

        var sb = new StringBuilder();
        sb.Append(Fence).Append('\n');
        sb.Append(yaml);
        // YamlDotNet always emits a trailing newline on its output; defensively ensure exactly
        // one before the closing fence so we don't produce ---\n---\n on an empty document or
        // skip the separator on a non-terminated one.
        if (!yaml.EndsWith('\n'))
        {
            sb.Append('\n');
        }
        sb.Append(Fence).Append('\n');
        sb.Append(trimmedBody);
        return sb.ToString();
    }

    /// <summary>
    /// Extract a list of tag strings from a frontmatter map's <c>tags:</c> entry. Returns an
    /// empty list when the key is absent or the value is not a sequence.
    /// </summary>
    /// <param name="frontmatter">The frontmatter map.</param>
    public static IReadOnlyList<string> ExtractTags(IReadOnlyDictionary<string, object?> frontmatter)
    {
        ArgumentNullException.ThrowIfNull(frontmatter);

        if (!frontmatter.TryGetValue("tags", out var raw) || raw is null)
        {
            return [];
        }

        if (raw is IReadOnlyList<object?> list)
        {
            return [.. list.Select(static t => t?.ToString() ?? string.Empty).Where(static s => s.Length > 0)];
        }

        // A scalar string under `tags:` is unusual but not invalid — surface it as a single-tag
        // list rather than silently dropping it.
        if (raw is string single && !string.IsNullOrWhiteSpace(single))
        {
            return [single];
        }

        return [];
    }

    private static readonly IReadOnlyDictionary<string, object?> EmptyMap =
        new Dictionary<string, object?>(0);

    private static bool TrySplit(string content, out string yaml, out string body)
    {
        // A frontmatter block must begin on the very first line with the fence followed by a
        // newline (CRLF tolerated). The closing fence is the next bare --- line on its own.
        if (!content.StartsWith(Fence + "\n", StringComparison.Ordinal)
            && !content.StartsWith(Fence + "\r\n", StringComparison.Ordinal))
        {
            yaml = string.Empty;
            body = content;
            return false;
        }

        var afterOpen = content.IndexOf('\n') + 1;

        // Scan for a line that is exactly "---" (CRLF tolerated). Using IndexOf in a loop
        // rather than Regex keeps allocations low for large notes.
        var cursor = afterOpen;
        while (cursor < content.Length)
        {
            var lineEnd = content.IndexOf('\n', cursor);
            var lineEndExclusive = lineEnd < 0 ? content.Length : lineEnd;
            var line = content[cursor..lineEndExclusive];
            if (line.EndsWith('\r'))
            {
                line = line[..^1];
            }

            if (line == Fence)
            {
                yaml = content[afterOpen..cursor];
                var bodyStart = lineEnd < 0 ? content.Length : lineEnd + 1;
                body = content[bodyStart..];
                return true;
            }

            if (lineEnd < 0)
            {
                break;
            }
            cursor = lineEnd + 1;
        }

        yaml = string.Empty;
        body = content;
        return false;
    }

    private static IReadOnlyDictionary<string, object?> NormaliseMap(IDictionary<object, object?> raw)
    {
        var result = new Dictionary<string, object?>(raw.Count, StringComparer.Ordinal);
        foreach (var (k, v) in raw)
        {
            var key = k?.ToString() ?? string.Empty;
            result[key] = NormaliseValue(v);
        }
        return result;
    }

    private static object? NormaliseValue(object? value) => value switch
    {
        null => null,
        IDictionary<object, object?> nested => NormaliseMap(nested),
        IList<object?> list => NormaliseList(list),
        // Scalars come through as string in the default YamlDotNet config; coerce booleans and
        // ints to their CLR types so equality checks in NoteQuery.FrontmatterFilters work
        // intuitively without callers having to .ToString() on both sides.
        string scalar => CoerceScalar(scalar),
        _ => value,
    };

    private static IReadOnlyList<object?> NormaliseList(IList<object?> list)
    {
        var result = new List<object?>(list.Count);
        foreach (var item in list)
        {
            result.Add(NormaliseValue(item));
        }
        return result;
    }

    private static object CoerceScalar(string s)
    {
        if (bool.TryParse(s, out var b))
        {
            return b;
        }
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
        {
            return i;
        }
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            // Reject leading-zero strings like "007" — they're more likely intentional than numeric.
            && !(s.Length > 1 && s.StartsWith('0') && !s.StartsWith("0.", StringComparison.Ordinal)))
        {
            return d;
        }
        return s;
    }

    private static IDictionary<object, object?> DenormaliseMap(IReadOnlyDictionary<string, object?> map)
    {
        // YamlDotNet writes Dictionary<object,object> entries with their literal types — strings
        // get quoted only when necessary, ints / bools come out unquoted. Boxing into
        // object/object keeps that path working.
        var dict = new Dictionary<object, object?>(map.Count);
        foreach (var (k, v) in map)
        {
            dict[k] = DenormaliseValue(v);
        }
        return dict;
    }

    private static object? DenormaliseValue(object? value) => value switch
    {
        null => null,
        IReadOnlyDictionary<string, object?> nested => DenormaliseMap(nested),
        IReadOnlyList<object?> list => list.Select(DenormaliseValue).ToList(),
        _ => value,
    };
}
