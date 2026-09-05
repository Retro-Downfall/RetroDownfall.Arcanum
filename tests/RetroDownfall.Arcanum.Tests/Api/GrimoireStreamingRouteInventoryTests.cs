using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Api.Streaming;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// The closed streaming surface: what a route may be, and what says so.
/// </summary>
/// <remarks>
/// The vocabulary is asserted rather than assumed because both enums are read by an inventory that
/// fails on anything it has not been told about. An added member with no route behind it would widen
/// what the catalog accepts without widening what anything proves, which is the one way a closed set
/// stops being closed without any test going red.
/// </remarks>
[Collection("ApiHost")]
public sealed class GrimoireStreamingRouteInventoryTests
{

    /// <summary>The complete positive quiesceable set, by endpoint name.</summary>
    private static readonly string[] QuiesceableEndpointNames =
    [
        "GetApprenticeChronicle",
        "GetDaemonEvents",
        "GetMcpEvents",
        "StreamLogs",
        "StreamSession",
    ];

    /// <summary>Every streaming route that is drained rather than quiesced, by endpoint name.</summary>
    private static readonly string[] DrainedEndpointNames =
    [
        "DownloadSessionAttachment",
        "GetOpenAiFileContent",
        "PostIntelligencePingStream",
        "PostOpenAiChatCompletions",
        "PostWebResearch",
        "Prompt_ExecuteStream",
        "Spell_ExecuteStream",
    ];

    private readonly ArcanumWebApplicationFactory _factory;

    public GrimoireStreamingRouteInventoryTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    /// <summary>
    /// Three classes, each with a live population, and no default-initialized member.
    /// </summary>
    /// <remarks>
    /// Zero is deliberately absent for the reason every persisted-adjacent enum in this repository
    /// omits it: a default-initialized value would read as a real classification on a marker somebody
    /// forgot to set, and "unset" and "finite" must never be the same answer — one is a route nobody
    /// classified and the other is a route that was classified and drains.
    /// </remarks>
    [Fact]
    public void The_class_vocabulary_is_exactly_three_named_members()
    {

        Assert.Equal(
            ["GrimoireQuiesceableStream", "FiniteDrain", "BillableDrain"],
            Enum.GetNames<GrimoireStreamClass>());

        Assert.Equal(1, (byte)GrimoireStreamClass.GrimoireQuiesceableStream);

        Assert.Equal(2, (byte)GrimoireStreamClass.FiniteDrain);

        Assert.Equal(3, (byte)GrimoireStreamClass.BillableDrain);

        Assert.DoesNotContain(GrimoireStreamClass.GrimoireQuiesceableStream, (GrimoireStreamClass[])[default]);

    }

    /// <summary>
    /// Authority is its own axis, because a quiesceable stream need not touch the database.
    /// </summary>
    /// <remarks>
    /// The three event routes read no Grimoire at all and are still in the complete positive
    /// quiesceable set: what makes a route quiesceable is that it is unbounded and declared, not that
    /// it holds a connection. Collapsing the two axes into one enum would force those three to be
    /// either quiesceable or authority-free and they are both.
    /// </remarks>
    [Fact]
    public void The_authority_vocabulary_is_exactly_two_named_members()
    {

        Assert.Equal(
            ["LiveGrimoire", "NoGrimoireAuthority"],
            Enum.GetNames<GrimoireStreamAuthority>());

        Assert.Equal(1, (byte)GrimoireStreamAuthority.LiveGrimoire);

        Assert.Equal(2, (byte)GrimoireStreamAuthority.NoGrimoireAuthority);

    }

