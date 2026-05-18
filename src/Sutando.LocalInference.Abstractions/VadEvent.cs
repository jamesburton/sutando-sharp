using System.Diagnostics.CodeAnalysis;

namespace Sutando.LocalInference;

/// <summary>
/// One event emitted by an <see cref="IVadDetector"/> while it analyses an audio stream.
/// </summary>
/// <remarks>
/// Three event kinds carry meaningful semantics: speech-start (the leading edge of a turn),
/// speech-end (the trailing edge — voice activity has fallen below threshold for at least
/// <see cref="VadOptions.SilenceHangover"/>), and energy-update (a periodic level signal
/// useful for UI indicators and downstream gating).
/// </remarks>
[Experimental("SUTANDO001")]
public readonly record struct VadEvent
{
    /// <summary>What kind of transition this event represents.</summary>
    public required VadEventKind Kind { get; init; }

    /// <summary>
    /// When the underlying audio frame was captured (uses <see cref="AudioFrame.CapturedAt"/>
    /// when available, otherwise the detector's own clock).
    /// </summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>
    /// Detector confidence / energy in <c>[0.0, 1.0]</c>. Semantically:
    /// <list type="bullet">
    ///   <item><description>For <see cref="VadEventKind.SpeechStart"/> / <see cref="VadEventKind.SpeechEnd"/>: the trigger probability that crossed the threshold.</description></item>
    ///   <item><description>For <see cref="VadEventKind.EnergyUpdate"/>: the rolling probability sample.</description></item>
    /// </list>
    /// </summary>
    public required float Probability { get; init; }

    /// <summary>Convenience builder for the start-of-speech event.</summary>
    public static VadEvent SpeechStart(DateTimeOffset at, float probability) =>
        new() { Kind = VadEventKind.SpeechStart, At = at, Probability = probability };

    /// <summary>Convenience builder for the end-of-speech event.</summary>
    public static VadEvent SpeechEnd(DateTimeOffset at, float probability) =>
        new() { Kind = VadEventKind.SpeechEnd, At = at, Probability = probability };

    /// <summary>Convenience builder for the periodic energy-update event.</summary>
    public static VadEvent EnergyUpdate(DateTimeOffset at, float probability) =>
        new() { Kind = VadEventKind.EnergyUpdate, At = at, Probability = probability };
}

/// <summary>The kind of transition represented by a <see cref="VadEvent"/>.</summary>
[Experimental("SUTANDO001")]
public enum VadEventKind
{
    /// <summary>Speech detected; the leading edge of a new turn.</summary>
    SpeechStart = 0,

    /// <summary>Speech ended; voice activity has been below threshold for at least <see cref="VadOptions.SilenceHangover"/>.</summary>
    SpeechEnd,

    /// <summary>Periodic energy / probability sample. Emitted whether or not the speech state changed; useful for waveform UIs.</summary>
    EnergyUpdate,
}

/// <summary>Tunable parameters for an <see cref="IVadDetector"/> run.</summary>
[Experimental("SUTANDO001")]
public sealed record VadOptions
{
    /// <summary>Default Silero-style probability threshold (the model emits <c>[0,1]</c>; values above this count as speech).</summary>
    public const float DefaultThreshold = 0.5f;

    /// <summary>Default minimum speech duration before <see cref="VadEventKind.SpeechStart"/> is emitted (filters click / cough false-positives).</summary>
    public static readonly TimeSpan DefaultMinSpeech = TimeSpan.FromMilliseconds(120);

    /// <summary>Default silence-hangover before <see cref="VadEventKind.SpeechEnd"/> is emitted (waits for natural pauses, not micro-gaps).</summary>
    public static readonly TimeSpan DefaultSilenceHangover = TimeSpan.FromMilliseconds(700);

    /// <summary>Default energy-update interval (controls how often <see cref="VadEventKind.EnergyUpdate"/> fires).</summary>
    public static readonly TimeSpan DefaultEnergyUpdateInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>Probability threshold in <c>[0.0, 1.0]</c>; samples above this are considered speech. Default <see cref="DefaultThreshold"/>.</summary>
    public float Threshold { get; init; } = DefaultThreshold;

    /// <summary>How long speech must persist before <see cref="VadEventKind.SpeechStart"/> fires.</summary>
    public TimeSpan MinSpeech { get; init; } = DefaultMinSpeech;

    /// <summary>How long silence must persist before <see cref="VadEventKind.SpeechEnd"/> fires.</summary>
    public TimeSpan SilenceHangover { get; init; } = DefaultSilenceHangover;

    /// <summary>How often to emit <see cref="VadEventKind.EnergyUpdate"/> events; set to <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> to disable.</summary>
    public TimeSpan EnergyUpdateInterval { get; init; } = DefaultEnergyUpdateInterval;
}
