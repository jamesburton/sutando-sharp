using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using GenerativeAI;
using GenerativeAI.Live;
using GenerativeAI.Live.Extensions;
using GenerativeAI.Types;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sutando.Realtime;

/// <summary>
/// Gemini Live implementation of MEAI's <see cref="IRealtimeClientSession"/>. Thin adapter over
/// the <c>Google_GenerativeAI.Live</c> NuGet package (<c>MultiModalLiveClient</c>) that exposes
/// the connection as a send/receive-stream session in MEAI shape.
/// </summary>
/// <remarks>
/// <para>
/// The connection is established lazily on first enumeration of
/// <see cref="GetStreamingResponseAsync"/> — callers can hold off attaching event handlers /
/// initial sends until the stream is being read. <see cref="SendAsync"/> queues messages and
/// auto-establishes the connection on first call too. Either entry point is safe.
/// </para>
/// <para>
/// The SDK fires WebSocket events on background threads. This class funnels every inbound event
/// through a single-producer/single-consumer <see cref="Channel{T}"/> so the consumer's
/// <c>await foreach</c> loop sees them serialised — deterministic ordering, no concurrent
/// handler invocations.
/// </para>
/// <para>
/// We deliberately leave the SDK's <c>FunctionTools</c> collection empty. The SDK will auto-
/// execute tool calls when <c>FunctionTools</c> is populated; the MEAI-level contract is "emit
/// a <see cref="ResponseOutputItemRealtimeServerMessage"/> and let the consumer (or the
/// <c>FunctionInvokingRealtimeClientSession</c> middleware) decide", so manual dispatch from
/// <c>MessageReceived → BidiResponsePayload.ToolCall</c> is what we want.
/// </para>
/// </remarks>
public sealed class GeminiLiveRealtimeClientSession : IRealtimeClientSession
{
    private readonly RealtimeSessionConfig _config;
    private readonly string _apiKey;
    private readonly ILogger<GeminiLiveRealtimeClientSession> _logger;
    private readonly Channel<RealtimeServerMessage> _messages = Channel.CreateUnbounded<RealtimeServerMessage>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    private readonly SemaphoreSlim _connectGate = new(1, 1);

    private MultiModalLiveClient? _client;
    private RealtimeCloseInitiator _closeInitiator = RealtimeCloseInitiator.Unexpected;
    private int _channelClosed; // 0 = open, 1 = closed; flipped via Interlocked
    private bool _connectInitiated;
    private bool _disposed;

