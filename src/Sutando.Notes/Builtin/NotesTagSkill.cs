using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Sutando.Skills;

namespace Sutando.Notes.Builtin;

/// <summary>
/// <c>notes.tag</c> — managed skill that adds and/or removes tags on an existing note. Either
/// <c>add</c> or <c>remove</c> (or both) must be supplied.
/// </summary>
/// <remarks>
/// Arguments:
/// <list type="bullet">
///   <item><term><c>path</c></term><description>Required. Store-relative note path.</description></item>
///   <item><term><c>add</c></term><description>Optional. Comma-separated list of tags to add.</description></item>
///   <item><term><c>remove</c></term><description>Optional. Comma-separated list of tags to remove.</description></item>
/// </list>
/// </remarks>
public sealed class NotesTagSkill : ISkill
{
    private readonly NotesService _service;

    /// <inheritdoc/>
    public SkillManifest Manifest { get; }

    /// <summary>Construct with the default manifest.</summary>
    public NotesTagSkill(NotesService service) : this(service, DefaultManifest()) { }

    /// <summary>Construct with a caller-supplied manifest.</summary>
    public NotesTagSkill(NotesService service, SkillManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(manifest);
        _service = service;
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
            return SkillResult.Fail("notes.tag: 'path' argument is required", sw.Elapsed);
        }

        var add = arguments.TryGetValue("add", out var a) && !string.IsNullOrWhiteSpace(a)
            ? SplitCsv(a)
            : [];
        var remove = arguments.TryGetValue("remove", out var r) && !string.IsNullOrWhiteSpace(r)
            ? SplitCsv(r)
            : [];

        if (add.Count == 0 && remove.Count == 0)
        {
            sw.Stop();
            return SkillResult.Fail("notes.tag: at least one of 'add' or 'remove' is required", sw.Elapsed);
        }

        Note? result = null;
        try
        {
            foreach (var tag in add)
            {
                result = await _service.AddTagAsync(path, tag, ct).ConfigureAwait(false);
            }
            foreach (var tag in remove)
            {
                result = await _service.RemoveTagAsync(path, tag, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            return SkillResult.Fail($"notes.tag: {ex.Message}", sw.Elapsed);
        }

        sw.Stop();
        var body = result is null
            ? "notes.tag: no changes"
            : Render(result, add, remove);
        return SkillResult.Ok(body, sw.Elapsed);
    }

    /// <summary>The canonical manifest for the <c>notes.tag</c> skill.</summary>
    public static SkillManifest DefaultManifest() => new()
    {
        Id = "notes.tag",
        Name = "Notes — tag",
        Description = "Add and/or remove tags on an existing note.",
        Version = "0.1.0",
        Runtime = SkillRuntime.Managed,
        Entry = "Sutando.Notes.Builtin.NotesTagSkill, Sutando.Notes",
        Triggers = ["notes.tag", "tag-note", "untag-note"],
        Capabilities = ["fs-write", "notes"],
    };

    private static IReadOnlyList<string> SplitCsv(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Render(Note note, IReadOnlyList<string> added, IReadOnlyList<string> removed)
    {
        var sb = new StringBuilder();
        sb.Append("notes.tag: ").Append(note.Path);
        if (added.Count > 0)
        {
            sb.Append("  +[").Append(string.Join(", ", added)).Append(']');
        }
        if (removed.Count > 0)
        {
            sb.Append("  -[").Append(string.Join(", ", removed)).Append(']');
        }
        sb.Append("  →  [").Append(string.Join(", ", note.Tags)).Append(']');
        return sb.ToString();
    }
}
