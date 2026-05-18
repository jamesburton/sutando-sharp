using System.Text.Json.Nodes;
using System.Threading.Channels;
using GenerativeAI;
using GenerativeAI.Live;
using GenerativeAI.Live.Extensions;
using GenerativeAI.Types;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sutando.Realtime;

/// <summary>
/// Gemini Live implementation of <see cref="IRealtimeTransport"/>. Thin adapter over the
/// <c>Google_GenerativeAI.Live</c> NuGet package (<c>MultiModalLiveClient</c>).
/// </summary>
/// <remarks>
/// <para>
/// The SDK fires WebSocket events on background threads. This class funnels every inbound event
/// through a single-producer/single-consumer <see cref="Channel{T}"/> so the consumer's
/// <c>await foreach</c> loop sees them serialised — deterministic ordering, no concurrent handler
/// invocations.
/// </para>
/// <para>
/// We deliberately leave the SDK's <c>FunctionTools</c> collection empty. The SDK will
/// auto-execute tool calls (via a private <c>CallFunctionsWithErrorHandlingAsync</c>) when
/// <c>FunctionTools</c> is populated; the transport-level contract is "surface the
/// <see cref="RealtimeToolCall"/> event and let the consumer decide", so manual dispatch
/// from <c>MessageReceived → BidiResponsePayload.ToolCall</c> is what we want.
/// </para>
/// </remarks>
public sealed class GeminiLiveTransport : IRealtimeTransport
{
    private readonly ILogger<GeminiLiveTransport> _logger;
    private readonly Channel<RealtimeServerEvent> _events = Channel.CreateUnbounded<RealtimeServerEvent>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

    private MultiModalLiveClient? _client;
    private RealtimeSessionConfig? _config;
    private RealtimeCloseInitiator _closeInitiator = RealtimeCloseInitiator.Unexpected;
    private int _channelClosed; // 0 = open, 1 = closed; flipped via Interlocked
    private bool _disposed;

    /// <summary>Creates a new transport bound to the given logger (use <see cref="NullLogger{T}.Instance"/> when not logging).</summary>
    /// <param name="logger">Optional logger. Defaults to <see cref="NullLogger{T}.Instance"/> when null.</param>
    public GeminiLiveTransport(ILogger<GeminiLiveTransport>? logger = null)
    {
        _logger = logger ?? NullLogger<GeminiLiveTransport>.Instance;
    }

    /// <inheritdoc />
    public async Task ConnectAsync(RealtimeSessionConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_client is not null)
        {
            throw new InvalidOperationException("Transport is already connected. Create a new instance to reconnect.");
        }

        _config = config;
        _closeInitiator = RealtimeCloseInitiator.Unexpected;

        // GoogleAi is the SDK's IPlatformAdapter entry point. It owns auth + base URL but doesn't
        // open any sockets until MultiModalLiveClient.ConnectAsync is called.
        var googleAi = new GoogleAi(config.ApiKey);
        var model = googleAi.CreateGenerativeModel(config.Model);

        // We pre-build the setup envelope ourselves rather than letting the SDK fill in defaults,
        // so the on-the-wire payload is exactly what bodhi/voice-agent.ts uses.
        var setup = BuildSetup(config);

        // CreateMultiModalLiveClient is an extension method on GenerativeModel — it has access to
        // the protected platform adapter on GenAI that we cannot reach from here. The transcription
        // flags don't surface on this overload, but our hand-built setup payload already carries
        // them, so this is fine.
        var client = model.CreateMultiModalLiveClient(
            config: setup.GenerationConfig,
            safetySettings: null,
            systemInstruction: config.SystemInstruction,
            logger: _logger);

        // Wire events BEFORE connecting so we don't miss the very first frames the server emits
        // immediately after setup-complete (bodhi has scars from this — see comments in
        // gemini-live-transport.ts around line 568).
        client.MessageReceived += OnMessageReceived;
        client.AudioChunkReceived += OnAudioChunkReceived;
        client.InputTranscriptionReceived += OnInputTranscription;
        client.OutputTranscriptionReceived += OnOutputTranscription;
        client.GenerationInterrupted += OnGenerationInterrupted;
        client.GoAwayReceived += OnGoAwayReceived;
        client.SessionResumableUpdateReceived += OnSessionResumableUpdate;
        client.Disconnected += OnSdkDisconnected;
        client.ErrorOccurred += OnSdkError;

        _client = client;

