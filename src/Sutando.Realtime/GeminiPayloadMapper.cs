using System.Text.Json;
using GenerativeAI.Types;
using Microsoft.Extensions.AI;

namespace Sutando.Realtime;

/// <summary>
/// Maps SDK message payloads to both MEAI's <see cref="RealtimeServerMessage"/> hierarchy and
/// (for backwards-compatible tests) Sutando's <see cref="RealtimeServerEvent"/> hierarchy.
/// </summary>
/// <remarks>
/// <para>
/// Two faces of the same coin: <see cref="MapToMeai"/> is what
/// <see cref="GeminiLiveRealtimeClientSession"/> uses on the inbound path, producing the
/// MEAI-shaped messages that flow through the session's
/// <see cref="IRealtimeClientSession.GetStreamingResponseAsync"/> stream. <see cref="Map"/>
/// is the original Sutando-shaped projection — preserved here so the existing payload-shape
/// tests (and any future bypass paths) keep working.
/// </para>
/// <para>
/// Internal so unit tests can feed deserialised wire frames in without standing up a real
/// WebSocket. Production callers go through <see cref="IRealtimeClient"/> /
/// <see cref="IRealtimeClientSession"/>.
/// </para>
/// </remarks>
internal static class GeminiPayloadMapper
{
    /// <summary>
    /// Maps a <see cref="BidiResponsePayload"/> into zero or more <see cref="RealtimeServerEvent"/>s.
    /// Original Sutando-shape projection, retained for test coverage of the wire-protocol contract.
    /// </summary>
    /// <param name="payload">The deserialised wire envelope.</param>
    /// <returns>Events to emit, in wire order. May be empty if the payload contained no recognised fields.</returns>
    public static IEnumerable<RealtimeServerEvent> Map(BidiResponsePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (payload.SetupComplete is not null)
        {
            yield return new RealtimeSetupComplete();
        }

        if (payload.ServerContent is { } serverContent)
        {
            if (serverContent.Interrupted == true)
            {
                yield return new RealtimeInterrupted();
            }
            if (serverContent.TurnComplete == true)
            {
                yield return new RealtimeTurnComplete();
            }
            if (serverContent.GroundingMetadata is { } grounding)
            {
                yield return new RealtimeGroundingMetadata(JsonSerializer.SerializeToElement(grounding));
            }
        }

        if (payload.ToolCall is { } toolCall && toolCall.FunctionCalls is { Length: > 0 } fcs)
        {
            var calls = new List<RealtimeFunctionCall>(fcs.Length);
            foreach (var call in fcs)
            {
                var args = call.Args is null
                    ? JsonDocument.Parse("{}").RootElement
                    : JsonSerializer.SerializeToElement(call.Args);
                calls.Add(new RealtimeFunctionCall(call.Id ?? string.Empty, call.Name ?? string.Empty, args));
            }
            yield return new RealtimeToolCall(calls);
        }

        if (payload.ToolCallCancellation is { } cancel && cancel.Ids is { Length: > 0 } ids)
        {
            yield return new RealtimeToolCallCancellation(ids);
        }

        if (payload.GoAway is { } goAway)
        {
            yield return new RealtimeGoAway(
                ErrorMessage: goAway.ErrorMessage,
                ErrorCode: goAway.ErrorCode,
                Reconnect: goAway.Reconnect ?? false,
                RetryAfter: goAway.RetryAfterSeconds is int secs ? TimeSpan.FromSeconds(secs) : null);
        }

        if (payload.SessionResumptionUpdate is { } update)
        {
            var resumable = update.Status is not SessionResumptionStatus.FAILED;
            yield return new RealtimeSessionResumptionUpdate(update.ResumptionToken ?? string.Empty, resumable);
        }
    }

    /// <summary>
    /// Maps a <see cref="BidiResponsePayload"/> into zero or more MEAI <see cref="RealtimeServerMessage"/>s.
    /// This is the inbound projection used by <see cref="GeminiLiveRealtimeClientSession"/>.
    /// </summary>
    /// <param name="payload">The deserialised wire envelope.</param>
    /// <returns>MEAI server messages to emit, in wire order. May be empty.</returns>
    public static IEnumerable<RealtimeServerMessage> MapToMeai(BidiResponsePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (payload.SetupComplete is not null)
        {
            yield return new RealtimeServerMessage { Type = SutandoRealtimeMessageTypes.SessionStarted };
        }

        if (payload.ServerContent is { } serverContent)
        {
            if (serverContent.Interrupted == true)
            {
                yield return new ResponseCreatedRealtimeServerMessage(RealtimeServerMessageType.ResponseDone)
                {
                    Status = RealtimeResponseStatus.Cancelled,
                };
            }
            if (serverContent.TurnComplete == true)
            {
                yield return new ResponseCreatedRealtimeServerMessage(RealtimeServerMessageType.ResponseDone)
                {
                    Status = RealtimeResponseStatus.Completed,
                };
            }
            if (serverContent.GroundingMetadata is { } grounding)
            {
                yield return new RealtimeServerMessage
                {
                    Type = SutandoRealtimeMessageTypes.GroundingMetadata,
                    RawRepresentation = JsonSerializer.SerializeToElement(grounding),
                };
            }
        }

        if (payload.ToolCall is { } toolCall && toolCall.FunctionCalls is { Length: > 0 } fcs)
        {
            var contents = new List<AIContent>(fcs.Length);
            foreach (var call in fcs)
            {
                IDictionary<string, object?>? args = null;
                if (call.Args is not null)
                {
                    args = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.SerializeToElement(call.Args).GetRawText());
                }
                contents.Add(new FunctionCallContent(call.Id ?? string.Empty, call.Name ?? string.Empty, args));
            }
            yield return new ResponseOutputItemRealtimeServerMessage(RealtimeServerMessageType.ResponseOutputItemDone)
            {
                Item = new RealtimeConversationItem(contents),
            };
        }

        if (payload.ToolCallCancellation is { } cancel && cancel.Ids is { Length: > 0 } ids)
        {
            yield return new RealtimeServerMessage
            {
                Type = SutandoRealtimeMessageTypes.ToolCallCancelled,
                RawRepresentation = new RealtimeToolCallCancellation(ids),
            };
        }

        if (payload.GoAway is { } goAway)
        {
            yield return new RealtimeServerMessage
            {
                Type = SutandoRealtimeMessageTypes.GoAway,
                RawRepresentation = new RealtimeGoAway(
                    ErrorMessage: goAway.ErrorMessage,
                    ErrorCode: goAway.ErrorCode,
                    Reconnect: goAway.Reconnect ?? false,
                    RetryAfter: goAway.RetryAfterSeconds is int secs ? TimeSpan.FromSeconds(secs) : null),
            };
        }

        if (payload.SessionResumptionUpdate is { } update)
        {
            var resumable = update.Status is not SessionResumptionStatus.FAILED;
            yield return new RealtimeServerMessage
            {
                Type = SutandoRealtimeMessageTypes.SessionResumptionUpdate,
                RawRepresentation = new RealtimeSessionResumptionUpdate(update.ResumptionToken ?? string.Empty, resumable),
            };
        }
    }
}
