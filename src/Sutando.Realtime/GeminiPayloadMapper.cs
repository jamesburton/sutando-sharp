using System.Text.Json;
using GenerativeAI.Types;

namespace Sutando.Realtime;

/// <summary>
/// Maps SDK message payloads to our provider-agnostic <see cref="RealtimeServerEvent"/> hierarchy.
/// </summary>
/// <remarks>
/// Extracted from <see cref="GeminiLiveTransport"/> so unit tests can feed known JSON frames in
/// without standing up a real WebSocket. The SDK's <see cref="BidiResponsePayload"/> is the
/// deserialised wire envelope, so a test that deserializes a known JSON string into this type and
/// then maps it covers the full payload-shape contract.
/// </remarks>
internal static class GeminiPayloadMapper
{
    /// <summary>
    /// Maps a <see cref="BidiResponsePayload"/> into zero or more <see cref="RealtimeServerEvent"/>s.
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
}
