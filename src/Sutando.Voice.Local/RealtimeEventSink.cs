using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Sutando.Pipeline;
using Sutando.Realtime;

namespace Sutando.Voice.Local;

/// <summary>
/// The pipeline's terminal sink for the local voice transport. Translates each
/// <see cref="PipelineFrame"/> the chat / TTS chain produces into the MEAI
/// <see cref="RealtimeServerMessage"/> shape that <see cref="LocalPipelineRealtimeClientSession"/>
/// surfaces through <see cref="IRealtimeClientSession.GetStreamingResponseAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this replaces <c>WebSocketAudioSink</c>.</b> <c>WebSocketAudioSink</c> writes raw PCM
/// bytes straight to a byte-stream transport. For the local voice transport the real "transport"
/// is the <see cref="IRealtimeClientSession"/> event stream — the voice WS server consumes
/// <see cref="RealtimeServerMessage"/>s (via <c>VoiceSession</c> + <c>MeaiToSutandoEventAdapter</c>),
/// not raw bytes. So this sink emits MEAI messages instead of bytes; both sinks coexist in the
/// codebase because they target different transports.
/// </para>
/// <para>
/// <b>Frame → message mapping</b> — each row is verified against
/// <c>Sutando.Realtime.MeaiToSutandoEventAdapter.Map</c>, which is what the voice WS server's
/// <c>VoiceSession</c> read-loop runs the messages through:
/// <list type="bullet">
///   <item><description><see cref="AudioOutputFrame"/> → <c>OutputAudioDelta</c> (base64 PCM) → <c>RealtimeAudioOutput</c>.</description></item>
///   <item><description>final <see cref="TextFrame"/> (user-side STT transcript) → <c>InputAudioTranscriptionCompleted</c> → <c>RealtimeInputTranscription</c>.</description></item>
///   <item><description>non-final <see cref="TextFrame"/> (streaming assistant text) → <c>OutputAudioTranscriptionDelta</c> → <c>RealtimeOutputTranscription</c>.</description></item>
///   <item><description><see cref="ControlFrame"/> <c>TurnComplete</c> → <c>ResponseDone</c> (non-cancelled) → <c>RealtimeTurnComplete</c>.</description></item>
///   <item><description><see cref="ControlFrame"/> <c>Interrupt</c> → <c>ResponseDone</c> (cancelled) → <c>RealtimeInterrupted</c>.</description></item>
/// </list>
/// <see cref="VadFrame"/>s and the <c>Start</c> / <c>Stop</c> control frames have no browser
/// representation and are dropped — the same "transparent composition" discard
/// <c>WebSocketAudioSink</c> applies to non-audio frames.
/// </para>
/// </remarks>
public sealed class RealtimeEventSink : IPipelineStage<PipelineFrame, Unit>
{
    private readonly ChannelWriter<RealtimeServerMessage> _output;

    /// <summary>Create a sink that writes mapped messages into <paramref name="output"/>.</summary>
    /// <param name="output">
    /// The session's outbound message channel. The sink writes mapped messages here; the
    /// session drains them through <see cref="IRealtimeClientSession.GetStreamingResponseAsync"/>.
    /// </param>
    public RealtimeEventSink(ChannelWriter<RealtimeServerMessage> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<Unit> ProcessAsync(
        IAsyncEnumerable<PipelineFrame> source,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);

        await foreach (var frame in source.WithCancellation(ct).ConfigureAwait(false))
        {
            var message = MapFrame(frame);
            if (message is not null)
            {
                await _output.WriteAsync(message, ct).ConfigureAwait(false);
            }
        }

        // Conventional "sink completed cleanly" signal — the pipeline orchestrator ignores it.
        yield return Unit.Value;
    }

    /// <summary>
    /// Translate a single pipeline frame into the MEAI server-message shape, or return
    /// <see langword="null"/> when the frame has no browser-facing representation.
    /// </summary>
    /// <param name="frame">The pipeline frame.</param>
    /// <returns>The mapped MEAI message, or <see langword="null"/> to drop the frame.</returns>
    /// <remarks>Internal so tests can assert the mapping without standing up a pipeline.</remarks>
    internal static RealtimeServerMessage? MapFrame(PipelineFrame frame) => frame switch
    {
        AudioOutputFrame audio => new OutputTextAudioRealtimeServerMessage(RealtimeServerMessageType.OutputAudioDelta)
        {
            // MeaiToSutandoEventAdapter expects base64-encoded PCM on .Audio — it decodes it back
            // to bytes and stamps the sample rate from the session's RealtimeAudioConfig (24 kHz).
            Audio = Convert.ToBase64String(audio.Audio.Pcm.Span),
        },

        // A final TextFrame is the user-side STT transcript (ChatStage forwards it before the
        // assistant turn). Surface it as the input transcription so the harness shows what the
        // user said.
        TextFrame { IsFinal: true } userText => new OutputTextAudioRealtimeServerMessage(
            RealtimeServerMessageType.InputAudioTranscriptionCompleted)
        {
            Text = userText.Text,
        },

        // A non-final TextFrame is a streaming assistant chunk — surface it as the output
        // transcription so the harness can render captions alongside the synthesised audio.
        TextFrame assistantText => new OutputTextAudioRealtimeServerMessage(
            RealtimeServerMessageType.OutputAudioTranscriptionDelta)
        {
            Text = assistantText.Text,
        },

        ControlFrame { Signal: ControlSignal.TurnComplete } => new ResponseCreatedRealtimeServerMessage(
            RealtimeServerMessageType.ResponseDone),

        ControlFrame { Signal: ControlSignal.Interrupt } => new ResponseCreatedRealtimeServerMessage(
            RealtimeServerMessageType.ResponseDone)
        {
            // MeaiToSutandoEventAdapter maps a Cancelled ResponseDone onto RealtimeInterrupted.
            Status = RealtimeResponseStatus.Cancelled,
        },

        // VadFrames, AudioInputFrames, Start / Stop control frames: no browser representation.
        _ => null,
    };
}
