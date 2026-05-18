using System.Runtime.InteropServices;
using Sutando.Skills;
using Sutando.Skills.Runtimes;
using Sutando.Workspace;

namespace Sutando.Tests.Skills;

/// <summary>
/// Subprocess-runtime smoke tests. Each test stages a tiny script in a temp directory,
/// constructs a <see cref="ScriptSkill"/> pointing at it, and asserts the JSON wire
/// format round-trips.
/// </summary>
public sealed class ScriptSkillTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string? _previousEnv;
    private readonly WorkspaceDirectory _workspace;

    public ScriptSkillTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "sutando-scriptskill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _previousEnv = Environment.GetEnvironmentVariable(WorkspaceDirectory.EnvVar);
        Environment.SetEnvironmentVariable(WorkspaceDirectory.EnvVar, _tempRoot);
        _workspace = WorkspaceDirectory.Resolve();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(WorkspaceDirectory.EnvVar, _previousEnv);
        try { Directory.Delete(_tempRoot, recursive: true); } catch (IOException) { }
    }

    [SkippableFact]
    public async Task PythonSkill_RoundTripsJsonResultEnvelope()
    {
        Skip.If(!ToolAvailable("python3") && !ToolAvailable("python"),
            "python3/python not on PATH; skip subprocess test");

        var skillDir = Path.Combine(_tempRoot, "py-echo");
        Directory.CreateDirectory(skillDir);
        var scriptPath = Path.Combine(skillDir, "echo.py");
        File.WriteAllText(scriptPath, """
            import json, sys
            payload = json.load(sys.stdin)
            args = payload.get('arguments', {})
            body = ' '.join(f"{k}={v}" for k, v in args.items())
            print(json.dumps({'success': True, 'body': body or 'no-args', 'artifacts': []}))
            """);

        var manifest = new SkillManifest
        {
            Id = "py-echo",
            Name = "Python Echo",
            Runtime = SkillRuntime.Python,
            Entry = "echo.py",
        };

        var pythonBinary = ToolAvailable("python3") ? "python3" : "python";
        var skill = new ScriptSkill(manifest, skillDir, new ScriptSkillRunnerOptions
        {
            PythonBinary = pythonBinary,
            Timeout = TimeSpan.FromSeconds(20),
        });

        var ctx = new SkillContext(_workspace, skillDir);
        var result = await skill.ExecuteAsync(ctx, new Dictionary<string, string> { ["greet"] = "world" }, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Contains("greet=world", result.Body);
    }

    [SkippableFact]
    public async Task BashSkill_PlainTextStdoutBecomesBody()
    {
        // Bash test exercises the POSIX shebang flow on Linux / macOS. On Windows the
        // available `bash` is typically Git Bash, which trips over CRLF shebangs and
        // Windows-style script paths — we skip there and rely on the Python test to
        // cover the cross-platform subprocess machinery.
        Skip.If(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "bash subprocess flow is POSIX-only; covered by PythonSkill on Windows");
        Skip.If(!ToolAvailable("bash"), "bash not on PATH; skip subprocess test");

        var skillDir = Path.Combine(_tempRoot, "sh-echo");
        Directory.CreateDirectory(skillDir);
        var scriptPath = Path.Combine(skillDir, "echo.sh");
        File.WriteAllText(scriptPath, """
            #!/usr/bin/env bash
            cat
            """);
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            File.SetUnixFileMode(scriptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        var manifest = new SkillManifest
        {
            Id = "sh-echo",
            Name = "Bash Echo",
            Runtime = SkillRuntime.Bash,
            Entry = "echo.sh",
        };

        var skill = new ScriptSkill(manifest, skillDir, new ScriptSkillRunnerOptions
        {
            Timeout = TimeSpan.FromSeconds(20),
        });

        var ctx = new SkillContext(_workspace, skillDir);
        var result = await skill.ExecuteAsync(ctx, new Dictionary<string, string> { ["k"] = "v" }, CancellationToken.None);

        // The script echoes its stdin (our invocation envelope) back as the body.
        Assert.True(result.Success);
        Assert.Contains("sh-echo", result.Body);
        Assert.Contains("\"k\":\"v\"", result.Body);
    }

    [Fact]
    public async Task NonexistentBinary_FailsCleanly()
    {
        var skillDir = Path.Combine(_tempRoot, "nope");
        Directory.CreateDirectory(skillDir);

        var manifest = new SkillManifest
        {
            Id = "nope",
            Name = "Nope",
            Runtime = SkillRuntime.Python,
            Entry = "script.py",
        };

        var skill = new ScriptSkill(manifest, skillDir, new ScriptSkillRunnerOptions
        {
            PythonBinary = "this-binary-does-not-exist-anywhere",
            Timeout = TimeSpan.FromSeconds(5),
        });

        var ctx = new SkillContext(_workspace, skillDir);
        // Process.Start throws Win32Exception (IOException) when binary isn't found; surface as a test assertion.
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await skill.ExecuteAsync(ctx, new Dictionary<string, string>(), CancellationToken.None));
    }

    private static bool ToolAvailable(string tool)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var separator = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';
        var extensions = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "", ".exe", ".cmd", ".bat" }
            : new[] { string.Empty };

        foreach (var dir in pathEnv.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                if (File.Exists(Path.Combine(dir, tool + ext)))
                {
                    return true;
                }
            }
        }
        return false;
    }
}
