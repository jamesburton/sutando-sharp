using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sutando.Notes;

namespace Sutando.Tests.Notes;

/// <summary>
/// Disk-backed tests for <see cref="FileSystemNoteStore"/>. Each test rents its own temp
/// directory under <see cref="Path.GetTempPath"/> and cleans up on dispose. Atomic-write
/// semantics are exercised by writing through the store and asserting only the final file (no
/// <c>.tmp</c> sibling) survives.
/// </summary>
public sealed class FileSystemNoteStoreTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly FileSystemNoteStore _store;

    public FileSystemNoteStoreTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "sutando-notes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _store = new FileSystemNoteStore(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task WriteAsync_ThenReadAsync_RoundTripsFrontmatterAndBody()
    {
        var fm = new Dictionary<string, object?>
        {
            ["title"] = "Hello",
            ["tags"] = new List<object?> { "alpha", "beta" },
        };
        var note = NewNote("hello.md", fm, "body content");

        await _store.WriteAsync(note);

        var loaded = await _store.ReadAsync("hello.md");
        Assert.NotNull(loaded);
        Assert.Equal("hello.md", loaded.Path);
        Assert.Equal("Hello", loaded.Frontmatter["title"]);
        Assert.Equal(["alpha", "beta"], loaded.Tags);
        Assert.Equal("body content\n", loaded.Body);
    }

    [Fact]
    public async Task WriteAsync_LeavesNoTempFileBehind()
    {
        var note = NewNote("atomic.md", new Dictionary<string, object?>(), "body");
        await _store.WriteAsync(note);

        var tempSiblings = Directory.GetFiles(_tempRoot, "*.tmp", SearchOption.AllDirectories);
        Assert.Empty(tempSiblings);
        Assert.True(File.Exists(Path.Combine(_tempRoot, "atomic.md")));
    }

    [Fact]
    public async Task ListAsync_IgnoresTmpStagingFiles()
    {
        // Simulate a crash mid-write by dropping a stray .tmp file in the tree. The list pass
        // must skip it so concurrent readers never observe partial content.
        await File.WriteAllTextAsync(Path.Combine(_tempRoot, "real.md"), "---\ntitle: Real\n---\nhi\n");
        await File.WriteAllTextAsync(Path.Combine(_tempRoot, "stale.md.tmp"), "---\ntitle: Stale\n---\nhi\n");

        var listed = new List<Note>();
        await foreach (var n in _store.ListAsync())
        {
            listed.Add(n);
        }

        Assert.Single(listed);
        Assert.Equal("real.md", listed[0].Path);
    }

    [Fact]
    public async Task ListAsync_FindsNestedNotes()
    {
        Directory.CreateDirectory(Path.Combine(_tempRoot, "ideas"));
        await File.WriteAllTextAsync(Path.Combine(_tempRoot, "top.md"), "---\ntitle: Top\n---\nbody\n");
        await File.WriteAllTextAsync(Path.Combine(_tempRoot, "ideas", "nested.md"), "---\ntitle: Nested\n---\nbody\n");

        var paths = new List<string>();
        await foreach (var n in _store.ListAsync())
        {
            paths.Add(n.Path);
        }

        Assert.Contains("top.md", paths);
        // Forward-slash normalisation regardless of host OS.
        Assert.Contains("ideas/nested.md", paths);
    }

    [Fact]
    public async Task ListAsync_SkipsMalformedNotesWithoutThrowing()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempRoot, "good.md"), "---\ntitle: Good\n---\nbody\n");
        await File.WriteAllTextAsync(Path.Combine(_tempRoot, "broken.md"), "---\ntitle: \"unterminated\n---\nbody\n");

        var paths = new List<string>();
        await foreach (var n in _store.ListAsync())
        {
            paths.Add(n.Path);
        }

        Assert.Contains("good.md", paths);
        Assert.DoesNotContain("broken.md", paths);
    }

    [Fact]
    public async Task ReadAsync_MissingFile_ReturnsNull()
    {
        var loaded = await _store.ReadAsync("does-not-exist.md");
        Assert.Null(loaded);
    }

    [Fact]
    public async Task DeleteAsync_RemovesFile_NoOpWhenMissing()
    {
        var note = NewNote("doomed.md", new Dictionary<string, object?>(), "body");
        await _store.WriteAsync(note);
        Assert.True(File.Exists(Path.Combine(_tempRoot, "doomed.md")));

        await _store.DeleteAsync("doomed.md");
        Assert.False(File.Exists(Path.Combine(_tempRoot, "doomed.md")));

        // Calling delete again is a no-op.
        await _store.DeleteAsync("doomed.md");
    }

    [Fact]
    public async Task WriteAsync_CreatesParentDirectories()
    {
        var note = NewNote("nested/deep/note.md", new Dictionary<string, object?>(), "body");
        await _store.WriteAsync(note);

        var expected = Path.Combine(_tempRoot, "nested", "deep", "note.md");
        Assert.True(File.Exists(expected));
    }

    [Fact]
    public async Task ReadAsync_PathTraversalAttempt_Throws()
    {
        // Should never be possible to escape the notes root via relative-segment tricks.
        await Assert.ThrowsAsync<ArgumentException>(
            () => _store.ReadAsync("../escape.md", CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_UsesFrontmatterTimestampsWhenPresent()
    {
        var fm = new Dictionary<string, object?>
        {
            ["created"] = "2026-01-02T03:04:05Z",
            ["modified"] = "2026-02-03T04:05:06Z",
        };
        var note = NewNote("stamped.md", fm, "body");
        await _store.WriteAsync(note);

        var loaded = await _store.ReadAsync("stamped.md");
        Assert.NotNull(loaded);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), loaded.Created);
        Assert.Equal(new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero), loaded.Modified);
    }

    [Fact]
    public async Task ReadAsync_FallsBackToFilesystemTimestampsWhenAbsent()
    {
        var note = NewNote("nostamp.md", new Dictionary<string, object?>(), "body");
        await _store.WriteAsync(note);

        var loaded = await _store.ReadAsync("nostamp.md");
        Assert.NotNull(loaded);
        // Filesystem timestamps are wall-clock-ish; verify they're at least populated and recent.
        Assert.True(loaded.Modified > DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.True(loaded.Created > DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    private static Note NewNote(string relativePath, IReadOnlyDictionary<string, object?> fm, string body) => new()
    {
        Path = relativePath,
        Frontmatter = fm,
        Tags = NoteFrontmatterParser.ExtractTags(fm),
        Body = body,
        Created = DateTimeOffset.UtcNow,
        Modified = DateTimeOffset.UtcNow,
    };
}
