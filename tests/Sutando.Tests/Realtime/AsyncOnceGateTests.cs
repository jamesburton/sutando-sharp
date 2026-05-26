using Sutando.Realtime;

namespace Sutando.Tests.Realtime;

/// <summary>
/// Tests for <see cref="AsyncOnceGate"/>. The gate fixes a real race observed in
/// <c>GeminiLiveRealtimeClientSession.EnsureConnectedAsync</c>: a concurrent caller used to
/// short-circuit on a "_connectInitiated = true" flag that was set BEFORE the WebSocket
/// handshake awaited, then drove into <c>client.SendXxxAsync</c> while the underlying WS was
/// mid-handshake — surfacing as "The WebSocket client is not connected." from the GenAI SDK.
/// </summary>
public sealed class AsyncOnceGateTests
{
    [Fact]
    public async Task EnsureAsync_runs_initializer_once_for_single_caller()
    {
        using var gate = new AsyncOnceGate();
        var runs = 0;

        await gate.EnsureAsync(_ => { runs++; return Task.CompletedTask; }, default);
        await gate.EnsureAsync(_ => { runs++; return Task.CompletedTask; }, default);
        await gate.EnsureAsync(_ => { runs++; return Task.CompletedTask; }, default);

        Assert.Equal(1, runs);
        Assert.True(gate.IsCompleted);
    }

    [Fact]
    public async Task EnsureAsync_concurrent_callers_serialize_until_initializer_completes()
    {
        // This is the contract the GeminiLive race violated. Caller A starts the initializer and
        // parks on an external signal. Caller B comes in while A is mid-init. B MUST NOT see
        // IsCompleted=true or return from EnsureAsync until A's initializer fully completes.
        using var gate = new AsyncOnceGate();
        var initEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initRuns = 0;

        async Task Initialize(CancellationToken ct)
        {
            Interlocked.Increment(ref initRuns);
            initEntered.SetResult();
            await initRelease.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        var a = Task.Run(() => gate.EnsureAsync(Initialize, default));
        await initEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // While A is parked inside the initializer:
        //  - the gate must NOT yet report completed
        //  - a concurrent EnsureAsync from B must NOT return early
        Assert.False(gate.IsCompleted, "Gate flipped to completed before initializer returned.");

        var b = Task.Run(() => gate.EnsureAsync(Initialize, default));

        // Give B a generous window to incorrectly complete.
        await Task.Delay(150);
        Assert.False(b.IsCompleted, "Caller B returned before initializer completed — race regression.");
        Assert.False(gate.IsCompleted, "Gate flipped to completed while initializer was still parked.");

        // Release A; both callers must finish, and the initializer must have run exactly once.
        initRelease.SetResult();
        await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(gate.IsCompleted);
        Assert.Equal(1, initRuns);
    }

    [Fact]
    public async Task EnsureAsync_failed_initializer_does_not_flip_gate_and_next_caller_retries()
    {
        using var gate = new AsyncOnceGate();
        var attempt = 0;

        async Task FailingInitialize(CancellationToken ct)
        {
            attempt++;
            await Task.Yield();
            throw new InvalidOperationException("simulated transport failure");
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.EnsureAsync(FailingInitialize, default));
        Assert.False(gate.IsCompleted);
        Assert.Equal(1, attempt);

        // Retry — the gate must not have latched closed; the next caller must re-enter the
        // initializer. (Real-world: a transient network blip on first connect shouldn't make
        // the session permanently unusable.)
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.EnsureAsync(FailingInitialize, default));
        Assert.Equal(2, attempt);

        // And a successful third attempt flips the gate exactly once.
        await gate.EnsureAsync(_ => Task.CompletedTask, default);
        Assert.True(gate.IsCompleted);

        // Subsequent calls are no-ops.
        var noopRuns = 0;
        await gate.EnsureAsync(_ => { noopRuns++; return Task.CompletedTask; }, default);
        Assert.Equal(0, noopRuns);
    }

    [Fact]
    public async Task EnsureAsync_honors_cancellation_while_waiting_for_semaphore()
    {
        using var gate = new AsyncOnceGate();
        var initEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task Initialize(CancellationToken ct)
        {
            initEntered.SetResult();
            await initRelease.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        var a = Task.Run(() => gate.EnsureAsync(Initialize, default));
        await initEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Caller B comes in with a cancellation token that fires while it's waiting on the gate.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gate.EnsureAsync(_ => Task.CompletedTask, cts.Token));

        // Release A to clean up.
        initRelease.SetResult();
        await a;
    }
}
