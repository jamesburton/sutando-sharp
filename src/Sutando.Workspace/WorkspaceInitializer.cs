using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Sutando.Workspace;

/// <summary>
/// Probe abstraction for prerequisite checks. Extracted so tests can supply a fake without
/// touching $PATH or making real network calls.
/// </summary>
public interface IPrerequisiteProbe
{
    /// <summary>Returns <see langword="true"/> if <paramref name="binary"/> resolves on <c>$PATH</c>.</summary>
    bool BinaryOnPath(string binary);

    /// <summary>HEAD <paramref name="url"/> with the given <paramref name="timeout"/>; success on any 2xx/3xx.</summary>
    Task<bool> ReachableAsync(string url, TimeSpan timeout, CancellationToken ct);
}

/// <summary>Default <see cref="IPrerequisiteProbe"/> — scans <c>$PATH</c> and uses <see cref="HttpClient"/>.</summary>
public sealed class DefaultPrerequisiteProbe : IPrerequisiteProbe
{
    private static readonly char PathSeparator = Path.PathSeparator;

    /// <inheritdoc/>
    public bool BinaryOnPath(string binary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binary);
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        // On Windows, executables need an extension matched against PATHEXT; on Unix the binary
        // is itself the file name. Probe both shapes to keep this cross-platform.
        var pathExt = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [string.Empty];

        foreach (var dir in path.Split(PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var ext in pathExt)
            {
                var candidate = Path.Combine(dir, binary + ext);
                if (File.Exists(candidate))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <inheritdoc/>
    public async Task<bool> ReachableAsync(string url, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        using var http = new HttpClient { Timeout = timeout };
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            // Any response — even 401/403 — proves the host answered. Treat connect failure as
            // the only "unreachable" signal.
            return (int)resp.StatusCode < 500;
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) { return false; }
    }
}

/// <summary>Result of a single prerequisite probe — surfaces to the operator on the init checklist.</summary>
public sealed record PrerequisiteCheck(string Label, bool Passed, string Detail);

/// <summary>Outcome of an <see cref="WorkspaceInitializer.RunAsync"/> invocation. Used by tests + by the CLI for exit codes.</summary>
public sealed record InitializationResult
{
    /// <summary>The resolved workspace root.</summary>
    public required DirectoryInfo Workspace { get; init; }

    /// <summary>All subdirectories that were created (or already present) under the workspace.</summary>
    public required IReadOnlyList<DirectoryInfo> Subdirectories { get; init; }

    /// <summary>The path to the <c>.env.example</c> file emitted alongside the workspace.</summary>
    public required FileInfo EnvExampleFile { get; init; }

    /// <summary>True if the <c>.env.example</c> was newly written (false if it already existed and we left it alone).</summary>
    public required bool EnvExampleWritten { get; init; }

    /// <summary>The prerequisite checks performed, in display order.</summary>
    public required IReadOnlyList<PrerequisiteCheck> Checks { get; init; }

    /// <summary>True if a heartbeat baseline was written into <c>state/cores/</c>.</summary>
    public bool HeartbeatWritten { get; init; }
}

/// <summary>
/// Options controlling <see cref="WorkspaceInitializer.RunAsync"/>. Carry user-visible CLI
/// flags into the orchestration layer in a single bundle.
/// </summary>
public sealed record WorkspaceInitializerOptions
{
    /// <summary>Skip the interactive y/N confirmation prompt.</summary>
    public bool AssumeYes { get; init; }

    /// <summary>Write a single heartbeat into <c>state/cores/</c> as a writability proof.</summary>
    public bool WriteHeartbeat { get; init; } = true;

    /// <summary>Launch the dashboard as a child process at the end of initialisation.</summary>
    public bool LaunchDashboard { get; init; }

    /// <summary>Override the network-reachability probe (host + timeout). Tests use this to skip the real probe.</summary>
    public string? NetworkProbeUrl { get; init; } = "https://api.anthropic.com/";

    /// <summary>Override the resolved workspace location. When null the standard resolution rules apply.</summary>
    public string? WorkspaceOverride { get; init; }
}

/// <summary>
/// Orchestrates <c>sutando init</c>: create the workspace + standard subdirectories, write
/// a <c>.env.example</c>, probe the host for prerequisites, optionally write a heartbeat
/// baseline + launch the dashboard.
/// </summary>
public sealed class WorkspaceInitializer
{
    /// <summary>Subdirectory names created under the workspace root.</summary>
    public static readonly string[] StandardSubdirs =
    [
        "tasks", "results", "state", "state/cores", "notes", "data", "logs",
    ];

