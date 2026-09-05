using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Api.Streaming;
using RetroDownfall.Arcanum.Tests.Fixtures;

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
