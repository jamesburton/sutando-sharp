namespace Sutando.Realtime;

/// <summary>
/// Configuration for a single Gemini Live session.
/// </summary>
/// <param name="Model">
/// Model id, e.g. <c>gemini-2.5-flash-live-preview</c> or <c>gemini-3.1-flash-live-preview</c>.
/// Passed verbatim into <c>BidiGenerateContentSetup.Model</c>.
/// </param>
/// <param name="ApiKey">Google AI Studio API key. Mandatory — there is no token-auth fallback here.</param>
/// <param name="VoiceName">
/// Prebuilt voice id (e.g. <c>Puck</c>, <c>Charon</c>, <c>Kore</c>). Null falls back to the
/// model default. Bodhi defaults to <c>Puck</c>; we keep the same default for parity.
/// </param>
/// <param name="SystemInstruction">
/// Optional system prompt. Passed as a single text part on <c>BidiGenerateContentSetup.SystemInstruction</c>.
/// </param>
/// <param name="Tools">
/// Tool definitions the model may call. Empty list = no tools. The transport converts these
/// into <c>functionDeclarations</c> at setup time.
/// </param>
/// <param name="Audio">Audio format. Almost always <see cref="RealtimeAudioConfig.Default"/>.</param>
/// <param name="EnableInputTranscription">
/// When true, the server emits transcriptions of audio sent by the user. Default true to match bodhi.
/// </param>
/// <param name="EnableOutputTranscription">
/// When true, the server emits transcriptions of audio it generates. Default true to match bodhi.
/// </param>
/// <param name="ResumptionHandle">
/// Optional handle from a prior <c>SessionResumptionUpdate</c> event. When set, the server attempts
/// to resume the prior session state. Used by <see cref="VoiceSession"/> on reconnect.
/// </param>
public sealed record RealtimeSessionConfig(
    string Model,
    string ApiKey,
    string? VoiceName = "Puck",
    string? SystemInstruction = null,
    IReadOnlyList<RealtimeToolDefinition>? Tools = null,
    RealtimeAudioConfig? Audio = null,
    bool EnableInputTranscription = true,
    bool EnableOutputTranscription = true,
    string? ResumptionHandle = null)
{
    /// <summary>The audio configuration in effect — falls back to <see cref="RealtimeAudioConfig.Default"/>.</summary>
    public RealtimeAudioConfig EffectiveAudio => Audio ?? RealtimeAudioConfig.Default;

    /// <summary>The tool list in effect — empty when none were registered.</summary>
    public IReadOnlyList<RealtimeToolDefinition> EffectiveTools => Tools ?? Array.Empty<RealtimeToolDefinition>();
}
