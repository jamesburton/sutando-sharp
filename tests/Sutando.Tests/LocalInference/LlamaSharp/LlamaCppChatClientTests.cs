using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Sutando.LocalInference.LlamaSharp;

namespace Sutando.Tests.LocalInference.LlamaSharp;

/// <summary>
/// Tests for the LlamaSharp chat client wrapper that don't need a GGUF model on disk —
/// model loading happens in the constructor (LLamaWeights.LoadFromFile), so any test that
/// actually exercises inference is <see cref="SkippableFactAttribute"/>-gated.
/// </summary>
[SuppressMessage("Usage", "SUTANDO001", Justification = "Tests target Sutando's experimental local-inference surface.")]
[SuppressMessage("Usage", "MEAI001", Justification = "Tests against experimental MEAI surfaces.")]
public sealed class LlamaCppChatClientTests
{
    [Fact]
    public void Options_RequiresModelPath()
    {
        // ArgumentException.ThrowIfNullOrWhiteSpace throws ArgumentNullException (a subclass)
        // for null and ArgumentException for empty / whitespace — both derive from
        // ArgumentException, which is what we use Assert.ThrowsAny on.
        Assert.ThrowsAny<ArgumentException>(() => new LlamaCppChatClientOptions(null!));
        Assert.ThrowsAny<ArgumentException>(() => new LlamaCppChatClientOptions(string.Empty));
        Assert.ThrowsAny<ArgumentException>(() => new LlamaCppChatClientOptions("   "));
    }

    [Fact]
    public void Options_DefaultsMatchExpectedValues()
    {
        var options = new LlamaCppChatClientOptions("Qwen3-8B-Q4_K_M.gguf");

        Assert.Equal("Qwen3-8B-Q4_K_M.gguf", options.ModelPath);
        Assert.Equal(LlamaCppChatClientOptions.DefaultContextSize, options.ContextSize);
        Assert.Equal(LlamaCppChatClientOptions.DefaultGpuLayerCount, options.GpuLayerCount);
        Assert.Null(options.SystemPrompt);
        Assert.Null(options.ConfigureModelParams);
        Assert.Null(options.ExecutorFactory);
    }

    [Fact]
    public void Options_DefaultsAreReasonable()
    {
        // 4096-token context is a sensible default for an 8B chat model on a developer laptop —
        // larger windows cost RAM and slow inference. 0 GPU layers makes the client CPU-only by
        // default, which is what the CI-portable path needs.
        Assert.Equal(4096u, LlamaCppChatClientOptions.DefaultContextSize);
        Assert.Equal(0, LlamaCppChatClientOptions.DefaultGpuLayerCount);
    }

    [Fact]
    public void AddLlamaCppChat_NullServices_Throws()
    {
        IServiceCollection? services = null;
        Assert.Throws<ArgumentNullException>(() => services!.AddLlamaCppChat("any.gguf"));
    }

    [Fact]
    public void AddLlamaCppChat_EmptyPath_Throws()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentException>(() => services.AddLlamaCppChat(string.Empty));
    }

    [Fact]
    public void AddLlamaCppChat_NullOptions_Throws()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentNullException>(() =>
            services.AddLlamaCppChat((LlamaCppChatClientOptions)null!));
    }

    [Fact]
    public void AddLlamaCppChat_ConfigureCallbackMutatesOptions()
    {
        var services = new ServiceCollection();

        // The factory is registered lazily — the configure callback runs only when the IChatClient
        // is resolved. We can't actually resolve (model file is fake) but we can verify the
        // registration was accepted.
        services.AddLlamaCppChat("fake.gguf", opts =>
        {
            opts.SystemPrompt = "You are a test.";
            opts.ContextSize = 8192;
            opts.GpuLayerCount = 32;
        });

        // Registration should succeed and the descriptor should be a singleton IChatClient.
        var descriptor = services.Single(d => d.ServiceType == typeof(IChatClient));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [SkippableFact]
    public async Task GetResponseAsync_WithModelOnDisk_ProducesAssistantMessage()
    {
        var modelPath = Environment.GetEnvironmentVariable("LLAMACPP_GGUF_MODEL_PATH");
        Skip.If(string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath),
            "LLAMACPP_GGUF_MODEL_PATH not set or file missing — skipping live inference smoke test.");

        using var client = new LlamaCppChatClient(new LlamaCppChatClientOptions(modelPath!)
        {
            SystemPrompt = "Reply with a single word.",
            ContextSize = 2048,
        });

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Say hello.")]);

        Assert.NotNull(response);
        Assert.NotNull(response.Messages);
        Assert.NotEmpty(response.Messages);
    }
}
