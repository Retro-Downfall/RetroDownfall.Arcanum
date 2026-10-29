using System.Reflection;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void Production_analysis_observations_share_discovery_and_only_selection_contracts_reenter()
    {
        Type inventory = typeof(HostedGrimoireProducerInventory);

        MethodInfo[] tests = [.. typeof(HostedGrimoireProducerInventoryTests).Assembly.GetTypes()
            .SelectMany(static type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(static method => method.GetCustomAttributesData().Any(static trait =>
                trait.AttributeType == typeof(TraitAttribute) && trait.ConstructorArguments.Count == 2
                && trait.ConstructorArguments[0].Value as string == "Category"
                && trait.ConstructorArguments[1].Value as string == "HostedProducerProductionAnalysis"))];

        Assert.NotEmpty(tests);

        string[] owners = [.. tests.Where(method => ReachesColdDiscovery(method,
                target => target.DeclaringType == inventory && target.Name == nameof(HostedGrimoireProducerInventory.DiscoverProductionSites),
                target => target.DeclaringType == inventory && target.Name is "get_ProductionSiteDiscovery" or "get_AdditionalProductionSiteDiscovery"))
            .Select(static method => method.Name).Order(StringComparer.Ordinal)];

        Assert.Equal([nameof(The_production_entry_runs_only_the_named_roots_under_the_production_premise)], owners);
    }

    [Theory]
    [InlineData(nameof(DiscoveryOwnershipDirect), true)]
    [InlineData(nameof(DiscoveryOwnershipWrapped), true)]
    [InlineData(nameof(DiscoveryOwnershipDelegate), true)]
    [InlineData(nameof(DiscoveryOwnershipShared), false)]
    [InlineData(nameof(DiscoveryOwnershipSharedWrapped), false)]
    public void Discovery_ownership_follows_compiled_calls_and_method_groups_until_shared_boundary(string member, bool repeats)
    {
        MethodInfo method = typeof(HostedGrimoireProducerInventoryTests).GetMethod(member, BindingFlags.Static | BindingFlags.NonPublic)!;

        Assert.Equal(repeats, ReachesColdDiscovery(method,
            static target => target.Name == nameof(DiscoveryOwnershipCold),
            static target => target.Name == nameof(DiscoveryOwnershipSharedBoundary)));
    }

    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void Production_root_observations_preserve_shared_snapshot_and_compiler_model()
    {
        var compilations = HostedGrimoireProducerInventory.ProductionCompilations;

        var full = HostedGrimoireProducerInventory.ProductionSiteDiscovery;

        string before = HostedGrimoireProducerInventory.RenderCapsuleManifest(full.Items);

        var observed = ObserveFullProductionRoot(full, "Loremaster");

        observed[0] = observed[0] with { RootType = "ObservationCopyOnly" };

        Assert.Same(full, HostedGrimoireProducerInventory.ProductionSiteDiscovery);

        Assert.Same(compilations, HostedGrimoireProducerInventory.ProductionCompilations);

        Assert.Equal(before, HostedGrimoireProducerInventory.RenderCapsuleManifest(full.Items));

        Assert.DoesNotContain(full.Items, static site => site.RootType == "ObservationCopyOnly");
    }

    [Fact]
    public void Full_graph_observation_keeps_exact_root_and_authority_without_hiding_diagnostics()
    {
        HostedProducerSite expected = new("Root", "Call", "source.cs", "Owner", "Run", HostedProducerSiteKind.DatabaseAccess, "Execute", "Authority");

        HostedProducerSite otherRoot = expected with { RootType = "Other" };

        HostedProducerSite otherAuthority = expected with { AuthorityOperationId = "Other" };

        HostedProducerInventoryDiagnostic refusal = new("REFUSAL", "Other", "Must remain visible globally");

        HostedProducerDiscovery<HostedProducerSite> full = new([expected, otherRoot, otherAuthority], [refusal]);

        HostedProducerSite[] observed = ObserveFullProductionRoot(full, "Root", "Authority");

        Assert.Equal(expected, Assert.Single(observed));

        Assert.Equal(2, ObserveFullProductionRoot(full, "Root").Length);

        observed[0] = otherRoot;

        Assert.Equal(expected, full.Items[0]);

        Assert.Equal(refusal, Assert.Single(full.Diagnostics));
    }

    [Theory]
    [InlineData("Missing", "Authority")]
    [InlineData("Root", "Missing")]
    public void Full_graph_observation_refuses_an_unreached_root_or_authority(string root, string authority)
    {
        HostedProducerSite site = new("Root", "Call", "source.cs", "Owner", "Run", HostedProducerSiteKind.DatabaseAccess, "Execute", "Authority");

        Assert.Throws<Xunit.Sdk.NotEmptyException>(() => ObserveFullProductionRoot(new([site], []), root, authority));
    }

    // This is a view of sites already discovered in the full composition, never a narrowed analysis.
    // Diagnostics and traversal metrics stay on the full result. A fresh array prevents an observation
    // from modifying the process-local shared snapshot; immutable site records preserve provenance.
    private static HostedProducerSite[] ObserveFullProductionRoot(
        HostedProducerDiscovery<HostedProducerSite> full, string rootType, string? authorityOperationId = null)
    {
        HostedProducerSite[] observed = [.. full.Items.Where(site => site.RootType == rootType
            && (authorityOperationId is null || site.AuthorityOperationId == authorityOperationId))];

        Assert.NotEmpty(observed);

        return observed;
    }

    private static bool ReachesColdDiscovery(MethodBase entry, Func<MethodBase, bool> cold, Func<MethodBase, bool> shared)
    {
        HashSet<MethodBase> seen = [];

        Stack<MethodBase> pending = new([entry]);

        while (pending.TryPop(out MethodBase? method))
        {
            if (shared(method) || !seen.Add(method)) continue;

            if (cold(method)) return true;

            if (method.Module.Assembly != entry.Module.Assembly) continue;

            foreach (MethodBase called in ClrMethodBodyReader.CalledMethods(method)) pending.Push(called);
        }

        return false;
    }

    private static void DiscoveryOwnershipCold() => throw new InvalidOperationException("The census must inspect calls without executing discovery.");

    private static void DiscoveryOwnershipDirect() => DiscoveryOwnershipCold();

    private static void DiscoveryOwnershipWrapped() => DiscoveryOwnershipDirect();

    private static void DiscoveryOwnershipDelegate() => ((Action)DiscoveryOwnershipDirect)();

    private static void DiscoveryOwnershipSharedBoundary() => DiscoveryOwnershipCold();

    private static void DiscoveryOwnershipShared() => DiscoveryOwnershipSharedBoundary();

    private static void DiscoveryOwnershipSharedWrapped() => DiscoveryOwnershipShared();
}
