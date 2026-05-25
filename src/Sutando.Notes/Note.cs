using System.Collections.Generic;

namespace Sutando.Notes;

/// <summary>
/// One markdown note in the workspace's <c>notes/</c> tree.
/// </summary>
/// <remarks>
/// <para>
/// Notes are plain markdown files with an optional leading YAML frontmatter block:
/// </para>
/// <code>
/// ---
/// title: Some note
/// tags: [ideas, projects]
/// created: 2026-05-25T10:23:00Z
/// ---
/// Body markdown goes here…
/// </code>
/// <para>
/// The <see cref="Frontmatter"/> dictionary is a pass-through projection of the YAML map —
/// scalar values come through as <see cref="string"/> / <see cref="long"/> / <see cref="bool"/>,
/// nested maps as <see cref="IReadOnlyDictionary{TKey, TValue}"/>, and lists as
/// <see cref="IReadOnlyList{T}"/>. Unknown shapes round-trip verbatim so notes written by other
/// tools (the upstream Sutando CLI, vim, etc.) are not corrupted on edit.
/// </para>
/// <para>
/// <see cref="Tags"/> is a flattened convenience projection of the frontmatter <c>tags:</c> list
/// (case preserved as-stored; <see cref="NotesService"/> normalises case when mutating).
/// </para>
/// </remarks>
public sealed record Note
{
    /// <summary>Path relative to the notes-root directory (e.g. <c>ideas/voice-home.md</c>).</summary>
    public required string Path { get; init; }

    /// <summary>Parsed YAML frontmatter as a pass-through map. Empty when the file had no frontmatter block.</summary>
    public required IReadOnlyDictionary<string, object?> Frontmatter { get; init; }

    /// <summary>Tags extracted from the frontmatter <c>tags:</c> list (or empty if absent).</summary>
    public required IReadOnlyList<string> Tags { get; init; }

    /// <summary>Markdown body — everything after the frontmatter block (or the entire file if absent).</summary>
    public required string Body { get; init; }

    /// <summary>Creation timestamp — from frontmatter <c>created:</c> if present, otherwise file metadata.</summary>
    public required DateTimeOffset Created { get; init; }

    /// <summary>Last-modified timestamp — from frontmatter <c>modified:</c> if present, otherwise file metadata.</summary>
    public required DateTimeOffset Modified { get; init; }
}
