using System.Text.Json.Serialization;

namespace Sutando.Phone;

/// <summary>
/// Wire envelopes Twilio Media Streams sends over the <c>/twilio/media</c> WebSocket. Each
/// frame is a text frame carrying UTF-8 JSON with an <c>event</c> discriminator. We model
/// only the events we care about — Twilio also sends <c>mark</c> and <c>dtmf</c> envelopes,
/// which we ignore for v1.
/// </summary>
/// <remarks>
/// Reference: <see href="https://www.twilio.com/docs/voice/twiml/stream#message-format"/>.
/// JSON property names are camelCase on the wire; <see cref="System.Text.Json"/> handles the
/// case folding via the shared <see cref="PhoneJson.Options"/>.
/// </remarks>
internal sealed class TwilioMediaEnvelope
{
    /// <summary>The wire event discriminator (lower-case): <c>connected</c>, <c>start</c>, <c>media</c>, <c>stop</c>, etc.</summary>
    [JsonPropertyName("event")]
    public string Event { get; set; } = string.Empty;

    /// <summary>The Twilio-assigned stream sid. Populated on <c>start</c>; required when sending audio back.</summary>
    [JsonPropertyName("streamSid")]
    public string? StreamSid { get; set; }

    /// <summary>Sequence number from Twilio. Useful for log breadcrumbs; we don't reorder on it.</summary>
    [JsonPropertyName("sequenceNumber")]
    public string? SequenceNumber { get; set; }

    /// <summary>Populated on <c>start</c> — describes the audio format Twilio is sending.</summary>
    [JsonPropertyName("start")]
    public TwilioMediaStart? Start { get; set; }

    /// <summary>Populated on <c>media</c> — base64-encoded μ-law / 8 kHz audio chunk.</summary>
    [JsonPropertyName("media")]
    public TwilioMediaPayload? Media { get; set; }
}

/// <summary>Start envelope — Twilio's setup frame for a Media Streams connection.</summary>
internal sealed class TwilioMediaStart
{
    /// <summary>Twilio call SID — primary key for the metadata-store record.</summary>
    [JsonPropertyName("callSid")]
    public string? CallSid { get; set; }

    /// <summary>Stream sid (also surfaced on the outer envelope).</summary>
    [JsonPropertyName("streamSid")]
    public string? StreamSid { get; set; }

    /// <summary>Twilio account sid.</summary>
    [JsonPropertyName("accountSid")]
    public string? AccountSid { get; set; }

    /// <summary>Custom parameters that <c>&lt;Parameter&gt;</c> nodes in the TwiML pushed onto the stream.</summary>
    /// <remarks>
    /// We pass <c>From</c>, <c>StirVerstat</c>, and the resolved tier through this channel so
    /// the WS handler does not need to re-read the original webhook form body.
    /// </remarks>
    [JsonPropertyName("customParameters")]
    public Dictionary<string, string>? CustomParameters { get; set; }
}

/// <summary>Media envelope payload — a single base64-encoded audio chunk.</summary>
internal sealed class TwilioMediaPayload
{
    /// <summary>Audio track — Twilio sends <c>inbound</c> for caller speech and <c>outbound</c> for echoes.</summary>
    [JsonPropertyName("track")]
    public string? Track { get; set; }

    /// <summary>Twilio chunk sequence — useful for log breadcrumbs.</summary>
    [JsonPropertyName("chunk")]
    public string? Chunk { get; set; }

    /// <summary>Wall-clock timestamp Twilio assigned to the chunk. Ignored by us in v1.</summary>
    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    /// <summary>Base64-encoded μ-law / 8 kHz audio.</summary>
    [JsonPropertyName("payload")]
    public string? Payload { get; set; }
}

/// <summary>
/// Outbound envelope we send to Twilio to push synthetic audio (Gemini's output) back to the
/// caller. Mirrors Twilio's documented <c>media</c> shape for the server-to-Twilio direction.
/// </summary>
/// <param name="StreamSid">The active stream sid (must match the <c>start</c> envelope).</param>
/// <param name="Media">A base64-encoded μ-law / 8 kHz payload.</param>
internal sealed record TwilioOutboundMedia(
    [property: JsonPropertyName("streamSid")] string StreamSid,
    [property: JsonPropertyName("media")] TwilioOutboundPayload Media)
{
    /// <summary>The wire event discriminator. Twilio expects <c>media</c>.</summary>
    [JsonPropertyName("event")]
    public string Event { get; init; } = "media";
}

/// <summary>Payload wrapper for an outbound media envelope.</summary>
/// <param name="Payload">Base64-encoded μ-law / 8 kHz audio chunk.</param>
internal sealed record TwilioOutboundPayload(
    [property: JsonPropertyName("payload")] string Payload);
