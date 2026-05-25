using Sutando.Workspace;

namespace Sutando.Proactive;

/// <summary>
/// The context handed to an <see cref="IProactivePass"/> on each invocation. Carries the
/// workspace (so the pass can read tasks / write status / inspect state files), the cron
/// entry that fired (when applicable), and a service provider for resolving host-supplied
/// dependencies (an <c>IAgentExecutor</c>, an HTTP client factory, etc.) without forcing
/// this library to take a dependency on any of them.
/// </summary>
public sealed class ProactivePassContext
{
    /// <summary>Initializes a new context.</summary>
    /// <param name="workspace">The workspace the pass should operate against.</param>
    /// <param name="services">Host service provider for resolving runtime-only dependencies.</param>
    /// <param name="triggeringEntry">The cron entry that triggered this pass, or <see langword="null"/> for ad-hoc / manual invocations.</param>
    /// <param name="utcNow">The wall-clock time the pass started (UTC). Captured at dispatch so the pass sees a coherent "now" even if its own work spans seconds.</param>
    public ProactivePassContext(
        WorkspaceDirectory workspace,
        IServiceProvider services,
        CronEntry? triggeringEntry,
        DateTimeOffset utcNow)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(services);
        Workspace = workspace;
        Services = services;
        TriggeringEntry = triggeringEntry;
        UtcNow = utcNow;
    }

    /// <summary>The workspace the pass should read from / write to.</summary>
    public WorkspaceDirectory Workspace { get; }

    /// <summary>Host service provider for resolving runtime-only dependencies.</summary>
    public IServiceProvider Services { get; }

    /// <summary>The cron entry that triggered this pass, or <see langword="null"/> for ad-hoc invocations.</summary>
    public CronEntry? TriggeringEntry { get; }

    /// <summary>The wall-clock time the pass started (UTC).</summary>
    public DateTimeOffset UtcNow { get; }
}
