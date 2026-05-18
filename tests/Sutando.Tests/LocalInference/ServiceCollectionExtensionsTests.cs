using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Sutando.LocalInference;

namespace Sutando.Tests.LocalInference;

[SuppressMessage("Usage", "SUTANDO001", Justification = "Tests for our own experimental surface.")]
[SuppressMessage("Usage", "MEAI001", Justification = "Tests against experimental MEAI surfaces.")]
public sealed class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddSutandoLocalInference_RegistersEveryStage()
    {
        var services = new ServiceCollection();
        services.AddSutandoLocalInference(b => b
            .UseVadDetector(_ => new FakeVad())
            .UseSpeechToText(_ => new FakeStt())
            .UseChatClient(_ => new FakeChat())
            .UseTextToSpeech(_ => new FakeTts()));

        var sp = services.BuildServiceProvider();
        Assert.IsType<FakeVad>(sp.GetRequiredService<IVadDetector>());
        Assert.IsType<FakeStt>(sp.GetRequiredService<ISpeechToTextClient>());
        Assert.IsType<FakeChat>(sp.GetRequiredService<IChatClient>());
        Assert.IsType<FakeTts>(sp.GetRequiredService<ITextToSpeechClient>());
    }

    [Fact]
    public void AddSutandoLocalInference_LaterRegistrationOverridesEarlier()
    {
        var services = new ServiceCollection();
        services.AddSutandoLocalInference(b => b
            .UseChatClient(_ => new FakeChat { Tag = "first" })
            .UseChatClient(_ => new FakeChat { Tag = "second" }));

        var sp = services.BuildServiceProvider();
        var client = (FakeChat)sp.GetRequiredService<IChatClient>();
        Assert.Equal("second", client.Tag);
    }

    private sealed class FakeVad : IVadDetector
    {
        public string Id => "fake";
        public IAsyncEnumerable<VadEvent> AnalyzeAsync(IAsyncEnumerable<AudioFrame> source, VadOptions options, CancellationToken ct = default)
            => AsyncEnumerable.Empty<VadEvent>();
    }

    private sealed class FakeStt : ISpeechToTextClient
    {
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public Task<SpeechToTextResponse> GetTextAsync(Stream audioSpeechStream, SpeechToTextOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new SpeechToTextResponse());
        public IAsyncEnumerable<SpeechToTextResponseUpdate> GetStreamingTextAsync(Stream audioSpeechStream, SpeechToTextOptions? options = null, CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<SpeechToTextResponseUpdate>();
        public void Dispose() { }
    }

    private sealed class FakeChat : IChatClient
    {
        public string Tag { get; init; } = string.Empty;
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse());
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<ChatResponseUpdate>();
        public void Dispose() { }
    }

    private sealed class FakeTts : ITextToSpeechClient
    {
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public Task<TextToSpeechResponse> GetAudioAsync(string text, TextToSpeechOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new TextToSpeechResponse());
        public IAsyncEnumerable<TextToSpeechResponseUpdate> GetStreamingAudioAsync(string text, TextToSpeechOptions? options = null, CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<TextToSpeechResponseUpdate>();
        public void Dispose() { }
    }
}

internal static class AsyncEnumerable
{
    public static async IAsyncEnumerable<T> Empty<T>()
    {
        await Task.Yield();
        yield break;
    }
}
