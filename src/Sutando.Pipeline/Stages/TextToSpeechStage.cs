using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Sutando.LocalInference;

namespace Sutando.Pipeline.Stages;

/// <summary>
/// Pipeline stage that wraps an <see cref="ITextToSpeechClient"/>. Consumes assistant
/// <see cref="TextFrame"/>s (streaming chat output), accumulates them up to natural sentence
/// boundaries, sends each sentence to the TTS client, and emits the resulting PCM as
/// <see cref="AudioOutputFrame"/>s.
/// </summary>
/// <remarks>
/// <para>
/// <b>Chunking strategy</b>: streaming-mode LLMs emit a few tokens at a time which is too
/// fine-grained for a TTS roundtrip. The stage buffers tokens until it sees a terminating
/// punctuation character (<c>.</c>, <c>?</c>, <c>!</c>) or a newline, then synthesises the
/// accumulated text. This keeps latency reasonable (one synthesis per sentence) while still
/// giving the listener prompt audio.
/// </para>
/// <para>
/// <b>Final flush</b>: when a <see cref="ControlFrame.TurnComplete"/> or
/// <see cref="ControlFrame.Stop"/> arrives, any remaining buffered text is synthesised before
/// the frame is forwarded.
/// </para>
/// <para>
/// <b>Interruption</b>: <see cref="ControlFrame.Interrupt"/> drops the current text buffer and
/// cancels any in-flight TTS synthesis. The frame is forwarded downstream so audio sinks can
/// flush their own queues.
/// </para>
/// </remarks>
[Experimental("SUTANDO001")]
public sealed class TextToSpeechStage : IPipelineStage<PipelineFrame, PipelineFrame>
{
    private readonly ITextToSpeechClient _client;
    private readonly TextToSpeechOptions? _ttsOptions;

    /// <summary>Default speaker output format produced by the stage's synthesised <see cref="AudioOutputFrame"/>s.</summary>
    public const int DefaultSampleRate = 24_000;

    private static readonly char[] SentenceTerminators = ['.', '!', '?', '\n'];

    /// <summary>Initialise the stage around a MEAI TTS client.</summary>
    /// <param name="client">The TTS client (e.g. <c>KokoroSharpTextToSpeechClient</c>).</param>
    /// <param name="ttsOptions">Optional MEAI options threaded into every synthesis call.</param>
    public TextToSpeechStage(ITextToSpeechClient client, TextToSpeechOptions? ttsOptions = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _ttsOptions = ttsOptions;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<PipelineFrame> ProcessAsync(
        IAsyncEnumerable<PipelineFrame> source,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);

        var buffer = new StringBuilder();
        CancellationTokenSource? turnCts = null;

        try
        {
            await foreach (var frame in source.WithCancellation(ct).ConfigureAwait(false))
            {
                switch (frame)
                {
                    case TextFrame text when !text.IsFinal:
                        // Streaming assistant output — accumulate, flush at natural breakpoints.
                        buffer.Append(text.Text);
                        await foreach (var audio in FlushSentencesAsync(buffer, finalFlush: false, EnsureTurnCts(ref turnCts, ct)).ConfigureAwait(false))
                        {
                            yield return audio;
                        }
                        // Forward the partial text so downstream sinks can render captions.
                        yield return frame;
                        break;

                    case TextFrame { IsFinal: true } finalText:
                        // A "final" TextFrame typically marks the user-side turn, not the assistant's.
                        // We do not synthesise it here — but we DO forward it. If a future use-case
                        // wants user-side TTS, layer a parallel stage rather than overloading this one.
                        yield return frame;
                        break;

                    case ControlFrame ctrl when ctrl.Signal == ControlSignal.Interrupt:
                        buffer.Clear();
                        if (turnCts is { } toCancel)
                        {
                            try { await toCancel.CancelAsync().ConfigureAwait(false); }
                            catch (ObjectDisposedException) { /* already-finished turn */ }
                        }
                        yield return frame;
                        break;

                    case ControlFrame ctrl when ctrl.Signal is ControlSignal.TurnComplete or ControlSignal.Stop:
                        await foreach (var audio in FlushSentencesAsync(buffer, finalFlush: true, EnsureTurnCts(ref turnCts, ct)).ConfigureAwait(false))
                        {
                            yield return audio;
                        }
                        // Reset the turn CTS for the next round; the existing one captured the
                        // synthesis we just completed and we don't want a future interrupt to
                        // try to cancel it.
                        turnCts?.Dispose();
                        turnCts = null;
                        yield return frame;
                        break;

                    default:
                        yield return frame;
                        break;
                }
            }
        }
        finally
        {
            turnCts?.Dispose();
        }
    }

