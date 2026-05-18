namespace Sutando.Phone;

/// <summary>
/// Naive linear-interpolation resampler. Handles the two rate changes the phone bridge
/// needs:
/// <list type="bullet">
///   <item><b>8 kHz → 16 kHz</b> inbound (caller speech, post μ-law decode).</item>
///   <item><b>24 kHz → 8 kHz</b> outbound (Gemini speech, pre μ-law encode).</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Quality caveat:</b> linear interpolation is not a band-limited resampler. There will
/// be measurable alias artefacts on tones above ~3.5 kHz on the inbound side and above
/// ~3.5 kHz on the outbound side. For a v1 phone bridge this is acceptable — Twilio's
/// 8 kHz narrowband channel already band-limits the inbound signal so the upsample is mostly
/// inventing samples, and the outbound downsample introduces aliasing in the high band that
/// the carrier strips anyway. A v2 swap-in target would be a polyphase FIR (NAudio's
/// <c>WdlResamplingSampleProvider</c> or libsamplerate via P/Invoke) — see INTEGRATION-NOTES.md.
/// </para>
/// <para>
/// <b>Why naive:</b> the alternative — NAudio — has good code but its packaging on net10.0
/// is "loads but warns". A managed third-party for ~30 lines of resampling math is the wrong
/// trade. Hand-rolling keeps the dependency footprint flat AND lets the unit tests cover the
/// exact arithmetic (no version-skew surprises).
/// </para>
/// <para>
/// The functions operate on byte buffers of little-endian signed-16-bit samples. Sample
/// counts and byte counts are always related by <c>bytes = samples * 2</c>; the resampler
/// validates that on entry and throws on a malformed buffer.
/// </para>
/// </remarks>
internal static class AudioResampler
{
    /// <summary>
    /// Upsample 8 kHz linear-PCM to 16 kHz linear-PCM. Each input sample produces two output
    /// samples; the second is the linear midpoint of the input pair.
    /// </summary>
    /// <param name="pcm8k">Little-endian 16-bit PCM at 8 kHz. Must be even-length in bytes.</param>
    /// <returns>A freshly allocated 16 kHz buffer with <c>2 * sampleCount</c> samples.</returns>
    /// <exception cref="ArgumentException">When <paramref name="pcm8k"/> has an odd byte count.</exception>
    public static byte[] Upsample8To16(ReadOnlySpan<byte> pcm8k)
    {
        if ((pcm8k.Length & 1) != 0)
        {
            throw new ArgumentException("PCM buffer must be a multiple of two bytes.", nameof(pcm8k));
        }
        var inSamples = pcm8k.Length / 2;
        if (inSamples == 0)
        {
            return Array.Empty<byte>();
        }
        var outSamples = inSamples * 2;
        var output = new byte[outSamples * 2];
        short previous = ReadSample(pcm8k, 0);
        for (var i = 0; i < inSamples; i++)
        {
            var current = ReadSample(pcm8k, i);
            // Midpoint between previous and current (saturating not strictly required —
            // (a + b) / 2 stays in [-32768, 32767] for any two int16 inputs).
            var midpoint = (short)((previous + current) / 2);
            WriteSample(output, (i * 2) + 0, midpoint);
            WriteSample(output, (i * 2) + 1, current);
            previous = current;
        }
        return output;
    }

    /// <summary>
    /// Downsample 24 kHz linear-PCM to 8 kHz linear-PCM. The integer ratio (3:1) is handled
    /// by averaging each three-sample input window into one output sample.
    /// </summary>
    /// <param name="pcm24k">Little-endian 16-bit PCM at 24 kHz. Must be even-length in bytes.</param>
    /// <returns>A freshly allocated 8 kHz buffer with <c>sampleCount / 3</c> samples (rounded down).</returns>
    /// <exception cref="ArgumentException">When <paramref name="pcm24k"/> has an odd byte count.</exception>
    /// <remarks>
    /// Twilio's voice channel filters the resulting signal to its 4 kHz Nyquist window, so the
    /// minor aliasing introduced by lack of a true anti-alias lowpass is masked at the carrier
    /// end. A polyphase replacement would be measurably cleaner but is out of v1 scope.
    /// </remarks>
    public static byte[] Downsample24To8(ReadOnlySpan<byte> pcm24k)
    {
        if ((pcm24k.Length & 1) != 0)
        {
            throw new ArgumentException("PCM buffer must be a multiple of two bytes.", nameof(pcm24k));
        }
        var inSamples = pcm24k.Length / 2;
        var outSamples = inSamples / 3;
        if (outSamples == 0)
        {
            return Array.Empty<byte>();
        }
        var output = new byte[outSamples * 2];
        for (var i = 0; i < outSamples; i++)
        {
            // Average three adjacent 24 kHz samples → one 8 kHz sample. Int arithmetic so we
            // don't overflow on three int16 values that each fit in 16 bits — sum at most fits
            // in 18 bits, fits in int comfortably.
            var sum = (int)ReadSample(pcm24k, (i * 3) + 0)
                    + ReadSample(pcm24k, (i * 3) + 1)
                    + ReadSample(pcm24k, (i * 3) + 2);
            WriteSample(output, i, (short)(sum / 3));
        }
        return output;
    }

    /// <summary>Read a little-endian 16-bit sample at the given <i>sample</i> index.</summary>
    /// <param name="buffer">Little-endian PCM byte buffer.</param>
    /// <param name="sampleIndex">Zero-based sample index (NOT byte index).</param>
    /// <returns>The signed sample.</returns>
    private static short ReadSample(ReadOnlySpan<byte> buffer, int sampleIndex)
    {
        var byteIndex = sampleIndex * 2;
        return (short)(buffer[byteIndex] | (buffer[byteIndex + 1] << 8));
    }

    /// <summary>Write a signed 16-bit sample at the given <i>sample</i> index in little-endian.</summary>
    /// <param name="buffer">Target byte buffer.</param>
    /// <param name="sampleIndex">Zero-based sample index (NOT byte index).</param>
    /// <param name="value">The sample to write.</param>
    private static void WriteSample(Span<byte> buffer, int sampleIndex, short value)
    {
        var byteIndex = sampleIndex * 2;
        buffer[byteIndex] = (byte)(value & 0xff);
        buffer[byteIndex + 1] = (byte)((value >> 8) & 0xff);
    }
}
