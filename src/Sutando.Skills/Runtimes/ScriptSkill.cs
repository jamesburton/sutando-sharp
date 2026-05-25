using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sutando.Skills.Runtimes;

/// <summary>
/// <see cref="ISkill"/> implementation backed by a subprocess script — Python / Node / Bash /
/// dotnet-tool. The script is invoked with arguments on stdin (JSON) and its stdout (also JSON)
/// is parsed back into a <see cref="SkillResult"/>.
/// </summary>
/// <remarks>
/// <para>
/// Wire format on stdin:
/// </para>
/// <code lang="json">
/// {
///   "skill_id": "openai-tts",
///   "skill_root": "/abs/path/to/skill/dir",
///   "workspace_root": "/abs/path/to/workspace",
///   "arguments": { "text": "Hello", "voice": "alloy" }
/// }
/// </code>
/// <para>
/// Wire format on stdout (last non-empty line is parsed; everything before is treated as the
/// human-readable body):
/// </para>
/// <code lang="json">
/// { "success": true, "body": "Hello", "artifacts": ["/tmp/out.mp3"] }
/// </code>
/// <para>
/// If the script writes plain text and exits 0, the entire stdout becomes the <c>body</c>.
/// Exit code != 0 maps to <see cref="SkillResult.Fail"/> with stderr in the error message.
/// </para>
/// </remarks>
public sealed class ScriptSkill : ISkill
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly string _skillRoot;
    private readonly ScriptSkillRunnerOptions _options;
    private readonly ILogger<ScriptSkill> _logger;

    /// <inheritdoc/>
    public SkillManifest Manifest { get; }

    /// <param name="manifest">Skill manifest.</param>
    /// <param name="skillRoot">Filesystem directory of the skill.</param>
    /// <param name="options">Runtime configuration (binary paths, timeout).</param>
    /// <param name="logger">Optional logger.</param>
    public ScriptSkill(
        SkillManifest manifest,
        string skillRoot,
        ScriptSkillRunnerOptions? options = null,
        ILogger<ScriptSkill>? logger = null)
    {
        Manifest = manifest;
        _skillRoot = skillRoot;
        _options = options ?? new ScriptSkillRunnerOptions();
        _logger = logger ?? NullLogger<ScriptSkill>.Instance;
    }

    /// <inheritdoc/>
    public async Task<SkillResult> ExecuteAsync(
        SkillContext context,
        IReadOnlyDictionary<string, string> arguments,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(arguments);

        var stopwatch = Stopwatch.StartNew();
        var (binary, args) = ResolveCommand();

        var psi = new ProcessStartInfo
        {
            FileName = binary,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _skillRoot,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"skill '{Manifest.Id}': failed to start {binary}");

        var payload = JsonSerializer.Serialize(new InvocationEnvelope
        {
            SkillId = Manifest.Id,
            SkillRoot = _skillRoot,
            WorkspaceRoot = context.Workspace.Root.FullName,
            Arguments = arguments,
        }, JsonOptions);

        try
        {
            await proc.StandardInput.WriteAsync(payload.AsMemory(), ct).ConfigureAwait(false);
            proc.StandardInput.Close();
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "skill '{Id}': stdin closed prematurely", Manifest.Id);
        }

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        var budget = _options.Timeout ?? DefaultTimeout;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(budget);

        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            stopwatch.Stop();

            if (ct.IsCancellationRequested)
            {
                throw;
            }
            return SkillResult.Fail($"timed out after {budget}", stopwatch.Elapsed);
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        stopwatch.Stop();

        if (proc.ExitCode != 0)
        {
            _logger.LogWarning(
                "skill '{Id}' exited {Code}: {Stderr}",
                Manifest.Id, proc.ExitCode, Truncate(stderr, 400));
            return SkillResult.Fail(
                $"{binary} exited {proc.ExitCode}: {Truncate(stderr, 200)}",
                stopwatch.Elapsed,
                body: stdout);
        }

        return ParseResultJsonOrPlainText(stdout, stopwatch.Elapsed);
    }

    private static SkillResult ParseResultJsonOrPlainText(string stdout, TimeSpan duration)
    {
        var trimmed = stdout.Trim();
        if (trimmed.Length == 0)
        {
            return SkillResult.Ok(string.Empty, duration);
        }

        // Last non-empty line is the canonical JSON payload — preceding lines are treated as
        // human-readable preamble appended to the body.
        var lastNewline = trimmed.LastIndexOf('\n');
        var jsonCandidate = lastNewline >= 0 ? trimmed[(lastNewline + 1)..].Trim() : trimmed;

        if (jsonCandidate.StartsWith('{') && jsonCandidate.EndsWith('}'))
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonCandidate);
                // Only treat the JSON as a result envelope if it carries at least one of the
                // canonical fields — otherwise a script that incidentally echoes a JSON object
                // (e.g. our own invocation envelope round-tripped through `cat`) would be
                // silently parsed as a defaulted ResultEnvelope and its content swallowed.
                var hasEnvelopeField =
                    doc.RootElement.TryGetProperty("success", out _) ||
                    doc.RootElement.TryGetProperty("body", out _) ||
                    doc.RootElement.TryGetProperty("error", out _) ||
                    doc.RootElement.TryGetProperty("artifacts", out _);
                if (hasEnvelopeField)
                {
                    var parsed = doc.RootElement.Deserialize<ResultEnvelope>(JsonOptions);
                    if (parsed is not null)
                    {
                        var bodyPreamble = lastNewline >= 0 ? trimmed[..lastNewline].TrimEnd() : string.Empty;
                        var body = string.IsNullOrEmpty(parsed.Body)
                            ? bodyPreamble
                            : (bodyPreamble.Length == 0 ? parsed.Body : bodyPreamble + "\n" + parsed.Body);
                        return parsed.Success
                            ? SkillResult.Ok(body, duration, parsed.Artifacts ?? [])
                            : SkillResult.Fail(parsed.Error ?? "skill reported failure", duration, body);
                    }
                }
            }
            catch (JsonException)
            {
                // Fall through to plain-text path.
            }
        }

        return SkillResult.Ok(stdout, duration);
    }

    private (string Binary, IReadOnlyList<string> Args) ResolveCommand()
    {
        var entryPath = Path.IsPathRooted(Manifest.Entry)
            ? Manifest.Entry
            : Path.Combine(_skillRoot, Manifest.Entry);

        return Manifest.Runtime switch
        {
            SkillRuntime.Python => (_options.PythonBinary, [entryPath]),
            SkillRuntime.Node => (_options.NodeBinary, [entryPath]),
            SkillRuntime.Bash => (_options.BashBinary, [entryPath]),
            SkillRuntime.DotnetTool => (_options.DotnetBinary, ["tool", "run", Manifest.Entry]),
            _ => throw new InvalidOperationException(
                $"ScriptSkill: unsupported runtime '{Manifest.Runtime}' for skill '{Manifest.Id}'"),
        };
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private sealed record InvocationEnvelope
    {
        [JsonPropertyName("skill_id")] public required string SkillId { get; init; }
        [JsonPropertyName("skill_root")] public required string SkillRoot { get; init; }
        [JsonPropertyName("workspace_root")] public required string WorkspaceRoot { get; init; }
        [JsonPropertyName("arguments")] public required IReadOnlyDictionary<string, string> Arguments { get; init; }
    }

    private sealed record ResultEnvelope
    {
        [JsonPropertyName("success")] public bool Success { get; init; } = true;
        [JsonPropertyName("body")] public string? Body { get; init; }
        [JsonPropertyName("error")] public string? Error { get; init; }
        [JsonPropertyName("artifacts")] public IReadOnlyList<string>? Artifacts { get; init; }
    }
}

/// <summary>Runtime configuration for <see cref="ScriptSkill"/>.</summary>
public sealed record ScriptSkillRunnerOptions
{
    /// <summary>Python interpreter binary; default <c>python3</c>.</summary>
    public string PythonBinary { get; init; } = "python3";

    /// <summary>Node.js binary; default <c>node</c>.</summary>
    public string NodeBinary { get; init; } = "node";

    /// <summary>POSIX shell binary; default <c>bash</c>.</summary>
    public string BashBinary { get; init; } = "bash";

    /// <summary>.NET CLI binary; default <c>dotnet</c>.</summary>
    public string DotnetBinary { get; init; } = "dotnet";

    /// <summary>Wall-clock timeout per invocation; default 5 minutes.</summary>
    public TimeSpan? Timeout { get; init; }
}
