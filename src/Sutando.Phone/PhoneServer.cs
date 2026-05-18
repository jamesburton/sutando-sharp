using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sutando.Bridge;
using Sutando.Realtime;
using Sutando.Workspace;
using Twilio.Security;

namespace Sutando.Phone;

/// <summary>
/// Builds the minimal-API host for the Twilio phone bridge.
/// </summary>
/// <remarks>
/// Public so tests can stand the host up via <c>WebApplicationFactory&lt;Program&gt;</c>
/// while overriding the DI container. Same structural pattern as Sutando.Voice,
/// Sutando.Api, and Sutando.Dashboard — port resolution, options binding, then route
/// mapping.
/// </remarks>
public static class PhoneServer
{
    /// <summary>Default bind port. Matches upstream's <c>PHONE_PORT</c> default.</summary>
    public const int DefaultPort = 3100;

    /// <summary>Configuration key used by tests to inject a workspace root.</summary>
    public const string WorkspaceRootConfigKey = "WorkspaceRoot";

    /// <summary>Build a configured <see cref="WebApplication"/>.</summary>
    /// <param name="args">CLI args. <c>--port &lt;n&gt;</c> overrides the bind port.</param>
    /// <returns>The fully-wired app, ready for <c>RunAsync</c>.</returns>
    public static WebApplication Build(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var builder = WebApplication.CreateBuilder(args);

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        });

        builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });

        builder.Services
            .AddOptions<PhoneOptions>()
            .Bind(builder.Configuration.GetSection(PhoneOptions.SectionName))
            .PostConfigure(options => ApplyOverrides(options, builder.Configuration, args));

        builder.Services.AddSingleton<CallTracker>();
        builder.Services.AddSingleton<CallMetadataStore>();
        builder.Services.AddSingleton<IPhoneTransportFactory, GeminiLivePhoneTransportFactory>();
        builder.Services.AddSingleton<TwilioMediaSocketHandler>();

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

        // The Twilio REST client is the outbound surface — tests substitute a fake. The default
        // registration deferral pattern (Func<>) is used because the auth token + sid are not
        // known until options are bound.
        builder.Services.AddSingleton<ITwilioRestClient>(sp =>
        {
            var phoneOpts = sp.GetRequiredService<IOptions<PhoneOptions>>().Value;
            if (string.IsNullOrWhiteSpace(phoneOpts.AccountSid) || string.IsNullOrWhiteSpace(phoneOpts.AuthToken))
            {
                // Sentinel: the outbound endpoint becomes a 503 because the singleton is a no-op
                // adapter. We avoid throwing here so the inbound webhook surface still works in
                // dev / dry-run setups that lack a Twilio account.
                return new InertTwilioRestClient(sp.GetRequiredService<ILoggerFactory>().CreateLogger<InertTwilioRestClient>());
            }
            return new TwilioRestClientAdapter(phoneOpts.AccountSid, phoneOpts.AuthToken);
        });

        var port = ResolvePort(builder.Configuration, args);
        builder.WebHost.UseUrls($"http://0.0.0.0:{port.ToString(CultureInfo.InvariantCulture)}");

        var app = builder.Build();

        // Warn at startup so the operator notices missing config — same approach as Sutando.Api.
        LogStartupBanner(app);

        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        });

        MapEndpoints(app);
        return app;
    }

    // --- endpoint wiring -----------------------------------------------------

    private static void MapEndpoints(WebApplication app)
    {
        // /healthz — open, no auth. Payload shape distinct from Sutando.Voice ('sessions'):
        // phone uses 'active_calls' as the task brief specifies.
        app.MapGet("/healthz", (CallTracker tracker) =>
        {
            return Results.Json(new HealthResponse("ok", tracker.Count), PhoneJson.Options, contentType: "application/json", statusCode: 200);
        });

        // POST /twilio/incoming — answer call, return TwiML opening a Media Streams connection.
        // Wrapped in a Delegate cast so the IResult return path is preserved (without the cast
        // the ASP0016 analyzer warns that the result is discarded by the RequestDelegate).
        app.MapPost("/twilio/incoming", (Delegate)(async (HttpContext ctx) => await HandleIncomingAsync(ctx).ConfigureAwait(false)));

        // POST /twilio/status — Twilio's call-lifecycle callbacks.
        app.MapPost("/twilio/status", (Delegate)(async (HttpContext ctx) => await HandleStatusAsync(ctx).ConfigureAwait(false)));

        // POST /twilio/outbound — initiate outbound call. Bearer-gated.
        app.MapPost("/twilio/outbound", (Delegate)(async (HttpContext ctx) => await HandleOutboundAsync(ctx).ConfigureAwait(false)));

        // GET /twilio/media — Media Streams WebSocket upgrade.
        app.Map("/twilio/media", async (HttpContext ctx, TwilioMediaSocketHandler handler) =>
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
    }

    private static async Task<IResult> HandleIncomingAsync(HttpContext ctx)
    {
        var opts = ctx.RequestServices.GetRequiredService<IOptions<PhoneOptions>>().Value;
        var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Phone.Incoming");

        if (!await IsValidTwilioRequestAsync(ctx, opts).ConfigureAwait(false))
        {
            logger.LogWarning("Rejecting unsigned /twilio/incoming from {RemoteIp}.", ctx.Connection.RemoteIpAddress);
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted).ConfigureAwait(false);
        var from = form["From"].ToString();
        var to = form["To"].ToString();
        var callSid = form["CallSid"].ToString();
        var stir = form["StirVerstat"].ToString();

        var tier = PhoneTierResolver.Resolve(from, stir, opts, out var downgraded);
        if (downgraded)
        {
            logger.LogWarning(
                "STIR/SHAKEN downgrade — owner candidate {From} arrived without A-level attestation (stirVerstat={Stir}). Dropping to verified.",
                from, string.IsNullOrEmpty(stir) ? "(none)" : stir);
        }
        logger.LogInformation(
            "Inbound call {CallSid} from {From} to {To} → tier={Tier} downgraded={Downgraded} stir={Stir}",
            callSid, from, to, tier, downgraded, string.IsNullOrEmpty(stir) ? "(none)" : stir);

        var twiml = BuildIncomingTwiml(ctx.Request, opts, from, stir, tier, downgraded);
        return Results.Content(twiml, "application/xml; charset=utf-8");
    }

    private static async Task<IResult> HandleStatusAsync(HttpContext ctx)
    {
        var opts = ctx.RequestServices.GetRequiredService<IOptions<PhoneOptions>>().Value;
        var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Phone.Status");

        if (!await IsValidTwilioRequestAsync(ctx, opts).ConfigureAwait(false))
        {
            logger.LogWarning("Rejecting unsigned /twilio/status from {RemoteIp}.", ctx.Connection.RemoteIpAddress);
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted).ConfigureAwait(false);
        var callSid = form["CallSid"].ToString();
        var callStatus = form["CallStatus"].ToString();
        logger.LogInformation("Twilio status callback — call={CallSid} status={Status}.", callSid, callStatus);
        // The lifecycle metadata is captured by the Media Streams handler from the start/stop
        // envelopes; status callbacks are surfaced here for observability but don't mutate the
        // call record (avoid double-bookkeeping).
        return Results.Ok();
    }

    private static async Task<IResult> HandleOutboundAsync(HttpContext ctx)
    {
        var opts = ctx.RequestServices.GetRequiredService<IOptions<PhoneOptions>>().Value;
        var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Phone.Outbound");

        // Auth — bearer token only. Unlike the inbound webhooks (which use Twilio signature
        // validation), outbound calls are operator-initiated so a shared secret is appropriate.
        if (string.IsNullOrWhiteSpace(opts.OutboundBearerToken))
        {
            logger.LogWarning("Refusing /twilio/outbound — outbound bearer token is not configured.");
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        var authHeader = ctx.Request.Headers.Authorization.ToString();
        var expected = "Bearer " + opts.OutboundBearerToken;
        if (!string.Equals(authHeader, expected, StringComparison.Ordinal))
        {
            return Results.Unauthorized();
        }

        OutboundRequest? body;
        try
        {
            body = await ctx.Request.ReadFromJsonAsync<OutboundRequest>(PhoneJson.Options, ctx.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new ErrorResponse("invalid JSON"));
        }

        if (body is null || string.IsNullOrWhiteSpace(body.To))
        {
            return Results.BadRequest(new ErrorResponse("'to' is required"));
        }

        var from = string.IsNullOrWhiteSpace(body.From) ? opts.PhoneNumber : body.From!;
        if (string.IsNullOrWhiteSpace(from))
        {
            return Results.BadRequest(new ErrorResponse("'from' is required when TWILIO_PHONE_NUMBER is unset"));
        }

        var twiml = BuildOutboundTwiml(ctx.Request, opts, body);
        var client = ctx.RequestServices.GetRequiredService<ITwilioRestClient>();

        try
        {
            var sid = await client.CreateCallAsync(
                to: body.To!,
                from: from,
                twimlUrl: null,
                twiml: twiml,
                statusCallback: null,
                ct: ctx.RequestAborted).ConfigureAwait(false);
            logger.LogInformation("Outbound call placed to {To} (sid={Sid}).", body.To, sid);
            return Results.Json(new OutboundResponse(sid), PhoneJson.Options, statusCode: StatusCodes.Status202Accepted);
        }
        catch (NotSupportedException ex)
        {
            // Inert client surfaces this when Twilio creds are not configured.
            logger.LogWarning(ex, "Outbound call refused — Twilio creds missing.");
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    // --- TwiML builders ------------------------------------------------------

    /// <summary>Build the TwiML response for an inbound call — opens a Media Streams connection.</summary>
    /// <param name="request">Inbound HTTP request — used to derive the public host for the WSS URL.</param>
    /// <param name="opts">Phone options (carrier of <c>PublicHost</c>).</param>
    /// <param name="from">Caller-id from the Twilio form.</param>
    /// <param name="stir">Raw StirVerstat value (may be empty).</param>
    /// <param name="tier">Resolved tier.</param>
    /// <param name="downgraded">Whether STIR forced a tier drop.</param>
    /// <returns>The TwiML XML body.</returns>
    /// <remarks>
    /// Internal so unit tests can call it directly without standing the host up. The
    /// <c>&lt;Parameter&gt;</c> entries carry the resolved-tier decision forward to the
    /// Media Streams WS handler so the handler doesn't have to recompute it.
    /// </remarks>
    internal static string BuildIncomingTwiml(
        HttpRequest request,
        PhoneOptions opts,
        string from,
        string? stir,
        AccessTier tier,
        bool downgraded)
    {
        var wsHost = ResolvePublicWsHost(request, opts);
        var streamUrl = $"wss://{wsHost}/twilio/media";
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.Append("<Response>");
        sb.Append("<Connect>");
        sb.Append(CultureInfo.InvariantCulture, $"<Stream url=\"{XmlEscape(streamUrl)}\">");
        AppendParameter(sb, "from", from ?? string.Empty);
        if (!string.IsNullOrEmpty(stir))
        {
            AppendParameter(sb, "stirVerstat", stir);
        }
        AppendParameter(sb, "tier", tier.ToString());
        AppendParameter(sb, "tierDowngraded", downgraded ? "true" : "false");
        sb.Append("</Stream>");
        sb.Append("</Connect>");
        sb.Append("</Response>");
        return sb.ToString();
    }

    /// <summary>Build the inline TwiML used for an outbound call.</summary>
    /// <remarks>
    /// Outbound calls open the same Media Streams pipe as inbound. The body's <c>message</c>
    /// / <c>task</c> fields are surfaced as Stream parameters so the WS handler can prime the
    /// voice session with them. The actual delivery is the model's job.
    /// </remarks>
    private static string BuildOutboundTwiml(HttpRequest request, PhoneOptions opts, OutboundRequest body)
    {
        var wsHost = ResolvePublicWsHost(request, opts);
        var streamUrl = $"wss://{wsHost}/twilio/media";
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.Append("<Response>");
        sb.Append("<Connect>");
        sb.Append(CultureInfo.InvariantCulture, $"<Stream url=\"{XmlEscape(streamUrl)}\">");
        // Outbound calls are operator-initiated so the recipient's tier is unknown. Default to
        // verified so the model can use the limited tool set; the operator-set bearer token
        // already controls who can invoke the endpoint.
        AppendParameter(sb, "tier", AccessTier.Verified.ToString());
        AppendParameter(sb, "direction", "outbound");
        if (!string.IsNullOrEmpty(body.Message))
        {
            AppendParameter(sb, "message", body.Message!);
        }
        if (!string.IsNullOrEmpty(body.Task))
        {
            AppendParameter(sb, "task", body.Task!);
        }
        sb.Append("</Stream>");
        sb.Append("</Connect>");
        sb.Append("</Response>");
        return sb.ToString();
    }

    private static void AppendParameter(StringBuilder sb, string name, string value)
    {
        sb.Append(CultureInfo.InvariantCulture, $"<Parameter name=\"{XmlEscape(name)}\" value=\"{XmlEscape(value)}\"/>");
    }

    private static string XmlEscape(string raw) => raw
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);

    /// <summary>Resolve the publicly-reachable host the Media Streams WS endpoint sits behind.</summary>
    /// <remarks>
    /// Twilio needs a fully-qualified WSS URL — it won't follow a redirect to a host name it
    /// hasn't already resolved. <see cref="PhoneOptions.PublicHost"/> wins; otherwise we fall
    /// back to the inbound request's host (works for ngrok / Cloud Run / reverse-proxied setups
    /// where the proxy forwards the original Host header).
    /// </remarks>
    private static string ResolvePublicWsHost(HttpRequest request, PhoneOptions opts)
    {
        if (!string.IsNullOrWhiteSpace(opts.PublicHost))
        {
            return opts.PublicHost!;
        }
        return request.Host.HasValue ? request.Host.Value : "localhost";
    }

    // --- Twilio signature validation -----------------------------------------

    /// <summary>
    /// HMAC-SHA1 webhook signature check using the Twilio SDK's <see cref="RequestValidator"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Internal so tests can call it directly. <see cref="PhoneOptions.AllowUnsignedWebhooks"/>
    /// is the dev-mode bypass — used by <c>WebApplicationFactory</c> tests to drive the
    /// webhook surface without signing each request.
    /// </para>
    /// <para>
    /// We read the form first (it caches on <c>HttpRequest.Form</c>) so the handler can re-read
    /// it without paying a second parse cost.
    /// </para>
    /// </remarks>
    internal static async Task<bool> IsValidTwilioRequestAsync(HttpContext ctx, PhoneOptions opts)
    {
        if (opts.AllowUnsignedWebhooks)
        {
            return true;
        }
        if (string.IsNullOrEmpty(opts.AuthToken))
        {
            // Refuse silently — the operator must either set the token or set the dev bypass.
            return false;
        }
        if (!ctx.Request.HasFormContentType)
        {
            // Twilio always sends application/x-www-form-urlencoded. Anything else is suspect.
            return false;
        }

        await ctx.Request.ReadFormAsync(ctx.RequestAborted).ConfigureAwait(false);
        var dict = ctx.Request.Form.ToDictionary(kv => kv.Key, kv => kv.Value.ToString(), StringComparer.Ordinal);
        var url = BuildFullUrl(ctx.Request, opts);
        var expected = ctx.Request.Headers["X-Twilio-Signature"].ToString();
        if (string.IsNullOrEmpty(expected))
        {
            return false;
        }
        var validator = new RequestValidator(opts.AuthToken);
        return validator.Validate(url, dict, expected);
    }

    /// <summary>Compose the full URL Twilio used to compute its signature.</summary>
    /// <remarks>
    /// When the host is behind a TLS-terminating proxy (the common case for ngrok / Cloud Run),
    /// the local <c>request.Scheme</c> is <c>http</c> even though Twilio dialled an <c>https</c>
    /// URL. <see cref="PhoneOptions.PublicHost"/> + the Host header give us the canonical URL.
    /// </remarks>
    private static string BuildFullUrl(HttpRequest request, PhoneOptions opts)
    {
        // Prefer the publicly-configured host so the URL exactly matches what Twilio hashed.
        var host = !string.IsNullOrWhiteSpace(opts.PublicHost)
            ? opts.PublicHost!
            : request.Host.HasValue ? request.Host.Value : "localhost";
        // Twilio almost always hits the bridge via HTTPS. If the request arrived with X-Forwarded-Proto
        // set to https (proxy / ngrok) honour that; otherwise use the local scheme.
        var scheme = request.Headers["X-Forwarded-Proto"].ToString();
        if (string.IsNullOrEmpty(scheme))
        {
            scheme = request.Scheme;
        }
        return $"{scheme}://{host}{request.Path}{request.QueryString}";
    }

    // --- overrides / port resolution -----------------------------------------

    /// <summary>
    /// Resolves config keys from environment variables. Internal so tests can call it
    /// directly when staging fixtures.
    /// </summary>
    /// <param name="options">Options being configured.</param>
    /// <param name="config">Host configuration.</param>
    /// <param name="args">Process args.</param>
    internal static void ApplyOverrides(PhoneOptions options, IConfiguration config, string[] args)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(args);

        // Gemini key — voice-specific wins over generic.
        var voiceKey = Environment.GetEnvironmentVariable(PhoneEnv.GeminiVoiceKey);
        var genericKey = Environment.GetEnvironmentVariable(PhoneEnv.GeminiKey);
        if (!string.IsNullOrEmpty(voiceKey))
        {
            options.ApiKey = voiceKey;
        }
        else if (!string.IsNullOrEmpty(genericKey))
        {
            options.ApiKey = genericKey;
        }

        // Twilio identity.
        var sid = Environment.GetEnvironmentVariable(PhoneEnv.TwilioAccountSid);
        if (!string.IsNullOrEmpty(sid))
        {
            options.AccountSid = sid;
        }
        var token = Environment.GetEnvironmentVariable(PhoneEnv.TwilioAuthToken);
        if (!string.IsNullOrEmpty(token))
        {
            options.AuthToken = token;
        }
        var phone = Environment.GetEnvironmentVariable(PhoneEnv.TwilioPhoneNumber);
        if (!string.IsNullOrEmpty(phone))
        {
            options.PhoneNumber = phone;
        }

        // Tier policy allow-lists.
        var owner = Environment.GetEnvironmentVariable(PhoneEnv.OwnerNumber);
        if (!string.IsNullOrEmpty(owner))
        {
            options.OwnerNumbers = owner;
        }
        var verified = Environment.GetEnvironmentVariable(PhoneEnv.VerifiedCallers);
        if (!string.IsNullOrEmpty(verified))
        {
            options.VerifiedCallers = verified;
        }

        // Outbound bearer.
        var outbound = Environment.GetEnvironmentVariable(PhoneEnv.OutboundBearer);
        if (!string.IsNullOrEmpty(outbound))
        {
            options.OutboundBearerToken = outbound;
        }

        var publicHost = Environment.GetEnvironmentVariable(PhoneEnv.PublicHost);
        if (!string.IsNullOrEmpty(publicHost))
        {
            options.PublicHost = publicHost;
        }

        if (string.Equals(Environment.GetEnvironmentVariable(PhoneEnv.AllowUnsigned), "true", StringComparison.OrdinalIgnoreCase))
        {
            options.AllowUnsignedWebhooks = true;
        }

        options.Port = ResolvePort(config, args);
    }

    private static int ResolvePort(IConfiguration config, string[] args)
    {
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

        var env = Environment.GetEnvironmentVariable(PhoneEnv.Port);
        if (!string.IsNullOrEmpty(env) && int.TryParse(env, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fromEnv))
        {
            return fromEnv;
        }

        var fromConfig = config.GetValue<int?>($"{PhoneOptions.SectionName}:Port");
        return fromConfig ?? DefaultPort;
    }

    private static void LogStartupBanner(WebApplication app)
    {
        var opts = app.Services.GetRequiredService<IOptions<PhoneOptions>>().Value;
        if (opts.AllowUnsignedWebhooks)
        {
            app.Logger.LogWarning(
                "WEBHOOK SIGNATURE VALIDATION IS DISABLED — Phone bridge is accepting unsigned Twilio webhooks. Set {Var}=false to re-enable.",
                PhoneEnv.AllowUnsigned);
        }
        if (string.IsNullOrWhiteSpace(opts.AuthToken) && !opts.AllowUnsignedWebhooks)
        {
            app.Logger.LogWarning(
                "TWILIO_AUTH_TOKEN is not set — inbound webhooks will be rejected. Set {Var} or {DevVar}=true for local dev.",
                PhoneEnv.TwilioAuthToken, PhoneEnv.AllowUnsigned);
        }
        if (string.IsNullOrWhiteSpace(opts.OutboundBearerToken))
        {
            app.Logger.LogWarning(
                "{Var} is not set — POST /twilio/outbound will return 503.",
                PhoneEnv.OutboundBearer);
        }
    }

    // --- DTOs ----------------------------------------------------------------

    /// <summary>Healthz JSON payload.</summary>
    /// <param name="Status">Always <c>"ok"</c> when the endpoint replies.</param>
    /// <param name="ActiveCalls">Currently-active call count.</param>
    internal sealed record HealthResponse(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("active_calls")] int ActiveCalls);

    /// <summary>Body of <c>POST /twilio/outbound</c>.</summary>
    internal sealed class OutboundRequest
    {
        /// <summary>E.164 destination number (required).</summary>
        public string? To { get; set; }

        /// <summary>E.164 caller-id; defaults to <see cref="PhoneOptions.PhoneNumber"/>.</summary>
        public string? From { get; set; }

        /// <summary>
        /// Free-form opening message the model is asked to say to the recipient. Surfaced as a
        /// Stream <c>&lt;Parameter&gt;</c> so the WS handler can prime the session.
        /// </summary>
        public string? Message { get; set; }

        /// <summary>Optional task description for the model to action during the call.</summary>
        public string? Task { get; set; }
    }

    /// <summary>Successful outbound response.</summary>
    /// <param name="Sid">Twilio-assigned call SID.</param>
    internal sealed record OutboundResponse(
        [property: JsonPropertyName("sid")] string Sid);

    /// <summary>Error response body for the JSON endpoints.</summary>
    /// <param name="Error">Human-readable explanation.</param>
    internal sealed record ErrorResponse(
        [property: JsonPropertyName("error")] string Error);

    /// <summary>
    /// Inert stand-in used when Twilio creds aren't configured. Throws on every call so the
    /// outbound handler surfaces a 503 — the goal is to never silently no-op an outbound call.
    /// </summary>
    internal sealed class InertTwilioRestClient : ITwilioRestClient
    {
        private readonly ILogger<InertTwilioRestClient> _logger;

        public InertTwilioRestClient(ILogger<InertTwilioRestClient> logger)
        {
            _logger = logger;
        }

        public Task<string> CreateCallAsync(string to, string from, Uri? twimlUrl, string? twiml, Uri? statusCallback, CancellationToken ct)
        {
            _logger.LogWarning("Outbound call ignored — Twilio credentials are not configured.");
            throw new NotSupportedException("Twilio REST client is not configured.");
        }
    }
}
