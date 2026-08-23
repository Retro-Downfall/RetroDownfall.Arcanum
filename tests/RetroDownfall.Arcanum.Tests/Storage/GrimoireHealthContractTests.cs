using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Tests.Storage;

/// <summary>
/// The Grimoire readiness latch and the Grimoire liveness probe are storage-health contracts, so they
/// are declared in <c>RetroDownfall.Arcanum.Core.Storage</c> beside <see cref="IGrimoireRepository"/>
/// and the other Grimoire ports. Neither describes an agent or an authored resource, so neither belongs
/// to a domain namespace.
/// </summary>
/// <remarks>
/// Pinned because the failure is silent. A contract left behind in the namespace it used to occupy
/// still compiles, because every consumer simply keeps its old <c>using</c>; no diagnostic anywhere in
/// the build distinguishes "moved" from "not moved". The stray sweep is deliberately a scan of the
/// whole Core assembly rather than a check of the two types above: a duplicate declaration left in the
/// old namespace would satisfy a per-type assertion and still leave the name ambiguous.
/// </remarks>
public sealed class GrimoireHealthContractTests
{

    private const string StorageNamespace = "RetroDownfall.Arcanum.Core.Storage";

    private static readonly string[] DomainNamespaces =
    [
        "RetroDownfall.Arcanum.Core.Tower",

        "RetroDownfall.Arcanum.Core.Conclave",

        "RetroDownfall.Arcanum.Core.Tower",
    ];

    public static TheoryData<Type> StorageHealthContracts() =>
    [
        typeof(IGrimoireDbReadiness),

        typeof(IGrimoireLivenessProbe),
    ];

    [Theory]
    [MemberData(nameof(StorageHealthContracts))]
    public void Storage_health_contract_is_declared_beside_the_other_grimoire_ports(Type contract)
    {

        Assert.Equal(StorageNamespace, contract.Namespace);

    }

    [Fact]
    public void No_storage_health_contract_is_declared_in_a_domain_namespace()
    {

        HashSet<string> contractNames =
        [
            nameof(IGrimoireDbReadiness),

            nameof(IGrimoireLivenessProbe),
        ];

        Type[] strays = typeof(IGrimoireRepository).Assembly
            .GetTypes()
            .Where(type => type.Namespace is string ns && DomainNamespaces.Contains(ns))
            .Where(type => contractNames.Contains(type.Name))
            .ToArray();

        Assert.Empty(strays);

    }

}
