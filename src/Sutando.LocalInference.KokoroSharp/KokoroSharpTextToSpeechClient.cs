using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Processing;
using KokoroSharp.Utilities;
using Microsoft.Extensions.AI;
using Microsoft.ML.OnnxRuntime;

namespace Sutando.LocalInference.KokoroSharp;

/// <summary>
/// Microsoft.Extensions.AI <see cref="ITextToSpeechClient"/> backed by KokoroSharp's
/// <see cref="KokoroWavSynthesizer"/>. Renders text as 24 kHz / 16-bit signed little-endian
/// mono PCM (the Kokoro v1.0 model's fixed output format).
/// </summary>
/// <remarks>
/// <para>
/// KokoroSharp 0.6.7 does not natively implement <see cref="ITextToSpeechClient"/>; this class
/// is the bridge. It owns a single <see cref="KokoroWavSynthesizer"/> instance and a default
/// <see cref="KokoroVoice"/>; the synthesizer is reusable across calls. Once KokoroSharp adopts
/// MEAI natively this wrapper can be deleted.
/// </para>
/// <para>
/// <b>Voice selection</b>: callers pass
/// <see cref="TextToSpeechOptions.VoiceId"/> as the KokoroSharp voice name (e.g. <c>af_heart</c>,
/// <c>bm_george</c>). Unrecognised names fall back to the configured default voice with no error.
/// Voice files are loaded lazily via <see cref="KokoroVoiceManager"/>; KokoroSharp ships them in
/// the NuGet package as <c>voices/*.npy</c>.
/// </para>
/// <para>
/// <b>Audio output</b>: synthesised audio is delivered as a single <see cref="DataContent"/>
/// per response / streaming chunk. For
/// <see cref="ITextToSpeechClient.GetAudioAsync(string, TextToSpeechOptions?, CancellationToken)"/>
/// the full audio is concatenated into one chunk; for
/// <see cref="ITextToSpeechClient.GetStreamingAudioAsync(string, TextToSpeechOptions?, CancellationToken)"/>
/// each KokoroSharp inference step (a tokeniser segment) yields one chunk so listeners can play
/// audio as it's produced. The media type is <c>audio/pcm; rate=24000; channels=1; bits=16</c>.
/// </para>
/// </remarks>
[Experimental("SUTANDO001")]
public sealed class KokoroSharpTextToSpeechClient : ITextToSpeechClient
{
    /// <summary>Kokoro's fixed output sample rate (Hz).</summary>
    public const int SampleRateHz = 24_000;

    /// <summary>Kokoro's fixed output channel count.</summary>
    public const int Channels = 1;

    /// <summary>Kokoro's fixed output bit depth.</summary>
    public const int BitsPerSample = 16;

    /// <summary>Default Kokoro voice name when <see cref="TextToSpeechOptions.VoiceId"/> is unset.</summary>
    /// <remarks>"af_heart" is the most popular voice in the v1.0 release and ships with the NuGet.</remarks>
    public const string DefaultVoiceName = "af_heart";

    /// <summary>MEAI media type used on the emitted <see cref="DataContent"/>.</summary>
    public const string PcmMediaType = "audio/pcm; rate=24000; channels=1; bits=16";

    private static readonly ChatClientMetadata Metadata = new("KokoroSharp");

    private readonly KokoroWavSynthesizer _synthesizer;
    private readonly string _defaultVoiceName;
    private readonly KokoroTTSPipelineConfig? _defaultPipelineConfig;
    private bool _disposed;

    /// <summary>
    /// Initialize a new client around the Kokoro ONNX model on disk.
    /// </summary>
    /// <param name="modelPath">Path to the Kokoro ONNX model file (e.g. <c>kokoro.onnx</c>).</param>
    /// <param name="defaultVoiceName">Voice to use when no <see cref="TextToSpeechOptions.VoiceId"/> is provided. Defaults to <see cref="DefaultVoiceName"/>.</param>
    /// <param name="sessionOptions">Optional ONNX Runtime <see cref="SessionOptions"/> for CPU/GPU/thread tuning. Pass <see langword="null"/> for KokoroSharp's defaults (CPU, 8 threads).</param>
    /// <param name="defaultPipelineConfig">Optional default <see cref="KokoroTTSPipelineConfig"/> used when callers don't override it.</param>
    /// <exception cref="ArgumentException"><paramref name="modelPath"/> is <see langword="null"/> or whitespace.</exception>
    public KokoroSharpTextToSpeechClient(
        string modelPath,
        string? defaultVoiceName = null,
        SessionOptions? sessionOptions = null,
        KokoroTTSPipelineConfig? defaultPipelineConfig = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);

