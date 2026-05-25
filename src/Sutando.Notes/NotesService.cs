using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Sutando.Notes;

/// <summary>
/// High-level note operations layered on top of an <see cref="INoteStore"/>. Owns the semantics
/// for tag normalisation, frontmatter timestamps, search predicates, and snippet extraction so
/// every consumer of the note library (CLI, voice skills, future agents) sees the same
/// behaviour.
/// </summary>
/// <remarks>
/// <para>
/// The service is intentionally stateless — it holds a reference to the store and nothing else.
/// Callers concerned with caching, throttling, or fan-out should layer those concerns on top.
/// </para>
/// <para>
/// Tag operations canonicalise to <c>lower-case</c> when matching and de-duplicating, but
/// preserve the casing the author chose when writing back to disk on the very first time a tag
/// is added. Subsequent identical-casing additions become no-ops; differing-casing additions
/// also become no-ops so we don't accumulate duplicate visual variants.
/// </para>
/// </remarks>
public sealed class NotesService
{
    private const string CreatedKey = "created";
    private const string ModifiedKey = "modified";
    private const string TagsKey = "tags";

    private const int SnippetRadius = 80;

    private readonly INoteStore _store;
    private readonly Func<DateTimeOffset> _clock;

    /// <param name="store">Backing store. Required.</param>
    /// <param name="clock">Optional clock for timestamp stamping; defaults to <see cref="DateTimeOffset.UtcNow"/>. Test seam.</param>
    public NotesService(INoteStore store, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Find notes matching <paramref name="query"/>. Returns a materialised list ordered by
    /// most-recently-modified first.
    /// </summary>
    /// <param name="query">Search predicate. <see langword="null"/> is treated as "match all".</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<IReadOnlyList<NoteSearchHit>> SearchAsync(NoteQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var hits = new List<NoteSearchHit>();
        await foreach (var note in _store.ListAsync(ct).ConfigureAwait(false))
        {
            if (!Matches(note, query, out var snippet))
            {
                continue;
            }
            hits.Add(new NoteSearchHit { Note = note, Snippet = snippet });
        }

        hits.Sort(static (a, b) => b.Note.Modified.CompareTo(a.Note.Modified));

        if (query.Limit is int limit && limit >= 0 && hits.Count > limit)
        {
            return hits.GetRange(0, limit);
        }
        return hits;
    }

    /// <summary>
    /// Create a new note at <paramref name="relativePath"/>. Fails when a note already exists at
    /// that path. Stamps the frontmatter <c>created</c> + <c>modified</c> keys to "now".
    /// </summary>
    /// <param name="relativePath">Store-relative target path (e.g. <c>ideas/foo.md</c>).</param>
    /// <param name="body">Markdown body.</param>
    /// <param name="frontmatter">Optional initial frontmatter (caller's keys are preserved; created/modified are stamped on top).</param>
    /// <param name="tags">Optional initial tags. Merged into frontmatter's <c>tags:</c> list.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The freshly-written note as round-tripped through the store.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the note already exists.</exception>
    public async Task<Note> CreateAsync(
        string relativePath,
        string body,
        IDictionary<string, object?>? frontmatter = null,
        IEnumerable<string>? tags = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentNullException.ThrowIfNull(body);

        if (await _store.ReadAsync(relativePath, ct).ConfigureAwait(false) is not null)
        {
            throw new InvalidOperationException($"note already exists at '{relativePath}'.");
        }

        var fm = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (frontmatter is not null)
        {
            foreach (var (k, v) in frontmatter)
            {
                fm[k] = v;
            }
        }

        if (tags is not null)
        {
            var canonical = MergeTagsAsStrings(GetTagList(fm), tags);
            // Store the canonical list as object?-typed entries so the YAML serializer keeps the
            // pass-through map shape consistent with frontmatter loaded from disk.
            fm[TagsKey] = canonical.Cast<object?>().ToList();
        }

        var now = _clock();
        fm[CreatedKey] = now.ToString("o");
        fm[ModifiedKey] = now.ToString("o");

        var note = new Note
        {
            Path = relativePath,
            Frontmatter = fm,
            Tags = NoteFrontmatterParser.ExtractTags(fm),
            Body = body,
            Created = now,
            Modified = now,
        };

        await _store.WriteAsync(note, ct).ConfigureAwait(false);
        return (await _store.ReadAsync(relativePath, ct).ConfigureAwait(false))!;
    }

    /// <summary>
    /// Replace the body of an existing note while preserving frontmatter. Stamps <c>modified</c>
    /// to "now". Throws when the note does not exist.
    /// </summary>
    public async Task<Note> UpdateBodyAsync(string relativePath, string body, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentNullException.ThrowIfNull(body);

        var existing = await _store.ReadAsync(relativePath, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"note '{relativePath}' does not exist.");

        var fm = CopyMutableFrontmatter(existing.Frontmatter);
        var now = _clock();
        fm[ModifiedKey] = now.ToString("o");

        var updated = new Note
        {
            Path = relativePath,
            Frontmatter = fm,
            Tags = NoteFrontmatterParser.ExtractTags(fm),
            Body = body,
            Created = existing.Created,
            Modified = now,
        };

        await _store.WriteAsync(updated, ct).ConfigureAwait(false);
        return (await _store.ReadAsync(relativePath, ct).ConfigureAwait(false))!;
    }

