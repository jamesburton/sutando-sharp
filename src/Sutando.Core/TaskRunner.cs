using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sutando.Bridge;
using Sutando.Skills;
using Sutando.Workspace;

namespace Sutando.Core;

/// <summary>
/// Orchestrates the task-execution loop: pull <see cref="TaskEnvelope"/>s from a
/// <see cref="TaskWatcher"/>, dispatch each through an <see cref="IAgentExecutor"/>
/// (or a matching <see cref="ISkill"/> when a <see cref="SkillRegistry"/> is wired in),
/// write the matching result, signal status, and archive on completion.
/// </summary>
/// <remarks>
/// <para>
/// Tasks are processed in priority order — at any moment the runner takes the highest-priority
/// pending envelope (ties broken FIFO by arrival). One task at a time keeps result-ordering
/// deterministic and matches upstream's single-consumer model.
/// </para>
/// <para>
/// <b>Skill routing.</b> When a <see cref="SkillRegistry"/> is supplied, before invoking the
/// executor the runner extracts a trigger keyword from the envelope body — the first
/// whitespace-delimited token, lowercased with invariant culture — and looks it up in the
/// registry via <see cref="SkillRegistry.ResolveByTrigger"/>. If a registered skill claims
/// that trigger the skill handles the task and the executor is bypassed; otherwise the
/// executor handles the task as usual. The registry parameter is optional so existing call
/// sites compile and behave unchanged.
/// </para>
/// </remarks>
public sealed class TaskRunner : IAsyncDisposable
{
    private readonly WorkspaceDirectory _workspace;
    private readonly TaskWatcher _watcher;
    private readonly IAgentExecutor _executor;
    private readonly SkillRegistry? _skills;
    private readonly ResultFile _results;
    private readonly TaskArchive _archive;
    private readonly CoreStatus _status;
    private readonly ILogger<TaskRunner> _logger;
    private readonly CancellationTokenSource _stopCts = new();
    private Task? _loop;

    /// <param name="workspace">Resolved workspace.</param>
    /// <param name="executor">Concrete executor (CLI or HTTP).</param>
    /// <param name="watcher">Pre-constructed watcher; <see cref="TaskWatcher.Start"/> is invoked here.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="skills">
    /// Optional skill registry. When supplied, the runner first looks up skills by the trigger
    /// keyword extracted from the envelope body before falling through to <paramref name="executor"/>.
    /// </param>
    public TaskRunner(
        WorkspaceDirectory workspace,
        IAgentExecutor executor,
        TaskWatcher? watcher = null,
        ILogger<TaskRunner>? logger = null,
        SkillRegistry? skills = null)
    {
        _workspace = workspace;
        _executor = executor;
        _watcher = watcher ?? new TaskWatcher(workspace);
        _results = new ResultFile(workspace.Results);
        _archive = new TaskArchive(workspace);
        _status = new CoreStatus(workspace);
        _logger = logger ?? NullLogger<TaskRunner>.Instance;
        _skills = skills;
    }

