using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sutando.Realtime;

namespace Sutando.Voice;

/// <summary>
/// Per-connection bridge between a browser <see cref="WebSocket"/> and a <see cref="VoiceSession"/>.
/// </summary>
/// <remarks>
/// <para>
/// Owns two pumps for the lifetime of one WebSocket:
/// </para>
/// <list type="bullet">
///   <item><b>Inbound:</b> reads text frames off the WS, parses the JSON envelope, forwards audio/text
///   to <see cref="VoiceSession.SendAsync(RealtimeInput, CancellationToken)"/>.</item>
///   <item><b>Outbound:</b> subscribes to <see cref="VoiceSession.EventReceived"/>, projects each
///   <see cref="RealtimeServerEvent"/> to a <see cref="ServerMessage"/>, and writes it as a single text
///   frame.</item>
/// </list>
/// <para>
/// Cleanup: when either pump exits (browser disconnect, Gemini transport closed, fault), the WS is
/// closed and the underlying <see cref="VoiceSession"/> is disposed so the Gemini Live socket releases.
/// </para>
/// </remarks>
public sealed class VoiceWebSocketHandler
{
    private static readonly int InboundBufferBytes = 64 * 1024;

    private readonly IRealtimeTransportFactory _transportFactory;
    private readonly VoiceSessionTracker _tracker;
    private readonly IOptions<VoiceOptions> _options;
    private readonly ILogger<VoiceWebSocketHandler> _logger;