    /// <summary>
    /// The marker carries one defined class and refuses anything else.
    /// </summary>
    /// <remarks>
    /// Refused at construction rather than read defensively at the admission stage, because the
    /// admission stage runs per request and a marker is built once at composition. A cast integer
    /// reaching the gate would select a request kind from a value no branch names.
    /// </remarks>
    [Fact]
    public void The_marker_refuses_a_class_the_vocabulary_does_not_define()
    {

        Assert.Equal(
            GrimoireStreamClass.GrimoireQuiesceableStream,
            GrimoireStreamRouteMetadata.Quiesceable.Class);

        Assert.Equal(
            GrimoireStreamClass.FiniteDrain,
            GrimoireStreamRouteMetadata.FiniteDrain.Class);

        Assert.Equal(
            GrimoireStreamClass.BillableDrain,
            GrimoireStreamRouteMetadata.BillableDrain.Class);

        _ = Assert.Throws<ArgumentOutOfRangeException>(
            static () => new GrimoireStreamRouteMetadata((GrimoireStreamClass)0));

        _ = Assert.Throws<ArgumentOutOfRangeException>(
            static () => new GrimoireStreamRouteMetadata((GrimoireStreamClass)99));

    }

    /// <summary>
    /// The three shared markers are singletons, so attaching one allocates nothing per route.
    /// </summary>
    [Fact]
    public void The_shared_markers_are_the_same_instance_every_time()
    {

        Assert.Same(GrimoireStreamRouteMetadata.Quiesceable, GrimoireStreamRouteMetadata.Quiesceable);

        Assert.Same(GrimoireStreamRouteMetadata.FiniteDrain, GrimoireStreamRouteMetadata.FiniteDrain);

        Assert.Same(GrimoireStreamRouteMetadata.BillableDrain, GrimoireStreamRouteMetadata.BillableDrain);

    }

    /// <summary>
    /// Exactly five composed routes are quiesceable, and they are the five the parent design names.
    /// </summary>
    /// <remarks>
    /// Asserted over the composed host's own endpoints rather than over source, because what decides
    /// a request's lease kind is the metadata that actually reached the route table. A sixth route
    /// marked quiesceable would be a stream a transition may cut, and that is a decision the parent
    /// design reserves to itself.
    /// </remarks>
    [Fact]
    public void Exactly_five_composed_routes_are_quiesceable()
    {

        Assert.Equal(
            QuiesceableEndpointNames,
            NamesWithClass(GrimoireStreamClass.GrimoireQuiesceableStream));

    }

    /// <summary>
    /// Every other streaming route carries a marker naming why it is drained instead.
    /// </summary>
    /// <remarks>
    /// The list is exact in both directions. A streaming route that lost its marker would silently
    /// fall back to a finite lease and still work, so nothing behavioural would fail — which is
    /// precisely why the classification is asserted rather than inferred.
    /// </remarks>
    [Fact]
    public void Every_other_streaming_route_is_classified_as_drained()
    {

        string[] drained =
        [
            .. NamesWithClass(GrimoireStreamClass.FiniteDrain)
                .Concat(NamesWithClass(GrimoireStreamClass.BillableDrain))
                .OrderBy(static name => name, StringComparer.Ordinal),
        ];

        Assert.Equal(DrainedEndpointNames, drained);

    }

    /// <summary>
    /// The two byte-body downloads are finite, and the five provider-backed streams are billable.
    /// </summary>
    /// <remarks>
    /// The split is the operator-visible half of the classification. A finite stream is drained
    /// because it ends on its own; a billable one is drained because cutting it would charge for an
    /// answer nobody receives. Recording only "not quiesceable" would lose which of those it was.
    /// </remarks>
    [Fact]
    public void The_drained_routes_say_which_kind_of_drained_they_are()
    {

        Assert.Equal(
            ["DownloadSessionAttachment", "GetOpenAiFileContent"],
            NamesWithClass(GrimoireStreamClass.FiniteDrain));

        Assert.Equal(
            [
                "PostIntelligencePingStream",
                "PostOpenAiChatCompletions",
                "PostWebResearch",
                "Prompt_ExecuteStream",
                "Spell_ExecuteStream",
            ],
            NamesWithClass(GrimoireStreamClass.BillableDrain).Where(static name => name.Length > 0));

    }

