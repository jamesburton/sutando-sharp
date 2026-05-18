using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sutando.Bridge;
using Sutando.Workspace;

namespace Sutando.Api;

/// <summary>
/// Static entry point for the Sutando HTTP task-submission API.
/// </summary>
/// <remarks>
/// <para>
/// This is a thin wrapper that builds and runs an ASP.NET Core minimal-API host on port
/// <c>7843</c> (configurable via the <c>ApiPort</c> setting, the <c>--port</c> CLI flag, or
/// the <c>SUTANDO_API_PORT</c> environment variable). It is intentionally trivial to call
/// from the future <c>sutando api</c> CLI verb.
/// </para>
/// <para>
/// Auth is bearer-token based; the token is read from the <c>SUTANDO_API_TOKEN</c> env var.
/// If unset, the API runs open and logs a warning on startup. The <c>/healthz</c> endpoint
/// never requires auth.
/// </para>
/// </remarks>
public static class ApiCommand
{
    /// <summary>Default listen port — matches upstream <c>agent-api.py</c>.</summary>
    public const int DefaultPort = 7843;

    /// <summary>Configuration key for the listen port.</summary>
    public const string PortConfigKey = "ApiPort";

    /// <summary>Environment variable that overrides the listen port.</summary>
    public const string PortEnvVar = "SUTANDO_API_PORT";

    /// <summary>Environment variable that holds the bearer token. When unset, auth is disabled.</summary>
    public const string TokenEnvVar = "SUTANDO_API_TOKEN";

    /// <summary>Configuration key used by tests to inject a workspace root without mutating env vars.</summary>
    public const string WorkspaceRootConfigKey = "WorkspaceRoot";

    /// <summary>Build the host and run it until cancelled.</summary>
    /// <param name="args">Process args; <c>--port &lt;n&gt;</c> is parsed here, anything else is forwarded to <see cref="WebApplication"/>.</param>
    /// <param name="ct">Cancellation token honoured by <see cref="WebApplication.RunAsync"/>.</param>
    /// <returns>A task that completes once the host stops.</returns>
    public static async Task RunAsync(string[] args, CancellationToken ct)
    {
        var app = Build(args);
        await app.RunAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Build the configured <see cref="WebApplication"/> without running it. Tests inject their
    /// own host via <c>WebApplicationFactory&lt;Program&gt;</c> and therefore never call this
    /// method, but it is exposed for embedding scenarios that just want a hot host.
    /// </summary>
    /// <param name="args">Process args. <c>--port &lt;n&gt;</c> is consumed; remaining args are kept.</param>
    /// <returns>A built <see cref="WebApplication"/> ready to run.</returns>
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // CLI override: --port <n>. Parsed here (not via Kestrel directly) so it composes with
        // the env var / config fallback that ConfigurePort applies inside ConfigureServices.
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

    /// <summary>Configure DI for the API. Exposed so tests can call it from a test host builder.</summary>
    /// <param name="builder">The web-application builder to mutate.</param>
    internal static void ConfigureServices(WebApplicationBuilder builder)
    {
        builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });

