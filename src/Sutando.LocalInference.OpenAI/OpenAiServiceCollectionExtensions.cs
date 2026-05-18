using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;

namespace Sutando.LocalInference.OpenAI;

/// <summary>
/// DI helpers that register OpenAI-compatible <see cref="IChatClient"/> /
/// <see cref="ISpeechToTextClient"/> / <see cref="ITextToSpeechClient"/> against any
/// endpoint that speaks the OpenAI HTTP protocol.
/// </summary>
/// <remarks>
/// <para>
/// One project covers every OpenAI-shaped backend the local stack uses:
/// </para>
/// <list type="bullet">
///   <item><description><b>Chat / LLM</b>: vLLM, llama-server, LM Studio, SGLang, TGI, TogetherAI, Groq, Anyscale, Fireworks, OpenAI proper.</description></item>
///   <item><description><b>STT</b>: speaches (formerly faster-whisper-server) — exposes <c>POST /v1/audio/transcriptions</c>.</description></item>
///   <item><description><b>TTS</b>: kokoro-fastapi — exposes <c>POST /v1/audio/speech</c>.</description></item>
/// </list>
/// <para>
/// Each method registers the appropriate MEAI client as a singleton. Repeated calls
/// override earlier registrations, matching MEAI's own builder semantics.
/// </para>
/// </remarks>
[Experimental("SUTANDO001")]
public static class OpenAiServiceCollectionExtensions
{
    /// <summary>Default chat model when <see cref="OpenAiEndpointOptions.Model"/> is unset.</summary>
    public const string DefaultChatModel = "Qwen/Qwen3-8B-AWQ";

    /// <summary>Default STT model when <see cref="OpenAiEndpointOptions.Model"/> is unset (matches speaches' shipped default).</summary>
    public const string DefaultSpeechToTextModel = "Systran/faster-whisper-medium.en";

    /// <summary>Default TTS model when <see cref="OpenAiEndpointOptions.Model"/> is unset.</summary>
    public const string DefaultTextToSpeechModel = "kokoro";

    /// <summary>
    /// Register an OpenAI-compatible <see cref="IChatClient"/> pointed at any endpoint that
    /// speaks <c>/v1/chat/completions</c> (vLLM, llama-server, LM Studio, TogetherAI, …).
    /// </summary>
    /// <param name="services">DI container.</param>
    /// <param name="options">Endpoint + key + model. Model defaults to <see cref="DefaultChatModel"/>.</param>
    /// <returns><paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddOpenAiCompatibleChatClient(
        this IServiceCollection services,
        OpenAiEndpointOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton<IChatClient>(_ =>
        {
            var model = options.Model ?? DefaultChatModel;
            return OpenAiClientFactory.Build(options).GetChatClient(model).AsIChatClient();
        });
        return services;
    }

    /// <summary>
    /// Register an OpenAI-compatible <see cref="ISpeechToTextClient"/> pointed at
    /// <c>POST /v1/audio/transcriptions</c> — the shape speaches (faster-whisper-server) ships.
    /// </summary>
    public static IServiceCollection AddOpenAiCompatibleSpeechToTextClient(
        this IServiceCollection services,
        OpenAiEndpointOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton<ISpeechToTextClient>(_ =>
        {
            var model = options.Model ?? DefaultSpeechToTextModel;
            return OpenAiClientFactory.Build(options).GetAudioClient(model).AsISpeechToTextClient();
        });
        return services;
    }

    /// <summary>
    /// Register an OpenAI-compatible <see cref="ITextToSpeechClient"/> pointed at
    /// <c>POST /v1/audio/speech</c> — the shape kokoro-fastapi exposes.
    /// </summary>
    public static IServiceCollection AddOpenAiCompatibleTextToSpeechClient(
        this IServiceCollection services,
        OpenAiEndpointOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton<ITextToSpeechClient>(_ =>
        {
            var model = options.Model ?? DefaultTextToSpeechModel;
            return OpenAiClientFactory.Build(options).GetAudioClient(model).AsITextToSpeechClient();
        });
        return services;
    }
}