        _synthesizer = new KokoroWavSynthesizer(modelPath, sessionOptions);
        _defaultVoiceName = string.IsNullOrWhiteSpace(defaultVoiceName) ? DefaultVoiceName : defaultVoiceName!;
        _defaultPipelineConfig = defaultPipelineConfig;
    }

    /// <inheritdoc/>
    public async Task<TextToSpeechResponse> GetAudioAsync(
        string text,
        TextToSpeechOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var voice = ResolveVoice(options?.VoiceId);
        cancellationToken.ThrowIfCancellationRequested();

        // KokoroWavSynthesizer.SynthesizeAsync renders the full PCM payload as a byte[]
        // (16-bit signed little-endian, mono, 24 kHz). We hand that to MEAI as one DataContent.
        var pcmBytes = await _synthesizer.SynthesizeAsync(text, voice, _defaultPipelineConfig).ConfigureAwait(false);

        var response = new TextToSpeechResponse
        {
            ResponseId = Guid.NewGuid().ToString("N"),
            ModelId = Metadata.ProviderName,
            Contents = [new DataContent(pcmBytes, PcmMediaType)],
        };
        return response;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<TextToSpeechResponseUpdate> GetStreamingAudioAsync(
        string text,
        TextToSpeechOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var voice = ResolveVoice(options?.VoiceId);

        // KokoroSharp produces audio one tokeniser segment at a time. We bridge each step's
        // OnStepComplete callback into the async-enumerable by funneling samples through a Channel.
        // Producer = KokoroSharp worker thread; consumer = the awaiter of this enumerable.
        var channel = System.Threading.Channels.Channel.CreateUnbounded<float[]>(
            new System.Threading.Channels.UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
            });

        _synthesizer.Synthesize(
            text,
            voice,
            OnProgress: samples => channel.Writer.TryWrite(samples),
            OnComplete: () => channel.Writer.TryComplete(),
            _defaultPipelineConfig);

        var responseId = Guid.NewGuid().ToString("N");
        var modelId = Metadata.ProviderName;

        try
        {
            await foreach (var samples in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                // Convert float[-1..1] PCM samples to 16-bit signed little-endian bytes —
                // KokoroPlayback.GetBytes does exactly that and is the same path the WAV
                // synthesizer uses for single-shot output, so the wire format matches.
                var pcmBytes = KokoroPlayback.GetBytes(samples);

                yield return new TextToSpeechResponseUpdate([new DataContent(pcmBytes, PcmMediaType)])
                {
                    ResponseId = responseId,
                    ModelId = modelId,
                };
            }
        }
        finally
        {
            // If the consumer cancelled (or threw) and the producer is still running, mark the
            // channel as complete so any final TryWrite from KokoroSharp's thread is a no-op
            // rather than leaking a reference.
            channel.Writer.TryComplete();
        }
    }

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(ChatClientMetadata))
        {
            return Metadata;
        }

        if (serviceType.IsInstanceOfType(_synthesizer))
        {
            return _synthesizer;
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _synthesizer.Dispose();
    }

    /// <summary>
    /// Resolve the requested voice name, falling back to the configured default when unknown.
    /// </summary>
    /// <param name="voiceId">The <see cref="TextToSpeechOptions.VoiceId"/> from the caller, or <see langword="null"/>.</param>
    /// <returns>A <see cref="KokoroVoice"/> ready for synthesis.</returns>
    internal KokoroVoice ResolveVoice(string? voiceId)
    {
        var name = string.IsNullOrWhiteSpace(voiceId) ? _defaultVoiceName : voiceId!;

        // KokoroVoiceManager.GetVoice throws InvalidOperationException ("Sequence contains no
        // matching element") for unknown names. Catch that and fall back to the default so
        // callers don't have to introspect the catalog before calling us.
        try
        {
            return KokoroVoiceManager.GetVoice(name);
        }
        catch (InvalidOperationException)
        {
            return KokoroVoiceManager.GetVoice(_defaultVoiceName);
        }
    }
}
