using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Sutando.Pipeline;

namespace Sutando.Tests.Pipeline;

/// <summary>
/// Round-trip composition test for the <see cref="Pipeline"/> orchestrator. A three-stage
/// pipeline (numbers source → identity → collecting sink) must deliver every input value to
/// the sink in order and without loss.
/// </summary>
[SuppressMessage("Usage", "SUTANDO001", Justification = "Tests for our own experimental surface.")]
public sealed class PipelineCompositionTests
{
    [Fact]
    public async Task ThreeStagePipeline_DeliversAllValuesInOrder()
    {
        var values = new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var sink = new CollectingSink();

        var pipeline = Sutando.Pipeline.Pipeline.Builder()
            .StartWith(new IntSource(values))
            .Then(new IdentityTransformer())
            .EndsWith(sink)
            .Build();

        await pipeline.RunAsync(CancellationToken.None);

        Assert.Equal(values, sink.Collected);
    }

    [Fact]
    public async Task SingleStagePipeline_FromSourceToSink_Works()
    {
        // A two-stage pipeline (source + sink) is a useful minimum — proves the orchestrator
        // doesn't require an intermediate transformer.
        var values = new[] { 42 };
        var sink = new CollectingSink();

        var pipeline = Sutando.Pipeline.Pipeline.Builder()
            .StartWith(new IntSource(values))
            .EndsWith(sink)
            .Build();

        await pipeline.RunAsync(CancellationToken.None);

        Assert.Equal(values, sink.Collected);
    }

    [Fact]
    public async Task EmptySource_CompletesGracefully()
    {
        // No inputs at all — every stage must still see end-of-stream and exit cleanly.
        var sink = new CollectingSink();

        var pipeline = Sutando.Pipeline.Pipeline.Builder()
            .StartWith(new IntSource(Array.Empty<int>()))
            .Then(new IdentityTransformer())
            .EndsWith(sink)
            .Build();

        await pipeline.RunAsync(CancellationToken.None);

        Assert.Empty(sink.Collected);
    }

    [Fact]
    public async Task Pipeline_IsSingleUse_ThrowsOnSecondRun()
    {
        var pipeline = Sutando.Pipeline.Pipeline.Builder()
            .StartWith(new IntSource(new[] { 1 }))
            .EndsWith(new CollectingSink())
            .Build();

        await pipeline.RunAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await pipeline.RunAsync(CancellationToken.None))
            ;
    }

    [Fact]
    public async Task StageFault_PropagatesAsAggregateException()
    {
        var pipeline = Sutando.Pipeline.Pipeline.Builder()
            .StartWith(new IntSource(new[] { 1, 2, 3 }))
            .Then(new FaultingTransformer())
            .EndsWith(new CollectingSink())
            .Build();

        var ex = await Assert.ThrowsAsync<AggregateException>(async () =>
            await pipeline.RunAsync(CancellationToken.None))
            ;

        Assert.Contains(ex.InnerExceptions, e => e is InvalidOperationException);
    }

    // --- helper stages ---

    /// <summary>Source that emits a fixed sequence of ints.</summary>
    private sealed class IntSource : IPipelineStage<Unit, int>
    {
        private readonly int[] _values;
        public IntSource(int[] values) => _values = values;

        public async IAsyncEnumerable<int> ProcessAsync(IAsyncEnumerable<Unit> source, [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var v in _values)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return v;
            }
        }
    }

    /// <summary>Identity transformer — forwards every input as output unchanged.</summary>
    private sealed class IdentityTransformer : IPipelineStage<int, int>
    {
        public async IAsyncEnumerable<int> ProcessAsync(IAsyncEnumerable<int> source, [EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var v in source.WithCancellation(ct))
            {
                yield return v;
            }
        }
    }

    /// <summary>Faulting transformer — throws on the second input.</summary>
    private sealed class FaultingTransformer : IPipelineStage<int, int>
    {
        public async IAsyncEnumerable<int> ProcessAsync(IAsyncEnumerable<int> source, [EnumeratorCancellation] CancellationToken ct)
        {
            var count = 0;
            await foreach (var v in source.WithCancellation(ct))
            {
                if (++count == 2)
                {
                    throw new InvalidOperationException("intentional test fault");
                }
                yield return v;
            }
        }
    }

    /// <summary>Sink that records every input it sees.</summary>
    internal sealed class CollectingSink : IPipelineStage<int, Unit>
    {
        public List<int> Collected { get; } = new();

        public async IAsyncEnumerable<Unit> ProcessAsync(IAsyncEnumerable<int> source, [EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var v in source.WithCancellation(ct))
            {
                Collected.Add(v);
            }
            yield break;
        }
    }
}
