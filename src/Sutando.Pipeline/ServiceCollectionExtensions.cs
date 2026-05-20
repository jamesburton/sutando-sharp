using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Sutando.Pipeline;

/// <summary>
/// DI helpers for composing a <see cref="Pipeline"/> against the host's
/// <see cref="IServiceProvider"/>.
/// </summary>
/// <remarks>
/// <para>
/// The pipeline's typed builder (<see cref="PipelineBuilder{TCurrent}"/>) ratchets through
/// generic parameters as stages are appended — that means we can't easily expose a single
/// <see cref="Pipeline"/> registration in DI without erasing the type information. Instead,
/// we register a factory that produces a fresh <see cref="PipelineBuilder"/> on each resolve,
/// pre-configured with the caller's options and logger; consumers compose the typed stages
/// on top and finalise with <see cref="PipelineCompletionBuilder.Build"/>.
/// </para>
/// <para>
/// This mirrors how <c>Microsoft.Extensions.AI</c> exposes <c>ChatClientBuilder</c> through
/// DI rather than the concrete <c>IChatClient</c> — the typed terminal surface stays under
/// the consumer's control.
/// </para>
/// </remarks>
[Experimental("SUTANDO001")]
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register a factory that produces a configured <see cref="PipelineBuilder"/>. The
    /// supplied <paramref name="configure"/> callback runs each time <see cref="PipelineBuilder"/>
    /// is resolved and can pull dependencies from the resolved <see cref="IServiceProvider"/>.
    /// </summary>
    /// <param name="services">The DI container.</param>
    /// <param name="configure">Callback that primes the <see cref="PipelineBuilder"/> (logger, options).</param>
    /// <returns><paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddSutandoPipeline(
        this IServiceCollection services,
        Action<IServiceProvider, PipelineBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddTransient(sp =>
        {
            var builder = Pipeline.Builder();
            configure(sp, builder);
            return builder;
        });

        return services;
    }

    /// <summary>
    /// Simpler overload that ignores the <see cref="IServiceProvider"/> at configure time.
    /// </summary>
    /// <param name="services">The DI container.</param>
    /// <param name="configure">Callback that primes the <see cref="PipelineBuilder"/>.</param>
    /// <returns><paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddSutandoPipeline(
        this IServiceCollection services,
        Action<PipelineBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        return services.AddSutandoPipeline((_, b) => configure(b));
    }
}
