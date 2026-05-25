using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sutando.Skills.Cloud.Common;

namespace Sutando.Skills.Cloud.Google;

/// <summary>
/// Lists upcoming or creates new Google Calendar events via the Calendar REST API v3,
/// authenticated with an OAuth2 refresh-token flow managed by <see cref="GoogleOAuthHelper"/>.
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
///     <term><c>action=upcoming</c></term>
///     <description>
///       Lists upcoming events on the primary calendar. Optional arg: <c>days</c> (default 7).
///       Returns up to 25 events, one per line as <c>&lt;start&gt; — &lt;summary&gt;</c>.
///     </description>
///   </item>
///   <item>
///     <term><c>action=create</c></term>
///     <description>
///       Creates a new event. Required args: <c>title</c>, <c>start</c>, <c>end</c> (ISO 8601
///       timestamps with UTC or explicit offset). The offset is passed through verbatim to the
///       Calendar API — no <c>timeZone</c> field is sent, relying on the offset embedded in the
///       value. If callers supply a bare date-only string (e.g. <c>2026-06-01</c>), the request
///       may be rejected by the API; advise callers to include a time component and offset.
///     </description>
///   </item>
/// </list>
/// </para>
/// </remarks>
public sealed class CalendarSkill : ISkill
{
    private const string CalendarApiBase = "https://www.googleapis.com/calendar/v3/calendars/primary";
    private const int MaxUpcomingResults = 25;
    private const int DefaultDays = 7;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly GoogleOAuthHelper _oauth;

    /// <inheritdoc/>
    public SkillManifest Manifest { get; }

    /// <summary>Construct with the default manifest and a fresh <see cref="GoogleOAuthHelper"/>.</summary>
    public CalendarSkill() : this(DefaultManifest(), new GoogleOAuthHelper()) { }

