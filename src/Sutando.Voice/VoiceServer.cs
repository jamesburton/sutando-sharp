using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Sutando.Voice;

/// <summary>
/// Builds the minimal-API host for the voice WS server. Public so tests can stand the host up via
/// <c>WebApplicationFactory&lt;Program&gt;</c> while overriding the DI container.
/// </summary>
public static class VoiceServer
{
    /// <summary>
    /// Build a <see cref="WebApplication"/> wired with:
    /// <list type="bullet">
    ///   <item><see cref="VoiceOptions"/> bound from configuration with <c>--port</c>/<c>SUTANDO_VOICE_PORT</c> overrides.</item>
    ///   <item><c>GET /</c> — developer harness HTML.</item>
    ///   <item><c>GET /healthz</c> — liveness probe + active-session count.</item>
    ///   <item><c>GET /voice</c> — WebSocket upgrade.</item>
    /// </list>
    /// </summary>
    /// <param name="args">CLI args. <c>--port &lt;n&gt;</c> overrides the bind port.</param>
    /// <returns>A fully-configured app, ready for <c>RunAsync</c>.</returns>
    public static WebApplication Build(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var builder = WebApplication.CreateBuilder(args);

        // Disable the default logging pipe — keeps the harness output readable. Operators can
        // re-enable via appsettings or env LOGGING__* knobs.
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        });

        builder.Services
            .AddOptions<VoiceOptions>()
            .Bind(builder.Configuration.GetSection(VoiceOptions.SectionName))
            .PostConfigure(options => ApplyOverrides(options, builder.Configuration, args));

        builder.Services.AddSingleton<VoiceSessionTracker>();

        // Transport selection: --local / SUTANDO_VOICE_LOCAL / Voice:UseLocal swaps the cloud
        // Gemini Live transport for the in-process STT → Chat → TTS pipeline. The choice is made
        // once at server boot — every connection uses the same transport.
        if (ResolveUseLocal(builder.Configuration, args))
        {
            builder.Services.AddSingleton<IRealtimeTransportFactory>(sp =>
            {
                var voiceOptions = sp.GetRequiredService<IOptions<VoiceOptions>>().Value;
                var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                return new LocalPipelineTransportFactory(voiceOptions, loggerFactory);
            });
        }
        else
        {
            builder.Services.AddSingleton<IRealtimeTransportFactory, GeminiLiveTransportFactory>();
        }

        builder.Services.AddSingleton<VoiceWebSocketHandler>();

        // Bind to the configured port. Kestrel reads URLs at host build time, so resolve the
        // effective port now (after the post-configure callback) and apply it as a Url override.
        var port = ResolvePort(builder.Configuration, args);
        builder.WebHost.UseUrls($"http://0.0.0.0:{port.ToString(CultureInfo.InvariantCulture)}");

        var app = builder.Build();

        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        });

        // GET / — developer harness page. Streamed from an embedded resource so the package self-
        // describes when installed via dnx.
        app.MapGet("/", () =>
        {
            var html = LoadHarnessHtml();
            return Results.Content(html, "text/html; charset=utf-8");
        });

        // GET /healthz — liveness + active session count.
        app.MapGet("/healthz", (VoiceSessionTracker tracker) =>
        {
            var payload = new HealthResponse("ok", tracker.Count);
            return Results.Json(payload, VoiceJson.Options, contentType: "application/json", statusCode: 200);
        });

        // GET /voice — WebSocket upgrade.
        app.Map("/voice", async (HttpContext ctx, VoiceWebSocketHandler handler) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync("expected websocket upgrade request").ConfigureAwait(false);
                return;
            }
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            await handler.HandleAsync(socket, ctx.RequestAborted).ConfigureAwait(false);
        });

        return app;
    }

    // --- helpers -------------------------------------------------------------

    /// <summary>
    /// Resolves <c>VoiceOptions.ApiKey</c> from environment variables, applies <c>--port</c> /
    /// <c>SUTANDO_VOICE_PORT</c> overrides, and leaves any remaining model/voice keys at their
    /// configured (or defaulted) values. Visibility is internal so tests can call it directly.
    /// </summary>
    /// <param name="options">The options instance being configured.</param>
    /// <param name="config">The host's <see cref="IConfiguration"/>.</param>
    /// <param name="args">CLI args.</param>
    internal static void ApplyOverrides(VoiceOptions options, IConfiguration config, string[] args)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(args);

        // API key — VOICE-specific key wins over the generic one. This mirrors upstream's
        // GEMINI_VOICE_API_KEY → GEMINI_API_KEY fallback.
        var voiceKey = Environment.GetEnvironmentVariable("GEMINI_VOICE_API_KEY");
        var genericKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (!string.IsNullOrEmpty(voiceKey))
        {
            options.ApiKey = voiceKey;
        }
        else if (!string.IsNullOrEmpty(genericKey))
        {
            options.ApiKey = genericKey;
        }

        // Port — CLI > env > config. Configuration already populated options.Port; the override
        // pair below only kicks in when the user supplied a more specific source.
        options.Port = ResolvePort(config, args);

        // Local-inference mode. CLI --local > SUTANDO_VOICE_LOCAL env > Voice:UseLocal config.
        options.UseLocal = ResolveUseLocal(config, args);

        // Model file paths for the --local pipeline. Env vars take precedence over any
        // Voice:LocalModels:* configuration keys the binder already applied.
        ApplyLocalModelOverride(Environment.GetEnvironmentVariable("SUTANDO_WHISPER_MODEL"), v => options.LocalModels.WhisperModel = v);
        ApplyLocalModelOverride(Environment.GetEnvironmentVariable("SUTANDO_LLAMA_MODEL"), v => options.LocalModels.LlamaModel = v);
        ApplyLocalModelOverride(Environment.GetEnvironmentVariable("SUTANDO_KOKORO_MODEL"), v => options.LocalModels.KokoroModel = v);
        ApplyLocalModelOverride(Environment.GetEnvironmentVariable("SUTANDO_SILERO_MODEL"), v => options.LocalModels.SileroModel = v);
    }

    private static void ApplyLocalModelOverride(string? envValue, Action<string> apply)
    {
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            apply(envValue);
        }
    }

    /// <summary>
    /// Resolves whether the voice server should run in local-inference mode. Precedence:
    /// CLI <c>--local</c> &gt; <c>SUTANDO_VOICE_LOCAL</c> env var &gt; <c>Voice:UseLocal</c> config.
    /// </summary>
    /// <param name="config">The host's <see cref="IConfiguration"/>.</param>
    /// <param name="args">CLI args.</param>
    /// <returns><see langword="true"/> when local mode is requested.</returns>
    internal static bool ResolveUseLocal(IConfiguration config, string[] args)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(args);

        // CLI flag — bare --local (or --local=true / --local=false).
        foreach (var a in args)
        {
            if (a == "--local")
            {
                return true;
            }
            if (a.StartsWith("--local=", StringComparison.Ordinal)
                && bool.TryParse(a.AsSpan("--local=".Length), out var fromCli))
            {
                return fromCli;
            }
        }

        var env = Environment.GetEnvironmentVariable("SUTANDO_VOICE_LOCAL");
        if (!string.IsNullOrWhiteSpace(env))
        {
            // Accept the conventional truthy spellings as well as bool.Parse's "true"/"false".
            if (bool.TryParse(env, out var fromEnv))
            {
                return fromEnv;
            }
            return env is "1" or "yes" or "on" || string.Equals(env, "true", StringComparison.OrdinalIgnoreCase);
        }

        return config.GetValue<bool?>($"{VoiceOptions.SectionName}:UseLocal") ?? false;
    }

    private static int ResolvePort(IConfiguration config, string[] args)
    {
        // CLI takes precedence: --port <n> or --port=<n>
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var fromCli))
            {
                return fromCli;
            }
            if (a.StartsWith("--port=", StringComparison.Ordinal)
                && int.TryParse(a.AsSpan("--port=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fromCliEq))
            {
                return fromCliEq;
            }
        }

        var env = Environment.GetEnvironmentVariable("SUTANDO_VOICE_PORT");
        if (!string.IsNullOrEmpty(env) && int.TryParse(env, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fromEnv))
        {
            return fromEnv;
        }

        var fromConfig = config.GetValue<int?>($"{VoiceOptions.SectionName}:Port");
        return fromConfig ?? 9900;
    }

    private static string LoadHarnessHtml()
    {
        // The HTML file is embedded — name is the project root namespace + path under wwwroot.
        var assembly = typeof(VoiceServer).Assembly;
        const string resourceName = "Sutando.Voice.wwwroot.index.html";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            // Defensive fallback so /healthz still tells the operator the host is up even if the
            // resource went missing at pack time.
            return "<!doctype html><meta charset=utf-8><title>sutando voice</title><p>sutando voice — harness not packaged.";
        }
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Health-probe payload. Serialised as <c>{"status":"ok","sessions":&lt;int&gt;}</c>.</summary>
    /// <param name="Status">Always <c>"ok"</c> when the endpoint replies.</param>
    /// <param name="Sessions">Number of currently-active voice sessions.</param>
    internal sealed record HealthResponse(string Status, int Sessions);
}
