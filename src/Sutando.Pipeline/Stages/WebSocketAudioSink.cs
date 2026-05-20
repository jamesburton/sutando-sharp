using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Sutando.Pipeline.Stages;

/// <summary>
/// A pipeline sink that writes <see cref="AudioOutputFrame"/>s' PCM bytes to a WebSocket-like
/// transport (via <see cref="IAudioByteStream"/>). Non-audio frames pass through silently.
/// </summary>
/// <remarks>
/// <para>
/// The sink does not own the underlying transport's lifetime — the caller is responsible for
/// closing it after the pipeline completes. This matches Pipecat's convention where sinks are
/// "leaves" rather than transport owners.
/// </para>
/// <para>
/// <b>Backpressure</b>: a slow underlying transport (e.g. a network-limited WebSocket) will
/// hold up <see cref="IAudioByteStream.WriteAsync"/>, which in turn fills the pipeline's bounded
/// channels and naturally throttles the upstream TTS / chat producers.
/// </para>
/// </remarks>
[Experimental("SUTANDO001")]
public sealed class WebSocketAudioSink : IPipelineStage<PipelineFrame, Unit>
{
    private readonly IAudioByteStream _stream;

    /// <summary>Initialise the sink.</summary>
    /// <param name="stream">The byte stream to write PCM into. Caller owns its lifetime.</param>
    public WebSocketAudioSink(IAudioByteStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<Unit> ProcessAsync(
        IAsyncEnumerable<PipelineFrame> source,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);

        await foreach (var frame in source.WithCancellation(ct).ConfigureAwait(false))
        {
            if (frame is AudioOutputFrame audio)
            {
                await _stream.WriteAsync(audio.Audio.Pcm, ct).ConfigureAwait(false);
            }
            // Non-audio frames (text, control, VAD) are discarded by the sink — the consumer
            // of the WS connection only wants PCM bytes. If a future protocol grows JSON
            // envelopes around the audio, swap in a richer sink that serialises each frame.
        }

        // The single Unit yielded here is the conventional "stage completed cleanly" signal.
        // The pipeline orchestrator ignores it; we yield it so the IAsyncEnumerable<Unit>
        // return type is non-empty in case any future consumer inspects it.
        yield return Unit.Value;
    }
}
