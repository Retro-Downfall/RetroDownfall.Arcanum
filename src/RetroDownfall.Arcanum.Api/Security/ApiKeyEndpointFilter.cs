using Microsoft.AspNetCore.Http;

namespace RetroDownfall.Arcanum.Api.Security;

/// <summary>
/// Defence-in-depth API-key gate for an individual endpoint. The primary gate is the pre-binding
/// middleware installed by <c>ApiBootstrapper.MapArcanumEndpoints</c>: minimal-API endpoint filters
/// run <b>after</b> parameter binding, so a filter-only gate lets an unauthenticated caller have its
/// request body read, spooled and deserialized before the 401 is written. This filter still runs and
/// keeps the route closed for any host that maps these endpoints without the Arcanum middleware
/// pipeline.
/// </summary>
/// <remarks>
/// ASP.NET Core activates one filter per endpoint, so the filter takes the singleton
/// <see cref="ApiKeyAuthenticator"/> rather than building its own: the middleware and every filter then
/// share one digest cache, one in-flight secret-store read and one memory of a failed read, and the
/// second check costs one extra SHA-256 of the presented header — never a second secret-store read.
/// </remarks>
public sealed class ApiKeyEndpointFilter(ApiKeyAuthenticator authenticator) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!await authenticator.IsAuthorizedAsync(context.HttpContext).ConfigureAwait(false))
        {
            return ApiKeyAuthenticator.Unauthorized(context.HttpContext);
        }

        return await next(context).ConfigureAwait(false);
    }
}
