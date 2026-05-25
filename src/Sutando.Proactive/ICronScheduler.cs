namespace Sutando.Proactive;

/// <summary>
/// Drives <see cref="CronEntry"/> dispatch on a wall-clock schedule. A scheduler is started
/// once, runs in the background, and invokes the supplied callback every time one of its
/// entries' cron expressions fires.
/// </summary>
/// <remarks>
/// The scheduler does NOT know about prompts, executors, or skills — it only knows when to
/// fire. Coupling to a host-provided <see cref="IProactivePass"/> happens one layer up in
/// <see cref="ProactiveBackgroundService"/>.
/// </remarks>
public interface ICronScheduler
{
    /// <summary>
    /// Starts the scheduler. Schedules a timer for the next-due entry only; on each fire,
    /// the entry's callback is invoked and the next occurrence is computed and scheduled
    /// recursively. Returns immediately — actual dispatch happens asynchronously.
    /// </summary>
    /// <param name="entries">The cron entries to schedule.</param>
    /// <param name="onFired">Callback invoked when an entry's cron fires. Must be non-throwing — exceptions are caught and logged so a bad pass doesn't kill the scheduler.</param>
    /// <param name="cancellationToken">Stops the scheduler and any pending timers when cancelled.</param>
    void Start(IReadOnlyList<CronEntry> entries, Func<CronEntry, CancellationToken, Task> onFired, CancellationToken cancellationToken);

    /// <summary>Stops the scheduler and disposes pending timers. Safe to call multiple times.</summary>
    void Stop();
}
