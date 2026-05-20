using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Sutando.Realtime;

/// <summary>
/// Translates MEAI <see cref="RealtimeServerMessage"/> instances (the wire-shape produced by an
/// <see cref="IRealtimeClientSession"/>) into Sutando's consumer-facing <see cref="RealtimeServerEvent"/>
/// discriminated union.
/// </summary>
/// <remarks>
/// <para>
/// This is the boundary where MEAI's open-string message type collapses back into a Sutando
/// sealed hierarchy. Keeping the union public-facing means <c>Sutando.Voice.VoiceWebSocketHandler</c>
/// and <c>Sutando.Phone.TwilioMediaSocketHandler</c> don't have to switch on a string struct.
/// See <c>MAPPING.md</c> for the full mapping table and the deviations from MEAI's contract.
/// </para>
/// <para>
/// Internal so unit tests can drive frames through it without standing up a real session.
/// </para>
/// </remarks>
internal static class MeaiToSutandoEventAdapter
{
    /// <summary>
    /// Translates a single MEAI server message into the equivalent Sutando event, or returns
    /// <see langword="null"/> when the message has no surfacing on the consumer side.
    /// </summary>
    /// <param name="message">The MEAI server message.</param>
    /// <param name="defaultAudioConfig">Default audio config — used to fill in sample-rate / channels / bit-depth when an audio delta lacks explicit format metadata.</param>
    /// <returns>The mapped Sutando event, or null when the message is non-surfacing.</returns>
    public static RealtimeServerEvent? Map(RealtimeServerMessage message, RealtimeAudioConfig defaultAudioConfig)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(defaultAudioConfig);

        var type = message.Type;

        // Sutando-custom types come first — they carry Gemini-specific semantics not present in
        // MEAI's starter type set.
        if (type == SutandoRealtimeMessageTypes.SessionStarted)
        {
            return new RealtimeSetupComplete();
        }
        if (type == SutandoRealtimeMessageTypes.GoAway)
        {
            return message.RawRepresentation as RealtimeGoAway
                ?? new RealtimeGoAway(ErrorMessage: null, ErrorCode: null, Reconnect: false, RetryAfter: null);
        }
        if (type == SutandoRealtimeMessageTypes.SessionResumptionUpdate)
        {
            return message.RawRepresentation as RealtimeSessionResumptionUpdate
                ?? new RealtimeSessionResumptionUpdate(string.Empty, false);
        }
        if (type == SutandoRealtimeMessageTypes.ToolCallCancelled)
        {
            return message.RawRepresentation as RealtimeToolCallCancellation
                ?? new RealtimeToolCallCancellation(Array.Empty<string>());
        }
        if (type == SutandoRealtimeMessageTypes.GroundingMetadata)
        {
            if (message.RawRepresentation is JsonElement json)
            {
                return new RealtimeGroundingMetadata(json);
            }
            return null;
        }
        if (type == SutandoRealtimeMessageTypes.SessionClosed)
        {
            var initiator = message.RawRepresentation is RealtimeCloseInitiator i
                ? i
                : RealtimeCloseInitiator.Unexpected;
            return new RealtimeTransportClosed(initiator);
        }

        // MEAI's well-known set follows. We hand off to the specific subclass on the basis of
        // Type alone — providers may emit the same body shape (OutputTextAudio*) for both audio
        // and transcript deltas, and we discriminate by the type code.
        if (type == RealtimeServerMessageType.OutputAudioDelta && message is OutputTextAudioRealtimeServerMessage audio)
        {
            if (string.IsNullOrEmpty(audio.Audio))
            {
                return null;
            }
            byte[] pcm;
            try
            {
                pcm = Convert.FromBase64String(audio.Audio);
            }
            catch (FormatException)
            {
                return null;
            }
            return new RealtimeAudioOutput(
                Pcm: pcm,
                SampleRateHz: defaultAudioConfig.OutputSampleRateHz,
                Channels: defaultAudioConfig.Channels,
                BitsPerSample: defaultAudioConfig.BitsPerSample);
        }

        if ((type == RealtimeServerMessageType.InputAudioTranscriptionDelta
                || type == RealtimeServerMessageType.InputAudioTranscriptionCompleted)
            && message is OutputTextAudioRealtimeServerMessage inputTranscript)
        {
            var finished = type == RealtimeServerMessageType.InputAudioTranscriptionCompleted;
            return new RealtimeInputTranscription(inputTranscript.Text ?? string.Empty, finished);
        }

        if ((type == RealtimeServerMessageType.OutputAudioTranscriptionDelta
                || type == RealtimeServerMessageType.OutputAudioTranscriptionDone)
            && message is OutputTextAudioRealtimeServerMessage outputTranscript)
        {
            var finished = type == RealtimeServerMessageType.OutputAudioTranscriptionDone;
            return new RealtimeOutputTranscription(outputTranscript.Text ?? string.Empty, finished);
        }

        if (type == RealtimeServerMessageType.ResponseDone && message is ResponseCreatedRealtimeServerMessage response)
        {
            // We model barge-in via the Cancelled status (matches MEAI's documented contract).
            // Normal turn completion → RealtimeTurnComplete.
            if (string.Equals(response.Status, RealtimeResponseStatus.Cancelled, StringComparison.OrdinalIgnoreCase))
            {
                return new RealtimeInterrupted();
            }
            return new RealtimeTurnComplete();
        }

        if (type == RealtimeServerMessageType.ResponseOutputItemDone && message is ResponseOutputItemRealtimeServerMessage outputItem)
        {
            if (outputItem.Item is { Contents.Count: > 0 } item)
            {
                var calls = new List<RealtimeFunctionCall>();
                foreach (var content in item.Contents)
                {
                    if (content is FunctionCallContent fc)
                    {
                        var argsJson = SerializeArguments(fc.Arguments);
                        calls.Add(new RealtimeFunctionCall(fc.CallId, fc.Name, argsJson));
                    }
                }
                if (calls.Count > 0)
                {
                    return new RealtimeToolCall(calls);
                }
            }
            return null;
        }

        if (type == RealtimeServerMessageType.Error && message is ErrorRealtimeServerMessage error)
        {
            return new RealtimeTransportError(error.Error?.Message ?? "Unknown realtime error.");
        }

        // ResponseCreated, ResponseOutputItemAdded, ConversationItemAdded / Done and friends are
        // lifecycle messages the middleware pipeline cares about but downstream consumers do not.
        // Drop silently — the caller's switch shouldn't get noisier than it needs to be.
        return null;
    }

    private static JsonElement SerializeArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return JsonDocument.Parse("{}").RootElement;
        }
        return JsonSerializer.SerializeToElement(arguments);
    }
}
