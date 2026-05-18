using System.Text.Json;
using Sutando.Realtime;
using Sutando.Voice;

namespace Sutando.Tests.Voice;

/// <summary>
/// Pure unit tests over <see cref="VoiceWebSocketHandler.MapEvent"/>. Asserts that every supported
/// <see cref="RealtimeServerEvent"/> projects to the documented envelope, and that the not-surfaced
/// subtypes return <see langword="null"/>.
/// </summary>
public sealed class VoiceEventMappingTests
{
    private static string Serialize(object envelope) =>
        JsonSerializer.Serialize(envelope, VoiceJsonOptionsAccessor.Options);

    [Fact]
    public void SetupComplete_maps_to_setup_complete_envelope()
    {
        var msg = VoiceWebSocketHandler.MapEvent(new RealtimeSetupComplete());
        Assert.NotNull(msg);
        var json = Serialize(msg!);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("setup_complete", doc.RootElement.GetProperty("type").GetString());
        Assert.False(doc.RootElement.TryGetProperty("data", out _));
    }

    [Fact]
    public void AudioOutput_maps_to_base64_data()
    {
        var pcm = new byte[] { 1, 2, 3, 4 };
        var msg = VoiceWebSocketHandler.MapEvent(new RealtimeAudioOutput(pcm, 24_000, 1, 16));
        Assert.NotNull(msg);
        var json = Serialize(msg!);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("audio", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(Convert.ToBase64String(pcm), doc.RootElement.GetProperty("data").GetString());
    }

    [Fact]
    public void InputTranscription_maps_to_text_field()
    {
        var msg = VoiceWebSocketHandler.MapEvent(new RealtimeInputTranscription("hello", Finished: false));
        Assert.NotNull(msg);
        var json = Serialize(msg!);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("input_transcription", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("hello", doc.RootElement.GetProperty("text").GetString());
        // The spec envelope does not carry the Finished flag — verify it does NOT leak.
        Assert.False(doc.RootElement.TryGetProperty("finished", out _));
    }

    [Fact]
    public void OutputTranscription_maps_to_text_field()
    {
        var msg = VoiceWebSocketHandler.MapEvent(new RealtimeOutputTranscription("from model", Finished: true));
        Assert.NotNull(msg);
        var json = Serialize(msg!);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("output_transcription", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("from model", doc.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public void Interrupted_and_TurnComplete_map_to_marker_envelopes()
    {
        var i = VoiceWebSocketHandler.MapEvent(new RealtimeInterrupted());
        var t = VoiceWebSocketHandler.MapEvent(new RealtimeTurnComplete());

        Assert.NotNull(i);
        Assert.NotNull(t);
        Assert.Equal("interrupted", JsonDocument.Parse(Serialize(i!)).RootElement.GetProperty("type").GetString());
        Assert.Equal("turn_complete", JsonDocument.Parse(Serialize(t!)).RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public void GoAway_with_RetryAfter_emits_time_left_ms()
    {
        var msg = VoiceWebSocketHandler.MapEvent(new RealtimeGoAway(
            ErrorMessage: "quota nearly exhausted",
            ErrorCode: null,
            Reconnect: true,
            RetryAfter: TimeSpan.FromMilliseconds(2500)));
        Assert.NotNull(msg);
        using var doc = JsonDocument.Parse(Serialize(msg!));
        Assert.Equal("go_away", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(2500, doc.RootElement.GetProperty("time_left_ms").GetInt32());
        Assert.Equal("quota nearly exhausted", doc.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public void TransportError_maps_to_error_envelope()
    {
        var msg = VoiceWebSocketHandler.MapEvent(new RealtimeTransportError("boom"));
        Assert.NotNull(msg);
        using var doc = JsonDocument.Parse(Serialize(msg!));
        Assert.Equal("error", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("boom", doc.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public void Unmapped_events_return_null()
    {
        Assert.Null(VoiceWebSocketHandler.MapEvent(new RealtimeTransportClosed(RealtimeCloseInitiator.Server)));
        Assert.Null(VoiceWebSocketHandler.MapEvent(new RealtimeSessionResumptionUpdate("h", true)));
    }
}

/// <summary>
/// Reaches into <c>Sutando.Voice</c> for the shared options. Both types are internal but visible
/// through the <c>InternalsVisibleTo</c> grant.
/// </summary>
internal static class VoiceJsonOptionsAccessor
{
    public static JsonSerializerOptions Options => VoiceJson.Options;
}
