using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Sutando.LocalInference.LlamaSharp;

/// <summary>
/// DI extensions that register <see cref="LlamaCppChatClient"/> as the singleton
/// <see cref="IChatClient"/> implementation for the local-inference stack.
/// </summary>
[Experimental("SUTANDO001")]
public static class LlamaSharpServiceCollectionExtensions
{
    /// <summary>
    /// Register a singleton <see cref="IChatClient"/> backed by a GGUF model loaded via
    /// LlamaSharp. The model is loaded into memory once on first resolution and reused for
    /// every request thereafter; dispose the DI container to free it.
    /// </summary>
    /// <param name="services">The DI container.</param>
    /// <param name="modelPath">Path to the GGUF model file (e.g. <c>Qwen3-8B-Q4_K_M.gguf</c>).</param>
    /// <param name="configure">Optional callback to tune the remaining
    /// <see cref="LlamaCppChatClientOptions"/> (system prompt, context size, GPU layer count).</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="modelPath"/> is null / empty.</exception>
    public static IServiceCollection AddLlamaCppChat(
        this IServiceCollection services,
        string modelPath,
        Action<LlamaCppChatClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);

        services.AddSingleton<IChatClient>(_ =>
        {
            var options = new LlamaCppChatClientOptions(modelPath);
            configure?.Invoke(options);
            return new LlamaCppChatClient(options);
        });

        return services;
    }

    /// <summary>
    /// Convert a fully-constructed <see cref="LlamaCppChatClientOptions"/> into a registered
    /// singleton <see cref="IChatClient"/>. Useful when the caller already has the options
    /// composed elsewhere (e.g. from configuration).
    /// </summary>
    /// <param name="services">The DI container.</param>
    /// <param name="options">Pre-built options bag.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    /// <exception cref="ArgumentNullException">Either parameter is <see langword="null"/>.</exception>
    public static IServiceCollection AddLlamaCppChat(
        this IServiceCollection services,
        LlamaCppChatClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton<IChatClient>(_ => new LlamaCppChatClient(options));
        return services;
    }
}
