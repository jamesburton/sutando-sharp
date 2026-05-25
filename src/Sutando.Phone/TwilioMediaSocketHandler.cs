using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sutando.Bridge;
using Sutando.Realtime;

namespace Sutando.Phone;

/// <summary>
/// Per-call bridge between a Twilio Media Streams WebSocket and a <see cref="VoiceSession"/>
/// driven by Gemini Live.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <c>Sutando.Voice.VoiceWebSocketHandler</c> structurally — two pumps for the
/// lifetime of one socket — but speaks Twilio's wire format instead of the browser JSON
/// envelope.
/// </para>
/// <list type="bullet">
///   <item>
///     <b>Inbound (caller → Gemini):</b> Twilio sends <c>media</c> envelopes carrying
///     base64-encoded μ-law / 8 kHz audio. We decode μ-law → linear PCM 8 kHz, upsample to
///     16 kHz, and forward to <see cref="VoiceSession.SendAsync"/> as a
///     <see cref="RealtimeInput.Audio(ReadOnlyMemory{byte}, string?)"/> envelope.
///   </item>
///   <item>
///     <b>Outbound (Gemini → caller):</b> the model emits <see cref="RealtimeAudioOutput"/>
///     at 24 kHz linear PCM. We downsample to 8 kHz, μ-law encode, base64, and send a Twilio
///     <c>media</c> envelope back over the same WS.
///   </item>
/// </list>
/// <para>
/// Cleanup: when Twilio sends <c>stop</c>, the browser disconnects, or the Gemini transport
/// faults, the WS is closed and the underlying <see cref="VoiceSession"/> is disposed so the
/// Gemini Live socket releases.
/// </para>
/// </remarks>
public sealed class TwilioMediaSocketHandler
{
    private const int InboundBufferBytes = 64 * 1024;
    private const string CustomParamFrom = "from";
    private const string CustomParamStir = "stirVerstat";
    private const string CustomParamTier = "tier";
    private const string CustomParamDowngraded = "tierDowngraded";

    private readonly IPhoneTransportFactory _transportFactory;
    private readonly CallTracker _tracker;
    private readonly CallMetadataStore _metadataStore;
    private readonly IOptions<PhoneOptions> _options;
    private readonly ILogger<TwilioMediaSocketHandler> _logger;
    private readonly PhoneSkillBridge? _skillBridge;

    /// <summary>Creates a new handler.</summary>
    /// <param name="transportFactory">Factory the handler hands to every <see cref="VoiceSession"/>.</param>
    /// <param name="tracker">Active-call counter — surfaced through <c>/healthz</c>.</param>
    /// <param name="metadataStore">Persistence sink for per-call metadata.</param>
    /// <param name="options">Bound phone options (Gemini key, model, voice, tier policy).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="skillBridge">
    /// Optional skill-tool bridge. When supplied (and non-empty), every call advertises the
    /// bridge's <see cref="PhoneSkillBridge.Tools"/> on the realtime session config and invokes
    /// <see cref="PhoneSkillBridge.RegisterWithSession"/> before <see cref="VoiceSession.ConnectAsync"/>.
    /// Without a bridge the phone path behaves exactly as before — no skill tools advertised.
    /// Matches the upstream <c>conversation-server.ts:587</c> behaviour of mirroring the voice
    /// agent's inline tools into the phone session.
    /// </param>
    public TwilioMediaSocketHandler(
        IPhoneTransportFactory transportFactory,
        CallTracker tracker,
        CallMetadataStore metadataStore,
        IOptions<PhoneOptions> options,
        ILogger<TwilioMediaSocketHandler> logger,
        PhoneSkillBridge? skillBridge = null)
    {
        ArgumentNullException.ThrowIfNull(transportFactory);
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentNullException.ThrowIfNull(metadataStore);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _transportFactory = transportFactory;
        _tracker = tracker;
        _metadataStore = metadataStore;
        _options = options;
        _logger = logger;
        _skillBridge = skillBridge;
    }

