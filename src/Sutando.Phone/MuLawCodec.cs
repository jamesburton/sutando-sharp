namespace Sutando.Phone;

/// <summary>
/// ITU-T G.711 μ-law codec. Hand-rolled — the algorithm is a 256-entry decode table and a
/// short encode lookup; pulling NAudio just to consume two tables would be more dependency
/// surface than the whole rest of the phone bridge.
/// </summary>
/// <remarks>
/// <para>
/// μ-law is the North-American narrowband telephony codec — every Twilio Media Streams
/// frame Twilio sends us is base64-encoded μ-law at 8 kHz / mono / 8-bit. Gemini Live wants
/// 16 kHz / mono / 16-bit linear PCM going in, and emits 24 kHz / mono / 16-bit linear PCM
/// coming out. The codec here handles the μ-law ↔ 16-bit-linear conversion; sample-rate
/// changes live in <see cref="AudioResampler"/>.
/// </para>
/// <para>
/// <b>Table values:</b> the decode table matches NAudio's <c>MuLawDecoder</c> table
/// byte-for-byte, which in turn comes from the canonical G.711 reference. The encode side
/// uses NAudio's segment-lookup approach so encode and decode are mutual inverses across the
/// full ±8159 G.711 dynamic range — much better than the formula-only variant in upstream
/// <c>conversation-server.ts</c>, which loses ~4× dynamic range and round-trip-collapses
/// loud samples. Twilio at the wire-level interoperates with both, so the upgrade is
/// transparent.
/// </para>
/// </remarks>
internal static class MuLawCodec
{
    private const int Bias = 0x84;
    private const int Clip = 32635;

    /// <summary>
    /// Precomputed decode table — 256 entries mapping each μ-law byte to its 16-bit signed
    /// linear PCM sample. Values match NAudio's <c>MuLawDecompressTable</c> verbatim, which is
    /// itself the ITU-T G.711 reference table.
    /// </summary>
    private static readonly short[] DecodeTable =
    [
         -32124, -31100, -30076, -29052, -28028, -27004, -25980, -24956,
         -23932, -22908, -21884, -20860, -19836, -18812, -17788, -16764,
         -15996, -15484, -14972, -14460, -13948, -13436, -12924, -12412,
         -11900, -11388, -10876, -10364,  -9852,  -9340,  -8828,  -8316,
          -7932,  -7676,  -7420,  -7164,  -6908,  -6652,  -6396,  -6140,
          -5884,  -5628,  -5372,  -5116,  -4860,  -4604,  -4348,  -4092,
          -3900,  -3772,  -3644,  -3516,  -3388,  -3260,  -3132,  -3004,
          -2876,  -2748,  -2620,  -2492,  -2364,  -2236,  -2108,  -1980,
          -1884,  -1820,  -1756,  -1692,  -1628,  -1564,  -1500,  -1436,
          -1372,  -1308,  -1244,  -1180,  -1116,  -1052,   -988,   -924,
           -876,   -844,   -812,   -780,   -748,   -716,   -684,   -652,
           -620,   -588,   -556,   -524,   -492,   -460,   -428,   -396,
           -372,   -356,   -340,   -324,   -308,   -292,   -276,   -260,
           -244,   -228,   -212,   -196,   -180,   -164,   -148,   -132,
           -120,   -112,   -104,    -96,    -88,    -80,    -72,    -64,
            -56,    -48,    -40,    -32,    -24,    -16,     -8,     -1,
          32124,  31100,  30076,  29052,  28028,  27004,  25980,  24956,
          23932,  22908,  21884,  20860,  19836,  18812,  17788,  16764,
          15996,  15484,  14972,  14460,  13948,  13436,  12924,  12412,
          11900,  11388,  10876,  10364,   9852,   9340,   8828,   8316,
           7932,   7676,   7420,   7164,   6908,   6652,   6396,   6140,
           5884,   5628,   5372,   5116,   4860,   4604,   4348,   4092,
           3900,   3772,   3644,   3516,   3388,   3260,   3132,   3004,
           2876,   2748,   2620,   2492,   2364,   2236,   2108,   1980,
           1884,   1820,   1756,   1692,   1628,   1564,   1500,   1436,
           1372,   1308,   1244,   1180,   1116,   1052,    988,    924,
            876,    844,    812,    780,    748,    716,    684,    652,
            620,    588,    556,    524,    492,    460,    428,    396,
            372,    356,    340,    324,    308,    292,    276,    260,
            244,    228,    212,    196,    180,    164,    148,    132,
            120,    112,    104,     96,     88,     80,     72,     64,
             56,     48,     40,     32,     24,     16,      8,      0,
    ];

