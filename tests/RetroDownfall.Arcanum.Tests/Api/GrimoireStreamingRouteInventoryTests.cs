using RetroDownfall.Arcanum.Api.Streaming;

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
public sealed class GrimoireStreamingRouteInventoryTests
{

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

}
