using System.Text.Json;
using Sutando.Realtime;
using Sutando.Skills;
using Sutando.Voice.Skills;
using Sutando.Workspace;

namespace Sutando.Tests.Voice.Skills;

/// <summary>
/// Unit tests for <see cref="SkillRegistryVoiceBridge"/> — snapshot of <see cref="SkillRegistry"/>
/// onto realtime tool definitions plus a name → handler dispatcher.
/// </summary>
public sealed class SkillRegistryVoiceBridgeTests
{
    private static WorkspaceDirectory Workspace() => WorkspaceDirectory.Resolve();

    [Fact]
    public void Empty_registry_produces_empty_definitions_list_and_zero_count()
    {
        var registry = new SkillRegistry();

        var bridge = new SkillRegistryVoiceBridge(registry, Workspace());

        Assert.Equal(0, bridge.Count);
        Assert.Empty(bridge.GetToolDefinitions());
        Assert.Null(bridge.TryGetHandler("anything"));
    }

    [Fact]
    public void Registry_with_N_skills_produces_N_tool_definitions()
    {
        var registry = new SkillRegistry();
        registry.RegisterInstance(new FakeSkill("skill-a", "first"));
        registry.RegisterInstance(new FakeSkill("skill-b", "second"));
        registry.RegisterInstance(new FakeSkill("skill-c", "third"));

        var bridge = new SkillRegistryVoiceBridge(registry, Workspace());

        Assert.Equal(3, bridge.Count);
        var defs = bridge.GetToolDefinitions();
        Assert.Equal(3, defs.Count);

        var names = defs.Select(d => d.Name).ToList();
        Assert.Contains("skill-a", names);
        Assert.Contains("skill-b", names);
        Assert.Contains("skill-c", names);
    }

    [Fact]
    public void TryGetHandler_returns_a_handler_for_a_registered_skill()
    {
        var registry = new SkillRegistry();
        registry.RegisterInstance(new FakeSkill("known-skill"));
        var bridge = new SkillRegistryVoiceBridge(registry, Workspace());

        var handler = bridge.TryGetHandler("known-skill");

        Assert.NotNull(handler);
    }

    [Fact]
    public void TryGetHandler_returns_null_for_unknown_or_empty_name()
    {
        var registry = new SkillRegistry();
        registry.RegisterInstance(new FakeSkill("only-known"));
        var bridge = new SkillRegistryVoiceBridge(registry, Workspace());

        Assert.Null(bridge.TryGetHandler("not-registered"));
        Assert.Null(bridge.TryGetHandler(string.Empty));
        Assert.Null(bridge.TryGetHandler(null!));
    }

    [Fact]
    public async Task Dispatcher_routes_by_name_to_the_matching_skill()
    {
        var first = new FakeSkill("first-skill");
        var second = new FakeSkill("second-skill");
        var registry = new SkillRegistry();
        registry.RegisterInstance(first);
        registry.RegisterInstance(second);
        var bridge = new SkillRegistryVoiceBridge(registry, Workspace());

        var handler = bridge.TryGetHandler("second-skill")!;
        var args = JsonDocument.Parse("""{"key":"value"}""").RootElement;
        var result = await handler(args, CancellationToken.None);

        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal(0, first.InvocationCount);
        Assert.Equal(1, second.InvocationCount);
        Assert.Equal("value", second.LastArguments["key"]);
    }

    [Fact]
    public void Schema_resolver_override_flows_through_to_each_tool_definition()
    {
        var registry = new SkillRegistry();
        registry.RegisterInstance(new FakeSkill("structured-skill"));
        var customSchema = JsonDocument.Parse("""{"type":"object","required":["x"]}""").RootElement;

        var bridge = new SkillRegistryVoiceBridge(
            registry,
            Workspace(),
            schemaResolver: manifest => manifest.Id == "structured-skill" ? customSchema : null);

        var def = bridge.GetToolDefinitions().Single();
        Assert.True(def.ParameterSchema.TryGetProperty("required", out var required));
        Assert.Equal("x", required[0].GetString());
    }

