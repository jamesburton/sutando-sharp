using System.Net.WebSockets;
using System.Text.Json;
using Sutando.Realtime;
using Sutando.Skills;
using Sutando.Voice.Skills;
using Sutando.Workspace;

namespace Sutando.Tests.Voice.Skills;

/// <summary>
/// In-process end-to-end test: the voice WS server boots with a
/// <see cref="SkillRegistryVoiceBridge"/> in DI, the bridge advertises its tools on the session's
/// setup envelope, and inbound tool-call frames from the model route through the bridge to the
/// matching skill. Trait-gated alongside the rest of the WS host suite so it can be filtered out
/// of the fast suite by CI scripts.
/// </summary>
[Trait("Category", "InProcessHost")]
public sealed class VoiceServerSkillBridgeTests
{
    private static TimeSpan ShortDeadline { get; } = TimeSpan.FromSeconds(10);

    private static async Task<WebSocket> ConnectAsync(VoiceTestHost host, CancellationToken ct)
    {
        var wsClient = host.Server.CreateWebSocketClient();
        var baseAddress = host.Server.BaseAddress;
        var uri = new UriBuilder(baseAddress) { Scheme = baseAddress.Scheme == "https" ? "wss" : "ws", Path = "/voice" }.Uri;
        return await wsClient.ConnectAsync(uri, ct);
    }

    [Fact]
    public async Task Bridge_tools_are_advertised_on_session_config_and_dispatch_routes_to_skill()
    {
        // Build a registry with a single fake skill; wrap it in a bridge; attach to the test host
        // before booting so the WS handler picks it up via DI.
        var skill = new FakeSkill("voice-test-skill");
        var registry = new SkillRegistry();
        registry.RegisterInstance(skill);
        var bridge = new SkillRegistryVoiceBridge(registry, WorkspaceDirectory.Resolve());

        await using var host = new VoiceTestHost { SkillBridge = bridge };
        using var cts = new CancellationTokenSource(ShortDeadline);

        // 1) Handshake — the WS upgrade triggers VoiceWebSocketHandler.HandleAsync which creates
        //    the VoiceSession, registers the bridge's tools, and connects.
        var ws = await ConnectAsync(host, cts.Token);
        Assert.Equal(WebSocketState.Open, ws.State);

        var fakeSession = await host.Factory.WaitForCreateAsync(TimeSpan.FromSeconds(5));

        // Assertion #1: the model-facing setup config carries our tool. This is the
        // "advertise at setup time" half of the deliverable.
        var advertised = fakeSession.LastConfig?.EffectiveTools ?? Array.Empty<RealtimeToolDefinition>();
        Assert.Contains(advertised, d => d.Name == "voice-test-skill");

        // Drive setup_complete so VoiceWebSocketHandler exits its wait-for-Listening gate. The
        // tool-call dispatch path itself doesn't go through PumpInboundAsync (it flows via
        // EventReceived → bridge handler), but emitting setup_complete here lets the close
        // handshake at the end of the test be acknowledged cleanly.
        fakeSession.Emit(new RealtimeSetupComplete());

        // 2) Push a tool-call event back through the session and assert the skill ran AND the
        //    response made it back onto the wire — the "dispatch" half of the deliverable.
        var args = JsonDocument.Parse("""{"name":"world"}""").RootElement;
        fakeSession.Emit(new RealtimeToolCall(new[]
        {
            new RealtimeFunctionCall("call-1", "voice-test-skill", args),
        }));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && fakeSession.SentToolResponses.Count == 0)
        {
            await Task.Delay(20, cts.Token);
        }

        var response = Assert.Single(fakeSession.SentToolResponses);
        Assert.Equal("call-1", response.ToolCallId);
        Assert.Equal("voice-test-skill", response.Name);
        Assert.True(response.Response.GetProperty("ok").GetBoolean());
        Assert.Equal("hello world", response.Response.GetProperty("body").GetString());
        Assert.Equal(1, skill.InvocationCount);
        Assert.Equal("world", skill.LastArguments["name"]);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", cts.Token);
    }

    [Fact]
    public async Task Voice_path_is_unchanged_when_no_bridge_is_registered()
    {
        // Regression guard: a host built with SkillBridge = null produces a config with no tools.
        // This locks in the "no bridge = no skill plumbing" invariant.
        await using var host = new VoiceTestHost();
        using var cts = new CancellationTokenSource(ShortDeadline);

        var ws = await ConnectAsync(host, cts.Token);
        var fakeSession = await host.Factory.WaitForCreateAsync(TimeSpan.FromSeconds(5));

        var advertised = fakeSession.LastConfig?.EffectiveTools ?? Array.Empty<RealtimeToolDefinition>();
        Assert.Empty(advertised);

        // Drive setup_complete so the handler exits its wait-for-Listening gate and the close
        // handshake at the end of the test is acknowledged cleanly.
        fakeSession.Emit(new RealtimeSetupComplete());

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", cts.Token);
    }

    /// <summary>Skill double — echoes a "hello {name}" body so the test can assert on dispatch + result shape.</summary>
    private sealed class FakeSkill : ISkill
    {
        public FakeSkill(string id)
        {
            Manifest = new SkillManifest
            {
                Id = id,
                Name = id,
                Description = "Fake skill for the voice-bridge integration test.",
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
            var name = arguments.TryGetValue("name", out var n) ? n : "anonymous";
            return Task.FromResult(SkillResult.Ok($"hello {name}", TimeSpan.FromMilliseconds(1)));
        }
    }
}
