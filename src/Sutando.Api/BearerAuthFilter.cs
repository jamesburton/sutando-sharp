using Microsoft.AspNetCore.Http;

namespace Sutando.Api;

/// <summary>
/// Endpoint filter that runs the bearer-token check via <see cref="ApiAuth"/> before any
/// other filter or handler in the group is invoked.
/// </summary>
/// <remarks>
/// Implemented as an <see cref="IEndpointFilter"/> rather than a full authentication scheme
/// because the token contract is single-secret + constant-time compare; standard auth
/// handlers add overhead we do not need.
/// </remarks>
internal sealed class BearerAuthFilter : IEndpointFilter
{
    private readonly ApiAuth _auth;

    /// <summary>Create the filter with a pre-resolved auth checker.</summary>
    /// <param name="auth">The shared <see cref="ApiAuth"/> instance from DI.</param>
    public BearerAuthFilter(ApiAuth auth) => _auth = auth;

    /// <inheritdoc/>
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!_auth.TryAuthorize(context.HttpContext))
        {
            return Results.Unauthorized();
        }
        return await next(context).ConfigureAwait(false);
    }
}
