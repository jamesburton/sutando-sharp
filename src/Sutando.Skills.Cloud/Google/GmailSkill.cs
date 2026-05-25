using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sutando.Skills.Cloud.Common;

namespace Sutando.Skills.Cloud.Google;

/// <summary>
/// Searches or retrieves Gmail messages via the Gmail REST API v1, authenticated with an
/// OAuth2 refresh-token flow managed by <see cref="GoogleOAuthHelper"/>.
/// </summary>
/// <remarks>
/// <para>
/// Requires three env vars (see <see cref="GoogleOAuthHelper.RequiredEnvVars"/>):
/// <c>GOOGLE_OAUTH_CLIENT_ID</c>, <c>GOOGLE_OAUTH_CLIENT_SECRET</c>,
/// <c>GOOGLE_OAUTH_REFRESH_TOKEN</c>.
/// </para>
/// <para>
/// Actions:
/// <list type="bullet">
///   <item>
///     <term><c>action=search</c></term>
///     <description>
///       Searches messages with the Gmail query language. Required arg: <c>query</c>.
///       Optional arg: <c>max</c> (default 10). Returns a list of message IDs / thread IDs.
///     </description>
///   </item>
///   <item>
///     <term><c>action=get</c></term>
///     <description>
///       Fetches a single message. Required arg: <c>id</c>. Returns a body of the form
///       <c>From: ...\nSubject: ...\n\n&lt;plain text&gt;</c>.
///     </description>
///   </item>
/// </list>
/// </para>
/// </remarks>
public sealed class GmailSkill : ISkill
{
    private const string GmailApiBase = "https://gmail.googleapis.com/gmail/v1/users/me";
    private const int DefaultMaxResults = 10;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly GoogleOAuthHelper _oauth;

    /// <inheritdoc/>
    public SkillManifest Manifest { get; }

    /// <summary>Construct with the default manifest and a fresh <see cref="GoogleOAuthHelper"/>.</summary>
    public GmailSkill() : this(DefaultManifest(), new GoogleOAuthHelper()) { }

    /// <summary>
    /// Construct with a caller-supplied manifest and OAuth helper (used by tests and the
    /// managed-factory path).
    /// </summary>
    public GmailSkill(SkillManifest manifest, GoogleOAuthHelper oauth)
    {
        Manifest = manifest;
        _oauth = oauth;
    }

    /// <inheritdoc/>
    public async Task<SkillResult> ExecuteAsync(
        SkillContext context,
        IReadOnlyDictionary<string, string> arguments,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(arguments);

        var sw = Stopwatch.StartNew();

        if (!TryReadCredentials(context, out var creds, out var missing))
        {
            sw.Stop();
            return SkillResult.Fail($"gmail: missing env var '{missing}'", sw.Elapsed);
        }

        if (!arguments.TryGetValue("action", out var action) || string.IsNullOrWhiteSpace(action))
        {
            sw.Stop();
            return SkillResult.Fail("gmail: 'action' argument is required (search or get)", sw.Elapsed);
        }

        string accessToken;
        try
        {
            accessToken = await _oauth.GetAccessTokenAsync(
                context.Http, creds.ClientId, creds.ClientSecret, creds.RefreshToken, ct)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            sw.Stop();
            return SkillResult.Fail($"gmail: {ex.Message}", sw.Elapsed);
        }

        return action.ToLowerInvariant() switch
        {
            "search" => await ExecuteSearchAsync(context, arguments, accessToken, sw, ct).ConfigureAwait(false),
            "get" => await ExecuteGetAsync(context, arguments, accessToken, sw, ct).ConfigureAwait(false),
            _ => FailWith($"gmail: unknown action '{action}' — expected 'search' or 'get'", sw),
        };
    }

    /// <summary>Canonical manifest for the gmail skill.</summary>
    public static SkillManifest DefaultManifest() => new()
    {
        Id = "gmail",
        Name = "Gmail",
        Description = "Search or read Gmail messages via the Gmail REST API v1 with OAuth2 refresh-token auth.",
        Version = "0.1.0",
        Runtime = SkillRuntime.Managed,
        Entry = "Sutando.Skills.Cloud.Google.GmailSkill, Sutando.Skills.Cloud",
        Triggers = ["gmail", "email", "read-email", "search-email"],
        Capabilities = ["http-out"],
    };

