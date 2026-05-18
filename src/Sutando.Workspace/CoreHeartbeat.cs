using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sutando.Workspace;

/// <summary>
/// Per-host heartbeat file (<c>state/cores/&lt;hostname&gt;.alive</c>) — written every
/// <see cref="DefaultBeatInterval"/> while a sutando process is alive, removed on graceful
/// shutdown. Peers use mtime + payload to know which cores are available.
/// </summary>
/// <remarks>
/// Direct port of upstream <c>src/core_heartbeat.py</c>. Per-host (not per-PID) so multiple
/// runs on the same machine share the file; the last process to start owns it.
/// </remarks>
public sealed class CoreHeartbeat : IAsyncDisposable, IDisposable
{
    /// <summary>Default beat cadence (30 s).</summary>
    public static readonly TimeSpan DefaultBeatInterval = TimeSpan.FromSeconds(30);

    /// <summary>Liveness window — mtime younger than this means alive.</summary>
    public static readonly TimeSpan AliveWindow = TimeSpan.FromSeconds(90);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly WorkspaceDirectory _workspace;
    private readonly ILogger<CoreHeartbeat> _logger;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private string _status = "idle";
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    /// <summary>The path the heartbeat file would be written to.</summary>
    public FileInfo AliveFile { get; }

    /// <summary>The hostname this heartbeat reports.</summary>
    public string Host { get; }

    /// <summary>The PID this heartbeat reports.</summary>
    public int Pid { get; }

    /// <param name="workspace">Resolved workspace.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="interval">Beat cadence; defaults to <see cref="DefaultBeatInterval"/>.</param>
    public CoreHeartbeat(WorkspaceDirectory workspace, ILogger<CoreHeartbeat>? logger = null, TimeSpan? interval = null)
    {
        _workspace = workspace;
        _logger = logger ?? NullLogger<CoreHeartbeat>.Instance;
        _interval = interval ?? DefaultBeatInterval;

        Host = ShortHostname();
        Pid = Environment.ProcessId;

        var coresDir = _workspace.State.CreateSubdirectory("cores");
        AliveFile = new FileInfo(Path.Combine(coresDir.FullName, $"{Host}.alive"));
    }

    /// <summary>Update the reported status. Picked up on the next beat.</summary>
    /// <param name="status">Free-form short status (e.g. <c>idle</c>, <c>running</c>, <c>degraded</c>).</param>
    public void SetStatus(string status) => _status = status;

    /// <summary>Start beating in the background. Idempotent.</summary>
    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }
        _loop = Task.Run(() => BeatLoopAsync(_cts.Token));
    }

    /// <summary>Write a single beat synchronously, for tests and one-shot CLI invocations.</summary>
    public void WriteOnce() => WritePayload(_status);

    /// <summary>Read the current heartbeat payload from disk, if present.</summary>
    public HeartbeatPayload? Read()
    {
        if (!AliveFile.Exists)
        {
            return null;
        }
        try
        {
            using var stream = AliveFile.OpenRead();
            return JsonSerializer.Deserialize<HeartbeatPayload>(stream, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogDebug(ex, "Heartbeat file unreadable; treating as absent");
            return null;
        }
    }

    /// <summary>True if any heartbeat file in <c>state/cores/</c> has mtime younger than <see cref="AliveWindow"/>.</summary>
    public static bool AnyCoreAlive(WorkspaceDirectory workspace, DateTimeOffset? now = null)
    {
        var nowVal = now ?? DateTimeOffset.UtcNow;
        var dir = new DirectoryInfo(Path.Combine(workspace.State.FullName, "cores"));
        if (!dir.Exists)
        {
            return false;
        }
        return dir
            .EnumerateFiles("*.alive")
            .Any(f => (nowVal - new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero)) < AliveWindow);
    }

    private async Task BeatLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                WritePayload(_status);
                await Task.Delay(_interval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Heartbeat loop terminated unexpectedly");
        }
    }

    private void WritePayload(string status)
    {
        var payload = new HeartbeatPayload
        {
            Host = Host,
            Pid = Pid,
            StartedAt = _startedAt.ToUnixTimeSeconds(),
            LastBeatAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Status = status,
            SchemaVersion = 1,
        };
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        WorkspaceDirectory.AtomicWrite(AliveFile.FullName, json);
    }

    private static string ShortHostname()
    {
        var name = Environment.MachineName;
        var dot = name.IndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    /// <summary>Stop beating and remove the alive file so peers see the offline transition.</summary>
    public async ValueTask DisposeAsync()
    {
        await CancelLoopAsync().ConfigureAwait(false);
        RemoveAliveFile();
        _cts.Dispose();
    }

    /// <summary>
    /// Synchronous disposal. Suitable when no background loop has been started — i.e.
    /// the heartbeat was only used via <see cref="WriteOnce"/>. If <see cref="Start"/>
    /// has been called, prefer <see cref="DisposeAsync"/> so the loop can drain.
    /// </summary>
    public void Dispose()
    {
        if (_loop is not null)
        {
            // Async path is correct here — fall through to GetAwaiter().GetResult() so
            // tests / sync callers still see the loop drain. Single-threaded test usage
            // makes this safe; in long-lived hosts use DisposeAsync().
            _cts.Cancel();
            try
            {
                _loop.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // expected
            }
        }
        RemoveAliveFile();
        _cts.Dispose();
    }

    private async Task CancelLoopAsync()
    {
        _cts.Cancel();
        if (_loop is null)
        {
            return;
        }
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected
        }
    }

    private void RemoveAliveFile()
    {
        try
        {
            AliveFile.Refresh();
            if (AliveFile.Exists)
            {
                AliveFile.Delete();
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not remove alive file on shutdown");
        }
    }
}

/// <summary>Heartbeat payload schema, version 1.</summary>
public sealed record HeartbeatPayload
{
    /// <summary>Short hostname (no domain suffix).</summary>
    [JsonPropertyName("host")] public string Host { get; init; } = string.Empty;

    /// <summary>OS process ID.</summary>
    [JsonPropertyName("pid")] public int Pid { get; init; }

    /// <summary>Unix epoch seconds when the process started.</summary>
    [JsonPropertyName("started_at")] public long StartedAt { get; init; }

    /// <summary>Unix epoch seconds of the latest written beat.</summary>
    [JsonPropertyName("last_beat_at")] public long LastBeatAt { get; init; }

    /// <summary>Short freeform status (<c>idle</c>, <c>running</c>, <c>degraded</c>).</summary>
    [JsonPropertyName("status")] public string Status { get; init; } = "idle";

    /// <summary>Schema version; bump when the payload shape changes.</summary>
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; } = 1;
}
