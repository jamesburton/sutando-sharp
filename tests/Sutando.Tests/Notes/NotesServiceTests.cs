using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Sutando.Notes;

namespace Sutando.Tests.Notes;

/// <summary>
/// End-to-end behaviour for <see cref="NotesService"/> against a real
/// <see cref="FileSystemNoteStore"/> rooted at a temp directory. Each test seeds whatever
/// fixtures it needs from scratch.
/// </summary>
public sealed class NotesServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly FileSystemNoteStore _store;
    private readonly NotesService _service;
    private DateTimeOffset _now = new(2026, 5, 25, 10, 0, 0, TimeSpan.Zero);

    public NotesServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "sutando-notes-svc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _store = new FileSystemNoteStore(_tempRoot);
        _service = new NotesService(_store, () => _now);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task CreateAsync_HappyPath_WritesNoteWithStampedTimestamps()
    {
        var note = await _service.CreateAsync(
            "ideas/voice-home.md",
            "voice-controlled home automation",
            frontmatter: new Dictionary<string, object?> { ["title"] = "Voice Home" },
            tags: ["ideas", "voice"]);

        Assert.Equal("ideas/voice-home.md", note.Path);
        Assert.Equal("Voice Home", note.Frontmatter["title"]);
        Assert.Equal(["ideas", "voice"], note.Tags);
        Assert.Equal(_now, note.Created);
        Assert.Equal(_now, note.Modified);
        Assert.Contains("voice-controlled home automation", note.Body);
    }

    [Fact]
    public async Task CreateAsync_ExistingPath_Throws()
    {
        await _service.CreateAsync("dup.md", "first");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CreateAsync("dup.md", "second"));
    }

    [Fact]
    public async Task UpdateBodyAsync_PreservesFrontmatterAndBumpsModified()
    {
        await _service.CreateAsync(
            "ideas.md",
            "v1 body",
            frontmatter: new Dictionary<string, object?> { ["title"] = "Ideas" },
            tags: ["a", "b"]);

        _now = _now.AddMinutes(5);

        var updated = await _service.UpdateBodyAsync("ideas.md", "v2 body");

        Assert.Equal("v2 body\n", updated.Body);
        Assert.Equal("Ideas", updated.Frontmatter["title"]);
        Assert.Equal(["a", "b"], updated.Tags);
        Assert.Equal(_now, updated.Modified);
        // Created should remain at the original timestamp (not bumped).
        Assert.NotEqual(updated.Modified, updated.Created);
    }

    [Fact]
    public async Task UpdateBodyAsync_MissingNote_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.UpdateBodyAsync("nope.md", "body"));
    }

    [Fact]
    public async Task AddTagAsync_AppendsNewTag_DedupesCaseInsensitive()
    {
        await _service.CreateAsync("n.md", "body", tags: ["ideas"]);

        var afterAdd = await _service.AddTagAsync("n.md", "voice");
        Assert.Equal(["ideas", "voice"], afterAdd.Tags);

        // Duplicate add (different casing) — no-op.
        var afterDup = await _service.AddTagAsync("n.md", "VOICE");
        Assert.Equal(["ideas", "voice"], afterDup.Tags);
    }

    [Fact]
    public async Task RemoveTagAsync_DropsTag_CaseInsensitiveMatch()
    {
        await _service.CreateAsync("n.md", "body", tags: ["Ideas", "Voice"]);

        var afterRemove = await _service.RemoveTagAsync("n.md", "voice");
        Assert.Equal(["Ideas"], afterRemove.Tags);

        // Removing a tag that isn't there is a no-op.
        var afterNoop = await _service.RemoveTagAsync("n.md", "nope");
        Assert.Equal(["Ideas"], afterNoop.Tags);
    }

    [Fact]
    public async Task SearchAsync_ByTextNeedle_HitsBodyAndReturnsSnippet()
    {
        await _service.CreateAsync("a.md", "long winding body mentions PowerShell somewhere in the middle here");
        await _service.CreateAsync("b.md", "this one has nothing to do with the search");

        var hits = await _service.SearchAsync(new NoteQuery { Text = "powershell" });

        var hit = Assert.Single(hits);
        Assert.Equal("a.md", hit.Note.Path);
        Assert.NotNull(hit.Snippet);
        Assert.Contains("PowerShell", hit.Snippet);
    }

    [Fact]
    public async Task SearchAsync_ByTags_RequiresAllTags()
    {
        await _service.CreateAsync("a.md", "body", tags: ["ideas", "voice"]);
        await _service.CreateAsync("b.md", "body", tags: ["ideas"]);
        await _service.CreateAsync("c.md", "body", tags: ["voice"]);

        var hits = await _service.SearchAsync(new NoteQuery { Tags = ["ideas", "voice"] });

        var paths = hits.Select(h => h.Note.Path).ToList();
        Assert.Single(paths);
        Assert.Contains("a.md", paths);
    }

    [Fact]
    public async Task SearchAsync_ByFrontmatterFilter_MatchesExactScalar()
    {
        await _service.CreateAsync("draft.md", "body",
            frontmatter: new Dictionary<string, object?> { ["status"] = "draft" });
        await _service.CreateAsync("published.md", "body",
            frontmatter: new Dictionary<string, object?> { ["status"] = "published" });

        var hits = await _service.SearchAsync(new NoteQuery
        {
            FrontmatterFilters = new Dictionary<string, object?> { ["status"] = "draft" },
        });

        var hit = Assert.Single(hits);
        Assert.Equal("draft.md", hit.Note.Path);
    }

    [Fact]
    public async Task SearchAsync_AppliesLimit_AfterSortByModifiedDescending()
    {
        await _service.CreateAsync("a.md", "needle"); _now = _now.AddMinutes(1);
        await _service.CreateAsync("b.md", "needle"); _now = _now.AddMinutes(1);
        await _service.CreateAsync("c.md", "needle");

        var hits = await _service.SearchAsync(new NoteQuery { Text = "needle", Limit = 2 });

        Assert.Equal(2, hits.Count);
        // Most-recent first.
        Assert.Equal("c.md", hits[0].Note.Path);
        Assert.Equal("b.md", hits[1].Note.Path);
    }

    [Fact]
    public async Task SearchAsync_NullText_MatchesEverything()
    {
        await _service.CreateAsync("a.md", "alpha");
        await _service.CreateAsync("b.md", "beta");

        var hits = await _service.SearchAsync(new NoteQuery());

        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public async Task SearchAsync_TextHitsFrontmatterTitle_Matches()
    {
        await _service.CreateAsync("a.md", "body unrelated",
            frontmatter: new Dictionary<string, object?> { ["title"] = "Magic Title Word" });

        var hits = await _service.SearchAsync(new NoteQuery { Text = "magic" });

        var hit = Assert.Single(hits);
        Assert.Equal("a.md", hit.Note.Path);
        // Body didn't contain the needle, so snippet stays null.
        Assert.Null(hit.Snippet);
    }
}
