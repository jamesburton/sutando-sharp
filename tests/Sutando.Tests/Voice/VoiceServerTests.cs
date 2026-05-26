using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Sutando.Realtime;
using Sutando.Voice;

namespace Sutando.Tests.Voice;

/// <summary>
/// End-to-end tests for the voice WebSocket server. Each test stands the host up in-process via
/// <see cref="VoiceTestHost"/>, connects a real WS client, and asserts on the round-tripped JSON
/// envelopes and the fake transport's recorded calls.
/// </summary>
// Heavy in-process host test — see LocalVoiceServerTests for the InProcessHost rationale.
[Trait("Category", "InProcessHost")]
public sealed class VoiceServerTests
{
    private static TimeSpan ShortDeadline { get; } = TimeSpan.FromSeconds(10);

    private static async Task<WebSocket> ConnectAsync(VoiceTestHost host, CancellationToken ct)
    {
        // Microsoft.AspNetCore.Mvc.Testing exposes the in-process server's WS client through
        // host.Server.CreateWebSocketClient(); we have to swap the http(s) prefix for ws(s) to
        // match the framework's URI checker.
        var wsClient = host.Server.CreateWebSocketClient();
        var baseAddress = host.Server.BaseAddress;
        var uri = new UriBuilder(baseAddress) { Scheme = baseAddress.Scheme == "https" ? "wss" : "ws", Path = "/voice" }.Uri;
        return await wsClient.ConnectAsync(uri, ct);
    }

