using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Sutando.LocalInference.KokoroSharp;

namespace Sutando.Tests.LocalInference.KokoroSharp;

/// <summary>
/// Tests for the KokoroSharp TTS wrapper that don't need the Kokoro ONNX model on disk —
/// the constructor calls <c>new InferenceSession(modelPath)</c> immediately, so the
/// "model present" path is <see cref="SkippableFactAttribute"/>-gated.
/// </summary>
[SuppressMessage("Usage", "SUTANDO001", Justification = "Tests target Sutando's experimental local-inference surface.")]
[SuppressMessage("Usage", "MEAI001", Justification = "ITextToSpeechClient is still marked experimental in MEAI 10.6.")]
public sealed class KokoroSharpTextToSpeechClientTests
{
    [Fact]
    public void Constructor_NullModelPath_Throws()
    {
        // ArgumentException.ThrowIfNullOrWhiteSpace throws ArgumentNullException (a subclass of
        // ArgumentException) for null, and ArgumentException for empty / whitespace. Accept both
        // via Assert.ThrowsAny<ArgumentException>.
        Assert.ThrowsAny<ArgumentException>(() => new KokoroSharpTextToSpeechClient(null!));
        Assert.ThrowsAny<ArgumentException>(() => new KokoroSharpTextToSpeechClient(string.Empty));
        Assert.ThrowsAny<ArgumentException>(() => new KokoroSharpTextToSpeechClient("   "));
    }

    [Fact]
    public void Constants_MatchKokoroOutputFormat()
    {
        // 24 kHz / mono / 16-bit are the fixed parameters of Kokoro's ONNX export. If KokoroSharp
        // ever ships a new model with a different shape, this test catches it.
        Assert.Equal(24_000, KokoroSharpTextToSpeechClient.SampleRateHz);
        Assert.Equal(1, KokoroSharpTextToSpeechClient.Channels);
        Assert.Equal(16, KokoroSharpTextToSpeechClient.BitsPerSample);
        Assert.Equal("audio/pcm; rate=24000; channels=1; bits=16", KokoroSharpTextToSpeechClient.PcmMediaType);
        Assert.Equal("af_heart", KokoroSharpTextToSpeechClient.DefaultVoiceName);
    }

    [Fact]
    public void AddKokoroSharp_NullServices_Throws()
    {
        IServiceCollection? services = null;
        Assert.Throws<ArgumentNullException>(() => services!.AddKokoroSharp("anything.onnx"));
    }

    [Fact]
    public void AddKokoroSharp_EmptyPath_Throws()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentException>(() => services.AddKokoroSharp(string.Empty));
    }

    [SkippableFact]
    public async Task GetAudioAsync_WithModelOnDisk_ProducesPcmContent()
    {
        var modelPath = Environment.GetEnvironmentVariable("KOKORO_ONNX_MODEL_PATH");
        Skip.If(string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath),
            "KOKORO_ONNX_MODEL_PATH not set or file missing — skipping live synthesis smoke test.");

        using var client = new KokoroSharpTextToSpeechClient(modelPath!);
        var response = await client.GetAudioAsync(
            "Hello, this is a test.",
            new TextToSpeechOptions { VoiceId = KokoroSharpTextToSpeechClient.DefaultVoiceName });

        Assert.NotNull(response);
        Assert.NotEmpty(response.Contents);

        // Whatever the chunking, at least one DataContent with our PCM media type must appear.
        Assert.Contains(response.Contents, c =>
            c is DataContent dc && dc.MediaType == KokoroSharpTextToSpeechClient.PcmMediaType);
    }

    [SkippableFact]
    public async Task GetStreamingAudioAsync_WithModelOnDisk_YieldsAtLeastOneUpdate()
    {
        var modelPath = Environment.GetEnvironmentVariable("KOKORO_ONNX_MODEL_PATH");
        Skip.If(string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath),
            "KOKORO_ONNX_MODEL_PATH not set or file missing — skipping streaming smoke test.");

        using var client = new KokoroSharpTextToSpeechClient(modelPath!);
        var updates = 0;

        await foreach (var update in client.GetStreamingAudioAsync(
            "This text has two sentences. Each one becomes a streaming segment.",
            new TextToSpeechOptions { VoiceId = KokoroSharpTextToSpeechClient.DefaultVoiceName }))
        {
            Assert.NotNull(update);
            updates++;
        }

        Assert.True(updates > 0, "Expected at least one TextToSpeechResponseUpdate from streaming synthesis.");
    }
}
