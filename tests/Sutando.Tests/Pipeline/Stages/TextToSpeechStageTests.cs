using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Sutando.Pipeline;
using Sutando.Pipeline.Stages;

namespace Sutando.Tests.Pipeline.Stages;

[SuppressMessage("Usage", "SUTANDO001", Justification = "Tests for our own experimental surface.")]
[SuppressMessage("Usage", "MEAI001", Justification = "Tests against experimental MEAI surfaces.")]
public sealed class TextToSpeechStageTests
{
    [Fact]
    public async Task TextToSpeechStage_FlushesOnSentenceBoundary()
    {
        var fakeTts = new EchoTtsClient();
        var stage = new TextToSpeechStage(fakeTts);

        var inputs = new PipelineFrame[]
        {
            new TextFrame("Hello", IsFinal: false),
            new TextFrame(", world.", IsFinal: false), // period triggers a flush
            new TextFrame(" Second sentence.", IsFinal: false), // second flush
            ControlFrame.TurnComplete,
        };

        var outputs = new List<PipelineFrame>();
        await foreach (var frame in stage.ProcessAsync(ToAsync(inputs), CancellationToken.None))
        {
            outputs.Add(frame);
        }

        var audioFrames = outputs.OfType<AudioOutputFrame>().ToList();
        Assert.Equal(2, audioFrames.Count);
        Assert.Equal("Hello, world.", fakeTts.Synthesised[0]);
        Assert.Equal("Second sentence.", fakeTts.Synthesised[1]);

        // The TurnComplete control frame is forwarded.
        Assert.Contains(outputs, f => f is ControlFrame { Signal: ControlSignal.TurnComplete });
    }

    [Fact]
    public async Task TextToSpeechStage_TurnComplete_FlushesIncompletSentence()
    {
        var fakeTts = new EchoTtsClient();
        var stage = new TextToSpeechStage(fakeTts);

        var inputs = new PipelineFrame[]
        {
            new TextFrame("no terminator", IsFinal: false),
            ControlFrame.TurnComplete,
        };

        var outputs = new List<PipelineFrame>();
        await foreach (var frame in stage.ProcessAsync(ToAsync(inputs), CancellationToken.None))
        {
            outputs.Add(frame);
        }

        Assert.Single(fakeTts.Synthesised);
        Assert.Equal("no terminator", fakeTts.Synthesised[0]);
        Assert.Single(outputs.OfType<AudioOutputFrame>());
    }

    [Fact]
    public async Task TextToSpeechStage_Interrupt_DropsBufferAndForwards()
    {
        var fakeTts = new EchoTtsClient();
        var stage = new TextToSpeechStage(fakeTts);

        var inputs = new PipelineFrame[]
        {
            new TextFrame("partial without terminator", IsFinal: false),
            ControlFrame.Interrupt,
            ControlFrame.TurnComplete, // would normally flush, but buffer was cleared by interrupt
        };

        await foreach (var _ in stage.ProcessAsync(ToAsync(inputs), CancellationToken.None))
        {
            // drain
        }

        Assert.Empty(fakeTts.Synthesised);
    }

    [Fact]
    public void ParseAudioMediaType_ParsesRateAndChannels()
    {
        Assert.Equal((24_000, 1), TextToSpeechStage.ParseAudioMediaType("audio/pcm; rate=24000; channels=1; bits=16"));
        Assert.Equal((16_000, 2), TextToSpeechStage.ParseAudioMediaType("audio/pcm;rate=16000;channels=2"));
        Assert.Equal((TextToSpeechStage.DefaultSampleRate, 1), TextToSpeechStage.ParseAudioMediaType("audio/pcm"));
        Assert.Equal((TextToSpeechStage.DefaultSampleRate, 1), TextToSpeechStage.ParseAudioMediaType(null));
    }

    private static async IAsyncEnumerable<PipelineFrame> ToAsync(IEnumerable<PipelineFrame> source)
    {
        foreach (var frame in source)
        {
            await Task.Yield();
            yield return frame;
        }
    }

    /// <summary>TTS client that records each synthesis input and emits one PCM frame per call.</summary>
    private sealed class EchoTtsClient : ITextToSpeechClient
    {
        public List<string> Synthesised { get; } = new();

        public Task<TextToSpeechResponse> GetAudioAsync(string text, TextToSpeechOptions? options = null, CancellationToken cancellationToken = default)
        {
            Synthesised.Add(text);
            return Task.FromResult(new TextToSpeechResponse());
        }

        public async IAsyncEnumerable<TextToSpeechResponseUpdate> GetStreamingAudioAsync(
            string text,
            TextToSpeechOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Synthesised.Add(text);
            await Task.Yield();
            var pcm = new byte[Math.Max(text.Length * 2, 4)];
            yield return new TextToSpeechResponseUpdate(
            [
                new DataContent(pcm, "audio/pcm; rate=24000; channels=1; bits=16"),
            ]);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
