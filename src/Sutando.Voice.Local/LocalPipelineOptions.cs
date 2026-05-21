using Microsoft.Extensions.AI;
using Sutando.LocalInference;
using Sutando.Pipeline;

namespace Sutando.Voice.Local;

/// <summary>
/// Everything <see cref="LocalPipelineRealtimeClient"/> needs to compose a local voice pipeline:
/// the four pluggable stage components (<see cref="IVadDetector"/>, <see cref="ISpeechToTextClient"/>,
/// <see cref="IChatClient"/>, <see cref="ITextToSpeechClient"/>) plus a handful of tuning knobs.
/// </summary>
/// <remarks>
/// <para>
/// <b>The two flavours share this type.</b> The "pure in-process" flavour populates the four
/// components from the Whisper.net / KokoroSharp / LlamaSharp / Silero adapters; the
/// "AppHost-orchestrated" flavour populates them from <c>Sutando.LocalInference.OpenAI</c>'s
/// HTTP-shaped clients pointed at LAN endpoints. Neither the transport nor the pipeline shape
/// changes between flavours — only which concrete clients land in these four slots. See
/// <c>INTEGRATION-NOTES.md</c> for how the AppHost flavour plugs in.
/// </para>
/// <para>
/// <b>Lifetime.</b> The four components are owned by the caller (typically a DI container).
/// The transport does not dispose them — multiple voice sessions reuse the same singletons,
/// exactly as the existing <c>GeminiLiveTransportFactory</c> hands out a fresh client per call
/// but the model weights stay loaded.
/// </para>
/// </remarks>
public sealed class LocalPipelineOptions
{
    /// <summary>
    /// Voice-activity detector marking turn boundaries. Required — without it the STT stage
    /// never sees a <c>SpeechEnd</c> and no turn is ever transcribed.
    /// </summary>
    public required IVadDetector VadDetector { get; init; }

    /// <summary>Speech-to-text client (e.g. Whisper.net) transcribing each buffered turn.</summary>
    public required ISpeechToTextClient SpeechToText { get; init; }

    /// <summary>Chat / LLM client (e.g. LlamaSharp) producing the assistant response.</summary>
    public required IChatClient Chat { get; init; }

    /// <summary>Text-to-speech client (e.g. KokoroSharp) synthesising the assistant audio.</summary>
    public required ITextToSpeechClient TextToSpeech { get; init; }

    /// <summary>
    /// Optional system prompt threaded into every conversation by the <see cref="Pipeline.Stages.ChatStage"/>.
    /// </summary>
    public string? SystemPrompt { get; init; }

    /// <summary>
    /// Sample rate of inbound microphone PCM, in Hz. The browser harness sends 16 kHz mono
    /// 16-bit; the VAD / STT adapters resample at their own boundary if they need a different rate.
    /// </summary>
    public int InputSampleRateHz { get; init; } = 16_000;

    /// <summary>Detection thresholds passed to the <see cref="IVadDetector"/>. Null uses <see cref="VadOptions"/> defaults.</summary>
    public VadOptions? VadOptions { get; init; }

    /// <summary>MEAI options threaded into every chat completion. Null uses the chat client's defaults.</summary>
    public ChatOptions? ChatOptions { get; init; }

    /// <summary>MEAI options threaded into every speech-to-text call. Null uses the STT client's defaults.</summary>
    public SpeechToTextOptions? SpeechToTextOptions { get; init; }

    /// <summary>MEAI options threaded into every text-to-speech call (voice id, speed, …). Null uses the TTS client's defaults.</summary>
    public TextToSpeechOptions? TextToSpeechOptions { get; init; }
}
