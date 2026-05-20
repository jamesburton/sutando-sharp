using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Sutando.Realtime;

namespace Sutando.Tests.Realtime;

/// <summary>
/// In-process fake of MEAI's <see cref="IRealtimeClient"/> + <see cref="IRealtimeClientSession"/>
/// pair. Tests pump <see cref="RealtimeServerEvent"/>s in via <see cref="FakeRealtimeClientSession.Emit"/>
/// and assert on the resulting <see cref="VoiceSession.State"/> transitions and the outbound
/// calls (<see cref="FakeRealtimeClientSession.SentInputs"/>, <see cref="FakeRealtimeClientSession.SentToolResponses"/>).
/// </summary>
/// <remarks>
/// Replaces the retired <c>IRealtimeTransport</c> fake. The shape is similar — a single channel
/// of inbound events plus two recording lists for outbound — but the layering matches MEAI's
/// client/session split. The client is a thin factory that captures every session it mints;
/// session-level state lives on <see cref="FakeRealtimeClientSession"/>.
/// </remarks>
internal sealed class FakeRealtimeClient : IRealtimeClient
{
    private readonly object _gate = new();
    private FakeRealtimeClientSession? _latest;

    public bool DisposeCalled { get; private set; }

    /// <summary>The session created on the most recent <see cref="CreateSessionAsync"/> call.</summary>
    public FakeRealtimeClientSession? LatestSession
    {
        get
        {
            lock (_gate)
            {
                return _latest;
            }
        }
    }

    public Task<IRealtimeClientSession> CreateSessionAsync(RealtimeSessionOptions? options = null, CancellationToken cancellationToken = default)
    {
        var session = new FakeRealtimeClientSession(options);
        lock (_gate)
        {
            _latest = session;
        }
        return Task.FromResult<IRealtimeClientSession>(session);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
        DisposeCalled = true;
    }
}

/// <summary>
/// In-process fake <see cref="IRealtimeClientSession"/>. Tests <see cref="Emit"/> Sutando-shaped
/// <see cref="RealtimeServerEvent"/>s — the fake translates them to MEAI's
/// <see cref="RealtimeServerMessage"/> shape internally so the production
/// <see cref="MeaiToSutandoEventAdapter"/> path still runs and is covered by every existing test.
/// </summary>
internal sealed class FakeRealtimeClientSession : IRealtimeClientSession
{
    private readonly Channel<RealtimeServerMessage> _messages = Channel.CreateUnbounded<RealtimeServerMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public FakeRealtimeClientSession(RealtimeSessionOptions? options)
    {
        Options = options;
        SutandoConfig = options?.RawRepresentationFactory?.Invoke() as RealtimeSessionConfig;
    }

    /// <summary>MEAI options the session was created with.</summary>
    public RealtimeSessionOptions? Options { get; }

    /// <summary>The Sutando-side config, when one was attached via <see cref="RealtimeSessionOptions.RawRepresentationFactory"/>.</summary>
    public RealtimeSessionConfig? SutandoConfig { get; }

    /// <summary>Convenience accessor mirroring the legacy fake transport's <c>LastConfig</c>.</summary>
    public RealtimeSessionConfig? LastConfig => SutandoConfig;

    public List<RealtimeInput> SentInputs { get; } = new();

    public List<ToolResponse> SentToolResponses { get; } = new();

    public bool DisconnectCalled { get; private set; }

