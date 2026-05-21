using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Sutando.LocalInference;
using Sutando.Pipeline;

namespace Sutando.Voice.Local;

/// <summary>
/// A pipeline source stage whose frames are pushed in from outside the pipeline through a
/// <see cref="Channel{T}"/>. The <see cref="LocalPipelineRealtimeClientSession"/> writes the
/// browser's inbound audio and text into the channel; the pipeline reads it back out as the
/// head of the STT → Chat → TTS chain.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a custom source instead of <c>WebSocketAudioSource</c>.</b> <c>WebSocketAudioSource</c>
/// reads raw PCM bytes off an <c>IAudioByteStream</c> and only ever produces
/// <see cref="AudioInputFrame"/>s. The local voice transport also has to inject user <i>text</i>
/// turns (the browser's <c>text</c> envelope) as <see cref="TextFrame"/>s with
/// <c>IsFinal = true</c>, and it already holds decoded <see cref="AudioFrame"/>s rather than a
/// byte stream. A channel-backed source that accepts arbitrary <see cref="PipelineFrame"/>s is
/// the natural fit — the session decides what frame each browser envelope becomes and writes it
/// straight in.
/// </para>
/// <para>
/// <b>Lifecycle.</b> The source emits a <see cref="ControlFrame.Start"/> first, then forwards
/// every frame written to <see cref="Writer"/> until the channel is completed (via
/// <see cref="Complete"/>), then emits a final <see cref="ControlFrame.Stop"/>. Completing the
/// channel is how the session tears the pipeline down on browser disconnect.
/// </para>
/// </remarks>
public sealed class ChannelPipelineSource : IPipelineStage<Unit, PipelineFrame>
{
    private readonly Channel<PipelineFrame> _channel;

    /// <summary>Create a new channel-backed source.</summary>
    /// <remarks>
    /// The backing channel is unbounded: the session's inbound WebSocket pump must never block
    /// on a slow pipeline (back-pressure is applied on the <i>output</i> side by the bounded
    /// inter-stage links instead). Browser audio arrives in small ~20 ms chunks, so an unbounded
    /// head channel cannot grow without limit in practice.
    /// </remarks>
    public ChannelPipelineSource()
    {
        _channel = Channel.CreateUnbounded<PipelineFrame>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>
    /// The writer the session pushes inbound frames into. Safe to call from the WebSocket
    /// inbound pump while the pipeline drains the reader side.
    /// </summary>
    public ChannelWriter<PipelineFrame> Writer => _channel.Writer;

    /// <summary>
    /// Mark the source complete. After the in-flight frames drain, the source emits a final
    /// <see cref="ControlFrame.Stop"/> and the pipeline tears down. Idempotent.
    /// </summary>
    public void Complete() => _channel.Writer.TryComplete();

    /// <inheritdoc/>
    public async IAsyncEnumerable<PipelineFrame> ProcessAsync(
        IAsyncEnumerable<Unit> source,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // A Start control frame at the top of the stream lets downstream stages (re)initialise
        // per-turn state — the same cue WebSocketAudioSource emits.
        yield return ControlFrame.Start;

        await foreach (var frame in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return frame;
        }

        // Channel completed (browser disconnect / session teardown) — signal graceful
        // end-of-stream so the chat / TTS stages flush any buffered turn before exiting.
        yield return ControlFrame.Stop;
    }
}
