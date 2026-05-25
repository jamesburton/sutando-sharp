using Sutando.Proactive;

namespace Sutando.Tests.Proactive;

/// <summary>
/// Verifies <see cref="CronConfigLoader"/> round-trips the upstream <c>crons.example.json</c>
/// shape, honours the primary-then-fallback resolution order, and filters out invalid rows.
/// </summary>
public sealed class CronConfigLoaderTests : IDisposable
{
    private readonly string _root;

    public CronConfigLoaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sutando-proactive-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // best effort
        }
    }

    [Fact]
    public void Load_NoFiles_ReturnsEmpty()
    {
        var loader = new CronConfigLoader();
        var entries = loader.Load(_root);
        Assert.Empty(entries);
    }

    [Fact]
    public void Load_FromPrimaryFile_DeserialisesUpstreamShape()
    {
        File.WriteAllText(
            Path.Combine(_root, CronConfigLoader.PrimaryFileName),
            """
            [
              { "name": "main-loop", "cron": "*/5 * * * *", "prompt_skill": "proactive-loop" },
              { "name": "morning-briefing", "cron": "57 6 * * *", "prompt": "Run morning briefing." }
            ]
            """);

        var loader = new CronConfigLoader();
        var entries = loader.Load(_root);

        Assert.Equal(2, entries.Count);
        Assert.Equal("main-loop", entries[0].Name);
        Assert.Equal("*/5 * * * *", entries[0].Cron);
        Assert.Equal("proactive-loop", entries[0].PromptSkill);
        Assert.Null(entries[0].Prompt);
        Assert.Equal("morning-briefing", entries[1].Name);
        Assert.Equal("Run morning briefing.", entries[1].Prompt);
        Assert.Null(entries[1].PromptSkill);
    }

    [Fact]
    public void Load_FallsBackToExampleFile_WhenPrimaryMissing()
    {
        File.WriteAllText(
            Path.Combine(_root, CronConfigLoader.FallbackFileName),
            """[ { "name": "from-example", "cron": "0 * * * *", "prompt": "hello" } ]""");

        var loader = new CronConfigLoader();
        var entries = loader.Load(_root);

        Assert.Single(entries);
        Assert.Equal("from-example", entries[0].Name);
    }

    [Fact]
    public void Load_PrimaryFile_TakesPrecedenceOverFallback()
    {
        File.WriteAllText(
            Path.Combine(_root, CronConfigLoader.PrimaryFileName),
            """[ { "name": "primary", "cron": "0 * * * *", "prompt": "winner" } ]""");
        File.WriteAllText(
            Path.Combine(_root, CronConfigLoader.FallbackFileName),
            """[ { "name": "fallback", "cron": "0 * * * *", "prompt": "loser" } ]""");

        var entries = new CronConfigLoader().Load(_root);

        Assert.Single(entries);
        Assert.Equal("primary", entries[0].Name);
    }

    [Fact]
    public void Load_InvalidEntries_AreSkipped_NotFatal()
    {
        // Three bad rows + one good row — loader must keep the good one.
        // Row 1: no name. Row 2: both prompt and prompt_skill. Row 3: neither prompt nor prompt_skill. Row 4: valid.
        File.WriteAllText(
            Path.Combine(_root, CronConfigLoader.PrimaryFileName),
            """
            [
              { "name": "", "cron": "0 * * * *", "prompt": "no name" },
              { "name": "both", "cron": "0 * * * *", "prompt": "p", "prompt_skill": "s" },
              { "name": "neither", "cron": "0 * * * *" },
              { "name": "good", "cron": "*/5 * * * *", "prompt": "ok" }
            ]
            """);

        var entries = new CronConfigLoader().Load(_root);

        Assert.Single(entries);
        Assert.Equal("good", entries[0].Name);
    }

    [Fact]
    public void Load_MalformedJson_ReturnsEmpty_NotThrow()
    {
        File.WriteAllText(Path.Combine(_root, CronConfigLoader.PrimaryFileName), "{ this is not json");
        var entries = new CronConfigLoader().Load(_root);
        Assert.Empty(entries);
    }

    [Fact]
    public void CronEntry_IsValid_EnforcesPromptXorPromptSkill()
    {
        Assert.True(new CronEntry("a", "* * * * *", Prompt: "x").IsValid());
        Assert.True(new CronEntry("a", "* * * * *", PromptSkill: "x").IsValid());
        Assert.False(new CronEntry("a", "* * * * *").IsValid());
        Assert.False(new CronEntry("a", "* * * * *", Prompt: "x", PromptSkill: "y").IsValid());
        Assert.False(new CronEntry("", "* * * * *", Prompt: "x").IsValid());
        Assert.False(new CronEntry("a", "", Prompt: "x").IsValid());
    }
}
