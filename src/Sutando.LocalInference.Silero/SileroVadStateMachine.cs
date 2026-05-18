using System.Diagnostics.CodeAnalysis;

namespace Sutando.LocalInference.Silero;

/// <summary>
/// Pure state machine that turns a stream of (timestamp, probability) samples into
/// <see cref="VadEvent"/> transitions according to the rules in <see cref="VadOptions"/>.
/// </summary>
/// <remarks>
/// <para>
/// Extracted as a separate type so it can be exercised in tests without the ONNX model:
/// callers feed in synthetic probability values and observe the emitted events. The public
/// <see cref="SileroVadDetector"/> wraps an instance of this around the ONNX-backed
/// probability function.
/// </para>
/// <para>
/// The transitions implemented:
/// <list type="bullet">
///   <item><description><see cref="VadEventKind.SpeechStart"/> fires when probability has stayed above <see cref="VadOptions.Threshold"/> for at least <see cref="VadOptions.MinSpeech"/>.</description></item>
///   <item><description><see cref="VadEventKind.SpeechEnd"/> fires when probability has stayed at or below the threshold for at least <see cref="VadOptions.SilenceHangover"/> (only after a SpeechStart).</description></item>
///   <item><description><see cref="VadEventKind.EnergyUpdate"/> fires whenever at least <see cref="VadOptions.EnergyUpdateInterval"/> has elapsed since the previous one.</description></item>
/// </list>
/// </para>
/// </remarks>
[Experimental("SUTANDO001")]
internal sealed class SileroVadStateMachine
{
    private readonly VadOptions _options;

    private bool _isSpeechActive;
    private DateTimeOffset? _candidateSpeechStart;
    private DateTimeOffset? _candidateSpeechEnd;
    private DateTimeOffset? _lastEnergyUpdate;

    public SileroVadStateMachine(VadOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>True once <see cref="VadEventKind.SpeechStart"/> has fired and SpeechEnd hasn't.</summary>
    public bool IsSpeechActive => _isSpeechActive;

    /// <summary>
    /// Feed one (probability, timestamp) sample. Returns zero or more <see cref="VadEvent"/>
    /// events that this sample caused.
    /// </summary>
    /// <param name="probability">Detector probability in <c>[0, 1]</c>.</param>
    /// <param name="at">The capture timestamp of the underlying audio frame.</param>
    /// <returns>Events to emit (often empty; energy updates are throttled by the configured interval).</returns>
    public IEnumerable<VadEvent> Push(float probability, DateTimeOffset at)
    {
        var events = new List<VadEvent>(2);

        var isAboveThreshold = probability >= _options.Threshold;

        if (!_isSpeechActive)
        {
            // Outside speech: looking for a sustained run above threshold long enough to trigger SpeechStart.
            if (isAboveThreshold)
            {
                _candidateSpeechStart ??= at;
                if (at - _candidateSpeechStart.Value >= _options.MinSpeech)
                {
                    _isSpeechActive = true;
                    _candidateSpeechEnd = null;
                    events.Add(VadEvent.SpeechStart(at, probability));
                }
            }
            else
            {
                // A dip below threshold during the candidate-speech window resets the timer —
                // a click or a cough shouldn't trigger a SpeechStart.
                _candidateSpeechStart = null;
            }
        }
        else
        {
            // Inside speech: looking for a sustained run below threshold long enough to trigger SpeechEnd.
            if (!isAboveThreshold)
            {
                _candidateSpeechEnd ??= at;
                if (at - _candidateSpeechEnd.Value >= _options.SilenceHangover)
                {
                    _isSpeechActive = false;
                    _candidateSpeechStart = null;
                    events.Add(VadEvent.SpeechEnd(at, probability));
                }
            }
            else
            {
                // Any sample back above threshold cancels the silence countdown.
                _candidateSpeechEnd = null;
            }
        }

        // Energy-update events are throttled by EnergyUpdateInterval; InfiniteTimeSpan disables them.
        if (_options.EnergyUpdateInterval != Timeout.InfiniteTimeSpan)
        {
            if (_lastEnergyUpdate is null || at - _lastEnergyUpdate.Value >= _options.EnergyUpdateInterval)
            {
                _lastEnergyUpdate = at;
                events.Add(VadEvent.EnergyUpdate(at, probability));
            }
        }

        return events;
    }

    /// <summary>
    /// Reset to the initial (no-speech) state — used when the audio stream is re-opened or
    /// the underlying ONNX session is recycled.
    /// </summary>
    public void Reset()
    {
        _isSpeechActive = false;
        _candidateSpeechStart = null;
        _candidateSpeechEnd = null;
        _lastEnergyUpdate = null;
    }
}
