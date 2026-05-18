using System.Globalization;
using Sutando.Bridge;
using Sutando.Workspace;

namespace Sutando.Channels.Cli;

/// <summary>
/// Local interactive REPL channel. Reads lines from <see cref="Console.In"/>, writes
/// <c>task-chat-&lt;ms&gt;.txt</c> envelopes into <c>&lt;workspace&gt;/tasks/</c>, polls
/// <c>&lt;workspace&gt;/results/&lt;id&gt;.txt</c> for matching results, and prints them.
/// </summary>
/// <remarks>
/// <para>
/// Slash-commands accepted: <c>:exit</c>, <c>:quit</c>, <c>:status</c>, <c>:tasks</c>.
/// EOF (Ctrl+Z+Enter on Windows, Ctrl+D on POSIX) and Ctrl+C also exit cleanly.
/// </para>
/// <para>
/// Polling strategy mirrors upstream's Telegram bridge: a <see cref="FileSystemWatcher"/>
/// over <c>results/</c> raises a signal, the channel debounces briefly to let the writer
/// flush, then reads the file. A fallback periodic rescan catches events that some
/// filesystem drivers drop (WSL bind mounts, network shares).
/// </para>
/// </remarks>
public sealed class CliChatChannel : IChannel
{
    private readonly WorkspaceDirectory _workspace;
    private readonly CliChatChannelOptions _options;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly OwnerActivity _ownerActivity;
    private readonly CoreStatus _coreStatus;

    /// <summary>Create a chat channel bound to the resolved workspace.</summary>
    /// <param name="workspace">Resolved workspace; this is where envelopes are written and results are polled.</param>
    /// <param name="options">Optional tunables (timeout, poll interval, debounce, greeting version).</param>
    /// <param name="input">Optional input source; defaults to <see cref="Console.In"/>. Injected for tests.</param>
    /// <param name="output">Optional output sink; defaults to <see cref="Console.Out"/>. Injected for tests.</param>
    public CliChatChannel(
        WorkspaceDirectory workspace,
        CliChatChannelOptions? options = null,
        TextReader? input = null,
        TextWriter? output = null)
    {
        _workspace = workspace;
        _options = options ?? new CliChatChannelOptions();
        _input = input ?? Console.In;
        _output = output ?? Console.Out;
        _ownerActivity = new OwnerActivity(workspace);
        _coreStatus = new CoreStatus(workspace);
    }

