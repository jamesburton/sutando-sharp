namespace Sutando.Channels.Cli;

/// <summary>
/// Tunable knobs for <see cref="CliChatChannel"/>. Tests inject small intervals; the CLI
/// dispatch path uses defaults that mirror upstream's Telegram bridge.
/// </summary>
public sealed record CliChatChannelOptions
{
    /// <summary>Default per-task wait — the CLI dispatch uses 5 minutes if the user doesn't override.</summary>
    public static readonly TimeSpan DefaultResultTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Default fallback rescan interval while waiting for a result. 1 s mirrors upstream.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>Default debounce after a FileSystemWatcher event to let the writer flush.</summary>
    public static readonly TimeSpan DefaultFileWriteDebounce = TimeSpan.FromMilliseconds(250);

    /// <summary>Version string printed in the greeting; the integrator passes this through.</summary>
    public string Version { get; init; } = "dev";

    /// <summary>Wall-clock budget for any single user message.</summary>
    public TimeSpan ResultTimeout { get; init; } = DefaultResultTimeout;

    /// <summary>Fallback poll interval while a result is pending.</summary>
    public TimeSpan PollInterval { get; init; } = DefaultPollInterval;

    /// <summary>Debounce after a FSW signal before the body is read off disk.</summary>
    public TimeSpan FileWriteDebounce { get; init; } = DefaultFileWriteDebounce;

    /// <summary>Originating user identifier on every task envelope; defaults to <c>chat-local</c>.</summary>
    public string UserId { get; init; } = "chat-local";

    /// <summary>Channel id stamped on every envelope.</summary>
    public string ChannelId { get; init; } = "local-chat";
}
