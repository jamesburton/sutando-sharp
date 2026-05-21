using Microsoft.Extensions.Logging;
using Sutando.LocalInference;
using Sutando.LocalInference.KokoroSharp;
using Sutando.LocalInference.LlamaSharp;
using Sutando.LocalInference.Silero;
using Sutando.LocalInference.WhisperNet;
using Sutando.Voice.Local;

namespace Sutando.Voice;

/// <summary>
/// Builds the "pure in-process" <see cref="LocalPipelineOptions"/> for <c>sutando voice --local</c>
/// from the model file paths on <see cref="VoiceOptions.LocalModels"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the laptop flavour from <c>docs/local-stack-scope.md §7</c>: every stage runs in the
/// same process from local model files — Whisper.net for STT, LlamaSharp for chat, KokoroSharp
/// for TTS, Silero for VAD. The AppHost-orchestrated flavour (HTTP clients pointed at a GPU box)
/// shares <see cref="LocalPipelineOptions"/> and <see cref="LocalPipelineTransportFactory"/>; only
/// this bootstrap differs. See <c>src/Sutando.Voice.Local/INTEGRATION-NOTES.md</c>.
/// </para>
/// <para>
/// <b>Fail-graceful contract.</b> A missing or empty model path throws
/// <see cref="LocalPipelineConfigurationException"/>, which <see cref="LocalPipelineTransportFactory"/>
/// catches and turns into a per-session error envelope. The host never crashes on a
/// misconfigured <c>--local</c>.
/// </para>
/// </remarks>
internal static class LocalPipelineBootstrap
{
    /// <summary>
    /// Resolve the four local-inference stage components and assemble a
    /// <see cref="LocalPipelineOptions"/>.
    /// </summary>
    /// <param name="voiceOptions">The bound voice options, carrying the model paths.</param>
    /// <param name="loggerFactory">Logger factory (currently unused by the adapters, threaded for future use).</param>
    /// <returns>A fully-populated <see cref="LocalPipelineOptions"/>.</returns>
    /// <exception cref="LocalPipelineConfigurationException">A required model file is missing or unreadable.</exception>
    public static LocalPipelineOptions Build(VoiceOptions voiceOptions, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(voiceOptions);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var models = voiceOptions.LocalModels;

        // Validate the three operator-supplied model files up front so a misconfiguration
        // surfaces as one clear message rather than a deep adapter stack trace.
        var whisperPath = RequireModelFile(models.WhisperModel, "Whisper STT", "SUTANDO_WHISPER_MODEL");
        var llamaPath = RequireModelFile(models.LlamaModel, "LlamaSharp chat", "SUTANDO_LLAMA_MODEL");
        var kokoroPath = RequireModelFile(models.KokoroModel, "KokoroSharp TTS", "SUTANDO_KOKORO_MODEL");

        IVadDetector vad;
        try
        {
            // Silero is the one stage with a zero-config fallback: a missing path triggers the
            // ~2 MB auto-download into the per-user cache. An explicit path is validated like
            // the others.
            if (string.IsNullOrWhiteSpace(models.SileroModel))
            {
                var sileroPath = SileroModelLocator.EnsureModelAsync().GetAwaiter().GetResult();
                vad = new SileroVadDetector(new SileroVadDetectorOptions { ModelPath = sileroPath });
            }
            else
            {
                var sileroPath = RequireModelFile(models.SileroModel, "Silero VAD", "SUTANDO_SILERO_MODEL");
                vad = new SileroVadDetector(new SileroVadDetectorOptions { ModelPath = sileroPath });
            }
        }
        catch (LocalPipelineConfigurationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new LocalPipelineConfigurationException(
                $"Silero VAD model could not be loaded: {ex.Message}", ex);
        }

        try
        {
            var stt = WhisperNetServiceCollectionExtensions.CreateClient(whisperPath);
            var chat = new LlamaCppChatClient(new LlamaCppChatClientOptions(llamaPath));
            var tts = new KokoroSharpTextToSpeechClient(kokoroPath);

            return new LocalPipelineOptions
            {
                VadDetector = vad,
                SpeechToText = stt,
                Chat = chat,
                TextToSpeech = tts,
                SystemPrompt = voiceOptions.SystemInstruction,
            };
        }
        catch (Exception ex)
        {
            // Adapter construction loads model weights — surface any failure as the same
            // operator-actionable configuration error so the host stays up.
            throw new LocalPipelineConfigurationException(
                $"A local-inference model could not be loaded: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Verify a model path is set and points at an existing file, returning it on success.
    /// </summary>
    /// <param name="path">The configured path (may be null / empty).</param>
    /// <param name="stage">Human-readable stage name for the error message.</param>
    /// <param name="envVar">The environment variable the operator should set.</param>
    /// <returns>The validated path.</returns>
    /// <exception cref="LocalPipelineConfigurationException">The path is empty or the file does not exist.</exception>
    private static string RequireModelFile(string? path, string stage, string envVar)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new LocalPipelineConfigurationException(
                $"sutando voice --local: the {stage} model is not configured. Set the {envVar} environment variable to the model file path.");
        }
        if (!File.Exists(path))
        {
            throw new LocalPipelineConfigurationException(
                $"sutando voice --local: the {stage} model file was not found at '{path}' (from {envVar}).");
        }
        return path;
    }
}

/// <summary>
/// Raised when the <c>--local</c> voice pipeline cannot be assembled — a missing model file or
/// an unreadable model. Operator-actionable; <see cref="LocalPipelineTransportFactory"/> catches
/// it and surfaces the message to the browser instead of crashing the host.
/// </summary>
public sealed class LocalPipelineConfigurationException : Exception
{
    /// <summary>Create the exception with an explanatory message.</summary>
    /// <param name="message">What is missing or misconfigured.</param>
    public LocalPipelineConfigurationException(string message)
        : base(message)
    {
    }

    /// <summary>Create the exception with an explanatory message and an underlying cause.</summary>
    /// <param name="message">What is missing or misconfigured.</param>
    /// <param name="innerException">The underlying failure.</param>
    public LocalPipelineConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
