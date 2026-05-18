using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sutando.Bridge;

namespace Sutando.Core.Executors;

/// <summary>
/// Default executor — shells out to the <c>claude</c> CLI so we reuse the user's existing
/// Claude Code subscription rather than burning Anthropic API quota.
/// </summary>
/// <remarks>
/// <para>
/// Non-owner tiers (team / other / unverified) are delegated to <c>codex exec --sandbox read-only</c>
/// instead, matching upstream's three-tier policy. Override the binary paths via
/// <see cref="ClaudeCliAgentExecutorOptions"/>.
/// </para>
/// <para>
/// Stdout becomes the result body; stderr is captured and logged but not surfaced unless the
/// process exits non-zero. The task body is fed via stdin to avoid arg-list-size limits and
/// shell-quoting hazards. <see cref="TaskEnvelope.Timeout"/> is enforced via a CTS linked to
/// the caller's token.
/// </para>
/// </remarks>
public sealed class ClaudeCliAgentExecutor : IAgentExecutor
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    private readonly ClaudeCliAgentExecutorOptions _options;
    private readonly ILogger<ClaudeCliAgentExecutor> _logger;

    /// <summary><inheritdoc cref="IAgentExecutor.Id"/></summary>
    public string Id => "claude-cli";

    /// <param name="options">CLI binary + argument configuration.</param>
    /// <param name="logger">Optional logger.</param>
    public ClaudeCliAgentExecutor(
        ClaudeCliAgentExecutorOptions? options = null,
        ILogger<ClaudeCliAgentExecutor>? logger = null)
    {
        _options = options ?? new ClaudeCliAgentExecutorOptions();
        _logger = logger ?? NullLogger<ClaudeCliAgentExecutor>.Instance;
    }

    /// <inheritdoc/>
    public async Task<AgentResult> ExecuteAsync(TaskEnvelope task, CancellationToken ct)
    {
        if (task.IsCancelInstruction)
        {
            // Cancel signals are bridge-level concerns; the executor short-circuits.
            return AgentResult.Ok($"cancelled task {task.CancelTargetId}", TimeSpan.Zero);
        }

        var (binary, args, useSandbox) = SelectBinary(task.AccessTier);
        var stopwatch = Stopwatch.StartNew();

        var psi = new ProcessStartInfo
        {
            FileName = binary,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        if (_options.WorkingDirectory is { Length: > 0 } wd)
        {
            psi.WorkingDirectory = wd;
        }

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {binary}");

        // Feed the task body via stdin so we sidestep shell-quoting and argv length limits.
        try
        {
            await proc.StandardInput.WriteAsync(task.Body.AsMemory(), ct).ConfigureAwait(false);
            proc.StandardInput.Close();
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "stdin closed prematurely");
        }

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        var budget = task.Timeout ?? DefaultTimeout;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(budget);

        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); }
            catch { /* best-effort kill */ }
            stopwatch.Stop();

            if (ct.IsCancellationRequested)
            {
                throw;
            }
            return AgentResult.Timeout(budget);
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        stopwatch.Stop();

        if (proc.ExitCode != 0)
        {
            _logger.LogWarning(
                "{Binary} exited {Code} for task {Id} (sandbox={Sandbox}). stderr: {Stderr}",
                binary, proc.ExitCode, task.Id, useSandbox, Truncate(stderr, 400));
            var body = string.IsNullOrWhiteSpace(stdout)
                ? $"{binary} exited {proc.ExitCode}: {Truncate(stderr, 200)}"
                : stdout;
            return AgentResult.Error(body, stopwatch.Elapsed);
        }

        return AgentResult.Ok(stdout, stopwatch.Elapsed);
    }

    private (string Binary, IReadOnlyList<string> Args, bool UseSandbox) SelectBinary(AccessTier tier)
    {
        // Owner: full claude with --dangerously-skip-permissions (matches upstream startup.sh).
        // Anyone else: codex exec --sandbox read-only — read-only sandbox per upstream policy.
        if (tier == AccessTier.Owner)
        {
            return (_options.ClaudeBinary, _options.ClaudeArgs, false);
        }
        return (_options.CodexBinary, _options.CodexSandboxArgs, true);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}

/// <summary>Configuration for <see cref="ClaudeCliAgentExecutor"/> — paths and flag sets.</summary>
public sealed record ClaudeCliAgentExecutorOptions
{
    /// <summary>Absolute path or PATH-resolvable name of the claude CLI binary. Default <c>claude</c>.</summary>
    public string ClaudeBinary { get; init; } = "claude";

    /// <summary>Args passed to claude for owner-tier tasks.</summary>
    public IReadOnlyList<string> ClaudeArgs { get; init; } = ["--dangerously-skip-permissions"];

    /// <summary>Absolute path or PATH-resolvable name of the codex CLI binary. Default <c>codex</c>.</summary>
    public string CodexBinary { get; init; } = "codex";

    /// <summary>Args used to run codex in read-only sandbox mode for non-owner tiers.</summary>
    public IReadOnlyList<string> CodexSandboxArgs { get; init; } = ["exec", "--sandbox", "read-only"];

    /// <summary>Working directory; defaults to the current directory.</summary>
    public string? WorkingDirectory { get; init; }
}
