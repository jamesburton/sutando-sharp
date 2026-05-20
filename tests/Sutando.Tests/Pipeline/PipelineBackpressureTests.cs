using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Sutando.Pipeline;

namespace Sutando.Tests.Pipeline;

/// <summary>
/// The bounded channel between stages must enforce backpressure: a fast producer feeding a
/// slow consumer should NOT be allowed to race ahead by more than the channel capacity
/// (plus one item buffered by the consumer's stage logic).
/// </summary>
[SuppressMessage("Usage", "SUTANDO001", Justification = "Tests for our own experimental surface.")]
public sealed class PipelineBackpressureTests
{
    [Fact]
    public async Task SlowConsumer_DoesNotLetProducerRaceAhead()
    {
        // Capacity 2 keeps the test fast and the assertion tight: with a slow consumer the
        // producer is allowed at most "capacity + the item the consumer is currently working
        // on + the item the producer is about to write" ahead — call it `capacity + 2`. We
        // assert an upper bound large enough to absorb the orchestrator's own bookkeeping
        // (one item buffered inside each stage's yield) but small enough to fail loudly if
        // the channel ever switched to unbounded buffering.
        const int Capacity = 2;
        const int InputCount = 100;
        const int InFlightCeiling = Capacity + 4;

        var producer = new CountingProducer(InputCount);
        var consumer = new SlowSink();

        var pipeline = Sutando.Pipeline.Pipeline.Builder()
            .WithOptions(new PipelineOptions { ChannelCapacity = Capacity })
            .StartWith(producer)
            .EndsWith(consumer)
            .Build();

        var runTask = pipeline.RunAsync(CancellationToken.None);

        // While the pipeline is running, sample the gap between produced and consumed values.
        // If the channel was unbounded, this would peak at ~InputCount; with proper
        // backpressure it stays under InFlightCeiling.
        var maxGap = 0;
        var sampleTask = Task.Run(async () =>
        {
            while (!runTask.IsCompleted)
            {
                var gap = producer.Produced - consumer.Consumed;
                if (gap > maxGap)
                {
                    maxGap = gap;
                }
                await Task.Delay(1, CancellationToken.None);
            }
        }, CancellationToken.None);

        await runTask;
        await sampleTask;

        Assert.Equal(InputCount, consumer.Consumed);
        Assert.True(maxGap <= InFlightCeiling, $"Producer raced too far ahead: maxGap = {maxGap}, ceiling = {InFlightCeiling}.");
    }

    /// <summary>Source that produces a fixed count of ints as fast as possible and counts emissions.</summary>
    private sealed class CountingProducer : IPipelineStage<Unit, int>
    {
        private readonly int _count;
        private int _produced;

        public CountingProducer(int count) => _count = count;

        public int Produced => Volatile.Read(ref _produced);

        public async IAsyncEnumerable<int> ProcessAsync(IAsyncEnumerable<Unit> source, [EnumeratorCancellation] CancellationToken ct)
        {
            for (var i = 0; i < _count; i++)
            {
                ct.ThrowIfCancellationRequested();
                Interlocked.Increment(ref _produced);
                yield return i;
                // No await — we want the producer to push as fast as possible. With the
                // bounded channel doing its job the WriteAsync inside the orchestrator's
                // glue will block once the consumer falls behind.
                await Task.Yield();
            }
        }
    }

    /// <summary>Sink that throttles consumption to a known pace.</summary>
    private sealed class SlowSink : IPipelineStage<int, Unit>
    {
        private int _consumed;

        public int Consumed => Volatile.Read(ref _consumed);

        public async IAsyncEnumerable<Unit> ProcessAsync(IAsyncEnumerable<int> source, [EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var _ in source.WithCancellation(ct))
            {
                // 2 ms per item is plenty slow to let the producer race ahead if backpressure
                // is broken; fast enough to keep the test under a second total.
                await Task.Delay(2, ct);
                Interlocked.Increment(ref _consumed);
            }
            yield break;
        }
    }
}
