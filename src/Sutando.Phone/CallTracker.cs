namespace Sutando.Phone;

/// <summary>
/// Thread-safe counter of currently-active phone calls. Surfaced through <c>/healthz</c>
/// and used by tests to assert that WebSocket close paths clean up the per-call
/// <see cref="Sutando.Realtime.VoiceSession"/>.
/// </summary>
/// <remarks>
/// Mirrors the voice-side <c>VoiceSessionTracker</c> in <c>Sutando.Voice</c> — kept as a
/// separate type so the healthz payload shape (which the phone bridge reports as
/// <c>active_calls</c>) stays distinct from the voice WS payload (which reports
/// <c>sessions</c>). The Phone project does not reference Sutando.Voice; we duplicate the
/// pattern to keep the dependency graph DAG-shaped.
/// </remarks>
public sealed class CallTracker
{
    private long _count;

    /// <summary>Current number of active calls.</summary>
    public int Count => (int)Interlocked.Read(ref _count);

    /// <summary>Increments the counter — called as soon as a Media Streams handshake completes.</summary>
    /// <returns>The new count after the increment.</returns>
    public int Increment() => (int)Interlocked.Increment(ref _count);

    /// <summary>Decrements the counter — called from the WS handler's <c>finally</c> block.</summary>
    /// <returns>The new count after the decrement.</returns>
    public int Decrement() => (int)Interlocked.Decrement(ref _count);
}