    /// <inheritdoc/>
    public async Task RunAsync(CancellationToken ct)
    {
        _output.WriteLine($"sutando chat — {_options.Version} (type :exit to quit, :status, :tasks)");

        while (!ct.IsCancellationRequested)
        {
            _output.Write("> ");
            _output.Flush();

            string? line;
            try
            {
                line = await ReadLineAsync(_input, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (line is null)
            {
                // EOF — clean exit.
                _output.WriteLine();
                _output.WriteLine("(eof)");
                return;
            }

            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            // Record owner activity on every user input — including slash commands. The
            // proactive loop only needs to see "the human is at the keyboard"; the
            // summary helps a human review the activity log later.
            _ownerActivity.Record("chat", trimmed);

            if (trimmed is ":exit" or ":quit")
            {
                _output.WriteLine("bye.");
                return;
            }

            if (trimmed == ":status")
            {
                PrintStatus();
                continue;
            }

            if (trimmed == ":tasks")
            {
                PrintPendingTasks();
                continue;
            }

            await SubmitAndAwaitResultAsync(trimmed, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Write a chat-task envelope and wait for the matching result file.</summary>
    private async Task SubmitAndAwaitResultAsync(string body, CancellationToken ct)
    {
        var id = $"task-chat-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        var envelope = new TaskEnvelope
        {
            Id = id,
            Timestamp = DateTimeOffset.UtcNow,
            Body = body,
            Source = TaskSource.Chat,
            ChannelId = _options.ChannelId,
            UserId = Environment.GetEnvironmentVariable("SUTANDO_DM_OWNER_ID") ?? _options.UserId,
            AccessTier = AccessTier.Owner,
            Priority = TaskPriority.Normal,
        };
        _ = TaskFile.Write(_workspace.Tasks.FullName, envelope);
        _output.WriteLine($"(submitted: {id})");

        var resultPath = Path.Combine(_workspace.Results.FullName, id + ".txt");
        using var timeoutCts = new CancellationTokenSource(_options.ResultTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            var raw = await WaitForResultFileAsync(resultPath, linkedCts.Token).ConfigureAwait(false);
            PrintResult(id, raw);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _output.WriteLine($"(timeout after {_options.ResultTimeout.TotalSeconds:0}s — no result for {id})");
        }
    }

    /// <summary>
    /// Wait until <paramref name="resultPath"/> exists, then read and return its text. Combines
    /// a <see cref="FileSystemWatcher"/> on <c>results/</c> with a fallback poll loop. Debounces
    /// briefly after a signal so the writer can finish flushing.
    /// </summary>
    private async Task<string> WaitForResultFileAsync(string resultPath, CancellationToken ct)
    {
        // Arm the watcher BEFORE the post-arm existence probe to plug the race where the
        // executor writes the result between our submit and our subscribe.
        using var signal = new SemaphoreSlim(0, 1);
        using var fsw = new FileSystemWatcher(_workspace.Results.FullName)
        {
            Filter = Path.GetFileName(resultPath),
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
        };

        void Pulse(object _, FileSystemEventArgs __)
        {
            // SemaphoreSlim.Release throws if already at max — swallow that race.
            try { signal.Release(); }
            catch (SemaphoreFullException) { /* already pulsed; one is enough */ }
        }
        fsw.Created += Pulse;
        fsw.Changed += Pulse;
        fsw.Renamed += (s, e) => Pulse(s, e);

        // Post-arm probe handles the instant-responder case.
        if (File.Exists(resultPath))
        {
            return await ReadAfterDebounceAsync(resultPath, ct).ConfigureAwait(false);
        }

        while (!ct.IsCancellationRequested)
        {
            var pollTask = Task.Delay(_options.PollInterval, ct);
            var signalTask = signal.WaitAsync(ct);
            var winner = await Task.WhenAny(pollTask, signalTask).ConfigureAwait(false);
            // Surface cancellation from whichever inner task observed it.
            await winner.ConfigureAwait(false);

            if (File.Exists(resultPath))
            {
                return await ReadAfterDebounceAsync(resultPath, ct).ConfigureAwait(false);
            }
        }

        ct.ThrowIfCancellationRequested();
        throw new OperationCanceledException(ct);
    }

    /// <summary>Sleep for the configured debounce then read the file. Tolerant of in-flight writes.</summary>
    /// <remarks>
    /// The FSW often fires while the writer still owns the handle. We retry on
    /// <see cref="IOException"/> for a bounded number of attempts (~1 s total) using
    /// FileShare.ReadWrite so we don't block a concurrent writer.
    /// </remarks>
    private async Task<string> ReadAfterDebounceAsync(string path, CancellationToken ct)
    {
        if (_options.FileWriteDebounce > TimeSpan.Zero)
        {
            await Task.Delay(_options.FileWriteDebounce, ct).ConfigureAwait(false);
        }

        const int maxAttempts = 10;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            }
            catch (IOException) when (attempt < maxAttempts - 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
            }
        }

        // Last-ditch — let the exception surface naturally.
        return await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
    }

    /// <summary>Pretty-print a parsed result body, honouring markers from <see cref="ResultMarkers"/>.</summary>
    private void PrintResult(string taskId, string raw)
    {
        var parsed = ResultBody.Parse(raw);
        _output.WriteLine($"< {taskId}");

        if (parsed.AlreadyReplied)
        {
            _output.WriteLine("  marker: [REPLIED] — already delivered elsewhere, skipping body");
            return;
        }

        if (parsed.NoSend)
        {
            _output.WriteLine("  marker: [no-send] — skipping body");
            return;
        }

        if (parsed.DedupedTo is not null)
        {
            _output.WriteLine($"  deduped → {parsed.DedupedTo}");
        }

        foreach (var att in parsed.Attachments)
        {
            _output.WriteLine($"  attach: {att}");
        }

        if (parsed.Text.Length > 0)
        {
            _output.WriteLine(parsed.Text);
        }
    }

    /// <summary>Render <c>core-status.json</c> for the <c>:status</c> command.</summary>
    private void PrintStatus()
    {
        var payload = _coreStatus.Read();
        if (payload is null)
        {
            _output.WriteLine("core-status: (no signal — agent has not written yet)");
            return;
        }
        var ts = DateTimeOffset.FromUnixTimeSeconds(payload.Ts);
        var step = string.IsNullOrEmpty(payload.Step) ? string.Empty : $" — {payload.Step}";
        _output.WriteLine($"core-status: {payload.Status}{step}  ({ts.ToString("O", CultureInfo.InvariantCulture)})");
    }

    /// <summary>List pending task files for the <c>:tasks</c> command.</summary>
    private void PrintPendingTasks()
    {
        var dir = _workspace.Tasks;
        if (!dir.Exists)
        {
            _output.WriteLine("(no pending tasks)");
            return;
        }
        var pending = dir.EnumerateFiles("task-*.txt", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f.LastWriteTimeUtc)
            .ToList();
        if (pending.Count == 0)
        {
            _output.WriteLine("(no pending tasks)");
            return;
        }
        foreach (var f in pending)
        {
            _output.WriteLine($"  {f.Name}  ({f.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture)})");
        }
    }

    /// <summary>
    /// Read a line asynchronously while honouring cancellation. <see cref="TextReader.ReadLineAsync()"/>
    /// on stdin blocks past cancellation on every platform — we wrap it with a polling
    /// helper that lets Ctrl+C unblock the loop within roughly one poll interval.
    /// </summary>
    private static async Task<string?> ReadLineAsync(TextReader reader, CancellationToken ct)
    {
        // For non-console readers (StringReader in tests), ReadLineAsync returns immediately
        // and cancellation never trips. For Console.In on a real terminal, run the read on a
        // background task and let the caller's cancellation surface.
        var readTask = Task.Run(reader.ReadLineAsync, CancellationToken.None);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (ct.Register(() => tcs.TrySetResult(true)))
        {
            var winner = await Task.WhenAny(readTask, tcs.Task).ConfigureAwait(false);
            if (winner == readTask)
            {
                return await readTask.ConfigureAwait(false);
            }
        }
        // Cancellation won the race; surface it.
        ct.ThrowIfCancellationRequested();
        // Defensive: shouldn't get here.
        return await readTask.ConfigureAwait(false);
    }
}