    /// <summary>
    /// Construct with a caller-supplied manifest and OAuth helper (used by tests and the
    /// managed-factory path).
    /// </summary>
    public CalendarSkill(SkillManifest manifest, GoogleOAuthHelper oauth)
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
            return SkillResult.Fail($"calendar: missing env var '{missing}'", sw.Elapsed);
        }

        if (!arguments.TryGetValue("action", out var action) || string.IsNullOrWhiteSpace(action))
        {
            sw.Stop();
            return SkillResult.Fail("calendar: 'action' argument is required (upcoming or create)", sw.Elapsed);
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
            return SkillResult.Fail($"calendar: {ex.Message}", sw.Elapsed);
        }

        return action.ToLowerInvariant() switch
        {
            "upcoming" => await ExecuteUpcomingAsync(context, arguments, accessToken, sw, ct).ConfigureAwait(false),
            "create" => await ExecuteCreateAsync(context, arguments, accessToken, sw, ct).ConfigureAwait(false),
            _ => FailWith($"calendar: unknown action '{action}' — expected 'upcoming' or 'create'", sw),
        };
    }

    /// <summary>Canonical manifest for the calendar skill.</summary>
    public static SkillManifest DefaultManifest() => new()
    {
        Id = "calendar",
        Name = "Google Calendar",
        Description = "List upcoming or create Google Calendar events via the Calendar REST API v3 with OAuth2 refresh-token auth.",
        Version = "0.1.0",
        Runtime = SkillRuntime.Managed,
        Entry = "Sutando.Skills.Cloud.Google.CalendarSkill, Sutando.Skills.Cloud",
        Triggers = ["calendar", "gcal", "upcoming-events", "create-event"],
        Capabilities = ["http-out"],
    };

    private async Task<SkillResult> ExecuteUpcomingAsync(
        SkillContext context,
        IReadOnlyDictionary<string, string> arguments,
        string accessToken,
        Stopwatch sw,
        CancellationToken ct)
    {
        var days = DefaultDays;
        if (arguments.TryGetValue("days", out var daysStr) && int.TryParse(daysStr, out var parsedDays) && parsedDays > 0)
        {
            days = parsedDays;
        }

        var now = DateTimeOffset.UtcNow;
        var timeMax = now.AddDays(days);

        // RFC 3339 format as required by the Calendar API.
        var timeMin = now.ToString("o", CultureInfo.InvariantCulture);
        var timeMaxStr = timeMax.ToString("o", CultureInfo.InvariantCulture);

        var uri = $"{CalendarApiBase}/events" +
                  $"?timeMin={Uri.EscapeDataString(timeMin)}" +
                  $"&timeMax={Uri.EscapeDataString(timeMaxStr)}" +
                  $"&singleEvents=true&orderBy=startTime&maxResults={MaxUpcomingResults}";

        var response = await SendAuthorizedGetAsync(context.Http, uri, accessToken, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errBody = await SafeReadAsync(response, ct).ConfigureAwait(false);
            sw.Stop();
            return SkillResult.Fail($"calendar: HTTP {(int)response.StatusCode}: {Truncate(errBody, 200)}", sw.Elapsed);
        }

        EventListResponse? parsed;
        try
        {
            parsed = await response.Content.ReadFromJsonAsync<EventListResponse>(JsonOptions, ct)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            sw.Stop();
            return SkillResult.Fail($"calendar: response was not valid JSON: {ex.Message}", sw.Elapsed);
        }

        var events = parsed?.Items ?? [];
        if (events.Count == 0)
        {
            sw.Stop();
            return SkillResult.Ok(body: "No upcoming events found.", duration: sw.Elapsed, artifacts: []);
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Upcoming events ({events.Count}):");
        foreach (var ev in events)
        {
            // Timed events have dateTime; all-day events have date only.
            var start = ev.Start?.DateTime ?? ev.Start?.Date ?? "(no start)";
            var summary = ev.Summary ?? "(no title)";
            sb.AppendLine($"{start} — {summary}");
        }

        sw.Stop();
        return SkillResult.Ok(body: sb.ToString().TrimEnd(), duration: sw.Elapsed, artifacts: []);
    }

    private async Task<SkillResult> ExecuteCreateAsync(
        SkillContext context,
        IReadOnlyDictionary<string, string> arguments,
        string accessToken,
        Stopwatch sw,
        CancellationToken ct)
    {
        if (!arguments.TryGetValue("title", out var title) || string.IsNullOrWhiteSpace(title))
        {
            return FailWith("calendar: 'title' argument is required for action=create", sw);
        }

        if (!arguments.TryGetValue("start", out var start) || string.IsNullOrWhiteSpace(start))
        {
            return FailWith("calendar: 'start' argument is required for action=create", sw);
        }

        if (!arguments.TryGetValue("end", out var end) || string.IsNullOrWhiteSpace(end))
        {
            return FailWith("calendar: 'end' argument is required for action=create", sw);
        }

        var eventBody = new CreateEventRequest
        {
            Summary = title,
            Start = new EventTime { DateTime = start },
            End = new EventTime { DateTime = end },
        };

        var uri = $"{CalendarApiBase}/events";

        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = JsonContent.Create(eventBody, options: JsonOptions),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        HttpResponseMessage response;
        try
        {
            response = await context.Http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            return SkillResult.Fail($"calendar: HTTP request failed: {ex.Message}", sw.Elapsed);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errBody = await SafeReadAsync(response, ct).ConfigureAwait(false);
            sw.Stop();
            return SkillResult.Fail($"calendar: HTTP {(int)response.StatusCode}: {Truncate(errBody, 200)}", sw.Elapsed);
        }

        CreatedEventResponse? created;
        try
        {
            created = await response.Content.ReadFromJsonAsync<CreatedEventResponse>(JsonOptions, ct)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            sw.Stop();
            return SkillResult.Fail($"calendar: response was not valid JSON: {ex.Message}", sw.Elapsed);
        }

        var eventId = created?.Id ?? "(unknown)";
        var htmlLink = created?.HtmlLink ?? "(no link)";

        sw.Stop();
        return SkillResult.Ok(
            body: $"Event created: id={eventId} link={htmlLink}",
            duration: sw.Elapsed,
            artifacts: []);
    }

    private static Task<HttpResponseMessage> SendAuthorizedGetAsync(
        HttpClient http, string uri, string accessToken, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return http.SendAsync(request, ct);
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

    private sealed record EventListResponse
    {
        [JsonPropertyName("items")] public IReadOnlyList<CalendarEvent>? Items { get; init; }
    }

    private sealed record CalendarEvent
    {
        [JsonPropertyName("id")] public string? Id { get; init; }
        [JsonPropertyName("summary")] public string? Summary { get; init; }
        [JsonPropertyName("start")] public EventTimeField? Start { get; init; }
        [JsonPropertyName("end")] public EventTimeField? End { get; init; }
        [JsonPropertyName("htmlLink")] public string? HtmlLink { get; init; }
    }

    private sealed record EventTimeField
    {
        /// <summary>Set for timed events (RFC 3339).</summary>
        [JsonPropertyName("dateTime")] public string? DateTime { get; init; }

        /// <summary>Set for all-day events (YYYY-MM-DD).</summary>
        [JsonPropertyName("date")] public string? Date { get; init; }
    }

    private sealed record CreateEventRequest
    {
        [JsonPropertyName("summary")] public required string Summary { get; init; }
        [JsonPropertyName("start")] public required EventTime Start { get; init; }
        [JsonPropertyName("end")] public required EventTime End { get; init; }
    }

    private sealed record EventTime
    {
        [JsonPropertyName("dateTime")] public required string DateTime { get; init; }
    }

    private sealed record CreatedEventResponse
    {
        [JsonPropertyName("id")] public string? Id { get; init; }
        [JsonPropertyName("htmlLink")] public string? HtmlLink { get; init; }
    }
}
