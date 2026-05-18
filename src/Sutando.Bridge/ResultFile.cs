namespace Sutando.Bridge;

/// <summary>
/// Writes results into <c>&lt;workspace&gt;/results/&lt;task-id&gt;.txt</c>. Channels poll
/// the same directory and consume marker-prefixed bodies.
/// </summary>
public sealed class ResultFile
{
    private readonly DirectoryInfo _resultsDir;

    /// <param name="resultsDir">The workspace's <c>results/</c> directory.</param>
    public ResultFile(DirectoryInfo resultsDir)
    {
        _resultsDir = resultsDir;
        _resultsDir.Create();
    }

    /// <summary>Write a plain text result for a task.</summary>
    /// <returns>The absolute path that was written.</returns>
    public string Write(string taskId, string body)
    {
        var path = Path.Combine(_resultsDir.FullName, taskId + ".txt");
        File.WriteAllText(path, body);
        return path;
    }

    /// <summary>
    /// Compose a result body with the given markers prepended, then write it. The text body
    /// is rendered last so consumers can lift markers cleanly.
    /// </summary>
    /// <param name="taskId">Task identifier whose result is being written.</param>
    /// <param name="text">Human-readable body; may be empty when only markers matter.</param>
    /// <param name="dedupedTo">If non-null, prepends <c>[deduped: &lt;id&gt;]</c>.</param>
    /// <param name="noSend">Prepends <c>[no-send]</c>.</param>
    /// <param name="alreadyReplied">Prepends <c>[REPLIED]</c>.</param>
    /// <param name="attachments">File paths to attach via <c>[file: …]</c>.</param>
    public string WriteWithMarkers(
        string taskId,
        string text,
        string? dedupedTo = null,
        bool noSend = false,
        bool alreadyReplied = false,
        IEnumerable<string>? attachments = null)
    {
        var body = string.Empty;
        if (dedupedTo is not null)
        {
            body += $"{ResultMarkers.DedupedPrefix} {dedupedTo}]\n";
        }
        if (noSend)
        {
            body += ResultMarkers.NoSend + "\n";
        }
        if (alreadyReplied)
        {
            body += ResultMarkers.Replied + "\n";
        }
        if (attachments is not null)
        {
            foreach (var path in attachments)
            {
                body += $"[file: {path}]\n";
            }
        }
        body += text ?? string.Empty;
        return Write(taskId, body);
    }

    /// <summary>Write a proactive (agent-initiated) result with no inbound task to mirror.</summary>
    public string WriteProactive(string text, DateTimeOffset? now = null)
    {
        var ts = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var path = Path.Combine(_resultsDir.FullName, $"proactive-{ts}.txt");
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>Write a question result so the user is prompted to answer.</summary>
    public string WriteQuestion(string text, DateTimeOffset? now = null)
    {
        var ts = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var path = Path.Combine(_resultsDir.FullName, $"question-{ts}.txt");
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>Read a result back. Returns <see langword="null"/> if no result exists for the task.</summary>
    public ResultBody? Read(string taskId)
    {
        var path = Path.Combine(_resultsDir.FullName, taskId + ".txt");
        if (!File.Exists(path))
        {
            return null;
        }
        return ResultBody.Parse(File.ReadAllText(path));
    }
}
