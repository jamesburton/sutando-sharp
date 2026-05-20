using System.Diagnostics.CodeAnalysis;

namespace Sutando.Pipeline;

/// <summary>
/// A pipeline stage — a transformer that consumes an <see cref="IAsyncEnumerable{T}"/> of
/// <typeparamref name="TIn"/> frames and produces an <see cref="IAsyncEnumerable{T}"/> of
/// <typeparamref name="TOut"/> frames.
/// </summary>
/// <remarks>
/// <para>
/// The single-method shape mirrors LINQ-over-async / Reactive pipelines and the upstream
/// Pipecat pattern (one process_frame per stage; stages are chained linearly). It also keeps
/// composition mechanical: <see cref="Pipeline"/> wires stages by handing one stage's output
/// to the next stage's <see cref="ProcessAsync(IAsyncEnumerable{TIn}, CancellationToken)"/>
/// input.
/// </para>
/// <para>
/// <b>Sources</b> are stages whose <typeparamref name="TIn"/> is <see cref="Unit"/> — they
/// produce frames without consuming any. Implementations may pass <see cref="EmptyAsync"/>
/// (or any dummy source) as the input; the source ignores it.
/// </para>
/// <para>
/// <b>Sinks</b> are stages whose <typeparamref name="TOut"/> is <see cref="Unit"/> — they
/// consume frames without producing any. Implementations either yield no values (returning
/// an empty <see cref="IAsyncEnumerable{T}"/>) or perform their side-effects and yield a
/// single <see cref="Unit.Value"/> on completion; the orchestrator does not care which.
/// </para>
/// <para>
/// <b>Backpressure</b> and <b>cancellation</b> are the caller's concern, not the stage's: the
/// <see cref="Pipeline"/> orchestrator wraps each link with a bounded
/// <see cref="System.Threading.Channels.Channel{T}"/> so a slow downstream consumer naturally
/// throttles the upstream producer. Stages should honour the <see cref="CancellationToken"/>
/// promptly — typically by passing it into the <c>await foreach</c> over their input.
/// </para>
/// <para>
/// <b>Interruption</b>: stages with in-flight per-turn work (chat completions, TTS) should
/// detect a <see cref="ControlFrame"/> with <see cref="ControlSignal.Interrupt"/> inside their
/// input stream and cancel that work using a linked per-turn CTS, then forward the control
/// frame downstream. See <see cref="ChatStage"/> for the canonical implementation. The
/// pipeline-level <see cref="CancellationToken"/> remains live across interruptions; only
/// per-turn CTSs are cancelled.
/// </para>
/// </remarks>
/// <typeparam name="TIn">The frame type consumed from upstream (<see cref="Unit"/> for sources).</typeparam>
/// <typeparam name="TOut">The frame type produced downstream (<see cref="Unit"/> for sinks).</typeparam>
[Experimental("SUTANDO001")]
public interface IPipelineStage<TIn, TOut>
{
    /// <summary>
    /// Run the stage. Consumes <paramref name="source"/> lazily and yields outputs as they
    /// become available. The returned enumerable should complete when <paramref name="source"/>
    /// completes or <paramref name="ct"/> fires.
    /// </summary>
    /// <param name="source">Upstream frames. May be empty for sources.</param>
    /// <param name="ct">Cancellation token. The stage MUST honour it promptly.</param>
    /// <returns>The transformed stream of frames.</returns>
    IAsyncEnumerable<TOut> ProcessAsync(IAsyncEnumerable<TIn> source, CancellationToken ct);

    /// <summary>A canonical empty <see cref="IAsyncEnumerable{T}"/>, useful for sources that ignore their input.</summary>
    public static async IAsyncEnumerable<TIn> EmptyAsync()
    {
        await Task.Yield();
        yield break;
    }
}
