using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NCrontab;

namespace Sutando.Proactive;

/// <summary>
/// Default <see cref="ICronScheduler"/> implementation backed by <see cref="NCrontab.CrontabSchedule"/>
/// and <see cref="TimeProvider"/>. One <see cref="ITimer"/> is held per entry; on each fire,
/// the next occurrence is computed and the timer is re-armed.
/// </summary>
/// <remarks>
/// <para>
/// All cron expressions are interpreted in UTC. The scheduler intentionally uses
/// <see cref="TimeProvider"/> rather than <see cref="System.Threading.PeriodicTimer"/> because
/// <c>PeriodicTimer</c> reads the system clock directly and cannot be driven by a
/// <c>FakeTimeProvider</c> in tests.
/// </para>
/// <para>
/// Exceptions thrown by the per-fire callback are caught and logged but never bubble out —
/// one bad pass must not kill the scheduler.
/// </para>
/// </remarks>
public sealed class CronScheduler : ICronScheduler, IDisposable
{
    private static readonly CrontabSchedule.ParseOptions FiveFieldOptions = new() { IncludingSeconds = false };

    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CronScheduler> _logger;
    private readonly Lock _gate = new();
    private readonly List<ITimer> _timers = new();
    private CancellationTokenSource? _stopCts;
    private bool _disposed;

    /// <summary>Initializes a new scheduler.</summary>
    /// <param name="timeProvider">Clock source; pass <see cref="TimeProvider.System"/> in production and a fake in tests.</param>
    /// <param name="logger">Optional logger.</param>
    public CronScheduler(TimeProvider? timeProvider = null, ILogger<CronScheduler>? logger = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<CronScheduler>.Instance;
    }

    /// <inheritdoc />
    public void Start(IReadOnlyList<CronEntry> entries, Func<CronEntry, CancellationToken, Task> onFired, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(onFired);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            // Stop any prior run before we re-arm — calling Start twice is allowed.
            StopInternalLocked();
            _stopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            foreach (var entry in entries)
            {
                ScheduleNextLocked(entry, onFired, _stopCts.Token);
            }
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        lock (_gate)
        {
            StopInternalLocked();
        }
    }

    /// <summary>Disposes the scheduler and any pending timers.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
    }

    private void ScheduleNextLocked(CronEntry entry, Func<CronEntry, CancellationToken, Task> onFired, CancellationToken stopToken, DateTime? fromUtcOverride = null)
    {
        if (stopToken.IsCancellationRequested)
        {
            return;
        }

        CrontabSchedule schedule;
        try
        {
            schedule = CrontabSchedule.Parse(entry.Cron, FiveFieldOptions);
        }
        catch (CrontabException ex)
        {
            _logger.LogError(ex, "Cron entry {Name} has invalid cron expression {Cron}; not scheduling.", entry.Name, entry.Cron);
            return;
        }

        // When re-arming inside a fire callback we compute the next occurrence relative to
        // the timer's *expected* fire time, NOT the live clock. FakeTimeProvider advances
        // its internal _now in one shot at the start of Advance(d), so by the time a
        // callback executes GetUtcNow() already reads end-of-advance. Anchoring on the
        // expected fire time keeps cadence correct under both real and fake clocks.
        var anchorUtc = fromUtcOverride ?? _timeProvider.GetUtcNow().UtcDateTime;
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var next = schedule.GetNextOccurrence(anchorUtc);
        var delay = next - nowUtc;
        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        ITimer? timer = null;

        // Capture the timer reference inside the callback so we can remove it on fire — this
        // keeps _timers from growing without bound across re-arms. The captured `next` is
        // passed through so the re-arm anchors on the correct wall-clock instant even when
        // the underlying TimeProvider advances time in bulk.
        var nextOccurrence = next;
        timer = _timeProvider.CreateTimer(
            _ => HandleFire(entry, onFired, stopToken, timer, nextOccurrence),
            state: null,
            dueTime: delay,
            period: Timeout.InfiniteTimeSpan);

        _timers.Add(timer);
        _logger.LogDebug("Scheduled cron entry {Name} for {Next:o} (in {Delay}).", entry.Name, next, delay);
    }

    private void HandleFire(CronEntry entry, Func<CronEntry, CancellationToken, Task> onFired, CancellationToken stopToken, ITimer? firedTimer, DateTime firedAtUtc)
    {
        if (stopToken.IsCancellationRequested)
        {
            return;
        }

        // Re-arm before invoking the callback so a long-running pass doesn't push the next
        // fire arbitrarily late. Lock is held only across the (cheap) scheduling work; the
        // user callback runs unlocked.
        lock (_gate)
        {
            if (firedTimer is not null)
            {
                _timers.Remove(firedTimer);
                firedTimer.Dispose();
            }

            ScheduleNextLocked(entry, onFired, stopToken, fromUtcOverride: firedAtUtc);
        }

        // Fire-and-forget — the scheduler does not await the pass body. Exceptions are
        // observed via the continuation so an unobserved-task exception never escapes.
        _ = InvokeCallbackAsync(entry, onFired, stopToken);
    }

    private async Task InvokeCallbackAsync(CronEntry entry, Func<CronEntry, CancellationToken, Task> onFired, CancellationToken stopToken)
    {
        try
        {
            await onFired(entry, stopToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
            // Expected on shutdown; swallow.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cron callback for {Name} threw; scheduler continues.", entry.Name);
        }
    }

    private void StopInternalLocked()
    {
        foreach (var timer in _timers)
        {
            try
            {
                timer.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Ignoring exception while disposing scheduler timer.");
            }
        }
        _timers.Clear();

        if (_stopCts is not null)
        {
            try
            {
                _stopCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // already disposed — fine
            }
            _stopCts.Dispose();
            _stopCts = null;
        }
    }
}
