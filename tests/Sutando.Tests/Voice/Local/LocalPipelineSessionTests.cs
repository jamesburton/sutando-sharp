using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using Sutando.Realtime;
using Sutando.Voice.Local;

namespace Sutando.Tests.Voice.Local;

/// <summary>
/// End-to-end tests for <see cref="LocalPipelineRealtimeClientSession"/> driven through the
/// MEAI <see cref="IRealtimeClientSession"/> surface — the exact seam the voice WS server uses.
/// Audio in via <see cref="IRealtimeClientSession.SendAsync"/>, MEAI messages out via
/// <see cref="IRealtimeClientSession.GetStreamingResponseAsync"/>. Fakes stand in for the four
/// stage components so no real model files are needed.
/// </summary>
[SuppressMessage("Usage", "SUTANDO001", Justification = "Tests for our own experimental surface.")]
[SuppressMessage("Usage", "MEAI001", Justification = "Tests against experimental MEAI surfaces.")]
public sealed class LocalPipelineSessionTests
{
    private static TimeSpan Deadline { get; } = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Session_EmitsSessionStarted_BeforeAnyPipelineOutput()
    {
        var client = new LocalPipelineRealtimeClient(LocalPipelineFakes.Build("hi"));
        await using var session = await client.CreateSessionAsync();

        using var cts = new CancellationTokenSource(Deadline);
        await using var reader = session.GetStreamingResponseAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        Assert.True(await reader.MoveNextAsync());
        // SutandoSessionStarted is what the WS server's VoiceSession maps onto RealtimeSetupComplete.
        Assert.Equal(SutandoRealtimeMessageTypes.SessionStarted, reader.Current.Type);
    }

    [Fact]
    public async Task Session_AudioTurn_FlowsThroughSttChatTts_ToAudioOutput()
    {
        // A burst of audio chunks → the bracketing VAD makes one turn → STT "transcribed user
        // turn" → chat streams "Hello world." → TTS synthesises it → audio output frame.
        var client = new LocalPipelineRealtimeClient(
            LocalPipelineFakes.Build("transcribed user turn", "Hello world."));
        await using var session = await client.CreateSessionAsync();

        using var cts = new CancellationTokenSource(Deadline);

        // Collect messages on a background task while we drive audio in.
        var messages = new List<RealtimeServerMessage>();
        var collector = Task.Run(async () =>
        {
            await foreach (var msg in session.GetStreamingResponseAsync(cts.Token))
            {
                lock (messages)
                {
                    messages.Add(msg);
                }
            }
        }, cts.Token);

        // Stream 20 ms 16 kHz mono PCM chunks continuously — like a real microphone. VadStage
        // drains its detector's events on each inbound audio frame, so a steady trickle (rather
        // than one burst then silence) is what guarantees the SpeechEnd edge is observed.
        using var feedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var feeder = Task.Run(async () =>
        {
            var chunk = new byte[640];
            while (!feedCts.IsCancellationRequested)
            {
                await session.SendAsync(new InputAudioBufferAppendRealtimeClientMessage(
                    new DataContent(chunk, "audio/pcm;rate=16000")), feedCts.Token);
                await Task.Delay(10, feedCts.Token);
            }
        }, feedCts.Token);

        // Wait for an audio-output message to appear, then tear the session down. Surface a
        // pipeline error promptly rather than letting the test hit the deadline silently.
        await WaitForAudioOrErrorAsync(messages, cts.Token);

        await feedCts.CancelAsync();
        try { await feeder; } catch (OperationCanceledException) { /* expected */ }
        await session.DisposeAsync();
        try { await collector; } catch (OperationCanceledException) { /* expected on teardown */ }

        lock (messages)
        {
            // Handshake first.
            Assert.Equal(SutandoRealtimeMessageTypes.SessionStarted, messages[0].Type);

            // The user-side transcript surfaced as an input transcription.
            var inputTranscript = messages
                .OfType<OutputTextAudioRealtimeServerMessage>()
                .FirstOrDefault(m => m.Type == RealtimeServerMessageType.InputAudioTranscriptionCompleted);
            Assert.NotNull(inputTranscript);
            Assert.Equal("transcribed user turn", inputTranscript!.Text);

            // At least one synthesised audio chunk reached the output.
            Assert.Contains(messages, IsAudioOutput);
        }
    }

