namespace Sutando.Bridge;

/// <summary>
/// Origin channel for a task. Determines default <see cref="TaskPriority"/> and result-delivery
/// behaviour.
/// </summary>
public enum TaskSource
{
    /// <summary>Web/native voice client.</summary>
    Voice,

    /// <summary>Direct chat input (local-chat, in-process console).</summary>
    Chat,

    /// <summary>Telegram DM or group.</summary>
    Telegram,

    /// <summary>Discord DM or channel @mention.</summary>
    Discord,

    /// <summary>Inbound phone call via Twilio.</summary>
    Phone,

    /// <summary>HTTP submission via the agent API.</summary>
    Api,

    /// <summary>Scheduled cron / proactive-loop tick.</summary>
    Cron,

    /// <summary>Self-initiated by the health-check watchdog.</summary>
    Health,

    /// <summary>Self-initiated by the proactive build loop.</summary>
    Proactive,
}

/// <summary>
/// 3-tier access control matching upstream — owner gets full access, verified gets a
/// curated tool subset, team / other / unverified are sandboxed.
/// </summary>
public enum AccessTier
{
    /// <summary>The system owner. Full capability access.</summary>
    Owner,

    /// <summary>Pre-approved caller / DM — limited tool subset, no system mutations.</summary>
    Verified,

    /// <summary>Team member from a shared channel. Sandboxed (read-only codex exec).</summary>
    Team,

    /// <summary>Anyone else with an answered channel. Sandboxed, information-only.</summary>
    Other,

    /// <summary>Unauthenticated / unverified caller — conversation only, hang up on attempts to escalate.</summary>
    Unverified,
}

/// <summary>
/// Dispatcher-visible priority for a pending task. Higher priority tasks are picked first;
/// ties broken by modification time (FIFO).
/// </summary>
public enum TaskPriority
{
    /// <summary>Voice / phone — sub-second latency target.</summary>
    Urgent = 0,

    /// <summary>Chat / owner DM — default.</summary>
    Normal = 1,

    /// <summary>Cron / health-check / non-owner DM.</summary>
    Low = 2,
}

/// <summary>Helpers for priority defaults per <see cref="TaskSource"/>.</summary>
public static class TaskPriorities
{
    /// <summary>Default priority for the given source — mirrors upstream <c>src/task_priority.py:default_priority_for_source</c>.</summary>
    public static TaskPriority DefaultFor(TaskSource source, AccessTier tier) => (source, tier) switch
    {
        (TaskSource.Voice, _) => TaskPriority.Urgent,
        (TaskSource.Phone, _) => TaskPriority.Urgent,
        (TaskSource.Cron, _) => TaskPriority.Low,
        (TaskSource.Health, _) => TaskPriority.Low,
        (TaskSource.Proactive, _) => TaskPriority.Low,
        (_, AccessTier.Owner) => TaskPriority.Normal,
        (_, AccessTier.Verified) => TaskPriority.Normal,
        _ => TaskPriority.Low,
    };
}