    /// <summary>
    /// Walk the buffer looking for sentence terminators. When found, slice the sentence off the
    /// front, synthesise it, and yield the resulting audio. On <paramref name="finalFlush"/>
    /// the remaining buffer (if any) is synthesised wholesale even without a terminator.
    /// </summary>
    private async IAsyncEnumerable<PipelineFrame> FlushSentencesAsync(StringBuilder buffer, bool finalFlush, [EnumeratorCancellation] CancellationToken turnCt)
    {
        while (true)
        {
            var rawText = buffer.ToString();
            var cut = IndexOfAny(rawText, SentenceTerminators);

            string sentence;
            if (cut >= 0)
            {
                // Include the terminator in the sentence we synthesise so prosody is preserved.
                sentence = rawText.Substring(0, cut + 1);
                buffer.Remove(0, cut + 1);
            }
            else if (finalFlush && rawText.Length > 0)
            {
                sentence = rawText;
                buffer.Clear();
            }
            else
            {
                yield break;
            }

            sentence = sentence.Trim();
            if (sentence.Length == 0)
            {
                continue;
            }

            await foreach (var audio in SynthesizeAsync(sentence, turnCt).ConfigureAwait(false))
            {
                yield return audio;
            }
        }
    }

    private async IAsyncEnumerable<PipelineFrame> SynthesizeAsync(string text, [EnumeratorCancellation] CancellationToken turnCt)
    {
        IAsyncEnumerator<TextToSpeechResponseUpdate>? enumerator = null;
        try
        {
            enumerator = _client.GetStreamingAudioAsync(text, _ttsOptions, turnCt).GetAsyncEnumerator(turnCt);
        }
        catch (OperationCanceledException)
        {
            yield break;
        }

        try
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }

                if (!moved)
                {
                    yield break;
                }

                var update = enumerator.Current;
                foreach (var content in update.Contents)
                {
                    if (content is DataContent data)
                    {
                        // KokoroSharp / Kokoro-FastAPI both emit "audio/pcm; rate=24000; channels=1; bits=16"
                        // by convention. Defaults match that; the media type's rate= parameter is
                        // consulted opportunistically (the parser is best-effort — failure falls
                        // back to the default).
                        var (rate, channels) = ParseAudioMediaType(data.MediaType);
                        var audio = new AudioFrame
                        {
                            SampleRate = rate,
                            Channels = channels,
                            Encoding = AudioEncoding.Pcm16Le,
                            Pcm = data.Data,
                        };
                        yield return new AudioOutputFrame(audio);
                    }
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Lazily allocate the per-turn CTS — we want one CTS shared across all the synthesis
    /// calls within a single turn so an Interrupt cancels every in-flight piece at once.
    /// </summary>
    private static CancellationToken EnsureTurnCts(ref CancellationTokenSource? turnCts, CancellationToken ct)
    {
        turnCts ??= CancellationTokenSource.CreateLinkedTokenSource(ct);
        return turnCts.Token;
    }

    private static int IndexOfAny(string source, char[] terminators)
    {
        for (var i = 0; i < source.Length; i++)
        {
            for (var j = 0; j < terminators.Length; j++)
            {
                if (source[i] == terminators[j])
                {
                    return i;
                }
            }
        }
        return -1;
    }

    /// <summary>
    /// Parse the optional <c>rate=</c> / <c>channels=</c> parameters from a media-type string
    /// like <c>audio/pcm; rate=24000; channels=1; bits=16</c>. Falls back to
    /// <see cref="DefaultSampleRate"/> / mono if the parameters are missing or malformed.
    /// </summary>
    internal static (int Rate, int Channels) ParseAudioMediaType(string? mediaType)
    {
        var rate = DefaultSampleRate;
        var channels = 1;
        if (string.IsNullOrEmpty(mediaType))
        {
            return (rate, channels);
        }

        foreach (var part in mediaType.Split(';'))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("rate=", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(trimmed.AsSpan(5), out var r) && r > 0)
            {
                rate = r;
            }
            else if (trimmed.StartsWith("channels=", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(trimmed.AsSpan(9), out var c) && c > 0)
            {
                channels = c;
            }
        }

        return (rate, channels);
    }
}
