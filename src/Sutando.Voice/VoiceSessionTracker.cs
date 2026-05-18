namespace Sutando.Voice;

/// <summary>
/// Thread-safe counter of currently-active voice sessions. Surfaced through <c>/healthz</c> and used
/// by tests to assert that <c>WebSocket</c> close paths clean up the per-connection
/// <see cref="Sutando.Realtime.VoiceSession"/>.
/// </summary>
public sealed class VoiceSessionTracker
{
    private long _count;

    /// <summary>Current number of active sessions.</summary>
    public int Count => (int)Interlocked.Read(ref _count);

    /// <summary>Increments the counter — called as soon as a WS handshake completes.</summary>
    /// <returns>The new count after the increment.</returns>
    public int Increment() => (int)Interlocked.Increment(ref _count);

    /// <summary>Decrements the counter — called from the WS handler's <c>finally</c> block.</summary>
    /// <returns>The new count after the decrement.</returns>
    public int Decrement() => (int)Interlocked.Decrement(ref _count);
}
