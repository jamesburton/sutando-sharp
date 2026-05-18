using System.Diagnostics.CodeAnalysis;
using KokoroSharp;
using KokoroSharp.Processing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.ML.OnnxRuntime;

namespace Sutando.LocalInference.KokoroSharp;

/// <summary>
/// DI extensions that register <see cref="KokoroSharpTextToSpeechClient"/> as the
/// <see cref="ITextToSpeechClient"/> implementation for the local-inference stack.
/// </summary>
[Experimental("SUTANDO001")]
public static class KokoroSharpServiceCollectionExtensions
{
    /// <summary>
    /// Register a singleton <see cref="ITextToSpeechClient"/> backed by KokoroSharp's
    /// ONNX TTS model.
    /// </summary>
    /// <param name="services">The DI container.</param>
    /// <param name="modelPath">
    /// Path to the Kokoro v1.0 ONNX model. The model can be downloaded from
    /// <c>https://github.com/taylorchu/kokoro-onnx/releases/tag/v0.2.0</c> (also fetched
    /// automatically by <see cref="KokoroTTS.LoadModelAsync(KModel, Action{float}?, SessionOptions?)"/>
    /// on first use, which operators may prefer to call separately at startup).
    /// </param>
    /// <param name="defaultVoiceName">
    /// Default Kokoro voice name when <see cref="TextToSpeechOptions.VoiceId"/> isn't set.
    /// Defaults to <see cref="KokoroSharpTextToSpeechClient.DefaultVoiceName"/> (<c>af_heart</c>).
    /// </param>
    /// <param name="sessionOptions">
    /// Optional ONNX Runtime <see cref="SessionOptions"/>. Pass <see langword="null"/> for
    /// KokoroSharp's defaults (CPU, 8 threads).
    /// </param>
    /// <param name="defaultPipelineConfig">
    /// Optional default <see cref="KokoroTTSPipelineConfig"/> that controls speed, preprocessing,
    /// segmentation, and pause behaviour.
    /// </param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="modelPath"/> is <see langword="null"/> or whitespace.</exception>
    public static IServiceCollection AddKokoroSharp(
        this IServiceCollection services,
        string modelPath,
        string? defaultVoiceName = null,
        SessionOptions? sessionOptions = null,
        KokoroTTSPipelineConfig? defaultPipelineConfig = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);

        services.AddSingleton<ITextToSpeechClient>(_ => new KokoroSharpTextToSpeechClient(
            modelPath,
            defaultVoiceName,
            sessionOptions,
            defaultPipelineConfig));
        return services;
    }
}
