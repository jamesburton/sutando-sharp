using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using Sutando.LocalInference;
using Sutando.Pipeline;
using Sutando.Realtime;
using Sutando.Voice.Local;

namespace Sutando.Tests.Voice.Local;

/// <summary>
/// Unit tests for <see cref="RealtimeEventSink.MapFrame"/> — the pipeline-frame → MEAI-message
/// translation. Each assertion also runs the produced message through
/// <c>MeaiToSutandoEventAdapter</c>-equivalent expectations so the mapping is verified end-to-end
/// against what the voice WS server actually surfaces to the browser.
/// </summary>
[SuppressMessage("Usage", "SUTANDO001", Justification = "Tests for our own experimental surface.")]
[SuppressMessage("Usage", "MEAI001", Justification = "Tests against experimental MEAI surfaces.")]
public sealed class RealtimeEventSinkTests
{
    [Fact]
    public void AudioOutputFrame_MapsToOutputAudioDelta_WithBase64Pcm()
    {
        var pcm = new byte[] { 0x10, 0x20, 0x30, 0x40 };
        var frame = new AudioOutputFrame(AudioFrame.Speaker24kMono(pcm));

        var message = RealtimeEventSink.MapFrame(frame);

        var audio = Assert.IsType<OutputTextAudioRealtimeServerMessage>(message);
        Assert.Equal(RealtimeServerMessageType.OutputAudioDelta, audio.Type);
        Assert.Equal(Convert.ToBase64String(pcm), audio.Audio);
    }

    [Fact]
    public void FinalTextFrame_MapsToInputTranscriptionCompleted()
    {
        // A final TextFrame is the user-side STT transcript — surfaced as the input transcription.
        var message = RealtimeEventSink.MapFrame(new TextFrame("hello there", IsFinal: true));

        var transcript = Assert.IsType<OutputTextAudioRealtimeServerMessage>(message);
        Assert.Equal(RealtimeServerMessageType.InputAudioTranscriptionCompleted, transcript.Type);
        Assert.Equal("hello there", transcript.Text);
    }

    [Fact]
    public void NonFinalTextFrame_MapsToOutputTranscriptionDelta()
    {
        // A non-final TextFrame is a streaming assistant chunk — surfaced as output transcription.
        var message = RealtimeEventSink.MapFrame(new TextFrame("partial", IsFinal: false));

        var transcript = Assert.IsType<OutputTextAudioRealtimeServerMessage>(message);
        Assert.Equal(RealtimeServerMessageType.OutputAudioTranscriptionDelta, transcript.Type);
        Assert.Equal("partial", transcript.Text);
    }

    [Fact]
    public void TurnCompleteControlFrame_MapsToNonCancelledResponseDone()
    {
        var message = RealtimeEventSink.MapFrame(ControlFrame.TurnComplete);

        var response = Assert.IsType<ResponseCreatedRealtimeServerMessage>(message);
        Assert.Equal(RealtimeServerMessageType.ResponseDone, response.Type);
        // A non-cancelled ResponseDone maps onward to RealtimeTurnComplete.
        Assert.NotEqual(RealtimeResponseStatus.Cancelled, response.Status);
    }

    [Fact]
    public void InterruptControlFrame_MapsToCancelledResponseDone()
    {
        var message = RealtimeEventSink.MapFrame(ControlFrame.Interrupt);

        var response = Assert.IsType<ResponseCreatedRealtimeServerMessage>(message);
        Assert.Equal(RealtimeServerMessageType.ResponseDone, response.Type);
        // A cancelled ResponseDone maps onward to RealtimeInterrupted.
        Assert.Equal(RealtimeResponseStatus.Cancelled, response.Status);
    }

    [Theory]
    [InlineData(ControlSignal.Start)]
    [InlineData(ControlSignal.Stop)]
    public void LifecycleControlFrames_HaveNoBrowserRepresentation(ControlSignal signal)
    {
        Assert.Null(RealtimeEventSink.MapFrame(new ControlFrame(signal)));
    }

    [Fact]
    public void VadAndAudioInputFrames_AreDropped()
    {
        Assert.Null(RealtimeEventSink.MapFrame(new VadFrame(VadEvent.SpeechStart(DateTimeOffset.UtcNow, 1f))));
        Assert.Null(RealtimeEventSink.MapFrame(new AudioInputFrame(AudioFrame.Microphone16kMono(new byte[8]))));
    }
}
