using System.Text.Json;
using Sutando.Realtime;
using Sutando.Skills;
using Sutando.Voice.Skills;
using Sutando.Workspace;

namespace Sutando.Tests.Voice.Skills;

/// <summary>
/// Unit tests for <see cref="SkillVoiceTool"/> — projects an <see cref="ISkill"/> onto the
/// realtime tool surface (<see cref="RealtimeToolDefinition"/> + <see cref="RealtimeToolHandler"/>).
/// </summary>
public sealed class SkillVoiceToolTests
{
    private static WorkspaceDirectory Workspace() => WorkspaceDirectory.Resolve();

    [Fact]
    public void Definition_uses_manifest_id_as_name_and_description_as_description()
    {
        var skill = new FakeSkill(new SkillManifest
        {
            Id = "fake-skill",
            Name = "Fake",
            Description = "A fake skill for tests.",
            Entry = "Fake.Entry, Fake.Assembly",
        });

        var tool = new SkillVoiceTool(skill, Workspace(), AppContext.BaseDirectory);

        Assert.Equal("fake-skill", tool.Definition.Name);
        Assert.Equal("A fake skill for tests.", tool.Definition.Description);
        Assert.Equal(RealtimeToolExecutionKind.Inline, tool.Definition.Execution);
    }

    [Fact]
    public void Definition_falls_back_to_manifest_name_when_description_is_empty()
    {
        var skill = new FakeSkill(new SkillManifest
        {
            Id = "fake-skill",
            Name = "Fake Display Name",
            Description = string.Empty,
            Entry = "Fake.Entry, Fake.Assembly",
        });

        var tool = new SkillVoiceTool(skill, Workspace(), AppContext.BaseDirectory);

        Assert.Equal("Fake Display Name", tool.Definition.Description);
    }

    [Fact]
    public void Default_schema_is_gemini_safe_object_with_empty_properties()
    {
        // Gemini Live's FunctionDeclaration.parameters uses an OpenAPI subset that doesn't accept
        // `additionalProperties`. The default must be an empty `properties: {}` object so the
        // setup envelope is accepted; per-skill schemas can override via the bridge resolver.
        var skill = new FakeSkill(new SkillManifest
        {
            Id = "fake-skill",
            Name = "Fake",
            Description = "Desc",
            Entry = "Fake.Entry, Fake.Assembly",
        });

        var tool = new SkillVoiceTool(skill, Workspace(), AppContext.BaseDirectory);
        var schema = tool.Definition.ParameterSchema;

        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal("Desc", schema.GetProperty("description").GetString());
        Assert.True(schema.TryGetProperty("properties", out var properties));
        Assert.Equal(JsonValueKind.Object, properties.ValueKind);
        Assert.Empty(properties.EnumerateObject());
        // Critically: NO additionalProperties — Gemini Live rejects setups that include it.
        Assert.False(schema.TryGetProperty("additionalProperties", out _));
    }

