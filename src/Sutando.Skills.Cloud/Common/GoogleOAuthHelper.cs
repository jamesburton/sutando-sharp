using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sutando.Skills.Cloud.Common;

/// <summary>
/// Exchanges a Google OAuth2 refresh token for a short-lived access token and caches the
/// result per (clientId, refreshToken) tuple until the token is about to expire.
/// </summary>
/// <remarks>
/// <para>
/// The token endpoint is <c>POST https://oauth2.googleapis.com/token</c> with a
/// form-urlencoded body (credentials go in the form — no Authorization header required).
/// </para>
/// <para>
/// Caching policy: tokens are cached until 60 seconds before their stated <c>expires_in</c>
/// deadline. A new token is fetched automatically when the cached entry has expired or is
/// about to expire. The cache is per-instance — the owning skill (typically long-lived)
/// holds the helper.
/// </para>
/// <para>
/// Required env vars (constants on this class):
/// <list type="bullet">
///   <item><term><see cref="ClientIdEnvVar"/></term><description><c>GOOGLE_OAUTH_CLIENT_ID</c></description></item>
///   <item><term><see cref="ClientSecretEnvVar"/></term><description><c>GOOGLE_OAUTH_CLIENT_SECRET</c></description></item>
///   <item><term><see cref="RefreshTokenEnvVar"/></term><description><c>GOOGLE_OAUTH_REFRESH_TOKEN</c></description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class GoogleOAuthHelper
{
    /// <summary>Env var carrying the Google OAuth2 client ID.</summary>
    public const string ClientIdEnvVar = "GOOGLE_OAUTH_CLIENT_ID";

    /// <summary>Env var carrying the Google OAuth2 client secret.</summary>
    public const string ClientSecretEnvVar = "GOOGLE_OAUTH_CLIENT_SECRET";

    /// <summary>Env var carrying the stored refresh token.</summary>
    public const string RefreshTokenEnvVar = "GOOGLE_OAUTH_REFRESH_TOKEN";

    /// <summary>The three env vars required for any skill using this helper to register.</summary>
    public static readonly IReadOnlyList<string> RequiredEnvVars =
        [ClientIdEnvVar, ClientSecretEnvVar, RefreshTokenEnvVar];

    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";

    // Safety margin subtracted from expires_in so we never return a token that's about to expire.
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly ConcurrentDictionary<(string ClientId, string RefreshToken), CachedToken> _cache = new();
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>Construct with the real system clock.</summary>
    public GoogleOAuthHelper() : this(() => DateTimeOffset.UtcNow) { }

    /// <summary>Construct with an injected clock — used by tests to control cache expiry.</summary>
    public GoogleOAuthHelper(Func<DateTimeOffset> clock) => _clock = clock;

    /// <summary>
    /// Returns a valid access token, either from the in-process cache or by exchanging the
    /// refresh token against the Google token endpoint.
    /// </summary>
    /// <param name="http">The HTTP client to use for the token exchange.</param>
    /// <param name="clientId">OAuth2 client ID.</param>
    /// <param name="clientSecret">OAuth2 client secret.</param>
    /// <param name="refreshToken">Long-lived refresh token.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A short-lived access token string.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the token endpoint returns a non-2xx response.</exception>
    public async Task<string> GetAccessTokenAsync(
        HttpClient http,
        string clientId,
        string clientSecret,
        string refreshToken,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        var key = (clientId, refreshToken);
        var now = _clock();

        // Return cached token if still valid (with margin).
        if (_cache.TryGetValue(key, out var cached) && cached.ExpiresAt > now)
        {
            return cached.AccessToken;
        }

        // Fetch a new token.
        var formContent = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("client_id", clientId),
            new KeyValuePair<string, string>("client_secret", clientSecret),
            new KeyValuePair<string, string>("refresh_token", refreshToken),
            new KeyValuePair<string, string>("grant_type", "refresh_token"),
        ]);

        var response = await http.PostAsync(TokenEndpoint, formContent, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await SafeReadAsync(response, ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"google-oauth: token exchange failed with HTTP {(int)response.StatusCode}: {body}");
        }

        TokenResponse? parsed;
        try
        {
            parsed = await response.Content.ReadFromJsonAsync<TokenResponse>(JsonOptions, ct)
                .ConfigureAwait(false);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidOperationException(
                $"google-oauth: token endpoint returned non-JSON response: {ex.Message}", ex);
        }

        if (string.IsNullOrEmpty(parsed?.AccessToken))
        {
            throw new InvalidOperationException("google-oauth: token endpoint response contained no access_token");
        }

        var expiresIn = parsed.ExpiresIn > 0 ? parsed.ExpiresIn : 3600;
        var expiresAt = now + TimeSpan.FromSeconds(expiresIn) - ExpiryMargin;
        var entry = new CachedToken(parsed.AccessToken, expiresAt);

        // Store (or replace) in cache — last-write wins in a race; both are valid tokens.
        _cache[key] = entry;
        return entry.AccessToken;
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch { return string.Empty; }
    }

    private readonly record struct CachedToken(string AccessToken, DateTimeOffset ExpiresAt);

    private sealed record TokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; init; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }
        [JsonPropertyName("token_type")] public string? TokenType { get; init; }
    }
}