    /// <summary>Creates a new handler. Registered as a singleton in <see cref="VoiceServer"/>; each WS connection re-uses the instance.</summary>
    /// <param name="transportFactory">Factory the handler hands to every <see cref="VoiceSession"/>.</param>
    /// <param name="tracker">Active-session counter — surfaced through <c>/healthz</c>.</param>
    /// <param name="options">Bound voice options (port, api key, model, voice name, system instruction).</param>
    /// <param name="logger">Logger.</param>
    public VoiceWebSocketHandler(
        IRealtimeTransportFactory transportFactory,
        VoiceSessionTracker tracker,
        IOptions<VoiceOptions> options,
        ILogger<VoiceWebSocketHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(transportFactory);
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _transportFactory = transportFactory;
        _tracker = tracker;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Run the bridge until the WebSocket closes. Returns when both pumps have stopped and the
    /// underlying <see cref="VoiceSession"/> has been disposed.
    /// </summary>
    /// <param name="socket">The accepted browser WebSocket.</param>
    /// <param name="ct">Cancellation token tied to the request lifetime. Cancelled when the host shuts down.</param>
    public async Task HandleAsync(WebSocket socket, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(socket);

        _tracker.Increment();
        // Serialises all outbound WS writes onto a single virtual thread of execution. WebSocket
        // throws InvalidOperationException("There is already one outstanding SendAsync call") when
        // two writes race; Gemini Live emits audio chunks every ~50 ms once the model speaks, so
        // back-to-back writes are the common case. EventReceived fires synchronously from the
        // read-loop but our fan-out task is fire-and-forget — without the semaphore the second
        // chunk would race the first.
        using var sendGate = new SemaphoreSlim(1, 1);
        try
        {
            var opts = _options.Value;

            // The API-key gate only applies to the cloud Gemini transport. In --local mode there
            // is no API key; the local-inference transport surfaces any model-config problem
            // itself (as an `error` envelope after the handshake — see LocalPipelineTransportFactory).
            if (!opts.UseLocal && string.IsNullOrWhiteSpace(opts.ApiKey))
            {
                // Surfaces back to the browser before we even try to connect to Gemini — a missing
                // key is an operator configuration problem, not a Gemini-side fault.
                await TrySendErrorAsync(socket, sendGate, "GEMINI_VOICE_API_KEY / GEMINI_API_KEY is not set on the server.", ct).ConfigureAwait(false);
                await TryCloseAsync(socket, WebSocketCloseStatus.PolicyViolation, "missing api key", ct).ConfigureAwait(false);
                return;
            }

            // ownsClient: true — the factory hands out a fresh IRealtimeClient per WS upgrade,
            // and tearing it down at the end of HandleAsync mirrors the original transport-per-
            // connection lifecycle (and keeps the in-process test fakes ergonomic — see
            // VoiceTestHost.FakeFactory).
            var session = new VoiceSession(client: _transportFactory.Create(), ownsClient: true);
            var config = new RealtimeSessionConfig(
                Model: opts.Model,
                ApiKey: opts.ApiKey,
                VoiceName: opts.VoiceName,
                SystemInstruction: opts.SystemInstruction);

            // The outbound pump is fed by EventReceived. Wire it before ConnectAsync so we never
            // miss a setup-complete event arriving on a fast path.
            EventHandler<RealtimeServerEvent> handler = (_, evt) =>
            {
                // Fire-and-forget: the event handler is sync; the actual WS send is async. The
                // semaphore inside ForwardEventAsync keeps overlapping audio chunks from racing
                // SendAsync on the same WebSocket.
                _ = ForwardEventAsync(socket, sendGate, evt, ct);
            };
            session.EventReceived += handler;

            try
            {
                await session.ConnectAsync(config, ct).ConfigureAwait(false);
                await PumpInboundAsync(socket, session, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected on host shutdown / client disconnect — fall through to cleanup
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Voice WS handler failed.");
                await TrySendErrorAsync(socket, sendGate, ex.Message, ct).ConfigureAwait(false);
            }
            finally
            {
                session.EventReceived -= handler;
                try
                {
                    await session.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // best-effort — we're tearing the session down anyway
                }
                await session.DisposeAsync().ConfigureAwait(false);
                await TryCloseAsync(socket, WebSocketCloseStatus.NormalClosure, "session ended", CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            _tracker.Decrement();
        }
    }

    // ---- inbound: WS → Gemini ----

    private async Task PumpInboundAsync(WebSocket socket, VoiceSession session, CancellationToken ct)
    {
        // ArrayPool keeps the 64 KiB receive buffer off the gen-2 heap. Most browser audio frames
        // are well under that; large frames are stitched across multiple ReceiveAsync calls before
        // we deserialize.
        var pool = ArrayPool<byte>.Shared;
        var buffer = pool.Rent(InboundBufferBytes);
        try
        {
            using var ms = new MemoryStream(capacity: InboundBufferBytes);
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                ms.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text || ms.Length == 0)
                {
                    // The developer harness sends every frame as text; binary frames are accepted
                    // only as base64-in-JSON. Drop and continue.
                    continue;
                }

                await DispatchClientMessageAsync(session, ms.GetBuffer().AsMemory(0, (int)ms.Length), ct).ConfigureAwait(false);
            }
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            // Browser tab closed without a clean handshake — normal in dev. Treat as a graceful exit.
        }
        finally
        {
            pool.Return(buffer);
        }
    }

    private async Task DispatchClientMessageAsync(VoiceSession session, ReadOnlyMemory<byte> json, CancellationToken ct)
    {
        ClientMessage? msg;
        try
        {
            msg = JsonSerializer.Deserialize<ClientMessage>(json.Span, VoiceJson.Options);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Discarding malformed client message.");
            return;
        }
        if (msg is null)
        {
            return;
        }

        switch (msg.Type)
        {
            case ClientMessageType.Audio:
                if (string.IsNullOrEmpty(msg.Data))
                {
                    return;
                }
                byte[] pcm;
                try
                {
                    pcm = Convert.FromBase64String(msg.Data);
                }
                catch (FormatException ex)
                {
                    _logger.LogWarning(ex, "Discarding audio frame with invalid base64.");
                    return;
                }
                await session.SendAsync(RealtimeInput.Audio(pcm), ct).ConfigureAwait(false);
                break;

            case ClientMessageType.Text:
                if (!string.IsNullOrEmpty(msg.Data))
                {
                    // Some clients send text under "data" instead of "text" — accept both.
                    await session.SendAsync(RealtimeInput.Text(msg.Data), ct).ConfigureAwait(false);
                }
                else if (!string.IsNullOrEmpty(msg.Text))
                {
                    await session.SendAsync(RealtimeInput.Text(msg.Text), ct).ConfigureAwait(false);
                }
                break;

            case ClientMessageType.Interrupt:
            case ClientMessageType.EndTurn:
                // IRealtimeTransport is fixed and exposes no method for client-initiated barge-in or
                // explicit turn-complete. We log the envelope so the wire protocol stays public, but
                // wiring it through is a follow-up that touches Sutando.Realtime — see commit body /
                // INTEGRATION-NOTES.md "Deferred".
                _logger.LogDebug("Client {Type} envelope ignored — see deferred work.", msg.Type);
                break;

            case ClientMessageType.Unknown:
            default:
                _logger.LogDebug("Discarding client message with unknown type.");
                break;
        }
    }

    // ---- outbound: Gemini → WS ----

    private async Task ForwardEventAsync(WebSocket socket, SemaphoreSlim sendGate, RealtimeServerEvent evt, CancellationToken ct)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }

        var envelope = MapEvent(evt);
        if (envelope is null)
        {
            return;
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, VoiceJson.Options);
        try
        {
            await sendGate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            // Cleanup raced us — the connection is going down anyway.
            return;
        }
        try
        {
            if (socket.State != WebSocketState.Open)
            {
                return;
            }
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebug(ex, "WebSocket write failed — likely client disconnect.");
        }
        finally
        {
            try
            {
                sendGate.Release();
            }
            catch (ObjectDisposedException)
            {
                // tearing down — swallow
            }
        }
    }