    [Fact]
    public void Parameter_schema_override_is_honoured()
    {
        var skill = new FakeSkill(new SkillManifest
        {
            Id = "fake-skill",
            Name = "Fake",
            Description = "Desc",
            Entry = "Fake.Entry, Fake.Assembly",
        });
        var custom = JsonDocument.Parse("""{"type":"object","required":["x"],"properties":{"x":{"type":"integer"}}}""").RootElement;

        var tool = new SkillVoiceTool(skill, Workspace(), AppContext.BaseDirectory, parameterSchemaOverride: custom);

        Assert.True(tool.Definition.ParameterSchema.TryGetProperty("required", out var required));
        Assert.Equal("x", required[0].GetString());
        Assert.Equal("integer", tool.Definition.ParameterSchema.GetProperty("properties").GetProperty("x").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("with.dot")]
    [InlineData("with:colon")]
    [InlineData("with space")]
    [InlineData("")]
    public void Construct_throws_on_invalid_tool_name(string id)
    {
        var skill = new FakeSkill(new SkillManifest
        {
            Id = id,
            Name = "Fake",
            Entry = "Fake.Entry, Fake.Assembly",
        });

        Assert.Throws<ArgumentException>(() => new SkillVoiceTool(skill, Workspace(), AppContext.BaseDirectory));
    }

    [Fact]
    public void Construct_throws_on_too_long_tool_name()
    {
        var skill = new FakeSkill(new SkillManifest
        {
            Id = new string('a', 64),
            Name = "Fake",
            Entry = "Fake.Entry, Fake.Assembly",
        });

        Assert.Throws<ArgumentException>(() => new SkillVoiceTool(skill, Workspace(), AppContext.BaseDirectory));
    }

    [Fact]
    public void CoerceArguments_returns_empty_for_non_object_payloads()
    {
        var stringPayload = JsonDocument.Parse("\"raw text\"").RootElement;
        var arrayPayload = JsonDocument.Parse("[1,2,3]").RootElement;
        var nullPayload = JsonDocument.Parse("null").RootElement;

        Assert.Empty(SkillVoiceTool.CoerceArguments(stringPayload));
        Assert.Empty(SkillVoiceTool.CoerceArguments(arrayPayload));
        Assert.Empty(SkillVoiceTool.CoerceArguments(nullPayload));
    }

    [Fact]
    public void CoerceArguments_passes_strings_through_and_keeps_non_strings_as_raw_json()
    {
        // Compact JSON (no whitespace) so the raw-text comparison stays predictable — GetRawText
        // preserves the source spacing of the JsonElement.
        var payload = JsonDocument.Parse(
            "{\"text\":\"hello\",\"count\":42,\"ratio\":1.5,\"flag\":true,\"nested\":{\"a\":1},\"list\":[1,2,3],\"absent\":null}"
        ).RootElement;

        var dict = SkillVoiceTool.CoerceArguments(payload);

        Assert.Equal("hello", dict["text"]);
        Assert.Equal("42", dict["count"]);
        Assert.Equal("1.5", dict["ratio"]);
        Assert.Equal("true", dict["flag"]);
        // Raw JSON survives — skills can re-parse if they want structured data back.
        Assert.Equal("{\"a\":1}", dict["nested"]);
        Assert.Equal("[1,2,3]", dict["list"]);
        Assert.Equal("null", dict["absent"]);
    }

    [Fact]
    public async Task Handler_round_trips_a_successful_skill_invocation()
    {
        var skill = new FakeSkill(new SkillManifest
        {
            Id = "fake-skill",
            Name = "Fake",
            Description = "desc",
            Entry = "Fake.Entry, Fake.Assembly",
        });
        var tool = new SkillVoiceTool(skill, Workspace(), AppContext.BaseDirectory);

        var args = JsonDocument.Parse("""{"text":"hello"}""").RootElement;
        var result = await tool.Handler(args, CancellationToken.None);

        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal("text=hello", result.GetProperty("body").GetString());
        Assert.True(skill.InvocationCount == 1);
        Assert.Equal("hello", skill.LastArguments["text"]);
    }

    [Fact]
    public async Task Handler_surfaces_a_failed_SkillResult_as_an_error_envelope()
    {
        var skill = new FakeSkill(
            new SkillManifest { Id = "fake-skill", Name = "Fake", Entry = "Fake.Entry, Fake.Assembly" },
            result: SkillResult.Fail("kaboom", TimeSpan.FromMilliseconds(5)));
        var tool = new SkillVoiceTool(skill, Workspace(), AppContext.BaseDirectory);

        var result = await tool.Handler(JsonDocument.Parse("{}").RootElement, CancellationToken.None);

        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal("kaboom", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Handler_catches_skill_exceptions_and_returns_error_envelope()
    {
        var skill = new FakeSkill(
            new SkillManifest { Id = "fake-skill", Name = "Fake", Entry = "Fake.Entry, Fake.Assembly" },
            thrower: new InvalidOperationException("blew up"));
        var tool = new SkillVoiceTool(skill, Workspace(), AppContext.BaseDirectory);

        var result = await tool.Handler(JsonDocument.Parse("{}").RootElement, CancellationToken.None);

        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal("blew up", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Handler_propagates_OperationCanceledException_when_token_is_signalled()
    {
        var skill = new FakeSkill(
            new SkillManifest { Id = "fake-skill", Name = "Fake", Entry = "Fake.Entry, Fake.Assembly" },
            thrower: new OperationCanceledException());
        var tool = new SkillVoiceTool(skill, Workspace(), AppContext.BaseDirectory);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => tool.Handler(JsonDocument.Parse("{}").RootElement, cts.Token));
    }

    /// <summary>
    /// Test double for <see cref="ISkill"/> — records args, returns a configurable result or throws.
    /// </summary>
    private sealed class FakeSkill : ISkill
    {
        private readonly SkillResult _result;
        private readonly Exception? _thrower;

        public FakeSkill(SkillManifest manifest, SkillResult? result = null, Exception? thrower = null)
        {
            Manifest = manifest;
            _result = result ?? SkillResult.Ok(string.Empty, TimeSpan.Zero);
            _thrower = thrower;
        }

        public SkillManifest Manifest { get; }
        public int InvocationCount { get; private set; }
        public IReadOnlyDictionary<string, string> LastArguments { get; private set; } = new Dictionary<string, string>();

        public Task<SkillResult> ExecuteAsync(SkillContext context, IReadOnlyDictionary<string, string> arguments, CancellationToken ct)
        {
            InvocationCount++;
            LastArguments = arguments;
            if (_thrower is not null)
            {
                throw _thrower;
            }
            if (_result.Body.Length == 0 && _result.Success)
            {
                // Echo the args for the "success" default — gives the handler test something to assert.
                var body = string.Join(",", arguments.Select(kv => $"{kv.Key}={kv.Value}"));
                return Task.FromResult(SkillResult.Ok(body, TimeSpan.FromMilliseconds(1)));
            }
            return Task.FromResult(_result);
        }
    }
}
