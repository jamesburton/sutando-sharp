using System.Text.Json;
using GenerativeAI.Types;
using Sutando.Realtime;

namespace Sutando.Tests.Realtime;

/// <summary>
/// Feeds known JSON envelopes through the SDK's deserializer and verifies they map to the right
/// <see cref="RealtimeServerEvent"/> shapes. Covers the wire-protocol contract for setup-complete,
/// server-content (turn-complete / interrupted / grounding), tool-call, tool-call-cancellation,
/// go-away, and session-resumption-update — i.e. every server-emitted message bodhi handles.
/// </summary>
public sealed class GeminiPayloadMapperTests
{
    private static BidiResponsePayload Deserialize(string json)
        => JsonSerializer.Deserialize<BidiResponsePayload>(json)
           ?? throw new InvalidOperationException("Deserialization returned null.");

    [Fact]
    public void SetupComplete_frame_maps_to_RealtimeSetupComplete()
    {
        var payload = Deserialize("""{"setupComplete":{}}""");

        var events = GeminiPayloadMapper.Map(payload).ToList();

        var evt = Assert.Single(events);
        Assert.IsType<RealtimeSetupComplete>(evt);
    }

    [Fact]
    public void ServerContent_turnComplete_maps_to_RealtimeTurnComplete()
    {
        var payload = Deserialize("""{"serverContent":{"turnComplete":true}}""");

        var events = GeminiPayloadMapper.Map(payload).ToList();

        var evt = Assert.Single(events);
        Assert.IsType<RealtimeTurnComplete>(evt);
    }

    [Fact]
    public void ServerContent_interrupted_maps_to_RealtimeInterrupted()
    {
        var payload = Deserialize("""{"serverContent":{"interrupted":true}}""");

        var events = GeminiPayloadMapper.Map(payload).ToList();

        var evt = Assert.Single(events);
        Assert.IsType<RealtimeInterrupted>(evt);
    }

    [Fact]
    public void ServerContent_groundingMetadata_maps_to_RealtimeGroundingMetadata()
    {
        var payload = Deserialize(
            """{"serverContent":{"groundingMetadata":{"groundingChunks":[{"web":{"uri":"https://example.com"}}]}}}""");

        var events = GeminiPayloadMapper.Map(payload).ToList();

        var grounding = Assert.IsType<RealtimeGroundingMetadata>(Assert.Single(events));
        Assert.Equal(JsonValueKind.Object, grounding.MetadataJson.ValueKind);
    }

    [Fact]
    public void ServerContent_with_turnComplete_and_interrupted_emits_both_in_order()
    {
        var payload = Deserialize("""{"serverContent":{"interrupted":true,"turnComplete":true}}""");

        var events = GeminiPayloadMapper.Map(payload).ToList();

        Assert.Equal(2, events.Count);
        Assert.IsType<RealtimeInterrupted>(events[0]);
        Assert.IsType<RealtimeTurnComplete>(events[1]);
    }

    [Fact]
    public void ToolCall_maps_to_RealtimeToolCall_with_id_name_and_args()
    {
        // Two-call batch exercises the list-flattening path.
        var payload = Deserialize(
            """{"toolCall":{"functionCalls":[{"id":"abc","name":"get_weather","args":{"city":"London"}},{"id":"def","name":"now","args":{}}]}}""");

        var events = GeminiPayloadMapper.Map(payload).ToList();

        var call = Assert.IsType<RealtimeToolCall>(Assert.Single(events));
        Assert.Equal(2, call.Calls.Count);
        Assert.Equal("abc", call.Calls[0].Id);
        Assert.Equal("get_weather", call.Calls[0].Name);
        Assert.Equal("London", call.Calls[0].Arguments.GetProperty("city").GetString());
        Assert.Equal("def", call.Calls[1].Id);
        Assert.Equal("now", call.Calls[1].Name);
    }

    [Fact]
    public void ToolCallCancellation_maps_to_RealtimeToolCallCancellation_with_id_list()
    {
        var payload = Deserialize("""{"toolCallCancellation":{"ids":["abc","def","ghi"]}}""");

        var events = GeminiPayloadMapper.Map(payload).ToList();

        var cancel = Assert.IsType<RealtimeToolCallCancellation>(Assert.Single(events));
        Assert.Equal(new[] { "abc", "def", "ghi" }, cancel.ToolCallIds);
    }

    [Fact]
    public void GoAway_maps_to_RealtimeGoAway_preserving_reconnect_and_retryAfter()
    {
        var payload = Deserialize(
            """{"goAway":{"errorMessage":"server restarting","errorCode":13,"reconnect":true,"retryAfterSeconds":3}}""");

        var events = GeminiPayloadMapper.Map(payload).ToList();

        var goAway = Assert.IsType<RealtimeGoAway>(Assert.Single(events));
        Assert.Equal("server restarting", goAway.ErrorMessage);
        Assert.Equal(13, goAway.ErrorCode);
        Assert.True(goAway.Reconnect);
        Assert.Equal(TimeSpan.FromSeconds(3), goAway.RetryAfter);
    }

    [Fact]
    public void SessionResumptionUpdate_with_success_status_is_resumable()
    {
        var payload = Deserialize(
            """{"sessionResumptionUpdate":{"resumptionToken":"handle-xyz","status":"SUCCESS"}}""");

        var events = GeminiPayloadMapper.Map(payload).ToList();

        var update = Assert.IsType<RealtimeSessionResumptionUpdate>(Assert.Single(events));
        Assert.Equal("handle-xyz", update.Handle);
        Assert.True(update.Resumable);
    }

    [Fact]
    public void SessionResumptionUpdate_with_failed_status_is_not_resumable()
    {
        var payload = Deserialize(
            """{"sessionResumptionUpdate":{"resumptionToken":"","status":"FAILED"}}""");

        var events = GeminiPayloadMapper.Map(payload).ToList();

        var update = Assert.IsType<RealtimeSessionResumptionUpdate>(Assert.Single(events));
        Assert.False(update.Resumable);
    }

    [Fact]
    public void Map_throws_on_null_payload()
    {
        Assert.Throws<ArgumentNullException>(() => GeminiPayloadMapper.Map(null!).ToList());
    }
}
