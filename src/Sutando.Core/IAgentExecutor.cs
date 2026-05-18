using Sutando.Bridge;

namespace Sutando.Core;

/// <summary>
/// The central abstraction that runs a task. Sutando ships two implementations:
/// <see cref="Executors.ClaudeCliAgentExecutor"/> (shells out to <c>claude</c> CLI to use
/// the user's Claude Code subscription) and <see cref="Executors.AnthropicHttpAgentExecutor"/>
/// (direct Anthropic API for users who provide an API key).
/// </summary>
/// <remarks>
/// Implementations MUST honour <see cref="TaskEnvelope.AccessTier"/>: owner tasks may use the
/// full agent surface; team / other / unverified tasks MUST be sandboxed (e.g. delegated
/// to <c>codex exec --sandbox read-only</c>) per upstream policy.
/// </remarks>
public interface IAgentExecutor
{
    /// <summary>Short identifier — <c>claude-cli</c>, <c>anthropic-http</c>, <c>fake</c>, …</summary>
    string Id { get; }

    /// <summary>Execute the task and return its result. Implementations should respect <paramref name="ct"/> promptly.</summary>
    Task<AgentResult> ExecuteAsync(TaskEnvelope task, CancellationToken ct);
}
