using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sutando.Workspace;

namespace Sutando.Bridge;

/// <summary>
/// Move completed task &amp; result files into month-partitioned archive subdirectories.
/// Mirrors upstream's <c>archive/YYYY-MM/</c> layout (PR #591). Archiving is silent on
/// failure — a stale file is better than a leaked exception during shutdown.
/// </summary>
public sealed class TaskArchive
{
    private readonly WorkspaceDirectory _workspace;
    private readonly ILogger<TaskArchive> _logger;
    private readonly TimeProvider _time;

    /// <param name="workspace">The resolved workspace.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="time">Optional time source — defaults to <see cref="TimeProvider.System"/>. Tests inject a fake.</param>
    public TaskArchive(WorkspaceDirectory workspace, ILogger<TaskArchive>? logger = null, TimeProvider? time = null)
    {
        _workspace = workspace;
        _logger = logger ?? NullLogger<TaskArchive>.Instance;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Archive both the task file and (optionally) the corresponding result.</summary>
    /// <param name="taskId">Task identifier.</param>
    /// <param name="alsoResult">If <see langword="true"/> and a result file exists, archive that too.</param>
    /// <returns>Number of files actually moved.</returns>
    public int Archive(string taskId, bool alsoResult = true)
    {
        var moved = 0;
        var ym = _time.GetUtcNow().UtcDateTime.ToString("yyyy-MM");

        var taskSrc = Path.Combine(_workspace.Tasks.FullName, taskId + ".txt");
        var taskArchiveDir = Path.Combine(_workspace.Tasks.FullName, "archive", ym);
        if (TryMove(taskSrc, taskArchiveDir, taskId + ".txt"))
        {
            moved++;
        }

        if (alsoResult)
        {
            var resultSrc = Path.Combine(_workspace.Results.FullName, taskId + ".txt");
            var resultArchiveDir = Path.Combine(_workspace.Results.FullName, "archive", ym);
            if (TryMove(resultSrc, resultArchiveDir, taskId + ".txt"))
            {
                moved++;
            }
        }

        return moved;
    }

    /// <summary>Search the archive for a task file, including legacy flat-archive locations.</summary>
    /// <returns>The absolute path of the archived task file, or <see langword="null"/> if not found.</returns>
    public string? FindArchivedTask(string taskId)
    {
        var fileName = taskId + ".txt";

        // New layout: tasks/archive/YYYY-MM/<id>.txt
        var partitioned = _workspace.Tasks.EnumerateDirectories("archive", SearchOption.TopDirectoryOnly)
            .SelectMany(d => d.EnumerateFiles(fileName, SearchOption.AllDirectories))
            .FirstOrDefault();
        if (partitioned is not null)
        {
            return partitioned.FullName;
        }

        // Legacy flat-archive: tasks/archive/<id>.txt
        var legacy = Path.Combine(_workspace.Tasks.FullName, "archive", fileName);
        return File.Exists(legacy) ? legacy : null;
    }

    private bool TryMove(string src, string destDir, string destName)
    {
        if (!File.Exists(src))
        {
            return false;
        }
        try
        {
            Directory.CreateDirectory(destDir);
            var dest = Path.Combine(destDir, destName);
            // If a same-named file already lives there, replace it deterministically.
            if (File.Exists(dest))
            {
                File.Delete(dest);
            }
            File.Move(src, dest);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Archive move failed: {Src} → {DestDir}", src, destDir);
            // Mirror upstream's fall-back-to-unlink behaviour so we never leave stale files behind.
            try
            {
                File.Delete(src);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
