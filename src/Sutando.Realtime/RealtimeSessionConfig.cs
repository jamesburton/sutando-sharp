namespace Sutando.Realtime;

/// <summary>
/// Per-session configuration for a Gemini Live (or compatible) realtime conversation.
/// </summary>
/// <remarks>
/// <para>
/// Credentials are NOT carried here — they live on the <c>IRealtimeClient</c>
/// (e.g. <c>GeminiLiveRealtimeClient</c>'s <c>ApiKey</c> ctor parameter), in line with MEAI's
/// client-owns-auth / session-owns-conversation split. The optional <see cref="ApiKey"/> field
/// is preserved purely as a back-compat carrier for <c>Sutando.Voice</c> /
/// <c>Sutando.Phone</c> consumers that historically supplied the key per-call: when present
/// and non-empty, the realtime adapter may use it to override the client's default key for
/// this session only. Greenfield callers should leave it null and configure the client.
/// </para>
/// <para>
/// The full set of Sutando-side fields maps onto MEAI's <c>RealtimeSessionOptions</c> at the
/// adapter boundary — see <c>MAPPING.md</c> in this project for the exact translation. Fields
/// without a MEAI peer (<see cref="ResumptionHandle"/>, the two transcription flags) survive
/// as Gemini-specific extensions and are read directly by <c>GeminiLiveRealtimeClientSession</c>.
/// </para>
/// </remarks>
/// <param name="Model">
/// Model id, e.g. <c>gemini-2.5-flash-live-preview</c> or <c>gemini-3.1-flash-live-preview</c>.
/// Passed verbatim into <c>BidiGenerateContentSetup.Model</c>.
/// </param>
/// <param name="ApiKey">
/// Optional override of the <c>IRealtimeClient</c>'s default API key. Null = use the
/// client's configured key. Kept as a field so existing call sites that constructed
/// <c>RealtimeSessionConfig</c> with an API key continue to compile; see the remarks for why
/// this is preserved.
/// </param>
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
    string? ApiKey = null,
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