    /// <summary>
    /// The composed endpoint names of every route carrying the supplied class, ordered.
    /// </summary>
    /// <remarks>
    /// The A2A server surface is deliberately excluded from the name-keyed assertions above: its
    /// routes are mapped by <c>A2A.AspNetCore</c> and carry no <c>.WithName(...)</c> of ours, so they
    /// have no stable name to assert on. That they carry the marker at all is covered by the source
    /// inventory, which keys on the <c>MapA2A</c> call site instead.
    /// </remarks>
    /// <summary>
    /// A streaming construct nobody classified fails on its own, without any other source present.
    /// </summary>
    /// <remarks>
    /// Injected rather than observed, because the property under test is what happens to a route that
    /// does not exist yet. A suite that could only assert over today's sources would pass for as long
    /// as nobody added a stream, which is exactly the interval it is supposed to cover.
    /// </remarks>
    [Fact]
    public void An_unclassified_streaming_construct_fails_on_its_own()
    {

        StreamingSource unlisted = new(
            "src/Fixtures/Fixture.cs",
            """
            using Microsoft.AspNetCore.Http;
            static class Fixture
            {
                static void Map(IEndpointRouteBuilder api)
                {
                    api.MapGet("/newly-added", (HttpContext ctx) =>
                    {
                        ctx.Response.ContentType = "text/event-stream; charset=utf-8";
                    });
                }
            }
            """);

        IReadOnlyList<StreamingInventoryFailure> failures = GrimoireStreamingRouteScanner.Validate(
            GrimoireStreamingRouteScanner.Discover([unlisted]),
            []);

        StreamingInventoryFailure failure = Assert.Single(failures);

        Assert.Equal(StreamingInventoryFailureCode.UncataloguedDiscovery, failure.Code);

        Assert.Equal("/newly-added", failure.Identity?.RouteLiteral);

    }

    /// <summary>
    /// A catalog entry naming a construct that has moved or gone fails too.
    /// </summary>
    /// <remarks>
    /// The other half of "bidirectional". A catalog that only grew would eventually describe a
    /// codebase that no longer exists, and every classification in it would be a claim nobody checks.
    /// </remarks>
    [Fact]
    public void A_catalog_entry_the_scanner_no_longer_finds_fails()
    {

        IReadOnlyList<StreamingInventoryFailure> failures = GrimoireStreamingRouteScanner.Validate(
            [],
            GrimoireStreamingRouteScanner.Catalog());

        Assert.NotEmpty(failures);

        Assert.All(
            failures,
            static failure =>
                Assert.Equal(StreamingInventoryFailureCode.StaleCatalogEntry, failure.Code));

    }

    /// <summary>
    /// Only the five routes the parent design declares may carry the quiesceable class.
    /// </summary>
    /// <remarks>
    /// A sixth quiesceable route would be a stream a transition may cut at a frame boundary, and which
    /// streams may be cut is a decision the parent design reserves to itself. The check is on the route
    /// pattern rather than on a count, so adding one and removing another still fails.
    /// </remarks>
    [Fact]
    public void A_quiesceable_entry_outside_the_declared_five_fails()
    {

        StreamingCatalogEntry undeclared = GrimoireStreamingRouteScanner.Catalog()
            .First(static entry => entry.Class == GrimoireStreamClass.GrimoireQuiesceableStream)
            with
            { RoutePattern = "/api/events/something-new" };

        IReadOnlyList<StreamingInventoryFailure> failures = GrimoireStreamingRouteScanner.Validate(
            [undeclared.Identity],
            [undeclared]);

        Assert.Contains(
            failures,
            static failure =>
                failure.Code == StreamingInventoryFailureCode.QuiesceableRouteNotDeclared);

    }

    /// <summary>
    /// A stream that is drained rather than quiesced has to record why it is drained.
    /// </summary>
    [Fact]
    public void A_drained_entry_with_no_proof_fails()
    {

        StreamingCatalogEntry unproved = GrimoireStreamingRouteScanner.Catalog()
            .First(static entry => entry.Class == GrimoireStreamClass.BillableDrain)
            with
            { Proof = null };

        IReadOnlyList<StreamingInventoryFailure> failures = GrimoireStreamingRouteScanner.Validate(
            [unproved.Identity],
            [unproved]);

        Assert.Contains(
            failures,
            static failure => failure.Code == StreamingInventoryFailureCode.MissingDrainProof);

    }

