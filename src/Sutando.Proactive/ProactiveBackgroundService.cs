using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sutando.Workspace;

namespace Sutando.Proactive;

/// <summary>
/// Hosted service that loads cron entries from the workspace, hands them to the configured
/// <see cref="ICronScheduler"/>, and on each fire dispatches a fresh <see cref="IProactivePass"/>
/// instance resolved from a per-pass DI scope.
/// </summary>
/// <remarks>
/// <para>
/// The pass body is intentionally pluggable — this library does not know how to "do" a
/// proactive pass (the 11-step procedure from the upstream <c>proactive-loop</c> SKILL is
/// LLM-driven). Hosts register an <see cref="IProactivePass"/> implementation that owns
/// the actual work; this service guarantees it is invoked on schedule with a coherent
/// <see cref="ProactivePassContext"/>.
/// </para>
/// <para>
/// Per-pass DI scoping mirrors the standard ASP.NET request pattern: scoped services
/// (e.g. an <c>IAgentExecutor</c> the host registers as scoped) get a fresh instance on
/// every cron fire, so the pass doesn't accidentally share state with the previous run.
/// </para>
/// </remarks>
public sealed class ProactiveBackgroundService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ICronScheduler _scheduler;
    private readonly CronConfigLoader _loader;
    private readonly WorkspaceDirectory _workspace;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ProactiveBackgroundService> _logger;

    /// <summary>Initializes a new background service.</summary>
    /// <param name="services">Root service provider; used to create per-pass scopes.</param>
    /// <param name="scheduler">Cron scheduler that drives dispatch.</param>
    /// <param name="loader">Loader for workspace-resident cron config.</param>
    /// <param name="workspace">The workspace to operate on.</param>
    /// <param name="timeProvider">Clock source; pass <see cref="TimeProvider.System"/> in production.</param>
    /// <param name="logger">Optional logger.</param>
    public ProactiveBackgroundService(
        IServiceProvider services,
        ICronScheduler scheduler,
        CronConfigLoader loader,
        WorkspaceDirectory workspace,
        TimeProvider? timeProvider = null,
        ILogger<ProactiveBackgroundService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(workspace);
        _services = services;
        _scheduler = scheduler;
        _loader = loader;
        _workspace = workspace;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<ProactiveBackgroundService>.Instance;
    }

    /// <summary>Number of cron entries actively scheduled. Exposed for tests + diagnostics.</summary>
    public int ScheduledEntryCount { get; private set; }

    /// <summary>Total number of proactive passes dispatched since startup. Exposed for tests + diagnostics.</summary>
    public long DispatchedPassCount => Interlocked.Read(ref _dispatchedPassCount);

    private long _dispatchedPassCount;
    private CancellationTokenSource? _runCts;

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Load + start the scheduler synchronously on the caller's thread so callers can
        // rely on "await StartAsync() returned → scheduler is live." The base implementation
        // launches ExecuteAsync via Task.Run, which means relying on ExecuteAsync for setup
        // races any code that fires immediately after StartAsync — including tests that
        // advance a FakeTimeProvider.
        var entries = _loader.Load(_workspace);
        ScheduledEntryCount = entries.Count;

        if (entries.Count == 0)
        {
            _logger.LogInformation(
                "Proactive loop started with no cron entries; service is alive but inert until a crons.json appears.");
        }
        else
        {
            _logger.LogInformation(
                "Proactive loop scheduling {Count} cron entries: {Names}",
                entries.Count,
                string.Join(", ", entries.Select(e => e.Name)));
        }

        // Own a long-lived CTS for the scheduler — the `cancellationToken` passed to
        // StartAsync is the startup token (cancelled when host startup is aborted), not the
        // service-lifetime token. StopAsync cancels and disposes ours.
        _runCts = new CancellationTokenSource();
        _scheduler.Start(entries, DispatchPassAsync, _runCts.Token);

        return base.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Setup is done in StartAsync (see comment there). ExecuteAsync just parks on the
        // shutdown signal so BackgroundService treats us as still running until stop.
        return WaitForShutdownAsync(stoppingToken);
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _scheduler.Stop();
        if (_runCts is not null)
        {
            try
            {
                _runCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // already disposed — fine
            }
            _runCts.Dispose();
            _runCts = null;
        }
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DispatchPassAsync(CronEntry entry, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _dispatchedPassCount);

        // Fresh scope per pass so scoped host services (e.g. IAgentExecutor) don't leak
        // state between cron fires.
        await using var scope = _services.CreateAsyncScope();
        var pass = scope.ServiceProvider.GetRequiredService<IProactivePass>();

        var context = new ProactivePassContext(
            _workspace,
            scope.ServiceProvider,
            entry,
            _timeProvider.GetUtcNow());

        try
        {
            await pass.RunAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected on shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Proactive pass for cron entry {Name} failed.", entry.Name);
        }
    }

    private static async Task WaitForShutdownAsync(CancellationToken stoppingToken)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = stoppingToken.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), tcs);
        await tcs.Task.ConfigureAwait(false);
    }
}
