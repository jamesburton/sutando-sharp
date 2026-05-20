using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sutando.Pipeline;

/// <summary>
/// A fully-wired pipeline ready to run. Construct via <see cref="Pipeline.Builder"/>.
/// </summary>
/// <remarks>
/// <para>
/// The pipeline owns a series of <see cref="IPipelineStage{TIn,TOut}"/> instances connected by
/// bounded <see cref="Channel{T}"/> links. Each stage runs as a long-lived task that:
/// <list type="number">
///   <item><description>reads from its upstream channel as an <see cref="IAsyncEnumerable{T}"/>,</description></item>
///   <item><description>invokes its <see cref="IPipelineStage{TIn,TOut}.ProcessAsync"/>,</description></item>
///   <item><description>writes each yielded output to the downstream channel,</description></item>
///   <item><description>completes the downstream channel when its own output enumerable completes.</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Backpressure</b>: each channel is bounded (capacity from
/// <see cref="PipelineOptions.ChannelCapacity"/>) and the default <see cref="BoundedChannelFullMode.Wait"/>
/// makes a slow consumer block its upstream producer naturally — no buffering blow-up.
/// </para>
/// <para>
/// <b>Cancellation</b>: <see cref="RunAsync(CancellationToken)"/> creates a linked CTS that is
/// signalled on (a) caller cancellation, (b) any stage faulting. The cancellation flows into
/// each stage via the <see cref="CancellationToken"/> argument it received and is also surfaced
/// by completing every channel with the cancellation exception, so even stages that are mid-
/// <c>await foreach</c> on a slow upstream unblock promptly.
/// </para>
/// <para>
/// <b>Interruption</b> is a separate, in-band signal — see <see cref="ControlFrame.Interrupt"/>.
/// Stages handle it themselves; the pipeline never cancels the top-level CT on interrupt.
/// </para>
/// </remarks>
[Experimental("SUTANDO001")]
public sealed class Pipeline
{
    private readonly IReadOnlyList<StageEntry> _stages;
    private readonly PipelineOptions _options;
    private readonly ILogger<Pipeline> _logger;
    private int _ran;

    internal Pipeline(IReadOnlyList<StageEntry> stages, PipelineOptions options, ILogger<Pipeline>? logger)
    {
        if (stages.Count == 0)
        {
            throw new ArgumentException("A pipeline must contain at least one stage.", nameof(stages));
        }
        _stages = stages;
        _options = options;
        _logger = logger ?? NullLogger<Pipeline>.Instance;
    }

    /// <summary>The number of stages in this pipeline (including source / sink, if any).</summary>
    public int StageCount => _stages.Count;

    /// <summary>Begin a new <see cref="PipelineBuilder"/>.</summary>
    /// <returns>A fresh builder.</returns>
    public static PipelineBuilder Builder() => new();

