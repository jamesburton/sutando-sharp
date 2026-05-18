namespace Sutando.Channels;

/// <summary>
/// Minimal channel-adapter surface. A channel writes inbound user input into the task
/// bridge (as <c>task-*.txt</c> envelopes) and surfaces outbound results back to the
/// originating user / chat.
/// </summary>
/// <remarks>
/// <para>
/// Concrete channels live in their own projects — <c>Sutando.Channels.Cli</c> for the
/// local REPL, <c>Sutando.Channels.Telegram</c>, <c>Sutando.Channels.Discord</c>, etc.
/// Each channel is independently activatable: the host process picks which channels to
/// run based on configuration / available credentials.
/// </para>
/// <para>
/// Channels MUST NOT call <see cref="Sutando"/>'s agent executor directly. They go
/// through the bridge so the proactive loop, sandboxing, dedup, and archive behaviour
/// remain centralised.
/// </para>
/// </remarks>
public interface IChannel
{
    /// <summary>Short identifier — <c>cli</c>, <c>telegram</c>, <c>discord</c>, <c>voice</c>, ….</summary>
    string Id { get; }

    /// <summary>Run the channel loop until <paramref name="ct"/> is cancelled or the user exits.</summary>
    /// <param name="ct">Cancellation token tied to process lifetime (Ctrl+C / SIGTERM).</param>
    Task RunAsync(CancellationToken ct);
}
