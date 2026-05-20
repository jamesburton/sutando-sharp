using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Sutando.LocalInference;
using Sutando.Pipeline;
using Sutando.Pipeline.Stages;

namespace Sutando.Tests.Pipeline.Stages;

[SuppressMessage("Usage", "SUTANDO001", Justification = "Tests for our own experimental surface.")]
[SuppressMessage("Usage", "MEAI001", Justification = "Tests against experimental MEAI surfaces.")]
public sealed class SpeechToTextStageTests
{
    [Fact]
    public async Task SpeechToTextStage_AccumulatesBetweenVadEdges_AndEmitsFinalTextFrame()
    {
        var fakeStt = new ScriptedStt("hello world");
        var stage = new SpeechToTextStage(fakeStt);

        var now = DateTimeOffset.UtcNow;
        var inputs = new PipelineFrame[]
        {
            new VadFrame(VadEvent.SpeechStart(now, 0.9f)),
            new AudioInputFrame(AudioFrame.Microphone16kMono(new byte[3200])),
            new AudioInputFrame(AudioFrame.Microphone16kMono(new byte[3200])),
            new VadFrame(VadEvent.SpeechEnd(now.AddSeconds(1), 0.1f)),
        };

        var outputs = new List<PipelineFrame>();
        await foreach (var frame in stage.ProcessAsync(ToAsync(inputs), CancellationToken.None))
        {
            outputs.Add(frame);
        }

        // Original frames must all pass through.
        Assert.Equal(2, outputs.OfType<VadFrame>().Count());
        Assert.Equal(2, outputs.OfType<AudioInputFrame>().Count());

        // Plus the transcribed final TextFrame after SpeechEnd.
        var text = outputs.OfType<TextFrame>().Single();
        Assert.True(text.IsFinal);
        Assert.Equal("hello world", text.Text);
    }

    [Fact]
    public async Task SpeechToTextStage_AudioOutsideSpeechTurn_IsForwardedButNotTranscribed()
    {
        var fakeStt = new ScriptedStt("should-not-fire");
        var stage = new SpeechToTextStage(fakeStt);

        // No VAD SpeechStart at all — audio frames just pass through without triggering a
        // transcription. This guarantees the stage doesn't accidentally transcribe noise
        // when VAD upstream is silent.
        var inputs = new PipelineFrame[]
        {
            new AudioInputFrame(AudioFrame.Microphone16kMono(new byte[3200])),
            new AudioInputFrame(AudioFrame.Microphone16kMono(new byte[3200])),
        };

        var outputs = new List<PipelineFrame>();
        await foreach (var frame in stage.ProcessAsync(ToAsync(inputs), CancellationToken.None))
        {
            outputs.Add(frame);
        }

        Assert.Equal(2, outputs.Count);
        Assert.All(outputs, f => Assert.IsType<AudioInputFrame>(f));
        Assert.Empty(outputs.OfType<TextFrame>());
        Assert.Equal(0, fakeStt.CallCount);
    }

    [Fact]
    public void WrapPcmInWavHeader_ProducesValidRiffWaveHeader()
    {
        // Sanity check on the WAV-wrapping helper that lets MEAI STT clients consume the
        // raw PCM the upstream stages emit. RIFF + WAVE magic bytes are the smoke test.
        var pcm = new byte[3200];
        var wav = SpeechToTextStage.WrapPcmInWavHeader(pcm, 0, pcm.Length, 16_000, 1, AudioEncoding.Pcm16Le);

        Assert.Equal(44 + pcm.Length, wav.Length);
        Assert.Equal((byte)'R', wav[0]);
        Assert.Equal((byte)'I', wav[1]);
        Assert.Equal((byte)'F', wav[2]);
        Assert.Equal((byte)'F', wav[3]);
        Assert.Equal((byte)'W', wav[8]);
        Assert.Equal((byte)'A', wav[9]);
        Assert.Equal((byte)'V', wav[10]);
        Assert.Equal((byte)'E', wav[11]);
    }

    private static async IAsyncEnumerable<PipelineFrame> ToAsync(IEnumerable<PipelineFrame> source)
    {
        foreach (var frame in source)
        {
            await Task.Yield();
            yield return frame;
        }
    }

    private sealed class ScriptedStt : ISpeechToTextClient
    {
        private readonly string _transcript;
        private int _callCount;
        public ScriptedStt(string transcript) => _transcript = transcript;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<SpeechToTextResponse> GetTextAsync(Stream audioSpeechStream, SpeechToTextOptions? options = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(new SpeechToTextResponse(_transcript));
        }

        public IAsyncEnumerable<SpeechToTextResponseUpdate> GetStreamingTextAsync(Stream audioSpeechStream, SpeechToTextOptions? options = null, CancellationToken cancellationToken = default)
            => EmptyStreaming();

        private static async IAsyncEnumerable<SpeechToTextResponseUpdate> EmptyStreaming()
        {
            await Task.Yield();
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
