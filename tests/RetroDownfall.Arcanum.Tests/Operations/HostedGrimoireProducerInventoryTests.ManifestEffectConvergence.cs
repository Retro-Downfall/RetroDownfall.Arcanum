using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Manifest_reads_under_fresh_work_round_trip_after_two_unrelated_incoming_effects(bool reverse)
    {
        HostedProducerDiscovery<HostedProducerSite> discovered = DiscoverManifestEffectConvergence();

        Assert.Empty(discovered.Diagnostics);

        HostedProducerSite read = Assert.Single(discovered.Items, static site => site.Callee == "System.IO.File.Exists");

        Assert.NotNull(read.WorkFrontierCapsuleId);

        Assert.NotNull(read.RegisteredRootWorkFrontierCapsuleId);

        Assert.NotEqual(read.WorkFrontierCapsuleId, read.RegisteredRootWorkFrontierCapsuleId);

        Assert.Null(read.EffectFrontierCapsuleId);

        HostedProducerSite[] acquisitions = [.. discovered.Items.Where(site => site.CapsuleId == read.WorkFrontierCapsuleId)];

        Assert.Equal(2, acquisitions.Length);

        Assert.All(acquisitions, static site => Assert.NotNull(site.EffectFrontierCapsuleId));

        Assert.Equal(2, acquisitions.Select(static site => site.EffectFrontierCapsuleId).Distinct().Count());

        foreach (HostedProducerSite acquisition in acquisitions)
        {
            HostedProducerSite group = Assert.Single(discovered.Items, site => site.CapsuleId == acquisition.EffectFrontierCapsuleId);

            Assert.NotEqual(read.WorkFrontierCapsuleId, group.WorkFrontierCapsuleId);

            Assert.Equal(read.RegisteredRootWorkFrontierCapsuleId, group.RegisteredRootWorkFrontierCapsuleId);
        }

        string rendered = HostedGrimoireProducerInventory.RenderCapsuleManifest(discovered.Items);

        output.WriteLine(rendered);

        HostedProducerCapsuleManifest parsed = ParseFrontierContextManifest(discovered.Items, reverse);

        Assert.Empty(parsed.Diagnostics);

        Assert.Equal(rendered, HostedGrimoireProducerInventory.RenderCapsuleManifest(parsed.Sites));

        Assert.Empty(NestedWorkKindCatalog(parsed.Sites).Diagnostics);
    }

    [Theory]
    [InlineData("missing-root")]
    [InlineData("missing-group")]
    [InlineData("duplicate-group")]
    [InlineData("group-without-parent")]
    [InlineData("group-on-consumer-work")]
    [InlineData("group-on-another-root")]
    [InlineData("group-not-self")]
    [InlineData("group-with-admitted-kind")]
    [InlineData("missing-parent")]
    [InlineData("ambiguous-parent")]
    [InlineData("conflicting-parent-other-root")]
    [InlineData("parent-without-kind")]
    [InlineData("parent-without-own-work")]
    [InlineData("duplicate-acquisition")]
    [InlineData("conflicting-acquisition-kind")]
    [InlineData("nonnull-consumer")]
    [InlineData("provider-consumer")]
    [InlineData("filesystem-effect-consumer")]
    public void Manifest_converged_reads_require_exact_unrelated_group_parents(string mutation)
    {
        List<HostedProducerSite> sites = [.. DiscoverManifestEffectConvergence().Items];

        HostedProducerSite read = Assert.Single(sites, static site => site.Callee == "System.IO.File.Exists");

        HostedProducerSite acquisition = sites.First(site => site.CapsuleId == read.WorkFrontierCapsuleId);

        HostedProducerSite group = Assert.Single(sites, site => site.CapsuleId == acquisition.EffectFrontierCapsuleId);

        if (mutation == "conflicting-parent-other-root")
            group = Assert.Single(sites, site => site.Kind == HostedProducerSiteKind.EffectFrontier
                && site.Callee.EndsWith(".TryBeginExternalEffectGroup", StringComparison.Ordinal)
                && site.WorkFrontierCapsuleId != read.RegisteredRootWorkFrontierCapsuleId);

        HostedProducerSite parent = Assert.Single(sites, site => site.CapsuleId == group.WorkFrontierCapsuleId);

        void Replace(HostedProducerSite old, HostedProducerSite changed) => sites[sites.IndexOf(old)] = changed;

        switch (mutation)
        {
            case "missing-root": sites = [.. sites.Select(static site => site with { RegisteredRootWorkFrontierCapsuleId = null })]; break;
            case "missing-group": sites.Remove(group); break;
            case "duplicate-group": sites.Add(group); break;
            case "group-without-parent": Replace(group, group with { WorkFrontierCapsuleId = null }); break;
            case "group-on-consumer-work": Replace(group, group with { WorkFrontierCapsuleId = read.WorkFrontierCapsuleId }); break;
            case "group-on-another-root": Replace(group, group with { RegisteredRootWorkFrontierCapsuleId = new string('F', 64) }); break;
            case "group-not-self": Replace(group, group with { EffectFrontierCapsuleId = null }); break;
            case "group-with-admitted-kind": Replace(group, group with { AdmittedWorkKind = GrimoireWorkKind.GuildStorage }); break;
            case "missing-parent": sites.Remove(parent); break;
            case "ambiguous-parent": sites.Add(parent with { EffectFrontierCapsuleId = group.CapsuleId }); break;
            case "conflicting-parent-other-root": sites.Add(parent with
            {
                RegisteredRootWorkFrontierCapsuleId = new string('F', 64),
                AdmittedWorkKind = GrimoireWorkKind.SagaExtraction,
            }); break;
            case "parent-without-kind": Replace(parent, parent with { AdmittedWorkKind = null }); break;
            case "parent-without-own-work": Replace(parent, parent with { WorkFrontierCapsuleId = null }); break;
            case "duplicate-acquisition": sites.Add(acquisition); break;
            case "conflicting-acquisition-kind": Replace(acquisition, acquisition with { AdmittedWorkKind = GrimoireWorkKind.SagaExtraction }); break;
            case "nonnull-consumer": Replace(read, read with { EffectFrontierCapsuleId = group.CapsuleId }); break;
            case "provider-consumer": Replace(read, read with { Kind = HostedProducerSiteKind.ProviderCall }); break;
            case "filesystem-effect-consumer": Replace(read, read with { Kind = HostedProducerSiteKind.FileSystemEffect }); break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        HostedProducerSite changedRead = Assert.Single(sites, static site => site.Callee == "System.IO.File.Exists");

        Assert.Contains(ParseFrontierContextManifest(sites).Diagnostics, diagnostic =>
            diagnostic.Code == "HOSTED_MANIFEST_FRONTIER_UNRESOLVED"
            && diagnostic.Identity == HostedGrimoireProducerInventory.StableSiteIdentity(changedRead));
    }

    [Fact]
    public void Persisted_manifest_frontiers_resolve_without_source_discovery()
    {
        HostedProducerCapsuleManifest parsed = HostedGrimoireProducerInventory.ParseCapsuleManifest(
            File.ReadAllLines(HostedGrimoireProducerInventory.CapsuleManifestPath));

        Assert.NotEmpty(parsed.Sites);

        Assert.Empty(parsed.Diagnostics);
    }

    private static HostedProducerDiscovery<HostedProducerSite> DiscoverManifestEffectConvergence()
    {
        string root = R2Admission.Replace(".WorkspaceIndexing", ".LongRunningOperationRecovery", StringComparison.Ordinal)
            + "await ReadUnderFreshWork(gate); await OtherEffect(gate);";

        string source = R2Source(root)
            .Replace("public enum GrimoireWorkKind { WorkspaceIndexing = 4 }",
                "public enum GrimoireWorkKind { WorkspaceIndexing = 4, LongRunningOperationRecovery = 14, GuildStorage = 20 }", StringComparison.Ordinal)
            .Replace("public Task StopAsync", $$"""
                private static async Task OtherEffect({{NestedKindContract}}IGrimoireConnectionAdmissionGate gate)
                {
                    if (!gate.TryAcquireWorkLease({{NestedKindContract}}GrimoireWorkKind.GuildStorage, out var acquired)) return;
                    using var own = acquired;
                    if (!own.TryBeginExternalEffectGroup(out var group)) return;
                    using var held = group;
                    await ReadUnderFreshWork(gate);
                }

                private static async Task ReadUnderFreshWork({{NestedKindContract}}IGrimoireConnectionAdmissionGate gate)
                {
                    if (!gate.TryAcquireWorkLease({{NestedKindContract}}GrimoireWorkKind.GuildStorage, out var acquired)) return;
                    using var own = acquired;
                    _ = System.IO.File.Exists("manifest-read");
                    await Task.CompletedTask;
                }

                public Task StopAsync
                """, StringComparison.Ordinal);

        CSharpCompilation compilation = Compile(source);

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        return HostedGrimoireProducerInventory.DiscoverProducerSites([compilation], new(["Worker"], []),
            [new("Worker", [OrdinaryRoot() with { WorkKind = GrimoireWorkKind.LongRunningOperationRecovery }])], []);
    }
}
