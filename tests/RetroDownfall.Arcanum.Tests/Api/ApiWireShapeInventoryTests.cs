using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Api.Streaming;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// Every route that answers with something other than the <c>ApiResponse</c> envelope is documented in
/// the API reference, in the table of non-envelope routes or on its own row.
/// </summary>
/// <remarks>
/// <para>The reference opens its wire-shape section by saying every JSON route under <c>/api</c> uses
/// the envelope "except for these", so the table is the one place a client author learns that a route
/// will hand back bytes, a text exposition, an SSE stream or a bodyless header answer instead. A route
/// missing from it reads as an envelope route: a client written from the reference parses a download
/// as JSON and finds out in production.</para>
/// <para>Three readings keep the table honest. Every route under <c>/api</c> that carries
/// <see cref="GrimoireStreamRouteMetadata"/> is a streaming or finite-drain route by declaration, so the
/// live host finds each one and the table has to name it; this is how the attachment content stream
/// and the A2A JSON-RPC route are caught. Every route the table names has to be mapped by the live host,
/// so a row cannot outlive the route it describes. And a route that is neither a stream nor in the table
/// but still answers with no envelope (a bodyless 204 or HEAD answer, a raw download) carries no metadata
/// that says so, so the source is read for the handful of result factories that produce such an answer,
/// per file, against an inventory that names the routes behind each count and the status or shape their
/// documentation has to state. A new bodyless or raw answer changes a count, and the failure asks for the
/// route to be documented and the inventory updated.</para>
/// <para>The table-derived reading cannot notice a deleted row, because it reads the rows it checks, and
/// no discovery sees a route that is outside <c>/api</c>, writes its answer from a handler rather than a
/// result factory, and carries no stream metadata (the Prometheus exposition is the plain case). Those
/// routes are a short fixed list, and each one is asserted to be both mapped and named in the table.</para>
/// <para>The host is built with the A2A server enabled because that surface is off by default and its
/// routes are exactly the ones a default host cannot show. <c>/v1</c> is out of
/// scope on purpose: it is an OpenAI-shaped surface documented as a whole, and the table names only the
/// two <c>/v1</c> routes a reader is most likely to take for native ones.</para>
/// </remarks>
[Collection("ApiHost")]
public sealed class ApiWireShapeInventoryTests
{
    /// <summary>
    /// The routes behind each source file's bodyless or raw answers, with the token the documentation of
    /// the route has to carry. A bodyless success is documented on the route's own row with its status;
    /// a raw download is a row of the non-envelope table.
    /// </summary>
    private static readonly Dictionary<string, BodylessOrRawFile> BodylessOrRawAnswers = new(StringComparer.Ordinal)
    {
        // A peer agent's push notification: a bodyless 202 when accepted, a bodyless 503 when the ledger is unavailable.
        ["A2A/A2ACallbackEndpoints.cs"] = new(3, [("POST /api/conclave/a2a/callbacks/{configId}", "**202**")]),

        ["Conclave/ApprenticeEndpoints.cs"] = new(1, [("DELETE /api/apprentices/{id}", "**204**")]),

        // The presence challenge: a bodyless 204 with proof headers, or a bodyless 503.
        ["Security/ArcanumPresenceEndpoint.cs"] = new(3, [("GET /api/presence", "**204**")]),

        ["Spells/SpellEndpoints.cs"] = new(1, [("DELETE /api/spells/{name}", "**204**")]),

        ["Tower/CampaignEndpoints.cs"] = new(1, [("DELETE /api/campaigns/{id}", "**204**")]),

        // One helper answers both the global and the Campaign CODEX delete.
        ["Tower/CodexEndpoints.cs"] = new(1, [("DELETE /api/codex", "**204**"), ("DELETE /api/campaigns/{id}/codex", "**204**")]),

        // The lexicon delete answers 204 from its handler and from the helper it shares.
        ["Tower/MemoryEndpoints.cs"] = new(2, [("DELETE /api/memory/lexicon/{name}", "**204**")]),

        ["Tower/PromptEndpoints.cs"] = new(1, [("DELETE /api/prompts/{id}", "**204**")]),

        // The single-memory delete answers 204 on two arms (deleted, and already absent).
        ["Tower/SagaEndpoints.cs"] = new(3, [("DELETE /api/saga/{id}", "**204**"), ("DELETE /api/saga", "**204**")]),

        // Three deletes and the attachment content stream, whose raw bytes are a row of the non-envelope table.
        ["Tower/SessionEndpoints.cs"] = new(
            4,
            [
                ("DELETE /api/sessions/{id}", "**204**"),
                ("DELETE /api/sessions/{id}/context-pins/{pinId}", "**204**"),
                ("DELETE /api/sessions/{id}/entries/{entryId}", "**204**"),
                ("GET /api/sessions/{id}/attachments/{attachmentId}/content", "raw bytes"),
            ]),

        // The workspace unregister, and the HEAD size/freshness check, which answers 200 with headers only.
        ["Workspaces/WorkspaceEndpoints.cs"] = new(
            2,
            [
                ("DELETE /api/workspaces/{id}", "**204**"),
                ("HEAD /api/workspaces/{id}/files/contents", "Content-Length"),
            ]),
    };

