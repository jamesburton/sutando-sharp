using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Sutando.LocalInference;

namespace Sutando.Pipeline.Stages;

/// <summary>
/// Pipeline stage that wraps an <see cref="IVadDetector"/>. Consumes <see cref="AudioInputFrame"/>s,
/// runs them through the detector, and emits a <see cref="VadFrame"/> for each detector event
/// (speech-start, speech-end, energy-update). Every other frame is forwarded downstream
/// unchanged (the "transparent composition" rule).
/// </summary>
/// <remarks>
/// <para>
/// The audio frames themselves are also forwarded after the VAD inspection so downstream
/// STT stages can still see them — this matches Pipecat's "tee" behaviour where VAD annotates
/// the stream rather than consuming it.
/// </para>
/// <para>
/// The detector is driven from a background unbounded channel: the stage funnels every
/// <see cref="AudioFrame"/> into the channel and reads <see cref="VadEvent"/>s back out. This
/// keeps the detector's <c>await foreach</c> over an <see cref="IAsyncEnumerable{AudioFrame}"/>
/// cleanly separated from the stage's frame-typed input / output streams.
/// </para>
/// </remarks>
[Experimental("SUTANDO001")]
public sealed class VadStage : IPipelineStage<PipelineFrame, PipelineFrame>
{
    private readonly IVadDetector _detector;
    private readonly VadOptions _options;

    /// <summary>Initialise the stage around a concrete VAD detector and detection options.</summary>
    /// <param name="detector">The detector implementation (e.g. <see cref="Sutando.LocalInference.Silero.SileroVadDetector"/>).</param>
    /// <param name="options">Detection thresholds. Pass <see langword="null"/> to use the defaults from <see cref="VadOptions"/>.</param>
    public VadStage(IVadDetector detector, VadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(detector);
        _detector = detector;
        _options = options ?? new VadOptions();
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<PipelineFrame> ProcessAsync(
        IAsyncEnumerable<PipelineFrame> source,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);

        // Audio feed to the detector. Unbounded so we never block the upstream producer from
        // inside our forwarder — the stage's own output is the place where backpressure bites.
        var audio = Channel.CreateUnbounded<AudioFrame>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        // VAD events the detector emits. Unbounded for the same reason.
        var events = Channel.CreateUnbounded<VadEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        var detectorTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var ev in _detector.AnalyzeAsync(ReadAllAudio(audio.Reader, ct), _options, ct).ConfigureAwait(false))
                {
                    await events.Writer.WriteAsync(ev, ct).ConfigureAwait(false);
                }
                events.Writer.TryComplete();
            }
            catch (OperationCanceledException)
            {
                events.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                events.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var frame in source.WithCancellation(ct).ConfigureAwait(false))
            {
                if (frame is AudioInputFrame audioIn)
                {
                    await audio.Writer.WriteAsync(audioIn.Audio, ct).ConfigureAwait(false);

                    // Drain any VAD events that have arrived since the last input frame. Using
                    // TryRead in a tight loop keeps latency low without blocking on the channel.
                    while (events.Reader.TryRead(out var ev))
                    {
                        yield return new VadFrame(ev);
                    }
                }

                // Forward the original frame so downstream stages still see audio + control signals.
                yield return frame;
            }
        }
        finally
        {
            audio.Writer.TryComplete();
            try { await detectorTask.ConfigureAwait(false); }
            catch { /* surfaced through the events channel; the await foreach below would have observed it. */ }
        }

        // Final drain — any events the detector produced after the input stream ended.
        while (await events.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (events.Reader.TryRead(out var ev))
            {
                yield return new VadFrame(ev);
            }
        }
    }

    private static async IAsyncEnumerable<AudioFrame> ReadAllAudio(ChannelReader<AudioFrame> reader, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var frame in reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return frame;
        }
    }
}
