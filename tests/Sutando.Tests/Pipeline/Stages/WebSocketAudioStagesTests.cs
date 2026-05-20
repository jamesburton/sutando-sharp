using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Sutando.LocalInference;
using Sutando.Pipeline;
using Sutando.Pipeline.Stages;

namespace Sutando.Tests.Pipeline.Stages;

[SuppressMessage("Usage", "SUTANDO001", Justification = "Tests for our own experimental surface.")]
public sealed class WebSocketAudioStagesTests
{
    [Fact]
    public async Task WebSocketAudioSource_ReadsPcmChunks_AndEmitsControlFrames()
    {
        // Feed the in-memory transport one chunk of 16 kHz mono PCM (640 bytes = 20 ms),
        // then end-of-stream. The source must emit Start, one AudioInputFrame, then Stop.
        var transport = new InMemoryAudioByteStream();
        const int OneChunkBytes = 16_000 * 2 / 50; // 20 ms @ 16 kHz mono 16-bit = 640 bytes
        await transport.QueueInboundAsync(new byte[OneChunkBytes]);
        transport.CompleteInbound();

        var source = new WebSocketAudioSource(transport);

        var frames = new List<PipelineFrame>();
        await foreach (var frame in source.ProcessAsync(EmptyUnitStream(), CancellationToken.None))
        {
            frames.Add(frame);
        }

        Assert.Equal(ControlSignal.Start, ((ControlFrame)frames[0]).Signal);
        Assert.IsType<AudioInputFrame>(frames[1]);
        Assert.Equal(16_000, ((AudioInputFrame)frames[1]).Audio.SampleRate);
        Assert.Equal(AudioEncoding.Pcm16Le, ((AudioInputFrame)frames[1]).Audio.Encoding);
        Assert.Equal(ControlSignal.Stop, ((ControlFrame)frames[^1]).Signal);
    }

    [Fact]
    public async Task WebSocketAudioSink_WritesAudioFrames_AndIgnoresOtherKinds()
    {
        var transport = new InMemoryAudioByteStream();
        var sink = new WebSocketAudioSink(transport);

        var inputs = new PipelineFrame[]
        {
            ControlFrame.Start,
            new AudioOutputFrame(AudioFrame.Speaker24kMono(new byte[] { 0x01, 0x02, 0x03, 0x04 })),
            new TextFrame("ignored by sink", IsFinal: false),
            new AudioOutputFrame(AudioFrame.Speaker24kMono(new byte[] { 0x05, 0x06 })),
            ControlFrame.Stop,
        };

        await foreach (var _ in sink.ProcessAsync(ToAsync(inputs), CancellationToken.None))
        {
            // drain
        }

        // Only the audio bytes should have reached the transport, concatenated in order.
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 }, transport.OutboundBytes);
    }

    private static async IAsyncEnumerable<Unit> EmptyUnitStream()
    {
        await Task.Yield();
        yield break;
    }

    private static async IAsyncEnumerable<PipelineFrame> ToAsync(IEnumerable<PipelineFrame> source)
    {
        foreach (var frame in source)
        {
            await Task.Yield();
            yield return frame;
        }
    }

    /// <summary>Fake <see cref="IAudioByteStream"/> backed by a channel + a list. Tests can drive it directly.</summary>
    private sealed class InMemoryAudioByteStream : IAudioByteStream
    {
        private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();
        private readonly List<byte> _outbound = new();

        public byte[] OutboundBytes => _outbound.ToArray();

        public async ValueTask QueueInboundAsync(byte[] payload)
        {
            await _inbound.Writer.WriteAsync(payload);
        }

        public void CompleteInbound() => _inbound.Writer.TryComplete();

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            // Wait for a chunk to arrive; copy it into the caller's buffer. If the channel is
            // complete and empty, return 0 to signal end-of-stream.
            if (await _inbound.Reader.WaitToReadAsync(ct))
            {
                if (_inbound.Reader.TryRead(out var chunk))
                {
                    var bytes = Math.Min(chunk.Length, buffer.Length);
                    chunk.AsSpan(0, bytes).CopyTo(buffer.Span);
                    return bytes;
                }
            }

            return 0;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct)
        {
            _outbound.AddRange(buffer.ToArray());
            return ValueTask.CompletedTask;
        }
    }
}