    /// <summary>
    /// Env vars sutando recognises. Each entry becomes a <c>VAR=</c> line in <c>.env.example</c>
    /// with a one-line description so a first-time user knows what the value means.
    /// </summary>
    public static readonly (string Name, string Description)[] KnownEnvVars =
    [
        ("SUTANDO_WORKSPACE", "Overrides the default ~/.sutando/workspace/ location."),
        ("SUTANDO_API_TOKEN", "Bearer token for `sutando api`. Unset = open API + warning on startup."),
        ("SUTANDO_API_PORT", "Override the HTTP API port (default 7843)."),
        ("SUTANDO_DASHBOARD_PORT", "Override the dashboard port (default 7844)."),
        ("SUTANDO_VOICE_PORT", "Override the voice WS port (default 9900)."),
        ("SUTANDO_DM_OWNER_ID", "Optional user-id override for the local chat REPL."),
        ("ANTHROPIC_API_KEY", "Anthropic API key for the HTTP executor (or Claude Code CLI fallback)."),
        ("GEMINI_API_KEY", "Google Gemini API key — used by the realtime voice transport when GEMINI_VOICE_API_KEY is unset."),
        ("GEMINI_VOICE_API_KEY", "Dedicated Gemini Live key for `sutando voice`."),
        ("TELEGRAM_BOT_TOKEN", "Required by `sutando telegram`."),
        ("TELEGRAM_OWNER_USER_ID", "Owner Telegram user id (gets owner tier)."),
        ("TELEGRAM_VERIFIED_USER_IDS", "Comma-separated verified user ids."),
        ("TELEGRAM_TEAM_USER_IDS", "Comma-separated team user ids."),
        ("DISCORD_BOT_TOKEN", "Required by `sutando discord`."),
        ("DISCORD_OWNER_USER_ID", "Owner Discord user id."),
        ("DISCORD_TEAM_ROLE_IDS", "Comma-separated team role ids."),
        ("DISCORD_ALLOWED_CHANNEL_IDS", "Comma-separated allow-list of channel ids."),
    ];

    private readonly IPrerequisiteProbe _probe;
    private readonly TextWriter _out;
    private readonly TextWriter _err;
    private readonly Func<string?> _readLine;
    private readonly Action<string, string[]>? _launchDashboard;

    /// <param name="probe">Prerequisite probe (binary lookup + network reachability).</param>
    /// <param name="output">Stdout sink. Defaults to <see cref="Console.Out"/>.</param>
    /// <param name="error">Stderr sink. Defaults to <see cref="Console.Error"/>.</param>
    /// <param name="readLine">Used for interactive confirmation; defaults to <see cref="Console.ReadLine"/>.</param>
    /// <param name="launchDashboard">
    /// Hook used to spawn the dashboard child process. Defaults to spawning the host binary;
    /// tests pass a no-op to keep the test deterministic.
    /// </param>
    public WorkspaceInitializer(
        IPrerequisiteProbe? probe = null,
        TextWriter? output = null,
        TextWriter? error = null,
        Func<string?>? readLine = null,
        Action<string, string[]>? launchDashboard = null)
    {
        _probe = probe ?? new DefaultPrerequisiteProbe();
        _out = output ?? Console.Out;
        _err = error ?? Console.Error;
        _readLine = readLine ?? Console.ReadLine;
        _launchDashboard = launchDashboard;
    }