    /// <summary>
    /// Maps a <see cref="RealtimeServerEvent"/> to the on-the-wire <see cref="ServerMessage"/>.
    /// </summary>
    /// <param name="evt">The inbound event.</param>
    /// <returns>The mapped envelope, or <see langword="null"/> when the event has no client representation.</returns>
    /// <remarks>
    /// Internal so tests in <c>Sutando.Tests</c> can assert mapping behaviour without spinning up the host.
    /// </remarks>
    internal static ServerMessage? MapEvent(RealtimeServerEvent evt) => evt switch
    {
        RealtimeSetupComplete => new ServerMessage(ServerMessageType.SetupComplete),
        RealtimeAudioOutput audio => new ServerMessage(ServerMessageType.Audio)
        {
            Data = Convert.ToBase64String(audio.Pcm.Span),
        },
        RealtimeInputTranscription t => new ServerMessage(ServerMessageType.InputTranscription)
        {
            // The Finished flag has no envelope slot — fragments are emitted as-they-come and the
            // client concatenates. See commit body / README for the rationale.
            Text = t.Text,
        },
        RealtimeOutputTranscription t => new ServerMessage(ServerMessageType.OutputTranscription)
        {
            Text = t.Text,
        },
        RealtimeInterrupted => new ServerMessage(ServerMessageType.Interrupted),
        RealtimeTurnComplete => new ServerMessage(ServerMessageType.TurnComplete),
        RealtimeGoAway away => new ServerMessage(ServerMessageType.GoAway)
        {
            TimeLeftMs = away.RetryAfter is { } retry ? (int)retry.TotalMilliseconds : null,
            Message = away.ErrorMessage,
        },
        RealtimeTransportError err => new ServerMessage(ServerMessageType.Error)
        {
            Message = err.Message,
        },
        // RealtimeGroundingMetadata, RealtimeToolCall, RealtimeToolCallCancellation,
        // RealtimeSessionResumptionUpdate, RealtimeTransportClosed: not surfaced to the browser in
        // this slice. Tool calls are dispatched server-side; the rest are operator concerns.
        _ => null,
    };

    private static async Task TrySendErrorAsync(WebSocket socket, SemaphoreSlim sendGate, string message, CancellationToken ct)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }
        try
        {
            await sendGate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // cancelled or disposed — fall through, the close handshake below will still try to land
            return;
        }
        try
        {
            if (socket.State != WebSocketState.Open)
            {
                return;
            }
            var envelope = new ServerMessage(ServerMessageType.Error) { Message = message };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, VoiceJson.Options);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
        }
        catch
        {
            // best-effort — the close handshake below will still try to land
        }
        finally
        {
            try
            {
                sendGate.Release();
            }
            catch (ObjectDisposedException)
            {
                // tearing down — swallow
            }
        }
    }

    private static async Task TryCloseAsync(WebSocket socket, WebSocketCloseStatus status, string reason, CancellationToken ct)
    {
        if (socket.State is WebSocketState.Closed or WebSocketState.Aborted)
        {
            return;
        }
        try
        {
            await socket.CloseAsync(status, reason, ct).ConfigureAwait(false);
        }
        catch
        {
            // best-effort — peer may already be gone
        }
    }
}