    /// <summary>
    /// A wildcard identity is refused, so a catalog cannot classify a family it never enumerated.
    /// </summary>
    [Fact]
    public void A_wildcard_identity_fails()
    {

        StreamingCatalogEntry broad = GrimoireStreamingRouteScanner.Catalog()[0] with
        {
            Identity = GrimoireStreamingRouteScanner.Catalog()[0].Identity with
            {
                RelativePath = "src/RetroDownfall.Arcanum.Api/Streaming/*.cs",
            },
        };

        IReadOnlyList<StreamingInventoryFailure> failures = GrimoireStreamingRouteScanner.Validate(
            [broad.Identity],
            [broad]);

        Assert.Contains(
            failures,
            static failure => failure.Code == StreamingInventoryFailureCode.WildcardIdentity);

    }

    /// <summary>
    /// Production and the catalog agree exactly, in both directions.
    /// </summary>
    /// <remarks>
    /// This is the assertion the whole file exists for. Every other case proves the validator can
    /// fail; this one proves the repository currently passes it.
    /// </remarks>
    [Fact]
    public void Every_authored_streaming_construct_is_classified_and_nothing_else_is()
    {

        IReadOnlyList<StreamingIdentity> discovered = GrimoireStreamingRouteScanner.Discover(
            GrimoireStreamingRouteScanner.ProductionSources());

        IReadOnlyList<StreamingInventoryFailure> failures = GrimoireStreamingRouteScanner.Validate(
            discovered,
            GrimoireStreamingRouteScanner.Catalog());

        Assert.Empty(
            failures.Select(static failure =>
                $"{failure.Code}: {failure.Identity?.RelativePath} {failure.Identity?.RouteLiteral} — {failure.Detail}"));

        Assert.Equal(15, discovered.Count);

        Assert.Equal(discovered.Count, GrimoireStreamingRouteScanner.Catalog().Count);

    }

    /// <summary>
    /// The catalog's five quiesceable entries are the five declared route patterns.
    /// </summary>
    [Fact]
    public void The_catalog_names_exactly_the_five_declared_quiesceable_routes()
    {

        string[] quiesceable =
        [
            .. GrimoireStreamingRouteScanner.Catalog()
                .Where(static entry => entry.Class == GrimoireStreamClass.GrimoireQuiesceableStream)
                .Select(static entry => entry.RoutePattern)
                .OrderBy(static pattern => pattern, StringComparer.Ordinal),
        ];

        Assert.Equal(GrimoireStreamingRouteScanner.DeclaredQuiesceableRoutes, quiesceable);

    }

    /// <summary>
    /// Every catalogued route pattern is a route the composed host actually maps.
    /// </summary>
    /// <remarks>
    /// The source inventory proves a construct was classified; this proves the classification names a
    /// real route rather than a pattern that was accurate when it was written. The two shared writers
    /// and the package-owned A2A surface are excluded because neither has a route pattern of ours —
    /// which is what their proofs already record.
    /// </remarks>
    [Fact]
    public void Every_catalogued_route_pattern_is_mapped_by_the_composed_host()
    {

        _ = _factory.CreateAuthenticatedClient();

        EndpointDataSource endpoints = _factory.Services.GetRequiredService<EndpointDataSource>();

        HashSet<string> mapped =
        [
            .. endpoints.Endpoints
                .OfType<RouteEndpoint>()
                .Select(static endpoint => "/" + endpoint.RoutePattern.RawText?.TrimStart('/')),
        ];

        string[] expected =
        [
            .. GrimoireStreamingRouteScanner.Catalog()
                .Where(static entry => entry.Proof
                    is not StreamingEntryProofKind.SharedWriterDeclaration
                    and not StreamingEntryProofKind.ThirdPartyFraming)
                .Select(static entry => entry.RoutePattern)
                .Distinct(StringComparer.Ordinal),
        ];

        Assert.All(expected, pattern => Assert.Contains(pattern, mapped));

    }

