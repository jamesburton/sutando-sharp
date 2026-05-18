namespace Sutando.Realtime;

/// <summary>
/// A single piece of input sent from the client to the model — text or PCM audio.
/// </summary>
/// <remarks>
/// Mirrors <c>BidiGenerateContentRealtimeInput</c>: exactly one variant is meaningful per
/// envelope. Constructed via <see cref="Text(string)"/> or <see cref="Audio(ReadOnlyMemory{byte}, string?)"/>.
/// </remarks>
public abstract record RealtimeInput
{
    private RealtimeInput()
    {
    }

    /// <summary>Builds a text input.</summary>
    /// <param name="text">The text content sent to the model as a real-time turn.</param>
    public static RealtimeInput Text(string text) => new RealtimeTextInput(text);

    /// <summary>Builds an audio chunk input.</summary>
    /// <param name="pcm">Raw 16-bit little-endian PCM audio at the session's input sample rate (16 kHz for Gemini).</param>
    /// <param name="mimeType">
    /// IANA MIME type. Defaults to <c>audio/pcm;rate=16000</c> when null. Override when sending pre-resampled audio.
    /// </param>
    public static RealtimeInput Audio(ReadOnlyMemory<byte> pcm, string? mimeType = null) => new RealtimeAudioInput(pcm, mimeType);

    /// <summary>A text input variant.</summary>
    /// <param name="Value">The text content.</param>
    public sealed record RealtimeTextInput(string Value) : RealtimeInput;

    /// <summary>A raw PCM audio input variant.</summary>
    /// <param name="Pcm">Raw 16-bit little-endian PCM samples.</param>
    /// <param name="MimeType">IANA MIME type. Null falls back to the session's input MIME.</param>
    public sealed record RealtimeAudioInput(ReadOnlyMemory<byte> Pcm, string? MimeType) : RealtimeInput;
}

/// <summary>
/// A response to a previously-received tool call. Sent back to the model so it can continue generating.
/// </summary>
/// <param name="ToolCallId">The id assigned to the tool call by the model. Required for matching.</param>
/// <param name="Name">Function name. Must match the original call.</param>
/// <param name="Response">JSON object containing the function's return value.</param>
public sealed record ToolResponse(string ToolCallId, string Name, System.Text.Json.JsonElement Response);
