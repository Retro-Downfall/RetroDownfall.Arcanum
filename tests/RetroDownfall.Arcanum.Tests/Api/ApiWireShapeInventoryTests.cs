using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Api.Streaming;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// Every route that answers with something other than the <c>ApiResponse</c> envelope is named in the
/// API reference's table of non-envelope routes.
/// </summary>
/// <remarks>
/// <para>The reference opens its wire-shape section by saying every JSON route under <c>/api</c> uses
/// the envelope "except for these", so the table is the one place a client author learns that a route
/// will hand back bytes, a text exposition, an SSE stream or a bodyless header answer instead. A route
/// missing from it reads as an envelope route: a client written from the reference parses a download
/// as JSON and finds out in production.</para>
/// <para>The inventory is read from the live host rather than from the document so that a route added
/// later is caught without anyone remembering this table. Two sources together cover the surface.
/// Every route under <c>/api</c> that carries <see cref="GrimoireStreamRouteMetadata"/> is a streaming
/// or finite-drain route by declaration, which is how the attachment content stream and the A2A
/// JSON-RPC route are found. The routes that are not streams but still leave the envelope (the presence
/// challenge, the Prometheus exposition and the Agent Card) carry no metadata that says so, so they are
/// listed by name, and the test fails if one stops being mapped.</para>
/// <para>The host is built with the A2A server enabled because that surface is the one that is off by
/// default and its two routes are exactly the ones a default host cannot show. <c>/v1</c> is out of
/// scope on purpose: it is an OpenAI-shaped surface documented as a whole, and the table names only
/// the two <c>/v1</c> routes a reader is most likely to take for native ones.</para>
/// </remarks>
[Collection("ApiHost")]
public sealed class ApiWireShapeInventoryTests
{
    /// <summary>
    /// Routes that leave the envelope without declaring a stream, so no metadata can find them.
    /// </summary>
    private static readonly string[] NonEnvelopeRoutesWithoutStreamMetadata =
    [
        "GET /api/presence",
        "GET /metrics",
        "GET /api/conclave/a2a/agent-card",
    ];

    private static readonly Regex TableRoute = new(
        @"`(?<method>GET|POST|PUT|DELETE|PATCH) (?<path>/[^`]*)`",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    private static readonly Regex RouteParameterConstraint = new(
        @"\{(?<name>[^{}:?=*]+)[:?=][^{}]*\}",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    [SkippableFact]
    public async Task Every_streaming_route_under_api_is_named_in_the_non_envelope_table()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateHostWithA2AServer();

        HashSet<string> named = NonEnvelopeTableRoutes();

        List<string> unnamed = [];

        int streamingRoutes = 0;

        foreach ((string method, string path, RouteEndpoint endpoint) in MappedRoutes(factory))
        {
            if (!path.StartsWith("/api/", StringComparison.Ordinal)
                || endpoint.Metadata.GetMetadata<GrimoireStreamRouteMetadata>() is null)
            {
                continue;
            }

            streamingRoutes++;

            if (!named.Contains($"{method} {path}"))
            {
                unnamed.Add($"{method} {path}");
            }
        }

        // A read that found no stream would pass vacuously; the host maps a dozen under /api.
        Assert.True(streamingRoutes >= 10, $"Only {streamingRoutes} streaming routes were found under /api.");

        Assert.True(
            unnamed.Count == 0,
            "A streaming route has no row in the non-envelope table of docs/Arcanum.API.md:\n"
                + string.Join('\n', unnamed.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)));
    }

    [SkippableFact]
    public async Task The_named_non_stream_routes_that_leave_the_envelope_are_mapped_and_in_the_table()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateHostWithA2AServer();

        HashSet<string> named = NonEnvelopeTableRoutes();

        HashSet<string> mapped = new(
            MappedRoutes(factory).Select(static route => $"{route.Method} {route.Path}"),
            StringComparer.Ordinal);

        List<string> offenders = [];

        foreach (string route in NonEnvelopeRoutesWithoutStreamMetadata)
        {
            if (!mapped.Contains(route))
            {
                offenders.Add($"{route} is listed here but the host no longer maps it");
            }

            if (!named.Contains(route))
            {
                offenders.Add($"{route} has no row in the non-envelope table of docs/Arcanum.API.md");
            }
        }

        Assert.True(offenders.Count == 0, string.Join('\n', offenders));
    }

    private static ArcanumWebApplicationFactory CreateHostWithA2AServer() =>
        new()
        {
            SettingsOverride = settings => settings with
            {
                Features = (settings.Features ?? new FeatureSettings()) with
                {
                    Conclave = true,
                    A2AServer = true,
                },
            },
        };

    private static IEnumerable<(string Method, string Path, RouteEndpoint Endpoint)> MappedRoutes(
        ArcanumWebApplicationFactory factory)
    {
        _ = factory.CreateAuthenticatedClient();

        EndpointDataSource endpoints = factory.Services.GetRequiredService<EndpointDataSource>();

        foreach (RouteEndpoint endpoint in endpoints.Endpoints.OfType<RouteEndpoint>())
        {
            string path = RouteParameterConstraint
                .Replace(endpoint.RoutePattern.RawText ?? string.Empty, "{${name}}")
                .TrimEnd('/');

            foreach (string method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
            {
                yield return (method, path, endpoint);
            }
        }
    }

    /// <summary>
    /// The method and path pairs the first cell of each row in the non-envelope table names, which can
    /// hold more than one (the OpenAPI document and the Scalar UI share a row).
    /// </summary>
    private static HashSet<string> NonEnvelopeTableRoutes()
    {
        string api = File
            .ReadAllText(Path.Combine(TestRepositoryPaths.RepositoryRoot(), "docs", "Arcanum.API.md"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        int start = api.IndexOf("except for these non-envelope routes:", StringComparison.Ordinal);

        Assert.True(start >= 0, "The API reference lost its table of non-envelope routes.");

        int end = api.IndexOf("\nEnvelope-payload specifics:", start, StringComparison.Ordinal);

        Assert.True(end > start, "The non-envelope table no longer ends at 'Envelope-payload specifics:'.");

        HashSet<string> routes = new(StringComparer.Ordinal);

        foreach (string row in api[start..end].Split('\n').Where(static line => line.StartsWith("| `", StringComparison.Ordinal)))
        {
            string firstCell = row.Split('|')[1];

            foreach (Match match in TableRoute.Matches(firstCell))
            {
                routes.Add($"{match.Groups["method"].Value} {match.Groups["path"].Value.TrimEnd('/')}");
            }
        }

        Assert.True(routes.Count >= 12, $"Only {routes.Count} routes were read from the non-envelope table.");

        return routes;
    }
}
