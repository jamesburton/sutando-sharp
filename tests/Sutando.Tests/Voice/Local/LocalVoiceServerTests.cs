using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Sutando.Voice;
using Sutando.Voice.Local;

namespace Sutando.Tests.Voice.Local;

/// <summary>
/// End-to-end tests for the voice WebSocket server running in <c>--local</c> mode. The host is
/// stood up in-process via <see cref="WebApplicationFactory{TEntryPoint}"/> with the
/// <see cref="IRealtimeTransportFactory"/> swapped for one that mints
/// <see cref="LocalPipelineRealtimeClient"/>s backed by fakes — no real model files, but the
/// full WS handler → VoiceSession → local pipeline → wire-envelope path exercised.
/// </summary>
// Heavy in-process E2E test: stands up a Kestrel host and drives a full multi-stage local
// pipeline (VAD → STT → chat → TTS) through `Task.Run`/`Task.Yield` per stage. When this runs
// concurrently with the parallel main suite (412 tests across many xUnit collections) the
// thread pool saturates and the pipeline's per-stage continuations starve — the test consumes
// its full deadline without ever producing an audio envelope. Isolated, it finishes in ~2 s.
//
// The `Category=InProcessHost` trait gates this (and its pipeline-only sibling
// LocalPipelineSessionTests, and the Realtime VoiceServerTests) into a separate CI test pass —
// see .github/workflows/ci.yml — so the heavy E2E hosts run uncontended.
[Trait("Category", "InProcessHost")]
[SuppressMessage("Usage", "SUTANDO001", Justification = "Tests for our own experimental surface.")]
[SuppressMessage("Usage", "MEAI001", Justification = "Tests against experimental MEAI surfaces.")]
public sealed class LocalVoiceServerTests
{
    // Generous deadline: the test stands up an in-process Kestrel host and runs a full local
    // pipeline. Under the parallel full-suite run the thread pool is saturated, so the deadline
    // is a stall backstop, not a latency assertion.
    private static TimeSpan Deadline { get; } = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task Local_voice_audio_turn_produces_audio_envelope_over_the_wire()
    {
        await using var host = new LocalVoiceTestHost(
            () => new LocalPipelineRealtimeClient(
                LocalPipelineFakes.Build("hello from the user", "Hi back.")));
        using var cts = new CancellationTokenSource(Deadline);

        var ws = await ConnectAsync(host, cts.Token);
        Assert.Equal(WebSocketState.Open, ws.State);

        // setup_complete arrives once the local session emits SutandoSessionStarted.
        var setup = await ReceiveEnvelopeAsync(ws, cts.Token);
        Assert.Equal("setup_complete", setup);

        // Stream 20 ms PCM chunks continuously — like a real microphone. The bracketing fake VAD
        // brackets the stream into one turn; a steady trickle keeps the VAD stage draining its
        // detector events so the SpeechEnd edge is reliably observed.
        var chunk = Convert.ToBase64String(new byte[640]);
        var audioEnvelope = JsonSerializer.SerializeToUtf8Bytes(new { type = "audio", data = chunk });
        using var feedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var feeder = Task.Run(async () =>
        {
            while (!feedCts.IsCancellationRequested)
            {
                await ws.SendAsync(audioEnvelope, WebSocketMessageType.Text, endOfMessage: true, feedCts.Token);
                // Pace at the real 20 ms PCM chunk cadence (640 B @ 16 kHz / 16-bit / mono = 20 ms).
                // The BracketingVadDetector closes the turn on a frame *count* (SpeechEndAfterFrame),
                // not wall-clock, so pacing only governs thread-pool pressure — feeding faster than
                // real time just inflicts needless continuation churn on the already-saturated pool.
                await Task.Delay(20, feedCts.Token);
            }
        }, feedCts.Token);

        // Drain envelopes until a synthesised `audio` frame comes back from the pipeline. An
        // `error` envelope fails fast rather than letting the test hit the deadline.
        var sawAudio = false;
        while (!sawAudio && !cts.IsCancellationRequested)
        {
            var type = await ReceiveEnvelopeAsync(ws, cts.Token);
            Assert.NotEqual("error", type);
            if (type == "audio")
            {
                sawAudio = true;
            }
        }
        Assert.True(sawAudio, "expected a synthesised audio envelope back from the local pipeline");

        await feedCts.CancelAsync();
        try { await feeder; } catch (OperationCanceledException) { /* expected */ }
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token);
    }

    [Fact]
    public async Task Local_voice_missing_model_surfaces_error_envelope()
    {
        // The fail-graceful path: an Unavailable client (models not resolved) still completes the
        // handshake, then sends an `error` envelope.
        await using var host = new LocalVoiceTestHost(
            () => LocalPipelineRealtimeClient.Unavailable(
                "sutando voice --local: the LlamaSharp chat model is not configured."));
        using var cts = new CancellationTokenSource(Deadline);

        var ws = await ConnectAsync(host, cts.Token);

        var setup = await ReceiveEnvelopeAsync(ws, cts.Token);
        Assert.Equal("setup_complete", setup);

        var json = await ReceiveRawAsync(ws, cts.Token);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("error", doc.RootElement.GetProperty("type").GetString());
        Assert.Contains("chat model", doc.RootElement.GetProperty("message").GetString());

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token);
    }

    private static async Task<WebSocket> ConnectAsync(LocalVoiceTestHost host, CancellationToken ct)
    {
        var wsClient = host.Server.CreateWebSocketClient();
        var baseAddress = host.Server.BaseAddress;
        var uri = new UriBuilder(baseAddress)
        {
            Scheme = baseAddress.Scheme == "https" ? "wss" : "ws",
            Path = "/voice",
        }.Uri;
        return await wsClient.ConnectAsync(uri, ct);
    }

    private static async Task<string> ReceiveEnvelopeAsync(WebSocket ws, CancellationToken ct)
    {
        var json = await ReceiveRawAsync(ws, ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("type").GetString() ?? string.Empty;
    }

    private static async Task<string> ReceiveRawAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return "{\"type\":\"\"}";
            }
            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }
}