    private static async Task<string> ReceiveTextAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return string.Empty;
            }
            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    [Fact]
    public async Task Healthz_returns_200_and_zero_sessions_when_idle()
    {
        await using var host = new VoiceTestHost();
        using var cts = new CancellationTokenSource(ShortDeadline);
        var client = host.CreateClient();

        var response = await client.GetAsync("/healthz", cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("sessions").GetInt32());
    }

    [Fact]
    public async Task Voice_handshake_succeeds_and_setup_complete_is_forwarded_as_text_envelope()
    {
        await using var host = new VoiceTestHost();
        using var cts = new CancellationTokenSource(ShortDeadline);

        var ws = await ConnectAsync(host, cts.Token);
        Assert.Equal(WebSocketState.Open, ws.State);

        // The handler creates a fake transport synchronously inside HandleAsync — wait for it.
        var fake = await host.Factory.WaitForCreateAsync(TimeSpan.FromSeconds(5));

        // Push setup_complete and read it off the wire.
        fake.Emit(new RealtimeSetupComplete());
        var json = await ReceiveTextAsync(ws, cts.Token);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("setup_complete", doc.RootElement.GetProperty("type").GetString());

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", cts.Token);
    }

    [Fact]
    public async Task Browser_audio_sent_before_setup_complete_is_held_until_setup_complete_then_dispatched()
    {
        // Regression test for the real-browser race that this project shipped with for weeks:
        // VoiceSession.ConnectAsync returns BEFORE Gemini Live's setup-complete arrives, but the
        // GenAI SDK's underlying WebSocket isn't actually open yet. If VoiceWebSocketHandler started
        // PumpInboundAsync immediately, the first browser frame would race the still-handshaking
        // transport and the SDK would throw "The WebSocket client is not connected." — surfaced
        // back to the browser as an error envelope.
        //
        // The fix is in VoiceWebSocketHandler.HandleAsync: await session.StateChanged to reach
        // Listening (= setup-complete arrived) BEFORE forwarding any client message.
        await using var host = new VoiceTestHost();
        using var cts = new CancellationTokenSource(ShortDeadline);

        var ws = await ConnectAsync(host, cts.Token);
        var fake = await host.Factory.WaitForCreateAsync(TimeSpan.FromSeconds(5));

        // Send an audio frame BEFORE emitting setup-complete. With the fix in place, the WS receive
        // buffer holds the frame; PumpInboundAsync hasn't started yet so SendAsync to the transport
        // isn't called.
        var pcm = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
        var envelope = JsonSerializer.SerializeToUtf8Bytes(new { type = "audio", data = Convert.ToBase64String(pcm) });
        await ws.SendAsync(envelope, WebSocketMessageType.Text, endOfMessage: true, cts.Token);

        // Give the server a moment to (incorrectly, pre-fix) dispatch the frame. With the fix the
        // server is parked waiting for Listening.
        await Task.Delay(200, cts.Token);
        Assert.Empty(fake.SentInputs);

        // Now flip the session to Listening and verify the buffered frame flows through.
        fake.Emit(new RealtimeSetupComplete());
        _ = await ReceiveTextAsync(ws, cts.Token);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline && fake.SentInputs.Count == 0)
        {
            await Task.Delay(20, cts.Token);
        }

        var sent = Assert.Single(fake.SentInputs);
        var audio = Assert.IsType<RealtimeInput.RealtimeAudioInput>(sent);
        Assert.Equal(pcm, audio.Pcm.ToArray());

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", cts.Token);
    }

    [Fact]
    public async Task Browser_audio_frame_is_forwarded_to_transport_as_pcm()
    {
        await using var host = new VoiceTestHost();
        using var cts = new CancellationTokenSource(ShortDeadline);

        var ws = await ConnectAsync(host, cts.Token);
        var fake = await host.Factory.WaitForCreateAsync(TimeSpan.FromSeconds(5));

        // Drive setup_complete first so the server-side session is in Listening — the audio path
        // doesn't strictly require it, but it mirrors real-world ordering.
        fake.Emit(new RealtimeSetupComplete());
        _ = await ReceiveTextAsync(ws, cts.Token);

        // 6 bytes of PCM (three 16-bit samples) — payload contents are irrelevant, only the round-trip matters.
        var pcm = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 };
        var envelope = JsonSerializer.SerializeToUtf8Bytes(new { type = "audio", data = Convert.ToBase64String(pcm) });
        await ws.SendAsync(envelope, WebSocketMessageType.Text, endOfMessage: true, cts.Token);

        // Spin briefly for the inbound pump to dispatch.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline && fake.SentInputs.Count == 0)
        {
            await Task.Delay(20, cts.Token);
        }

        var sent = Assert.Single(fake.SentInputs);
        var audio = Assert.IsType<RealtimeInput.RealtimeAudioInput>(sent);
        Assert.Equal(pcm, audio.Pcm.ToArray());

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", cts.Token);
    }

    [Fact]
    public async Task Concurrent_outbound_audio_events_are_serialised_onto_the_socket()
    {
        // Regression test for the WebSocket-send race: WebSocket.SendAsync throws
        // InvalidOperationException("There is already one outstanding 'SendAsync' call") when
        // two writes overlap. The handler serialises sends behind a SemaphoreSlim — this test
        // emits a tight burst of audio chunks and asserts every one is delivered intact.
        await using var host = new VoiceTestHost();
        using var cts = new CancellationTokenSource(ShortDeadline);

        var ws = await ConnectAsync(host, cts.Token);
        var fake = await host.Factory.WaitForCreateAsync(TimeSpan.FromSeconds(5));

        fake.Emit(new RealtimeSetupComplete());
        _ = await ReceiveTextAsync(ws, cts.Token);

        const int chunkCount = 10;
        for (var i = 0; i < chunkCount; i++)
        {
            // Distinct payload per chunk so the assertion can verify ordering AND uniqueness.
            fake.Emit(new RealtimeAudioOutput(new byte[] { (byte)i, (byte)(i + 1) }, 24_000, 1, 16));
        }

        var received = new List<string>();
        for (var i = 0; i < chunkCount; i++)
        {
            var json = await ReceiveTextAsync(ws, cts.Token);
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("audio", doc.RootElement.GetProperty("type").GetString());
            received.Add(doc.RootElement.GetProperty("data").GetString() ?? string.Empty);
        }
        Assert.Equal(chunkCount, received.Count);
        Assert.Equal(chunkCount, received.Distinct().Count());

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", cts.Token);
    }

    [Fact]
    public async Task Healthz_session_count_reflects_active_voice_connection()
    {
        await using var host = new VoiceTestHost();
        var client = host.CreateClient();
        using var cts = new CancellationTokenSource(ShortDeadline);

        var ws = await ConnectAsync(host, cts.Token);

        // The tracker is incremented at the top of HandleAsync; let the scheduler land it.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        int count = 0;
        while (DateTime.UtcNow < deadline)
        {
            var response = await client.GetAsync("/healthz", cts.Token);
            var body = await response.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(body);
            count = doc.RootElement.GetProperty("sessions").GetInt32();
            if (count >= 1)
            {
                break;
            }
            await Task.Delay(20, cts.Token);
        }
        Assert.True(count >= 1, $"expected sessions >= 1, got {count}");

        // Drive setup_complete so the handler exits its wait-for-Listening gate and PumpInboundAsync
        // is alive to acknowledge the close handshake. Without this, ws.CloseAsync would hang until
        // the test's cts expires.
        var fake = await host.Factory.WaitForCreateAsync(TimeSpan.FromSeconds(2));
        fake.Emit(new RealtimeSetupComplete());

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", cts.Token);
    }
}
