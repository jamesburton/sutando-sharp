using System.Diagnostics.CodeAnalysis;
using Sutando.LocalInference;

namespace Sutando.Tests.LocalInference;

[SuppressMessage("Usage", "SUTANDO001", Justification = "Tests for our own experimental surface.")]
public sealed class VadEventTests
{
    [Fact]
    public void SpeechStart_HasExpectedShape()
    {
        var ts = DateTimeOffset.UtcNow;
        var ev = VadEvent.SpeechStart(ts, 0.85f);
        Assert.Equal(VadEventKind.SpeechStart, ev.Kind);
        Assert.Equal(ts, ev.At);
        Assert.Equal(0.85f, ev.Probability);
    }

    [Fact]
    public void SpeechEnd_HasExpectedShape()
    {
        var ts = DateTimeOffset.UtcNow;
        var ev = VadEvent.SpeechEnd(ts, 0.10f);
        Assert.Equal(VadEventKind.SpeechEnd, ev.Kind);
        Assert.Equal(0.10f, ev.Probability);
    }

    [Fact]
    public void EnergyUpdate_HasExpectedShape()
    {
        var ts = DateTimeOffset.UtcNow;
        var ev = VadEvent.EnergyUpdate(ts, 0.42f);
        Assert.Equal(VadEventKind.EnergyUpdate, ev.Kind);
        Assert.Equal(0.42f, ev.Probability);
    }

    [Fact]
    public void VadOptions_Defaults_MatchUpstreamSileroConventions()
    {
        var opts = new VadOptions();
        Assert.Equal(0.5f, opts.Threshold);
        Assert.Equal(TimeSpan.FromMilliseconds(120), opts.MinSpeech);
        Assert.Equal(TimeSpan.FromMilliseconds(700), opts.SilenceHangover);
        Assert.Equal(TimeSpan.FromMilliseconds(50), opts.EnergyUpdateInterval);
    }

    [Fact]
    public void VadOptions_Record_ImmutableUpdate()
    {
        var opts = new VadOptions();
        var tweaked = opts with { Threshold = 0.7f, SilenceHangover = TimeSpan.FromSeconds(1) };
        Assert.Equal(0.5f, opts.Threshold);             // original unchanged
        Assert.Equal(0.7f, tweaked.Threshold);
        Assert.Equal(TimeSpan.FromSeconds(1), tweaked.SilenceHangover);
    }
}
