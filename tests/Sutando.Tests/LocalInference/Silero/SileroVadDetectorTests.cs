using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Sutando.LocalInference;
using Sutando.LocalInference.Silero;

namespace Sutando.Tests.LocalInference.Silero;

/// <summary>
/// Surface / DI tests for <see cref="SileroVadDetector"/>. The actual ONNX-driven probability
/// path is covered indirectly: the synthetic state-machine tests live in
/// <see cref="SileroVadStateMachineTests"/>; a live-model run is gated behind an env var because
/// the ONNX file is not in CI.
/// </summary>
[SuppressMessage("Usage", "SUTANDO001", Justification = "Tests target Sutando's experimental local-inference surface.")]
public sealed class SileroVadDetectorTests
{
    [Fact]
    public void Constructor_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SileroVadDetector(null!));
    }

    [Fact]
    public void Options_RequiresModelPath()
    {
        // The 'required' modifier means we can't omit ModelPath at the type system level, but
        // empty / whitespace is still possible and the constructor must reject it.
        Assert.Throws<ArgumentException>(() => new SileroVadDetector(new SileroVadDetectorOptions { ModelPath = string.Empty }));
        Assert.Throws<ArgumentException>(() => new SileroVadDetector(new SileroVadDetectorOptions { ModelPath = "  " }));
    }

    [Fact]
    public void Constants_MatchSileroV5Spec()
    {
        // 16 kHz / 512-sample chunks are baked into the v5 ONNX export — if these change we
        // need to rev the adapter, not silently mismatch.
        Assert.Equal(16_000, SileroVadDetector.RequiredSampleRate);
        Assert.Equal(512, SileroVadDetector.ChunkSamples);
    }

    [Fact]
    public void AddSileroVad_NullServices_Throws()
    {
        IServiceCollection? services = null;
        Assert.Throws<ArgumentNullException>(() => services!.AddSileroVad("any.onnx"));
    }

    [Fact]
    public void AddSileroVad_EmptyPath_Throws()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentException>(() => services.AddSileroVad(string.Empty));
    }

    [Fact]
    public void AddSileroVad_NullOptions_Throws()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentNullException>(() => services.AddSileroVad((SileroVadDetectorOptions)null!));
    }

    [Fact]
    public async Task AnalyzeAsync_EmptySource_CompletesCleanly()
    {
        // We don't need the ONNX session to be valid because the empty source never reaches
        // the inference call. The cancellation token isn't required; the empty enumerable
        // completes immediately. Use SkippableFact for the "with model" version; this one
        // proves the cancellation / await-foreach plumbing.
        var modelPath = Environment.GetEnvironmentVariable("SILERO_ONNX_MODEL_PATH");
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
        {
            // No model — we can't even construct the detector. Just assert that the
            // construction path throws for missing files (the InferenceSession constructor
            // fails to load), which is what the contract promises.
            Assert.ThrowsAny<Exception>(() => new SileroVadDetector(
                new SileroVadDetectorOptions { ModelPath = "definitely-not-real.onnx" }));
            return;
        }

        using var detector = new SileroVadDetector(new SileroVadDetectorOptions { ModelPath = modelPath! });
        var count = 0;
        await foreach (var _ in detector.AnalyzeAsync(EmptyFrames(), new VadOptions()))
        {
            count++;
        }
        Assert.Equal(0, count);
    }

    [SkippableFact]
    public async Task AnalyzeAsync_AllZerosInput_DoesNotEmitSpeech()
    {
        var modelPath = Environment.GetEnvironmentVariable("SILERO_ONNX_MODEL_PATH");
        Skip.If(string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath),
            "SILERO_ONNX_MODEL_PATH not set — skipping live model integration test.");

        using var detector = new SileroVadDetector(new SileroVadDetectorOptions { ModelPath = modelPath! });

        var events = new List<VadEvent>();
        await foreach (var ev in detector.AnalyzeAsync(SilentFrames(SileroVadDetector.ChunkSamples, count: 32), new VadOptions()))
        {
            events.Add(ev);
        }

        // No speech should be detected in pure zero PCM — only at most EnergyUpdate events.
        Assert.DoesNotContain(events, e => e.Kind == VadEventKind.SpeechStart);
    }

    private static async IAsyncEnumerable<AudioFrame> EmptyFrames()
    {
        await Task.Yield();
        yield break;
    }

    private static async IAsyncEnumerable<AudioFrame> SilentFrames(int samplesPerFrame, int count)
    {
        var bytes = new byte[samplesPerFrame * 2];   // all zero = silence
        var start = DateTimeOffset.UtcNow;
        for (var i = 0; i < count; i++)
        {
            yield return AudioFrame.Microphone16kMono(bytes, start.AddMilliseconds(i * 32));
            await Task.Yield();
        }
    }
}
