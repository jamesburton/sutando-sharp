using Microsoft.Extensions.AI;

namespace Sutando.Realtime;

/// <summary>
/// Sutando-defined <see cref="RealtimeServerMessageType"/> values for surfaces Gemini Live
/// emits but MEAI's realtime contract has no first-class peer for.
/// </summary>
/// <remarks>
/// <para>
/// MEAI's <see cref="RealtimeServerMessageType"/> is an open string-struct. Providers add their
/// own values by constructing new instances; the well-known set (<c>OutputAudioDelta</c>,
/// <c>ResponseDone</c>, etc.) is just the OpenAI-realtime-flavoured starter pack. Gemini-specific
/// signals — setup-complete, go-away, session-resumption-update, tool-call-cancellation,
/// grounding-metadata, the local-only transport-closed envelope — live here.
/// </para>
/// <para>
/// See <c>MAPPING.md</c> for the per-type rationale and the consumer-facing
/// <see cref="RealtimeServerEvent"/> the adapter projects each one onto.
/// </para>
/// </remarks>
public static class SutandoRealtimeMessageTypes
{
    /// <summary>The Gemini server acknowledged the setup envelope and the session is ready for input.</summary>
    public static RealtimeServerMessageType SessionStarted { get; } = new("SutandoSessionStarted");

    /// <summary>The Gemini server emitted a <c>goAway</c> frame — connection is about to terminate.</summary>
    public static RealtimeServerMessageType GoAway { get; } = new("SutandoGoAway");

    /// <summary>The Gemini server emitted a <c>sessionResumptionUpdate</c> with a fresh handle.</summary>
    public static RealtimeServerMessageType SessionResumptionUpdate { get; } = new("SutandoSessionResumptionUpdate");

    /// <summary>The Gemini server retracted one or more previously-issued tool calls.</summary>
    public static RealtimeServerMessageType ToolCallCancelled { get; } = new("SutandoToolCallCancelled");

    /// <summary>Grounding metadata attached to a model turn (Gemini-only).</summary>
    public static RealtimeServerMessageType GroundingMetadata { get; } = new("SutandoGroundingMetadata");

    /// <summary>The underlying transport closed. Local-only marker, not on the wire.</summary>
    public static RealtimeServerMessageType SessionClosed { get; } = new("SutandoSessionClosed");
}