    /// <summary>
    /// Table routes the host maps from raw <c>IConfiguration</c> at boot rather than from the settings the
    /// test host overrides, so this host cannot show them. The Scalar UI is the one: it is covered, mapped
    /// and refused, by <c>ApiBootstrapperScalarTests</c>.
    /// </summary>
    private static readonly HashSet<string> RoutesMappedOnlyFromRawConfiguration = new(StringComparer.Ordinal)
    {
        "GET /api/scalar",
    };

    /// <summary>
    /// The non-envelope routes that no discovery in this class can see: none carries stream metadata, none
    /// answers through a result factory the source count reads, and the table-derived reading would pass
    /// with the row deleted. <c>GET /metrics</c> writes the Prometheus text from a handler and lives
    /// outside <c>/api</c>; the OpenAPI document and the Scalar UI are mapped by library calls; the Agent
    /// Card is the SDK's own JSON. Each is asserted mapped (the Scalar UI excepted, see
    /// <see cref="RoutesMappedOnlyFromRawConfiguration"/>) and named in the table.
    /// </summary>
    private static readonly string[] NonEnvelopeRoutesNoDiscoveryCanSee =
    [
        "GET /metrics",
        "GET /api/openapi/v1.json",
        "GET /api/scalar",
        "GET /api/conclave/a2a/agent-card",
    ];

    /// <summary>
    /// A result factory that answers without an <c>ApiResponse</c> body: a bodyless 204, 200 or 202, a bare
    /// status, or a raw stream, text, content, file or byte answer. A factory given an envelope (for
    /// example <c>Results.Accepted(location, envelope)</c>) is not one.
    /// </summary>
    private static readonly Regex BodylessOrRawAnswer = new(
        @"\b(?:Typed)?Results\.(?:(?:NoContent|Accepted|Ok)\(\)|(?:Stream|Text|Content|File|Bytes|StatusCode)\()",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(30));

    private static readonly Regex TableRoute = new(
        @"`(?<method>GET|POST|PUT|DELETE|PATCH|HEAD) (?<path>/[^`]*)`",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    private static readonly Regex ReferenceRow = new(
        @"^\| (?<method>GET|POST|PUT|DELETE|PATCH|HEAD) \| `(?<path>/[^`]*)` \| (?<text>.*)\|$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    private static readonly Regex RouteParameterConstraint = new(
        @"\{(?:\*{1,2})?(?<name>[^{}:?=*]+)(?:[:?=][^{}]*)?\}",
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
    public async Task Every_route_the_non_envelope_table_names_is_mapped()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateHostWithA2AServer();

        List<(string Method, Regex Template)> templates = MappedRouteTemplates(factory);

        string[] missing =
        [
            .. NonEnvelopeTableRoutes()
                .Where(route => !RoutesMappedOnlyFromRawConfiguration.Contains(route))
                .Where(route => !IsMapped(templates, route))
                .Order(StringComparer.Ordinal),
        ];

        Assert.True(
            missing.Length == 0,
            "A row of the non-envelope table in docs/Arcanum.API.md names a route the host does not map:\n"
                + string.Join('\n', missing));
    }

    /// <summary>
    /// A route no discovery can see is pinned by name: the host maps it, and the table names it. The
    /// table-derived reading above would pass if its row were deleted, so this is the reading that fails.
    /// </summary>
    [SkippableFact]
    public async Task The_non_envelope_routes_no_discovery_can_see_are_mapped_and_named_in_the_table()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateHostWithA2AServer();

        List<(string Method, Regex Template)> templates = MappedRouteTemplates(factory);

        HashSet<string> named = NonEnvelopeTableRoutes();

        List<string> offenders = [];

        foreach (string route in NonEnvelopeRoutesNoDiscoveryCanSee)
        {
            if (!RoutesMappedOnlyFromRawConfiguration.Contains(route) && !IsMapped(templates, route))
            {
                offenders.Add($"{route} is listed here but the host does not map it");
            }

            if (!named.Contains(route))
            {
                offenders.Add($"{route} has no row in the non-envelope table of docs/Arcanum.API.md");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A non-envelope route that no discovery can see is not both mapped and named in the table:\n"
                + string.Join('\n', offenders));
    }

    /// <summary>
    /// The A2A pair is mapped when the A2A server flag is set, with no Conclave flag beside it, and is
    /// absent without it: the Conclave setting is derived from the A2A flags, and the API reference says
    /// the one flag gates the two routes.
    /// </summary>
    [SkippableFact]
    public async Task The_a2a_routes_are_mapped_by_the_server_flag_alone_and_absent_without_it()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string[] pair = ["GET /api/conclave/a2a/agent-card", "POST /api/conclave/a2a"];

        await using (ArcanumWebApplicationFactory enabled = CreateHostWithA2AServer())
        {
            HashSet<string> mapped = MappedRouteNames(enabled);

            foreach (string route in pair)
            {
                Assert.Contains(route, mapped);
            }
        }

        await using ArcanumWebApplicationFactory disabled = new();

        HashSet<string> defaultMapped = MappedRouteNames(disabled);

        foreach (string route in pair)
        {
            Assert.DoesNotContain(route, defaultMapped);
        }
    }

