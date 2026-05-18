using System.Globalization;
using System.Text.Json;
using Sutando.Bridge;
using Sutando.Browser;
using Sutando.Channels.Cli;
using Sutando.Workspace;

namespace Sutando.Cli;

/// <summary>Implementations of the top-level subcommands. Each returns a process exit code.</summary>
internal static class Commands
{
    public static int Version(string version)
    {
        Console.WriteLine(version);
        return 0;
    }

    public static int Hello(string version, string[] args)
    {
        var who = args.Length > 1 ? string.Join(' ', args[1..]) : "world";
        Console.WriteLine($"hello, {who} — sutando {version} is alive.");
        return 0;
    }

    public static int WorkspaceInfo()
    {
        var ws = WorkspaceDirectory.Resolve();
        Console.WriteLine($"workspace:     {ws.Root.FullName}");
        Console.WriteLine($"tasks:         {ws.Tasks.FullName}");
        Console.WriteLine($"results:       {ws.Results.FullName}");
        Console.WriteLine($"state:         {ws.State.FullName}");
        Console.WriteLine($"core-status:   {ws.CoreStatusFile.FullName}");
        return 0;
    }

    public static async Task<int> WorkAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: sutando work <task description>");
            return 64;
        }
        var ws = WorkspaceDirectory.Resolve();
        var body = string.Join(' ', args[1..]);
        var id = $"task-chat-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

        var envelope = new TaskEnvelope
        {
            Id = id,
            Timestamp = DateTimeOffset.UtcNow,
            Body = body,
            Source = TaskSource.Chat,
            ChannelId = "local-chat",
            UserId = Environment.GetEnvironmentVariable("SUTANDO_DM_OWNER_ID") ?? "chat-local",
            AccessTier = AccessTier.Owner,
            Priority = TaskPriority.Normal,
        };
        var path = TaskFile.Write(ws.Tasks.FullName, envelope);
        Console.WriteLine($"task: {id}");
        Console.WriteLine($"path: {path}");
        await Task.CompletedTask.ConfigureAwait(false);
        return 0;
    }

    public static async Task<int> WatchAsync(string[] args)
    {
        _ = args;
        var ws = WorkspaceDirectory.Resolve();
        using var cts = NewSigIntCts();

        await using var watcher = new TaskWatcher(ws);
        watcher.Start();

        Console.WriteLine($"watching: {ws.Tasks.FullName} (Ctrl+C to stop)");
        try
        {
            await foreach (var envelope in watcher.Reader.ReadAllAsync(cts.Token).ConfigureAwait(false))
            {
                Console.WriteLine($"task: {envelope.Id} ({envelope.Source}, {envelope.Priority}) tier={envelope.AccessTier} user={envelope.UserId}");
                Console.WriteLine($"  body: {Truncate(envelope.Body, 200)}");
            }
        }
        catch (OperationCanceledException)
        {
            // expected on Ctrl+C
        }
        return 0;
    }

    public static async Task<int> ResultsAsync(string[] args)
    {
        if (args.Length < 2 || args[1] != "tail")
        {
            Console.Error.WriteLine("usage: sutando results tail");
            return 64;
        }
        var ws = WorkspaceDirectory.Resolve();
        using var cts = NewSigIntCts();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Console.WriteLine($"tailing: {ws.Results.FullName} (Ctrl+C to stop)");
        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                foreach (var f in ws.Results.EnumerateFiles("*.txt", SearchOption.TopDirectoryOnly).OrderBy(f => f.LastWriteTimeUtc))
                {
                    var key = f.FullName;
                    if (!seen.Add(key))
                    {
                        continue;
                    }
                    var body = await File.ReadAllTextAsync(f.FullName, cts.Token).ConfigureAwait(false);
                    var parsed = ResultBody.Parse(body);
                    Console.WriteLine($"-- {f.Name} --");
                    if (parsed.DedupedTo is not null)
                    {
                        Console.WriteLine($"  deduped → {parsed.DedupedTo}");
                    }
                    if (parsed.NoSend) { Console.WriteLine("  marker:[no-send]"); }
                    if (parsed.AlreadyReplied) { Console.WriteLine("  marker:[REPLIED]"); }
                    foreach (var att in parsed.Attachments)
                    {
                        Console.WriteLine($"  attach: {att}");
                    }
                    if (parsed.Text.Length > 0)
                    {
                        Console.WriteLine($"  {Truncate(parsed.Text, 500)}");
                    }
                }
                await Task.Delay(TimeSpan.FromMilliseconds(500), cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // expected on Ctrl+C
        }
        return 0;
    }

    public static int Status(string[] args)
    {
        var ws = WorkspaceDirectory.Resolve();
        var cs = new CoreStatus(ws);
        var watch = args.Contains("--watch");

        do
        {
            var payload = cs.Read();
            if (payload is null)
            {
                Console.WriteLine("core-status: (no signal — agent has not written yet)");
            }
            else
            {
                var ts = DateTimeOffset.FromUnixTimeSeconds(payload.Ts);
                var step = payload.Step is { Length: > 0 } ? $" — {payload.Step}" : string.Empty;
                Console.WriteLine($"core-status: {payload.Status}{step}  ({ts:O})");
            }
            if (watch)
            {
                Thread.Sleep(TimeSpan.FromSeconds(2));
            }
        } while (watch);

        return 0;
    }

    public static async Task<int> HeartbeatAsync(string[] args)
    {
        var ws = WorkspaceDirectory.Resolve();
        var loop = args.Contains("--start");

        if (!loop)
        {
            using var hb = new CoreHeartbeat(ws);
            hb.WriteOnce();
            var payload = hb.Read();
            Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        await using var heartbeat = new CoreHeartbeat(ws);
        heartbeat.Start();
        Console.WriteLine($"heartbeat: writing to {heartbeat.AliveFile.FullName} every {CoreHeartbeat.DefaultBeatInterval} (Ctrl+C to stop)");
        using var cts = NewSigIntCts();
        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected
        }
        return 0;
    }

    public static async Task<int> ChatAsync(string version, string[] args)
    {
        var ws = WorkspaceDirectory.Resolve();

        // --timeout <seconds> overrides the default 5-minute result wait.
        var timeout = TimeSpan.FromMinutes(5);
        for (var i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == "--timeout"
                && double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var secs)
                && secs > 0)
            {
                timeout = TimeSpan.FromSeconds(secs);
                break;
            }
        }

        var channel = new CliChatChannel(ws, new CliChatChannelOptions
        {
            Version = version,
            ResultTimeout = timeout,
        });
        using var cts = NewSigIntCts();
        await channel.RunAsync(cts.Token).ConfigureAwait(false);
        return 0;
    }

    public static async Task<int> BrowserAsync(string[] args)
    {
        // Drop the leading "browser" verb before forwarding to the shim.
        var forwarded = args.Length > 1 ? args[1..] : [];
        using var cts = NewSigIntCts();
        return await BrowserCommand.RunAsync(forwarded, options: null, ct: cts.Token).ConfigureAwait(false);
    }

    private static CancellationTokenSource NewSigIntCts()
    {
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        return cts;
    }

    private static string Truncate(string s, int max)
    {
        if (s.Length <= max)
        {
            return s.Replace('\n', ' ');
        }
        return s[..max].Replace('\n', ' ') + "…";
    }
}
