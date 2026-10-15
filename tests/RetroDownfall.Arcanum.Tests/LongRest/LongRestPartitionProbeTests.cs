using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.LongRest;

/// <summary>
/// What the protected partition probe costs and what it still refuses. The probe runs once per protected partition, and once more to learn
/// there are no more, on every eligible Session turn, so it builds its projection of the corpus once per call, and not at all for an
/// installation with nothing protected in it - while every case that makes the projection refuse still refuses.
/// </summary>
[Collection("Grimoire")]
public sealed class LongRestPartitionProbeTests
{
    private static async Task<(LongRestSweepHarness H, List<CovenantDigest> Partitions)> SeedAsync(int clean, int protectedSources)
    {
        LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        List<CovenantDigest> partitions = [];

        for (int number = 1; number <= clean + protectedSources; number++)
        {
            Guid memory = new("a0000000-0000-0000-0000-" + number.ToString("D12", System.Globalization.CultureInfo.InvariantCulture));

            await h.SeedDistinctAsync(memory.ToString("D"), number);

            if (number > clean)
            {
                partitions.Add(await h.ProtectAsync(memory, GenerationProvenance.CreateExact([Guid.NewGuid()])));
            }
        }

        partitions.Sort(static (left, right) => string.CompareOrdinal(left.ToString(), right.ToString()));

        return (h, partitions);
    }

    [Fact]
    public async Task The_probe_builds_its_projection_once_per_call_whatever_the_cursor()
    {
        (LongRestSweepHarness h, List<CovenantDigest> partitions) = await SeedAsync(clean: 4, protectedSources: 3);

        await using LongRestSweepHarness harness = h;

        using LongRestVerificationTally tally = LongRestVerificationCounters.Measure();

        using SqliteStatementTrace trace = new(h.Connection);

        CovenantDigest? cursor = null;

        foreach (CovenantDigest expected in partitions)
        {
            cursor = LongRestSweepHarness.Require(await h.Store.ReadNextProtectedPartitionAsync(CanonicalCampaignContext.GlobalOnly, cursor, CancellationToken.None));

            Assert.Equal(expected, cursor);
        }

        Assert.Null(LongRestSweepHarness.Require(await h.Store.ReadNextProtectedPartitionAsync(CanonicalCampaignContext.GlobalOnly, cursor, CancellationToken.None)));

        // Three partitions and the call that learns there are no more: four calls, one pass each.
        Assert.Equal(4, tally.PartitionPasses);

        Assert.Equal(4, trace.Statements.Count(static statement => statement.Contains("WITH eligible", StringComparison.Ordinal)));

        Assert.Equal(0, tally.FullLeafReads);

        Assert.Equal(0, tally.MetadataLeafReads);
    }

    [Fact]
    public async Task The_probe_does_not_build_its_projection_for_an_installation_with_nothing_protected()
    {
        (LongRestSweepHarness h, _) = await SeedAsync(clean: 6, protectedSources: 0);

        await using LongRestSweepHarness harness = h;

        using LongRestVerificationTally tally = LongRestVerificationCounters.Measure();

        using SqliteStatementTrace trace = new(h.Connection);

        Assert.Null(LongRestSweepHarness.Require(await h.Store.ReadNextProtectedPartitionAsync(CanonicalCampaignContext.GlobalOnly, null, CancellationToken.None)));

        Assert.Equal(0, tally.PartitionPasses);

        Assert.DoesNotContain(trace.Statements, static statement => statement.Contains("WITH eligible", StringComparison.Ordinal));

        // One deferred transaction and the one early-out statement.
        Assert.Equal(1, trace.Statements.Count(static statement => statement.Contains("FROM annal_versions", StringComparison.Ordinal)));
    }

    /// <summary>
    /// The early-out must be a superset test. A source whose version says it is sensitive and which has no label is a hole in the ledger, and
    /// the probe refuses it; short-circuiting on "no label exists" alone would have walked straight past it.
    /// </summary>
    [Fact]
    public async Task A_sensitive_version_without_a_label_still_refuses_where_no_label_exists()
    {
        (LongRestSweepHarness h, _) = await SeedAsync(clean: 3, protectedSources: 0);

        await using LongRestSweepHarness harness = h;

        Assert.Equal(0L, await h.ScalarAsync("SELECT count(*) FROM artifact_sensitivity;"));

        await h.ExecuteAsync("DROP TRIGGER annal_versions_guard_update;");

        await h.ExecuteAsync("UPDATE annal_versions SET SensitivityCode = 1 WHERE VersionId = (SELECT min(VersionId) FROM annal_versions);");

        using LongRestVerificationTally tally = LongRestVerificationCounters.Measure();

        Assert.True((await h.Store.ReadNextProtectedPartitionAsync(CanonicalCampaignContext.GlobalOnly, null, CancellationToken.None)).IsFailure);

        Assert.Equal(1, tally.PartitionPasses);
    }

