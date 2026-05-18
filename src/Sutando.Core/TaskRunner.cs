using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sutando.Bridge;
using Sutando.Workspace;

namespace Sutando.Core;

/// <summary>
/// Orchestrates the task-execution loop: pull <see cref="TaskEnvelope"/>s from a
/// <see cref="TaskWatcher"/>, dispatch each through an <see cref="IAgentExecutor"/>,
/// write the matching result, signal status, and archive on completion.
/// </summary>
/// <remarks>
/// Tasks are processed in priority order — at any moment the runner takes the highest-priority
/// pending envelope (ties broken FIFO by arrival). One task at a time keeps result-ordering
/// deterministic and matches upstream's single-consumer model.
/// </remarks>
public sealed class TaskRunner : IAsyncDisposable
{
    private readonly WorkspaceDirectory _workspace;
    private readonly TaskWatcher _watcher;
    private readonly IAgentExecutor _executor;
    private readonly ResultFile _results;
    private readonly TaskArchive _archive;
    private readonly CoreStatus _status;
    private readonly ILogger<TaskRunner> _logger;
    private readonly CancellationTokenSource _stopCts = new();
    private Task? _loop;

    /// <param name="workspace">Resolved workspace.</param>
    /// <param name="executor">Concrete executor (CLI or HTTP).</param>
    /// <param name="watcher">Pre-constructed watcher; <see cref="TaskWatcher.Start"/> is invoked here.</param>
    /// <param name="logger">Optional logger.</param>
    public TaskRunner(
        WorkspaceDirectory workspace,
        IAgentExecutor executor,
        TaskWatcher? watcher = null,
        ILogger<TaskRunner>? logger = null)
    {
        _workspace = workspace;
        _executor = executor;
        _watcher = watcher ?? new TaskWatcher(workspace);
        _results = new ResultFile(workspace.Results);
        _archive = new TaskArchive(workspace);
        _status = new CoreStatus(workspace);
        _logger = logger ?? NullLogger<TaskRunner>.Instance;
    }

    /// <summary>Start consuming tasks. Idempotent. Returns the background Task for monitoring.</summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        if (_loop is not null)
        {
            return _loop;
        }
        _watcher.Start();
        _status.SignalIdle();
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopCts.Token);
        _loop = Task.Run(() => RunAsync(linked.Token), linked.Token);
        return _loop;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TaskEnvelope envelope;
                try
                {
                    envelope = await _watcher.Reader.ReadAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                await ProcessAsync(envelope, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _status.SignalIdle();
        }
    }

    /// <summary>
    /// Process a single envelope: execute via <see cref="IAgentExecutor"/>, write the marker-
    /// composed result, archive. Public so tests can drive it deterministically.
    /// </summary>
    public async Task ProcessAsync(TaskEnvelope envelope, CancellationToken ct)
    {
        _status.SignalRunning(BuildStepLabel(envelope));
        try
        {
            var result = await _executor.ExecuteAsync(envelope, ct).ConfigureAwait(false);
            WriteResult(envelope.Id, result);
            _logger.LogInformation(
                "task {Id} done in {Ms} ms (executor={Executor}, timedOut={Timeout}, err={Error})",
                envelope.Id, (long)result.Duration.TotalMilliseconds, _executor.Id, result.TimedOut, result.IsError);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("task {Id} cancelled before completion", envelope.Id);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "task {Id} threw — writing error result", envelope.Id);
            _results.Write(envelope.Id, $"executor crashed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _archive.Archive(envelope.Id);
            _status.SignalIdle();
        }
    }

    private void WriteResult(string taskId, AgentResult result)
    {
        _results.WriteWithMarkers(
            taskId,
            result.Body,
            dedupedTo: result.DedupedTo,
            noSend: result.NoSend,
            alreadyReplied: result.AlreadyReplied,
            attachments: result.Attachments);
    }

    private static string BuildStepLabel(TaskEnvelope envelope)
    {
        var preview = envelope.Body.Replace('\n', ' ');
        return preview.Length <= 60 ? preview : preview[..60] + "…";
    }

    /// <summary>Stop the loop and dispose the underlying watcher.</summary>
    public async ValueTask DisposeAsync()
    {
        _stopCts.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }
        await _watcher.DisposeAsync().ConfigureAwait(false);
        _stopCts.Dispose();
    }
}
