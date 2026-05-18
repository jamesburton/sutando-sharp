using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Sutando.LocalInference.Silero;

/// <summary>
/// DI extensions registering <see cref="SileroVadDetector"/> as the singleton
/// <see cref="IVadDetector"/> implementation for the local-inference stack.
/// </summary>
[Experimental("SUTANDO001")]
public static class SileroServiceCollectionExtensions
{
    /// <summary>
    /// Register a singleton <see cref="IVadDetector"/> backed by Silero VAD v5.
    /// </summary>
    /// <param name="services">The DI container.</param>
    /// <param name="modelPath">Path to <c>silero_vad.onnx</c> on disk.</param>
    /// <param name="vadOptions">Optional default <see cref="VadOptions"/> used when <see cref="IVadDetector.AnalyzeAsync(IAsyncEnumerable{AudioFrame}, VadOptions, CancellationToken)"/> doesn't override them.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="modelPath"/> is null / empty.</exception>
    public static IServiceCollection AddSileroVad(
        this IServiceCollection services,
        string modelPath,
        VadOptions? vadOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);

        services.AddSingleton<IVadDetector>(_ => new SileroVadDetector(
            new SileroVadDetectorOptions { ModelPath = modelPath },
            vadOptions));
        return services;
    }

    /// <summary>
    /// Register a singleton <see cref="IVadDetector"/> backed by Silero VAD v5 using a
    /// fully-formed options bag (e.g. with custom <see cref="Microsoft.ML.OnnxRuntime.SessionOptions"/>).
    /// </summary>
    public static IServiceCollection AddSileroVad(
        this IServiceCollection services,
        SileroVadDetectorOptions options,
        VadOptions? vadOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton<IVadDetector>(_ => new SileroVadDetector(options, vadOptions));
        return services;
    }

    /// <summary>
    /// Register a singleton <see cref="IVadDetector"/> that downloads the Silero VAD ONNX model
    /// from the upstream GitHub repository on first resolution and caches it under the user's
    /// local-application-data directory. Subsequent calls re-use the cached file.
    /// </summary>
    /// <param name="services">The DI container.</param>
    /// <param name="vadOptions">Optional default <see cref="VadOptions"/>.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    /// <remarks>
    /// The download is synchronous-with-respect-to-the-first-resolution: the first request that
    /// resolves the singleton blocks for the model fetch (~2.2 MB over HTTPS, typically &lt; 1
    /// second on a modern connection). If your host needs strictly non-blocking startup, call
    /// <see cref="SileroModelLocator.EnsureModelAsync(CancellationToken)"/> during bootstrap and
    /// pass the returned path to the
    /// <see cref="AddSileroVad(IServiceCollection, string, VadOptions?)"/> overload instead.
    /// </remarks>
    public static IServiceCollection AddSileroVadAutoDownload(
        this IServiceCollection services,
        VadOptions? vadOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IVadDetector>(_ =>
        {
            // Block the resolution-time thread on the download exactly once. We intentionally
            // avoid an async-over-sync factory because IServiceProvider has no native async
            // resolution and callers expect AddSingleton<T> to hand back a fully-built T.
            var modelPath = SileroModelLocator.EnsureModelAsync().GetAwaiter().GetResult();
            return new SileroVadDetector(new SileroVadDetectorOptions { ModelPath = modelPath }, vadOptions);
        });
        return services;
    }
}
