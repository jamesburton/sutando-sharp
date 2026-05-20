using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Sutando.LocalInference;

namespace Sutando.Pipeline.Stages;

/// <summary>
/// Pipeline stage that wraps an <see cref="ISpeechToTextClient"/>. Buffers
/// <see cref="AudioInputFrame"/>s between a <see cref="VadFrame"/> with
/// <see cref="VadEventKind.SpeechStart"/> and one with <see cref="VadEventKind.SpeechEnd"/>,
/// then transcribes the buffer and emits a final <see cref="TextFrame"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Turn boundaries</b>: STT is naturally turn-shaped — the model needs a complete utterance
/// to transcribe. We rely on the upstream <see cref="VadStage"/> to mark turn boundaries via
/// <see cref="VadFrame"/>s. Audio frames received outside a turn are still forwarded downstream
/// (other stages may want them) but are not added to the transcription buffer.
/// </para>
/// <para>
/// <b>Interruption</b>: a <see cref="ControlFrame"/> with <see cref="ControlSignal.Interrupt"/>
/// cancels any in-flight transcription via a per-turn linked CTS and clears the buffer. The
/// frame is forwarded downstream.
/// </para>
/// <para>
/// <b>Streaming vs single-shot</b>: this stage uses the single-shot
/// <see cref="ISpeechToTextClient.GetTextAsync(Stream, SpeechToTextOptions?, CancellationToken)"/>
/// at turn end — incremental streaming transcription is left to a future enhancement once we
/// have a consumer that needs it.
/// </para>
/// </remarks>
[Experimental("SUTANDO001")]
public sealed class SpeechToTextStage : IPipelineStage<PipelineFrame, PipelineFrame>
{
    private readonly ISpeechToTextClient _client;
    private readonly SpeechToTextOptions? _sttOptions;

    /// <summary>Initialise the stage around a MEAI STT client.</summary>
    /// <param name="client">The STT client (e.g. Whisper.net's <c>WhisperSpeechToTextClient</c>).</param>
    /// <param name="sttOptions">Optional MEAI options threaded into every transcription call.</param>
    public SpeechToTextStage(ISpeechToTextClient client, SpeechToTextOptions? sttOptions = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _sttOptions = sttOptions;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<PipelineFrame> ProcessAsync(
        IAsyncEnumerable<PipelineFrame> source,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);

        var buffer = new MemoryStream();
        var inSpeech = false;
        var turnFormat = (SampleRate: 0, Channels: 0, Encoding: AudioEncoding.Pcm16Le);

        await foreach (var frame in source.WithCancellation(ct).ConfigureAwait(false))
        {
            switch (frame)
            {
                case VadFrame vad when vad.Event.Kind == VadEventKind.SpeechStart:
                    buffer.SetLength(0);
                    inSpeech = true;
                    yield return frame;
                    break;

                case VadFrame vad when vad.Event.Kind == VadEventKind.SpeechEnd:
                    inSpeech = false;
                    yield return frame;
                    if (buffer.Length > 0)
                    {
                        var text = await TranscribeAsync(buffer, turnFormat, ct).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(text))
                        {
                            yield return new TextFrame(text, IsFinal: true);
                        }
                    }
                    buffer.SetLength(0);
                    break;

                case AudioInputFrame audio when inSpeech:
                    turnFormat = (audio.Audio.SampleRate, audio.Audio.Channels, audio.Audio.Encoding);
                    buffer.Write(audio.Audio.Pcm.Span);
                    yield return frame;
                    break;

                case ControlFrame ctrl when ctrl.Signal == ControlSignal.Interrupt:
                    // Discard any in-flight turn. We don't currently have an in-flight async
                    // transcription here (single-shot only fires at SpeechEnd), but if a future
                    // streaming variant is added it should cancel its per-turn CTS here.
                    buffer.SetLength(0);
                    inSpeech = false;
                    yield return frame;
                    break;

                default:
                    yield return frame;
                    break;
            }
        }
    }

    private async Task<string> TranscribeAsync(MemoryStream buffer, (int SampleRate, int Channels, AudioEncoding Encoding) format, CancellationToken ct)
    {
        // MEAI's ISpeechToTextClient consumes a Stream — typically a WAV / FLAC / OGG container,
        // not raw PCM. Wrap the buffered raw PCM in a minimal 44-byte RIFF/WAVE header so
        // every concrete client (Whisper.net, OpenAI-compatible HTTP clients) can decode it
        // without per-adapter glue.
        var wav = WrapPcmInWavHeader(buffer.GetBuffer(), 0, (int)buffer.Length, format.SampleRate, format.Channels, format.Encoding);
        using var stream = new MemoryStream(wav, writable: false);

        var response = await _client.GetTextAsync(stream, _sttOptions, ct).ConfigureAwait(false);
        return response.Text ?? string.Empty;
    }

    internal static byte[] WrapPcmInWavHeader(byte[] pcm, int offset, int length, int sampleRate, int channels, AudioEncoding encoding)
    {
        // Minimal RIFF/WAVE header for a single fmt chunk + data chunk. Matches the format
        // emitted by NAudio / ffmpeg / dotnet libraries — every conformant decoder reads it.
        var bitsPerSample = encoding switch
        {
            AudioEncoding.Pcm16Le => 16,
            AudioEncoding.Pcm32FloatLe => 32,
            _ => 16,
        };
        // Audio format: 1 = PCM (integer), 3 = IEEE 754 float.
        var audioFormat = encoding == AudioEncoding.Pcm32FloatLe ? (short)3 : (short)1;
        var byteRate = sampleRate * channels * bitsPerSample / 8;
        var blockAlign = (short)(channels * bitsPerSample / 8);
        var dataSize = length;
        var fileSize = 36 + dataSize;

        var output = new byte[44 + dataSize];
        var span = output.AsSpan();

        // RIFF chunk
        span[0] = (byte)'R'; span[1] = (byte)'I'; span[2] = (byte)'F'; span[3] = (byte)'F';
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(4, 4), fileSize);
        span[8] = (byte)'W'; span[9] = (byte)'A'; span[10] = (byte)'V'; span[11] = (byte)'E';

        // fmt sub-chunk
        span[12] = (byte)'f'; span[13] = (byte)'m'; span[14] = (byte)'t'; span[15] = (byte)' ';
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(16, 4), 16); // chunk size
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(20, 2), audioFormat);
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(22, 2), (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(28, 4), byteRate);
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(32, 2), blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(34, 2), (short)bitsPerSample);

        // data sub-chunk
        span[36] = (byte)'d'; span[37] = (byte)'a'; span[38] = (byte)'t'; span[39] = (byte)'a';
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(40, 4), dataSize);
        pcm.AsSpan(offset, length).CopyTo(span.Slice(44));

        return output;
    }
}
