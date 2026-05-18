using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Sutando.LocalInference;

/// <summary>
/// DI helpers for composing a Sutando local-inference stack out of the four pipeline
/// stages: VAD → STT → chat → TTS. Adapter projects (Sutando.LocalInference.WhisperNet,
/// Sutando.LocalInference.KokoroSharp, Sutando.LocalInference.LlamaSharp,
/// Sutando.LocalInference.Silero, plus the HTTP-shaped variants) register their
/// concrete clients via the standard MEAI <see cref="ChatClientBuilder"/> /
/// <c>SpeechToTextClientBuilder</c> / <c>TextToSpeechClientBuilder</c> patterns.
/// </summary>
/// <remarks>
/// <para>
/// We intentionally do NOT re-export MEAI's chat / STT / TTS interfaces from this
/// project. Consumers reference <see cref="IChatClient"/>, <see cref="ISpeechToTextClient"/>,
/// and <see cref="ITextToSpeechClient"/> directly from MEAI. Re-exporting would create
/// a tax-alias layer that adds nothing.
/// </para>
/// <para>
/// What this project DOES own: <see cref="IVadDetector"/> (no MEAI equivalent yet),
/// and the <see cref="AddSutandoLocalInference"/> helper below that ties everything
/// together for callers that want one-line setup.
/// </para>
/// </remarks>
[Experimental("SUTANDO001")]
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the full local-inference stack (VAD + STT + chat + TTS) in the DI container.
    /// Concrete clients are supplied by the caller — this method just stitches the bindings.
    /// </summary>
    /// <param name="services">The DI container.</param>
    /// <param name="configure">Builder callback selecting which client implements each stage.</param>
    /// <returns><paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddSutandoLocalInference(
        this IServiceCollection services,
        Action<SutandoLocalInferenceBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new SutandoLocalInferenceBuilder(services);
        configure(builder);
        return services;
    }
}

/// <summary>
/// Fluent builder accumulated by <see cref="ServiceCollectionExtensions.AddSutandoLocalInference"/>.
/// Each <c>Use…</c> method registers a singleton factory for that stage; later registrations
/// override earlier ones, matching MEAI's own builder semantics.
/// </summary>
[Experimental("SUTANDO001")]
public sealed class SutandoLocalInferenceBuilder
{
    private readonly IServiceCollection _services;

    internal SutandoLocalInferenceBuilder(IServiceCollection services) => _services = services;

    /// <summary>Register the VAD implementation.</summary>
    public SutandoLocalInferenceBuilder UseVadDetector(Func<IServiceProvider, IVadDetector> factory)
    {
        _services.AddSingleton(factory);
        return this;
    }

    /// <summary>Register the STT client (a MEAI <see cref="ISpeechToTextClient"/>).</summary>
    public SutandoLocalInferenceBuilder UseSpeechToText(Func<IServiceProvider, ISpeechToTextClient> factory)
    {
        _services.AddSingleton(factory);
        return this;
    }

    /// <summary>Register the chat / LLM client (a MEAI <see cref="IChatClient"/>).</summary>
    public SutandoLocalInferenceBuilder UseChatClient(Func<IServiceProvider, IChatClient> factory)
    {
        _services.AddSingleton(factory);
        return this;
    }

    /// <summary>Register the TTS client (a MEAI <see cref="ITextToSpeechClient"/>).</summary>
    public SutandoLocalInferenceBuilder UseTextToSpeech(Func<IServiceProvider, ITextToSpeechClient> factory)
    {
        _services.AddSingleton(factory);
        return this;
    }
}
