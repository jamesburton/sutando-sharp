using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sutando.Workspace;

/// <summary>
/// Canonical workspace-directory resolution for Sutando services.
/// </summary>
/// <remarks>
/// <para>
/// All runtime artifacts (<c>tasks/</c>, <c>results/</c>, <c>state/</c>, <c>data/</c>,
/// <c>build_log.md</c>, …) live under one workspace dir. Every consumer resolves it the
/// same way:
/// </para>
/// <list type="number">
///   <item><description><c>SUTANDO_WORKSPACE</c> env var (override; <c>~</c> is expanded).</description></item>
///   <item><description><c>~/.sutando/workspace/</c> (default; OS-neutral home-relative path).</description></item>
/// </list>
/// <para>
/// The default deliberately avoids <c>~/Library/Application Support/sutando/</c> on macOS
/// and <c>%APPDATA%\sutando\</c> on Windows so that the workspace never collides with a
/// native app's cache territory.
/// </para>
/// <para>
/// Direct port of upstream <c>src/workspace_default.py</c> and the parallel TypeScript
/// resolver in <c>src/task-bridge.ts</c>.
/// </para>
/// </remarks>
public sealed class WorkspaceDirectory
{
    /// <summary>Environment variable used to override the workspace location.</summary>
    public const string EnvVar = "SUTANDO_WORKSPACE";

    private static readonly string[] DefaultSubpath = [".sutando", "workspace"];

    /// <summary>Directory names that, if found under a legacy fallback root, signal an in-use older install.</summary>
    private static readonly string[] LegacyDirs = ["tasks", "results", "state", "notes"];

    private readonly ILogger<WorkspaceDirectory> _logger;

    /// <summary>The resolved workspace root.</summary>
    public DirectoryInfo Root { get; }

    /// <summary><c>&lt;root&gt;/tasks</c> directory.</summary>
    public DirectoryInfo Tasks => Root.CreateSubdirectory("tasks");

    /// <summary><c>&lt;root&gt;/results</c> directory.</summary>
    public DirectoryInfo Results => Root.CreateSubdirectory("results");

    /// <summary><c>&lt;root&gt;/state</c> directory.</summary>
    public DirectoryInfo State => Root.CreateSubdirectory("state");

    /// <summary><c>&lt;root&gt;/logs</c> directory.</summary>
    public DirectoryInfo Logs => Root.CreateSubdirectory("logs");

    /// <summary><c>&lt;root&gt;/data</c> directory.</summary>
    public DirectoryInfo Data => Root.CreateSubdirectory("data");

    /// <summary><c>&lt;root&gt;/notes</c> directory.</summary>
    public DirectoryInfo Notes => Root.CreateSubdirectory("notes");

    /// <summary><c>&lt;root&gt;/core-status.json</c> — single-file work signal.</summary>
    public FileInfo CoreStatusFile => new(Path.Combine(Root.FullName, "core-status.json"));

    private WorkspaceDirectory(DirectoryInfo root, ILogger<WorkspaceDirectory>? logger)
    {
        Root = root;
        _logger = logger ?? NullLogger<WorkspaceDirectory>.Instance;
    }

    /// <summary>
    /// Resolve and ensure the workspace directory. Triggers a one-time legacy migration
    /// when the new default location is empty but a sibling legacy fallback contains
    /// runtime state.
    /// </summary>
    /// <param name="logger">Optional logger; defaults to <see cref="NullLogger{T}"/>.</param>
    /// <param name="legacyFallback">
    ///   Optional path to scan for legacy in-repo state (typically the repo root for installs
    ///   that ran the upstream layout). When <see langword="null"/>, no migration is attempted.
    /// </param>
    /// <returns>A resolved <see cref="WorkspaceDirectory"/>.</returns>
    public static WorkspaceDirectory Resolve(
        ILogger<WorkspaceDirectory>? logger = null,
        string? legacyFallback = null)
    {
        var rootPath = ResolvePath();
        var root = new DirectoryInfo(rootPath);
        root.Create();

        var workspace = new WorkspaceDirectory(root, logger);

        if (legacyFallback is not null)
        {
            workspace.MigrateLegacy(legacyFallback);
        }

        return workspace;
    }

    /// <summary>Returns the resolved workspace path without creating any directories.</summary>
    public static string ResolvePath()
    {
        var fromEnv = Environment.GetEnvironmentVariable(EnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return ExpandHome(fromEnv);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, Path.Combine(DefaultSubpath));
    }

    /// <summary>
    /// One-time migration from a legacy fallback root (typically the upstream repo root)
    /// into the canonical workspace. Triggers only when the canonical root is empty AND
    /// the legacy root shows runtime evidence (a <c>task-*</c> file under one of the
    /// known runtime-state dirs).
    /// </summary>
    /// <param name="legacyRoot">Directory to scan for legacy state.</param>
    /// <returns><see langword="true"/> if at least one directory was migrated.</returns>
    public bool MigrateLegacy(string legacyRoot)
    {
        var legacy = new DirectoryInfo(legacyRoot);
        if (!legacy.Exists)
        {
            return false;
        }

        // Don't migrate if target already has content — assume a working setup exists.
        if (Root.Exists && Root.EnumerateFileSystemInfos().Any())
        {
            return false;
        }

        var hasEvidence = LegacyDirs
            .Select(d => new DirectoryInfo(Path.Combine(legacy.FullName, d)))
            .Any(d => d.Exists && d.EnumerateFiles("task-*", SearchOption.TopDirectoryOnly).Any());

        if (!hasEvidence)
        {
            return false;
        }

        Root.Create();
        var moved = 0;
        foreach (var dir in LegacyDirs)
        {
            var src = new DirectoryInfo(Path.Combine(legacy.FullName, dir));
            if (!src.Exists)
            {
                continue;
            }

            var dst = new DirectoryInfo(Path.Combine(Root.FullName, dir));
            if (dst.Exists)
            {
                continue;
            }

            try
            {
                src.MoveTo(dst.FullName);
                moved++;
                _logger.LogInformation("Migrated legacy workspace dir {Dir} → {Target}", dir, dst.FullName);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Failed to migrate legacy dir {Dir}", dir);
            }
        }

        return moved > 0;
    }

    /// <summary>Atomically writes a file by writing to <c>&lt;path&gt;.tmp</c> and renaming.</summary>
    /// <param name="path">Absolute target path.</param>
    /// <param name="content">UTF-8 content.</param>
    public static void AtomicWrite(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        if (File.Exists(path))
        {
            File.Replace(tmp, path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tmp, path);
        }
    }

    private static string ExpandHome(string path)
    {
        if (path.StartsWith('~'))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, path[1..].TrimStart('/', '\\'));
        }
        return path;
    }
}