    /// <summary>Creates a new session. Normally called by <see cref="GeminiLiveRealtimeClient.CreateSessionAsync"/>.</summary>
    /// <param name="options">MEAI session options. Stored on <see cref="Options"/>.</param>
    /// <param name="config">Sutando-side config carrying the Gemini extensions (resumption handle, transcription flags).</param>
    /// <param name="apiKey">The resolved API key — already picked by the client between its default and the per-session override.</param>
    /// <param name="logger">Logger.</param>
    public GeminiLiveRealtimeClientSession(
        RealtimeSessionOptions options,
        RealtimeSessionConfig config,
        string apiKey,
        ILogger<GeminiLiveRealtimeClientSession>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrEmpty(apiKey))
        {
            throw new ArgumentException("API key is required.", nameof(apiKey));
        }
        Options = options;
        _config = config;
        _apiKey = apiKey;
        _logger = logger ?? NullLogger<GeminiLiveRealtimeClientSession>.Instance;
    }

    /// <inheritdoc />
    public RealtimeSessionOptions? Options { get; }

    /// <summary>Sutando-side config the session was opened against. Exposes the Gemini-specific extension fields (resumption handle, transcription flags).</summary>
    public RealtimeSessionConfig SutandoConfig => _config;

    /// <inheritdoc />
    public async Task SendAsync(RealtimeClientMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var client = _client!;

        switch (message)
        {
            case InputAudioBufferAppendRealtimeClientMessage audio:
            {
                var content = audio.Content;
                var mime = string.IsNullOrEmpty(content.MediaType)
                    ? _config.EffectiveAudio.InputMimeType
                    : content.MediaType;
                var bytes = content.Data.IsEmpty ? Array.Empty<byte>() : content.Data.ToArray();
                await client.SendAudioAsync(bytes, mime, cancellationToken).ConfigureAwait(false);
                return;
            }
            case CreateConversationItemRealtimeClientMessage create:
            {
                await SendConversationItemAsync(client, create.Item, cancellationToken).ConfigureAwait(false);
                return;
            }
            default:
                throw new NotSupportedException(
                    $"Gemini Live realtime adapter does not support client message type '{message.GetType().FullName}'.");
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RealtimeServerMessage> GetStreamingResponseAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        await foreach (var msg in _messages.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return msg;
        }
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is null && serviceType.IsAssignableFrom(GetType()))
        {
            return this;
        }
        return null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        await DisconnectAsync().ConfigureAwait(false);
        CompleteChannel();
        _client?.Dispose();
        _client = null;
        _connectGate.Dispose();
    }

    // --- connection lifecycle -------------------------------------------

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_connectInitiated)
        {
            return;
        }

        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_connectInitiated)
            {
                return;
            }

            _closeInitiator = RealtimeCloseInitiator.Unexpected;

            var googleAi = new GoogleAi(_apiKey);
            var model = googleAi.CreateGenerativeModel(_config.Model);

            var setup = BuildSetup(_config);

            var client = model.CreateMultiModalLiveClient(
                config: setup.GenerationConfig,
                safetySettings: null,
                systemInstruction: _config.SystemInstruction,
                logger: _logger);

            // Wire event handlers BEFORE connecting so we don't miss frames the server emits
            // immediately after setup-complete.
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
            _connectInitiated = true;

            // autoSendSetup=false because we want full control over the setup payload.
            await client.ConnectAsync(autoSendSetup: false, cancellationToken: ct).ConfigureAwait(false);
            await client.SendSetupAsync(setup, ct).ConfigureAwait(false);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task DisconnectAsync()
    {
        if (_client is null)
        {
            return;
        }

        _closeInitiator = RealtimeCloseInitiator.Client;
        try
        {
            await _client.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Exception during DisconnectAsync — swallowed; we're tearing down anyway.");
        }
    }

    private static async Task SendConversationItemAsync(MultiModalLiveClient client, RealtimeConversationItem item, CancellationToken ct)
    {
        // The MEAI conversation-item shape is provider-agnostic. For Gemini we currently support
        // two flavours: a text turn (TextContent in Contents) or a tool result (FunctionResultContent).
        // Other content kinds — images, structured data — would slot in here as needed.
        foreach (var content in item.Contents)
        {
            switch (content)
            {
                case TextContent text:
                    await client.SentTextAsync(text.Text, ct).ConfigureAwait(false);
                    break;
                case FunctionResultContent result:
                {
                    var payload = BuildToolResponsePayload(result);
                    await client.SendToolResponseAsync(payload, ct).ConfigureAwait(false);
                    break;
                }
                default:
                    throw new NotSupportedException(
                        $"Gemini Live realtime adapter does not support AIContent type '{content.GetType().FullName}' yet.");
            }
        }
    }

    private static BidiGenerateContentToolResponse BuildToolResponsePayload(FunctionResultContent result)
    {
        // FunctionResultContent.Result is object? — serialize to a JSON node before handing it
        // to Gemini, which expects a JSON object on the wire. Null becomes an empty object.
        JsonNode responseNode;
        if (result.Result is null)
        {
            responseNode = new JsonObject();
        }
        else if (result.Result is JsonElement json)
        {
            responseNode = JsonNode.Parse(json.GetRawText()) ?? new JsonObject();
        }
        else
        {
            responseNode = JsonSerializer.SerializeToNode(result.Result) ?? new JsonObject();
        }
        if (responseNode is not JsonObject jsonObject)
        {
            // Gemini's wire shape wraps non-object results so the model gets a recognisable
            // structure back. Pick a conventional key — { "result": <whatever> }.
            jsonObject = new JsonObject { ["result"] = responseNode };
        }

        return new BidiGenerateContentToolResponse
        {
            FunctionResponses = new[]
            {
                new FunctionResponse
                {
                    Id = result.CallId,
                    Name = result.AdditionalProperties?.GetValueOrDefault("name") as string ?? string.Empty,
                    Response = jsonObject,
                },
            },
        };
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

    private void TryPublish(RealtimeServerMessage message)
    {
        if (_channelClosed != 0)
        {
            return;
        }

        if (!_messages.Writer.TryWrite(message))
        {
            // Unbounded channel — TryWrite only fails after completion. Race with CompleteChannel.
            _logger.LogTrace("Dropping message {Type} — channel already completed.", message.Type);
        }
    }

    private void CompleteChannel()
    {
        if (Interlocked.Exchange(ref _channelClosed, 1) == 0)
        {
            _messages.Writer.TryComplete();
        }
    }

    // --- SDK event handlers ---------------------------------------------

    private void OnMessageReceived(object? sender, MessageReceivedEventArgs e)
    {
        foreach (var msg in GeminiPayloadMapper.MapToMeai(e.Payload))
        {
            // Skip GoAway / SessionResumptionUpdate here — handled by the dedicated SDK events to
            // keep type-routing and `_closeInitiator` bookkeeping in one place.
            if (msg.Type == SutandoRealtimeMessageTypes.GoAway || msg.Type == SutandoRealtimeMessageTypes.SessionResumptionUpdate)
            {
                continue;
            }
            TryPublish(msg);
        }
    }

    private void OnAudioChunkReceived(object? sender, AudioBufferReceivedEventArgs e)
    {
        // The MEAI shape is base64 string on OutputTextAudioRealtimeServerMessage.Audio. We pay
        // the encode here so downstream consumers see a uniform wire-shape; the adapter
        // (MeaiToSutandoEventAdapter) decodes it back to bytes when surfacing
        // RealtimeAudioOutput to the consumer. The double-trip is unfortunate but it keeps
        // GeminiLiveRealtimeClientSession honest as an IRealtimeClientSession — middleware
        // (e.g. FunctionInvokingRealtimeClientSession) that wants to inspect the audio frame
        // sees it in MEAI's canonical shape.
        var base64 = e.Buffer is { Length: > 0 } buf ? Convert.ToBase64String(buf) : null;
        TryPublish(new OutputTextAudioRealtimeServerMessage(RealtimeServerMessageType.OutputAudioDelta)
        {
            Audio = base64,
        });
    }

    private void OnInputTranscription(object? sender, Transcription t)
    {
        var finished = t.Finished ?? false;
        TryPublish(new OutputTextAudioRealtimeServerMessage(
            finished ? RealtimeServerMessageType.InputAudioTranscriptionCompleted : RealtimeServerMessageType.InputAudioTranscriptionDelta)
        {
            Text = t.Text ?? string.Empty,
        });
    }

    private void OnOutputTranscription(object? sender, Transcription t)
    {
        var finished = t.Finished ?? false;
        TryPublish(new OutputTextAudioRealtimeServerMessage(
            finished ? RealtimeServerMessageType.OutputAudioTranscriptionDone : RealtimeServerMessageType.OutputAudioTranscriptionDelta)
        {
            Text = t.Text ?? string.Empty,
        });
    }

    private void OnGenerationInterrupted(object? sender, EventArgs e)
    {
        // Gemini's "interrupted" maps to MEAI's "response cancelled". Synthesise a ResponseDone
        // with status=Cancelled so middleware that watches the response lifecycle still sees it.
        TryPublish(new ResponseCreatedRealtimeServerMessage(RealtimeServerMessageType.ResponseDone)
        {
            Status = RealtimeResponseStatus.Cancelled,
        });
    }

    private void OnGoAwayReceived(object? sender, LiveServerGoAway goAway)
    {
        TryPublish(new RealtimeServerMessage
        {
            Type = SutandoRealtimeMessageTypes.GoAway,
            RawRepresentation = new RealtimeGoAway(
                ErrorMessage: goAway.ErrorMessage,
                ErrorCode: goAway.ErrorCode,
                Reconnect: goAway.Reconnect ?? false,
                RetryAfter: goAway.RetryAfterSeconds is int secs ? TimeSpan.FromSeconds(secs) : null),
        });
        // A goAway implies the server is about to close — mark the initiator so the eventual
        // Disconnected event is attributed correctly.
        _closeInitiator = RealtimeCloseInitiator.Server;
    }

    private void OnSessionResumableUpdate(object? sender, LiveServerSessionResumptionUpdate update)
    {
        var resumable = update.Status is not SessionResumptionStatus.FAILED;
        TryPublish(new RealtimeServerMessage
        {
            Type = SutandoRealtimeMessageTypes.SessionResumptionUpdate,
            RawRepresentation = new RealtimeSessionResumptionUpdate(update.ResumptionToken ?? string.Empty, resumable),
        });
    }

    private void OnSdkDisconnected(object? sender, EventArgs e)
    {
        TryPublish(new RealtimeServerMessage
        {
            Type = SutandoRealtimeMessageTypes.SessionClosed,
            RawRepresentation = _closeInitiator,
        });
        CompleteChannel();
    }

    private void OnSdkError(object? sender, System.IO.ErrorEventArgs e)
    {
        TryPublish(new ErrorRealtimeServerMessage
        {
            Error = new ErrorContent(e.GetException()?.Message ?? "Unknown transport error."),
        });
    }
}
