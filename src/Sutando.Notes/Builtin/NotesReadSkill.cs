using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Sutando.Skills;

namespace Sutando.Notes.Builtin;

/// <summary>
/// <c>notes.read</c> — managed skill that reads a single note from the store by its
/// store-relative path and renders its frontmatter + body verbatim.
/// </summary>
/// <remarks>
/// Arguments:
/// <list type="bullet">
///   <item><term><c>path</c></term><description>Required. Store-relative note path (e.g. <c>ideas/foo.md</c>).</description></item>
/// </list>
/// </remarks>
public sealed class NotesReadSkill : ISkill
{
    private readonly INoteStore _store;

    /// <inheritdoc/>
    public SkillManifest Manifest { get; }

    /// <summary>Construct with the default manifest.</summary>
    public NotesReadSkill(INoteStore store) : this(store, DefaultManifest()) { }

    /// <summary>Construct with a caller-supplied manifest.</summary>
    public NotesReadSkill(INoteStore store, SkillManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(manifest);
        _store = store;
        Manifest = manifest;
    }

    /// <inheritdoc/>
    public async Task<SkillResult> ExecuteAsync(
        SkillContext context,
        IReadOnlyDictionary<string, string> arguments,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(arguments);

        var sw = Stopwatch.StartNew();

        if (!arguments.TryGetValue("path", out var path) || string.IsNullOrWhiteSpace(path))
        {
            sw.Stop();
            return SkillResult.Fail("notes.read: 'path' argument is required", sw.Elapsed);
        }

        Note? note;
        try
        {
            note = await _store.ReadAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            return SkillResult.Fail($"notes.read: {ex.Message}", sw.Elapsed);
        }

        sw.Stop();
        if (note is null)
        {
            return SkillResult.Fail($"notes.read: no note at '{path}'", sw.Elapsed);
        }

        return SkillResult.Ok(Render(note), sw.Elapsed);
    }

    /// <summary>The canonical manifest for the <c>notes.read</c> skill.</summary>
    public static SkillManifest DefaultManifest() => new()
    {
        Id = "notes.read",
        Name = "Notes — read",
        Description = "Read a single note by its store-relative path and surface its frontmatter + body.",
        Version = "0.1.0",
        Runtime = SkillRuntime.Managed,
        Entry = "Sutando.Notes.Builtin.NotesReadSkill, Sutando.Notes",
        Triggers = ["notes.read", "read-note", "open-note"],
        Capabilities = ["fs-read", "notes"],
    };

    private static string Render(Note note)
    {
        var sb = new StringBuilder();
        sb.Append("notes.read: ").Append(note.Path).Append('\n');
        if (note.Tags.Count > 0)
        {
            sb.Append("tags: ").Append(string.Join(", ", note.Tags)).Append('\n');
        }
        sb.Append("modified: ").Append(note.Modified.ToString("o")).Append('\n');
        sb.Append("---\n");
        sb.Append(note.Body);
        return sb.ToString().TrimEnd('\n');
    }
}
