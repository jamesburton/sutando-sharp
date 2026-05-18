using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sutando.Workspace;

namespace Sutando.Bridge;

/// <summary>
/// Watches the <c>tasks/</c> directory for new task files and emits parsed envelopes through
/// a bounded channel. Picks up files already present on start and watches for new arrivals.
/// </summary>
/// <remarks>
/// Built on <see cref="FileSystemWatcher"/> for cross-platform low-latency notification, with
/// a fallback periodic rescan (configurable) to catch events that some FS drivers miss on
/// network mounts and WSL bind mounts.
/// </remarks>
public sealed class TaskWatcher : IAsyncDisposable, IDisposable
{
    private readonly WorkspaceDirectory _workspace;
    private readonly ILogger<TaskWatcher> _logger;
    private readonly Channel<TaskEnvelope> _channel;
    private readonly TimeSpan _rescanInterval;
    private readonly CancellationTokenSource _cts = new();

    private FileSystemWatcher? _fsw;
    private Task? _rescanLoop;
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _seenGate = new();

    /// <summary>Channel of parsed task envelopes, oldest first. Caller consumes via <see cref="ChannelReader{T}.ReadAsync"/>.</summary>
    public ChannelReader<TaskEnvelope> Reader => _channel.Reader;

    /// <param name="workspace">Resolved workspace.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="rescanInterval">Periodic rescan fallback; defaults to 30 s. Set to <see cref="Timeout.InfiniteTimeSpan"/> to disable.</param>
    public TaskWatcher(
        WorkspaceDirectory workspace,
        ILogger<TaskWatcher>? logger = null,
        TimeSpan? rescanInterval = null)
    {
        _workspace = workspace;
        _logger = logger ?? NullLogger<TaskWatcher>.Instance;
        _rescanInterval = rescanInterval ?? TimeSpan.FromSeconds(30);

        _channel = Channel.CreateUnbounded<TaskEnvelope>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
        });
    }

    /// <summary>Begin watching. Idempotent — subsequent calls do nothing.</summary>
    public void Start()
    {
        if (_fsw is not null)
        {
            return;
        }

        // Drain anything already present.
        Rescan();

        _fsw = new FileSystemWatcher(_workspace.Tasks.FullName)
        {
            Filter = "task-*.txt",
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
        };
        _fsw.Created += OnFileChanged;
        _fsw.Renamed += OnFileChanged;
        _fsw.Changed += OnFileChanged;
        _fsw.Error += (_, args) => _logger.LogWarning(args.GetException(), "task watcher error");

        if (_rescanInterval != Timeout.InfiniteTimeSpan)
        {
            _rescanLoop = Task.Run(() => RescanLoopAsync(_cts.Token));
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        TryEnqueue(e.FullPath);
    }

    private void Rescan()
    {
        if (!_workspace.Tasks.Exists)
        {
            return;
        }
        foreach (var f in _workspace.Tasks.EnumerateFiles("task-*.txt", SearchOption.TopDirectoryOnly))
        {
            TryEnqueue(f.FullName);
        }
    }

    private async Task RescanLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_rescanInterval, ct).ConfigureAwait(false);
                Rescan();
            }
        }
        catch (OperationCanceledException)
        {
            // expected
        }
    }

    private void TryEnqueue(string path)
    {
        // Skip if we've already enqueued this file.
        var key = NormalizeKey(path);
        lock (_seenGate)
        {
            if (!_seen.Add(key))
            {
                return;
            }
        }

        // Skip files that no longer exist (the writer may have moved them already).
        if (!File.Exists(path))
        {
            lock (_seenGate) { _seen.Remove(key); }
            return;
        }

        TaskEnvelope envelope;
        try
        {
            envelope = TaskFile.ParseFile(path);
        }
        catch (Exception ex) when (ex is IOException or FormatException)
        {
            _logger.LogWarning(ex, "Unparseable task file {Path} — skipping", path);
            // Don't add to seen-set permanently; a partial write might still complete.
            lock (_seenGate) { _seen.Remove(key); }
            return;
        }

        if (!_channel.Writer.TryWrite(envelope))
        {
            _logger.LogWarning("Task channel rejected {Id} — dropping", envelope.Id);
        }
    }

    private static string NormalizeKey(string path) => Path.GetFullPath(path).ToLowerInvariant();

    /// <summary>Stop watching and drain the channel.</summary>
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _fsw?.Dispose();
        _fsw = null;
        if (_rescanLoop is not null)
        {
            try { await _rescanLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }
        _channel.Writer.TryComplete();
        _cts.Dispose();
    }

    /// <summary>Sync disposal. See <see cref="DisposeAsync"/>.</summary>
    public void Dispose()
    {
        _cts.Cancel();
        _fsw?.Dispose();
        _fsw = null;
        if (_rescanLoop is not null)
        {
            try { _rescanLoop.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { /* expected */ }
        }
        _channel.Writer.TryComplete();
        _cts.Dispose();
    }
}
