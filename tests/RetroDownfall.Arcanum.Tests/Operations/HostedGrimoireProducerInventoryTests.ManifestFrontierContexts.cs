using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Fact]
    public void ManifestWorkAcquisitionSelfReferencesRetainBothInheritedEffectContexts()
    {
        HostedProducerSite[] sites = ManifestAcquisitionEffectContexts();

        HostedProducerCapsuleManifest manifest = ParseFrontierContextManifest(sites);

        Assert.Empty(manifest.Diagnostics);

        Assert.Equal(4, manifest.Sites.Count);

        HostedProducerSite[] acquisitions = [.. manifest.Sites.Where(static site => site.Member == "WithInstallationAsync")];

        Assert.Equal(2, acquisitions.Length);

        Assert.Equal(acquisitions[0].CapsuleId, acquisitions[1].CapsuleId);

        Assert.All(acquisitions, static site => Assert.Equal(site.CapsuleId, site.WorkFrontierCapsuleId));

        Assert.Contains(acquisitions, static site => site.EffectFrontierCapsuleId is null);

        Assert.Contains(acquisitions, site => site.EffectFrontierCapsuleId == sites[1].CapsuleId);

        Assert.Equal(2, acquisitions.Select(HostedGrimoireProducerInventory.StableSiteIdentity).Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("different-work-kind")]
    [InlineData("different-work")]
    [InlineData("missing-work")]
    [InlineData("different-member")]
    [InlineData("different-type")]
    [InlineData("unresolved-effect")]
    public void ManifestWorkAcquisitionContextsCannotConcealConflictingAdmissionFacts(string shape)
    {
        HostedProducerSite[] sites = ManifestAcquisitionEffectContexts();

        HostedProducerSite contextual = sites[3];

        sites[3] = shape switch
        {
            "different-work-kind" => contextual with { AdmittedWorkKind = GrimoireWorkKind.SagaExtraction },
            "different-work" => contextual with { WorkFrontierCapsuleId = sites[0].CapsuleId },
            "missing-work" => contextual with { WorkFrontierCapsuleId = null },
            "different-member" => contextual with { Member = "AnotherMember" },
            "different-type" => contextual with { EnclosingType = "AnotherType" },
            "unresolved-effect" => contextual with { EffectFrontierCapsuleId = new string('F', 64) },
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        Assert.Contains(ParseFrontierContextManifest(sites).Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_MANIFEST_FRONTIER_UNRESOLVED");
    }

    [Theory]
    [InlineData("root")]
    [InlineData("operation")]
    [InlineData("source")]
    [InlineData("member-identity")]
    [InlineData("site-kind")]
    [InlineData("callee")]
    [InlineData("ordinal")]
    [InlineData("fingerprint")]
    public void ManifestFrontierReferencesRequireTheirExactRootOperationAndPhysicalCapsule(string shape)
    {
        HostedProducerSite frontier = ManifestAcquisitionEffectContexts()[2];

        HostedProducerSite reference = frontier with
        {
            Kind = HostedProducerSiteKind.DatabaseAccess,
            Callee = "Microsoft.Data.Sqlite.SqliteConnection.Open",
            Member = "Read",
            MemberIdentity = "M:Fixture.Reader.Read",
            SyntaxFingerprint = new string('D', 64),
            AdmittedWorkKind = null,
        };

        frontier = shape switch
        {
            "root" => frontier with { RootType = "AnotherRoot" },
            "operation" => frontier with { AuthorityOperationId = "Fixture.Worker.AnotherOperation" },
            "source" => frontier with { SourcePath = "src/AnotherSource.cs" },
            "member-identity" => frontier with { MemberIdentity = "M:Fixture.Storage.AnotherMember" },
            "site-kind" => frontier with { Kind = HostedProducerSiteKind.DatabaseAccess },
            "callee" => frontier with { Callee = "Fixture.Gate.TryBeginExternalEffectGroup" },
            "ordinal" => frontier with { SyntaxOrdinal = 1 },
            "fingerprint" => frontier with { SyntaxFingerprint = new string('E', 64) },
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        HostedProducerCapsuleManifest manifest = ParseFrontierContextManifest([frontier, reference]);

        Assert.Contains(manifest.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_MANIFEST_FRONTIER_UNRESOLVED"
            && diagnostic.Identity == HostedGrimoireProducerInventory.StableSiteIdentity(reference));
    }

    [Fact]
    public void ManifestContextualAcquisitionsDoNotRelaxNonselfWorkOrEffectReferencesOrDuplicateRows()
    {
        HostedProducerSite[] sites = ManifestAcquisitionEffectContexts();

        HostedProducerSite reference = sites[2] with
        {
            Kind = HostedProducerSiteKind.DatabaseAccess,
            Callee = "Microsoft.Data.Sqlite.SqliteConnection.Open",
            MemberIdentity = "M:Fixture.Storage.Read",
            AdmittedWorkKind = null,
        };

        Assert.Contains(ParseFrontierContextManifest([.. sites, reference]).Diagnostics,
            diagnostic => diagnostic.Code == "HOSTED_MANIFEST_FRONTIER_UNRESOLVED"
                && diagnostic.Identity == HostedGrimoireProducerInventory.StableSiteIdentity(reference));

        HostedProducerSite groupAlias = sites[1] with { WorkFrontierCapsuleId = null };

        Assert.Contains(ParseFrontierContextManifest([.. sites, groupAlias]).Diagnostics,
            diagnostic => diagnostic.Code == "HOSTED_MANIFEST_FRONTIER_UNRESOLVED"
                && diagnostic.Identity == HostedGrimoireProducerInventory.StableSiteIdentity(sites[3]));

        Assert.Contains(ParseFrontierContextManifest([.. sites, sites[2]]).Diagnostics,
            static diagnostic => diagnostic.Code == "HOSTED_MANIFEST_ROW_DUPLICATE");
    }

    private static HostedProducerSite[] ManifestAcquisitionEffectContexts()
    {
        const string operation = "Fixture.Worker.ExecuteAsync";

        const string acquire = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease";

        HostedProducerSite outer = new("Worker", operation, "src/Worker.cs", "Fixture.Worker", "ExecuteAsync",
            HostedProducerSiteKind.EffectFrontier, acquire, operation, "M:Fixture.Worker.ExecuteAsync", 0,
            new string('A', 64), AdmittedWorkKind: GrimoireWorkKind.SagaExtraction);

        outer = outer with { WorkFrontierCapsuleId = outer.CapsuleId };

        HostedProducerSite group = outer with
        {
            Callee = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup",
            SyntaxFingerprint = new string('B', 64),
            AdmittedWorkKind = null,
        };

        group = group with { EffectFrontierCapsuleId = group.CapsuleId };

        HostedProducerSite inner = outer with
        {
            SourcePath = "src/Storage.cs",
            EnclosingType = "Fixture.Storage",
            Member = "WithInstallationAsync",
            MemberIdentity = "M:Fixture.Storage.WithInstallationAsync",
            SyntaxFingerprint = new string('C', 64),
            AdmittedWorkKind = GrimoireWorkKind.GuildStorage,
        };

        inner = inner with { WorkFrontierCapsuleId = inner.CapsuleId };

        return [outer, group, inner, inner with { EffectFrontierCapsuleId = group.CapsuleId }];
    }

    private static HostedProducerCapsuleManifest ParseFrontierContextManifest(IEnumerable<HostedProducerSite> sites) =>
        HostedGrimoireProducerInventory.ParseCapsuleManifest(HostedGrimoireProducerInventory.RenderCapsuleManifest(sites).Split('\n'));
}
