using System.Diagnostics.CodeAnalysis;

namespace Sutando.LocalInference;

/// <summary>
/// Voice-activity detection over a stream of PCM audio. Concrete implementations
/// (Silero ONNX, WebRTC VAD, server-side VAD wrappers) consume an
/// <see cref="IAsyncEnumerable{T}"/> of <see cref="AudioFrame"/> and emit
/// <see cref="VadEvent"/>s when the speech state transitions.
/// </summary>
/// <remarks>
/// <para>
/// No equivalent of this interface exists in <c>Microsoft.Extensions.AI</c> 10.6 —
/// MEAI represents VAD as a config blob (<c>VoiceActivityDetectionOptions</c>) inside
/// <c>RealtimeSessionOptions</c>, treating VAD as a server-side feature of the realtime
/// session rather than a pluggable .NET surface.
/// </para>
/// <para>
/// We keep our own interface so the in-process pipeline (Silero ONNX feeding a chat /
/// STT / TTS chain locally) has a uniform abstraction. Shape mirrors MEAI conventions:
/// the audio source last argument, an options blob, <see cref="CancellationToken"/>
/// last, <see cref="IAsyncEnumerable{T}"/> return so consumers can pipe events through
/// a downstream stage. If MEAI later ships an <c>IVoiceActivityDetector</c>, the
/// migration cost is a rename and a re-export.
/// </para>
/// </remarks>
[Experimental("SUTANDO001")]
public interface IVadDetector
{
    /// <summary>Short identifier — <c>silero</c>, <c>webrtc</c>, <c>server-side</c>, …</summary>
    string Id { get; }

    /// <summary>
    /// Analyse the audio stream and emit voice-activity transitions. The returned
    /// enumerable completes when <paramref name="source"/> completes or the
    /// <paramref name="ct"/> is cancelled.
    /// </summary>
    /// <param name="source">PCM frames in. Adapters that need a specific sample-rate / encoding should resample at the boundary.</param>
    /// <param name="options">Detection thresholds and hangover timings.</param>
    /// <param name="ct">Cancellation. Honour promptly.</param>
    IAsyncEnumerable<VadEvent> AnalyzeAsync(
        IAsyncEnumerable<AudioFrame> source,
        VadOptions options,
        CancellationToken ct = default);
}