    /// <summary>
    /// Every catalogued route's class is the class its endpoint actually carries.
    /// </summary>
    /// <remarks>
    /// This is the join, and without it the two halves of the inventory prove less together than they
    /// appear to. The source scanner proves a construct was classified <i>somewhere</i>; the marker
    /// proves an endpoint takes a particular lease. Nothing connected them, so a new SSE route that
    /// nobody marked could be catalogued as <c>FiniteDrain</c> and every assertion would stay green
    /// while the route silently took a finite lease and held a transition open until it timed out.
    ///
    /// <para>Asserted per route pattern rather than in aggregate so a failure names the route whose
    /// classification and marker disagree, instead of reporting that two sets differ.</para>
    /// </remarks>
    [Fact]
    public void Every_catalogued_routes_class_matches_the_marker_its_endpoint_carries()
    {

        _ = _factory.CreateAuthenticatedClient();

        EndpointDataSource endpoints = _factory.Services.GetRequiredService<EndpointDataSource>();

        Dictionary<string, GrimoireStreamClass?> markedByPattern = [];

        foreach (RouteEndpoint endpoint in endpoints.Endpoints.OfType<RouteEndpoint>())
        {

            markedByPattern["/" + (endpoint.RoutePattern.RawText?.TrimStart('/') ?? string.Empty)] =
                endpoint.Metadata.GetMetadata<GrimoireStreamRouteMetadata>()?.Class;

        }

        foreach (StreamingCatalogEntry entry in GrimoireStreamingRouteScanner.Catalog())
        {

            // A shared writer has no route of its own, and the A2A surface is mapped by its package
            // under an operator-configurable path this host leaves disabled. Both say so in their
            // proof, which is what makes the exclusion a recorded decision rather than a gap.
            if (entry.Proof is StreamingEntryProofKind.SharedWriterDeclaration
                or StreamingEntryProofKind.ThirdPartyFraming)
            {

                continue;

            }

            Assert.True(
                markedByPattern.TryGetValue(entry.RoutePattern, out GrimoireStreamClass? marked),
                $"the catalog names {entry.RoutePattern}, which this host maps no endpoint for");

            Assert.Equal(entry.Class, marked);

        }

    }

    /// <summary>
    /// Every endpoint carrying a streaming marker is a route the catalog classifies.
    /// </summary>
    /// <remarks>
    /// The other direction of the same join. A route marked in composition but absent from the
    /// catalog is one whose classification nobody reviewed, and it would be invisible to the
    /// source scanner whenever its framing lives in a shared writer or a handler bound by method
    /// group. Names are deliberately not used here: nothing obliges a streaming route to call
    /// <c>WithName</c>, and an assertion keyed on names would simply not see one that skipped it.
    /// </remarks>
    [Fact]
    public void Every_marked_endpoint_is_a_route_the_catalog_classifies()
    {

        _ = _factory.CreateAuthenticatedClient();

        EndpointDataSource endpoints = _factory.Services.GetRequiredService<EndpointDataSource>();

        HashSet<string> catalogued =
        [
            .. GrimoireStreamingRouteScanner.Catalog().Select(static entry => entry.RoutePattern),
        ];

        string[] markedButUncatalogued =
        [
            .. endpoints.Endpoints
                .OfType<RouteEndpoint>()
                .Where(static endpoint =>
                    endpoint.Metadata.GetMetadata<GrimoireStreamRouteMetadata>() is not null)
                .Select(static endpoint => "/" + (endpoint.RoutePattern.RawText?.TrimStart('/') ?? string.Empty))
                .Where(pattern => !catalogued.Contains(pattern))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static pattern => pattern, StringComparer.Ordinal),
        ];

        Assert.Empty(markedButUncatalogued);

    }

    private string[] NamesWithClass(GrimoireStreamClass streamClass)
    {

        _ = _factory.CreateAuthenticatedClient();

        EndpointDataSource endpoints = _factory.Services.GetRequiredService<EndpointDataSource>();

        return
        [
            .. endpoints.Endpoints
                .Where(candidate =>
                    candidate.Metadata.GetMetadata<GrimoireStreamRouteMetadata>()?.Class == streamClass)
                .Select(static candidate =>
                    candidate.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ?? string.Empty)
                .Where(static name => name.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static name => name, StringComparer.Ordinal),
        ];

    }

}
