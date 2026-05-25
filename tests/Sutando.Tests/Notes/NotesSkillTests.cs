using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Sutando.Notes;
using Sutando.Notes.Builtin;
using Sutando.Skills;
using Sutando.Workspace;

namespace Sutando.Tests.Notes;

/// <summary>
/// Exercises each of the four built-in notes skills (<c>notes.search</c>, <c>notes.read</c>,
/// <c>notes.write</c>, <c>notes.tag</c>) via <see cref="SkillContext"/> against a temp-rooted
/// <see cref="FileSystemNoteStore"/>. Verifies the <see cref="SkillResult"/> shape mirrors the
/// cloud-skills convention (success/failure, body content, no spurious artifacts).
/// </summary>
public sealed class NotesSkillTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string? _previousEnv;
    private readonly WorkspaceDirectory _workspace;
    private readonly FileSystemNoteStore _store;
    private readonly NotesService _service;

    public NotesSkillTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "sutando-notes-skill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _previousEnv = Environment.GetEnvironmentVariable(WorkspaceDirectory.EnvVar);
        Environment.SetEnvironmentVariable(WorkspaceDirectory.EnvVar, _tempRoot);
        _workspace = WorkspaceDirectory.Resolve();
        _store = new FileSystemNoteStore(_workspace);
        _service = new NotesService(_store);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(WorkspaceDirectory.EnvVar, _previousEnv);
        try { Directory.Delete(_tempRoot, recursive: true); } catch (IOException) { }
    }

    private SkillContext Context() => new(_workspace, skillRoot: _tempRoot);

    // ----- notes.search ---------------------------------------------------------------

    [Fact]
    public async Task SearchSkill_NoArgs_ReturnsZeroHitsBody()
    {
        var skill = new NotesSearchSkill(_service);
        var result = await skill.ExecuteAsync(Context(), new Dictionary<string, string>(), CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Contains("0 hits", result.Body);
        Assert.Empty(result.Artifacts);
    }

    [Fact]
    public async Task SearchSkill_TextNeedle_FindsMatchingNote()
    {
        await _service.CreateAsync("a.md", "alpha body mentions widget X");
        await _service.CreateAsync("b.md", "beta body, no match");

        var skill = new NotesSearchSkill(_service);
        var result = await skill.ExecuteAsync(
            Context(),
            new Dictionary<string, string> { ["text"] = "widget" },
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Contains("a.md", result.Body);
        Assert.DoesNotContain("b.md", result.Body);
    }

    [Fact]
    public async Task SearchSkill_TagsArgument_SplitsCsvAndFiltersAll()
    {
        await _service.CreateAsync("ai.md", "body", tags: ["ai", "ideas"]);
        await _service.CreateAsync("ideas.md", "body", tags: ["ideas"]);

        var skill = new NotesSearchSkill(_service);
        var result = await skill.ExecuteAsync(
            Context(),
            new Dictionary<string, string> { ["tags"] = "ai, ideas" },
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Contains("ai.md", result.Body);
        Assert.DoesNotContain("ideas.md", result.Body);
    }

    // ----- notes.read -----------------------------------------------------------------

    [Fact]
    public async Task ReadSkill_HappyPath_RendersFrontmatterAndBody()
    {
        await _service.CreateAsync("hello.md", "hello world body",
            frontmatter: new Dictionary<string, object?> { ["title"] = "Hello" },
            tags: ["greetings"]);

        var skill = new NotesReadSkill(_store);
        var result = await skill.ExecuteAsync(
            Context(),
            new Dictionary<string, string> { ["path"] = "hello.md" },
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Contains("hello.md", result.Body);
        Assert.Contains("hello world body", result.Body);
        Assert.Contains("greetings", result.Body);
    }

    [Fact]
    public async Task ReadSkill_MissingPath_FailsCleanly()
    {
        var skill = new NotesReadSkill(_store);
        var result = await skill.ExecuteAsync(
            Context(),
            new Dictionary<string, string>(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("'path'", result.Error);
    }

    [Fact]
    public async Task ReadSkill_NonExistent_FailsWithPathInError()
    {
        var skill = new NotesReadSkill(_store);
        var result = await skill.ExecuteAsync(
            Context(),
            new Dictionary<string, string> { ["path"] = "ghost.md" },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("ghost.md", result.Error);
    }

    // ----- notes.write ----------------------------------------------------------------

    [Fact]
    public async Task WriteSkill_NewPath_CreatesNote()
    {
        var skill = new NotesWriteSkill(_service, _store);
        var result = await skill.ExecuteAsync(
            Context(),
            new Dictionary<string, string>
            {
                ["path"] = "new.md",
                ["body"] = "fresh body",
                ["tags"] = "ideas, voice",
            },
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Contains("new.md", result.Body);

        var loaded = await _store.ReadAsync("new.md");
        Assert.NotNull(loaded);
        Assert.Equal("fresh body\n", loaded.Body);
        Assert.Equal(["ideas", "voice"], loaded.Tags);
    }

    [Fact]
    public async Task WriteSkill_ExistingPath_UpdatesBodyAndMergesTags()
    {
        await _service.CreateAsync("update.md", "v1", tags: ["ideas"]);

        var skill = new NotesWriteSkill(_service, _store);
        var result = await skill.ExecuteAsync(
            Context(),
            new Dictionary<string, string>
            {
                ["path"] = "update.md",
                ["body"] = "v2",
                ["tags"] = "voice",
            },
            CancellationToken.None);

        Assert.True(result.Success, result.Error);

        var loaded = await _store.ReadAsync("update.md");
        Assert.NotNull(loaded);
        Assert.Equal("v2\n", loaded.Body);
        Assert.Contains("ideas", loaded.Tags);
        Assert.Contains("voice", loaded.Tags);
    }

    [Fact]
    public async Task WriteSkill_MissingPath_FailsCleanly()
    {
        var skill = new NotesWriteSkill(_service, _store);
        var result = await skill.ExecuteAsync(
            Context(),
            new Dictionary<string, string> { ["body"] = "no path" },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("'path'", result.Error);
    }

    [Fact]
    public async Task WriteSkill_MissingBody_FailsCleanly()
    {
        var skill = new NotesWriteSkill(_service, _store);
        var result = await skill.ExecuteAsync(
            Context(),
            new Dictionary<string, string> { ["path"] = "x.md" },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("'body'", result.Error);
    }

    // ----- notes.tag ------------------------------------------------------------------

    [Fact]
    public async Task TagSkill_AddAndRemove_RoundTripsThroughService()
    {
        await _service.CreateAsync("t.md", "body", tags: ["alpha"]);

        var skill = new NotesTagSkill(_service);
        var result = await skill.ExecuteAsync(
            Context(),
            new Dictionary<string, string>
            {
                ["path"] = "t.md",
                ["add"] = "beta, gamma",
                ["remove"] = "alpha",
            },
            CancellationToken.None);

        Assert.True(result.Success, result.Error);

        var loaded = await _store.ReadAsync("t.md");
        Assert.NotNull(loaded);
        Assert.DoesNotContain("alpha", loaded.Tags);
        Assert.Contains("beta", loaded.Tags);
        Assert.Contains("gamma", loaded.Tags);
    }

    [Fact]
    public async Task TagSkill_NeitherAddNorRemove_FailsCleanly()
    {
        await _service.CreateAsync("t.md", "body");

        var skill = new NotesTagSkill(_service);
        var result = await skill.ExecuteAsync(
            Context(),
            new Dictionary<string, string> { ["path"] = "t.md" },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("'add'", result.Error);
    }

    // ----- registration ---------------------------------------------------------------

    [Fact]
    public void RegisterAll_RegistersAllFourSkills()
    {
        var registry = new SkillRegistry();
        var ids = NotesSkillRegistration.RegisterAll(registry, _service, _store);

        Assert.Equal(["notes.search", "notes.read", "notes.write", "notes.tag"], ids);
        Assert.IsType<NotesSearchSkill>(registry.TryGet("notes.search"));
        Assert.IsType<NotesReadSkill>(registry.TryGet("notes.read"));
        Assert.IsType<NotesWriteSkill>(registry.TryGet("notes.write"));
        Assert.IsType<NotesTagSkill>(registry.TryGet("notes.tag"));
    }

    [Fact]
    public void DefaultManifests_HaveExpectedShape()
    {
        // Belt-and-braces — guard against accidental id / runtime / entry drift between the
        // manifest constants and what's wired into NotesSkillRegistration.
        var search = NotesSearchSkill.DefaultManifest();
        Assert.Equal("notes.search", search.Id);
        Assert.Equal(SkillRuntime.Managed, search.Runtime);
        Assert.Equal("Sutando.Notes.Builtin.NotesSearchSkill, Sutando.Notes", search.Entry);
        Assert.Contains("notes.search", search.Triggers);

        var read = NotesReadSkill.DefaultManifest();
        Assert.Equal("notes.read", read.Id);
        Assert.Equal("Sutando.Notes.Builtin.NotesReadSkill, Sutando.Notes", read.Entry);

        var write = NotesWriteSkill.DefaultManifest();
        Assert.Equal("notes.write", write.Id);
        Assert.Equal("Sutando.Notes.Builtin.NotesWriteSkill, Sutando.Notes", write.Entry);

        var tag = NotesTagSkill.DefaultManifest();
        Assert.Equal("notes.tag", tag.Id);
        Assert.Equal("Sutando.Notes.Builtin.NotesTagSkill, Sutando.Notes", tag.Entry);
    }
}
