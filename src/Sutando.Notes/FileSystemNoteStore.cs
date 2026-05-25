using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sutando.Workspace;

namespace Sutando.Notes;

/// <summary>
/// File-system backed implementation of <see cref="INoteStore"/>. Walks the workspace's
/// <c>notes/</c> directory for <c>*.md</c> files and reads / writes them via
/// <see cref="NoteFrontmatterParser"/>.
/// </summary>
/// <remarks>
/// <para>
/// Writes are atomic — content is staged to <c>&lt;target&gt;.tmp</c> then moved into place
/// via <see cref="File.Move(string, string, bool)"/>. Concurrent <see cref="ListAsync"/>
/// callers therefore never observe a partially-written file (the temp suffix is also filtered
/// out of the listing for belt-and-braces).
/// </para>
/// <para>
/// Path separators on the round-trip use forward slashes regardless of host OS — the relative
/// paths a caller passes to <see cref="ReadAsync"/> and the paths that surface on
/// <see cref="Note.Path"/> match on Windows and on Unix.
/// </para>
/// </remarks>
public sealed class FileSystemNoteStore : INoteStore
{
    private const string TempSuffix = ".tmp";
    private const string MarkdownExtension = ".md";

    private readonly ILogger<FileSystemNoteStore> _logger;

    /// <inheritdoc/>
    public string RootPath { get; }

    /// <summary>Construct a store rooted at <paramref name="rootPath"/>. The directory is created on demand.</summary>
    /// <param name="rootPath">Absolute filesystem path to the notes-root directory.</param>
    /// <param name="logger">Optional logger; defaults to <see cref="NullLogger{T}"/>.</param>
    public FileSystemNoteStore(string rootPath, ILogger<FileSystemNoteStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        RootPath = Path.GetFullPath(rootPath);
        _logger = logger ?? NullLogger<FileSystemNoteStore>.Instance;
        Directory.CreateDirectory(RootPath);
    }

    /// <summary>
    /// Convenience constructor that wires the store to the workspace's canonical notes
    /// directory (<see cref="WorkspaceDirectory.Notes"/>).
    /// </summary>
    /// <param name="workspace">Resolved workspace.</param>
    /// <param name="logger">Optional logger.</param>
    public FileSystemNoteStore(WorkspaceDirectory workspace, ILogger<FileSystemNoteStore>? logger = null)
        : this(EnsureWorkspaceNotes(workspace), logger)
    {
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<Note> ListAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!Directory.Exists(RootPath))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(RootPath, "*" + MarkdownExtension, SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            // Skip atomic-write staging files so concurrent readers never see a half-written note.
            if (file.EndsWith(TempSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            Note? note;
            try
            {
                note = await ReadFileAsync(file, ct).ConfigureAwait(false);
            }
            catch (FormatException ex)
            {
                _logger.LogWarning(ex, "Skipping malformed note at {Path}", file);
                continue;
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Skipping unreadable note at {Path}", file);
                continue;
            }

            if (note is not null)
            {
                yield return note;
            }
        }
    }

    /// <inheritdoc/>
    public async Task<Note?> ReadAsync(string relativePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var abs = ResolveAbsolute(relativePath);
        if (!File.Exists(abs))
        {
            return null;
        }
        return await ReadFileAsync(abs, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task WriteAsync(Note note, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(note);
        ArgumentException.ThrowIfNullOrWhiteSpace(note.Path);

        var abs = ResolveAbsolute(note.Path);
        var dir = Path.GetDirectoryName(abs);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var content = NoteFrontmatterParser.Compose(note.Frontmatter, note.Body);
        var tmp = abs + TempSuffix;

        // Atomic-write: stage to <target>.tmp then move into place. File.Move with
        // overwrite:true gives us replace-or-create semantics in one call across both Windows
        // and Unix.
        await File.WriteAllTextAsync(tmp, content, ct).ConfigureAwait(false);
        File.Move(tmp, abs, overwrite: true);
    }

    /// <inheritdoc/>
    public Task DeleteAsync(string relativePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var abs = ResolveAbsolute(relativePath);
        if (File.Exists(abs))
        {
            File.Delete(abs);
        }
        return Task.CompletedTask;
    }

    private async Task<Note?> ReadFileAsync(string absolutePath, CancellationToken ct)
    {
        var content = await File.ReadAllTextAsync(absolutePath, ct).ConfigureAwait(false);
        var (frontmatter, body) = NoteFrontmatterParser.Parse(content);

        var info = new FileInfo(absolutePath);
        var fsCreated = new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero);
        var fsModified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);

        var created = TimestampFromFrontmatter(frontmatter, "created") ?? fsCreated;
        var modified = TimestampFromFrontmatter(frontmatter, "modified") ?? fsModified;

        return new Note
        {
            Path = ToRelative(absolutePath),
            Frontmatter = frontmatter,
            Tags = NoteFrontmatterParser.ExtractTags(frontmatter),
            Body = body,
            Created = created,
            Modified = modified,
        };
    }

    private static DateTimeOffset? TimestampFromFrontmatter(IReadOnlyDictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var raw) || raw is null)
        {
            return null;
        }
        return raw switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(dt.ToUniversalTime(), TimeSpan.Zero),
            string s when DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed) => parsed,
            _ => null,
        };
    }

    private string ResolveAbsolute(string relativePath)
    {
        var normalised = relativePath.Replace('\\', Path.DirectorySeparatorChar)
                                     .Replace('/', Path.DirectorySeparatorChar)
                                     .TrimStart(Path.DirectorySeparatorChar);
        var combined = Path.GetFullPath(Path.Combine(RootPath, normalised));

        // Defensive: guarantee callers can't escape the notes-root via "../" traversal.
        if (!combined.StartsWith(RootPath, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"relative path '{relativePath}' escapes the notes root.", nameof(relativePath));
        }
        return combined;
    }

    private string ToRelative(string absolutePath)
    {
        var rel = Path.GetRelativePath(RootPath, absolutePath);
        return rel.Replace('\\', '/');
    }

    private static string EnsureWorkspaceNotes(WorkspaceDirectory workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return workspace.Notes.FullName;
    }
}
