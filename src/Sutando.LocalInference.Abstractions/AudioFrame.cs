using System.Diagnostics.CodeAnalysis;

namespace Sutando.LocalInference;

/// <summary>
/// One frame of PCM audio flowing through Sutando's local-inference pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Used by <see cref="IVadDetector"/> as the input format. The STT / TTS surfaces in
/// Microsoft.Extensions.AI use their own data shapes
/// (<see cref="Microsoft.Extensions.AI.DataContent"/>, <see cref="System.IO.Stream"/>,
/// and the realtime audio shapes) — those types are unchanged here and the rest of the
/// pipeline interops via stream / span conversions at the adapter boundary.
/// </para>
/// <para>
/// Default conventions follow the upstream realtime stack:
/// 16 kHz / mono / 16-bit signed little-endian PCM for inbound (microphone),
/// 24 kHz / mono / 16-bit signed little-endian PCM for outbound (synthesised). Adapter
/// implementations may resample at the boundary and document it.
/// </para>
/// </remarks>
[Experimental("SUTANDO001")]
public readonly record struct AudioFrame
{
    /// <summary>Sample rate in Hz (e.g. 16000, 24000, 48000).</summary>
    public required int SampleRate { get; init; }

    /// <summary>Channel count. 1 = mono, 2 = stereo interleaved.</summary>
    public required int Channels { get; init; }

    /// <summary>PCM sample encoding.</summary>
    public required AudioEncoding Encoding { get; init; }

    /// <summary>The raw audio payload in <see cref="Encoding"/>'s format.</summary>
    public required ReadOnlyMemory<byte> Pcm { get; init; }

    /// <summary>Optional capture timestamp. Adapters that don't track timing may leave this <see langword="default"/>.</summary>
    public DateTimeOffset CapturedAt { get; init; }

    /// <summary>Convenience constructor for the upstream-standard 16 kHz mono 16-bit microphone frame.</summary>
    public static AudioFrame Microphone16kMono(ReadOnlyMemory<byte> pcm, DateTimeOffset? capturedAt = null) => new()
    {
        SampleRate = 16_000,
        Channels = 1,
        Encoding = AudioEncoding.Pcm16Le,
        Pcm = pcm,
        CapturedAt = capturedAt ?? DateTimeOffset.UtcNow,
    };

    /// <summary>Convenience constructor for the upstream-standard 24 kHz mono 16-bit speaker frame.</summary>
    public static AudioFrame Speaker24kMono(ReadOnlyMemory<byte> pcm, DateTimeOffset? capturedAt = null) => new()
    {
        SampleRate = 24_000,
        Channels = 1,
        Encoding = AudioEncoding.Pcm16Le,
        Pcm = pcm,
        CapturedAt = capturedAt ?? DateTimeOffset.UtcNow,
    };

    /// <summary>Number of PCM samples per channel in this frame.</summary>
    public int SampleCount => Encoding switch
    {
        AudioEncoding.Pcm16Le => Pcm.Length / (2 * Channels),
        AudioEncoding.Pcm32FloatLe => Pcm.Length / (4 * Channels),
        _ => 0,
    };

    /// <summary>Frame duration computed from <see cref="SampleCount"/> and <see cref="SampleRate"/>.</summary>
    public TimeSpan Duration => SampleRate > 0
        ? TimeSpan.FromSeconds((double)SampleCount / SampleRate)
        : TimeSpan.Zero;
}

/// <summary>PCM sample encoding for an <see cref="AudioFrame"/>.</summary>
[Experimental("SUTANDO001")]
public enum AudioEncoding
{
    /// <summary>16-bit signed little-endian PCM. The default for both upstream microphone and speaker frames.</summary>
    Pcm16Le = 0,

    /// <summary>32-bit IEEE-754 float little-endian PCM. Used by some ONNX models that prefer normalised float input.</summary>
    Pcm32FloatLe,
}