    [Fact]
    public void SkillRoot_resolver_is_used_when_supplied()
    {
        var registry = new SkillRegistry();
        registry.RegisterInstance(new FakeSkill("rooted-skill"));
        var resolvedRoots = new List<string>();

        _ = new SkillRegistryVoiceBridge(
            registry,
            Workspace(),
            skillRootResolver: manifest =>
            {
                resolvedRoots.Add(manifest.Id);
                return "/custom/root";
            });

        Assert.Contains("rooted-skill", resolvedRoots);
    }

    [Fact]
    public void Dotted_skill_id_is_translated_to_underscore_for_Gemini_compatibility()
    {
        var registry = new SkillRegistry();
        registry.RegisterInstance(new FakeSkill("notes.search"));
        registry.RegisterInstance(new FakeSkill("plain"));
        var bridge = new SkillRegistryVoiceBridge(registry, Workspace());

        // The dotted id MUST appear on the wire as notes_search (Gemini Live tool-name rule),
        // but the underlying skill id stays unchanged on the registry.
        var defs = bridge.GetToolDefinitions();
        Assert.Contains(defs, d => d.Name == "notes_search");
        Assert.Contains(defs, d => d.Name == "plain");

        // Dispatcher resolves on the translated wire name — that's what Gemini sends back.
        Assert.NotNull(bridge.TryGetHandler("notes_search"));
        Assert.NotNull(bridge.TryGetHandler("plain"));

        // The original dotted id is NOT a valid wire name and must not resolve.
        Assert.Null(bridge.TryGetHandler("notes.search"));
    }

    [Fact]
    public void Skill_id_with_unfixable_invalid_chars_is_skipped_rather_than_aborting_construction()
    {
        var registry = new SkillRegistry();
        registry.RegisterInstance(new FakeSkill("ok-skill"));
        registry.RegisterInstance(new FakeSkill("bad id with space"));
        var bridge = new SkillRegistryVoiceBridge(registry, Workspace());

        var defs = bridge.GetToolDefinitions();
        Assert.Single(defs);
        Assert.Equal("ok-skill", defs[0].Name);
    }

    [Fact]
    public void RegisterWith_adds_every_tool_to_the_session()
    {
        var registry = new SkillRegistry();
        registry.RegisterInstance(new FakeSkill("alpha"));
        registry.RegisterInstance(new FakeSkill("beta"));
        var bridge = new SkillRegistryVoiceBridge(registry, Workspace());

        // We don't need a live transport — RegisterTool is a pure in-memory operation on the
        // session. A fresh VoiceSession built around a Fake transport stays Idle until ConnectAsync,
        // so RegisterTool runs identically to the production path.
        var client = new Sutando.Tests.Realtime.FakeRealtimeClient();
        var session = new VoiceSession(client);

        bridge.RegisterWith(session);

        // Adding the same set a second time must throw (duplicate registration), which proves
        // both calls actually landed in the session's tool table.
        Assert.Throws<InvalidOperationException>(() => bridge.RegisterWith(session));
    }

    /// <summary>
    /// Trivial test double — records args / count, returns a benign success result.
    /// </summary>
    private sealed class FakeSkill : ISkill
    {
        public FakeSkill(string id, string? description = null)
        {
            Manifest = new SkillManifest
            {
                Id = id,
                Name = id,
                Description = description ?? string.Empty,
                Entry = $"Fake.{id}, Fake.Assembly",
            };
        }

        public SkillManifest Manifest { get; }
        public int InvocationCount { get; private set; }
        public IReadOnlyDictionary<string, string> LastArguments { get; private set; } = new Dictionary<string, string>();

        public Task<SkillResult> ExecuteAsync(SkillContext context, IReadOnlyDictionary<string, string> arguments, CancellationToken ct)
        {
            InvocationCount++;
            LastArguments = arguments;
            return Task.FromResult(SkillResult.Ok($"ran {Manifest.Id}", TimeSpan.FromMilliseconds(1)));
        }
    }
}