    /// <summary>
    /// Encode-side segment lookup. For a given high byte of <c>(sample + bias)</c> in the
    /// range 0..255, this returns the μ-law exponent. Lifted verbatim from NAudio's
    /// <c>MuLawCompressTable</c> — the same lookup the G.711 reference uses.
    /// </summary>
    private static readonly byte[] EncodeSegmentTable =
    [
         0,0,1,1,2,2,2,2,3,3,3,3,3,3,3,3,
         4,4,4,4,4,4,4,4,4,4,4,4,4,4,4,4,
         5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,
         5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,5,
         6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,
         6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,
         6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,
         6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,
         7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,
         7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,
         7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,
         7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,
         7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,
         7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,
         7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,
         7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,7,
    ];

    /// <summary>
    /// Decode a μ-law byte to a signed 16-bit PCM sample.
    /// </summary>
    /// <param name="muLawByte">A single μ-law-encoded byte (0..255).</param>
    /// <returns>The decoded signed 16-bit sample.</returns>
    public static short Decode(byte muLawByte) => DecodeTable[muLawByte];

    /// <summary>
    /// Decode a μ-law buffer into a freshly-allocated 16-bit PCM buffer (little-endian).
    /// </summary>
    /// <param name="muLaw">μ-law-encoded source bytes.</param>
    /// <returns>A new byte array twice the length, holding little-endian 16-bit samples.</returns>
    public static byte[] DecodeBlock(ReadOnlySpan<byte> muLaw)
    {
        var output = new byte[muLaw.Length * 2];
        for (var i = 0; i < muLaw.Length; i++)
        {
            var sample = DecodeTable[muLaw[i]];
            // Little-endian: low byte first. Gemini Live's input format is 16 kHz LE-PCM and
            // Twilio's Media Streams μ-law is endian-neutral on the wire, so we explicitly
            // serialise here to avoid surprises on big-endian platforms.
            output[(i * 2) + 0] = (byte)(sample & 0xff);
            output[(i * 2) + 1] = (byte)((sample >> 8) & 0xff);
        }
        return output;
    }

    /// <summary>
    /// Encode a signed 16-bit PCM sample into a single μ-law byte.
    /// </summary>
    /// <param name="sample">Signed 16-bit linear PCM sample.</param>
    /// <returns>The μ-law-encoded byte.</returns>
    public static byte Encode(short sample)
    {
        // Match NAudio's <c>LinearToMuLawSample</c> verbatim — established G.711 reference.
        // Sign bit from the high byte's sign indicator.
        var sign = (sample >> 8) & 0x80;
        if (sign != 0)
        {
            sample = (short)-sample;
        }
        if (sample > Clip)
        {
            sample = Clip;
        }
        sample = (short)(sample + Bias);
        var exponent = EncodeSegmentTable[(sample >> 7) & 0xff];
        var mantissa = (sample >> (exponent + 3)) & 0x0f;
        var compressed = ~(sign | (exponent << 4) | mantissa);
        return (byte)compressed;
    }

    /// <summary>
    /// Encode a buffer of little-endian 16-bit PCM samples into a freshly-allocated μ-law buffer.
    /// </summary>
    /// <param name="pcm">Source PCM bytes; must contain an even number of bytes.</param>
    /// <returns>A new byte array half the length, holding μ-law samples.</returns>
    /// <exception cref="ArgumentException">When <paramref name="pcm"/> has an odd byte count.</exception>
    public static byte[] EncodeBlock(ReadOnlySpan<byte> pcm)
    {
        if ((pcm.Length & 1) != 0)
        {
            throw new ArgumentException("PCM buffer must contain whole 16-bit samples.", nameof(pcm));
        }
        var output = new byte[pcm.Length / 2];
        for (var i = 0; i < output.Length; i++)
        {
            var sample = (short)(pcm[(i * 2) + 0] | (pcm[(i * 2) + 1] << 8));
            output[i] = Encode(sample);
        }
        return output;
    }
}
