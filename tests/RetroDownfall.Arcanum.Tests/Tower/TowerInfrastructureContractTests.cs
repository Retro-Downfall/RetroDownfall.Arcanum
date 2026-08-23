using RetroDownfall.Arcanum.Infrastructure.Tower;

namespace RetroDownfall.Arcanum.Tests.Tower;

/// <summary>
/// The prompt renderer and the Campaign root-marker components serve the authored-resource domain, so
/// they are declared in <c>RetroDownfall.Arcanum.Infrastructure.Tower</c> beside the contracts they
/// implement. Nothing in the folder they left was agent-related, so nothing in it became Conclave.
/// </summary>
/// <remarks>
/// The sweep is over the whole Infrastructure assembly rather than over the named types, because the
/// failure is silent in both directions: a type left behind still compiles while every consumer keeps
/// its old <c>using</c>, and a type nobody remembered would not appear in a list anybody wrote.
/// <see cref="PhysicalCampaignRootOpener"/> is a partial split across two files, and a partial whose
/// halves declare different namespaces is two unrelated types rather than a compile error.
/// </remarks>
public sealed class TowerInfrastructureContractTests
{

    private const string InfrastructureTowerNamespace = "RetroDownfall.Arcanum.Infrastructure.Tower";

    private const string RetiredNamespace = "RetroDownfall.Arcanum.Infrastructure.TheForge";

    public static TheoryData<Type> MovedTypes() =>
    [
        typeof(CampaignPathMarkerCodec),

        typeof(PhysicalCampaignRootOpener),

        typeof(PromptRenderer),
    ];

    [Theory]
    [MemberData(nameof(MovedTypes))]
    public void Moved_type_declares_the_infrastructure_tower_namespace(Type type)
    {

        Assert.Equal(InfrastructureTowerNamespace, type.Namespace);

    }

    [Fact]
    public void Infrastructure_declares_no_type_in_the_retired_namespace()
    {

        string[] strays = typeof(PromptRenderer).Assembly
            .GetTypes()
            .Where(static type => type.Namespace is string ns
                && (string.Equals(ns, RetiredNamespace, StringComparison.Ordinal)
                    || ns.StartsWith(RetiredNamespace + ".", StringComparison.Ordinal)))
            .Select(static type => type.FullName ?? type.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(strays);

    }

}
