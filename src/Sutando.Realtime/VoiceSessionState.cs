namespace Sutando.Realtime;

/// <summary>
/// Lifecycle states for a <see cref="VoiceSession"/>.
/// </summary>
/// <remarks>
/// Mirrors bodhi's <c>SessionManager</c> state machine — <c>IDLE → CONNECTING → CONNECTED</c> plus
/// the runtime-flavoured sub-states <c>Listening</c> / <c>Speaking</c> that bodhi tracks via
/// <c>InteractionModeManager</c>. We collapse bodhi's <c>CONNECTED</c> into <c>Listening</c>
/// directly — the moment the server acknowledges setup we're listening for user input.
/// <c>Failed</c> is terminal; consumers must allocate a new session (and a new transport) to recover.
/// </remarks>
public enum VoiceSessionState
{
    /// <summary>Initial state. Nothing started.</summary>
    Idle = 0,

    /// <summary>The WebSocket handshake / setup envelope is in flight.</summary>
    Connecting = 1,

    /// <summary>The session is ready and the model is awaiting user input (no audio out in flight).</summary>
    Listening = 2,

    /// <summary>The session is ready and the model is currently emitting audio output.</summary>
    Speaking = 3,

    /// <summary>The transport has cleanly disconnected. Allocate a new session to reconnect.</summary>
    Disconnected = 4,

    /// <summary>An error left the session non-recoverable.</summary>
    Failed = 5,
}

/// <summary>
/// Notification raised whenever <see cref="VoiceSession.State"/> changes.
/// </summary>
/// <param name="Previous">The state we transitioned out of.</param>
/// <param name="Current">The new state.</param>
public sealed record VoiceSessionStateChange(VoiceSessionState Previous, VoiceSessionState Current);
