using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sutando.Api;
using Sutando.Bridge;
using Sutando.Browser;
using Sutando.Channels.Cli;
using Sutando.Channels.Discord;
using Sutando.Channels.Telegram;
using Sutando.Cli.Skills;
using Sutando.Dashboard;
using Sutando.Phone;
using Sutando.Proactive;
using Sutando.Skills;
using Sutando.Skills.Discovery;
using Sutando.Voice;
using Sutando.Voice.Skills;
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

    public static async Task<int> InitAsync(string[] args)
    {
        var options = new WorkspaceInitializerOptions
        {
            AssumeYes = args.Contains("--yes") || args.Contains("-y"),
            WriteHeartbeat = !args.Contains("--no-heartbeat"),
            LaunchDashboard = args.Contains("--launch-dashboard"),
        };

        var initializer = new WorkspaceInitializer();
        using var cts = NewSigIntCts();
        var (exit, _) = await initializer.RunAsync(options, cts.Token).ConfigureAwait(false);
        return exit;
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

    public static async Task<int> TelegramAsync(string[] args)
    {
        _ = args;
        var token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.Error.WriteLine("sutando telegram: TELEGRAM_BOT_TOKEN env var is required.");
            return 64;
        }
        var options = new TelegramChannelOptions
        {
            BotToken = token,
            OwnerUserId = ParseLong(Environment.GetEnvironmentVariable("TELEGRAM_OWNER_USER_ID")),
            VerifiedUserIds = ParseLongList(Environment.GetEnvironmentVariable("TELEGRAM_VERIFIED_USER_IDS")),
            TeamUserIds = ParseLongList(Environment.GetEnvironmentVariable("TELEGRAM_TEAM_USER_IDS")),
        };
        var ws = WorkspaceDirectory.Resolve();
        var channel = new TelegramChannel(ws, options);
        using var cts = NewSigIntCts();
        Console.WriteLine($"sutando telegram: starting (owner={options.OwnerUserId}, verified={options.VerifiedUserIds.Count}, team={options.TeamUserIds.Count}). Ctrl+C to stop.");
        await channel.RunAsync(cts.Token).ConfigureAwait(false);
        return 0;
    }

    public static async Task<int> ApiAsync(string[] args)
    {
        // Drop the leading "api" verb before forwarding to the host shim.
        var forwarded = args.Length > 1 ? args[1..] : [];
        using var cts = NewSigIntCts();
        await ApiCommand.RunAsync(forwarded, cts.Token).ConfigureAwait(false);
        return 0;
    }

    public static async Task<int> DashboardAsync(string[] args)
    {
        var forwarded = args.Length > 1 ? args[1..] : [];
        using var cts = NewSigIntCts();
        await DashboardCommand.RunAsync(forwarded, cts.Token).ConfigureAwait(false);
        return 0;
    }

    public static async Task<int> VoiceAsync(string[] args)
    {
        var forwarded = args.Length > 1 ? args[1..] : [];

        // --no-skills opts out of the SkillRegistry → voice-tool bridge. Default is on so cloud +
        // notes skills are reachable from voice without explicit configuration. --skills is accepted
        // for symmetry but the default already enables it.
        var skillsEnabled = !forwarded.Contains("--no-skills");
        var argsForVoiceServer = forwarded.Where(a => a is not "--skills" and not "--no-skills").ToArray();

        SkillRegistryVoiceBridge? bridge = null;
        if (skillsEnabled)
        {
            var ws = WorkspaceDirectory.Resolve();
            var (registry, report) = SkillsHost.BuildRegistry(ws);
            bridge = new SkillRegistryVoiceBridge(registry, ws);
            Console.WriteLine($"sutando voice: skill bridge enabled — {bridge.Count} tools ({report.DiskIds.Count} disk + {report.CloudIds.Count} cloud + {report.NotesIds.Count} notes). Pass --no-skills to disable.");
        }
        else
        {
            Console.WriteLine("sutando voice: skill bridge disabled (--no-skills).");
        }

        using var cts = NewSigIntCts();
        return await VoiceCommand.RunAsync(argsForVoiceServer, bridge, cts.Token).ConfigureAwait(false);
    }

    public static async Task<int> PhoneAsync(string[] args)
    {
        var forwarded = args.Length > 1 ? args[1..] : [];

        // Same --skills/--no-skills semantics as `sutando voice` — default on, opt out with
        // --no-skills. Mirrors upstream conversation-server.ts:587 (phone agent inherits the
        // voice agent's inline-tool surface).
        var skillsEnabled = !forwarded.Contains("--no-skills");
        var argsForPhoneServer = forwarded.Where(a => a is not "--skills" and not "--no-skills").ToArray();

        PhoneSkillBridge? bridge = null;
        if (skillsEnabled)
        {
            var ws = WorkspaceDirectory.Resolve();
            var (registry, report) = SkillsHost.BuildRegistry(ws);
            var voiceBridge = new SkillRegistryVoiceBridge(registry, ws);
            bridge = new PhoneSkillBridge(voiceBridge.GetToolDefinitions(), voiceBridge.RegisterWith);
            Console.WriteLine($"sutando phone: skill bridge enabled — {voiceBridge.Count} tools ({report.DiskIds.Count} disk + {report.CloudIds.Count} cloud + {report.NotesIds.Count} notes). Pass --no-skills to disable.");
        }
        else
        {
            Console.WriteLine("sutando phone: skill bridge disabled (--no-skills).");
        }

        using var cts = NewSigIntCts();
        return await PhoneCommand.RunAsync(argsForPhoneServer, bridge, cts.Token).ConfigureAwait(false);
    }

    public static async Task<int> DiscordAsync(string[] args)
    {
        _ = args;
        var token = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.Error.WriteLine("sutando discord: DISCORD_BOT_TOKEN env var is required.");
            return 64;
        }
        var options = new DiscordChannelOptions
        {
            BotToken = token,
            OwnerUserId = ParseUlong(Environment.GetEnvironmentVariable("DISCORD_OWNER_USER_ID")),
            TeamRoleIds = ParseUlongList(Environment.GetEnvironmentVariable("DISCORD_TEAM_ROLE_IDS")),
            AllowedChannelIds = ParseUlongList(Environment.GetEnvironmentVariable("DISCORD_ALLOWED_CHANNEL_IDS")),
        };
        var ws = WorkspaceDirectory.Resolve();
        var channel = new DiscordChannel(ws, options);
        using var cts = NewSigIntCts();
        Console.WriteLine($"sutando discord: starting (owner={options.OwnerUserId}, team-roles={options.TeamRoleIds.Count}, channels={options.AllowedChannelIds.Count}). Ctrl+C to stop.");
        await channel.RunAsync(cts.Token).ConfigureAwait(false);
        return 0;
    }

    public static async Task<int> ProactiveAsync(string[] args)
    {
        var sub = args.Length > 1 ? args[1] : "run";

        if (sub is "run" or "start")
        {
            return await ProactiveRunAsync().ConfigureAwait(false);
        }

        Console.Error.WriteLine("usage: sutando proactive [run]");
        Console.Error.WriteLine("       Starts the proactive background service. The default IProactivePass is");
        Console.Error.WriteLine("       NoopProactivePass; host implementations register their own via DI.");
        return 64;
    }

    private static async Task<int> ProactiveRunAsync()
    {
        var ws = WorkspaceDirectory.Resolve();
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(ws);
        builder.Services.AddSutandoProactive();
        // IProactivePass defaults to NoopProactivePass via AddSutandoProactive; the host owns
        // overrides. This verb intentionally keeps the body trivial so the operator can verify
        // the scheduling chassis without wiring an executor first.

        using var host = builder.Build();
        using var cts = NewSigIntCts();

        var cronCount = new CronConfigLoader().Load(ws).Count;
        Console.WriteLine($"sutando proactive: starting (workspace={ws.Root.FullName}, crons={cronCount}). Default pass is NoopProactivePass — register IProactivePass in DI to customise. Ctrl+C to stop.");

        try
        {
            await host.RunAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected on Ctrl+C
        }
        return 0;
    }

    public static int Cron(string[] args)
    {
        var sub = args.Length > 1 ? args[1] : "list";

        if (sub is "list" or "ls")
        {
            return CronList();
        }

        Console.Error.WriteLine("usage: sutando cron list");
        Console.Error.WriteLine("       Prints entries from <workspace>/crons.json (or crons.example.json fallback).");
        Console.Error.WriteLine("       To add/remove entries, edit <workspace>/crons.json directly — the file");
        Console.Error.WriteLine("       shape matches upstream's `skills/schedule-crons/crons.json`.");
        return 64;
    }

    private static int CronList()
    {
        var ws = WorkspaceDirectory.Resolve();
        var entries = new CronConfigLoader().Load(ws);

        if (entries.Count == 0)
        {
            Console.WriteLine($"(no cron entries — looked at {Path.Combine(ws.Root.FullName, CronConfigLoader.PrimaryFileName)} and {Path.Combine(ws.Root.FullName, CronConfigLoader.FallbackFileName)})");
            return 0;
        }

        var nameWidth = Math.Max(4, entries.Max(e => (e.Name ?? string.Empty).Length));
        var cronWidth = Math.Max(8, entries.Max(e => (e.Cron ?? string.Empty).Length));

        Console.WriteLine($"{"NAME".PadRight(nameWidth)}  {"CRON".PadRight(cronWidth)}  TARGET");
        Console.WriteLine(new string('-', nameWidth + cronWidth + 8 + 24));

        foreach (var e in entries.OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            var target = !string.IsNullOrEmpty(e.PromptSkill)
                ? $"skill:{e.PromptSkill}"
                : Truncate(e.Prompt ?? string.Empty, 60);
            Console.WriteLine($"{(e.Name ?? string.Empty).PadRight(nameWidth)}  {(e.Cron ?? string.Empty).PadRight(cronWidth)}  {target}");
        }

        return 0;
    }

    public static async Task<int> SkillsAsync(string[] args)
    {
        // Dispatch on the subverb: "list" or "run".
        var sub = args.Length > 1 ? args[1] : string.Empty;

        if (sub is "list")
        {
            return SkillsList(args);
        }

        if (sub is "run")
        {
            return await SkillsRunAsync(args).ConfigureAwait(false);
        }

        Console.Error.WriteLine("usage: sutando skills list");
        Console.Error.WriteLine("       sutando skills run <id> [--arg key=value ...]");
        return 64;
    }

    private static int SkillsList(string[] args)
    {
        _ = args;
        var ws = WorkspaceDirectory.Resolve();
        var (registry, _) = SkillsHost.BuildRegistry(ws);

        var manifests = registry.Manifests;
        if (manifests.Count == 0)
        {
            Console.WriteLine("(no skills registered — set credential env vars for cloud skills or add scripts to <workspace>/skills/)");
            return 0;
        }

        // Compute column widths for aligned output.
        const int MinIdWidth = 2;
        const int MinRuntimeWidth = 7;

        var idWidth = Math.Max(MinIdWidth, manifests.Max(m => m.Id.Length));
        var runtimeWidth = Math.Max(MinRuntimeWidth, manifests.Max(m => m.Runtime.ToString().Length));

        Console.WriteLine($"{"ID".PadRight(idWidth)}  {"RUNTIME".PadRight(runtimeWidth)}  {"TRIGGERS".PadRight(16)}  DESCRIPTION");
        Console.WriteLine(new string('-', idWidth + runtimeWidth + 16 + 6 + 16));

        foreach (var m in manifests.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            var triggers = m.Triggers.Count > 0 ? string.Join(", ", m.Triggers) : "-";
            var desc = m.Description.Length > 0 ? m.Description : m.Name;
            Console.WriteLine(
                $"{m.Id.PadRight(idWidth)}  {m.Runtime.ToString().PadRight(runtimeWidth)}  {triggers.PadRight(16)}  {desc}");
        }

        return 0;
    }

    private static async Task<int> SkillsRunAsync(string[] args)
    {
        // args[0] = "skills", args[1] = "run", args[2] = <id>, then optional --arg key=value pairs.
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: sutando skills run <id> [--arg key=value ...]");
            return 64;
        }

        var id = args[2];

        // Parse --arg key=value entries from the remainder of the arg list.
        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 3; i < args.Length; i++)
        {
            if (args[i] != "--arg")
            {
                Console.Error.WriteLine($"sutando skills run: unexpected argument '{args[i]}'; expected --arg key=value");
                return 64;
            }
            if (i + 1 >= args.Length)
            {
                Console.Error.WriteLine("sutando skills run: --arg requires a key=value operand");
                return 64;
            }
            i++;
            var pair = args[i];
            // Split on the first '=' only so values may contain '='.
            var sep = pair.IndexOf('=', StringComparison.Ordinal);
            if (sep <= 0)
            {
                Console.Error.WriteLine($"sutando skills run: malformed --arg value '{pair}' (expected key=value)");
                return 64;
            }
            arguments[pair[..sep]] = pair[(sep + 1)..];
        }

        var ws = WorkspaceDirectory.Resolve();
        // Perform the two-step build directly so we have access to each DiscoveredSkill.Root,
        // which gives script-based skills the correct filesystem base for relative asset paths.
        var discovered = SkillDiscovery.Default(ws).Discover();
        var (registry, _) = SkillsHost.BuildRegistry(ws);
        // Build an id→root lookup from disk-discovered skills.  Cloud skills fall back to the
        // host base directory (consistent with SkillRegistry.RegisterInstance default).
        var rootById = discovered.ToDictionary(d => d.Manifest.Id, d => d.Root, StringComparer.Ordinal);

        var skill = registry.TryGet(id);
        if (skill is null)
        {
            Console.Error.WriteLine($"sutando skills run: skill '{id}' is not registered.");
            return 1;
        }

        var skillRoot = rootById.TryGetValue(id, out var diskRoot)
            ? diskRoot
            : AppContext.BaseDirectory;

        using var cts = NewSigIntCts();
        var ctx = new SkillContext(ws, skillRoot);
        SkillResult result;
        try
        {
            result = await skill.ExecuteAsync(ctx, arguments, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"sutando skills run: {ex.Message}");
            return 1;
        }

        if (!result.Success)
        {
            Console.Error.WriteLine(result.Error ?? result.Body);
            return 1;
        }

        Console.WriteLine(result.Body);
        foreach (var artifact in result.Artifacts)
        {
            Console.WriteLine(artifact);
        }
        return 0;
    }

    private static long? ParseLong(string? raw) =>
        long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static ulong? ParseUlong(string? raw) =>
        ulong.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static IReadOnlyList<long> ParseLongList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) { return []; }
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => ParseLong(s))
            .Where(v => v is not null)
            .Select(v => v!.Value)
            .ToArray();
    }

    private static IReadOnlyList<ulong> ParseUlongList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) { return []; }
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => ParseUlong(s))
            .Where(v => v is not null)
            .Select(v => v!.Value)
            .ToArray();
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
