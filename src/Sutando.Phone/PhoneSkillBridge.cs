using Sutando.Realtime;

namespace Sutando.Phone;

/// <summary>
/// Realtime tool-bridge shape consumed by <see cref="TwilioMediaSocketHandler"/>. Decouples
/// <c>Sutando.Phone</c> from <c>Sutando.Skills</c> — the actual bridge implementation lives in
/// <c>Sutando.Voice.Skills</c> (<c>SkillRegistryVoiceBridge</c>) and the integrator wraps it into
/// this record so <c>Sutando.Phone</c> never takes a transitive dependency on <c>Sutando.Skills</c>.
/// </summary>
/// <remarks>
/// Mirrors the upstream pattern where <c>conversation-server.ts:587</c> pushes the same
/// <c>inlineTools</c> list into the phone session's tool table that the voice agent uses. By
/// passing the same registry through both the voice <c>SkillRegistryVoiceBridge</c> and this
/// adapter, callers get parity on both surfaces from a single source of truth.
/// </remarks>
/// <param name="Tools">Tool definitions to advertise on the session config.</param>
/// <param name="RegisterWithSession">
/// Callback invoked once per call after the <see cref="VoiceSession"/> is constructed and before
/// <see cref="VoiceSession.ConnectAsync"/>. Registers each tool's handler against the session so
/// the realtime client can dispatch inbound tool calls.
/// </param>
public sealed record PhoneSkillBridge(
    IReadOnlyList<RealtimeToolDefinition> Tools,
    Action<VoiceSession> RegisterWithSession);