    private async Task<SkillResult> ExecuteSearchAsync(
        SkillContext context,
        IReadOnlyDictionary<string, string> arguments,
        string accessToken,
        Stopwatch sw,
        CancellationToken ct)
    {
        if (!arguments.TryGetValue("query", out var query) || string.IsNullOrWhiteSpace(query))
        {
            return FailWith("gmail: 'query' argument is required for action=search", sw);
        }

        var max = DefaultMaxResults;
        if (arguments.TryGetValue("max", out var maxStr) && int.TryParse(maxStr, out var parsed) && parsed > 0)
        {
            max = parsed;
        }

        var encodedQuery = Uri.EscapeDataString(query);
        var uri = $"{GmailApiBase}/messages?q={encodedQuery}&maxResults={max}";

        var response = await SendAuthorizedGetAsync(context.Http, uri, accessToken, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errBody = await SafeReadAsync(response, ct).ConfigureAwait(false);
            sw.Stop();
            return SkillResult.Fail($"gmail: HTTP {(int)response.StatusCode}: {Truncate(errBody, 200)}", sw.Elapsed);
        }

        MessageListResponse? listResult;
        try
        {
            listResult = await response.Content.ReadFromJsonAsync<MessageListResponse>(JsonOptions, ct)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            sw.Stop();
            return SkillResult.Fail($"gmail: response was not valid JSON: {ex.Message}", sw.Elapsed);
        }

        var messages = listResult?.Messages ?? [];
        var sb = new StringBuilder();
        sb.AppendLine($"Found {messages.Count} message(s):");
        foreach (var msg in messages)
        {
            sb.AppendLine($"- {msg.Id} (thread: {msg.ThreadId})");
        }

        sw.Stop();
        return SkillResult.Ok(body: sb.ToString().TrimEnd(), duration: sw.Elapsed, artifacts: []);
    }

    private async Task<SkillResult> ExecuteGetAsync(
        SkillContext context,
        IReadOnlyDictionary<string, string> arguments,
        string accessToken,
        Stopwatch sw,
        CancellationToken ct)
    {
        if (!arguments.TryGetValue("id", out var id) || string.IsNullOrWhiteSpace(id))
        {
            return FailWith("gmail: 'id' argument is required for action=get", sw);
        }

        var uri = $"{GmailApiBase}/messages/{Uri.EscapeDataString(id)}?format=full";

        var response = await SendAuthorizedGetAsync(context.Http, uri, accessToken, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errBody = await SafeReadAsync(response, ct).ConfigureAwait(false);
            sw.Stop();
            return SkillResult.Fail($"gmail: HTTP {(int)response.StatusCode}: {Truncate(errBody, 200)}", sw.Elapsed);
        }

        MessageResponse? parsed;
        try
        {
            parsed = await response.Content.ReadFromJsonAsync<MessageResponse>(JsonOptions, ct)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            sw.Stop();
            return SkillResult.Fail($"gmail: response was not valid JSON: {ex.Message}", sw.Elapsed);
        }

        var from = FindHeader(parsed?.Payload?.Headers, "From") ?? "(unknown)";
        var subject = FindHeader(parsed?.Payload?.Headers, "Subject") ?? "(no subject)";
        var plainText = ExtractPlainText(parsed?.Payload) ?? string.Empty;

        var body = $"From: {from}\nSubject: {subject}\n\n{plainText}";

        sw.Stop();
        return SkillResult.Ok(body: body, duration: sw.Elapsed, artifacts: []);
    }

    private static Task<HttpResponseMessage> SendAuthorizedGetAsync(
        HttpClient http, string uri, string accessToken, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return http.SendAsync(request, ct);
    }

    private static string? FindHeader(IReadOnlyList<MessageHeader>? headers, string name)
    {
        if (headers is null) return null;
        foreach (var h in headers)
        {
            if (string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return h.Value;
            }
        }
        return null;
    }

    /// <summary>
    /// Walks the Gmail message payload depth-first, returning the decoded text/plain body.
    /// Handles both single-part messages (body at <c>payload.body.data</c>) and multipart
    /// messages where the plain-text part may be nested under multipart/alternative or
    /// multipart/mixed.
    /// </summary>
    private static string? ExtractPlainText(MessagePayload? payload)
    {
        if (payload is null) return null;

        // Single-part plain-text: body lives directly on payload.body.data.
        if (string.Equals(payload.MimeType, "text/plain", StringComparison.OrdinalIgnoreCase))
        {
            return DecodeBase64Url(payload.Body?.Data);
        }

        // Multipart: recurse through parts depth-first.
        if (payload.Parts is not null)
        {
            foreach (var part in payload.Parts)
            {
                var result = ExtractPlainTextFromPart(part);
                if (result is not null) return result;
            }
        }

        return null;
    }

