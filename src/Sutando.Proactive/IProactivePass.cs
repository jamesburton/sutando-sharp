namespace Sutando.Proactive;

/// <summary>
/// One iteration of Sutando's proactive loop. A pass receives the workspace + the cron
/// entry that fired (if any) and is expected to do whatever work is appropriate — read
/// pending tasks, run health checks, drive an executor, post heartbeats, etc.
/// </summary>
/// <remarks>
/// <para>
/// This library does not ship an "interesting" pass — the upstream <c>proactive-loop</c>
/// SKILL describes an 11-step procedure that is fundamentally LLM-driven (read tasks,
/// pick the highest-ROI work, act on it, update build log). The managed contract here is
/// pure plumbing: cron fires → host's <see cref="IProactivePass"/> runs.
/// </para>
/// <para>
/// Hosts wire their pass via <c>AddSutandoProactive</c>. The default registration is
/// <see cref="NoopProactivePass"/>, which logs and returns — useful for tests and for
/// confirming the scheduler chassis is alive in environments that don't yet have an
/// executor configured.
/// </para>
/// </remarks>
public interface IProactivePass
{
    /// <summary>Run one pass.</summary>
    /// <param name="context">The dispatch context (workspace, triggering cron entry, services).</param>
    /// <param name="cancellationToken">Cancelled when the host is shutting down.</param>
    /// <returns>A task that completes when the pass is done.</returns>
    Task RunAsync(ProactivePassContext context, CancellationToken cancellationToken);
}
