namespace Sutando.Bridge;

/// <summary>
/// Decoded task file — the canonical work-unit that flows through Sutando's tasks→results
/// pipeline. Channels produce these by writing them to <c>&lt;workspace&gt;/tasks/&lt;id&gt;.txt</c>;
/// the agent executor consumes them, runs the body, and writes a corresponding result file.
/// </summary>
/// <remarks>
/// Wire format is the line-based <c>key: value</c> grammar parsed/written by
/// <see cref="TaskFile"/>. See <c>docs/bridge-contract.md</c> for the locked schema.
/// </remarks>
public sealed record TaskEnvelope
{
    /// <summary>Globally-unique task identifier (e.g. <c>task-chat-1747500000</c>).</summary>
    public required string Id { get; init; }

    /// <summary>UTC timestamp when the task was submitted.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>The work to perform. Free-form, may contain newlines.</summary>
    public required string Body { get; init; }

    /// <summary>Originating channel.</summary>
    public required TaskSource Source { get; init; }

    /// <summary>Source-specific channel identifier (Telegram chat ID, Discord channel ID, <c>local-chat</c>, …).</summary>
    public required string ChannelId { get; init; }

    /// <summary>Originating user identifier.</summary>
    public required string UserId { get; init; }

    /// <summary>Access tier — drives sandboxing.</summary>
    public required AccessTier AccessTier { get; init; }

    /// <summary>Dispatcher priority.</summary>
    public required TaskPriority Priority { get; init; }

    /// <summary>Wall-clock budget for the task; <see langword="null"/> uses the executor default.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>If <see langword="true"/>, DM the owner on timeout. Default <see langword="false"/>.</summary>
    public bool DmOnTimeout { get; init; }

    /// <summary>Optional reply target (Telegram / Discord message ID).</summary>
    public string? ReplyToMessageId { get; init; }

    /// <summary>Free-form per-source metadata namespace.</summary>
    public IReadOnlyDictionary<string, string> Meta { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// True if the task body is a cancellation signal. The body begins with
    /// <c>CANCEL_INSTRUCTION: &lt;target-task-id&gt;</c>; executors must terminate
    /// the referenced task and not process this task's body further.
    /// </summary>
    public bool IsCancelInstruction =>
        Body.StartsWith("CANCEL_INSTRUCTION:", StringComparison.Ordinal);

    /// <summary>Returns the target task id when <see cref="IsCancelInstruction"/> is true; <see langword="null"/> otherwise.</summary>
    public string? CancelTargetId
    {
        get
        {
            if (!IsCancelInstruction)
            {
                return null;
            }
            var rest = Body["CANCEL_INSTRUCTION:".Length..].TrimStart();
            var newline = rest.IndexOfAny(['\r', '\n']);
            return (newline > 0 ? rest[..newline] : rest).Trim();
        }
    }
}
