namespace Sutando.Channels.Discord;

/// <summary>
/// Tunable knobs for <see cref="DiscordChannel"/>. The integrator binds these from
/// configuration / environment variables and hands the record to the channel constructor.
/// </summary>
public sealed record DiscordChannelOptions
{
    /// <summary>Default fallback rescan interval while polling the workspace for new results. 500 ms mirrors upstream.</summary>
    public static readonly TimeSpan DefaultResultPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Discord bot token. Required — the channel refuses to start without one.</summary>
    public required string BotToken { get; init; }

    /// <summary>
    /// The owner's Discord user id. Messages from this user resolve to <see cref="Sutando.Bridge.AccessTier.Owner"/>
    /// and the executor runs them with full capability. <see langword="null"/> means
    /// "no owner configured" — every inbound message resolves to <see cref="Sutando.Bridge.AccessTier.Other"/>.
    /// </summary>
    public ulong? OwnerUserId { get; init; }

    /// <summary>
    /// Role ids that mark a member as Team-tier. Only consulted for channel-context messages —
    /// DMs from non-owner senders default to <see cref="Sutando.Bridge.AccessTier.Other"/> because
    /// roles aren't visible without a guild context.
    /// </summary>
    public IReadOnlyList<ulong> TeamRoleIds { get; init; } = [];

    /// <summary>
    /// Whitelist of guild-channel ids the bot will respond to. Empty list means "no allow-list" —
    /// the bot honours <c>@mention</c> in every channel it can see. DMs are always allowed.
    /// </summary>
    public IReadOnlyList<ulong> AllowedChannelIds { get; init; } = [];

    /// <summary>Fallback rescan interval for the result poller. The FSW on <c>results/</c> handles the fast path; this is the safety net for filesystems that drop events.</summary>
    public TimeSpan ResultPollInterval { get; init; } = DefaultResultPollInterval;
}
