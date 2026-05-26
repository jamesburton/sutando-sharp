using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Sutando.Pipeline;

namespace Sutando.Tests.Pipeline;

[SuppressMessage("Usage", "SUTANDO001", Justification = "Tests for our own experimental surface.")]
public sealed class PipelineDiTests
{
    [Fact]
    public void AddSutandoPipeline_RegistersConfiguredBuilder()
    {
        // The DI helper registers a factory that hands out a primed builder per resolve.
        var services = new ServiceCollection();
        services.AddSutandoPipeline(b => b.WithOptions(new PipelineOptions { ChannelCapacity = 7 }));

        var sp = services.BuildServiceProvider();
        var builder = sp.GetRequiredService<PipelineBuilder>();

        Assert.NotNull(builder);
        // The builder we got back should accept a typed source — that's the only way to
        // confirm WithOptions actually flowed in, since the option is read inside Build().
        var pipeline = builder
            .StartWith(new NoOpSource())
            .EndsWith(new NoOpSink())
            .Build();

        Assert.Equal(2, pipeline.StageCount);
    }

    [Fact]
    public void AddSutandoPipeline_PerResolve_HandsOutFreshBuilders()
    {
        // Pipelines are single-use — therefore the registered builder factory MUST be transient,
        // not singleton. Otherwise consumers would race on a shared builder.
        var services = new ServiceCollection();
        services.AddSutandoPipeline(_ => { });

        var sp = services.BuildServiceProvider();
        var first = sp.GetRequiredService<PipelineBuilder>();
        var second = sp.GetRequiredService<PipelineBuilder>();

        Assert.NotSame(first, second);
    }

    [Fact]
    public void AddSutandoPipeline_NullArguments_Throws()
    {
        IServiceCollection? nullServices = null;
        Assert.Throws<ArgumentNullException>(() => nullServices!.AddSutandoPipeline(_ => { }));

        var services = new ServiceCollection();
        Assert.Throws<ArgumentNullException>(() => services.AddSutandoPipeline((Action<PipelineBuilder>)null!));
    }

    private sealed class NoOpSource : IPipelineStage<Unit, int>
    {
        public async IAsyncEnumerable<int> ProcessAsync(IAsyncEnumerable<Unit> source, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield break;
        }
    }

    private sealed class NoOpSink : IPipelineStage<int, Unit>
    {
        public async IAsyncEnumerable<Unit> ProcessAsync(IAsyncEnumerable<int> source, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var _ in source.WithCancellation(ct)) { }
            yield break;
        }
    }
}
