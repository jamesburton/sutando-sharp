namespace Sutando.Realtime;

/// <summary>
/// Single-shot async-initialisation gate. Serialises concurrent first-callers against the
/// initialiser's completion: every caller waits inside the gate until the initialiser has
/// finished, then sees the gate as "completed" and returns. Subsequent callers fast-path through
/// after the gate has flipped to completed.
/// </summary>
/// <remarks>
/// <para>
/// Designed to fix a race in <see cref="GeminiLiveRealtimeClientSession.EnsureConnectedAsync"/>:
/// the previous implementation set <c>_connectInitiated = true</c> before <c>await
/// client.ConnectAsync(...)</c> completed, so a concurrent caller would short-circuit on the
/// flag, see <c>_client</c> already assigned, and call <c>client.SendXxxAsync</c> while the
/// underlying WebSocket was still mid-handshake — surfacing as
/// "The WebSocket client is not connected." from the GenAI SDK.
/// </para>
/// <para>
/// This helper enforces the correct semantics by construction: the gate doesn't flip to
/// "completed" until <c>initialise</c> has fully returned. There is no fast-path that races
/// the initialiser.
/// </para>
/// </remarks>
internal sealed class AsyncOnceGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private volatile bool _completed;

    /// <summary>True once <see cref="EnsureAsync"/>'s initialiser has fully completed.</summary>
    public bool IsCompleted => _completed;

    /// <summary>
    /// Run <paramref name="initialise"/> exactly once. The first caller drives the initialisation;
    /// every other caller blocks on the semaphore until the first finishes (success or throw),
    /// then re-checks the completed flag.
    /// </summary>
    /// <param name="initialise">Idempotent initialiser. May throw — in which case the gate stays open and the next caller retries.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task EnsureAsync(Func<CancellationToken, Task> initialise, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(initialise);

        // No outer fast-path. Always take the semaphore so concurrent first-callers serialise
        // against the initialiser's completion. After the first success, the semaphore is
        // uncontended (every later caller takes-and-releases in microseconds), so the saved
        // syscall isn't worth the race window the fast-path opens.
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_completed)
            {
                return;
            }
            await initialise(ct).ConfigureAwait(false);
            _completed = true;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _semaphore.Dispose();
}
