namespace Sutando.Channels.Cli;

/// <summary>
/// Minimal channel-adapter surface. A channel writes inbound user input into the task
/// bridge and surfaces outbound results back to the user.
/// </summary>
/// <remarks>
/// Defined here for now so the local-CLI chat channel is self-contained. When a second
/// channel lands (telegram, discord, voice) this should be promoted to a shared
/// <c>Sutando.Channels.Abstractions</c> project.
/// </remarks>
public interface IChannel
{
    /// <summary>Run the channel loop until <paramref name="ct"/> is cancelled or the user exits.</summary>
    /// <param name="ct">Cancellation token tied to process lifetime (Ctrl+C).</param>
    Task RunAsync(CancellationToken ct);
}
