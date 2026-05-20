using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Sutando.Pipeline;

namespace Sutando.Tests.Pipeline;

/// <summary>
/// Cancelling the top-level <see cref="CancellationToken"/> must tear down every stage
/// promptly. "Promptly" means well under a second — long enough to absorb await-foreach
/// state-machine overhead, short enough to fail loudly if a stage is ignoring the token.
/// </summary>
[SuppressMessage("Usage", "SUTANDO001", Justification = "Tests for our own experimental surface.")]
public sealed class PipelineCancellationTests
{
    [Fact]
    public async Task CancelTopLevelToken_StopsEveryStagePromptly()
    {
        var producer = new InfiniteProducer();
        var middle = new PassThroughTransformer();
        var sink = new BlackholeSink();

        var pipeline = Sutando.Pipeline.Pipeline.Builder()
            .StartWith(producer)
            .Then(middle)
            .EndsWith(sink)
            .Build();

        using var cts = new CancellationTokenSource();

        var runTask = pipeline.RunAsync(cts.Token);

        // Let the pipeline get rolling so we know every stage has entered its main loop.
        await Task.Delay(50, CancellationToken.None);

        var sw = Stopwatch.StartNew();
        await cts.CancelAsync();

        // The pipeline orchestrator surfaces OperationCanceledException via the linked CTS;
        // we accept either OperationCanceledException OR an AggregateException wrapping one
        // (the AggregateException path fires when a stage's exception propagation race is won
        // by the per-stage fault handler).
        try
        {
            await runTask;
        }
        catch (OperationCanceledException) { }
        catch (AggregateException ae) when (ae.InnerExceptions.All(e => e is OperationCanceledException))
        {
            // expected — every stage cancelled
        }

        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 1_000, $"Pipeline took {sw.ElapsedMilliseconds} ms to tear down — should be sub-second.");
    }

    [Fact]
    public async Task PrecancelledToken_PipelineCompletesImmediately()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var pipeline = Sutando.Pipeline.Pipeline.Builder()
            .StartWith(new InfiniteProducer())
            .EndsWith(new BlackholeSink())
            .Build();

        var sw = Stopwatch.StartNew();
        try
        {
            await pipeline.RunAsync(cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (AggregateException ae) when (ae.InnerExceptions.All(e => e is OperationCanceledException)) { }
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 500, $"Pre-cancelled pipeline took {sw.ElapsedMilliseconds} ms to settle.");
    }

    /// <summary>Source that never completes on its own — relies on cancellation to exit.</summary>
    private sealed class InfiniteProducer : IPipelineStage<Unit, int>
    {
        public async IAsyncEnumerable<int> ProcessAsync(IAsyncEnumerable<Unit> source, [EnumeratorCancellation] CancellationToken ct)
        {
            var i = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return i++;
            }
        }
    }

    private sealed class PassThroughTransformer : IPipelineStage<int, int>
    {
        public async IAsyncEnumerable<int> ProcessAsync(IAsyncEnumerable<int> source, [EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var v in source.WithCancellation(ct))
            {
                yield return v;
            }
        }
    }

    private sealed class BlackholeSink : IPipelineStage<int, Unit>
    {
        public async IAsyncEnumerable<Unit> ProcessAsync(IAsyncEnumerable<int> source, [EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var _ in source.WithCancellation(ct))
            {
                // intentional swallow
            }
            yield break;
        }
    }
}