    /// <summary>
    /// Run the full init flow. Returns the resulting <see cref="InitializationResult"/> and
    /// an exit code (0 on success, non-zero only when the user declined confirmation).
    /// </summary>
    public async Task<(int ExitCode, InitializationResult Result)> RunAsync(
        WorkspaceInitializerOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Resolve the workspace path lazily so the override doesn't need to touch SUTANDO_WORKSPACE.
        var workspaceRoot = options.WorkspaceOverride is not null
            ? new DirectoryInfo(options.WorkspaceOverride)
            : new DirectoryInfo(WorkspaceDirectory.ResolvePath());

        _out.WriteLine($"sutando init will set up:");
        _out.WriteLine($"  workspace: {workspaceRoot.FullName}");
        _out.WriteLine($"  subdirs:   {string.Join(", ", StandardSubdirs)}");
        _out.WriteLine($"  .env.example with {KnownEnvVars.Length} placeholders");
        if (options.WriteHeartbeat)
        {
            _out.WriteLine($"  heartbeat baseline in state/cores/");
        }
        if (options.LaunchDashboard)
        {
            _out.WriteLine($"  launch the dashboard after init");
        }

        if (!options.AssumeYes && !Confirm())
        {
            _out.WriteLine("aborted.");
            var aborted = new InitializationResult
            {
                Workspace = workspaceRoot,
                Subdirectories = [],
                EnvExampleFile = new FileInfo(Path.Combine(workspaceRoot.FullName, ".env.example")),
                EnvExampleWritten = false,
                Checks = [],
            };
            return (1, aborted);
        }

        var subdirs = CreateSubdirectories(workspaceRoot);
        var (envExample, envWritten) = WriteEnvExample(workspaceRoot);
        var heartbeatWritten = options.WriteHeartbeat && WriteHeartbeatBaseline(workspaceRoot);
        var checks = await RunChecksAsync(workspaceRoot, options.NetworkProbeUrl, ct).ConfigureAwait(false);
        PrintChecklist(checks);

        if (options.LaunchDashboard)
        {
            LaunchDashboard();
        }

        return (0, new InitializationResult
        {
            Workspace = workspaceRoot,
            Subdirectories = subdirs,
            EnvExampleFile = envExample,
            EnvExampleWritten = envWritten,
            Checks = checks,
            HeartbeatWritten = heartbeatWritten,
        });
    }

    /// <summary>Create each <see cref="StandardSubdirs"/> entry under the workspace. Idempotent.</summary>
    public static IReadOnlyList<DirectoryInfo> CreateSubdirectories(DirectoryInfo workspaceRoot)
    {
        ArgumentNullException.ThrowIfNull(workspaceRoot);
        workspaceRoot.Create();
        var created = new List<DirectoryInfo>(StandardSubdirs.Length);
        foreach (var sub in StandardSubdirs)
        {
            // Path.Combine handles the "state/cores" nested case correctly.
            var dir = new DirectoryInfo(Path.Combine(workspaceRoot.FullName, sub.Replace('/', Path.DirectorySeparatorChar)));
            dir.Create();
            created.Add(dir);
        }
        return created;
    }

    /// <summary>
    /// Write <c>&lt;workspace&gt;/.env.example</c>. Does NOT overwrite an existing file —
    /// the operator may have hand-edited it. Returns the file info + whether it was written.
    /// </summary>
    public static (FileInfo File, bool Written) WriteEnvExample(DirectoryInfo workspaceRoot)
    {
        ArgumentNullException.ThrowIfNull(workspaceRoot);
        var path = Path.Combine(workspaceRoot.FullName, ".env.example");
        var file = new FileInfo(path);
        if (file.Exists)
        {
            return (file, false);
        }

        var sb = new StringBuilder();
        sb.AppendLine("# sutando — environment variable placeholders.");
        sb.AppendLine("# Copy this file to `.env` and fill in the values you actually need.");
        sb.AppendLine("# Sutando reads from process environment, so any standard .env loader works.");
        sb.AppendLine();
        foreach (var (name, description) in KnownEnvVars)
        {
            sb.Append("# ").AppendLine(description);
            sb.Append(name).AppendLine("=");
            sb.AppendLine();
        }
        File.WriteAllText(path, sb.ToString());
        file.Refresh();
        return (file, true);
    }