    /// <summary>
    /// Add <paramref name="tag"/> to the note's <c>tags:</c> list. Case-insensitive dedupe;
    /// preserves the casing of the first add. No-op when the tag is already present (any case).
    /// </summary>
    public Task<Note> AddTagAsync(string relativePath, string tag, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        return MutateTagsAsync(relativePath, tags => MergeTagsAsStrings(tags, [tag]), ct);
    }

    /// <summary>
    /// Remove <paramref name="tag"/> from the note's <c>tags:</c> list. Case-insensitive match.
    /// No-op when the tag is absent.
    /// </summary>
    public Task<Note> RemoveTagAsync(string relativePath, string tag, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        return MutateTagsAsync(relativePath, tags =>
        {
            var result = new List<string>(tags.Count);
            foreach (var existing in tags)
            {
                if (!string.Equals(existing, tag, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(existing);
                }
            }
            return result;
        }, ct);
    }

    private async Task<Note> MutateTagsAsync(string relativePath, Func<IReadOnlyList<string>, IReadOnlyList<string>> mutator, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var existing = await _store.ReadAsync(relativePath, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"note '{relativePath}' does not exist.");

        var fm = CopyMutableFrontmatter(existing.Frontmatter);
        var newTags = mutator(existing.Tags);
        fm[TagsKey] = newTags.Cast<object?>().ToList();

        var now = _clock();
        fm[ModifiedKey] = now.ToString("o");

        var updated = new Note
        {
            Path = relativePath,
            Frontmatter = fm,
            Tags = newTags,
            Body = existing.Body,
            Created = existing.Created,
            Modified = now,
        };

        await _store.WriteAsync(updated, ct).ConfigureAwait(false);
        return (await _store.ReadAsync(relativePath, ct).ConfigureAwait(false))!;
    }

    private static Dictionary<string, object?> CopyMutableFrontmatter(IReadOnlyDictionary<string, object?> fm)
    {
        var copy = new Dictionary<string, object?>(fm.Count, StringComparer.Ordinal);
        foreach (var (k, v) in fm)
        {
            copy[k] = v;
        }
        return copy;
    }

    private static List<string> GetTagList(IReadOnlyDictionary<string, object?> fm) =>
        [.. NoteFrontmatterParser.ExtractTags(fm)];

    private static List<string> MergeTagsAsStrings(IReadOnlyList<string> existing, IEnumerable<string> additions)
    {
        // Preserve insertion order; first-write wins on casing; dedupe case-insensitively.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var tag in existing)
        {
            if (seen.Add(tag))
            {
                result.Add(tag);
            }
        }
        foreach (var tag in additions)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                continue;
            }
            if (seen.Add(tag))
            {
                result.Add(tag);
            }
        }
        return result;
    }

    private static bool Matches(Note note, NoteQuery query, out string? snippet)
    {
        snippet = null;

        if (query.Tags is { Count: > 0 } requiredTags)
        {
            foreach (var required in requiredTags)
            {
                var present = note.Tags.Any(t => string.Equals(t, required, StringComparison.OrdinalIgnoreCase));
                if (!present)
                {
                    return false;
                }
            }
        }

        if (query.FrontmatterFilters is { Count: > 0 } filters)
        {
            foreach (var (key, expected) in filters)
            {
                if (!note.Frontmatter.TryGetValue(key, out var actual))
                {
                    return false;
                }
                if (!ScalarEquals(actual, expected))
                {
                    return false;
                }
            }
        }

        if (!string.IsNullOrEmpty(query.Text))
        {
            var needle = query.Text;
            var bodyIdx = note.Body.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (bodyIdx >= 0)
            {
                snippet = ExtractSnippet(note.Body, bodyIdx, needle.Length);
                return true;
            }

            // Fall back to frontmatter scalars — title matches, etc.
            foreach (var value in note.Frontmatter.Values)
            {
                if (value is string s && s.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        return true;
    }

    private static bool ScalarEquals(object? actual, object? expected)
    {
        if (actual is null || expected is null)
        {
            return actual is null && expected is null;
        }
        if (actual is string actualStr && expected is string expectedStr)
        {
            return string.Equals(actualStr, expectedStr, StringComparison.OrdinalIgnoreCase);
        }
        return Equals(actual, expected);
    }

    private static string ExtractSnippet(string body, int matchStart, int matchLength)
    {
        var start = Math.Max(0, matchStart - SnippetRadius);
        var end = Math.Min(body.Length, matchStart + matchLength + SnippetRadius);
        var slice = body[start..end].Replace('\n', ' ').Replace('\r', ' ');
        var prefix = start > 0 ? "…" : string.Empty;
        var suffix = end < body.Length ? "…" : string.Empty;
        return prefix + slice.Trim() + suffix;
    }
}