    public Task SendAsync(RealtimeClientMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        switch (message)
        {
            case InputAudioBufferAppendRealtimeClientMessage audio:
                SentInputs.Add(RealtimeInput.Audio(audio.Content.Data, audio.Content.MediaType));
                break;
            case CreateConversationItemRealtimeClientMessage create:
            {
                foreach (var content in create.Item.Contents)
                {
                    switch (content)
                    {
                        case TextContent text:
                            SentInputs.Add(RealtimeInput.Text(text.Text));
                            break;
                        case FunctionResultContent result:
                        {
                            var name = result.AdditionalProperties?.GetValueOrDefault("name") as string ?? string.Empty;
                            // Reverse the wrap-around BuildToolResponsePayload performs — the JsonElement
                            // we recorded as Response should be the raw object the tool handler produced.
                            JsonElement element = result.Result is JsonElement je
                                ? je
                                : (result.Result is null
                                    ? JsonDocument.Parse("{}").RootElement
                                    : JsonSerializer.SerializeToElement(result.Result));
                            SentToolResponses.Add(new ToolResponse(result.CallId, name, element));
                            break;
                        }
                        default:
                            throw new NotSupportedException(
                                $"FakeRealtimeClientSession does not record AIContent type '{content.GetType().FullName}'.");
                    }
                }
                break;
            }
            default:
                throw new NotSupportedException(
                    $"FakeRealtimeClientSession does not record client message type '{message.GetType().FullName}'.");
        }
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<RealtimeServerMessage> GetStreamingResponseAsync(CancellationToken cancellationToken = default)
        => _messages.Reader.ReadAllAsync(cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public ValueTask DisposeAsync()
    {
        DisconnectCalled = true;
        _messages.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Pump a Sutando-shaped event into the fake. Translated to its MEAI equivalent at the
    /// boundary so the read loop exercises the real <see cref="MeaiToSutandoEventAdapter"/>.
    /// </summary>
    public void Emit(RealtimeServerEvent evt)
    {
        var msg = SutandoToMeai(evt);
        if (!_messages.Writer.TryWrite(msg))
        {
            throw new InvalidOperationException("Channel already completed — cannot emit further events.");
        }
    }

    public void Complete() => _messages.Writer.TryComplete();

    private static RealtimeServerMessage SutandoToMeai(RealtimeServerEvent evt) => evt switch
    {
        RealtimeSetupComplete => new RealtimeServerMessage { Type = SutandoRealtimeMessageTypes.SessionStarted },
        RealtimeAudioOutput audio => new OutputTextAudioRealtimeServerMessage(RealtimeServerMessageType.OutputAudioDelta)
        {
            Audio = audio.Pcm.IsEmpty ? null : Convert.ToBase64String(audio.Pcm.Span),
        },
        RealtimeInputTranscription t => new OutputTextAudioRealtimeServerMessage(
            t.Finished ? RealtimeServerMessageType.InputAudioTranscriptionCompleted : RealtimeServerMessageType.InputAudioTranscriptionDelta)
        {
            Text = t.Text,
        },
        RealtimeOutputTranscription t => new OutputTextAudioRealtimeServerMessage(
            t.Finished ? RealtimeServerMessageType.OutputAudioTranscriptionDone : RealtimeServerMessageType.OutputAudioTranscriptionDelta)
        {
            Text = t.Text,
        },
        RealtimeInterrupted => new ResponseCreatedRealtimeServerMessage(RealtimeServerMessageType.ResponseDone)
        {
            Status = RealtimeResponseStatus.Cancelled,
        },
        RealtimeTurnComplete => new ResponseCreatedRealtimeServerMessage(RealtimeServerMessageType.ResponseDone)
        {
            Status = RealtimeResponseStatus.Completed,
        },
        RealtimeToolCall tc => new ResponseOutputItemRealtimeServerMessage(RealtimeServerMessageType.ResponseOutputItemDone)
        {
            Item = new RealtimeConversationItem(
                tc.Calls.Select(c => (AIContent)new FunctionCallContent(
                    c.Id,
                    c.Name,
                    c.Arguments.ValueKind == JsonValueKind.Object
                        ? JsonSerializer.Deserialize<Dictionary<string, object?>>(c.Arguments.GetRawText())
                        : null)).ToList()),
        },
        RealtimeToolCallCancellation tcc => new RealtimeServerMessage
        {
            Type = SutandoRealtimeMessageTypes.ToolCallCancelled,
            RawRepresentation = tcc,
        },
        RealtimeGoAway ga => new RealtimeServerMessage
        {
            Type = SutandoRealtimeMessageTypes.GoAway,
            RawRepresentation = ga,
        },
        RealtimeSessionResumptionUpdate sru => new RealtimeServerMessage
        {
            Type = SutandoRealtimeMessageTypes.SessionResumptionUpdate,
            RawRepresentation = sru,
        },
        RealtimeGroundingMetadata gm => new RealtimeServerMessage
        {
            Type = SutandoRealtimeMessageTypes.GroundingMetadata,
            RawRepresentation = gm.MetadataJson,
        },
        RealtimeTransportClosed tc => new RealtimeServerMessage
        {
            Type = SutandoRealtimeMessageTypes.SessionClosed,
            RawRepresentation = tc.Initiator,
        },
        RealtimeTransportError err => new ErrorRealtimeServerMessage
        {
            Error = new ErrorContent(err.Message),
        },
        _ => throw new NotSupportedException($"FakeRealtimeClientSession cannot emit {evt.GetType().FullName}."),
    };
}