    private static string? ExtractPlainTextFromPart(MessagePayloadPart part)
    {
        if (string.Equals(part.MimeType, "text/plain", StringComparison.OrdinalIgnoreCase))
        {
            return DecodeBase64Url(part.Body?.Data);
        }

        // Recurse into multipart subtypes (multipart/alternative, multipart/mixed, etc.).
        if (part.MimeType?.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase) == true && part.Parts is not null)
        {
            foreach (var child in part.Parts)
            {
                var result = ExtractPlainTextFromPart(child);
                if (result is not null) return result;
            }
        }

        return null;
    }

    /// <summary>
    /// Decodes a base64url-encoded string (RFC 4648 §5: <c>-</c>/<c>_</c> instead of
    /// <c>+</c>/<c>/</c>, no padding). Returns null if the input is null or empty.
    /// </summary>
    private static string? DecodeBase64Url(string? data)
    {
        if (string.IsNullOrEmpty(data)) return null;

        // Substitute base64url alphabet to standard base64, then add padding.
        var base64 = data.Replace('-', '+').Replace('_', '/');
        var paddingNeeded = (4 - base64.Length % 4) % 4;
        if (paddingNeeded > 0) base64 = base64.PadRight(base64.Length + paddingNeeded, '=');

        try
        {
            var bytes = Convert.FromBase64String(base64);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool TryReadCredentials(SkillContext context, out OAuthCredentials creds, out string? missingVar)
    {
        foreach (var v in GoogleOAuthHelper.RequiredEnvVars)
        {
            if (!context.Environment.TryGetValue(v, out var value) || string.IsNullOrWhiteSpace(value))
            {
                creds = default;
                missingVar = v;
                return false;
            }
        }
        creds = new OAuthCredentials(
            context.Environment[GoogleOAuthHelper.ClientIdEnvVar],
            context.Environment[GoogleOAuthHelper.ClientSecretEnvVar],
            context.Environment[GoogleOAuthHelper.RefreshTokenEnvVar]);
        missingVar = null;
        return true;
    }

    private static SkillResult FailWith(string message, Stopwatch sw)
    {
        sw.Stop();
        return SkillResult.Fail(message, sw.Elapsed);
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch { return string.Empty; }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private readonly record struct OAuthCredentials(string ClientId, string ClientSecret, string RefreshToken);

    // --- JSON wire types ---

    private sealed record MessageListResponse
    {
        [JsonPropertyName("messages")] public IReadOnlyList<MessageRef>? Messages { get; init; }
        [JsonPropertyName("resultSizeEstimate")] public int ResultSizeEstimate { get; init; }
    }

    private sealed record MessageRef
    {
        [JsonPropertyName("id")] public string? Id { get; init; }
        [JsonPropertyName("threadId")] public string? ThreadId { get; init; }
    }

    private sealed record MessageResponse
    {
        [JsonPropertyName("id")] public string? Id { get; init; }
        [JsonPropertyName("threadId")] public string? ThreadId { get; init; }
        [JsonPropertyName("snippet")] public string? Snippet { get; init; }
        [JsonPropertyName("payload")] public MessagePayload? Payload { get; init; }
    }

    private sealed record MessagePayload
    {
        [JsonPropertyName("mimeType")] public string? MimeType { get; init; }
        [JsonPropertyName("headers")] public IReadOnlyList<MessageHeader>? Headers { get; init; }
        [JsonPropertyName("body")] public MessageBody? Body { get; init; }
        [JsonPropertyName("parts")] public IReadOnlyList<MessagePayloadPart>? Parts { get; init; }
    }

    private sealed record MessagePayloadPart
    {
        [JsonPropertyName("mimeType")] public string? MimeType { get; init; }
        [JsonPropertyName("body")] public MessageBody? Body { get; init; }
        [JsonPropertyName("parts")] public IReadOnlyList<MessagePayloadPart>? Parts { get; init; }
    }

    private sealed record MessageBody
    {
        [JsonPropertyName("data")] public string? Data { get; init; }
        [JsonPropertyName("size")] public int Size { get; init; }
    }

    private sealed record MessageHeader
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("value")] public string? Value { get; init; }
    }
}
