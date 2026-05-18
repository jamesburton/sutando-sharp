using Sutando.Skills;

namespace Sutando.Tests.Skills;

public sealed class SkillManifestTests
{
    [Fact]
    public void Parse_RoundTripsCanonicalShape()
    {
        const string json = """
            {
              "id": "openai-tts",
              "name": "OpenAI TTS",
              "description": "Render text to mp3 via OpenAI tts-1-hd",
              "version": "1.0.0",
              "runtime": "python",
              "entry": "scripts/tts.py",
              "triggers": ["tts", "speak", "synthesize"],
              "capabilities": ["http-out", "fs-write"],
              "author": "sutando",
              "license": "MIT"
            }
            """;

        var manifest = SkillManifest.Parse(json);

        Assert.Equal("openai-tts", manifest.Id);
        Assert.Equal("OpenAI TTS", manifest.Name);
        Assert.Equal(SkillRuntime.Python, manifest.Runtime);
        Assert.Equal("scripts/tts.py", manifest.Entry);
        Assert.Equal(["tts", "speak", "synthesize"], manifest.Triggers);
        Assert.Equal(["http-out", "fs-write"], manifest.Capabilities);
        Assert.Equal("MIT", manifest.License);
    }

    [Fact]
    public void Parse_DefaultsRuntimeToManaged_WhenOmitted()
    {
        const string json = """
            { "id": "x", "name": "X", "entry": "X.X, X" }
            """;
        var manifest = SkillManifest.Parse(json);
        Assert.Equal(SkillRuntime.Managed, manifest.Runtime);
    }

    [Fact]
    public void Parse_AllowsCommentsAndTrailingCommas()
    {
        const string json = """
            {
              // top-level metadata
              "id": "a", "name": "A", "entry": "A.A, A",
            }
            """;
        var manifest = SkillManifest.Parse(json);
        Assert.Equal("a", manifest.Id);
    }

    [Fact]
    public void Parse_MissingId_Throws()
    {
        const string json = """ { "name": "X", "entry": "X" } """;
        Assert.Throws<FormatException>(() => SkillManifest.Parse(json));
    }

    [Fact]
    public void Parse_MissingEntry_Throws()
    {
        const string json = """ { "id": "x", "name": "X" } """;
        Assert.Throws<FormatException>(() => SkillManifest.Parse(json));
    }

    [Fact]
    public void Serialize_ProducesParseableJson()
    {
        var manifest = new SkillManifest
        {
            Id = "echo",
            Name = "Echo",
            Entry = "Sutando.Skills.Builtin.EchoSkill, Sutando.Skills",
            Triggers = ["echo", "ping"],
        };

        var json = manifest.Serialize();
        var parsed = SkillManifest.Parse(json);
        Assert.Equal(manifest.Id, parsed.Id);
        Assert.Equal(manifest.Entry, parsed.Entry);
        Assert.Equal(manifest.Triggers, parsed.Triggers);
    }

    [Theory]
    [InlineData("managed", SkillRuntime.Managed)]
    [InlineData("python", SkillRuntime.Python)]
    [InlineData("node", SkillRuntime.Node)]
    [InlineData("bash", SkillRuntime.Bash)]
    [InlineData("dotnet_tool", SkillRuntime.DotnetTool)]
    public void Parse_RuntimeAlias_MapsAsExpected(string raw, SkillRuntime expected)
    {
        var json = $$"""{ "id": "x", "name": "X", "entry": "e", "runtime": "{{raw}}" }""";
        var manifest = SkillManifest.Parse(json);
        Assert.Equal(expected, manifest.Runtime);
    }
}
