using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sutando.Workspace;

namespace Sutando.Dashboard;

/// <summary>
/// Hosted service that watches the workspace for core-status, task, and result file changes
/// and pushes each event to connected <see cref="StatusHub"/> clients.
/// </summary>
/// <remarks>
/// <para>
/// Three <see cref="FileSystemWatcher"/> instances are owned by this service: one for
/// <c>core-status.json</c>, one for <c>tasks/</c>, and one for <c>results/</c>. The service
/// is also a <see cref="IBroadcastChannel"/> so tests can drive broadcasts deterministically
/// without touching the filesystem.
/// </para>
/// </remarks>
public sealed class WorkspaceBroadcaster : IHostedService, IDisposable, IBroadcastChannel
{
    /// <summary>Event name pushed when <c>core-status.json</c> changes.</summary>
    public const string CoreStatusChanged = "core_status_changed";

    /// <summary>Event name pushed when a new file appears in <c>tasks/</c>.</summary>
    public const string TaskAdded = "task_added";

    /// <summary>Event name pushed when a new file appears in <c>results/</c>.</summary>
    public const string ResultAdded = "result_added";

    private readonly WorkspaceDirectory _workspace;
    private readonly CoreStatus _coreStatus;
    private readonly IHubContext<StatusHub> _hub;
    private readonly ILogger<WorkspaceBroadcaster> _logger;

    private FileSystemWatcher? _statusWatcher;
    private FileSystemWatcher? _tasksWatcher;
    private FileSystemWatcher? _resultsWatcher;

    /// <param name="workspace">Resolved workspace.</param>
    /// <param name="coreStatus">Reader for <c>core-status.json</c>.</param>
    /// <param name="hub">SignalR hub context used to broadcast.</param>
    /// <param name="logger">Diagnostic logger.</param>
    public WorkspaceBroadcaster(
        WorkspaceDirectory workspace,
        CoreStatus coreStatus,
        IHubContext<StatusHub> hub,
        ILogger<WorkspaceBroadcaster> logger)
    {
        _workspace = workspace;
        _coreStatus = coreStatus;
        _hub = hub;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Ensure all watched dirs exist before we attach — FileSystemWatcher throws on missing.
        _ = _workspace.Tasks;
        _ = _workspace.Results;

        _statusWatcher = new FileSystemWatcher(_workspace.Root.FullName)
        {
            Filter = "core-status.json",
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };
        _statusWatcher.Changed += (_, _) => _ = BroadcastCoreStatusAsync();
        _statusWatcher.Created += (_, _) => _ = BroadcastCoreStatusAsync();
        _statusWatcher.Renamed += (_, _) => _ = BroadcastCoreStatusAsync();
        _statusWatcher.Error += (_, e) => _logger.LogWarning(e.GetException(), "core-status watcher error");

        _tasksWatcher = new FileSystemWatcher(_workspace.Tasks.FullName)
        {
            Filter = "task-*.txt",
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
        };
        _tasksWatcher.Created += (_, e) => _ = BroadcastTaskAddedAsync(e.FullPath);
        _tasksWatcher.Renamed += (_, e) => _ = BroadcastTaskAddedAsync(e.FullPath);
        _tasksWatcher.Error += (_, e) => _logger.LogWarning(e.GetException(), "tasks watcher error");

        _resultsWatcher = new FileSystemWatcher(_workspace.Results.FullName)
        {
            // Match task-, proactive-, question-* result files.
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
        };
        _resultsWatcher.Created += (_, e) => _ = BroadcastResultAddedAsync(e.FullPath);
        _resultsWatcher.Renamed += (_, e) => _ = BroadcastResultAddedAsync(e.FullPath);
        _resultsWatcher.Error += (_, e) => _logger.LogWarning(e.GetException(), "results watcher error");

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        DisposeWatchers();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task BroadcastCoreStatusAsync()
    {
        var payload = _coreStatus.Read();
        try
        {
            await _hub.Clients.All.SendAsync(CoreStatusChanged, payload).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "core_status_changed broadcast failed");
        }
    }

    /// <inheritdoc/>
    public async Task BroadcastTaskAddedAsync(string path)
    {
        var id = Path.GetFileNameWithoutExtension(path);
        var payload = new TaskAddedPayload
        {
            Id = id,
            Path = path,
            DetectedAt = DateTimeOffset.UtcNow,
        };
        try
        {
            await _hub.Clients.All.SendAsync(TaskAdded, payload).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "task_added broadcast failed for {Path}", path);
        }
    }

    /// <inheritdoc/>
    public async Task BroadcastResultAddedAsync(string path)
    {
        var id = Path.GetFileNameWithoutExtension(path);
        var payload = new ResultAddedPayload
        {
            Id = id,
            Path = path,
            DetectedAt = DateTimeOffset.UtcNow,
        };
        try
        {
            await _hub.Clients.All.SendAsync(ResultAdded, payload).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "result_added broadcast failed for {Path}", path);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        DisposeWatchers();
    }

    private void DisposeWatchers()
    {
        _statusWatcher?.Dispose();
        _statusWatcher = null;
        _tasksWatcher?.Dispose();
        _tasksWatcher = null;
        _resultsWatcher?.Dispose();
        _resultsWatcher = null;
    }
}

/// <summary>
/// Surface used by tests to drive broadcasts deterministically; production callers should
/// just let the FileSystemWatchers run.
/// </summary>
public interface IBroadcastChannel
{
    /// <summary>Send the current <c>core-status.json</c> payload to all clients.</summary>
    /// <returns>The completion task.</returns>
    Task BroadcastCoreStatusAsync();

    /// <summary>Send a <c>task_added</c> event for the given path.</summary>
    /// <param name="path">Absolute path of the new task file.</param>
    /// <returns>The completion task.</returns>
    Task BroadcastTaskAddedAsync(string path);

    /// <summary>Send a <c>result_added</c> event for the given path.</summary>
    /// <param name="path">Absolute path of the new result file.</param>
    /// <returns>The completion task.</returns>
    Task BroadcastResultAddedAsync(string path);
}
