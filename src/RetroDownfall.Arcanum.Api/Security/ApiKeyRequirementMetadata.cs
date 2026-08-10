namespace RetroDownfall.Arcanum.Api.Security;

/// <summary>
/// Endpoint marker declaring that a route is gated by the Arcanum API key. Read by the pre-binding
/// authentication middleware (<c>ApiBootstrapper.UseArcanumApiKeyAuthentication</c>) after routing has
/// matched an endpoint but before the endpoint delegate — and therefore before parameter binding —
/// runs. Attached in the same place as <see cref="ApiKeyEndpointFilter"/>
/// (<c>ApiBootstrapper.RequireArcanumApiKey</c>) so the middleware and the filter can never gate
/// different route sets.
/// </summary>
public sealed class ApiKeyRequirementMetadata
{

    public static ApiKeyRequirementMetadata Instance { get; } = new();

    private ApiKeyRequirementMetadata()
    {
    }

}
