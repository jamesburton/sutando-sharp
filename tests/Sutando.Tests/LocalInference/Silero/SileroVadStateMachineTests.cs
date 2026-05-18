using System.Diagnostics.CodeAnalysis;
using Sutando.LocalInference;
using Sutando.LocalInference.Silero;

namespace Sutando.Tests.LocalInference.Silero;

/// <summary>
/// State-machine transition tests for the Silero VAD detector. We drive the state machine
/// with synthetic probability samples to verify the SpeechStart / SpeechEnd / EnergyUpdate
/// emission rules in isolation from the ONNX model.
/// </summary>
[SuppressMessage("Usage", "SUTANDO001", Justification = "Tests target Sutando's experimental local-inference surface.")]
public sealed class SileroVadStateMachineTests
{
    private static VadOptions MakeOptions(
        float threshold = 0.5f,
        double minSpeechMs = 60,
        double silenceMs = 100,
        double? energyMs = null) => new()
        {
            Threshold = threshold,
            MinSpeech = TimeSpan.FromMilliseconds(minSpeechMs),
            SilenceHangover = TimeSpan.FromMilliseconds(silenceMs),
            EnergyUpdateInterval = energyMs is null ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(energyMs.Value),
        };

    [Fact]
    public void EmptyStream_EmitsNothing()
    {
        var sm = new SileroVadStateMachine(MakeOptions());
        Assert.False(sm.IsSpeechActive);
    }

    [Fact]
    public void SingleAboveThresholdSample_DoesNotEmitSpeechStart()
    {
        // Default MinSpeech of 60 ms means one isolated above-threshold sample shouldn't trigger.
        var sm = new SileroVadStateMachine(MakeOptions());

        var events = sm.Push(0.9f, At(0)).ToList();

        Assert.DoesNotContain(events, e => e.Kind == VadEventKind.SpeechStart);
        Assert.False(sm.IsSpeechActive);
    }

    [Fact]
    public void SustainedHighProbability_EmitsSpeechStart()
    {
        var sm = new SileroVadStateMachine(MakeOptions(minSpeechMs: 60));

        sm.Push(0.9f, At(0));
        sm.Push(0.9f, At(30));
        var events = sm.Push(0.9f, At(60)).ToList();

        Assert.Contains(events, e => e.Kind == VadEventKind.SpeechStart);
        Assert.True(sm.IsSpeechActive);
    }

    [Fact]
    public void SpeechStart_FiresOnlyOnce_ForContinuousSpeech()
    {
        var sm = new SileroVadStateMachine(MakeOptions(minSpeechMs: 60));

        sm.Push(0.9f, At(0));
        sm.Push(0.9f, At(60));   // SpeechStart here
        sm.Push(0.9f, At(120));  // shouldn't fire again
        sm.Push(0.9f, At(180));

        // Push another above-threshold sample once we're active; it should produce no further SpeechStart.
        var more = sm.Push(0.9f, At(240)).ToList();
        Assert.DoesNotContain(more, e => e.Kind == VadEventKind.SpeechStart);
    }

    [Fact]
    public void TransientDipBelowThreshold_ResetsCandidateSpeechStart()
    {
        // Two near-misses separated by a dip shouldn't accumulate — each dip resets the timer.
        var sm = new SileroVadStateMachine(MakeOptions(minSpeechMs: 100));

        sm.Push(0.9f, At(0));
        sm.Push(0.9f, At(50));
        sm.Push(0.1f, At(70));   // dip resets candidate
        sm.Push(0.9f, At(80));
        var events = sm.Push(0.9f, At(130)).ToList();   // only 50 ms of sustained speech post-reset

        Assert.DoesNotContain(events, e => e.Kind == VadEventKind.SpeechStart);
    }

    [Fact]
    public void SilenceAfterSpeech_FiresSpeechEnd_AfterHangover()
    {
        var sm = new SileroVadStateMachine(MakeOptions(minSpeechMs: 60, silenceMs: 100));

        // Get into the speech state.
        sm.Push(0.9f, At(0));
        sm.Push(0.9f, At(60));   // SpeechStart
        Assert.True(sm.IsSpeechActive);

        // Now silence the input for at least the hangover window.
        sm.Push(0.1f, At(120));
        var endEvents = sm.Push(0.1f, At(220)).ToList();   // 100 ms of silence after first dip

        Assert.Contains(endEvents, e => e.Kind == VadEventKind.SpeechEnd);
        Assert.False(sm.IsSpeechActive);
    }

    [Fact]
    public void TransientAboveThreshold_DuringSilence_ResetsHangoverCountdown()
    {
        var sm = new SileroVadStateMachine(MakeOptions(minSpeechMs: 60, silenceMs: 100));

        sm.Push(0.9f, At(0));
        sm.Push(0.9f, At(60));   // SpeechStart

        sm.Push(0.1f, At(120));   // silence begins
        sm.Push(0.9f, At(170));   // pops back above threshold → resets countdown
        sm.Push(0.1f, At(180));   // silence resumes
        var events = sm.Push(0.1f, At(230)).ToList();  // only 50 ms of fresh silence

        Assert.DoesNotContain(events, e => e.Kind == VadEventKind.SpeechEnd);
        Assert.True(sm.IsSpeechActive);
    }

    [Fact]
    public void EnergyUpdate_FiresOnFirstSample_WhenIntervalEnabled()
    {
        var sm = new SileroVadStateMachine(MakeOptions(energyMs: 50));

        var events = sm.Push(0.3f, At(0)).ToList();

        Assert.Contains(events, e => e.Kind == VadEventKind.EnergyUpdate);
    }

    [Fact]
    public void EnergyUpdate_ThrottledByInterval()
    {
        var sm = new SileroVadStateMachine(MakeOptions(energyMs: 100));

        var first = sm.Push(0.3f, At(0));
        var second = sm.Push(0.3f, At(50));   // within the throttle window
        var third = sm.Push(0.3f, At(150));   // past the throttle window

        Assert.Contains(first, e => e.Kind == VadEventKind.EnergyUpdate);
        Assert.DoesNotContain(second, e => e.Kind == VadEventKind.EnergyUpdate);
        Assert.Contains(third, e => e.Kind == VadEventKind.EnergyUpdate);
    }

    [Fact]
    public void EnergyUpdate_Disabled_WhenIntervalIsInfinite()
    {
        var sm = new SileroVadStateMachine(MakeOptions());   // energyMs = null → InfiniteTimeSpan

        for (var ms = 0; ms < 1000; ms += 50)
        {
            var events = sm.Push(0.3f, At(ms)).ToList();
            Assert.DoesNotContain(events, e => e.Kind == VadEventKind.EnergyUpdate);
        }
    }

    [Fact]
    public void Reset_RestoresInitialState()
    {
        var sm = new SileroVadStateMachine(MakeOptions(minSpeechMs: 60));

        sm.Push(0.9f, At(0));
        sm.Push(0.9f, At(60));   // SpeechStart
        Assert.True(sm.IsSpeechActive);

        sm.Reset();

        Assert.False(sm.IsSpeechActive);

        // After reset we need a fresh full MinSpeech window again — one above-threshold sample is not enough.
        var events = sm.Push(0.9f, At(70)).ToList();
        Assert.DoesNotContain(events, e => e.Kind == VadEventKind.SpeechStart);
    }

    private static DateTimeOffset At(double ms) =>
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(ms);
}