    /// <summary>
    /// Run the bridge until the WebSocket closes. Returns when both pumps have stopped and
    /// the <see cref="VoiceSession"/> has been disposed.
    /// </summary>
    /// <param name="socket">The accepted Twilio Media Streams WebSocket.</param>
    /// <param name="ct">Cancellation token tied to the request lifetime.</param>
    public async Task HandleAsync(WebSocket socket, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(socket);

        _tracker.Increment();
        using var sendGate = new SemaphoreSlim(1, 1);

        // Mutable per-call state that the inbound pump fills in as Twilio's start envelope arrives.
        CallState? state = null;
        VoiceSession? session = null;
        EventHandler<RealtimeServerEvent>? handler = null;
        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            var opts = _options.Value;
            if (string.IsNullOrWhiteSpace(opts.ApiKey))
            {
                _logger.LogError("Refusing Twilio Media Streams upgrade — neither GEMINI_VOICE_API_KEY nor GEMINI_API_KEY is set.");
                await TryCloseAsync(socket, WebSocketCloseStatus.PolicyViolation, "missing api key", ct).ConfigureAwait(false);
                return;
            }

            // ownsClient: true — the factory hands out a fresh IRealtimeClient per call, and
            // tearing it down at the end of HandleAsync mirrors the original transport-per-
            // connection lifecycle (and keeps the in-process test fakes ergonomic).
            session = new VoiceSession(client: _transportFactory.Create(), ownsClient: true);

            // Skill-tool wiring — only when a PhoneSkillBridge is in the DI container. Mirrors the
            // voice-side flow (VoiceWebSocketHandler.HandleAsync); tools must be registered before
            // ConnectAsync so the model sees them on the setup envelope.
            var skillTools = _skillBridge?.Tools;
            if (_skillBridge is not null && skillTools is { Count: > 0 })
            {
                _skillBridge.RegisterWithSession(session);
            }

            // SystemInstruction + voice are bound once on the session — there's no per-tier
            // diff in this slice. Unverified tier still gets the full voice but its session is
            // forcibly torn down after PhoneOptions.UnverifiedSessionTimeoutSeconds.
            var config = new RealtimeSessionConfig(
                Model: opts.Model,
                ApiKey: opts.ApiKey,
                VoiceName: opts.VoiceName,
                SystemInstruction: opts.SystemInstruction,
                Tools: skillTools);

            // Outbound (Gemini → Twilio) pump — fired off EventReceived as in Sutando.Voice.
            handler = (_, evt) =>
            {
                if (state is null)
                {
                    // We can't send media before Twilio sent its start envelope (we don't have a
                    // streamSid yet). Drop early audio chunks.
                    return;
                }
                _ = ForwardEventAsync(socket, sendGate, state, evt, ct);
            };
            session.EventReceived += handler;

            try
            {
                await session.ConnectAsync(config, ct).ConfigureAwait(false);
                state = await PumpInboundAsync(socket, session, sendGate, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected on host shutdown / call ended
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Twilio Media Streams handler failed.");
            }
        }
        finally
        {
            if (session is not null)
            {
                if (handler is not null)
                {
                    session.EventReceived -= handler;
                }
                try
                {
                    await session.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // best-effort
                }
                await session.DisposeAsync().ConfigureAwait(false);
            }

            if (state is not null)
            {
                var endedAt = DateTimeOffset.UtcNow;
                var duration = (long)(endedAt - startedAt).TotalMilliseconds;
                _metadataStore.Save(state.Record with
                {
                    EndedAt = endedAt,
                    DurationMs = duration,
                    ToolCalls = state.ToolCalls,
                });
            }

            await TryCloseAsync(socket, WebSocketCloseStatus.NormalClosure, "call ended", CancellationToken.None).ConfigureAwait(false);
            _tracker.Decrement();
        }
    }

    // ---- inbound: Twilio → Gemini ----