    [Fact]
    public async Task Session_TextTurn_BypassesStt_AndProducesAudioOutput()
    {
        // A typed user turn (the browser's `text` envelope) should drive chat → TTS directly.
        var client = new LocalPipelineRealtimeClient(
            LocalPipelineFakes.Build(transcript: "unused", "Sure thing."));
        await using var session = await client.CreateSessionAsync();

        using var cts = new CancellationTokenSource(Deadline);
        var messages = new List<RealtimeServerMessage>();
        var collector = Task.Run(async () =>
        {
            await foreach (var msg in session.GetStreamingResponseAsync(cts.Token))
            {
                lock (messages)
                {
                    messages.Add(msg);
                }
            }
        }, cts.Token);

        var item = new RealtimeConversationItem(
            new List<AIContent> { new TextContent("what is the time?") },
            role: ChatRole.User);
        await session.SendAsync(new CreateConversationItemRealtimeClientMessage(item), cts.Token);

        await WaitUntilAsync(
            () => { lock (messages) { return messages.Any(IsAudioOutput); } },
            cts.Token);

        await session.DisposeAsync();
        try { await collector; } catch (OperationCanceledException) { /* expected */ }

        lock (messages)
        {
            Assert.Contains(messages, IsAudioOutput);
        }
    }

    [Fact]
    public async Task UnavailableSession_EmitsHandshakeThenErrorThenCompletes()
    {
        // The fail-graceful path: a misconfigured local stack still completes the handshake so
        // the browser shows the error rather than a dead socket.
        var client = LocalPipelineRealtimeClient.Unavailable(
            "sutando voice --local: the Whisper STT model is not configured.");
        await using var session = await client.CreateSessionAsync();

        using var cts = new CancellationTokenSource(Deadline);
        var messages = new List<RealtimeServerMessage>();
        await foreach (var msg in session.GetStreamingResponseAsync(cts.Token))
        {
            messages.Add(msg);
        }

        // Exactly two messages, then the stream completes: session-started, then the error.
        Assert.Equal(2, messages.Count);
        Assert.Equal(SutandoRealtimeMessageTypes.SessionStarted, messages[0].Type);
        var error = Assert.IsType<ErrorRealtimeServerMessage>(messages[1]);
        Assert.Contains("Whisper STT model", error.Error?.Message);
    }

    [Fact]
    public void BuildPipeline_ComposesSixStages()
    {
        // Source + VAD + STT + Chat + TTS + sink.
        var session = new LocalPipelineRealtimeClientSession(LocalPipelineFakes.Build("x"));
        var pipeline = session.BuildPipeline();
        Assert.Equal(6, pipeline.StageCount);
    }

    private static bool IsAudioOutput(RealtimeServerMessage message) =>
        message is OutputTextAudioRealtimeServerMessage { Type: var t }
        && t == RealtimeServerMessageType.OutputAudioDelta;

    /// <summary>
    /// Poll until an audio-output message appears, throwing immediately if the pipeline surfaced
    /// an <see cref="ErrorRealtimeServerMessage"/> instead.
    /// </summary>
    private static async Task WaitForAudioOrErrorAsync(List<RealtimeServerMessage> messages, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            lock (messages)
            {
                var error = messages.OfType<ErrorRealtimeServerMessage>().FirstOrDefault();
                if (error is not null)
                {
                    throw new Xunit.Sdk.XunitException($"Pipeline faulted: {error.Error?.Message}");
                }
                if (messages.Any(IsAudioOutput))
                {
                    return;
                }
            }
            await Task.Delay(20, ct).ConfigureAwait(false);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(20, ct).ConfigureAwait(false);
        }
    }
}
