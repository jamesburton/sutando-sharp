using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Whisper.net;

namespace Sutando.LocalInference.WhisperNet;

/// <summary>
/// DI extensions that register Whisper.net's
/// <see cref="WhisperSpeechToTextClient"/> as the
/// <see cref="ISpeechToTextClient"/> implementation for the local-inference stack.
/// </summary>
/// <remarks>
/// <para>
/// Whisper.net 1.9 ships a public <see cref="WhisperSpeechToTextClient"/> that already implements
/// <see cref="ISpeechToTextClient"/> from <c>Microsoft.Extensions.AI</c>. This adapter therefore
/// has no wrapper class of its own — its job is purely to wire the client into DI with a
/// caller-supplied model path and (optionally) a factory configuration callback.
/// </para>
/// <para>
/// <b>Model files</b>: Whisper.net consumes GGML-format Whisper models (the <c>ggml-*.bin</c>
/// files distributed by the whisper.cpp ecosystem on Hugging Face). Resolution rules:
/// <list type="bullet">
///   <item><description>
///     <paramref name="modelPath"/> is passed verbatim to <see cref="WhisperFactory.FromPath(string, WhisperFactoryOptions)"/>;
///     relative paths resolve against <see cref="System.IO.Directory.GetCurrentDirectory"/>.
///   </description></item>
///   <item><description>
///     The runtime backend is selected by which <c>Whisper.net.Runtime.*</c> NuGet is referenced
///     by the host application (CPU by default; CUDA / CoreML / OpenVino opt-in).
///   </description></item>
///   <item><description>
///     The model file is NOT downloaded by this adapter — operators ship it alongside their
///     deployment, or fetch it at first run via their own bootstrap step.
///   </description></item>
/// </list>
/// </para>
/// </remarks>
[Experimental("SUTANDO001")]
public static class WhisperNetServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="WhisperSpeechToTextClient"/> as a singleton
    /// <see cref="ISpeechToTextClient"/> using the GGML model at <paramref name="modelPath"/>.
    /// </summary>
    /// <param name="services">The DI container.</param>
    /// <param name="modelPath">Absolute or relative path to a Whisper GGML model file (e.g. <c>ggml-medium.bin</c>).</param>
    /// <param name="configureFactory">
    /// Optional callback to configure the underlying <see cref="WhisperFactoryOptions"/> (e.g.
    /// to pin the runtime library or override the temp folder). May be <see langword="null"/>
    /// to use Whisper.net's defaults.
    /// </param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="modelPath"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddWhisperNet(
        this IServiceCollection services,
        string modelPath,
        Action<WhisperFactoryOptions>? configureFactory = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);

        services.AddSingleton<ISpeechToTextClient>(_ => CreateClient(modelPath, configureFactory));
        return services;
    }

    /// <summary>
    /// Create a <see cref="WhisperSpeechToTextClient"/> directly without going through DI —
    /// useful for tests and ad-hoc transcription scripts.
    /// </summary>
    /// <param name="modelPath">Path to the GGML model file.</param>
    /// <param name="configureFactory">Optional <see cref="WhisperFactoryOptions"/> configuration callback.</param>
    /// <returns>A disposable <see cref="WhisperSpeechToTextClient"/>; caller owns the lifetime.</returns>
    public static WhisperSpeechToTextClient CreateClient(
        string modelPath,
        Action<WhisperFactoryOptions>? configureFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);

        if (configureFactory is null)
        {
            // Simple-path constructor — Whisper.net builds a default WhisperFactory from the file path.
            return new WhisperSpeechToTextClient(modelPath);
        }

        // Factory-builder constructor — gives the caller a chance to set runtime / threads / etc.
        return new WhisperSpeechToTextClient(() =>
        {
            var options = new WhisperFactoryOptions();
            configureFactory(options);
            return WhisperFactory.FromPath(modelPath, options);
        });
    }
}
