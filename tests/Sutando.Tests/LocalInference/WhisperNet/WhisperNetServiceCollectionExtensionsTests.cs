using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Sutando.LocalInference.WhisperNet;
using Whisper.net;

namespace Sutando.Tests.LocalInference.WhisperNet;

/// <summary>
/// Smoke tests for the Whisper.net DI wiring. Live transcription is
/// <see cref="SkippableFactAttribute"/>-gated because GGML model files are big and not in CI.
/// </summary>
[SuppressMessage("Usage", "SUTANDO001", Justification = "Tests target Sutando's experimental local-inference surface.")]
[SuppressMessage("Usage", "MEAI001", Justification = "ISpeechToTextClient is still marked experimental in MEAI 10.6.")]
public sealed class WhisperNetServiceCollectionExtensionsTests
{
    [Fact]
    public void AddWhisperNet_RegistersWhisperSpeechToTextClient()
    {
        // We don't need the model file to load to verify the registration — the factory
        // is lazy, so the singleton resolves to a constructed WhisperSpeechToTextClient
        // pointing at a non-existent path. The factory only opens the file on the first
        // transcription call.
        var services = new ServiceCollection();
        services.AddWhisperNet("does-not-exist.bin");

        using var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<ISpeechToTextClient>();

        Assert.NotNull(client);
        Assert.IsType<WhisperSpeechToTextClient>(client);

        // Dispose should be safe even when the model was never loaded.
        client.Dispose();
    }

    [Fact]
    public void AddWhisperNet_NullServices_Throws()
    {
        IServiceCollection? services = null;
        Assert.Throws<ArgumentNullException>(() => services!.AddWhisperNet("any.bin"));
    }

    [Fact]
    public void AddWhisperNet_EmptyPath_Throws()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentException>(() => services.AddWhisperNet(string.Empty));
        Assert.Throws<ArgumentException>(() => services.AddWhisperNet("   "));
    }

    [Fact]
    public void CreateClient_WithFactoryCallback_BuildsWhisperSpeechToTextClient()
    {
        // The callback shape must be wired so consumers can pin runtime / threads. We can't
        // exercise the inner factory invocation without a model, but we can verify the client
        // type and dispose safely.
        using var client = WhisperNetServiceCollectionExtensions.CreateClient(
            "does-not-exist.bin",
            configureFactory: opts => { _ = opts; });

        Assert.NotNull(client);
    }

    [SkippableFact]
    public async Task GetTextAsync_WithModelOnDisk_ProducesTranscript()
    {
        var modelPath = Environment.GetEnvironmentVariable("WHISPER_NET_MODEL_PATH");
        Skip.If(string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath),
            "WHISPER_NET_MODEL_PATH not set or file missing — skipping live transcription smoke test.");

        var samplePath = Environment.GetEnvironmentVariable("WHISPER_NET_SAMPLE_WAV");
        Skip.If(string.IsNullOrWhiteSpace(samplePath) || !File.Exists(samplePath),
            "WHISPER_NET_SAMPLE_WAV not set — skipping live transcription smoke test.");

        using var client = WhisperNetServiceCollectionExtensions.CreateClient(modelPath!);
        await using var stream = File.OpenRead(samplePath!);
        var response = await client.GetTextAsync(stream);

        Assert.NotNull(response);
        Assert.NotEmpty(response.Contents);
    }
}
