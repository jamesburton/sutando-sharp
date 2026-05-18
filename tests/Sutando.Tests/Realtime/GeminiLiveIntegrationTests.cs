using Sutando.Realtime;

namespace Sutando.Tests.Realtime;

/// <summary>
/// Live-wire integration test against the Gemini Live endpoint. Skipped unless a real
/// <c>GEMINI_API_KEY</c> is available. Run manually with
/// <c>dotnet test --filter VoiceSessionLive_setup_complete_arrives_within_five_seconds</c>
/// after removing the <c>Skip</c> attribute and supplying the env var.
/// </summary>
public sealed class GeminiLiveIntegrationTests
{
    [Fact(Skip = "requires GEMINI_API_KEY — flip Skip to null to run live")]
    public async Task VoiceSessionLive_setup_complete_arrives_within_five_seconds()
    {
        var apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
            ?? throw new InvalidOperationException("Set GEMINI_API_KEY before running.");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var session = new VoiceSession();

        var config = new RealtimeSessionConfig(
            Model: "gemini-2.5-flash-live-preview",
            ApiKey: apiKey,
            SystemInstruction: "You are a terse test responder. Say nothing on connect.");

        var setupComplete = new TaskCompletionSource();
        session.EventReceived += (_, evt) =>
        {
            if (evt is RealtimeSetupComplete)
            {
                setupComplete.TrySetResult();
            }
        };

        await session.ConnectAsync(config, cts.Token);

        var completed = await Task.WhenAny(setupComplete.Task, Task.Delay(TimeSpan.FromSeconds(5), cts.Token));
        Assert.Same(setupComplete.Task, completed);
        Assert.Equal(VoiceSessionState.Listening, session.State);

        await session.DisconnectAsync(cts.Token);
    }
}