    /// <summary>
    /// The label is found by the normalized identity, so a label stored in another spelling is still the label of the source it names - and
    /// is still refused as a label, because only the canonical spelling is a valid one.
    /// </summary>
    [Theory]
    [InlineData("lower-dashed")]
    [InlineData("upper-dashless")]
    [InlineData("lower-dashless")]
    public async Task A_mis_spelled_label_still_counts_as_the_source_s_label_and_is_still_refused(string spelling)
    {
        (LongRestSweepHarness h, _) = await SeedAsync(clean: 2, protectedSources: 1);

        await using LongRestSweepHarness harness = h;

        await h.ExecuteAsync("DROP TRIGGER artifact_sensitivity_guard_update;");

        string spelled = spelling switch
        {
            "lower-dashed" => "lower(ArtifactId)",
            "upper-dashless" => "replace(ArtifactId, '-', '')",
            _ => "lower(replace(ArtifactId, '-', ''))",
        };

        await h.ExecuteAsync($"UPDATE artifact_sensitivity SET ArtifactId = {spelled};");

        Assert.NotEqual("a0000000-0000-0000-0000-000000000003".ToUpperInvariant(), await h.ScalarAsync("SELECT ArtifactId FROM artifact_sensitivity;"));

        long changes = (long)(await h.ScalarAsync("SELECT total_changes();"))!;

        Assert.True((await h.Store.ReadNextProtectedPartitionAsync(CanonicalCampaignContext.GlobalOnly, null, CancellationToken.None)).IsFailure);

        Assert.Equal(changes, await h.ScalarAsync("SELECT total_changes();"));

        // The clean corpus does not take the source either: a mis-spelled protected label is still protection.
        LongRestDiscoveryGeneration captured = await h.CaptureAsync();

        Assert.Equal(2L, await h.ScalarAsync($"SELECT count(*) FROM long_rest_discovery_leaves WHERE GenerationId = '{captured.GenerationId.ToString("D").ToUpperInvariant()}';"));
    }

    /// <summary>
    /// A label written in any spelling bumps the epoch of the tracked source it names, because the trigger finds the source by the same
    /// normalized identity. A tracked source whose epoch did not move would let a generation that captured it go on being proved.
    /// </summary>
    [Theory]
    [InlineData("upper-dashless")]
    [InlineData("lower-dashed")]
    [InlineData("lower-dashless")]
    public async Task A_label_written_in_any_spelling_still_bumps_the_epoch_of_the_source_it_names(string spelling)
    {
        (LongRestSweepHarness h, _) = await SeedAsync(clean: 1, protectedSources: 1);

        await using LongRestSweepHarness harness = h;

        const string clean = "a0000000-0000-0000-0000-000000000001";

        long before = (long)(await h.ScalarAsync($"SELECT MutationEpoch FROM long_rest_sources WHERE MemoryId = '{clean}';"))!;

        string spelled = spelling switch
        {
            "upper-dashless" => "upper(replace($memory, '-', ''))",
            "lower-dashed" => "lower($memory)",
            _ => "lower(replace($memory, '-', ''))",
        };

        await h.ExecuteAsync($"""
            INSERT INTO artifact_sensitivity (LabelId, ArtifactKindCode, ArtifactId, SensitivityCode, ProvenanceModeCode, ExactGenerationIds, GenerationBloom,
                SessionId, CampaignId, TurnId, ArtifactRevision, ArtifactContentDigest, SensitivityDigest, ProducingPlanDigest, ProducingAdmissionDigest,
                ProducingMaintenanceReceiptDigest, ArtifactLabelDigest, CreatedAtUtc)
            SELECT $label, ArtifactKindCode, {spelled}, SensitivityCode, ProvenanceModeCode, ExactGenerationIds, GenerationBloom,
                SessionId, CampaignId, TurnId, ArtifactRevision, ArtifactContentDigest, SensitivityDigest, ProducingPlanDigest, ProducingAdmissionDigest,
                ProducingMaintenanceReceiptDigest, ArtifactLabelDigest, CreatedAtUtc
            FROM artifact_sensitivity LIMIT 1;
            """, ("$label", Guid.NewGuid().ToString("D").ToUpperInvariant()), ("$memory", clean));

        Assert.Equal(before + 1, await h.ScalarAsync($"SELECT MutationEpoch FROM long_rest_sources WHERE MemoryId = '{clean}';"));
    }
}
