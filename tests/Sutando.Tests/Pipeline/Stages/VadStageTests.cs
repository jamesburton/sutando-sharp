using System.Diagnostics.CodeAnalysis;
using Sutando.LocalInference;
using Sutando.Pipeline;
using Sutando.Pipeline.Stages;

namespace Sutando.Tests.Pipeline.Stages;

[SuppressMessage("Usage", "SUTANDO001", Justification = "Tests for our own experimental surface.")]
public sealed class VadStageTests
{
    [Fact]
    public async Task VadStage_ForwardsAudioFrames_AndEmitsVadEvents()
    {
        var fakeVad = new FakeVadDetector(new[]
        {
            VadEvent.SpeechStart(DateTimeOffset.UtcNow, 0.85f),
            VadEvent.SpeechEnd(DateTimeOffset.UtcNow.AddMilliseconds(500), 0.15f),
        });

        var stage = new VadStage(fakeVad);

        var inputs = new List<PipelineFrame>
        {
            ControlFrame.Start,
            new AudioInputFrame(AudioFrame.Microphone16kMono(new byte[3200])),
            new AudioInputFrame(AudioFrame.Microphone16kMono(new byte[3200])),
            ControlFrame.Stop,
        };

        var outputs = new List<PipelineFrame>();
        await foreach (var frame in stage.ProcessAsync(ToAsync(inputs), CancellationToken.None))
        {
            outputs.Add(frame);
        }

        // Original frames must all appear in the output stream.
        Assert.Contains(outputs, f => f is ControlFrame { Signal: ControlSignal.Start });
        Assert.Equal(2, outputs.OfType<AudioInputFrame>().Count());
        Assert.Contains(outputs, f => f is ControlFrame { Signal: ControlSignal.Stop });

        // Plus the VadFrames produced by the detector.
        var vadFrames = outputs.OfType<VadFrame>().ToList();
        Assert.Equal(2, vadFrames.Count);
        Assert.Equal(VadEventKind.SpeechStart, vadFrames[0].Event.Kind);
        Assert.Equal(VadEventKind.SpeechEnd, vadFrames[1].Event.Kind);
    }

    [Fact]
    public async Task VadStage_NoAudioInput_StillForwardsControl()
    {
        var fakeVad = new FakeVadDetector(Array.Empty<VadEvent>());
        var stage = new VadStage(fakeVad);

        var inputs = new PipelineFrame[]
        {
            ControlFrame.Start,
            new TextFrame("hello", IsFinal: true),
            ControlFrame.TurnComplete,
        };

        var outputs = new List<PipelineFrame>();
        await foreach (var frame in stage.ProcessAsync(ToAsync(inputs), CancellationToken.None))
        {
            outputs.Add(frame);
        }

        Assert.Equal(3, outputs.Count);
        Assert.IsType<ControlFrame>(outputs[0]);
        Assert.IsType<TextFrame>(outputs[1]);
        Assert.IsType<ControlFrame>(outputs[2]);
    }

    private static async IAsyncEnumerable<PipelineFrame> ToAsync(IEnumerable<PipelineFrame> source)
    {
        foreach (var frame in source)
        {
            await Task.Yield();
            yield return frame;
        }
    }

    private sealed class FakeVadDetector : IVadDetector
    {
        private readonly VadEvent[] _events;
        public FakeVadDetector(VadEvent[] events) => _events = events;
        public string Id => "fake";

        public async IAsyncEnumerable<VadEvent> AnalyzeAsync(
            IAsyncEnumerable<AudioFrame> source,
            VadOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            // Consume the audio stream so the producer doesn't block, but emit our pre-canned
            // event list paced 1:1 with audio frames so the test is deterministic.
            var index = 0;
            await foreach (var _ in source.WithCancellation(ct))
            {
                if (index < _events.Length)
                {
                    yield return _events[index++];
                }
            }

            // Any remaining pre-canned events flush after the audio stream completes.
            while (index < _events.Length)
            {
                yield return _events[index++];
            }
        }
    }
}
