using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace Sutando.Pipeline;

/// <summary>
/// Fluent builder that types-check stage composition at compile time. Start from
/// <see cref="Pipeline.Builder"/>, then call <see cref="StartWith{TOut}(IPipelineStage{Unit, TOut})"/>
/// to plant a source — the returned <see cref="PipelineBuilder{TCurrent}"/> ratchets the type
/// through each <c>Then</c> call.
/// </summary>
/// <remarks>
/// <para>
/// The two-phase shape (untyped builder → typed builder) is required because we cannot infer
/// the "current frame type" until the source is planted. Sources are <c>IPipelineStage&lt;Unit,
/// TOut&gt;</c> — the source's <c>TOut</c> becomes the next stage's <c>TIn</c>.
/// </para>
/// </remarks>
[Experimental("SUTANDO001")]
public sealed class PipelineBuilder
{
    private PipelineOptions _options = new();
    private ILogger<Pipeline>? _logger;

    internal PipelineBuilder() { }

    /// <summary>Override the runtime knobs (channel capacity, etc.).</summary>
    public PipelineBuilder WithOptions(PipelineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        return this;
    }

    /// <summary>Attach a logger to the pipeline for diagnostics around stage faults.</summary>
    public PipelineBuilder WithLogger(ILogger<Pipeline> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        return this;
    }

    /// <summary>
    /// Plant a source stage — an <see cref="IPipelineStage{TIn,TOut}"/> with
    /// <see cref="Unit"/> as its input. Returns the typed builder ready to take
    /// <see cref="PipelineBuilder{TCurrent}.Then{TNext}"/> calls.
    /// </summary>
    /// <typeparam name="TOut">The frame type the source produces.</typeparam>
    /// <param name="source">The source stage.</param>
    public PipelineBuilder<TOut> StartWith<TOut>(IPipelineStage<Unit, TOut> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var stages = new List<Pipeline.StageEntry> { new Pipeline.StageEntry<Unit, TOut>(source) };
        return new PipelineBuilder<TOut>(stages, _options, _logger);
    }
}

/// <summary>
/// Typed continuation of <see cref="PipelineBuilder"/> — <typeparamref name="TCurrent"/> is
/// the frame type produced by the most-recently-added stage and consumed by the next.
/// </summary>
/// <typeparam name="TCurrent">The current "tip" frame type.</typeparam>
[Experimental("SUTANDO001")]
public sealed class PipelineBuilder<TCurrent>
{
    private readonly List<Pipeline.StageEntry> _stages;
    private readonly PipelineOptions _options;
    private readonly ILogger<Pipeline>? _logger;

    internal PipelineBuilder(List<Pipeline.StageEntry> stages, PipelineOptions options, ILogger<Pipeline>? logger)
    {
        _stages = stages;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Append a transformer stage. The stage's input type must match the current tip
    /// <typeparamref name="TCurrent"/>; its output type <typeparamref name="TNext"/> becomes
    /// the new tip.
    /// </summary>
    /// <typeparam name="TNext">The frame type produced by <paramref name="stage"/>.</typeparam>
    /// <param name="stage">The stage to append.</param>
    public PipelineBuilder<TNext> Then<TNext>(IPipelineStage<TCurrent, TNext> stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        _stages.Add(new Pipeline.StageEntry<TCurrent, TNext>(stage));
        return new PipelineBuilder<TNext>(_stages, _options, _logger);
    }

    /// <summary>
    /// Append a sink stage — an <see cref="IPipelineStage{TIn,TOut}"/> whose output is
    /// <see cref="Unit"/>. Returns a finalised <see cref="PipelineCompletionBuilder"/> that
    /// only supports <see cref="PipelineCompletionBuilder.Build"/>.
    /// </summary>
    /// <param name="sink">The sink stage.</param>
    public PipelineCompletionBuilder EndsWith(IPipelineStage<TCurrent, Unit> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _stages.Add(new Pipeline.StageEntry<TCurrent, Unit>(sink));
        return new PipelineCompletionBuilder(_stages, _options, _logger);
    }

    /// <summary>
    /// Finalise the pipeline without an explicit sink — useful for tests or for pipelines
    /// whose terminal stage is a "naturally sinking" transformer (one that performs IO and
    /// happens to type as <c>IPipelineStage&lt;X, Unit&gt;</c>). Equivalent to constructing
    /// a <see cref="Pipeline"/> directly.
    /// </summary>
    public Pipeline Build() => new(_stages, _options, _logger);
}

/// <summary>
/// Finalised builder after a sink has been planted. The only operation it exposes is
/// <see cref="Build"/>.
/// </summary>
[Experimental("SUTANDO001")]
public sealed class PipelineCompletionBuilder
{
    private readonly List<Pipeline.StageEntry> _stages;
    private readonly PipelineOptions _options;
    private readonly ILogger<Pipeline>? _logger;

    internal PipelineCompletionBuilder(List<Pipeline.StageEntry> stages, PipelineOptions options, ILogger<Pipeline>? logger)
    {
        _stages = stages;
        _options = options;
        _logger = logger;
    }

    /// <summary>Construct the immutable pipeline ready for <see cref="Pipeline.RunAsync"/>.</summary>
    public Pipeline Build() => new(_stages, _options, _logger);
}
