namespace Sutando.Channels.Telegram;

/// <summary>
/// Tunable configuration for <see cref="TelegramChannel"/>.
/// </summary>
/// <remarks>
/// <para>
/// Allow-list semantics mirror upstream's <c>src/telegram-bridge.py</c>:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="OwnerUserId"/> — full <c>owner</c> access tier.</description></item>
///   <item><description><see cref="VerifiedUserIds"/> — pre-approved peers, <c>verified</c> tier.</description></item>
///   <item><description><see cref="TeamUserIds"/> — shared-channel users, <c>team</c> tier (sandboxed).</description></item>
///   <item><description>Anyone else — <c>unverified</c>; channel writes a polite decline inline and does NOT enqueue a task.</description></item>
/// </list>
/// </remarks>
public sealed record TelegramChannelOptions
{
    /// <summary>Default poll interval for results-directory rescans.</summary>
    public static readonly TimeSpan DefaultResultPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Default debounce after a FSW signal before reading a result file.</summary>
    public static readonly TimeSpan DefaultFileWriteDebounce = TimeSpan.FromMilliseconds(150);

    /// <summary>Default Telegram long-poll timeout — large value reduces idle wake-ups.</summary>
    public static readonly TimeSpan DefaultLongPollTimeout = TimeSpan.FromSeconds(25);

    /// <summary>Default decline text returned to unverified users.</summary>
    public const string DefaultUnverifiedDecline =
        "Sorry — this bot is private. I can only talk to approved users.";

    /// <summary>Telegram bot token from BotFather. Required.</summary>
    public required string BotToken { get; init; }

    /// <summary>The Telegram user-id of the system owner; <see langword="null"/> if no owner is configured.</summary>
    public long? OwnerUserId { get; init; }

    /// <summary>Pre-approved Telegram user ids — receive <c>Verified</c> access tier.</summary>
    public IReadOnlyList<long> VerifiedUserIds { get; init; } = [];

    /// <summary>Team-member Telegram user ids — receive <c>Team</c> access tier.</summary>
    public IReadOnlyList<long> TeamUserIds { get; init; } = [];

    /// <summary>Interval between result-directory rescans.</summary>
    public TimeSpan ResultPollInterval { get; init; } = DefaultResultPollInterval;

    /// <summary>Debounce after a FSW signal before a result file is read off disk.</summary>
    public TimeSpan FileWriteDebounce { get; init; } = DefaultFileWriteDebounce;

    /// <summary>
    /// Long-poll timeout passed to <c>GetUpdates</c>. Telegram caps this at 50 s.
    /// </summary>
    public TimeSpan LongPollTimeout { get; init; } = DefaultLongPollTimeout;

    /// <summary>
    /// Relative path (under the workspace root) of the offset persistence file. The channel
    /// reads <c>last_update_id</c> on startup and writes it back after every batch so
    /// restarts don't reprocess history.
    /// </summary>
    public string? PersistenceFile { get; init; } = "state/telegram-last-update.json";

    /// <summary>Message sent back to unverified users in lieu of running their request.</summary>
    public string UnverifiedDecline { get; init; } = DefaultUnverifiedDecline;
}
