using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Sutando.Api;

/// <summary>
/// Constant-time bearer-token check for the Sutando API.
/// </summary>
/// <remarks>
/// When the configured token is <see langword="null"/> or empty, the API is open and every
/// request passes — a startup warning is emitted via <see cref="LogStartupBanner"/>. When the
/// token is set, callers must send <c>Authorization: Bearer &lt;token&gt;</c>; the comparison
/// is constant-time so timing-side-channels do not leak the token.
/// </remarks>
public sealed class ApiAuth
{
    private readonly byte[]? _expectedTokenBytes;
    private readonly ILogger<ApiAuth> _logger;

    /// <summary>True when no token is configured. Callers can use this to skip per-request work.</summary>
    public bool IsOpen => _expectedTokenBytes is null;

    /// <summary>Create a new auth checker.</summary>
    /// <param name="token">Expected bearer token; <see langword="null"/> / empty disables auth.</param>
    /// <param name="logger">Logger for the startup banner.</param>
    public ApiAuth(string? token, ILogger<ApiAuth> logger)
    {
        _logger = logger;
        _expectedTokenBytes = string.IsNullOrWhiteSpace(token) ? null : Encoding.UTF8.GetBytes(token);
    }

    /// <summary>Log a one-line banner at startup so it's obvious which mode the API is in.</summary>
    /// <param name="appLogger">Logger used by the host startup code.</param>
    public void LogStartupBanner(ILogger appLogger)
    {
        if (IsOpen)
        {
            appLogger.LogWarning(
                "Sutando API running OPEN — no SUTANDO_API_TOKEN set. Anyone on this host can submit tasks.");
        }
        else
        {
            appLogger.LogInformation("Sutando API requires bearer token (SUTANDO_API_TOKEN set).");
        }
    }

    /// <summary>Check the bearer token on a request. Returns true if the request is authorised.</summary>
    /// <param name="context">The current HTTP context.</param>
    /// <returns>True when auth passes; false otherwise.</returns>
    public bool TryAuthorize(HttpContext context)
    {
        if (_expectedTokenBytes is null)
        {
            return true;
        }

        var header = context.Request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        if (string.IsNullOrEmpty(header) || !header.StartsWith(scheme, StringComparison.Ordinal))
        {
            return false;
        }
        var token = header[scheme.Length..].Trim();
        if (token.Length == 0)
        {
            return false;
        }
        var actual = Encoding.UTF8.GetBytes(token);
        // CryptographicOperations.FixedTimeEquals only treats equal-length inputs as potentially
        // equal; differing lengths short-circuit to false but in constant time relative to the
        // shorter input. That's the desired property.
        return CryptographicOperations.FixedTimeEquals(actual, _expectedTokenBytes);
    }

    /// <summary>Suppress the logger-field unused warning when injection runs but no log fires.</summary>
    /// <returns>The current logger; intended only for tests that want to verify wiring.</returns>
    internal ILogger<ApiAuth> Logger => _logger;
}
