using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sutando.Workspace;

namespace Sutando.Dashboard;

/// <summary>
/// Static entry point for the Sutando read-only status dashboard.
/// </summary>
/// <remarks>
/// <para>
/// The dashboard serves a single-page HTML status view, a JSON snapshot endpoint, and a
/// SignalR hub (<see cref="StatusHub"/>) that pushes live updates as files in the workspace
/// change. It runs read-only by intent — there is no write surface and no auth.
/// </para>
/// <para>
/// Default port <c>7844</c>, overridable via <c>--port</c>, the <c>DashboardPort</c> config
/// key, or the <c>SUTANDO_DASHBOARD_PORT</c> env var.
/// </para>
/// </remarks>
public static class DashboardCommand
{
    /// <summary>Default listen port — matches upstream <c>dashboard.py</c>.</summary>
    public const int DefaultPort = 7844;

    /// <summary>Configuration key for the listen port.</summary>
    public const string PortConfigKey = "DashboardPort";

    /// <summary>Environment variable that overrides the listen port.</summary>
    public const string PortEnvVar = "SUTANDO_DASHBOARD_PORT";

    /// <summary>Configuration key used by tests to inject a workspace root.</summary>
    public const string WorkspaceRootConfigKey = "WorkspaceRoot";

    /// <summary>Build the host and run it until cancelled.</summary>
    /// <param name="args">Process args; <c>--port &lt;n&gt;</c> is parsed here.</param>
    /// <param name="ct">Cancellation token honoured by <see cref="WebApplication.RunAsync"/>.</param>
    /// <returns>A task that completes once the host stops.</returns>
    public static async Task RunAsync(string[] args, CancellationToken ct)
    {
        var app = Build(args);
        await app.RunAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Build a configured <see cref="WebApplication"/> for embedding scenarios.</summary>
    /// <param name="args">Process args; <c>--port &lt;n&gt;</c> is consumed.</param>
    /// <returns>The built <see cref="WebApplication"/>.</returns>
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        int? cliPort = null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--port" && int.TryParse(args[i + 1], out var p))
            {
                cliPort = p;
                break;
            }
        }
        if (cliPort is not null)
        {
            builder.Configuration[PortConfigKey] = cliPort.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        ConfigureServices(builder);
        var port = ResolvePort(builder.Configuration);
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

        var app = builder.Build();
        MapEndpoints(app);
        return app;
    }

    /// <summary>Configure DI; exposed for tests.</summary>
    /// <param name="builder">The web-application builder to mutate.</param>
    internal static void ConfigureServices(WebApplicationBuilder builder)
    {
        builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });

        builder.Services.AddSignalR();

        builder.Services.AddSingleton<WorkspaceDirectory>(sp =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger<WorkspaceDirectory>();
            var overrideRoot = cfg[WorkspaceRootConfigKey];
            if (!string.IsNullOrWhiteSpace(overrideRoot))
            {
                var dir = new DirectoryInfo(overrideRoot);
                dir.Create();
                Environment.SetEnvironmentVariable(WorkspaceDirectory.EnvVar, overrideRoot);
            }
            return WorkspaceDirectory.Resolve(logger);
        });

        builder.Services.AddSingleton<CoreStatus>();
        builder.Services.AddSingleton<OwnerActivity>();
        builder.Services.AddSingleton<DashboardSnapshot>();

        // Register the broadcaster as a singleton AND as the hosted-service entry point so
        // tests can resolve IBroadcastChannel to drive deterministic events.
        builder.Services.AddSingleton<WorkspaceBroadcaster>();
        builder.Services.AddSingleton<IBroadcastChannel>(sp => sp.GetRequiredService<WorkspaceBroadcaster>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<WorkspaceBroadcaster>());
    }

    /// <summary>Map HTTP and hub endpoints.</summary>
    /// <param name="app">The web application to wire endpoints onto.</param>
    internal static void MapEndpoints(WebApplication app)
    {
        app.MapGet("/", (DashboardSnapshot snapshot) =>
        {
            var snap = snapshot.Capture();
            return Results.Content(DashboardHtml.Render(snap), "text/html; charset=utf-8");
        });

        app.MapGet("/snapshot", (DashboardSnapshot snapshot) => Results.Ok(snapshot.Capture()));

        app.MapGet("/healthz", () => Results.Ok(new HealthResponse
        {
            Ok = true,
            UptimeSeconds = (long)(DateTimeOffset.UtcNow - StartedAt).TotalSeconds,
        }));

        app.MapHub<StatusHub>("/hub/status");
    }

    private static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;

    private static int ResolvePort(IConfiguration config)
    {
        var fromConfig = config[PortConfigKey];
        if (!string.IsNullOrWhiteSpace(fromConfig) && int.TryParse(fromConfig, out var p1))
        {
            return p1;
        }
        var fromEnv = Environment.GetEnvironmentVariable(PortEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv) && int.TryParse(fromEnv, out var p2))
        {
            return p2;
        }
        return DefaultPort;
    }
}

/// <summary>Response body for <c>GET /healthz</c>.</summary>
public sealed record HealthResponse
{
    /// <summary>Always true when the endpoint responded.</summary>
    [JsonPropertyName("ok")] public required bool Ok { get; init; }

    /// <summary>Process uptime in seconds since the dashboard started.</summary>
    [JsonPropertyName("uptime_seconds")] public required long UptimeSeconds { get; init; }
}
