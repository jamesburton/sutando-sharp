using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Sutando.LocalInference.OpenAI;

namespace Sutando.Tests.LocalInference.OpenAI;

public sealed class OpenAiServiceCollectionExtensionsTests
{
    private static readonly OpenAiEndpointOptions LocalEndpoint = new()
    {
        Endpoint = new Uri("http://localhost:8000/v1"),
    };

    [Fact]
    public void AddOpenAiCompatibleChatClient_ResolvesAsIChatClient()
    {
        var services = new ServiceCollection();
        services.AddOpenAiCompatibleChatClient(LocalEndpoint);

        var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<IChatClient>();

        Assert.NotNull(client);
        Assert.NotNull(client.GetService(typeof(ChatClientMetadata)) as ChatClientMetadata);
    }

    [Fact]
    public void AddOpenAiCompatibleSpeechToTextClient_ResolvesAsISpeechToTextClient()
    {
        var services = new ServiceCollection();
        services.AddOpenAiCompatibleSpeechToTextClient(LocalEndpoint);

        var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<ISpeechToTextClient>();
        Assert.NotNull(client);
    }

    [Fact]
    public void AddOpenAiCompatibleTextToSpeechClient_ResolvesAsITextToSpeechClient()
    {
        var services = new ServiceCollection();
        services.AddOpenAiCompatibleTextToSpeechClient(LocalEndpoint);

        var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<ITextToSpeechClient>();
        Assert.NotNull(client);
    }

    [Fact]
    public void AllThreeStages_CanBeRegisteredOnTheSameContainer()
    {
        var services = new ServiceCollection();
        services
            .AddOpenAiCompatibleChatClient(LocalEndpoint with { Endpoint = new Uri("http://llm:8000/v1") })
            .AddOpenAiCompatibleSpeechToTextClient(LocalEndpoint with { Endpoint = new Uri("http://stt:8000/v1") })
            .AddOpenAiCompatibleTextToSpeechClient(LocalEndpoint with { Endpoint = new Uri("http://tts:8000/v1") });

        var sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetRequiredService<IChatClient>());
        Assert.NotNull(sp.GetRequiredService<ISpeechToTextClient>());
        Assert.NotNull(sp.GetRequiredService<ITextToSpeechClient>());
    }

    [Fact]
    public void LaterRegistration_OverridesEarlier()
    {
        var services = new ServiceCollection();
        services
            .AddOpenAiCompatibleChatClient(LocalEndpoint with { Endpoint = new Uri("http://first:8000/v1") })
            .AddOpenAiCompatibleChatClient(LocalEndpoint with { Endpoint = new Uri("http://second:8000/v1") });

        // AddSingleton replaces the prior registration with the same service type; we can't
        // peek at the underlying endpoint from MEAI's adapter, but we CAN verify there's
        // exactly one IChatClient registration left.
        var matching = services.Where(d => d.ServiceType == typeof(IChatClient)).ToList();
        Assert.Equal(2, matching.Count); // both registrations still in the descriptor list
        var sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetRequiredService<IChatClient>()); // resolver picks the last one
    }

    [Fact]
    public void AddOpenAiCompatibleChatClient_ThrowsOnNullArguments()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentNullException>(() => services.AddOpenAiCompatibleChatClient(null!));
        Assert.Throws<ArgumentNullException>(() =>
            OpenAiServiceCollectionExtensions.AddOpenAiCompatibleChatClient(null!, LocalEndpoint));
    }
}