    /// <summary>
    /// Write a one-shot heartbeat into <c>state/cores/&lt;host&gt;.alive</c> — proves the
    /// workspace is writable and gives a fresh signal for any process that polls it.
    /// </summary>
    /// <remarks>
    /// We instantiate <see cref="CoreHeartbeat"/> and call <see cref="CoreHeartbeat.WriteOnce"/>
    /// but deliberately skip <c>Dispose()</c>: that method removes the alive file as a "core
    /// went offline" signal, which would defeat the purpose here. The heartbeat process is
    /// short — the file is closed inside <c>WriteOnce</c> — so leaking the wrapper is harmless.
    /// </remarks>
    public static bool WriteHeartbeatBaseline(DirectoryInfo workspaceRoot)
    {
        ArgumentNullException.ThrowIfNull(workspaceRoot);
        try
        {
            var previous = Environment.GetEnvironmentVariable(WorkspaceDirectory.EnvVar);
            try
            {
                Environment.SetEnvironmentVariable(WorkspaceDirectory.EnvVar, workspaceRoot.FullName);
                var ws = WorkspaceDirectory.Resolve();
                var hb = new CoreHeartbeat(ws);
                hb.WriteOnce();
                // Intentionally do NOT dispose: Dispose() removes the alive file.
                return File.Exists(hb.AliveFile.FullName);
            }
            finally
            {
                Environment.SetEnvironmentVariable(WorkspaceDirectory.EnvVar, previous);
            }
        }
        catch (IOException)
        {
            // Heartbeat failure isn't fatal — the rest of init has already proved the directory
            // is writable. We just surface false so the result captures the truth.
            return false;
        }
    }

    /// <summary>Run the prerequisite probes and return a checklist suitable for printing.</summary>
    private async Task<IReadOnlyList<PrerequisiteCheck>> RunChecksAsync(
        DirectoryInfo workspaceRoot,
        string? networkProbeUrl,
        CancellationToken ct)
    {
        var checks = new List<PrerequisiteCheck>();

        void ProbeBinary(string binary, string usedFor)
        {
            var ok = _probe.BinaryOnPath(binary);
            checks.Add(new PrerequisiteCheck(
                Label: $"{binary} on PATH",
                Passed: ok,
                Detail: ok ? "found" : $"missing — only needed for {usedFor}"));
        }

        ProbeBinary("claude", "the Claude CLI executor (ClaudeCliAgentExecutor)");
        ProbeBinary("codex", "non-owner-tier sandboxing");
        ProbeBinary("python3", "Python script-skill adapters");
        ProbeBinary("node", "Node script-skill adapters");
        ProbeBinary("bash", "Bash script-skill adapters");

        if (!string.IsNullOrWhiteSpace(networkProbeUrl))
        {
            var reachable = await _probe.ReachableAsync(networkProbeUrl, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            checks.Add(new PrerequisiteCheck(
                Label: $"reachable: {networkProbeUrl}",
                Passed: reachable,
                Detail: reachable ? "ok" : "no response within 2s — offline or blocked"));
        }

        checks.Add(new PrerequisiteCheck(
            Label: "workspace path resolved",
            Passed: true,
            Detail: workspaceRoot.FullName));

        return checks;
    }

    private void PrintChecklist(IReadOnlyList<PrerequisiteCheck> checks)
    {
        _out.WriteLine();
        _out.WriteLine("prerequisites:");
        foreach (var c in checks)
        {
            // ASCII glyphs (not unicode) so terminals without a smart codepage render cleanly.
            var glyph = c.Passed ? "[ok]" : "[--]";
            _out.WriteLine(string.Format(CultureInfo.InvariantCulture, "  {0} {1} — {2}", glyph, c.Label, c.Detail));
        }
    }

    private bool Confirm()
    {
        _out.Write("Proceed? [y/N]: ");
        _out.Flush();
        var answer = _readLine();
        return answer is not null && (answer.Trim().Equals("y", StringComparison.OrdinalIgnoreCase)
            || answer.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    private void LaunchDashboard()
    {
        if (_launchDashboard is not null)
        {
            _launchDashboard("sutando", ["dashboard"]);
            return;
        }
        try
        {
            // Fire-and-forget: spawn a detached `sutando dashboard` process. We use the current
            // process's executable when its file name matches `sutando` (the tool name) so the
            // child shares the same binary that was just invoked; otherwise fall back to PATH.
            var current = Process.GetCurrentProcess().MainModule?.FileName;
            var fileName = current is not null && Path.GetFileNameWithoutExtension(current)
                .Equals("sutando", StringComparison.OrdinalIgnoreCase)
                ? current
                : "sutando";
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = "dashboard",
                UseShellExecute = false,
                CreateNoWindow = false,
            };
            Process.Start(psi);
            _out.WriteLine("dashboard: launched (detach).");
        }
        catch (Exception ex)
        {
            _err.WriteLine($"dashboard: failed to launch ({ex.Message}).");
        }
    }
}
