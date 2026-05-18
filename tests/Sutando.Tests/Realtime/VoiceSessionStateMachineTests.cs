using System.Text.Json;
using Sutando.Realtime;

namespace Sutando.Tests.Realtime;

/// <summary>
/// Pumps known server events through a <see cref="VoiceSession"/> over the in-process
/// <see cref="FakeRealtimeTransport"/> and asserts that state transitions, resumption-handle
/// capture, and tool dispatch behave as documented.
/// </summary>
public sealed class VoiceSessionStateMachineTests
{
    private static RealtimeSessionConfig Config() => new(Model: "gemini-2.5-flash-live-preview", ApiKey: "AIzaTestKey");

    private static async Task WaitForState(VoiceSession session, VoiceSessionState target, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(2));
        while (DateTime.UtcNow < deadline)
        {
            if (session.State == target)
            {
                return;
            }
            await Task.Delay(10);
        }
        throw new Xunit.Sdk.XunitException($"Timed out waiting for state {target} — actual {session.State}.");
    }

    [Fact]
    public async Task Connect_then_SetupComplete_transitions_to_Listening()
    {
        var fake = new FakeRealtimeTransport();
        await using var session = new VoiceSession(transportFactory: () => fake);

        Assert.Equal(VoiceSessionState.Idle, session.State);

        await session.ConnectAsync(Config(), CancellationToken.None);
        Assert.Equal(VoiceSessionState.Connecting, session.State);
        Assert.True(fake.ConnectCalled);

        fake.Emit(new RealtimeSetupComplete());

        await WaitForState(session, VoiceSessionState.Listening);
    }

    [Fact]
    public async Task AudioOutput_transitions_to_Speaking_and_TurnComplete_returns_to_Listening()
    {
        var fake = new FakeRealtimeTransport();
        await using var session = new VoiceSession(transportFactory: () => fake);
        await session.ConnectAsync(Config(), CancellationToken.None);
        fake.Emit(new RealtimeSetupComplete());
        await WaitForState(session, VoiceSessionState.Listening);

        fake.Emit(new RealtimeAudioOutput(new byte[] { 0x00, 0x01 }, 24_000, 1, 16));
        await WaitForState(session, VoiceSessionState.Speaking);

        fake.Emit(new RealtimeTurnComplete());
        await WaitForState(session, VoiceSessionState.Listening);
    }

    [Fact]
    public async Task Interrupted_event_returns_Speaking_to_Listening()
    {
        var fake = new FakeRealtimeTransport();
        await using var session = new VoiceSession(transportFactory: () => fake);
        await session.ConnectAsync(Config(), CancellationToken.None);
        fake.Emit(new RealtimeSetupComplete());
        await WaitForState(session, VoiceSessionState.Listening);
        fake.Emit(new RealtimeAudioOutput(new byte[] { 0x00 }, 24_000, 1, 16));
        await WaitForState(session, VoiceSessionState.Speaking);

        fake.Emit(new RealtimeInterrupted());

        await WaitForState(session, VoiceSessionState.Listening);
    }

    [Fact]
    public async Task SessionResumptionUpdate_caches_handle_on_session()
    {
        var fake = new FakeRealtimeTransport();
        await using var session = new VoiceSession(transportFactory: () => fake);
        await session.ConnectAsync(Config(), CancellationToken.None);

        fake.Emit(new RealtimeSessionResumptionUpdate("handle-xyz", true));

        // Give the read loop a moment to consume the event.
        await Task.Delay(50);

        Assert.Equal("handle-xyz", session.ResumptionHandle);
    }

    [Fact]
    public async Task TransportClosed_event_transitions_to_Disconnected()
    {
        var fake = new FakeRealtimeTransport();
        await using var session = new VoiceSession(transportFactory: () => fake);
        await session.ConnectAsync(Config(), CancellationToken.None);

        fake.Emit(new RealtimeTransportClosed(RealtimeCloseInitiator.Server));
        fake.Complete();

        await WaitForState(session, VoiceSessionState.Disconnected);
    }

    [Fact]
    public async Task ToolCall_is_dispatched_to_registered_handler_and_response_is_sent()
    {
        var fake = new FakeRealtimeTransport();
        await using var session = new VoiceSession(transportFactory: () => fake);

        var schema = JsonDocument.Parse("""{"type":"object","properties":{"x":{"type":"integer"}}}""").RootElement;
        var calledArgs = JsonDocument.Parse("null").RootElement;
        session.RegisterTool(
            new RealtimeToolDefinition("echo", "echoes the input", schema),
            (args, ct) =>
            {
                calledArgs = args;
                return Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true, echoed = 42 }));
            });

        await session.ConnectAsync(Config(), CancellationToken.None);
        fake.Emit(new RealtimeSetupComplete());
        await WaitForState(session, VoiceSessionState.Listening);

        var argsElement = JsonDocument.Parse("""{"x":42}""").RootElement;
        fake.Emit(new RealtimeToolCall(new[] { new RealtimeFunctionCall("call-1", "echo", argsElement) }));

        // Wait for the response to flow back through the transport.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline && fake.SentToolResponses.Count == 0)
        {
            await Task.Delay(10);
        }

        var response = Assert.Single(fake.SentToolResponses);
        Assert.Equal("call-1", response.ToolCallId);
        Assert.Equal("echo", response.Name);
        Assert.True(response.Response.GetProperty("ok").GetBoolean());
        Assert.Equal(42, response.Response.GetProperty("echoed").GetInt32());
        Assert.Equal(42, calledArgs.GetProperty("x").GetInt32());
    }

    [Fact]
    public async Task ToolCall_for_unregistered_tool_returns_error_response()
    {
        var fake = new FakeRealtimeTransport();
        await using var session = new VoiceSession(transportFactory: () => fake);

        await session.ConnectAsync(Config(), CancellationToken.None);
        fake.Emit(new RealtimeSetupComplete());
        await WaitForState(session, VoiceSessionState.Listening);

        var argsElement = JsonDocument.Parse("{}").RootElement;
        fake.Emit(new RealtimeToolCall(new[] { new RealtimeFunctionCall("call-1", "unknown_tool", argsElement) }));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline && fake.SentToolResponses.Count == 0)
        {
            await Task.Delay(10);
        }

        var response = Assert.Single(fake.SentToolResponses);
        Assert.Equal("call-1", response.ToolCallId);
        Assert.True(response.Response.TryGetProperty("error", out var err));
        Assert.Contains("unknown_tool", err.GetString());
    }

    [Fact]
    public void RegisterTool_throws_on_duplicate_name()
    {
        var schema = JsonDocument.Parse("{}").RootElement;
        var session = new VoiceSession(transportFactory: () => new FakeRealtimeTransport());
        session.RegisterTool(new RealtimeToolDefinition("dup", "", schema), (_, _) => Task.FromResult(schema));

        Assert.Throws<InvalidOperationException>(
            () => session.RegisterTool(new RealtimeToolDefinition("dup", "", schema), (_, _) => Task.FromResult(schema)));
    }

    [Fact]
    public async Task ConnectAsync_reapplies_cached_resumption_handle()
    {
        var fakes = new Queue<FakeRealtimeTransport>(new[] { new FakeRealtimeTransport(), new FakeRealtimeTransport() });
        await using var session = new VoiceSession(transportFactory: () => fakes.Dequeue());

        await session.ConnectAsync(Config(), CancellationToken.None);
        var first = session.GetType()
            .GetField("_transport", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(session) as FakeRealtimeTransport;
        Assert.NotNull(first);

        first!.Emit(new RealtimeSessionResumptionUpdate("handle-A", true));
        await Task.Delay(50);
        first.Emit(new RealtimeTransportClosed(RealtimeCloseInitiator.Server));
        first.Complete();
        await WaitForState(session, VoiceSessionState.Disconnected);

        await session.ConnectAsync(Config(), CancellationToken.None);

        var second = session.GetType()
            .GetField("_transport", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(session) as FakeRealtimeTransport;
        Assert.NotNull(second);
        Assert.Equal("handle-A", second!.LastConfig?.ResumptionHandle);
    }
}