/// <summary>
/// In-process voice host wired for <c>--local</c> mode: <see cref="VoiceOptions.UseLocal"/> set,
/// and the <see cref="IRealtimeTransportFactory"/> swapped for one that returns a caller-supplied
/// <see cref="LocalPipelineRealtimeClient"/>. Mirrors <c>VoiceTestHost</c>'s fake-factory pattern.
/// </summary>
internal sealed class LocalVoiceTestHost : WebApplicationFactory<global::Sutando.Voice.Program>
{
    private readonly Func<LocalPipelineRealtimeClient> _clientFactory;

    public LocalVoiceTestHost(Func<LocalPipelineRealtimeClient> clientFactory)
    {
        _clientFactory = clientFactory;
        // --local mode is selected at server boot via the SUTANDO_VOICE_LOCAL env var; the WS
        // handler then skips the Gemini API-key gate.
        Environment.SetEnvironmentVariable("SUTANDO_VOICE_LOCAL", "1");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            // Replace the real LocalPipelineTransportFactory (which would need model files on
            // disk) with one returning a fakes-backed client.
            for (var i = services.Count - 1; i >= 0; i--)
            {
                if (services[i].ServiceType == typeof(IRealtimeTransportFactory))
                {
                    services.RemoveAt(i);
                }
            }
            services.AddSingleton<IRealtimeTransportFactory>(new FakeLocalTransportFactory(_clientFactory));
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Environment.SetEnvironmentVariable("SUTANDO_VOICE_LOCAL", null);
        }
        base.Dispose(disposing);
    }

    /// <summary>Transport factory that hands the WS handler a fakes-backed local client per connection.</summary>
    private sealed class FakeLocalTransportFactory : IRealtimeTransportFactory
    {
        private readonly Func<LocalPipelineRealtimeClient> _clientFactory;

        public FakeLocalTransportFactory(Func<LocalPipelineRealtimeClient> clientFactory)
            => _clientFactory = clientFactory;

        public IRealtimeClient Create() => _clientFactory();
    }
}
