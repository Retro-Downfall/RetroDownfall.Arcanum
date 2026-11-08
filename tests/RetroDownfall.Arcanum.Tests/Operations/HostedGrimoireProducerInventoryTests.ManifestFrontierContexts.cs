using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("database", false)]
    [InlineData("database", true)]
    [InlineData("file-read", false)]
    [InlineData("file-read", true)]
    [InlineData("ordinary-route", false)]
    [InlineData("ordinary-route", true)]
    public void ManifestNullEffectWorkOnlyConsumersResolveOneExactAcquisitionContext(string shape, bool reverse)
    {
        HostedProducerSiteKind kind = shape switch
        {
            "database" => HostedProducerSiteKind.DatabaseAccess,
            "file-read" => HostedProducerSiteKind.FileSystemRead,
            "ordinary-route" => HostedProducerSiteKind.OrdinaryConnectionRoute,
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        HostedProducerSite[] acquisitions = ManifestAcquisitionEffectContexts();

        HostedProducerSite consumer = ManifestWorkOnlyConsumer(acquisitions[2], kind);

        HostedProducerSite[] sites = [.. acquisitions, consumer];

        HostedProducerCapsuleManifest manifest = ParseFrontierContextManifest(sites, reverse);

        Assert.Empty(manifest.Diagnostics);

        Assert.Equal(5, manifest.Sites.Count);

        Assert.Equal(HostedGrimoireProducerInventory.RenderCapsuleManifest(sites),
            HostedGrimoireProducerInventory.RenderCapsuleManifest(manifest.Sites));

        HostedProducerSite[] contexts = [.. manifest.Sites.Where(site => site.CapsuleId == acquisitions[2].CapsuleId)];

        Assert.Equal(2, contexts.Length);

        Assert.Equal(2, contexts.Select(HostedGrimoireProducerInventory.StableSiteIdentity).Distinct(StringComparer.Ordinal).Count());

        Assert.Single(contexts, static site => site.EffectFrontierCapsuleId is null);

        Assert.Contains(contexts, site => site.EffectFrontierCapsuleId == acquisitions[1].CapsuleId);

        HostedProducerSite parsedConsumer = Assert.Single(manifest.Sites, site => site.CapsuleId == consumer.CapsuleId);

        Assert.Equal(acquisitions[2].CapsuleId, parsedConsumer.WorkFrontierCapsuleId);

        Assert.Null(parsedConsumer.EffectFrontierCapsuleId);
    }

    [Theory]
    [InlineData("different-work-kind")]
    [InlineData("different-work")]
    [InlineData("missing-work")]
    [InlineData("different-member")]
    [InlineData("different-type")]
    [InlineData("duplicate-acquisition")]
    [InlineData("duplicate-consumer")]
    [InlineData("unresolved-effect")]
    [InlineData("missing-null-context")]
    [InlineData("nonnull-consumer")]
    [InlineData("matching-nonnull-consumer")]
    [InlineData("physical-effect")]
    [InlineData("provider-effect")]
    [InlineData("group")]
    [InlineData("handoff")]
    public void ManifestNullEffectWorkOnlyConsumersPreserveStrictFrontierBoundaries(string shape)
    {
        HostedProducerSite[] acquisitions = ManifestAcquisitionEffectContexts();

        HostedProducerSite consumer = ManifestWorkOnlyConsumer(acquisitions[2], HostedProducerSiteKind.DatabaseAccess);

        List<HostedProducerSite> sites = [.. acquisitions];

        HostedProducerSite anotherGroup = acquisitions[1] with { SyntaxFingerprint = new string('E', 64) };

        anotherGroup = anotherGroup with { EffectFrontierCapsuleId = anotherGroup.CapsuleId };

        switch (shape)
        {
            case "different-work-kind": sites[3] = sites[3] with { AdmittedWorkKind = GrimoireWorkKind.SagaExtraction }; break;
            case "different-work": sites[3] = sites[3] with { WorkFrontierCapsuleId = sites[0].CapsuleId }; break;
            case "missing-work": sites[3] = sites[3] with { WorkFrontierCapsuleId = null }; break;
            case "different-member": sites[3] = sites[3] with { Member = "AnotherMember" }; break;
            case "different-type": sites[3] = sites[3] with { EnclosingType = "AnotherType" }; break;
            case "duplicate-acquisition": sites.Add(sites[2]); break;
            case "duplicate-consumer": sites.Add(consumer); break;
            case "unresolved-effect": sites[3] = sites[3] with { EffectFrontierCapsuleId = new string('F', 64) }; break;
            case "missing-null-context":
                sites.Add(anotherGroup);

                sites[2] = sites[2] with { EffectFrontierCapsuleId = anotherGroup.CapsuleId };

                break;
            case "nonnull-consumer":
                sites.Add(anotherGroup);

                consumer = consumer with { EffectFrontierCapsuleId = anotherGroup.CapsuleId };

                break;
            case "matching-nonnull-consumer": consumer = consumer with { EffectFrontierCapsuleId = acquisitions[1].CapsuleId }; break;
            case "physical-effect": consumer = consumer with { Kind = HostedProducerSiteKind.FileSystemEffect, Callee = "System.IO.File.Delete" }; break;
            case "provider-effect": consumer = consumer with { Kind = HostedProducerSiteKind.ProviderCall, Callee = "Microsoft.Extensions.AI.IChatClient.GetResponseAsync" }; break;
            case "group":
                consumer = consumer with { Kind = HostedProducerSiteKind.EffectFrontier, Callee = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup" };

                consumer = consumer with { EffectFrontierCapsuleId = consumer.CapsuleId };

                break;
            case "handoff": consumer = consumer with { Kind = HostedProducerSiteKind.LeaseHandoff, Callee = "Fixture.Worker.Handoff" }; break;
            default: throw new ArgumentOutOfRangeException(nameof(shape));
        }

        sites.Add(consumer);

        HostedProducerCapsuleManifest manifest = ParseFrontierContextManifest(sites);

        string refusedIdentity = HostedGrimoireProducerInventory.StableSiteIdentity(
            shape == "unresolved-effect" ? sites[3] : consumer);

        string expectedCode = shape == "duplicate-consumer"
            ? "HOSTED_MANIFEST_ROW_DUPLICATE"
            : "HOSTED_MANIFEST_FRONTIER_UNRESOLVED";

        Assert.Contains(manifest.Diagnostics, diagnostic => diagnostic.Code == expectedCode
            && diagnostic.Identity == refusedIdentity);

        if (shape == "duplicate-acquisition")
        {
            Assert.Contains(manifest.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_MANIFEST_ROW_DUPLICATE");
        }
    }

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
            // Only a null-Effect Work-only consumer may select a contextual acquisition.
            EffectFrontierCapsuleId = sites[1].CapsuleId,
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

    private static HostedProducerSite ManifestWorkOnlyConsumer(HostedProducerSite acquisition, HostedProducerSiteKind kind) =>
        acquisition with
        {
            Kind = kind,
            Callee = kind switch
            {
                HostedProducerSiteKind.FileSystemRead => "System.IO.File.ReadAllText",
                HostedProducerSiteKind.OrdinaryConnectionRoute => "RetroDownfall.Arcanum.Infrastructure.Data.GrimoireOrdinaryConnectionFactory.OpenFreshAsync",
                _ => "Microsoft.Data.Sqlite.SqliteConnection.Open",
            },
            Member = "Read",
            MemberIdentity = "M:Fixture.Storage.Read",
            SyntaxFingerprint = new string('D', 64),
            AdmittedWorkKind = null,
            EffectFrontierCapsuleId = null,
        };

    private static HostedProducerCapsuleManifest ParseFrontierContextManifest(IEnumerable<HostedProducerSite> sites, bool reverse = false)
    {
        string[] lines = HostedGrimoireProducerInventory.RenderCapsuleManifest(sites).Split('\n');

        return HostedGrimoireProducerInventory.ParseCapsuleManifest(reverse
            ? [.. lines.Take(2), .. lines.Skip(2).Where(static line => line.Length != 0).Reverse()]
            : lines);
    }
}
