using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Sutando.Workspace;

namespace Sutando.Proactive;

/// <summary>
/// DI extensions for wiring Sutando's proactive loop into a host.
/// </summary>
public static class ProactiveServiceCollectionExtensions
{
    /// <summary>
    /// Register the cron scheduler + proactive background service. The host is expected to
    /// also register:
    /// <list type="bullet">
    ///   <item><description>A <see cref="WorkspaceDirectory"/> (singleton).</description></item>
    ///   <item><description>An <see cref="IProactivePass"/> implementation (scoped is recommended). Falls back to <see cref="NoopProactivePass"/> when none is provided.</description></item>
    /// </list>
    /// </summary>
    /// <param name="services">Target service collection.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddSutandoProactive(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<CronConfigLoader>();
        services.TryAddSingleton<ICronScheduler, CronScheduler>();
        services.TryAddScoped<IProactivePass, NoopProactivePass>();

        services.AddHostedService<ProactiveBackgroundService>();

        return services;
    }
}