        // autoSendSetup=false because we want full control over the setup payload. We send our
        // pre-built one immediately afterwards.
        await client.ConnectAsync(autoSendSetup: false, cancellationToken: ct).ConfigureAwait(false);
        await client.SendSetupAsync(setup, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SendRealtimeInputAsync(RealtimeInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var client = RequireConnected();
        var config = _config!;

        switch (input)
        {
            case RealtimeInput.RealtimeTextInput text:
                await client.SentTextAsync(text.Value, ct).ConfigureAwait(false);
                break;

            case RealtimeInput.RealtimeAudioInput audio:
                var mime = audio.MimeType ?? config.EffectiveAudio.InputMimeType;
                // SendAudioAsync wants byte[] — pay the copy here rather than restructuring the
                // SDK's signature. For a 20 ms audio frame at 16 kHz mono 16-bit = 640 bytes, this
                // is negligible.
                await client.SendAudioAsync(audio.Pcm.ToArray(), mime, ct).ConfigureAwait(false);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(input), input.GetType(), "Unknown RealtimeInput variant.");
        }
    }

    /// <inheritdoc />
    public async Task SendToolResponseAsync(ToolResponse response, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(response);
        var client = RequireConnected();

        var responseNode = JsonNode.Parse(response.Response.GetRawText()) as JsonObject
            ?? throw new ArgumentException("ToolResponse.Response must be a JSON object.", nameof(response));

        var payload = new BidiGenerateContentToolResponse
        {
            FunctionResponses = new[]
            {
                new FunctionResponse
                {
                    Id = response.ToolCallId,
                    Name = response.Name,
                    Response = responseNode,
                },
            },
        };
        await client.SendToolResponseAsync(payload, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<RealtimeServerEvent> ReadEventsAsync(CancellationToken ct)
        => _events.Reader.ReadAllAsync(ct);

    /// <inheritdoc />
    public async Task DisconnectAsync(CancellationToken ct)
    {
        if (_client is null)
        {
            return;
        }

        _closeInitiator = RealtimeCloseInitiator.Client;
        try
        {
            await _client.DisconnectAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Exception during DisconnectAsync — swallowed; we're tearing down anyway.");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        CompleteChannel();
        _client?.Dispose();
        _client = null;
    }

    // --- helpers ---------------------------------------------------------

    private MultiModalLiveClient RequireConnected()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _client ?? throw new InvalidOperationException("Transport is not connected. Call ConnectAsync first.");
    }

    private static BidiGenerateContentSetup BuildSetup(RealtimeSessionConfig config)
    {
        var generationConfig = new GenerationConfig
        {
            ResponseModalities = new List<Modality> { Modality.AUDIO },
        };

        if (!string.IsNullOrEmpty(config.VoiceName))
        {
            generationConfig.SpeechConfig = new SpeechConfig
            {
                VoiceConfig = new VoiceConfig
                {
                    PrebuiltVoiceConfig = new PrebuiltVoiceConfig
                    {
                        VoiceName = config.VoiceName,
                    },
                },
            };
        }

        var setup = new BidiGenerateContentSetup
        {
            Model = $"models/{config.Model}",
            GenerationConfig = generationConfig,
        };

        if (!string.IsNullOrEmpty(config.SystemInstruction))
        {
            // No Role — Gemini's wire format for systemInstruction is a Content object with parts
            // only. Setting Role = "user" makes Gemini treat it as a user turn instead of system
            // context, which silently corrupts the conversation start.
            setup.SystemInstruction = new Content
            {
                Parts = new List<Part>
                {
                    new() { Text = config.SystemInstruction },
                },
            };
        }

        if (config.EffectiveTools.Count > 0)
        {
            var declarations = new List<FunctionDeclaration>(config.EffectiveTools.Count);
            foreach (var tool in config.EffectiveTools)
            {
                var schema = JsonNode.Parse(tool.ParameterSchema.GetRawText())
                    ?? throw new InvalidOperationException(
                        $"Tool '{tool.Name}' has an invalid parameter schema (could not parse as JSON).");
                declarations.Add(new FunctionDeclaration
                {
                    Name = tool.Name,
                    Description = tool.Description,
                    ParametersJsonSchema = schema,
                });
            }
            setup.Tools = new[]
            {
                new Tool { FunctionDeclarations = declarations },
            };
        }

        if (config.EnableInputTranscription)
        {
            setup.InputAudioTranscription = new AudioTranscriptionConfig();
        }

        if (config.EnableOutputTranscription)
        {
            setup.OutputAudioTranscription = new AudioTranscriptionConfig();
        }

        if (!string.IsNullOrEmpty(config.ResumptionHandle))
        {
            setup.SessionResumption = new SessionResumptionConfig
            {
                Handle = config.ResumptionHandle,
            };
        }

        return setup;
    }

    private void TryPublish(RealtimeServerEvent evt)
    {
        if (_channelClosed != 0)
        {
            return;
        }

        if (!_events.Writer.TryWrite(evt))
        {
            // Unbounded channel — TryWrite only fails after completion. Race with CompleteChannel.
            _logger.LogTrace("Dropping event {Event} — channel already completed.", evt.GetType().Name);
        }
    }

    private void CompleteChannel()
    {
        if (Interlocked.Exchange(ref _channelClosed, 1) == 0)
        {
            _events.Writer.TryComplete();
        }
    }

    // --- SDK event handlers ---------------------------------------------

    private void OnMessageReceived(object? sender, MessageReceivedEventArgs e)
    {
        // The mapping is shared with GeminiPayloadMapper so unit tests can verify the
        // payload-shape contract without standing up a real WebSocket. Note that AudioChunkReceived
        // already fires for inlineData audio, so we don't re-emit it from modelTurn.parts inside
        // the mapper. GoAway / SessionResumptionUpdate are also delivered via their dedicated SDK
        // events; the mapper yields them too, but in production we suppress those branches at the
        // SDK-event boundary (the dedicated handlers below own them) — so any double-emit is harmless.
        foreach (var evt in GeminiPayloadMapper.Map(e.Payload))
        {
            // Skip GoAway / SessionResumptionUpdate here — handled by the dedicated SDK events to
            // keep type-routing and `_closeInitiator` bookkeeping in one place.
            if (evt is RealtimeGoAway or RealtimeSessionResumptionUpdate)
            {
                continue;
            }
            TryPublish(evt);
        }
    }

    private void OnAudioChunkReceived(object? sender, AudioBufferReceivedEventArgs e)
    {
        var info = e.HeaderInfo;
        TryPublish(new RealtimeAudioOutput(
            Pcm: e.Buffer,
            SampleRateHz: info?.SampleRate ?? _config?.EffectiveAudio.OutputSampleRateHz ?? 24_000,
            Channels: info?.Channels ?? _config?.EffectiveAudio.Channels ?? 1,
            BitsPerSample: info?.BitsPerSample ?? _config?.EffectiveAudio.BitsPerSample ?? 16));
    }

    private void OnInputTranscription(object? sender, Transcription t)
    {
        TryPublish(new RealtimeInputTranscription(t.Text ?? string.Empty, t.Finished ?? false));
    }

    private void OnOutputTranscription(object? sender, Transcription t)
    {
        TryPublish(new RealtimeOutputTranscription(t.Text ?? string.Empty, t.Finished ?? false));
    }

    private void OnGenerationInterrupted(object? sender, EventArgs e)
    {
        TryPublish(new RealtimeInterrupted());
    }

    private void OnGoAwayReceived(object? sender, LiveServerGoAway goAway)
    {
        TryPublish(new RealtimeGoAway(
            ErrorMessage: goAway.ErrorMessage,
            ErrorCode: goAway.ErrorCode,
            Reconnect: goAway.Reconnect ?? false,
            RetryAfter: goAway.RetryAfterSeconds is int secs ? TimeSpan.FromSeconds(secs) : null));
        // A goAway implies the server is about to close — mark the initiator so the eventual
        // Disconnected event is attributed correctly.
        _closeInitiator = RealtimeCloseInitiator.Server;
    }

    private void OnSessionResumableUpdate(object? sender, LiveServerSessionResumptionUpdate update)
    {
        // The wire-level message exposes both `newHandle` and `resumable`. The SDK surfaces the
        // handle as `ResumptionToken` and embeds the resumable flag in `Status`. Treat any non-FAILED
        // status as "resumable" — matches bodhi's behaviour, which fires the event on every update.
        var resumable = update.Status is not SessionResumptionStatus.FAILED;
        TryPublish(new RealtimeSessionResumptionUpdate(update.ResumptionToken ?? string.Empty, resumable));
    }

    private void OnSdkDisconnected(object? sender, EventArgs e)
    {
        TryPublish(new RealtimeTransportClosed(_closeInitiator));
        CompleteChannel();
    }

    private void OnSdkError(object? sender, System.IO.ErrorEventArgs e)
    {
        TryPublish(new RealtimeTransportError(e.GetException()?.Message ?? "Unknown transport error."));
    }
}
