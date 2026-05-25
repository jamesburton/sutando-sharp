using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Sutando.Skills.Cloud.Common;

/// <summary>
/// Minimal RFC 5849 OAuth 1.0a request signer. Builds the canonical signature base string,
/// HMAC-SHA1-signs it with the consumer + token secret pair, and renders the resulting
/// <c>Authorization: OAuth …</c> header value.
/// </summary>
/// <remarks>
/// <para>
/// Scope: this signer covers the request-signing case used by the X / Twitter v2 endpoints —
/// HTTP method + base URL + collected oauth_* + query parameters, JSON request bodies are
/// <em>not</em> included in the signature (per Twitter's own docs, only form-urlencoded bodies
/// participate). If a future caller needs the form-body branch, extend
/// <see cref="BuildAuthorizationHeader"/> to accept and merge those parameters in.
/// </para>
/// <para>
/// The two random per-request fields (<c>oauth_nonce</c> and <c>oauth_timestamp</c>) are
/// generated internally with <see cref="RandomNumberGenerator"/> and
/// <see cref="DateTimeOffset.UtcNow"/> — both can be injected by tests via the constructor so
/// the signature is deterministic and assertable against published OAuth test vectors.
/// </para>
/// </remarks>
public sealed class OAuth1Signer
{
    private readonly Func<string> _nonceProvider;
    private readonly Func<long> _timestampProvider;

    /// <summary>Build a signer using cryptographically-random nonces and the current UTC unix timestamp.</summary>
    public OAuth1Signer()
        : this(DefaultNonce, DefaultTimestamp) { }

    /// <summary>Test-only ctor: inject deterministic nonce + timestamp generators.</summary>
    public OAuth1Signer(Func<string> nonceProvider, Func<long> timestampProvider)
    {
        _nonceProvider = nonceProvider;
        _timestampProvider = timestampProvider;
    }

    /// <summary>
    /// Build the <c>Authorization</c> header value (the literal "OAuth …" payload — not the
    /// scheme prefix) for a request to <paramref name="url"/> with HTTP method
    /// <paramref name="httpMethod"/>.
    /// </summary>
    /// <param name="httpMethod">Uppercase HTTP method (<c>GET</c>, <c>POST</c>).</param>
    /// <param name="url">Full request URL including any query string.</param>
    /// <param name="consumerKey">OAuth consumer key (the application's API key).</param>
    /// <param name="consumerSecret">OAuth consumer secret.</param>
    /// <param name="accessToken">User access token.</param>
    /// <param name="accessTokenSecret">User access token secret.</param>
    public string BuildAuthorizationHeader(
        string httpMethod,
        string url,
        string consumerKey,
        string consumerSecret,
        string accessToken,
        string accessTokenSecret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(httpMethod);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerSecret);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessTokenSecret);

        var uri = new Uri(url);
        var baseUrl = $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? string.Empty : ":" + uri.Port.ToString(CultureInfo.InvariantCulture))}{uri.AbsolutePath}";

        var oauthParams = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["oauth_consumer_key"] = consumerKey,
            ["oauth_nonce"] = _nonceProvider(),
            ["oauth_signature_method"] = "HMAC-SHA1",
            ["oauth_timestamp"] = _timestampProvider().ToString(CultureInfo.InvariantCulture),
            ["oauth_token"] = accessToken,
            ["oauth_version"] = "1.0",
        };

        // Merge query-string params into the base-string parameter set. They participate in
        // the signature but never appear in the Authorization header (which only carries the
        // oauth_* fields). Body parameters are deliberately omitted — see the class remarks.
        var allParams = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in oauthParams) { allParams[k] = v; }
        if (!string.IsNullOrEmpty(uri.Query))
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                var key = eq >= 0 ? Uri.UnescapeDataString(pair[..eq]) : Uri.UnescapeDataString(pair);
                var value = eq >= 0 ? Uri.UnescapeDataString(pair[(eq + 1)..]) : string.Empty;
                allParams[key] = value;
            }
        }

        var paramString = string.Join("&", allParams.Select(p => $"{PercentEncode(p.Key)}={PercentEncode(p.Value)}"));
        var baseString = $"{httpMethod.ToUpperInvariant()}&{PercentEncode(baseUrl)}&{PercentEncode(paramString)}";
        var signingKey = $"{PercentEncode(consumerSecret)}&{PercentEncode(accessTokenSecret)}";

        var hash = HMACSHA1.HashData(Encoding.UTF8.GetBytes(signingKey), Encoding.UTF8.GetBytes(baseString));
        var signature = Convert.ToBase64String(hash);
        oauthParams["oauth_signature"] = signature;

        var header = new StringBuilder("OAuth ");
        var first = true;
        foreach (var (key, value) in oauthParams)
        {
            if (!first) { header.Append(", "); }
            header.Append(PercentEncode(key)).Append("=\"").Append(PercentEncode(value)).Append('"');
            first = false;
        }
        return header.ToString();
    }

    /// <summary>
    /// RFC 3986 "unreserved characters" percent-encoder. <see cref="Uri.EscapeDataString"/>
    /// is close but historically left a few characters un-encoded that RFC 3986 says should
    /// be — Microsoft fixed this on .NET 5+, but we keep an explicit pass to be safe across
    /// runtimes and to match OAuth published test vectors exactly.
    /// </summary>
    private static string PercentEncode(string s)
    {
        // Per RFC 3986 §2.3: unreserved = ALPHA / DIGIT / "-" / "." / "_" / "~"
        var sb = new StringBuilder(s.Length * 3);
        var bytes = Encoding.UTF8.GetBytes(s);
        foreach (var b in bytes)
        {
            var c = (char)b;
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '.' or '_' or '~')
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }
        return sb.ToString();
    }

    private static string DefaultNonce()
    {
        // 32 hex chars of crypto-random — matches Twitter's example formatting and gives plenty
        // of entropy to avoid replay collisions within the 300-second timestamp window.
        Span<byte> buf = stackalloc byte[16];
        RandomNumberGenerator.Fill(buf);
        return Convert.ToHexString(buf).ToLowerInvariant();
    }

    private static long DefaultTimestamp() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
