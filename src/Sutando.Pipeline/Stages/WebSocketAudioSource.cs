using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Sutando.LocalInference;

namespace Sutando.Pipeline.Stages;

/// <summary>
/// A pipeline source that surfaces PCM audio arriving over a WebSocket-like transport as
/// <see cref="AudioInputFrame"/>s. The actual transport is abstracted behind
/// <see cref="IAudioByteStream"/> so consumers (e.g. <c>Sutando.Voice</c>) can wire the
/// <c>System.Net.WebSockets.WebSocket</c> they already own without dragging an ASP.NET
/// dependency into this project.
/// </summary>
/// <remarks>
/// <para>
/// The source reads chunks of PCM bytes from the underlying stream, wraps each one in an
/// <see cref="AudioInputFrame"/> with the configured sample-rate / channels / encoding, and
/// yields it. When the stream ends (returns 0 bytes) the source emits a
/// <see cref="ControlFrame.Stop"/> and completes.
/// </para>
/// <para>
/// <b>Wire format</b>: the stream is assumed to carry raw PCM (no framing). For Sutando's
/// upstream WS protocol the audio comes in as base64-decoded payloads of envelopes; the
/// caller is responsible for stripping the envelope before feeding the source.
/// </para>
/// </remarks>
[Experimental("SUTANDO001")]
public sealed class WebSocketAudioSource : IPipelineStage<Unit, PipelineFrame>
{
    private readonly IAudioByteStream _stream;
    private readonly WebSocketAudioSourceOptions _options;

    /// <summary>Initialise the source.</summary>
    /// <param name="stream">The byte stream feeding the source. Caller owns its lifetime.</param>
    /// <param name="options">Sample rate / channels / chunk size. Pass <see langword="null"/> for the upstream-standard 16 kHz mono 16-bit at 20 ms chunks.</param>
    public WebSocketAudioSource(IAudioByteStream stream, WebSocketAudioSourceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _options = options ?? new WebSocketAudioSourceOptions();
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<PipelineFrame> ProcessAsync(
        IAsyncEnumerable<Unit> source,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return ControlFrame.Start;

        var chunkBytes = ComputeChunkBytes(_options);
        var buffer = new byte[chunkBytes];

        while (!ct.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await _stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (read <= 0)
            {
                break;
            }

            var pcm = new byte[read];
            Buffer.BlockCopy(buffer, 0, pcm, 0, read);

            var frame = new AudioFrame
            {
                SampleRate = _options.SampleRate,
                Channels = _options.Channels,
                Encoding = _options.Encoding,
                Pcm = pcm,
                CapturedAt = DateTimeOffset.UtcNow,
            };
            yield return new AudioInputFrame(frame);
        }

        yield return ControlFrame.Stop;
    }

    private static int ComputeChunkBytes(WebSocketAudioSourceOptions options)
    {
        var bytesPerSample = options.Encoding == AudioEncoding.Pcm32FloatLe ? 4 : 2;
        var samplesPerChunk = (int)(options.SampleRate * options.ChunkDuration.TotalSeconds);
        return Math.Max(bytesPerSample * options.Channels, samplesPerChunk * options.Channels * bytesPerSample);
    }
}

/// <summary>Tuning knobs for <see cref="WebSocketAudioSource"/>.</summary>
[Experimental("SUTANDO001")]
public sealed record WebSocketAudioSourceOptions
{
    /// <summary>Sample rate of the inbound PCM. Default 16 kHz (Sutando upstream microphone).</summary>
    public int SampleRate { get; init; } = 16_000;

    /// <summary>Channel count. Default mono.</summary>
    public int Channels { get; init; } = 1;

    /// <summary>Sample encoding. Default <see cref="AudioEncoding.Pcm16Le"/>.</summary>
    public AudioEncoding Encoding { get; init; } = AudioEncoding.Pcm16Le;

    /// <summary>Target chunk duration. Default 20 ms (320 samples @ 16 kHz mono).</summary>
    public TimeSpan ChunkDuration { get; init; } = TimeSpan.FromMilliseconds(20);
}

/// <summary>
/// Minimal byte-stream contract the source / sink stages use as their transport. Caller
/// implementations typically wrap a <see cref="System.Net.WebSockets.WebSocket"/> or a
/// <see cref="Channel{T}"/> for tests.
/// </summary>
[Experimental("SUTANDO001")]
public interface IAudioByteStream
{
    /// <summary>Read up to <paramref name="buffer"/>.Length bytes. Returns 0 on end-of-stream.</summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct);

    /// <summary>Write the given bytes to the transport.</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct);
}