        builder.Services.AddSingleton<WorkspaceDirectory>(sp =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger<WorkspaceDirectory>();
            var overrideRoot = cfg[WorkspaceRootConfigKey];
            if (!string.IsNullOrWhiteSpace(overrideRoot))
            {
                // Test / embedded path: caller dictates the workspace root explicitly.
                var dir = new DirectoryInfo(overrideRoot);
                dir.Create();
                Environment.SetEnvironmentVariable(WorkspaceDirectory.EnvVar, overrideRoot);
            }
            return WorkspaceDirectory.Resolve(logger);
        });

        builder.Services.AddSingleton<CoreStatus>();
        builder.Services.AddSingleton<TaskArchive>();

        builder.Services.AddSingleton(sp =>
        {
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            return new ApiAuth(Environment.GetEnvironmentVariable(TokenEnvVar), loggerFactory.CreateLogger<ApiAuth>());
        });
    }

    /// <summary>Map all HTTP endpoints onto the running app. Idempotent within a single build.</summary>
    /// <param name="app">The web application to wire endpoints onto.</param>
    internal static void MapEndpoints(WebApplication app)
    {
        var auth = app.Services.GetRequiredService<ApiAuth>();
        auth.LogStartupBanner(app.Logger);

        // /healthz is always open — used by load balancers + container probes.
        app.MapGet("/healthz", (WorkspaceDirectory ws) => Results.Ok(new HealthResponse
        {
            Ok = true,
            UptimeSeconds = (long)(DateTimeOffset.UtcNow - StartedAt).TotalSeconds,
            Workspace = ws.Root.FullName,
        }));

        // Everything else funnels through the bearer-auth filter.
        var protectedGroup = app.MapGroup(string.Empty);
        protectedGroup.AddEndpointFilter(new BearerAuthFilter(auth));

        protectedGroup.MapPost("/tasks", SubmitTask);
        protectedGroup.MapGet("/tasks", ListTasks);
        protectedGroup.MapGet("/tasks/{id}", GetTask);
        protectedGroup.MapGet("/status", GetStatus);
    }

    private static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;

    private static int ResolvePort(IConfiguration config)
    {
        // Precedence: explicit config (set by --port if present) → env var → default.
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

    private static async Task<IResult> SubmitTask(HttpContext ctx, [FromServices] WorkspaceDirectory ws)
    {
        SubmitTaskRequest? req;
        try
        {
            req = await ctx.Request.ReadFromJsonAsync<SubmitTaskRequest>(GetJsonOptions(ctx)).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new ErrorResponse { Error = "invalid JSON" });
        }
        if (req is null || string.IsNullOrWhiteSpace(req.Body))
        {
            return Results.BadRequest(new ErrorResponse { Error = "body is required" });
        }

        var id = $"task-api-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        var envelope = new TaskEnvelope
        {
            Id = id,
            Timestamp = DateTimeOffset.UtcNow,
            Body = req.Body,
            Source = TaskSource.Api,
            ChannelId = string.IsNullOrWhiteSpace(req.ChannelId) ? "api-default" : req.ChannelId!,
            UserId = string.IsNullOrWhiteSpace(req.UserId) ? "api-client" : req.UserId!,
            AccessTier = AccessTier.Verified,
            Priority = ParsePriority(req.Priority) ?? TaskPriority.Normal,
            Timeout = req.TimeoutMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null,
        };

        var path = TaskFile.Write(ws.Tasks.FullName, envelope);
        return Results.Json(
            new SubmitTaskResponse { Id = id, Path = path },
            GetJsonOptions(ctx),
            statusCode: StatusCodes.Status202Accepted);
    }

    private static IResult ListTasks([FromServices] WorkspaceDirectory ws)
    {
        var manifest = new List<TaskManifestEntry>();
        if (!ws.Tasks.Exists)
        {
            return Results.Ok(manifest);
        }
        foreach (var file in ws.Tasks.EnumerateFiles("task-*.txt", SearchOption.TopDirectoryOnly)
                     .OrderByDescending(f => f.LastWriteTimeUtc))
        {
            if (TryReadEnvelope(file.FullName, out var env))
            {
                manifest.Add(BuildManifestEntry(env, file));
            }
        }
        return Results.Ok(manifest);
    }

    private static IResult GetTask(string id, [FromServices] WorkspaceDirectory ws, [FromServices] TaskArchive archive)
    {
        var safeId = SanitizeId(id);
        if (safeId is null)
        {
            return Results.BadRequest(new ErrorResponse { Error = "invalid id" });
        }

        var taskPath = FindTaskFile(ws, archive, safeId);
        var resultPath = FindResultFile(ws, safeId);

        if (taskPath is null && resultPath is null)
        {
            return Results.NotFound(new ErrorResponse { Error = "task not found" });
        }

        TaskEnvelope? envelope = null;
        if (taskPath is not null && TryReadEnvelope(taskPath, out var env))
        {
            envelope = env;
        }

        string? resultText = null;
        if (resultPath is not null)
        {
            try
            {
                resultText = File.ReadAllText(resultPath);
            }
            catch (IOException)
            {
                // best-effort; an unreadable result still surfaces the task.
            }
        }

        return Results.Ok(new TaskDetailResponse
        {
            Id = safeId,
            Status = resultText is null ? "pending" : "completed",
            Task = envelope is null ? null : new TaskManifestEntry
            {
                Id = envelope.Id,
                Source = SerializeSource(envelope.Source),
                Priority = SerializePriority(envelope.Priority),
                Timestamp = envelope.Timestamp,
                BodyPreview = Preview(envelope.Body),
            },
            Result = resultText,
        });
    }

    private static IResult GetStatus([FromServices] CoreStatus status)
    {
        var payload = status.Read();
        if (payload is null)
        {
            return Results.Ok(new CoreStatusPayload { Status = CoreStatus.Idle, Ts = 0 });
        }
        return Results.Ok(payload);
    }

    private static JsonSerializerOptions GetJsonOptions(HttpContext ctx)
    {
        var opts = ctx.RequestServices.GetService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>();
        return opts?.Value.SerializerOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        };
    }

    private static string? FindTaskFile(WorkspaceDirectory ws, TaskArchive archive, string id)
    {
        var live = Path.Combine(ws.Tasks.FullName, id + ".txt");
        if (File.Exists(live))
        {
            return live;
        }
        return archive.FindArchivedTask(id);
    }

    private static string? FindResultFile(WorkspaceDirectory ws, string id)
    {
        var fileName = id + ".txt";
        var live = Path.Combine(ws.Results.FullName, fileName);
        if (File.Exists(live))
        {
            return live;
        }
        var archiveRoot = new DirectoryInfo(Path.Combine(ws.Results.FullName, "archive"));
        if (!archiveRoot.Exists)
        {
            return null;
        }
        foreach (var month in archiveRoot.EnumerateDirectories())
        {
            var candidate = Path.Combine(month.FullName, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private static bool TryReadEnvelope(string path, out TaskEnvelope envelope)
    {
        try
        {
            envelope = TaskFile.ParseFile(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or FormatException)
        {
            envelope = null!;
            return false;
        }
    }

    private static TaskManifestEntry BuildManifestEntry(TaskEnvelope env, FileInfo file)
    {
        // Timestamp from the envelope itself is authoritative; mtime is a fallback for legacy
        // tasks written before the envelope-timestamp field was mandatory.
        var ts = env.Timestamp == default
            ? new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero)
            : env.Timestamp;
        return new TaskManifestEntry
        {
            Id = env.Id,
            Source = SerializeSource(env.Source),
            Priority = SerializePriority(env.Priority),
            Timestamp = ts,
            BodyPreview = Preview(env.Body),
        };
    }

    private static string Preview(string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return string.Empty;
        }
        var firstLine = body.Split('\n', 2)[0];
        return firstLine.Length > 80 ? firstLine[..80] : firstLine;
    }

    private static TaskPriority? ParsePriority(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        return raw.Trim().ToLowerInvariant() switch
        {
            "urgent" => TaskPriority.Urgent,
            "normal" => TaskPriority.Normal,
            "low" => TaskPriority.Low,
            _ => null,
        };
    }

    private static string SerializeSource(TaskSource source) => source switch
    {
        TaskSource.Voice => "voice",
        TaskSource.Chat => "chat",
        TaskSource.Telegram => "telegram",
        TaskSource.Discord => "discord",
        TaskSource.Phone => "phone",
        TaskSource.Api => "api",
        TaskSource.Cron => "cron",
        TaskSource.Health => "health",
        TaskSource.Proactive => "proactive",
        _ => source.ToString().ToLowerInvariant(),
    };

    private static string SerializePriority(TaskPriority priority) => priority switch
    {
        TaskPriority.Urgent => "urgent",
        TaskPriority.Normal => "normal",
        TaskPriority.Low => "low",
        _ => priority.ToString().ToLowerInvariant(),
    };

    private static string? SanitizeId(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        // Path-traversal defense: only allow chars consistent with our id grammar.
        foreach (var ch in raw)
        {
            if (!(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.'))
            {
                return null;
            }
        }
        return raw;
    }
}
