using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Sutando.LocalInference;

namespace Sutando.Pipeline;

/// <summary>
/// Base type for every frame that flows between pipeline stages.
/// </summary>
/// <remarks>
/// <para>
/// Modelled as an abstract record so concrete frame kinds form a discriminated union over
/// the same root. Stages typically <c>switch</c> on the runtime type — frames a stage does
/// not transform are forwarded downstream unchanged (the "transparent composition" rule
/// from Pipecat's <c>FrameProcessor</c>).
/// </para>
/// <para>
/// Each frame carries an <see cref="Id"/> (a random GUID per instance) and a
/// <see cref="CreatedAt"/> timestamp. Stages may use these for tracing / metrics; they're
/// not load-bearing for ordering (the channel-per-link plumbing guarantees in-order delivery).
/// </para>
/// </remarks>
[Experimental("SUTANDO001")]
public abstract record PipelineFrame
{
    /// <summary>Per-frame identifier. Useful for tracing across stages.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>When the frame was constructed.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Inbound microphone PCM. Produced by audio-source stages; consumed by VAD / STT.</summary>
/// <param name="Audio">The PCM audio frame.</param>
[Experimental("SUTANDO001")]
public sealed record AudioInputFrame(AudioFrame Audio) : PipelineFrame;

/// <summary>Outbound speaker PCM. Produced by TTS; consumed by audio-sink stages.</summary>
/// <param name="Audio">The PCM audio frame.</param>
[Experimental("SUTANDO001")]
public sealed record AudioOutputFrame(AudioFrame Audio) : PipelineFrame;

/// <summary>
/// Text moving between stages — STT transcription output, LLM chat output, prompt input, etc.
/// </summary>
/// <param name="Text">The text payload.</param>
/// <param name="IsFinal">
/// <see langword="true"/> when this is the terminal chunk of a logical turn (an STT final
/// transcript or the last LLM streaming update). Downstream stages typically gate
/// turn-boundary work (TTS synthesis, tool dispatch) on this flag.
/// </param>
[Experimental("SUTANDO001")]
public sealed record TextFrame(string Text, bool IsFinal) : PipelineFrame;

/// <summary>A VAD transition emitted by the <see cref="VadStage"/>.</summary>
/// <param name="Event">The underlying detector event.</param>
[Experimental("SUTANDO001")]
public sealed record VadFrame(VadEvent Event) : PipelineFrame;

/// <summary>An LLM tool-call invocation. Produced by the chat stage; consumed by a tool-dispatch stage.</summary>
/// <param name="Name">The tool name.</param>
/// <param name="Arguments">The tool arguments, as raw JSON (the model's payload before validation).</param>
/// <param name="CallId">Optional caller-supplied correlation id, threaded back through the matching <see cref="ToolResultFrame"/>.</param>
[Experimental("SUTANDO001")]
public sealed record ToolCallFrame(string Name, JsonElement Arguments, string? CallId = null) : PipelineFrame;

/// <summary>The outcome of a tool execution. Produced by a tool-runner stage; typically fed back into the chat stage.</summary>
/// <param name="Name">The tool name (matches the originating <see cref="ToolCallFrame.Name"/>).</param>
/// <param name="Result">The result payload — typically JSON, but free-form text is allowed.</param>
/// <param name="CallId">Optional caller-supplied correlation id matching <see cref="ToolCallFrame.CallId"/>.</param>
/// <param name="IsError"><see langword="true"/> when the result represents a failure (the chat stage may surface it as a model-visible error).</param>
[Experimental("SUTANDO001")]
public sealed record ToolResultFrame(string Name, string Result, string? CallId = null, bool IsError = false) : PipelineFrame;

/// <summary>
/// A non-data control signal that flows through the same stream as the data frames. Stages
/// inspect the <see cref="Signal"/> and react accordingly (start their turn, cancel in-flight
/// work, flush, etc.). Stages that don't recognise a signal forward the frame downstream
/// unchanged.
/// </summary>
/// <param name="Signal">Which control signal this frame carries.</param>
[Experimental("SUTANDO001")]
public sealed record ControlFrame(ControlSignal Signal) : PipelineFrame
{
    /// <summary>Singleton <see cref="ControlSignal.Start"/> frame for callers that don't need a unique id.</summary>
    public static ControlFrame Start { get; } = new(ControlSignal.Start);

    /// <summary>Singleton <see cref="ControlSignal.Stop"/> frame.</summary>
    public static ControlFrame Stop { get; } = new(ControlSignal.Stop);

    /// <summary>Singleton <see cref="ControlSignal.Interrupt"/> frame. Stages with in-flight per-turn work cancel it on receipt.</summary>
    public static ControlFrame Interrupt { get; } = new(ControlSignal.Interrupt);

    /// <summary>Singleton <see cref="ControlSignal.TurnComplete"/> frame, emitted by the chat / TTS stages at the end of a turn.</summary>
    public static ControlFrame TurnComplete { get; } = new(ControlSignal.TurnComplete);
}

/// <summary>The discriminator for <see cref="ControlFrame"/>.</summary>
[Experimental("SUTANDO001")]
public enum ControlSignal
{
    /// <summary>
    /// Pipeline / turn boundary marker — sent at the top of the stream by a source, or at the
    /// start of a new turn by a transport stage. Stages may use it to (re)initialise per-turn
    /// state. Forwarded downstream unchanged.
    /// </summary>
    Start = 0,

    /// <summary>
    /// Graceful termination signal — sent at the end of the stream. Stages should complete any
    /// in-flight work, forward the signal downstream, and exit cleanly. The pipeline's
    /// <see cref="CancellationToken"/> remains live; this is a cooperative end-of-stream marker.
    /// </summary>
    Stop,

    /// <summary>
    /// Interruption — the user has started a new turn while the model was still speaking.
    /// Stages with in-flight per-turn work (chat completion, TTS synthesis) MUST cancel that
    /// work, then forward the frame downstream. The pipeline-level CT is not cancelled by an
    /// interrupt; only the per-turn CTS owned by each stage is.
    /// </summary>
    Interrupt,

    /// <summary>
    /// End-of-turn marker — emitted by the chat stage after its final
    /// <see cref="TextFrame.IsFinal"/> = <see langword="true"/> chunk, or by the TTS stage
    /// after the last audio chunk has been emitted. Downstream sinks may use it for UI cues.
    /// </summary>
    TurnComplete,
}
