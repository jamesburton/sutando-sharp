using System.Diagnostics.CodeAnalysis;
using Sutando.LocalInference;

namespace Sutando.Tests.LocalInference;

[SuppressMessage("Usage", "SUTANDO001", Justification = "Tests for our own experimental surface.")]
public sealed class AudioFrameTests
{
    [Fact]
    public void Microphone16kMono_FillsExpectedDefaults()
    {
        var pcm = new byte[3200]; // 100 ms @ 16 kHz mono 16-bit = 1600 samples × 2 bytes
        var frame = AudioFrame.Microphone16kMono(pcm);

        Assert.Equal(16_000, frame.SampleRate);
        Assert.Equal(1, frame.Channels);
        Assert.Equal(AudioEncoding.Pcm16Le, frame.Encoding);
        Assert.Equal(3200, frame.Pcm.Length);
        Assert.NotEqual(default, frame.CapturedAt);
    }

    [Fact]
    public void Speaker24kMono_FillsExpectedDefaults()
    {
        var pcm = new byte[4800];
        var frame = AudioFrame.Speaker24kMono(pcm);

        Assert.Equal(24_000, frame.SampleRate);
        Assert.Equal(1, frame.Channels);
        Assert.Equal(AudioEncoding.Pcm16Le, frame.Encoding);
        Assert.Equal(4800, frame.Pcm.Length);
    }

    [Fact]
    public void SampleCount_IsBytesDividedByEncodingWidthTimesChannels()
    {
        var monoPcm16 = new AudioFrame { SampleRate = 16_000, Channels = 1, Encoding = AudioEncoding.Pcm16Le, Pcm = new byte[3200] };
        Assert.Equal(1600, monoPcm16.SampleCount);

        var stereoPcm16 = new AudioFrame { SampleRate = 48_000, Channels = 2, Encoding = AudioEncoding.Pcm16Le, Pcm = new byte[3200] };
        Assert.Equal(800, stereoPcm16.SampleCount); // 3200 / (2 * 2) = 800 samples per channel

        var monoFloat32 = new AudioFrame { SampleRate = 16_000, Channels = 1, Encoding = AudioEncoding.Pcm32FloatLe, Pcm = new byte[6400] };
        Assert.Equal(1600, monoFloat32.SampleCount); // 6400 / 4 = 1600 float samples
    }

    [Fact]
    public void Duration_DerivesFromSampleCountAndRate()
    {
        var frame = AudioFrame.Microphone16kMono(new byte[3200]); // 1600 samples / 16 kHz = 100 ms
        Assert.Equal(TimeSpan.FromMilliseconds(100), frame.Duration);
    }

    [Fact]
    public void Duration_ZeroSampleRate_ReturnsZero()
    {
        var frame = new AudioFrame { SampleRate = 0, Channels = 1, Encoding = AudioEncoding.Pcm16Le, Pcm = new byte[100] };
        Assert.Equal(TimeSpan.Zero, frame.Duration);
    }

    [Fact]
    public void CapturedAt_PassthroughOverridesDefault()
    {
        var stamp = new DateTimeOffset(2026, 5, 18, 10, 0, 0, TimeSpan.Zero);
        var frame = AudioFrame.Microphone16kMono(new byte[3200], stamp);
        Assert.Equal(stamp, frame.CapturedAt);
    }
}
