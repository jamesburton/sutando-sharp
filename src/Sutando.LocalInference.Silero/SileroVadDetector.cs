using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Sutando.LocalInference.Silero;

/// <summary>
/// <see cref="IVadDetector"/> implementation backed by Silero VAD v5 running through
/// <c>Microsoft.ML.OnnxRuntime</c>. Carries the model's LSTM state across frames so the
/// detection stays stable through a long microphone session.
/// </summary>
/// <remarks>
/// <para>
/// <b>Audio format</b>: Silero v5 expects 16 kHz mono float32 PCM in 512-sample chunks
/// (32 ms at 16 kHz). The detector consumes <see cref="AudioFrame"/>s in any frame size /
/// sample rate / encoding combination supported by <see cref="AudioEncoding"/>, then
/// re-frames internally:
/// <list type="bullet">
///   <item><description>Mono is mandatory. Stereo / multi-channel frames will throw.</description></item>
///   <item><description>16 kHz is mandatory at the model boundary. We assume callers feed 16 kHz already (Sutando's upstream microphone path); if they don't, an <see cref="ArgumentException"/> is raised so the mismatch is visible.</description></item>
///   <item><description><see cref="AudioEncoding.Pcm16Le"/> is converted to float32 by dividing by <c>32768f</c>.</description></item>
///   <item><description><see cref="AudioEncoding.Pcm32FloatLe"/> is consumed directly.</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Model file</b>: the v5 ONNX model (<c>silero_vad.onnx</c>, ~2.2 MB) is loaded via
/// <see cref="SileroVadDetectorOptions.ModelPath"/>. Ship it alongside the deployment, fetch
/// it lazily, or use the convenience overload of
/// <see cref="SileroServiceCollectionExtensions.AddSileroVad(Microsoft.Extensions.DependencyInjection.IServiceCollection, string, VadOptions?)"/>.
/// </para>
/// </remarks>
[Experimental("SUTANDO001")]
public sealed class SileroVadDetector : IVadDetector, IDisposable
{
    /// <summary>Silero v5's required input sample rate (Hz).</summary>
    public const int RequiredSampleRate = 16_000;

    /// <summary>Silero v5's required input chunk size (samples per inference at 16 kHz).</summary>
    public const int ChunkSamples = 512;

    /// <summary>Silero v5's input names — verified against the upstream ONNX export.</summary>
    private const string InputAudioName = "input";

    private const string InputStateName = "state";
    private const string InputSampleRateName = "sr";
    private const string OutputProbabilityName = "output";
    private const string OutputStateName = "stateN";

    private readonly InferenceSession _session;
    private readonly VadOptions _vadOptions;
    private bool _disposed;

    // Silero v5 state: [2, batch=1, 128]. Persists across frames so the LSTM has context.
    private readonly float[] _state = new float[2 * 1 * 128];

    /// <summary>The detector's stable identifier — useful for logging / telemetry tags.</summary>
    public string Id => "silero";

    /// <summary>Initialize the detector from a Silero VAD ONNX model on disk.</summary>
    /// <param name="options">Construction options (model path + ONNX session tuning).</param>
    /// <param name="vadOptions">Detection thresholds applied when no per-call override is provided.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Model path is null / empty.</exception>
    public SileroVadDetector(SileroVadDetectorOptions options, VadOptions? vadOptions = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ModelPath);

        _session = options.SessionOptions is null
            ? new InferenceSession(options.ModelPath)
            : new InferenceSession(options.ModelPath, options.SessionOptions);

