using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Sutando.Skills;

namespace Sutando.Notes.Builtin;

/// <summary>
/// <c>notes.search</c> — managed skill that surfaces the workspace's notes via the
/// <see cref="NotesService"/> search predicate. Accepts free-text, tag, and limit arguments
/// and renders the matches as a human-readable list (most-recent first).
/// </summary>
/// <remarks>
/// Arguments:
/// <list type="bullet">
///   <item><term><c>text</c></term><description>Optional. Free-text needle (case-insensitive, body + frontmatter scalars).</description></item>
///   <item><term><c>tags</c></term><description>Optional. Comma-separated tag list — every tag must be present.</description></item>
///   <item><term><c>limit</c></term><description>Optional. Cap on result count. Defaults to 10.</description></item>
/// </list>
/// </remarks>
public sealed class NotesSearchSkill : ISkill
{
    /// <summary>Default cap when the caller doesn't pass <c>limit</c>.</summary>
    public const int DefaultLimit = 10;

    private readonly NotesService _service;

    /// <inheritdoc/>
    public SkillManifest Manifest { get; }

    /// <summary>Construct with the default manifest.</summary>
    /// <param name="service">Backing notes service.</param>
    public NotesSearchSkill(NotesService service) : this(service, DefaultManifest()) { }

    /// <summary>Construct with a caller-supplied manifest (used by the managed-factory path).</summary>
    /// <param name="service">Backing notes service.</param>
    /// <param name="manifest">Pre-built manifest.</param>
    public NotesSearchSkill(NotesService service, SkillManifest manifest)
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

        var text = arguments.TryGetValue("text", out var t) && !string.IsNullOrWhiteSpace(t) ? t : null;
        var tags = arguments.TryGetValue("tags", out var tg) && !string.IsNullOrWhiteSpace(tg)
            ? SplitCsv(tg)
            : null;

        var limit = DefaultLimit;
        if (arguments.TryGetValue("limit", out var lim)
            && int.TryParse(lim, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLimit)
            && parsedLimit >= 0)
        {
            limit = parsedLimit;
        }

        var query = new NoteQuery
        {
            Text = text,
            Tags = tags,
            Limit = limit,
        };

        IReadOnlyList<NoteSearchHit> hits;
        try
        {
            hits = await _service.SearchAsync(query, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            return SkillResult.Fail($"notes.search: {ex.Message}", sw.Elapsed);
        }

        sw.Stop();
        return SkillResult.Ok(RenderHits(hits), sw.Elapsed);
    }

    /// <summary>The canonical manifest for the <c>notes.search</c> skill.</summary>
    public static SkillManifest DefaultManifest() => new()
    {
        Id = "notes.search",
        Name = "Notes — search",
        Description = "Search the workspace's notes by free text, tag, or frontmatter; returns hits ordered by most-recently-modified.",
        Version = "0.1.0",
        Runtime = SkillRuntime.Managed,
        Entry = "Sutando.Notes.Builtin.NotesSearchSkill, Sutando.Notes",
        Triggers = ["notes.search", "search-notes", "find-note"],
        Capabilities = ["fs-read", "notes"],
    };

    private static IReadOnlyList<string> SplitCsv(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string RenderHits(IReadOnlyList<NoteSearchHit> hits)
    {
        if (hits.Count == 0)
        {
            return "notes.search: 0 hits";
        }

        var sb = new StringBuilder();
        sb.Append("notes.search: ").Append(hits.Count.ToString(CultureInfo.InvariantCulture)).Append(" hit(s)").Append('\n');
        foreach (var hit in hits)
        {
            sb.Append("- ").Append(hit.Note.Path);
            if (hit.Note.Tags.Count > 0)
            {
                sb.Append("  [").Append(string.Join(", ", hit.Note.Tags)).Append(']');
            }
            if (!string.IsNullOrWhiteSpace(hit.Snippet))
            {
                sb.Append("  — ").Append(hit.Snippet);
            }
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }
}
