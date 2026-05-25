using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sutando.Skills.Cloud.Common;

namespace Sutando.Skills.Cloud.Twitter;

/// <summary>
/// Post a tweet via the X (Twitter) v2 <c>/2/tweets</c> endpoint with OAuth 1.0a user-context
/// auth. Maps directly to the upstream Sutando project's <c>x-twitter</c> skill.
/// </summary>
/// <remarks>
/// <para>
/// Endpoint: <c>POST https://api.twitter.com/2/tweets</c>. JSON body
/// <c>{"text": "&lt;text&gt;"}</c>. Returns the created tweet id; the skill puts it into
/// <see cref="SkillResult.Body"/> and surfaces the tweet's URL alongside.
/// </para>
/// <para>
/// Auth requires four env vars — consumer key + secret + user access token + access secret.
/// All four must be present for the skill to register
/// (see <see cref="CloudSkillRegistration"/>).
/// </para>
/// <para>
/// Arguments:
/// <list type="bullet">
///   <item><term><c>text</c></term><description>Required. The tweet body (up to 280 chars for free-tier accounts).</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class XTwitterSkill : ISkill
{
    /// <summary>Env var carrying the X application's consumer key (a.k.a. API key).</summary>
    public const string ApiKeyEnvVar = "TWITTER_API_KEY";

    /// <summary>Env var carrying the X application's consumer secret.</summary>
    public const string ApiSecretEnvVar = "TWITTER_API_SECRET";

    /// <summary>Env var carrying the authorising user's access token.</summary>
    public const string AccessTokenEnvVar = "TWITTER_ACCESS_TOKEN";

    /// <summary>Env var carrying the authorising user's access-token secret.</summary>
    public const string AccessSecretEnvVar = "TWITTER_ACCESS_SECRET";

    /// <summary>The four env vars required for this skill to register.</summary>
    public static readonly IReadOnlyList<string> RequiredEnvVars =
        [ApiKeyEnvVar, ApiSecretEnvVar, AccessTokenEnvVar, AccessSecretEnvVar];

    private const string Endpoint = "https://api.twitter.com/2/tweets";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly OAuth1Signer _signer;

    /// <inheritdoc/>
    public SkillManifest Manifest { get; }

    /// <summary>Construct with the default manifest and a fresh <see cref="OAuth1Signer"/>.</summary>
    public XTwitterSkill() : this(DefaultManifest(), new OAuth1Signer()) { }

    /// <summary>Construct with a caller-supplied signer (used by tests with deterministic nonce/timestamp).</summary>
    public XTwitterSkill(SkillManifest manifest, OAuth1Signer signer)
    {
        Manifest = manifest;
        _signer = signer;
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

        if (!arguments.TryGetValue("text", out var text) || string.IsNullOrWhiteSpace(text))
        {
            sw.Stop();
            return SkillResult.Fail("x-twitter: 'text' argument is required", sw.Elapsed);
        }

        if (!TryReadCredentials(context, out var creds, out var missing))
        {
            sw.Stop();
            return SkillResult.Fail($"x-twitter: missing env var '{missing}'", sw.Elapsed);
        }

        var authHeader = _signer.BuildAuthorizationHeader(
            "POST", Endpoint,
            creds.ConsumerKey, creds.ConsumerSecret,
            creds.AccessToken, creds.AccessSecret);

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(new CreateTweetRequest { Text = text }, options: JsonOptions),
        };
        request.Headers.TryAddWithoutValidation("Authorization", authHeader);

        HttpResponseMessage response;
        try
        {
            response = await context.Http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            return SkillResult.Fail($"x-twitter: HTTP request failed: {ex.Message}", sw.Elapsed);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errBody = await SafeReadAsync(response, ct).ConfigureAwait(false);
            sw.Stop();
            return SkillResult.Fail(
                $"x-twitter: HTTP {(int)response.StatusCode}: {Truncate(errBody, 200)}",
                sw.Elapsed);
        }

        CreateTweetResponse? parsed;
        try
        {
            parsed = await response.Content.ReadFromJsonAsync<CreateTweetResponse>(JsonOptions, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            sw.Stop();
            return SkillResult.Fail($"x-twitter: response was not valid JSON: {ex.Message}", sw.Elapsed);
        }

        var tweetId = parsed?.Data?.Id;
        if (string.IsNullOrEmpty(tweetId))
        {
            sw.Stop();
            return SkillResult.Fail("x-twitter: response did not include a tweet id", sw.Elapsed);
        }

        sw.Stop();
        // No artifact files — Twitter doesn't return media we'd persist. The body carries the
        // id + a convenience URL the caller can hand to a browser.
        return SkillResult.Ok(
            body: $"Tweet posted: id={tweetId} url=https://x.com/i/web/status/{tweetId}",
            duration: sw.Elapsed,
            artifacts: []);
    }

    /// <summary>Canonical manifest for the x-twitter skill.</summary>
    public static SkillManifest DefaultManifest() => new()
    {
        Id = "x-twitter",
        Name = "X (Twitter) — post a tweet",
        Description = "Post a tweet via the X v2 /2/tweets endpoint, OAuth 1.0a user-context auth.",
        Version = "0.1.0",
        Runtime = SkillRuntime.Managed,
        Entry = "Sutando.Skills.Cloud.Twitter.XTwitterSkill, Sutando.Skills.Cloud",
        Triggers = ["x-twitter", "tweet", "post-tweet"],
        Capabilities = ["http-out", "network-out"],
    };

    private static bool TryReadCredentials(SkillContext context, out Credentials creds, out string? missingVar)
    {
        foreach (var v in RequiredEnvVars)
        {
            if (!context.Environment.TryGetValue(v, out var value) || string.IsNullOrWhiteSpace(value))
            {
                creds = default;
                missingVar = v;
                return false;
            }
        }
        creds = new Credentials(
            context.Environment[ApiKeyEnvVar],
            context.Environment[ApiSecretEnvVar],
            context.Environment[AccessTokenEnvVar],
            context.Environment[AccessSecretEnvVar]);
        missingVar = null;
        return true;
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch { return string.Empty; }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private readonly record struct Credentials(string ConsumerKey, string ConsumerSecret, string AccessToken, string AccessSecret);

    private sealed record CreateTweetRequest
    {
        public required string Text { get; init; }
    }

    private sealed record CreateTweetResponse
    {
        [JsonPropertyName("data")] public TweetData? Data { get; init; }
    }

    private sealed record TweetData
    {
        [JsonPropertyName("id")] public string? Id { get; init; }
        [JsonPropertyName("text")] public string? Text { get; init; }
    }
}
