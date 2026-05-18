namespace Sutando.Phone;

/// <summary>
/// Runtime configuration for the Twilio phone bridge. Populated from <c>appsettings</c>, environment
/// variables, and command-line args. <see cref="PhoneServer"/> applies the precedence
/// <b>CLI args &gt; environment &gt; configuration</b>.
/// </summary>
public sealed class PhoneOptions
{
    /// <summary>The <c>IConfiguration</c> binding key. Maps to a top-level <c>Phone:*</c> section.</summary>
    public const string SectionName = "Phone";

    /// <summary>TCP port the HTTP host listens on. Default <c>3100</c> mirrors upstream.</summary>
    public int Port { get; set; } = 3100;

    /// <summary>
    /// Gemini API key. <see cref="PhoneServer"/> reads <c>GEMINI_VOICE_API_KEY</c> first, then
    /// falls back to <c>GEMINI_API_KEY</c>; either value lands here. Empty when no key is set
    /// — the WS handler then closes each Media Streams upgrade after writing an error envelope.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>The Gemini model id used for each phone session. Mirrors upstream's default phone model.</summary>
    public string Model { get; set; } = "gemini-2.5-flash-live-preview";

    /// <summary>Prebuilt voice id. <c>Puck</c> matches upstream's default.</summary>
    public string VoiceName { get; set; } = "Puck";

    /// <summary>Optional system prompt for every session. Null disables.</summary>
    public string? SystemInstruction { get; set; }

    /// <summary>
    /// Twilio account SID — used for the outbound REST call surface AND as part of the
    /// authoritative signing identity. Empty when unset; outbound calls then return 503.
    /// </summary>
    public string AccountSid { get; set; } = string.Empty;

    /// <summary>
    /// Twilio auth token. Used for two purposes:
    /// <list type="bullet">
    ///   <item>HMAC-SHA1 signing key for inbound webhook validation.</item>
    ///   <item>Bearer credential for outbound REST calls.</item>
    /// </list>
    /// Empty in dev mode <i>only</i> when <see cref="AllowUnsignedWebhooks"/> is true.
    /// </summary>
    public string AuthToken { get; set; } = string.Empty;

    /// <summary>
    /// Caller-id used when placing outbound calls (E.164). Empty disables the outbound endpoint.
    /// </summary>
    public string PhoneNumber { get; set; } = string.Empty;

    /// <summary>
    /// Comma-separated allow-list of OWNER caller IDs (E.164). Normalisation matches upstream:
    /// digits-only, with US 10-digit numbers prepended with a leading <c>1</c>.
    /// </summary>
    public string OwnerNumbers { get; set; } = string.Empty;

    /// <summary>Comma-separated allow-list of VERIFIED caller IDs (E.164).</summary>
    public string VerifiedCallers { get; set; } = string.Empty;

    /// <summary>
    /// Hard time-cap for an unverified caller's voice session, in seconds. <c>60</c> mirrors
    /// upstream's policy — unverified callers get one minute, then the bridge hangs up.
    /// </summary>
    public int UnverifiedSessionTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// When true, Twilio webhook signature validation is bypassed. Use only for local dev /
    /// in-process tests that don't have a real <c>X-Twilio-Signature</c>; the host logs a
    /// loud warning at startup so it's never silently shipped to production.
    /// </summary>
    public bool AllowUnsignedWebhooks { get; set; }

    /// <summary>
    /// Bearer token gating <c>/twilio/outbound</c>. Anyone reaching the port can otherwise
    /// place calls on the owner's Twilio bill. Read from <see cref="PhoneEnv.OutboundBearer"/>
    /// (env var <c>SUTANDO_PHONE_OUTBOUND_TOKEN</c>) at startup. Empty disables the endpoint
    /// entirely — the handler returns 503.
    /// </summary>
    public string OutboundBearerToken { get; set; } = string.Empty;

    /// <summary>
    /// Public host used when building the TwiML <c>&lt;Stream url="..."/&gt;</c>. Twilio Media
    /// Streams need a publicly-reachable WSS URL (typically an ngrok tunnel). When unset, the
    /// host derives the URL from the inbound webhook's <c>Host</c> header — fine when the
    /// service sits behind a reverse proxy that terminates TLS and forwards the original host.
    /// </summary>
    public string? PublicHost { get; set; }
}

/// <summary>Convention-stable env var names. Centralised so tests / docs can reference them.</summary>
internal static class PhoneEnv
{
    /// <summary>Env var override for <see cref="PhoneOptions.Port"/>.</summary>
    public const string Port = "SUTANDO_PHONE_PORT";

    /// <summary>Env var holding the Gemini key reserved for voice/phone sessions.</summary>
    public const string GeminiVoiceKey = "GEMINI_VOICE_API_KEY";

    /// <summary>Env var holding the generic Gemini key.</summary>
    public const string GeminiKey = "GEMINI_API_KEY";

    /// <summary>Env var holding the Twilio account SID.</summary>
    public const string TwilioAccountSid = "TWILIO_ACCOUNT_SID";

    /// <summary>Env var holding the Twilio auth token.</summary>
    public const string TwilioAuthToken = "TWILIO_AUTH_TOKEN";

    /// <summary>Env var holding the Twilio caller-id number used for outbound calls.</summary>
    public const string TwilioPhoneNumber = "TWILIO_PHONE_NUMBER";

    /// <summary>Env var holding the comma-separated OWNER caller-id allow-list.</summary>
    public const string OwnerNumber = "OWNER_NUMBER";

    /// <summary>Env var holding the comma-separated VERIFIED caller-id allow-list.</summary>
    public const string VerifiedCallers = "VERIFIED_CALLERS";

    /// <summary>Env var holding the bearer token that gates <c>POST /twilio/outbound</c>.</summary>
    public const string OutboundBearer = "SUTANDO_PHONE_OUTBOUND_TOKEN";

    /// <summary>Env var holding the public host (e.g. ngrok URL) for the Media Streams WSS endpoint.</summary>
    public const string PublicHost = "SUTANDO_PHONE_PUBLIC_HOST";

    /// <summary>Env var that bypasses webhook signature validation. Dev-only.</summary>
    public const string AllowUnsigned = "SUTANDO_PHONE_ALLOW_UNSIGNED";
}