    /// <summary>
    /// The result factories that answer with no <c>ApiResponse</c> body (a 204, a bodyless 202, 200 or
    /// status, a raw stream) are counted per source file, and each count names the routes it is, so a route
    /// added with such an answer fails here until it is documented. The server-sent event routes finish
    /// with <c>Results.Empty</c> and are found by their stream metadata instead.
    /// </summary>
    [Fact]
    public void Every_bodyless_or_raw_answer_in_the_api_sources_is_counted_against_a_documented_route()
    {
        string api = Path.Combine(
            TestRepositoryPaths.RepositoryRoot(),
            "src",
            "RetroDownfall.Arcanum.Api");

        Dictionary<string, int> found = new(StringComparer.Ordinal);

        foreach (string path in Directory.EnumerateFiles(api, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(api, path).Replace(Path.DirectorySeparatorChar, '/');

            if (relative.StartsWith("bin/", StringComparison.Ordinal)
                || relative.StartsWith("obj/", StringComparison.Ordinal)
                || relative.StartsWith("OpenAiV1", StringComparison.Ordinal))
            {
                continue;
            }

            int count = BodylessOrRawAnswer.Count(File.ReadAllText(path));

            if (count > 0)
            {
                found[relative] = count;
            }
        }

        // The factory pattern has to find what it is there to count, or an empty scan would pass.
        Assert.True(found.Values.Sum() >= 20, $"Only {found.Values.Sum()} bodyless or raw answers were found.");

        string[] differences =
        [
            .. BodylessOrRawAnswers.Keys
                .Union(found.Keys, StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Where(file => found.GetValueOrDefault(file) != (BodylessOrRawAnswers.TryGetValue(file, out BodylessOrRawFile? entry) ? entry.Count : 0))
                .Select(file => $"{file}: the source has {found.GetValueOrDefault(file)}, the inventory says {(BodylessOrRawAnswers.TryGetValue(file, out BodylessOrRawFile? known) ? known.Count : 0)}"),
        ];

        Assert.True(
            differences.Length == 0,
            "A route now answers (or no longer answers) without an ApiResponse body. Document the route (a bodyless success on its"
                + " own row with its status, a raw download in the non-envelope table of docs/Arcanum.API.md), then update the"
                + " inventory in this test:\n"
                + string.Join('\n', differences));

        Dictionary<string, string> documented = DocumentedRoutes();

        List<string> undocumented = [];

        foreach ((string file, BodylessOrRawFile entry) in BodylessOrRawAnswers)
        {
            foreach ((string route, string token) in entry.Routes)
            {
                if (!documented.TryGetValue(route, out string? text))
                {
                    undocumented.Add($"{file}: {route} has no row in docs/Arcanum.API.md");
                }
                else if (!text.Contains(token, StringComparison.Ordinal))
                {
                    undocumented.Add($"{file}: the row for {route} does not say {token}");
                }
            }
        }

        Assert.True(
            undocumented.Count == 0,
            "A route with a bodyless or raw answer is not documented as one:\n" + string.Join('\n', undocumented));
    }

    private static ArcanumWebApplicationFactory CreateHostWithA2AServer() =>
        new()
        {
            SettingsOverride = settings => settings with
            {
                Features = (settings.Features ?? new FeatureSettings()) with
                {
                    A2AServer = true,
                },
            },
        };

    /// <summary>
    /// The mapped routes as templates. The reference names a concrete route (the OpenAPI document is
    /// <c>/api/openapi/v1.json</c>) where the host maps a template (<c>/api/openapi/{documentName}.json</c>),
    /// so a documented route is mapped when a mapped route of its method matches it with each parameter
    /// standing for one segment's text.
    /// </summary>
    private static List<(string Method, Regex Template)> MappedRouteTemplates(ArcanumWebApplicationFactory factory) =>
    [
        .. MappedRouteNames(factory).Select(static route =>
        {
            string[] parts = route.Split(' ', 2);

            return (parts[0], new Regex(
                "^" + Regex.Replace(Regex.Escape(parts[1]), @"\\\{[^/]*?\}", "[^/]+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5)) + "$",
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(5)));
        }),
    ];

    private static bool IsMapped(
        List<(string Method, Regex Template)> templates,
        string route)
    {
        string[] parts = route.Split(' ', 2);

        return templates.Any(template => template.Method == parts[0] && template.Template.IsMatch(parts[1]));
    }

    private static HashSet<string> MappedRouteNames(ArcanumWebApplicationFactory factory) =>
        new(
            MappedRoutes(factory).Select(static route => $"{route.Method} {route.Path}"),
            StringComparer.Ordinal);

    private static IEnumerable<(string Method, string Path, RouteEndpoint Endpoint)> MappedRoutes(
        ArcanumWebApplicationFactory factory)
    {
        _ = factory.CreateAuthenticatedClient();

        EndpointDataSource endpoints = factory.Services.GetRequiredService<EndpointDataSource>();

        foreach (RouteEndpoint endpoint in endpoints.Endpoints.OfType<RouteEndpoint>())
        {
            string path = NormalizeRoute(endpoint.RoutePattern.RawText ?? string.Empty);

            foreach (string method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
            {
                yield return (method, path, endpoint);
            }
        }
    }

    /// <summary>A route pattern with its parameter constraints and catch-all markers removed, as the reference writes it.</summary>
    private static string NormalizeRoute(string pattern) =>
        RouteParameterConstraint
            .Replace(pattern, "{${name}}")
            .TrimEnd('/');

    private static string ReadApi() =>
        File
            .ReadAllText(Path.Combine(TestRepositoryPaths.RepositoryRoot(), "docs", "Arcanum.API.md"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>
    /// The method and path pairs the first cell of each row in the non-envelope table names, which can
    /// hold more than one (the OpenAPI document and the Scalar UI share a row).
    /// </summary>
    private static HashSet<string> NonEnvelopeTableRoutes()
    {
        string api = ReadApi();

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
                routes.Add($"{match.Groups["method"].Value} {NormalizeRoute(match.Groups["path"].Value)}");
            }
        }

        Assert.True(routes.Count >= 12, $"Only {routes.Count} routes were read from the non-envelope table.");

        return routes;
    }

    /// <summary>
    /// Every route the reference documents, in its route tables or the non-envelope table, with the text
    /// of the row or rows that document it.
    /// </summary>
    private static Dictionary<string, string> DocumentedRoutes()
    {
        string api = ReadApi();

        Dictionary<string, string> documented = new(StringComparer.Ordinal);

        void Add(
            string route,
            string text)
        {
            documented[route] = documented.TryGetValue(route, out string? existing)
                ? $"{existing} {text}"
                : text;
        }

        int tableStart = api.IndexOf("except for these non-envelope routes:", StringComparison.Ordinal);

        int tableEnd = api.IndexOf("\nEnvelope-payload specifics:", tableStart, StringComparison.Ordinal);

        foreach (string line in api.Split('\n'))
        {
            Match row = ReferenceRow.Match(line);

            if (row.Success)
            {
                Add($"{row.Groups["method"].Value} {NormalizeRoute(row.Groups["path"].Value)}", row.Groups["text"].Value);
            }
        }

        foreach (string row in api[tableStart..tableEnd].Split('\n').Where(static line => line.StartsWith("| `", StringComparison.Ordinal)))
        {
            string[] cells = row.Split('|');

            foreach (Match match in TableRoute.Matches(cells[1]))
            {
                Add($"{match.Groups["method"].Value} {NormalizeRoute(match.Groups["path"].Value)}", cells[2]);
            }
        }

        Assert.True(documented.Count > 150, $"Only {documented.Count} routes were read from the reference.");

        return documented;
    }

    private sealed record BodylessOrRawFile(
        int Count,
        (string Route, string Token)[] Routes);
}
