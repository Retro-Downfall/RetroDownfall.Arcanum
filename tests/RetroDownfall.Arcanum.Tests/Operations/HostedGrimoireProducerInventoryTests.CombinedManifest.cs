using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    private const string ReviewedRecoveryType = "RetroDownfall.Arcanum.Infrastructure.Guild.GuildQuestRecoveryHandler";

    private const string ReviewedRecoveryOperation = ReviewedRecoveryType + ".RecoverAsync";

    // This combines two already-completed production observations; it never preserves persisted
    // rows or runs another discovery. Both complete diagnostic sets remain authority for update.
    private static HostedProducerDiscovery<HostedProducerSite> ReviewedCombinedDiscovery(
        HostedProducerDiscovery<HostedProducerSite> primary, HostedProducerDiscovery<HostedProducerSite> additional)
    {
        HostedProducerSite[] recovery = [.. additional.Items.Where(site => site.RootType == ReviewedRecoveryType
            && site.AuthorityOperationId == ReviewedRecoveryOperation)];

        List<HostedProducerInventoryDiagnostic> diagnostics = [.. primary.Diagnostics, .. additional.Diagnostics];

        if (recovery.Length == 0)
            diagnostics.Add(new("HOSTED_REVIEWED_ADDITIONAL_ROOT_MISSING", ReviewedRecoveryOperation,
                "The reviewed additional recovery operation produced no sites."));

        return new([.. primary.Items, .. recovery], diagnostics, primary.AnalysisMetrics);
    }

    [Fact]
    public void Reviewed_combined_manifest_uses_primary_and_exact_additional_recovery_only()
    {
        HostedProducerSite primarySite = new("Primary", "Run", "p.cs", "P", "Run", HostedProducerSiteKind.DatabaseAccess, "read", "Primary.Run");

        HostedProducerSite recovery = new(ReviewedRecoveryType, "Recover", "r.cs", "R", "Recover", HostedProducerSiteKind.DatabaseAccess, "read", ReviewedRecoveryOperation);

        HostedProducerSite other = recovery with { AuthorityOperationId = ReviewedRecoveryOperation + "Other" };

        HostedProducerSite wrongRoot = recovery with { RootType = "Other" };

        var combined = ReviewedCombinedDiscovery(new([primarySite], []), new([recovery, other, wrongRoot], []));

        Assert.Equal(new[] { primarySite, recovery }, combined.Items);

        Assert.Empty(combined.Diagnostics);

        string expected = HostedGrimoireProducerInventory.RenderCapsuleManifest(combined.Items);

        string stale = HostedGrimoireProducerInventory.RenderCapsuleManifest([primarySite, recovery, other]);

        var comparison = HostedGrimoireProducerInventory.CompareCapsuleManifests(expected, stale);

        Assert.False(comparison.ExactTextMatch);

        Assert.Single(comparison.RemovedRows);
    }

    [Fact]
    public void Reviewed_combined_manifest_missing_recovery_fails_closed()
    {
        var combined = ReviewedCombinedDiscovery(new([], []), new([], []));

        Assert.Contains(combined.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_REVIEWED_ADDITIONAL_ROOT_MISSING");
    }

    [Fact]
    public void Reviewed_combined_manifest_refuses_update_when_primary_discovery_fails() =>
        AssertCombinedManifestRefusesUpdate(additionalFailure: false);

    [Fact]
    public void Reviewed_combined_manifest_refuses_update_when_additional_discovery_fails() =>
        AssertCombinedManifestRefusesUpdate(additionalFailure: true);

    private static void AssertCombinedManifestRefusesUpdate(bool additionalFailure)
    {
        HostedProducerSite recovery = new(ReviewedRecoveryType, "Recover", "r.cs", "R", "Recover", HostedProducerSiteKind.DatabaseAccess, "read", ReviewedRecoveryOperation);

        HostedProducerInventoryDiagnostic refusal = new("DISCOVERY_ERROR", "identity", "detail");

        var primary = new HostedProducerDiscovery<HostedProducerSite>([], additionalFailure ? [] : [refusal]);

        var additional = new HostedProducerDiscovery<HostedProducerSite>([recovery], additionalFailure ? [refusal] : []);

        var combined = ReviewedCombinedDiscovery(primary, additional);

        DirectoryInfo root = Directory.CreateTempSubdirectory("arcanum-combined-manifest-");

        try
        {
            string path = Path.Combine(root.FullName, "reviewed.tsv");

            const string retained = "retained old bytes\n";

            File.WriteAllText(path, retained);

            bool reported = false;

            var update = HostedGrimoireProducerInventory.ReadOrUpdateCapsuleManifest(path,
                HostedGrimoireProducerInventory.RenderCapsuleManifest(combined.Items), combined.Diagnostics, true, _ => reported = true);

            Assert.Contains(refusal, combined.Diagnostics);

            Assert.False(reported);

            Assert.Equal(retained, update.Reviewed);

            Assert.Equal(retained, File.ReadAllText(path));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
