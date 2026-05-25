using System.Collections.Generic;

namespace Sutando.Notes;

/// <summary>
/// Search predicate handed to <see cref="NotesService.SearchAsync"/>. All non-null filters are
/// applied with AND semantics — a note must match every populated criterion to surface in the
/// result set.
/// </summary>
public sealed record NoteQuery
{
    /// <summary>Optional free-text needle. Case-insensitive contains over body + every frontmatter scalar value.</summary>
    public string? Text { get; init; }

    /// <summary>Optional tag list. A note must carry <em>every</em> tag in the list (case-insensitive).</summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>
    /// Optional frontmatter equality predicates. A note must have a matching scalar value for
    /// each key (case-insensitive on strings; exact on numerics / bools).
    /// </summary>
    public IReadOnlyDictionary<string, object?>? FrontmatterFilters { get; init; }

    /// <summary>Optional cap on the number of returned hits. <see langword="null"/> = unlimited.</summary>
    public int? Limit { get; init; }
}

/// <summary>
/// A single search match — the matched <see cref="Note"/> plus a short snippet around the
/// first text hit (when the query carried a <see cref="NoteQuery.Text"/> needle).
/// </summary>
public sealed record NoteSearchHit
{
    /// <summary>The matched note.</summary>
    public required Note Note { get; init; }

    /// <summary>
    /// Snippet of the note's body around the first text-needle hit, or <see langword="null"/>
    /// when the query had no text component (or the hit landed inside frontmatter, not body).
    /// </summary>
    public string? Snippet { get; init; }
}