    /// <summary>
    /// Run the pipeline to completion. The returned task completes when every stage finishes
    /// (either because the source completed naturally or the <paramref name="ct"/> fired or a
    /// stage faulted).
    /// </summary>
    /// <param name="ct">Top-level cancellation token. Cancelling it tears down every stage.</param>
    /// <returns>A task that completes when every stage has finished.</returns>
    /// <exception cref="InvalidOperationException">The pipeline has already been run (single-use).</exception>
    /// <exception cref="AggregateException">One or more stages faulted. The inner exceptions carry the originals.</exception>
    public async Task RunAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _ran, 1) == 1)
        {
            throw new InvalidOperationException("Pipeline has already been run. Pipelines are single-use; build a new one.");
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = linkedCts.Token;

        // One bounded channel per link between adjacent stages. For N stages we need N+1 channels:
        // a "before stage 0" channel (which we feed an empty stream into) and an "after stage N-1"
        // channel that we drain to completion. Modelling it this way keeps the per-stage code
        // uniform — every stage reads from one channel and writes to another.
        var channels = new IChannel[_stages.Count + 1];
        for (var i = 0; i < channels.Length; i++)
        {
            channels[i] = _stages.Count > i
                ? _stages[i].CreateInputChannel(_options.ChannelCapacity)
                : _stages[i - 1].CreateOutputChannel(_options.ChannelCapacity);
        }

        // Seed the head channel with a single Start control frame so sources that publish "on
        // any input" have something to react to, then complete it so they exit cleanly. Sources
        // typed as <Unit, ...> ignore the content; they treat the empty enumerable as their cue.
        await channels[0].CompleteWithSeedAsync(token).ConfigureAwait(false);

        var tasks = new Task[_stages.Count];
        for (var i = 0; i < _stages.Count; i++)
        {
            var input = channels[i];
            var output = channels[i + 1];
            var stage = _stages[i];
            var index = i;
            tasks[i] = Task.Run(
                () => RunStageAsync(stage, input, output, index, linkedCts, token),
                CancellationToken.None);
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            // Surface the original aggregate so callers see every stage failure, not just the first.
            var failed = tasks.Where(t => t.IsFaulted).SelectMany(t => t.Exception!.InnerExceptions).ToList();
            if (failed.Count > 0)
            {
                throw new AggregateException("One or more pipeline stages faulted.", failed);
            }
            throw;
        }
    }

    private async Task RunStageAsync(StageEntry stage, IChannel input, IChannel output, int index, CancellationTokenSource linkedCts, CancellationToken ct)
    {
        try
        {
            await stage.RunAsync(input, output, ct).ConfigureAwait(false);
            await output.CompleteAsync(error: null).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cooperative cancellation — complete the downstream channel without an error so
            // downstream stages drain remaining items and exit cleanly themselves.
            await output.CompleteAsync(error: null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pipeline stage #{Index} faulted.", index);
            await output.CompleteAsync(ex).ConfigureAwait(false);
            // Trigger the linked CTS so peers don't hang waiting for their (now-dead) producer.
            try { await linkedCts.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { /* race with successful completion — fine */ }
            throw;
        }
    }

    /// <summary>
    /// Internal contract for a per-link channel. We need to hide the generic parameter from
    /// <see cref="RunStageAsync"/> because adjacent stages can have different frame types
    /// (Unit / PipelineFrame / Unit). The non-generic shape exposes the operations the
    /// orchestrator needs.
    /// </summary>
    internal interface IChannel
    {
        /// <summary>Mark the channel complete with an optional error. Idempotent.</summary>
        ValueTask CompleteAsync(Exception? error);

        /// <summary>
        /// Mark the channel complete and emit no data — used for the head channel so sources
        /// that <c>await foreach</c> over the input see an immediately-empty stream and
        /// don't block.
        /// </summary>
        ValueTask CompleteWithSeedAsync(CancellationToken ct);
    }

    /// <summary>
    /// One stage + the typed channel writers / readers it expects on either side. Built by
    /// <see cref="PipelineBuilder"/>; consumed by <see cref="Pipeline.RunAsync"/> through the
    /// non-generic <see cref="RunAsync"/> shim below.
    /// </summary>
    internal abstract class StageEntry
    {
        public abstract IChannel CreateInputChannel(int capacity);
        public abstract IChannel CreateOutputChannel(int capacity);
        public abstract Task RunAsync(IChannel input, IChannel output, CancellationToken ct);
    }

    internal sealed class StageEntry<TIn, TOut> : StageEntry
    {
        private readonly IPipelineStage<TIn, TOut> _stage;

        public StageEntry(IPipelineStage<TIn, TOut> stage) => _stage = stage;

        public override IChannel CreateInputChannel(int capacity) => new ChannelLink<TIn>(capacity);

        public override IChannel CreateOutputChannel(int capacity) => new ChannelLink<TOut>(capacity);

        public override async Task RunAsync(IChannel input, IChannel output, CancellationToken ct)
        {
            var inputLink = (ChannelLink<TIn>)input;
            var outputLink = (ChannelLink<TOut>)output;

            await foreach (var item in _stage.ProcessAsync(ReadAll(inputLink, ct), ct).WithCancellation(ct).ConfigureAwait(false))
            {
                await outputLink.WriteAsync(item, ct).ConfigureAwait(false);
            }
        }

        private static async IAsyncEnumerable<TIn> ReadAll(ChannelLink<TIn> link, [EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var item in link.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return item;
            }
        }
    }

    /// <summary>
    /// Typed wrapper around a single <see cref="System.Threading.Channels.Channel{T}"/>. Holds
    /// the reader / writer pair and the bounded channel's capacity for diagnostics.
    /// </summary>
    internal sealed class ChannelLink<T> : IChannel
    {
        private readonly Channel<T> _channel;

        public ChannelLink(int capacity)
        {
            // BoundedChannelFullMode.Wait gives us proper backpressure — the upstream producer's
            // WriteAsync awaits until a consumer makes room. This is the load-bearing knob; do
            // NOT flip it to DropOldest / DropWrite unless a specific stage explicitly opts in.
            _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(Math.Max(1, capacity))
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            });
        }

        public ValueTask WriteAsync(T item, CancellationToken ct) => _channel.Writer.WriteAsync(item, ct);

        public IAsyncEnumerable<T> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);

        public ValueTask CompleteAsync(Exception? error)
        {
            _channel.Writer.TryComplete(error);
            return ValueTask.CompletedTask;
        }

        public ValueTask CompleteWithSeedAsync(CancellationToken ct)
        {
            _channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Runtime knobs for a <see cref="Pipeline"/>. Mutable inside the builder; frozen at <see cref="PipelineBuilder.Build"/>.</summary>
[Experimental("SUTANDO001")]
public sealed record PipelineOptions
{
    /// <summary>The default per-link channel capacity. Tuned for low-latency voice — small enough that backpressure is felt quickly, large enough that single-burst writes don't stall.</summary>
    public const int DefaultChannelCapacity = 16;

    /// <summary>
    /// Maximum number of in-flight frames buffered on each inter-stage channel. A small number
    /// (1–4) maximises backpressure sensitivity; a larger one (16–64) tolerates jitter. Default
    /// <see cref="DefaultChannelCapacity"/>.
    /// </summary>
    public int ChannelCapacity { get; init; } = DefaultChannelCapacity;
}
