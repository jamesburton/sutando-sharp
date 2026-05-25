// Sutando — entry point.
//
// The CLI is a thin dispatch table. Each subcommand is one function: keep the surface
// area discoverable from `--help` and keep the binary lean for dnx ergonomics.

using System.Reflection;
using Sutando.Bridge;
using Sutando.Cli;
using Sutando.Workspace;

var version = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
    ?? "unknown";

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintHelp(version);
    return 0;
}

try
{
    return args[0] switch
    {
        "version" or "--version" or "-v" => Commands.Version(version),
        "hello" => Commands.Hello(version, args),
        "work" => await Commands.WorkAsync(args).ConfigureAwait(false),
        "watch" => await Commands.WatchAsync(args).ConfigureAwait(false),
        "results" => await Commands.ResultsAsync(args).ConfigureAwait(false),
        "status" => Commands.Status(args),
        "heartbeat" => await Commands.HeartbeatAsync(args).ConfigureAwait(false),
        "workspace" => Commands.WorkspaceInfo(),
        "init" => await Commands.InitAsync(args).ConfigureAwait(false),
        "chat" => await Commands.ChatAsync(version, args).ConfigureAwait(false),
        "browser" => await Commands.BrowserAsync(args).ConfigureAwait(false),
        "telegram" => await Commands.TelegramAsync(args).ConfigureAwait(false),
        "discord" => await Commands.DiscordAsync(args).ConfigureAwait(false),
        "voice" => await Commands.VoiceAsync(args).ConfigureAwait(false),
        "phone" => await Commands.PhoneAsync(args).ConfigureAwait(false),
        "api" => await Commands.ApiAsync(args).ConfigureAwait(false),
        "dashboard" => await Commands.DashboardAsync(args).ConfigureAwait(false),
        "skills" => await Commands.SkillsAsync(args).ConfigureAwait(false),
        "proactive" => await Commands.ProactiveAsync(args).ConfigureAwait(false),
        "cron" => Commands.Cron(args),
        _ => Unknown(args[0]),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"sutando: {ex.Message}");
    return 1;
}

static int Unknown(string cmd)
{
    Console.Error.WriteLine($"sutando: unknown command '{cmd}'. Try 'sutando help'.");
    return 64; // EX_USAGE
}

static void PrintHelp(string version)
{
    Console.WriteLine($"sutando {version}");
    Console.WriteLine();
    Console.WriteLine("USAGE");
    Console.WriteLine("  sutando <command> [options]");
    Console.WriteLine();
    Console.WriteLine("COMMANDS");
    Console.WriteLine("  hello [name]              Print a greeting (used to verify the dnx round-trip).");
    Console.WriteLine("  version                   Print the running version and exit.");
    Console.WriteLine("  workspace                 Print the resolved workspace directory and key paths.");
    Console.WriteLine("  init [--yes] [--launch-dashboard]");
    Console.WriteLine("                            Bootstrap a fresh workspace: create subdirs, write .env.example, probe prerequisites.");
    Console.WriteLine("  work <task>               Submit a chat-source task into the workspace bridge.");
    Console.WriteLine("  watch                     Watch tasks/ for new envelopes and print them as they arrive.");
    Console.WriteLine("  results tail              Tail results/ and print new entries.");
    Console.WriteLine("  chat [--timeout <s>]      Interactive REPL: send chat tasks and wait for results.");
    Console.WriteLine("  browser <url> [actions]   Drive a Playwright browser session (navigate, click, fill, screenshot, ...).");
    Console.WriteLine("  telegram                  Run the Telegram channel (reads TELEGRAM_BOT_TOKEN + allow-lists from env).");
    Console.WriteLine("  discord                   Run the Discord channel (reads DISCORD_BOT_TOKEN + allow-lists from env).");
    Console.WriteLine("  voice [--local]           Run the voice WebSocket server on :9900. Default uses Gemini Live (needs");
    Console.WriteLine("                            GEMINI_VOICE_API_KEY or GEMINI_API_KEY); --local runs the in-process");
    Console.WriteLine("                            STT/Chat/TTS pipeline (needs SUTANDO_WHISPER_MODEL / SUTANDO_LLAMA_MODEL /");
    Console.WriteLine("                            SUTANDO_KOKORO_MODEL model files).");
    Console.WriteLine("  phone                     Run the Twilio phone bridge on :3100 (needs TWILIO_AUTH_TOKEN; Media Streams → Gemini Live).");
    Console.WriteLine("  api                       Run the HTTP task-submission API on :7843 (bearer auth via SUTANDO_API_TOKEN).");
    Console.WriteLine("  dashboard                 Run the read-only status dashboard on :7844 (SignalR live updates).");
    Console.WriteLine("  status [--watch]          Show the current core-status.json signal.");
    Console.WriteLine("  heartbeat [--start]       Write a single heartbeat (or run the loop until Ctrl+C).");
    Console.WriteLine("  skills list               List all registered skills (id, runtime, triggers, description).");
    Console.WriteLine("  skills run <id> [--arg k=v ...]");
    Console.WriteLine("                            Invoke a skill by id with optional key=value arguments. Exits 0 on");
    Console.WriteLine("                            success (body + artifact paths to stdout) or 1 on failure (error to stderr).");
    Console.WriteLine("  proactive [run]           Start the proactive background service (cron scheduler + per-pass dispatch).");
    Console.WriteLine("                            Default pass is NoopProactivePass — host implementations register");
    Console.WriteLine("                            their own IProactivePass via DI.");
    Console.WriteLine("  cron list                 Print entries from <workspace>/crons.json (mirrors upstream's");
    Console.WriteLine("                            skills/schedule-crons/crons.json shape).");
    Console.WriteLine("  help                      Show this message.");
    Console.WriteLine();
    Console.WriteLine("Workspace: $SUTANDO_WORKSPACE overrides the default '~/.sutando/workspace/'.");
}
