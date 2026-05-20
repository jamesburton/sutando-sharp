using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Sutando.Pipeline;
using Sutando.Pipeline.Stages;

namespace Sutando.Tests.Pipeline.Stages;

[SuppressMessage("Usage", "SUTANDO001", Justification = "Tests for our own experimental surface.")]
[SuppressMessage("Usage", "MEAI001", Justification = "Tests against experimental MEAI surfaces.")]
public sealed class ChatStageTests
{
    [Fact]
    public async Task ChatStage_FinalUserText_StreamsAssistantUpdates_AndEmitsTurnComplete()
    {
        var fakeChat = new ScriptedChatClient(new[] { "Hello", ", ", "world!" });
        var stage = new ChatStage(fakeChat);

        var inputs = new PipelineFrame[]
        {
            new TextFrame("Hi assistant", IsFinal: true),
        };

        var outputs = new List<PipelineFrame>();
        await foreach (var frame in stage.ProcessAsync(ToAsync(inputs), CancellationToken.None))
        {
            outputs.Add(frame);
        }

        // First emission is the user frame forwarded; then the streamed assistant chunks;
        // then the TurnComplete control frame.
        Assert.Collection(outputs,
            f => Assert.Equal("Hi assistant", ((TextFrame)f).Text),
            f => Assert.Equal("Hello", ((TextFrame)f).Text),
            f => Assert.Equal(", ", ((TextFrame)f).Text),
            f => Assert.Equal("world!", ((TextFrame)f).Text),
            f => Assert.Equal(ControlSignal.TurnComplete, ((ControlFrame)f).Signal));

        Assert.False(((TextFrame)outputs[1]).IsFinal);

        // History should contain the system / user / assistant turn.
        var history = stage.HistorySnapshot;
        Assert.Equal(2, history.Count); // user + assistant (no system prompt provided)
        Assert.Equal(ChatRole.User, history[0].Role);
        Assert.Equal(ChatRole.Assistant, history[1].Role);
        Assert.Equal("Hello, world!", history[1].Text);
    }

    [Fact]
    public async Task ChatStage_Interrupt_CancelsInFlightCall_AndForwardsFrame()
    {
        // The scripted client blocks for 10 s on every chunk; an interrupt should abort the
        // call promptly and the stage should resume.
        var fakeChat = new SlowChatClient(TimeSpan.FromSeconds(10));
        var stage = new ChatStage(fakeChat);

        var inputs = AsyncInterruptScenario();

        var outputs = new List<PipelineFrame>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await foreach (var frame in stage.ProcessAsync(inputs, cts.Token))
        {
            outputs.Add(frame);
        }

        // Must see: user text (forwarded), then the interrupt (forwarded). TurnComplete must NOT
        // appear, since the in-flight call was cancelled before completion.
        Assert.Contains(outputs, f => f is TextFrame { IsFinal: true });
        Assert.Contains(outputs, f => f is ControlFrame { Signal: ControlSignal.Interrupt });
        Assert.DoesNotContain(outputs, f => f is ControlFrame { Signal: ControlSignal.TurnComplete });
    }

    [Fact]
    public async Task ChatStage_SystemPrompt_PrependsHistory()
    {
        var fakeChat = new ScriptedChatClient(new[] { "hi" });
        var stage = new ChatStage(fakeChat, systemPrompt: "You are a test bot.");

        var inputs = new PipelineFrame[] { new TextFrame("question", IsFinal: true) };
        await foreach (var _ in stage.ProcessAsync(ToAsync(inputs), CancellationToken.None))
        {
            // drain
        }

        var history = stage.HistorySnapshot;
        Assert.Equal(3, history.Count);
        Assert.Equal(ChatRole.System, history[0].Role);
        Assert.Equal("You are a test bot.", history[0].Text);
        Assert.Equal(ChatRole.User, history[1].Role);
        Assert.Equal(ChatRole.Assistant, history[2].Role);
    }

    /// <summary>
    /// Yields the user-text frame, waits long enough for the chat call to start, then yields
    /// the interrupt frame. Used by the interrupt test above.
    /// </summary>
    private static async IAsyncEnumerable<PipelineFrame> AsyncInterruptScenario()
    {
        yield return new TextFrame("Hi", IsFinal: true);
        await Task.Delay(100, CancellationToken.None);
        yield return ControlFrame.Interrupt;
    }

    private static async IAsyncEnumerable<PipelineFrame> ToAsync(IEnumerable<PipelineFrame> source)
    {
        foreach (var frame in source)
        {
            await Task.Yield();
            yield return frame;
        }
    }

    /// <summary>Chat client that returns a fixed sequence of chunks as streaming updates.</summary>
    private sealed class ScriptedChatClient : IChatClient
    {
        private readonly string[] _chunks;
        public ScriptedChatClient(string[] chunks) => _chunks = chunks;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse());

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var chunk in _chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>Chat client that blocks each chunk for a configurable delay so cancellation can be observed.</summary>
    private sealed class SlowChatClient : IChatClient
    {
        private readonly TimeSpan _perChunkDelay;
        public SlowChatClient(TimeSpan perChunkDelay) => _perChunkDelay = perChunkDelay;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse());

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            for (var i = 0; i < 100; i++)
            {
                await Task.Delay(_perChunkDelay, cancellationToken);
                yield return new ChatResponseUpdate(ChatRole.Assistant, "chunk");
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
