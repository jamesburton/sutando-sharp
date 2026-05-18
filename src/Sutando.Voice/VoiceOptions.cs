namespace Sutando.Voice;

/// <summary>
/// Runtime configuration for the voice WS server. Populated from <c>appsettings</c>, environment
/// variables (<c>SUTANDO_VOICE_PORT</c>, <c>GEMINI_VOICE_API_KEY</c>, <c>GEMINI_API_KEY</c>) and
/// command-line args (<c>--port</c>). The host's resolver applies the precedence
/// <b>CLI args &gt; environment &gt; configuration</b>.
/// </summary>
public sealed class VoiceOptions
{
    /// <summary>The <c>IConfiguration</c> binding key. Maps to a top-level <c>Voice:*</c> section.</summary>
    public const string SectionName = "Voice";

    /// <summary>TCP port the WS server listens on. Default <c>9900</c> mirrors upstream sutando.</summary>
    public int Port { get; set; } = 9900;

    /// <summary>
    /// Gemini API key. <see cref="VoiceServer"/> reads <c>GEMINI_VOICE_API_KEY</c> first, then falls
    /// back to <c>GEMINI_API_KEY</c>; either value lands here. Empty when no key is set — the WS
    /// handler then refuses the upgrade with HTTP 503 instead of attempting a doomed Gemini connect.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>The Gemini model id used for each session. Mirrors upstream's default voice model.</summary>
    public string Model { get; set; } = "gemini-2.5-flash-live-preview";

    /// <summary>Prebuilt voice id. <c>Puck</c> matches upstream's default.</summary>
    public string VoiceName { get; set; } = "Puck";

    /// <summary>Optional system prompt for every session. Null disables.</summary>
    public string? SystemInstruction { get; set; }
}
