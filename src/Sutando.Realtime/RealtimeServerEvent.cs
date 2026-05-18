using System.Text.Json;

namespace Sutando.Realtime;

/// <summary>
/// Base type for every event the transport surfaces back to the consumer. Concrete sealed subtypes
/// form a discriminated union — switch on the runtime type or use pattern matching to dispatch.
/// </summary>
/// <remarks>
/// The set mirrors the message types emitted on the Gemini Live BidiGenerateContent stream:
/// <c>setupComplete</c>, <c>serverContent</c> (split here into its constituent sub-events for
/// easier consumer code), <c>toolCall</c>, <c>toolCallCancellation</c>, <c>goAway</c>,
/// <c>sessionResumptionUpdate</c>, plus two purely-local lifecycle events (<see cref="RealtimeTransportClosed"/>
/// and <see cref="RealtimeTransportError"/>) so consumers can react to WS-level state without
/// reaching into the underlying SDK.
/// </remarks>
public abstract record RealtimeServerEvent
{
    private protected RealtimeServerEvent()
    {
    }
}

/// <summary>
/// Emitted exactly once per connection, after the server has accepted the setup envelope. This is the
/// signal that the session is ready to accept input — <see cref="VoiceSession"/> uses it to drive its
/// <c>Connecting → Listening</c> transition.
/// </summary>
public sealed record RealtimeSetupComplete : RealtimeServerEvent;

/// <summary>A chunk of model-generated audio output (24 kHz PCM).</summary>
/// <param name="Pcm">Raw 16-bit little-endian PCM samples.</param>
/// <param name="SampleRateHz">Sample rate of <paramref name="Pcm"/>. Typically 24000 for Gemini, but read off the wire.</param>
/// <param name="Channels">Channel count. Typically 1 (mono).</param>
/// <param name="BitsPerSample">Bit depth. Typically 16.</param>
public sealed record RealtimeAudioOutput(
    ReadOnlyMemory<byte> Pcm,
    int SampleRateHz,
    int Channels,
    int BitsPerSample) : RealtimeServerEvent;

/// <summary>A transcript of the user's audio input.</summary>
/// <param name="Text">The transcribed text fragment. Concatenate fragments across events to reconstruct the full turn.</param>
/// <param name="Finished">True if this is the last fragment for the current turn.</param>
public sealed record RealtimeInputTranscription(string Text, bool Finished) : RealtimeServerEvent;

/// <summary>A transcript of the model's spoken output, when output-audio transcription is enabled.</summary>
/// <param name="Text">The transcribed text fragment.</param>
/// <param name="Finished">True if this is the last fragment for the current turn.</param>
public sealed record RealtimeOutputTranscription(string Text, bool Finished) : RealtimeServerEvent;

/// <summary>
/// Emitted when the model detects that the user has barged in on the current spoken response. Consumers
/// playing audio out should flush their buffer immediately.
/// </summary>
public sealed record RealtimeInterrupted : RealtimeServerEvent;

/// <summary>
/// Emitted at the end of a model turn. Consumers can use this to flip the local state from <c>Speaking</c>
/// back to <c>Listening</c>.
/// </summary>
public sealed record RealtimeTurnComplete : RealtimeServerEvent;

/// <summary>
/// Emitted when the model attaches grounding metadata to the current turn (e.g. citation chunks from
/// Google Search). Treated as opaque here — the consumer renders/inspects it as needed.
/// </summary>
/// <param name="MetadataJson">The grounding metadata payload as raw JSON.</param>
public sealed record RealtimeGroundingMetadata(JsonElement MetadataJson) : RealtimeServerEvent;

/// <summary>The model has asked the client to execute one or more function calls.</summary>
/// <param name="Calls">The set of function calls in this batch.</param>
public sealed record RealtimeToolCall(IReadOnlyList<RealtimeFunctionCall> Calls) : RealtimeServerEvent;

/// <summary>A single function-call request emitted inside a <see cref="RealtimeToolCall"/>.</summary>
/// <param name="Id">Unique id assigned by the model. Use it when sending the <see cref="ToolResponse"/>.</param>
/// <param name="Name">Function name. Matches a registered <see cref="RealtimeToolDefinition.Name"/>.</param>
/// <param name="Arguments">Model-supplied arguments as a JSON object. Empty when the function takes no parameters.</param>
public sealed record RealtimeFunctionCall(string Id, string Name, JsonElement Arguments);

/// <summary>
/// The model has retracted one or more previously-issued tool calls. Consumers should attempt to undo
/// any side-effects from those calls if possible.
/// </summary>
/// <param name="ToolCallIds">The ids of the tool calls that have been cancelled.</param>
public sealed record RealtimeToolCallCancellation(IReadOnlyList<string> ToolCallIds) : RealtimeServerEvent;

/// <summary>
/// Server is preparing to terminate the connection. Consumers may use the embedded reconnect / retry
/// hints to plan a follow-up <c>ConnectAsync</c>.
/// </summary>
/// <param name="ErrorMessage">Optional human-readable explanation.</param>
/// <param name="ErrorCode">Optional gRPC / app-defined error code.</param>
/// <param name="Reconnect">When true, the client SHOULD reconnect with the latest resumption handle.</param>
/// <param name="RetryAfter">Suggested back-off before reconnecting.</param>
public sealed record RealtimeGoAway(
    string? ErrorMessage,
    int? ErrorCode,
    bool Reconnect,
    TimeSpan? RetryAfter) : RealtimeServerEvent;

/// <summary>
/// New resumption handle from the server. Cache the most recent value and pass it back as
/// <see cref="RealtimeSessionConfig.ResumptionHandle"/> on the next <c>ConnectAsync</c> to resume server-side state.
/// </summary>
/// <param name="Handle">The opaque resumption token.</param>
/// <param name="Resumable">True if the session can in fact be resumed at the moment the handle was issued.</param>
public sealed record RealtimeSessionResumptionUpdate(string Handle, bool Resumable) : RealtimeServerEvent;

/// <summary>
/// The underlying WebSocket disconnected. Local-only event — not part of the wire protocol — but
/// surfaced through the same channel so consumers do not have to wire a separate callback.
/// </summary>
/// <param name="Initiator">Who initiated the close: client, server, or an unexpected fault.</param>
public sealed record RealtimeTransportClosed(RealtimeCloseInitiator Initiator) : RealtimeServerEvent;

/// <summary>An error surfaced by the transport. Local-only event.</summary>
/// <param name="Message">Human-readable error message.</param>
public sealed record RealtimeTransportError(string Message) : RealtimeServerEvent;

/// <summary>Who initiated a <see cref="RealtimeTransportClosed"/>.</summary>
public enum RealtimeCloseInitiator
{
    /// <summary>The consumer called <c>DisconnectAsync</c>.</summary>
    Client = 0,

    /// <summary>The server closed the stream (typically following a <c>goAway</c>).</summary>
    Server = 1,

    /// <summary>The connection dropped without a clean close — network failure, process kill, etc.</summary>
    Unexpected = 2,
}