    private async Task<CallState?> PumpInboundAsync(
        WebSocket socket,
        VoiceSession session,
        SemaphoreSlim sendGate,
        CancellationToken ct)
    {
        var pool = ArrayPool<byte>.Shared;
        var buffer = pool.Rent(InboundBufferBytes);
        CallState? state = null;

        // Linked CTS so the unverified-tier timeout cap can fire independently of any other
        // cancellation source — disposed at end of pump regardless of which token fired first.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            using var ms = new MemoryStream(capacity: InboundBufferBytes);
            while (socket.State == WebSocketState.Open && !linked.Token.IsCancellationRequested)
            {
                ms.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), linked.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return state;
                    }
                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text || ms.Length == 0)
                {
                    continue;
                }

                state = await DispatchTwilioEnvelopeAsync(socket, session, sendGate, ms.GetBuffer().AsMemory(0, (int)ms.Length), state, linked, ct).ConfigureAwait(false);
                // Returning null means "stop" — the inbound pump exits and the finally block tears
                // the session down.
                if (state is { Stopped: true })
                {
                    return state;
                }
            }
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            // Twilio dropped the WS without a clean handshake — common when the caller hangs up. Treat as a graceful exit.
        }
        finally
        {
            pool.Return(buffer);
        }
        return state;
    }

    private async Task<CallState?> DispatchTwilioEnvelopeAsync(
        WebSocket socket,
        VoiceSession session,
        SemaphoreSlim sendGate,
        ReadOnlyMemory<byte> json,
        CallState? state,
        CancellationTokenSource linked,
        CancellationToken ct)
    {
        TwilioMediaEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<TwilioMediaEnvelope>(json.Span, PhoneJson.Options);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Discarding malformed Twilio Media envelope.");
            return state;
        }
        if (envelope is null || string.IsNullOrEmpty(envelope.Event))
        {
            return state;
        }

        switch (envelope.Event)
        {
            case "connected":
                // Setup acknowledgement; nothing to do.
                _logger.LogDebug("Twilio Media stream connected.");
                return state;

            case "start":
                if (envelope.Start is null || string.IsNullOrEmpty(envelope.StreamSid))
                {
                    _logger.LogWarning("Twilio start envelope missing streamSid; ignoring.");
                    return state;
                }
                state = BuildInitialState(envelope.Start, envelope.StreamSid);
                _metadataStore.Save(state.Record);
                _logger.LogInformation(
                    "Twilio call {CallSid} started — tier={Tier} downgraded={Downgraded} from={From} stir={Stir}",
                    state.Record.CallSid, state.Record.Tier, state.Record.TierDowngraded,
                    state.Record.From, state.Record.StirAttestation ?? "(none)");
                // Apply the unverified time-cap — fires AbortAsync on the linked CTS so both
                // pumps see cancellation simultaneously.
                if (state.Record.Tier == AccessTier.Unverified)
                {
                    var timeoutMs = Math.Max(1, _options.Value.UnverifiedSessionTimeoutSeconds) * 1000;
                    linked.CancelAfter(timeoutMs);
                    _logger.LogInformation(
                        "Unverified caller — call {CallSid} hard-capped at {TimeoutMs} ms.",
                        state.Record.CallSid, timeoutMs);
                }
                return state;

            case "media":
                if (state is null || envelope.Media is null || string.IsNullOrEmpty(envelope.Media.Payload))
                {
                    return state;
                }
                // Twilio sends the inbound caller track by default; outbound track is what we
                // ourselves sent back. We only forward inbound audio to Gemini.
                if (envelope.Media.Track is not null
                    && !string.Equals(envelope.Media.Track, "inbound", StringComparison.OrdinalIgnoreCase))
                {
                    return state;
                }
                byte[] muLaw;
                try
                {
                    muLaw = Convert.FromBase64String(envelope.Media.Payload);
                }
                catch (FormatException ex)
                {
                    _logger.LogWarning(ex, "Discarding Twilio media frame with invalid base64.");
                    return state;
                }
                var pcm8k = MuLawCodec.DecodeBlock(muLaw);
                var pcm16k = AudioResampler.Upsample8To16(pcm8k);
                try
                {
                    await session.SendAsync(RealtimeInput.Audio(pcm16k), ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Failed to forward inbound audio frame to Gemini.");
                }
                return state;

            case "stop":
                _logger.LogInformation("Twilio call {CallSid} stop received.", state?.Record.CallSid ?? "(unknown)");
                return state is null ? new CallState(InitialPlaceholderRecord(), string.Empty) { Stopped = true } : state with { Stopped = true };

            default:
                // mark / dtmf / unknown — log once at debug and move on.
                _logger.LogDebug("Ignoring Twilio envelope of type {Event}.", envelope.Event);
                return state;
        }
    }

    private CallState BuildInitialState(TwilioMediaStart start, string streamSid)
    {
        var opts = _options.Value;

        var customParams = start.CustomParameters ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var from = customParams.GetValueOrDefault(CustomParamFrom) ?? string.Empty;
        var stir = customParams.GetValueOrDefault(CustomParamStir);
        var tierString = customParams.GetValueOrDefault(CustomParamTier);
        var downgradedString = customParams.GetValueOrDefault(CustomParamDowngraded);

        // Parse tier from the start envelope's custom params — the webhook already resolved it
        // and embedded it on the <Parameter> nodes the TwiML emitted. Fall back to recomputing
        // here if the webhook didn't pass tier explicitly (defensive — in production every
        // start envelope ships with tier).
        AccessTier tier;
        bool downgraded;
        if (Enum.TryParse(tierString, ignoreCase: true, out AccessTier parsed))
        {
            tier = parsed;
            downgraded = string.Equals(downgradedString, "true", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            tier = PhoneTierResolver.Resolve(from, stir, opts, out downgraded);
        }

        if (downgraded)
        {
            _logger.LogWarning(
                "STIR/SHAKEN downgrade — owner number {From} arrived with stirVerstat={Stir}; dropping to verified.",
                from, stir ?? "(none)");
        }

        var record = new CallMetadataRecord(
            CallSid: start.CallSid ?? streamSid,
            From: from,
            To: opts.PhoneNumber,
            Direction: "inbound",
            Tier: tier,
            StirAttestation: string.IsNullOrEmpty(stir) ? null : stir,
            TierDowngraded: downgraded,
            StartedAt: DateTimeOffset.UtcNow);

        return new CallState(record, streamSid);
    }

    private static CallMetadataRecord InitialPlaceholderRecord() => new(
        CallSid: "unknown",
        From: string.Empty,
        To: string.Empty,
        Direction: "inbound",
        Tier: AccessTier.Unverified,
        StirAttestation: null,
        TierDowngraded: false,
        StartedAt: DateTimeOffset.UtcNow);

    // ---- outbound: Gemini → Twilio ----

    private async Task ForwardEventAsync(WebSocket socket, SemaphoreSlim sendGate, CallState state, RealtimeServerEvent evt, CancellationToken ct)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }

        // Track tool calls in the metadata record so the dashboard can render them.
        if (evt is RealtimeToolCall tc)
        {
            foreach (var call in tc.Calls)
            {
                var argsRaw = call.Arguments.ValueKind == JsonValueKind.Undefined
                    ? string.Empty
                    : call.Arguments.GetRawText();
                state.ToolCalls = state.ToolCalls.Append(new CallToolCall(
                    Name: call.Name,
                    At: DateTimeOffset.UtcNow,
                    ArgumentsPreview: Truncate(argsRaw, 240))).ToList();
            }
            return;
        }

        if (evt is not RealtimeAudioOutput audio)
        {
            // Other events (transcription, turn-complete) aren't surfaced to the carrier — log
            // them at debug for traceability.
            _logger.LogDebug("Phone-side ignored event {Type}.", evt.GetType().Name);
            return;
        }

        // 24 kHz PCM (Gemini's output rate) → 8 kHz PCM → μ-law → base64.
        var pcm8k = AudioResampler.Downsample24To8(audio.Pcm.Span);
        if (pcm8k.Length == 0)
        {
            return;
        }
        var muLaw = MuLawCodec.EncodeBlock(pcm8k);
        var base64 = Convert.ToBase64String(muLaw);
        var envelope = new TwilioOutboundMedia(state.StreamSid, new TwilioOutboundPayload(base64));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, PhoneJson.Options);

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
            _logger.LogDebug(ex, "Twilio Media Streams write failed — likely Twilio disconnect.");
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

    private static string Truncate(string input, int max)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }
        return input.Length <= max ? input : input[..max];
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

    /// <summary>
    /// Mutable container threaded through the inbound pump. <see cref="ToolCalls"/> is a
    /// reference-stable list because the outbound pump mutates it in-place on every tool
    /// invocation; the record is only re-saved on call-end so the metadata file always has the
    /// latest list.
    /// </summary>
    private sealed record CallState(CallMetadataRecord Record, string StreamSid)
    {
        /// <summary>Tool calls captured during the session.</summary>
        public List<CallToolCall> ToolCalls { get; set; } = new();

        /// <summary>True after Twilio sent <c>stop</c>; signals the outer loop to drop the call.</summary>
        public bool Stopped { get; init; }
    }
}
