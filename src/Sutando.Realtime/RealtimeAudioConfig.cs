namespace Sutando.Realtime;

/// <summary>
/// Audio format contract for the realtime session.
/// </summary>
/// <remarks>
/// Gemini Live's wire contract is fixed at 16 kHz / mono / 16-bit PCM for input
/// and 24 kHz / mono / 16-bit PCM for output. The fields are exposed here for
/// honest documentation and to give future transports (e.g. OpenAI Realtime,
/// which negotiates at session-setup time) somewhere to plug different values
/// in without changing the consumer-facing API.
/// </remarks>
/// <param name="InputSampleRateHz">Sample rate of audio sent to the model. Gemini requires 16000.</param>
/// <param name="OutputSampleRateHz">Sample rate of audio received from the model. Gemini emits 24000.</param>
/// <param name="Channels">Channel count. Gemini requires 1 (mono).</param>
/// <param name="BitsPerSample">Sample width in bits. Gemini requires 16.</param>
public sealed record RealtimeAudioConfig(
    int InputSampleRateHz = 16_000,
    int OutputSampleRateHz = 24_000,
    int Channels = 1,
    int BitsPerSample = 16)
{
    /// <summary>The IANA MIME type for input audio chunks (<c>audio/pcm;rate=16000</c> by default).</summary>
    public string InputMimeType => $"audio/pcm;rate={InputSampleRateHz}";

    /// <summary>The default Gemini-aligned configuration: 16 kHz in, 24 kHz out, mono, 16-bit PCM.</summary>
    public static RealtimeAudioConfig Default { get; } = new();
}
