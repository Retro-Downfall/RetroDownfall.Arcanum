using Microsoft.AspNetCore.Http;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Api.Security;

/// <summary>
/// Defence-in-depth API-key gate for an individual endpoint. The primary gate is the pre-binding
/// middleware installed by <c>ApiBootstrapper.MapArcanumEndpoints</c>: minimal-API endpoint filters
/// run <b>after</b> parameter binding, so a filter-only gate lets an unauthenticated caller have its
/// request body read, spooled and deserialized before the 401 is written. This filter still runs
/// (both gates share the singleton <see cref="IApiKeyDigestCache"/>, so the second check costs one
/// extra SHA-256 of the presented header) and keeps the route closed for any host that maps these
/// endpoints without the Arcanum middleware pipeline.
/// </summary>
public sealed class ApiKeyEndpointFilter(
    ISecretStore secretStore,
    IApiKeyDigestCache digestCache) : IEndpointFilter
{
    private readonly ApiKeyAuthenticator _authenticator = new(secretStore, digestCache);

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!await _authenticator.IsAuthorizedAsync(context.HttpContext).ConfigureAwait(false))
        {
            return ApiKeyAuthenticator.Unauthorized(context.HttpContext);
        }

        return await next(context).ConfigureAwait(false);
    }
}
