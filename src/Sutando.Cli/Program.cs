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
    Console.WriteLine("  work <task>               Submit a chat-source task into the workspace bridge.");
    Console.WriteLine("  watch                     Watch tasks/ for new envelopes and print them as they arrive.");
    Console.WriteLine("  results tail              Tail results/ and print new entries.");
    Console.WriteLine("  status [--watch]          Show the current core-status.json signal.");
    Console.WriteLine("  heartbeat [--start]       Write a single heartbeat (or run the loop until Ctrl+C).");
    Console.WriteLine("  help                      Show this message.");
    Console.WriteLine();
    Console.WriteLine("Workspace: $SUTANDO_WORKSPACE overrides the default '~/.sutando/workspace/'.");
}
