using Sutando.Bridge;

namespace Sutando.Channels.Discord;

/// <summary>
/// Pure mapping from <c>(senderUserId, senderRoleIds, isDirectMessage)</c> to the canonical
/// <see cref="AccessTier"/>. Extracted from <see cref="DiscordChannel"/> so tier policy can
/// be unit-tested without spinning up a Discord gateway.
/// </summary>
public static class DiscordTierResolver
{
    /// <summary>Apply the tier rules from <c>discord-bridge.py</c>.</summary>
    /// <param name="senderUserId">Discord user id of the message author.</param>
    /// <param name="senderRoleIds">Role ids the author holds in the message guild — empty for DMs.</param>
    /// <param name="isDirectMessage">
    ///   <see langword="true"/> when the message arrived as a DM. DMs from a non-owner sender
    ///   always resolve to <see cref="AccessTier.Other"/> because role membership isn't visible
    ///   outside guild context.
    /// </param>
    /// <param name="options">Channel options carrying the owner id and team-role allow-list.</param>
    /// <returns>The resolved tier.</returns>
    public static AccessTier Resolve(
        ulong senderUserId,
        IReadOnlyCollection<ulong> senderRoleIds,
        bool isDirectMessage,
        DiscordChannelOptions options)
    {
        ArgumentNullException.ThrowIfNull(senderRoleIds);
        ArgumentNullException.ThrowIfNull(options);

        if (options.OwnerUserId is { } ownerId && senderUserId == ownerId)
        {
            return AccessTier.Owner;
        }

        // Non-owner DMs cannot prove team membership (no guild context), so drop to Other.
        if (isDirectMessage)
        {
            return AccessTier.Other;
        }

        if (options.TeamRoleIds.Count > 0 && senderRoleIds.Any(r => options.TeamRoleIds.Contains(r)))
        {
            return AccessTier.Team;
        }

        return AccessTier.Other;
    }
}