    /// <summary>Start consuming tasks. Idempotent. Returns the background Task for monitoring.</summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        if (_loop is not null)
        {
            return _loop;
        }
        _watcher.Start();
        _status.SignalIdle();
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopCts.Token);
        _loop = Task.Run(() => RunAsync(linked.Token), linked.Token);
        return _loop;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TaskEnvelope envelope;
                try
                {
                    envelope = await _watcher.Reader.ReadAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                await ProcessAsync(envelope, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _status.SignalIdle();
        }
    }

    /// <summary>
    /// Process a single envelope. If a <see cref="SkillRegistry"/> is wired in and a matching
    /// skill claims the envelope's leading trigger keyword, the skill produces the result;
    /// otherwise the result comes from <see cref="IAgentExecutor.ExecuteAsync"/>. Either way
    /// the result is marker-composed, written, and archived. Public so tests can drive it
    /// deterministically.
    /// </summary>
    public async Task ProcessAsync(TaskEnvelope envelope, CancellationToken ct)
    {
        _status.SignalRunning(BuildStepLabel(envelope));
        try
        {
            var skill = TryMatchSkill(envelope);
            AgentResult result;
            if (skill is not null)
            {
                result = await RunSkillAsync(skill, envelope, ct).ConfigureAwait(false);
                _logger.LogInformation(
                    "task {Id} done in {Ms} ms via skill {Skill} (timedOut={Timeout}, err={Error})",
                    envelope.Id, (long)result.Duration.TotalMilliseconds, skill.Manifest.Id, result.TimedOut, result.IsError);
            }
            else
            {
                result = await _executor.ExecuteAsync(envelope, ct).ConfigureAwait(false);
                _logger.LogInformation(
                    "task {Id} done in {Ms} ms (executor={Executor}, timedOut={Timeout}, err={Error})",
                    envelope.Id, (long)result.Duration.TotalMilliseconds, _executor.Id, result.TimedOut, result.IsError);
            }

            WriteResult(envelope.Id, result);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("task {Id} cancelled before completion", envelope.Id);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "task {Id} threw — writing error result", envelope.Id);
            _results.Write(envelope.Id, $"executor crashed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _archive.Archive(envelope.Id);
            _status.SignalIdle();
        }
    }

    /// <summary>
    /// Resolve a skill for this envelope using the leading-token heuristic. Returns null when
    /// no registry is wired in, the body is empty, or no registered skill claims the trigger.
    /// </summary>
    private ISkill? TryMatchSkill(TaskEnvelope envelope)
    {
        if (_skills is null)
        {
            return null;
        }

        var trigger = ExtractTrigger(envelope.Body);
        if (trigger is null)
        {
            return null;
        }

        var matches = _skills.ResolveByTrigger(trigger);
        // First match wins. Multiple skills sharing a trigger is a registration smell — log
        // and continue rather than failing the task.
        if (matches.Count > 1)
        {
            _logger.LogWarning(
                "task {Id}: trigger '{Trigger}' matched {Count} skills; using '{Selected}'",
                envelope.Id, trigger, matches.Count, matches[0].Manifest.Id);
        }
        return matches.Count > 0 ? matches[0] : null;
    }

    /// <summary>
    /// Heuristic trigger extraction: take the first whitespace-delimited token of <paramref name="body"/>
    /// and lowercase it via invariant culture. Returns null when the body has no usable token.
    /// </summary>
    /// <remarks>
    /// Public + static so tests and future routing code can exercise the heuristic without
    /// building a runner.
    /// </remarks>
    public static string? ExtractTrigger(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        // The first line is typically the user's intent; split it on whitespace to get the
        // leading token. Body may be a multi-line task with a heredoc / quoted block below.
        var newline = body.IndexOfAny(['\n', '\r']);
        var firstLine = newline >= 0 ? body[..newline] : body;
        var token = firstLine.AsSpan().Trim();
        if (token.IsEmpty)
        {
            return null;
        }
        var space = token.IndexOf(' ');
        if (space > 0)
        {
            token = token[..space];
        }
        // Tab and other whitespace characters too — guard against them.
        for (var i = 0; i < token.Length; i++)
        {
            if (char.IsWhiteSpace(token[i]))
            {
                token = token[..i];
                break;
            }
        }
        return token.IsEmpty ? null : token.ToString().ToLowerInvariant();
    }

    /// <summary>Invoke the skill and translate its <see cref="SkillResult"/> into an <see cref="AgentResult"/>.</summary>
    private async Task<AgentResult> RunSkillAsync(ISkill skill, TaskEnvelope envelope, CancellationToken ct)
    {
        var context = new SkillContext(_workspace, _workspace.Root.FullName);
        // The skill argument shape is intentionally minimal here — task envelope's body lands
        // as a single "body" argument. Future work: parse k=v pairs out of the trailing body.
        var args = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["task_id"] = envelope.Id,
            ["body"] = envelope.Body,
            ["user_id"] = envelope.UserId,
            ["channel_id"] = envelope.ChannelId,
            ["source"] = envelope.Source.ToString(),
        };

        SkillResult result;
        try
        {
            result = await skill.ExecuteAsync(context, args, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "skill {Skill} crashed on task {Id}", skill.Manifest.Id, envelope.Id);
            return AgentResult.Error($"skill '{skill.Manifest.Id}' crashed: {ex.GetType().Name}: {ex.Message}", TimeSpan.Zero);
        }

        return new AgentResult
        {
            Body = result.Body,
            Duration = result.Duration,
            IsError = !result.Success,
            Attachments = result.Artifacts,
        };
    }

    private void WriteResult(string taskId, AgentResult result)
    {
        _results.WriteWithMarkers(
            taskId,
            result.Body,
            dedupedTo: result.DedupedTo,
            noSend: result.NoSend,
            alreadyReplied: result.AlreadyReplied,
            attachments: result.Attachments);
    }

    private static string BuildStepLabel(TaskEnvelope envelope)
    {
        var preview = envelope.Body.Replace('\n', ' ');
        return preview.Length <= 60 ? preview : preview[..60] + "…";
    }

    /// <summary>Stop the loop and dispose the underlying watcher.</summary>
    public async ValueTask DisposeAsync()
    {
        _stopCts.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }
        await _watcher.DisposeAsync().ConfigureAwait(false);
        _stopCts.Dispose();
    }
}
