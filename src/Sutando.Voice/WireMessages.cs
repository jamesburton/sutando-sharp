using System.Text.Json.Serialization;

namespace Sutando.Voice;

/// <summary>
/// Discriminator for the inbound message envelope sent by the browser over the <c>/voice</c>
/// WebSocket text frame. Snake_case enum mapping is configured globally in
/// <see cref="VoiceJson.Options"/>.
/// </summary>
internal enum ClientMessageType
{
    /// <summary>Default — message type was missing or unrecognised.</summary>
    Unknown = 0,

    /// <summary>Base64-encoded PCM 16 kHz mono 16-bit audio chunk.</summary>
    Audio,

    /// <summary>A text turn from the user (no audio).</summary>
    Text,

    /// <summary>User barge-in. Currently logged only — <see cref="Sutando.Realtime.IRealtimeTransport"/> exposes no interrupt API.</summary>
    Interrupt,

    /// <summary>Explicit turn-complete hint from the client. Currently logged only — see above.</summary>
    EndTurn,
}

/// <summary>
/// Inbound message from the browser. Exactly one of <see cref="Data"/> or <see cref="Text"/> is
/// populated depending on <see cref="Type"/>; the others should be ignored.
/// </summary>
/// <param name="Type">Discriminator.</param>
/// <param name="Data">Base64-encoded PCM audio chunk (16 kHz mono 16-bit). Populated only for <see cref="ClientMessageType.Audio"/>.</param>
/// <param name="Text">User text turn. Populated only for <see cref="ClientMessageType.Text"/>.</param>
internal sealed record ClientMessage(
    [property: JsonPropertyName("type")] ClientMessageType Type,
    [property: JsonPropertyName("data")] string? Data = null,
    [property: JsonPropertyName("text")] string? Text = null);

/// <summary>
/// Discriminator for the server-to-client envelope. Each variant corresponds to one
/// <see cref="Sutando.Realtime.RealtimeServerEvent"/> subtype the server cares to surface;
/// other event subtypes are intentionally elided (the developer harness doesn't render them).
/// </summary>
internal enum ServerMessageType
{
    /// <summary>Default — never serialised; protected against accidental emission.</summary>
    Unknown = 0,

    /// <summary>Setup envelope acknowledged by Gemini. Client is now safe to send audio.</summary>
    SetupComplete,

    /// <summary>Base64-encoded PCM 24 kHz mono 16-bit audio chunk from the model.</summary>
    Audio,

    /// <summary>Transcript of the user's audio input (fragment, may be partial).</summary>
    InputTranscription,

    /// <summary>Transcript of the model's spoken output (fragment, may be partial).</summary>
    OutputTranscription,

    /// <summary>Model detected user barge-in.</summary>
    Interrupted,

    /// <summary>Model finished the current turn.</summary>
    TurnComplete,

    /// <summary>Server is preparing to terminate the session.</summary>
    GoAway,

    /// <summary>Local-only — surfaces a transport error to the client so it can disconnect/retry.</summary>
    Error,
}

/// <summary>
/// Outbound message envelope serialised to the browser. Field nullability follows
/// <see cref="JsonIgnoreCondition.WhenWritingNull"/> so each variant emits only
/// the keys that are meaningful for it.
/// </summary>
/// <param name="Type">Discriminator. Always populated.</param>
internal sealed record ServerMessage(
    [property: JsonPropertyName("type")] ServerMessageType Type)
{
    /// <summary>Base64-encoded PCM audio chunk (used by <see cref="ServerMessageType.Audio"/>).</summary>
    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Data { get; init; }

    /// <summary>Transcript text fragment (used by the two transcription variants).</summary>
    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }

    /// <summary>Suggested back-off in milliseconds before the client should reconnect (used by <see cref="ServerMessageType.GoAway"/>).</summary>
    [JsonPropertyName("time_left_ms")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TimeLeftMs { get; init; }

    /// <summary>Human-readable error message (used by <see cref="ServerMessageType.Error"/>).</summary>
    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }
}