        _vadOptions = vadOptions ?? new VadOptions();
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<VadEvent> AnalyzeAsync(
        IAsyncEnumerable<AudioFrame> source,
        VadOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var stateMachine = new SileroVadStateMachine(options);
        Array.Clear(_state, 0, _state.Length);

        // Re-framer buffer: incoming AudioFrames may have any sample count; we buffer them and
        // emit ChunkSamples-sized windows to the ONNX session. The buffer is float32 so both
        // input encodings (Pcm16Le, Pcm32FloatLe) end up in the same representation.
        var carry = new float[ChunkSamples];
        var carryLength = 0;

        await foreach (var frame in source.WithCancellation(ct).ConfigureAwait(false))
        {
            ValidateFrame(frame);

            var samples = DecodeToFloat(frame);
            var sampleIndex = 0;

            while (sampleIndex < samples.Length)
            {
                var copy = Math.Min(ChunkSamples - carryLength, samples.Length - sampleIndex);
                Array.Copy(samples, sampleIndex, carry, carryLength, copy);
                carryLength += copy;
                sampleIndex += copy;

                if (carryLength == ChunkSamples)
                {
                    var probability = RunInference(carry);
                    foreach (var ev in stateMachine.Push(probability, frame.CapturedAt == default ? DateTimeOffset.UtcNow : frame.CapturedAt))
                    {
                        yield return ev;
                    }
                    carryLength = 0;
                }
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.Dispose();
    }

    private static void ValidateFrame(AudioFrame frame)
    {
        if (frame.SampleRate != RequiredSampleRate)
        {
            throw new ArgumentException(
                $"Silero VAD requires {RequiredSampleRate} Hz input; got {frame.SampleRate} Hz. Resample at the source.",
                nameof(frame));
        }

        if (frame.Channels != 1)
        {
            throw new ArgumentException(
                $"Silero VAD requires mono input; got {frame.Channels} channels.",
                nameof(frame));
        }
    }

    private static float[] DecodeToFloat(AudioFrame frame)
    {
        var pcm = frame.Pcm.Span;

        return frame.Encoding switch
        {
            AudioEncoding.Pcm16Le => DecodePcm16(pcm),
            AudioEncoding.Pcm32FloatLe => DecodePcm32Float(pcm),
            _ => throw new ArgumentOutOfRangeException(nameof(frame), frame.Encoding, "Unsupported audio encoding for Silero VAD."),
        };
    }

    private static float[] DecodePcm16(ReadOnlySpan<byte> pcm)
    {
        var sampleCount = pcm.Length / 2;
        var samples = new float[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(i * 2, 2));
            samples[i] = sample / 32768f;
        }
        return samples;
    }

    private static float[] DecodePcm32Float(ReadOnlySpan<byte> pcm)
    {
        var sampleCount = pcm.Length / 4;
        var samples = new float[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            samples[i] = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(pcm.Slice(i * 4, 4)));
        }
        return samples;
    }

    private float RunInference(float[] chunk)
    {
        // Input tensors: input[1,512], state[2,1,128], sr scalar int64. Names match Silero v5 export.
        // Note: pass dimensions as ReadOnlySpan<int> (explicit array) — bare collection literals
        // get ambiguous between DenseTensor overloads that accept Memory<T> data and the
        // ReadOnlySpan<int> dimensions argument.
        var audioTensor = new DenseTensor<float>(chunk, new[] { 1, ChunkSamples });
        var stateTensor = new DenseTensor<float>(_state, new[] { 2, 1, 128 });
        var srTensor = new DenseTensor<long>(new long[] { RequiredSampleRate }, new[] { 1 });

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(InputAudioName, audioTensor),
            NamedOnnxValue.CreateFromTensor(InputStateName, stateTensor),
            NamedOnnxValue.CreateFromTensor(InputSampleRateName, srTensor),
        };

        using var results = _session.Run(inputs);

        var probability = results.First(r => r.Name == OutputProbabilityName).AsTensor<float>()[0];

        // Persist the model's updated state for the next chunk.
        var newState = results.First(r => r.Name == OutputStateName).AsTensor<float>();
        var newStateArray = newState.ToArray();
        Array.Copy(newStateArray, _state, Math.Min(newStateArray.Length, _state.Length));

        return probability;
    }
}

/// <summary>Construction options for <see cref="SileroVadDetector"/>.</summary>
[Experimental("SUTANDO001")]
public sealed class SileroVadDetectorOptions
{
    /// <summary>Path to the Silero VAD ONNX model (<c>silero_vad.onnx</c>, v5).</summary>
    public required string ModelPath { get; init; }

    /// <summary>Optional ONNX session tuning (thread count, intra-op parallelism, etc.).</summary>
    public SessionOptions? SessionOptions { get; init; }
}
