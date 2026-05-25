using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Sutando.Skills;

namespace Sutando.Notes.Builtin;

/// <summary>
/// <c>notes.write</c> — managed skill that creates or replaces a note at the given path. Maps
/// to <see cref="NotesService.CreateAsync"/> for new notes and
/// <see cref="NotesService.UpdateBodyAsync"/> for existing ones, with optional tag-merge in
/// either path.
/// </summary>
/// <remarks>
/// Arguments:
/// <list type="bullet">
///   <item><term><c>path</c></term><description>Required. Store-relative target path.</description></item>
///   <item><term><c>body</c></term><description>Required. Markdown body.</description></item>
///   <item><term><c>tags</c></term><description>Optional. Comma-separated tag list to apply.</description></item>
/// </list>
/// </remarks>
public sealed class NotesWriteSkill : ISkill
{
    private readonly NotesService _service;
    private readonly INoteStore _store;

    /// <inheritdoc/>
    public SkillManifest Manifest { get; }

    /// <summary>Construct with the default manifest.</summary>
    public NotesWriteSkill(NotesService service, INoteStore store) : this(service, store, DefaultManifest()) { }

    /// <summary>Construct with a caller-supplied manifest.</summary>
    public NotesWriteSkill(NotesService service, INoteStore store, SkillManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(manifest);
        _service = service;
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
            return SkillResult.Fail("notes.write: 'path' argument is required", sw.Elapsed);
        }
        if (!arguments.TryGetValue("body", out var body) || body is null)
        {
            sw.Stop();
            return SkillResult.Fail("notes.write: 'body' argument is required", sw.Elapsed);
        }

        var tags = arguments.TryGetValue("tags", out var tg) && !string.IsNullOrWhiteSpace(tg)
            ? SplitCsv(tg)
            : null;

        Note result;
        try
        {
            // Read once to decide create-vs-update so we can pick the right service method and
            // report what we did in the result body. Cheaper than a try/catch around CreateAsync.
            var existing = await _store.ReadAsync(path, ct).ConfigureAwait(false);
            if (existing is null)
            {
                result = await _service.CreateAsync(path, body, frontmatter: null, tags: tags, ct).ConfigureAwait(false);
            }
            else
            {
                result = await _service.UpdateBodyAsync(path, body, ct).ConfigureAwait(false);
                if (tags is { Count: > 0 })
                {
                    foreach (var tag in tags)
                    {
                        result = await _service.AddTagAsync(path, tag, ct).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            return SkillResult.Fail($"notes.write: {ex.Message}", sw.Elapsed);
        }

        sw.Stop();
        return SkillResult.Ok($"notes.write: wrote {result.Path}", sw.Elapsed);
    }

    /// <summary>The canonical manifest for the <c>notes.write</c> skill.</summary>
    public static SkillManifest DefaultManifest() => new()
    {
        Id = "notes.write",
        Name = "Notes — write",
        Description = "Create or update a note at the given store-relative path, with optional tag list.",
        Version = "0.1.0",
        Runtime = SkillRuntime.Managed,
        Entry = "Sutando.Notes.Builtin.NotesWriteSkill, Sutando.Notes",
        Triggers = ["notes.write", "save-note", "write-note", "take-note"],
        Capabilities = ["fs-write", "notes"],
    };

    private static IReadOnlyList<string> SplitCsv(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
