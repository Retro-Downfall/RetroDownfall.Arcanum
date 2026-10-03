using System.Collections.Immutable;
using System.Data;
using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Memory;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The one plan a Covenant entry erasure counts, deletes and proves absent by, run inside the caller's
/// transaction over rows the production kernel and services wrote.
/// </summary>
/// <remarks>
/// Raw SQL here is assertion-only, with named exceptions: the legacy receipt no head writer can produce
/// any more, the earlier-epoch curation chain no writer from version 6 on can produce, and the faults
/// the absence proof is shown to catch.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class CovenantEntryErasurePlanTests
{
    private const string Key = "erasure.subject";

    private static readonly Guid CampaignOne = CovenantOperationGateFixture.CampaignOne;

    private static readonly Guid CampaignTwo = CovenantOperationGateFixture.CampaignTwo;

    private static readonly string[] OrderedTargets =
    [
        "covenant_search_documents",
        "covenant_search_outbox",
        "covenant_mutation_receipts",
        "covenant_version_attachment_provenance",
        "covenant_heads",
        "covenant_versions",
        "covenant_review_events",
        "covenant_review_decision_receipts",
        "covenant_entries",
        "covenant_curation_heads",
        "covenant_curation_versions",
        "covenant_curation_receipts",
    ];

    private static readonly string[] CurationTables =
        ["covenant_curation_heads", "covenant_curation_versions", "covenant_curation_receipts"];

    private static CancellationToken Token => CancellationToken.None;

    [Theory]
    [InlineData(nameof(CovenantEntryErasureMode.Live))]
    [InlineData(nameof(CovenantEntryErasureMode.Staged))]
    public async Task Count_measures_exactly_what_Delete_removes(string modeName)
    {
        CovenantEntryErasureMode mode = Enum.Parse<CovenantEntryErasureMode>(modeName);

        await using CovenantServiceHarness harness = await StartAsync();

        Guid entryId = await SeedFullEntryAsync(harness);

        long decisions = await CountAsync(harness, "SELECT count(*) FROM covenant_review_decision_receipts;");

        Assert.True(decisions > 0);

        CovenantEntryErasureTally counted = null!;

        CovenantEntryErasureTally deleted = null!;

        IReadOnlyList<MemoryErasureTableCount> remaining = null!;

        await InTransactionAsync(harness, mode, async (connection, transaction) =>
        {
            CovenantEntryErasureSubject subject = await ReadSubjectAsync(connection, transaction, entryId);

            Assert.True(subject.ReclaimsKey);

            counted = await CovenantEntryErasurePlan.RunAsync(
                connection, transaction, subject, CovenantArtifactPlanMode.Count, mode, Token);

            deleted = await CovenantEntryErasurePlan.RunAsync(
                connection, transaction, subject, CovenantArtifactPlanMode.Delete, mode, Token);

            remaining = await CovenantEntryErasurePlan.ProveAbsentAsync(
                connection, transaction, subject, deleted.VersionIds, Token);
        });

        Assert.Equal([.. OrderedTargets, "covenant_key_epochs"], counted.Targets.Select(static target => target.Table));

        Assert.Equal(counted.Targets, deleted.Targets);

        Assert.All(
            counted.Targets,
            static target => Assert.True(target.Rows > 0, $"{target.Table} held none of the entry's rows."));

        // The second level of the versions' cascade: decision receipts go with their review events.
        Assert.Equal(decisions, Rows(counted, "covenant_review_decision_receipts"));

        Assert.Equal(1, Rows(counted, "covenant_search_documents"));

        Assert.Equal(2, Rows(counted, "covenant_heads"));

        Assert.Equal(3, Rows(counted, "covenant_versions"));

        Assert.Equal(3, Rows(counted, "covenant_review_events"));

        Assert.Equal(1, Rows(counted, "covenant_version_attachment_provenance"));

        Assert.Equal(3, Rows(counted, "covenant_mutation_receipts"));

        Assert.Equal(1, Rows(counted, "covenant_entries"));

        Assert.Equal(1, Rows(counted, "covenant_key_epochs"));

        Assert.Equal((2, 1), (counted.ConfirmedVersions, counted.ProposedVersions));

        Assert.Equal(3, counted.VersionIds.Count);

        Assert.True(deleted.KeyReclaimed);

        Assert.Empty(remaining);

        Assert.Equal(0, await CountAsync(harness, "SELECT count(*) FROM covenant_entries;"));

        Assert.Equal(0, await CountAsync(harness, "SELECT count(*) FROM covenant_search_documents;"));

        Assert.Equal(0, await CountAsync(harness, "SELECT count(*) FROM covenant_review_decision_receipts;"));

        await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(harness.Fixture.Connection, Token);
    }

    [Theory]
    [InlineData(nameof(CovenantEntryErasureMode.Live), 2, 1)]
    [InlineData(nameof(CovenantEntryErasureMode.Staged), 0, 0)]
    public async Task Live_mode_appends_one_absent_delta_per_erased_head_and_advances_the_search_sequence_once(
        string modeName,
        int expectedDeltas,
        int expectedAdvance)
    {
        CovenantEntryErasureMode mode = Enum.Parse<CovenantEntryErasureMode>(modeName);

        await using CovenantServiceHarness harness = await StartAsync();

        await harness.AddCampaignAsync(CampaignOne, Token);

        await harness.SetAsync(CovenantScope.Campaign, CampaignOne, Key, "Operator text.", Token);

        await ProposeAsync(harness, CampaignOne, Key, "Agent text.");

        Guid entryId = await EntryIdAsync(harness, CovenantScope.Campaign, CampaignOne, Key);

        long before = await CountAsync(harness, "SELECT CanonicalSearchSequence FROM covenant_state WHERE StateKey = 1;");

        long[] headRows = await RowIdsAsync(harness, entryId);

        Assert.Equal(2, headRows.Length);

        Assert.True(await CountAsync(harness, "SELECT count(*) FROM covenant_search_outbox WHERE DesiredVersionId IS NOT NULL;") > 0);

        CovenantEntryErasureTally deleted = null!;

        await InTransactionAsync(harness, mode, async (connection, transaction) =>
        {
            CovenantEntryErasureSubject subject = await ReadSubjectAsync(connection, transaction, entryId);

            deleted = await CovenantEntryErasurePlan.RunAsync(
                connection, transaction, subject, CovenantArtifactPlanMode.Delete, mode, Token);
        });

        Assert.Equal(
            expectedDeltas,
            await CountAsync(
                harness,
                $"SELECT count(*) FROM covenant_search_outbox WHERE DesiredVersionId IS NULL AND SearchSequence = {before + 1} AND SearchRowId IN ({string.Join(',', headRows)});"));

        Assert.Equal(expectedDeltas, await CountAsync(harness, "SELECT count(*) FROM covenant_search_outbox;"));

        Assert.Equal(
            before + expectedAdvance,
            await CountAsync(harness, "SELECT CanonicalSearchSequence FROM covenant_state WHERE StateKey = 1;"));

        foreach (Guid version in deleted.VersionIds)
        {
            Assert.Equal(
                0,
                await CountAsync(
                    harness,
                    $"SELECT count(*) FROM covenant_search_outbox WHERE lower(replace(DesiredVersionId, '-', '')) = '{version:N}';"));
        }

        await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(harness.Fixture.Connection, Token);
    }

    /// <summary>
    /// A receipt written before canonical version 6 names no entry, so the erase finds a NoChange one by
    /// its scope, Campaign and lane inside the entry's lifetime, and counts it with the rest.
    /// </summary>
    /// <remarks>
    /// The one raw seed in this suite besides the fault below: no head writer from version 6 on can
    /// write a receipt without its entry, so the shape is reachable only on a catalog that predates it.
    /// </remarks>
    [Fact]
    public async Task A_legacy_NoChange_receipt_in_the_entry_window_is_deleted_and_counted()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(Token);

        await InstallAsync(connection, CovenantCanonicalSchemaVersionFiveFixture.ChainSet(), 5);

        await ExecuteAsync(connection, LegacyNoChangeReceiptSql("legacy-inside", "2099-01-01T00:00:00.0000000Z"));

        await ExecuteAsync(connection, LegacyNoChangeReceiptSql("legacy-before", "2000-01-01T00:00:00.0000000Z"));

        await InstallAsync(connection, GrimoireSchemaVersionChains.Default, 6);

        await ApplyOperatorSetAsync(connection, Key, "Global text.");

        Assert.Equal(1, await ScalarAsync(connection, "SELECT count(*) FROM covenant_mutation_receipts WHERE EntryId IS NOT NULL;"));

        Guid entryId = Guid.Parse((string)(await ScalarObjectAsync(connection, "SELECT EntryId FROM covenant_entries;"))!, CultureInfo.InvariantCulture);

        CovenantEntryErasureTally counted;

        CovenantEntryErasureTally deleted;

        await using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            using CovenantSqliteAuthorizationScope authorized = CovenantSqliteConnectionInitializer.Instance.Authorize(
                connection,
                CovenantSqliteAuthorizationKind.CovenantEntryErasure);

            CovenantEntryErasureSubject subject = await ReadSubjectAsync(connection, transaction, entryId);

            counted = await CovenantEntryErasurePlan.RunAsync(
                connection, transaction, subject, CovenantArtifactPlanMode.Count, CovenantEntryErasureMode.Live, Token);

            deleted = await CovenantEntryErasurePlan.RunAsync(
                connection, transaction, subject, CovenantArtifactPlanMode.Delete, CovenantEntryErasureMode.Live, Token);

            Assert.Empty(await CovenantEntryErasurePlan.ProveAbsentAsync(connection, transaction, subject, deleted.VersionIds, Token));

            await transaction.CommitAsync(Token);
        }

        Assert.Equal(2, counted.Targets.Single(static target => target.Table == "covenant_mutation_receipts").Rows);

        Assert.Equal(counted.Targets, deleted.Targets);

        Assert.Equal(0, await ScalarAsync(connection, "SELECT count(*) FROM covenant_mutation_receipts WHERE MutationId = 'legacy-inside';"));

        Assert.Equal(1, await ScalarAsync(connection, "SELECT count(*) FROM covenant_mutation_receipts WHERE MutationId = 'legacy-before';"));

        await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(connection, Token);
    }

    /// <summary>
    /// A Campaign's mask is policy about the Global key, not content of the erased entry, so it outlives
    /// the erase whenever the key itself stays. It is kept at the key's binding epoch, which for a key
    /// row created from version 6 on is 0 while its dependency epoch has moved.
    /// </summary>
    [Fact]
    public async Task A_masked_campaign_confirmed_subject_is_retained_when_the_key_is_not_reclaimed()
    {
        await using CovenantServiceHarness harness = await StartAsync();

        await harness.AddCampaignAsync(CampaignOne, Token);

        await harness.SetAsync(CovenantScope.Global, null, Key, "Global text.", Token);

        await harness.SetAsync(CovenantScope.Campaign, CampaignOne, Key, "Campaign text.", Token);

        await ProposeAsync(harness, CampaignOne, Key, "Agent text.");

        await CurateAsync(harness, CovenantCurationKind.Mask, CovenantScope.Campaign, CampaignOne, CovenantLane.Confirmed);

        await CurateAsync(harness, CovenantCurationKind.Pin, CovenantScope.Campaign, CampaignOne, CovenantLane.Proposed);

        Assert.Equal(0, await CountAsync(harness, $"SELECT IncarnationEpoch FROM covenant_key_epochs WHERE NormalizedKey = '{Key}';"));

        Assert.True(await CountAsync(harness, $"SELECT KeyEpoch FROM covenant_key_epochs WHERE NormalizedKey = '{Key}';") > 0);

        Guid entryId = await EntryIdAsync(harness, CovenantScope.Campaign, CampaignOne, Key);

        CovenantEntryErasureTally deleted = null!;

        await InTransactionAsync(harness, CovenantEntryErasureMode.Live, async (connection, transaction) =>
        {
            CovenantEntryErasureSubject subject = await ReadSubjectAsync(connection, transaction, entryId);

            Assert.True(subject.IsMasked);

            Assert.False(subject.ReclaimsKey);

            deleted = await CovenantEntryErasurePlan.RunAsync(
                connection, transaction, subject, CovenantArtifactPlanMode.Delete, CovenantEntryErasureMode.Live, Token);

            Assert.Empty(await CovenantEntryErasurePlan.ProveAbsentAsync(connection, transaction, subject, deleted.VersionIds, Token));
        });

        Assert.True(deleted.RetainsCampaignMask);

        Assert.False(deleted.KeyReclaimed);

        Assert.DoesNotContain(deleted.Targets, static target => target.Table == "covenant_key_epochs");

        Assert.Equal(
            1,
            await CountAsync(
                harness,
                $"SELECT count(*) FROM covenant_curation_heads WHERE NormalizedKey = '{Key}' AND CampaignId IS NOT NULL AND LaneCode = 1 AND IsMasked = 1 AND KeyEpoch = 0;"));

        Assert.Equal(1, await CountAsync(harness, $"SELECT count(*) FROM covenant_curation_heads WHERE NormalizedKey = '{Key}';"));

        Assert.Equal(1, await CountAsync(harness, $"SELECT count(*) FROM covenant_curation_versions WHERE NormalizedKey = '{Key}';"));

        Assert.Equal(0, await CountAsync(harness, $"SELECT count(*) FROM covenant_curation_heads WHERE NormalizedKey = '{Key}' AND IsPinned = 1;"));

        Assert.Equal(1, await CountAsync(harness, "SELECT count(*) FROM covenant_entries;"));

        await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(harness.Fixture.Connection, Token);
    }

    /// <summary>
    /// The key's only entry reclaims the key: every curation row for it in every scope goes in the same
    /// transaction as its epoch row, the reclamation epoch moves once, and a pin that outlived the purge
    /// could never bind the key that next takes the name.
    /// </summary>
    [Fact]
    public async Task Reclamation_purges_curation_in_every_scope_deletes_the_key_row_and_advances_the_epoch()
    {
        await using CovenantServiceHarness harness = await StartAsync();

        await harness.AddCampaignAsync(CampaignOne, Token);

        await harness.AddCampaignAsync(CampaignTwo, Token);

        await harness.SetAsync(CovenantScope.Campaign, CampaignOne, Key, "Campaign text.", Token);

        await CurateAsync(harness, CovenantCurationKind.Pin, CovenantScope.Campaign, CampaignOne, CovenantLane.Confirmed);

        await CurateAsync(harness, CovenantCurationKind.Pin, CovenantScope.Global, null, CovenantLane.Confirmed);

        await CurateAsync(harness, CovenantCurationKind.Mask, CovenantScope.Campaign, CampaignTwo, CovenantLane.Confirmed);

        foreach (string table in CurationTables)
        {
            Assert.Equal(3, await CountAsync(harness, $"SELECT count(*) FROM {table} WHERE NormalizedKey = '{Key}';"));
        }

        long reclamation = await CountAsync(harness, "SELECT KeyReclamationEpoch FROM covenant_state WHERE StateKey = 1;");

        Guid entryId = await EntryIdAsync(harness, CovenantScope.Campaign, CampaignOne, Key);

        CovenantEntryErasureTally deleted = null!;

        await InTransactionAsync(harness, CovenantEntryErasureMode.Live, async (connection, transaction) =>
        {
            CovenantEntryErasureSubject subject = await ReadSubjectAsync(connection, transaction, entryId);

            Assert.True(subject.ReclaimsKey);

            deleted = await CovenantEntryErasurePlan.RunAsync(
                connection, transaction, subject, CovenantArtifactPlanMode.Delete, CovenantEntryErasureMode.Live, Token);

            Assert.Empty(await CovenantEntryErasurePlan.ProveAbsentAsync(connection, transaction, subject, deleted.VersionIds, Token));
        });

        Assert.True(deleted.KeyReclaimed);

        Assert.False(deleted.RetainsCampaignMask);

        Assert.Equal(3, Rows(deleted, "covenant_curation_heads"));

        foreach (string table in CurationTables)
        {
            Assert.Equal(0, await CountAsync(harness, $"SELECT count(*) FROM {table} WHERE NormalizedKey = '{Key}';"));
        }

        Assert.Equal(0, await CountAsync(harness, $"SELECT count(*) FROM covenant_key_epochs WHERE NormalizedKey = '{Key}';"));

        Assert.Equal(reclamation + 1, await CountAsync(harness, "SELECT KeyReclamationEpoch FROM covenant_state WHERE StateKey = 1;"));

        // The key is re-created by a later operator set. Its binding epoch starts at 0 again, and no pin
        // recorded against the reclaimed incarnation is there to bind it.
        await harness.SetAsync(CovenantScope.Global, null, Key, "A new key of the same name.", Token);

        Assert.Equal(0, await CountAsync(harness, $"SELECT IncarnationEpoch FROM covenant_key_epochs WHERE NormalizedKey = '{Key}';"));

        Assert.Equal(
            0,
            await CountAsync(
                harness,
                $"SELECT count(*) FROM covenant_curation_heads c WHERE c.NormalizedKey = '{Key}' AND c.KeyEpoch = COALESCE((SELECT IncarnationEpoch FROM covenant_key_epochs WHERE NormalizedKey = c.NormalizedKey), 0);"));

        await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(harness.Fixture.Connection, Token);
    }

    [Fact]
    public async Task Search_documents_are_deleted_even_when_covenant_fts_secure_delete_is_unverifiable()
    {
        await using CovenantServiceHarness harness = await StartAsync();

        await harness.AddCampaignAsync(CampaignOne, Token);

        await harness.SetAsync(CovenantScope.Campaign, CampaignOne, Key, "Indexed text.", Token);

        _ = await CovenantSearchFixture.SynchronizeAsync(harness.Fixture, Token);

        Guid entryId = await EntryIdAsync(harness, CovenantScope.Campaign, CampaignOne, Key);

        Assert.Equal(1, await CountAsync(harness, $"SELECT count(*) FROM covenant_search_documents WHERE lower(replace(EntryId, '-', '')) = '{entryId:N}';"));

        await ExecuteAsync(harness.Fixture.Connection, "INSERT INTO covenant_fts(covenant_fts, rank) VALUES('secure-delete', 0);");

        CovenantEntryErasureTally deleted = null!;

        await InTransactionAsync(harness, CovenantEntryErasureMode.Live, async (connection, transaction) =>
        {
            CovenantEntryErasureSubject subject = await ReadSubjectAsync(connection, transaction, entryId);

            deleted = await CovenantEntryErasurePlan.RunAsync(
                connection, transaction, subject, CovenantArtifactPlanMode.Delete, CovenantEntryErasureMode.Live, Token);
        });

        Assert.False(deleted.FullTextSecureDeleteVerified);

        Assert.Equal(1, Rows(deleted, "covenant_search_documents"));

        Assert.Equal(0, await CountAsync(harness, $"SELECT count(*) FROM covenant_search_documents WHERE lower(replace(EntryId, '-', '')) = '{entryId:N}';"));

        await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(harness.Fixture.Connection, Token);
    }

    /// <summary>The absence proof counts every target again and reports exactly what it still finds.</summary>
    /// <remarks>
    /// The fault: one outbox delta re-naming an erased version, which only a raw insert can make, since
    /// the outbox carries no insert guard and no production writer names a version it never wrote.
    /// </remarks>
    [Fact]
    public async Task Absence_proof_reports_any_remaining_target()
    {
        await using CovenantServiceHarness harness = await StartAsync();

        await harness.AddCampaignAsync(CampaignOne, Token);

        await harness.SetAsync(CovenantScope.Campaign, CampaignOne, Key, "Campaign text.", Token);

        Guid entryId = await EntryIdAsync(harness, CovenantScope.Campaign, CampaignOne, Key);

        IReadOnlyList<MemoryErasureTableCount> remaining = null!;

        await InTransactionAsync(harness, CovenantEntryErasureMode.Live, async (connection, transaction) =>
        {
            CovenantEntryErasureSubject subject = await ReadSubjectAsync(connection, transaction, entryId);

            CovenantEntryErasureTally deleted = await CovenantEntryErasurePlan.RunAsync(
                connection, transaction, subject, CovenantArtifactPlanMode.Delete, CovenantEntryErasureMode.Live, Token);

            Assert.Empty(await CovenantEntryErasurePlan.ProveAbsentAsync(connection, transaction, subject, deleted.VersionIds, Token));

            await using (SqliteCommand fault = connection.CreateCommand())
            {
                fault.Transaction = transaction;

                fault.CommandText = """
                    INSERT INTO covenant_search_outbox (SearchSequence, Ordinal, SearchRowId, EntryId, LaneCode, DesiredVersionId)
                    VALUES (9000, 0, 9000, $entry, 1, $version);
                    """;

                _ = fault.Parameters.AddWithValue("$entry", entryId.ToString("D"));

                _ = fault.Parameters.AddWithValue("$version", deleted.VersionIds[0].ToString("D"));

                _ = await fault.ExecuteNonQueryAsync(Token);
            }

            remaining = await CovenantEntryErasurePlan.ProveAbsentAsync(connection, transaction, subject, deleted.VersionIds, Token);
        });

        MemoryErasureTableCount left = Assert.Single(remaining);

        Assert.Equal(new MemoryErasureTableCount("covenant_search_outbox", 1), left);

        await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(harness.Fixture.Connection, Token);
    }

    /// <summary>
    /// Two Campaign entries share a key, so erasing the first reclaims nothing; the subject read for the
    /// second after the first is gone does reclaim. A staged restore relies on reading each subject
    /// immediately before its own delete.
    /// </summary>
    [Fact]
    public async Task ReadSubject_recomputes_reclamation_per_entry()
    {
        await using CovenantServiceHarness harness = await StartAsync();

        await harness.AddCampaignAsync(CampaignOne, Token);

        await harness.AddCampaignAsync(CampaignTwo, Token);

        await harness.SetAsync(CovenantScope.Campaign, CampaignOne, Key, "First Campaign text.", Token);

        await harness.SetAsync(CovenantScope.Campaign, CampaignTwo, Key, "Second Campaign text.", Token);

        Guid first = await EntryIdAsync(harness, CovenantScope.Campaign, CampaignOne, Key);

        Guid second = await EntryIdAsync(harness, CovenantScope.Campaign, CampaignTwo, Key);

        await InTransactionAsync(harness, CovenantEntryErasureMode.Staged, async (connection, transaction) =>
        {
            CovenantEntryErasureSubject one = await ReadSubjectAsync(connection, transaction, first);

            Assert.False(one.ReclaimsKey);

            CovenantEntryErasureTally removed = await CovenantEntryErasurePlan.RunAsync(
                connection, transaction, one, CovenantArtifactPlanMode.Delete, CovenantEntryErasureMode.Staged, Token);

            Assert.False(removed.KeyReclaimed);

            CovenantEntryErasureSubject two = await ReadSubjectAsync(connection, transaction, second);

            Assert.True(two.ReclaimsKey);

            CovenantEntryErasureTally reclaimed = await CovenantEntryErasurePlan.RunAsync(
                connection, transaction, two, CovenantArtifactPlanMode.Delete, CovenantEntryErasureMode.Staged, Token);

            Assert.True(reclaimed.KeyReclaimed);

            Assert.Null(await CovenantEntryErasurePlan.ReadSubjectAsync(connection, transaction, first, Token));
        });

        Assert.Equal(0, await CountAsync(harness, $"SELECT count(*) FROM covenant_key_epochs WHERE NormalizedKey = '{Key}';"));

        await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(harness.Fixture.Connection, Token);
    }

    /// <summary>
    /// The absence proof reports exactly the targets a delete left behind. A test-only trigger silently
    /// skips the named tables' deletes, or the full-text delete trigger is dropped, and the proof must
    /// name those tables and nothing else, so every arm it has is shown to count.
    /// </summary>
    /// <remarks>
    /// A table that a kept row still references is neutered together with what references it, because
    /// skipping its delete alone fails the referencing delete's foreign key instead. Review events and
    /// decision receipts go by cascade, so they are reported by the cases that keep the versions. A kept
    /// search document keeps its full-text row, because the document's delete trigger never fires. The
    /// faults are installed inside the transaction and rolled back with it.
    /// </remarks>
    [Theory]
    [InlineData("covenant_search_documents", "covenant_search_documents,covenant_fts")]
    [InlineData("covenant_search_outbox", "covenant_search_outbox")]
    [InlineData("covenant_mutation_receipts", "covenant_mutation_receipts")]
    [InlineData(
        "covenant_version_attachment_provenance,covenant_versions,covenant_entries",
        "covenant_version_attachment_provenance,covenant_versions,covenant_review_events,covenant_review_decision_receipts,covenant_entries")]
    [InlineData(
        "covenant_heads,covenant_versions,covenant_entries",
        "covenant_heads,covenant_versions,covenant_review_events,covenant_review_decision_receipts,covenant_entries")]
    [InlineData(
        "covenant_versions,covenant_entries",
        "covenant_versions,covenant_review_events,covenant_review_decision_receipts,covenant_entries")]
    [InlineData("covenant_entries", "covenant_entries")]
    [InlineData("covenant_curation_heads,covenant_curation_versions", "covenant_curation_heads,covenant_curation_versions")]
    [InlineData("covenant_curation_versions", "covenant_curation_versions")]
    [InlineData("covenant_curation_receipts", "covenant_curation_receipts")]
    [InlineData("covenant_key_epochs", "covenant_key_epochs")]
    [InlineData("covenant_search_documents_ad", "covenant_fts")]
    public async Task Absence_proof_reports_exactly_the_targets_a_neutered_delete_left_behind(string faults, string expected)
    {
        await using CovenantServiceHarness harness = await StartAsync();

        Guid entryId = await SeedFullEntryAsync(harness);

        IReadOnlyList<MemoryErasureTableCount> remaining = null!;

        SqliteConnection connection = harness.Fixture.Connection;

        await using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            using CovenantSqliteAuthorizationScope authorized = CovenantSqliteConnectionInitializer.Instance.Authorize(
                connection,
                CovenantSqliteAuthorizationKind.CovenantEntryErasure);

            foreach (string fault in faults.Split(','))
            {
                await ExecuteAsync(
                    connection,
                    fault.EndsWith("_ad", StringComparison.Ordinal)
                        ? $"DROP TRIGGER {fault};"
                        : $"CREATE TRIGGER test_neuter_{fault} BEFORE DELETE ON {fault} BEGIN SELECT RAISE(IGNORE); END;",
                    transaction);
            }

            CovenantEntryErasureSubject subject = await ReadSubjectAsync(connection, transaction, entryId);

            Assert.True(subject.ReclaimsKey);

            CovenantEntryErasureTally deleted = await CovenantEntryErasurePlan.RunAsync(
                connection, transaction, subject, CovenantArtifactPlanMode.Delete, CovenantEntryErasureMode.Live, Token);

            remaining = await CovenantEntryErasurePlan.ProveAbsentAsync(connection, transaction, subject, deleted.VersionIds, Token);

            await transaction.RollbackAsync(Token);
        }

        Assert.All(remaining, static table => Assert.True(table.Rows > 0, $"{table.Table} was reported with no rows."));

        Assert.Equal(
            expected.Split(',').Order(StringComparer.Ordinal),
            remaining.Select(static table => table.Table).Order(StringComparer.Ordinal));

        Assert.Equal(1, await CountAsync(harness, "SELECT count(*) FROM covenant_entries;"));

        await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(harness.Fixture.Connection, Token);
    }

    /// <summary>
    /// A search document of the entry that no head names is still the entry's: its full-text row is
    /// proved gone too, not only the rows of the heads' own search ids.
    /// </summary>
    /// <remarks>
    /// Only projection corruption can leave such a document, so it is planted here. The delete trigger is
    /// replaced inside the rolled-back transaction by one that skips that document alone, so every head's
    /// own row is subtracted from the index exactly as in production and the planted document's row is the
    /// only thing left in it.
    /// </remarks>
    [Fact]
    public async Task Absence_proof_names_the_full_text_row_of_a_search_document_no_head_names()
    {
        await using CovenantServiceHarness harness = await StartAsync();

        Guid entryId = await SeedFullEntryAsync(harness);

        const long Stray = 7_000_001;

        IReadOnlyList<MemoryErasureTableCount> remaining = null!;

        SqliteConnection connection = harness.Fixture.Connection;

        await using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            using CovenantSqliteAuthorizationScope authorized = CovenantSqliteConnectionInitializer.Instance.Authorize(
                connection,
                CovenantSqliteAuthorizationKind.CovenantEntryErasure);

            // The seeded entry holds a document in one lane only. The planted one takes the other lane, so
            // the one-document-per-head index has no objection, and its insert trigger indexes it.
            await ExecuteAsync(
                connection,
                $"""
                INSERT INTO covenant_search_documents (
                    SearchRowId, EntryId, LaneCode, VersionId, ScopeCode, CampaignId, LifecycleCode,
                    NormalizedKey, AuthoredContent, CompiledContent, DatasetGeneration, CanonicalSearchSequence)
                SELECT {Stray}, EntryId, CASE LaneCode WHEN 1 THEN 2 ELSE 1 END, VersionId, ScopeCode, CampaignId,
                       LifecycleCode, NormalizedKey, AuthoredContent, CompiledContent, DatasetGeneration,
                       CanonicalSearchSequence
                FROM covenant_search_documents
                LIMIT 1;
                """,
                transaction);

            await ExecuteAsync(
                connection,
                $"""
                DROP TRIGGER covenant_search_documents_ad;
                CREATE TRIGGER covenant_search_documents_ad AFTER DELETE ON covenant_search_documents
                WHEN old.SearchRowId <> {Stray}
                BEGIN
                    INSERT INTO covenant_fts(covenant_fts, rowid, NormalizedKey, AuthoredContent, CompiledContent, EntryId, LaneCode, VersionId)
                    VALUES ('delete', old.SearchRowId, old.NormalizedKey, old.AuthoredContent, old.CompiledContent, old.EntryId, old.LaneCode, old.VersionId);
                END;
                """,
                transaction);

            CovenantEntryErasureSubject subject = await ReadSubjectAsync(connection, transaction, entryId);

            CovenantEntryErasureTally deleted = await CovenantEntryErasurePlan.RunAsync(
                connection, transaction, subject, CovenantArtifactPlanMode.Delete, CovenantEntryErasureMode.Live, Token);

            remaining = await CovenantEntryErasurePlan.ProveAbsentAsync(connection, transaction, subject, deleted.VersionIds, Token);

            await transaction.RollbackAsync(Token);
        }

        MemoryErasureTableCount left = Assert.Single(remaining);

        Assert.Equal("covenant_fts", left.Table);

        Assert.Equal(1, left.Rows);

        await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(harness.Fixture.Connection, Token);
    }

    /// <summary>
    /// A kept mask keeps only its chain at the key's binding epoch. Curation of the subject recorded
    /// against any other epoch, as version 5 recorded it against the key's dependency epoch, is the
    /// erased subject's curation and goes with it.
    /// </summary>
    /// <remarks>
    /// The earlier-epoch chain is a raw seed: from version 6 on every curation write records the
    /// binding epoch, and a key's binding epoch never moves while its row exists, so no production
    /// writer can make it any more.
    /// </remarks>
    [Fact]
    public async Task A_retained_mask_keeps_only_its_binding_epoch_chain()
    {
        await using CovenantServiceHarness harness = await StartAsync();

        await harness.AddCampaignAsync(CampaignOne, Token);

        await harness.SetAsync(CovenantScope.Global, null, Key, "Global text.", Token);

        await harness.SetAsync(CovenantScope.Campaign, CampaignOne, Key, "Campaign text.", Token);

        long dependency = await CountAsync(harness, $"SELECT KeyEpoch FROM covenant_key_epochs WHERE NormalizedKey = '{Key}';");

        Assert.True(dependency > 0);

        Assert.Equal(0, await CountAsync(harness, $"SELECT IncarnationEpoch FROM covenant_key_epochs WHERE NormalizedKey = '{Key}';"));

        await ExecuteAsync(harness.Fixture.Connection, EarlierEpochMaskChainSql(CampaignOne, Key, dependency));

        await CurateAsync(harness, CovenantCurationKind.Mask, CovenantScope.Campaign, CampaignOne, CovenantLane.Confirmed);

        Guid entryId = await EntryIdAsync(harness, CovenantScope.Campaign, CampaignOne, Key);

        CovenantEntryErasureTally deleted = null!;

        await InTransactionAsync(harness, CovenantEntryErasureMode.Live, async (connection, transaction) =>
        {
            CovenantEntryErasureSubject subject = await ReadSubjectAsync(connection, transaction, entryId);

            Assert.True(subject.IsMasked);

            Assert.False(subject.ReclaimsKey);

            deleted = await CovenantEntryErasurePlan.RunAsync(
                connection, transaction, subject, CovenantArtifactPlanMode.Delete, CovenantEntryErasureMode.Live, Token);

            Assert.Empty(await CovenantEntryErasurePlan.ProveAbsentAsync(connection, transaction, subject, deleted.VersionIds, Token));
        });

        Assert.True(deleted.RetainsCampaignMask);

        // The earlier-epoch chain is gone: its head, its version, and the receipt that produced it.
        Assert.Equal(0, await CountAsync(harness, $"SELECT count(*) FROM covenant_curation_heads WHERE NormalizedKey = '{Key}' AND KeyEpoch = {dependency};"));

        Assert.Equal(0, await CountAsync(harness, $"SELECT count(*) FROM covenant_curation_versions WHERE NormalizedKey = '{Key}' AND KeyEpoch = {dependency};"));

        Assert.Equal(0, await CountAsync(harness, "SELECT count(*) FROM covenant_curation_receipts WHERE MutationId = 'earlier-mask-mutation';"));

        // The binding-epoch chain is kept. A receipt records the key's dependency epoch when it was
        // written, so it is matched by the version it produced rather than by its epoch.
        Assert.Equal(1, await CountAsync(harness, $"SELECT count(*) FROM covenant_curation_heads WHERE NormalizedKey = '{Key}' AND KeyEpoch = 0 AND IsMasked = 1;"));

        Assert.Equal(1, await CountAsync(harness, $"SELECT count(*) FROM covenant_curation_versions WHERE NormalizedKey = '{Key}' AND KeyEpoch = 0;"));

        Assert.Equal(
            1,
            await CountAsync(
                harness,
                $"SELECT count(*) FROM covenant_curation_receipts WHERE NormalizedKey = '{Key}' AND ResultingVersionId IN (SELECT CurationVersionId FROM covenant_curation_versions WHERE KeyEpoch = 0);"));

        Assert.Equal(1, await CountAsync(harness, $"SELECT count(*) FROM covenant_curation_receipts WHERE NormalizedKey = '{Key}';"));

        await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(harness.Fixture.Connection, Token);
    }

    /// <summary>
    /// A Campaign erase that keeps its key touches no other scope's curation of that key: another
    /// Campaign's mask and pin and the Global pin are left byte for byte, and none of them makes the
    /// erased subject read as pinned.
    /// </summary>
    [Fact]
    public async Task A_non_reclaiming_campaign_erase_leaves_every_other_scopes_curation_untouched()
    {
        await using CovenantServiceHarness harness = await StartAsync();

        await harness.AddCampaignAsync(CampaignOne, Token);

        await harness.AddCampaignAsync(CampaignTwo, Token);

        await harness.SetAsync(CovenantScope.Global, null, Key, "Global text.", Token);

        await harness.SetAsync(CovenantScope.Campaign, CampaignOne, Key, "Campaign text.", Token);

        await CurateAsync(harness, CovenantCurationKind.Mask, CovenantScope.Campaign, CampaignTwo, CovenantLane.Confirmed);

        await CurateAsync(harness, CovenantCurationKind.Pin, CovenantScope.Campaign, CampaignTwo, CovenantLane.Proposed);

        await CurateAsync(harness, CovenantCurationKind.Pin, CovenantScope.Global, null, CovenantLane.Confirmed);

        IReadOnlyList<string> before = await OtherScopesCurationAsync(harness, CampaignOne);

        Assert.Equal(9, before.Count);

        Guid entryId = await EntryIdAsync(harness, CovenantScope.Campaign, CampaignOne, Key);

        CovenantEntryErasureScopeFacts facts = null!;

        CovenantEntryErasureTally deleted = null!;

        await InTransactionAsync(harness, CovenantEntryErasureMode.Live, async (connection, transaction) =>
        {
            CovenantEntryErasureSubject subject = await ReadSubjectAsync(connection, transaction, entryId);

            Assert.False(subject.ReclaimsKey);

            Assert.False(subject.IsMasked);

            facts = await CovenantEntryErasurePlan.ReadScopeFactsAsync(connection, transaction, subject, Token);

            deleted = await CovenantEntryErasurePlan.RunAsync(
                connection, transaction, subject, CovenantArtifactPlanMode.Delete, CovenantEntryErasureMode.Live, Token);

            Assert.Empty(await CovenantEntryErasurePlan.ProveAbsentAsync(connection, transaction, subject, deleted.VersionIds, Token));
        });

        Assert.False(facts.IsPinned);

        foreach (string table in CurationTables)
        {
            Assert.Equal(0, Rows(deleted, table));
        }

        Assert.Equal(before, await OtherScopesCurationAsync(harness, CampaignOne));

        await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(harness.Fixture.Connection, Token);
    }

    /// <summary>
    /// Every target table at once: two Confirmed versions, one projected into the search index and one
    /// still pending in the outbox, a Proposed version with a provenance leaf, three receipts, and a pin
    /// in each lane.
    /// </summary>
    private static async Task<Guid> SeedFullEntryAsync(CovenantServiceHarness harness)
    {
        await harness.AddCampaignAsync(CampaignOne, Token);

        await harness.SetAsync(CovenantScope.Campaign, CampaignOne, Key, "First operator text.", Token);

        _ = await CovenantSearchFixture.SynchronizeAsync(harness.Fixture, Token);

        await harness.CorrectAsync(CovenantScope.Campaign, CampaignOne, Key, "Second operator text.", Token);

        await ProposeAsync(
            harness,
            CampaignOne,
            Key,
            "Agent text.",
            [
                new CovenantMutationProvenanceLeaf(
                    0,
                    new Guid("aaaaaaaa-1111-4111-8111-111111111111"),
                    new Guid("bbbbbbbb-1111-4111-8111-111111111111"),
                    "logical/one",
                    CovenantOperationGateFixture.Digest(5),
                    CovenantMaterializationSourceRange.WholeSource,
                    null,
                    null,
                    null,
                    null),
            ]);

        await CurateAsync(harness, CovenantCurationKind.Pin, CovenantScope.Campaign, CampaignOne, CovenantLane.Confirmed);

        await CurateAsync(harness, CovenantCurationKind.Pin, CovenantScope.Campaign, CampaignOne, CovenantLane.Proposed);

        await ConfirmReviewQueueAsync(harness, CampaignOne);

        Assert.True(await CountAsync(harness, "SELECT count(*) FROM covenant_search_outbox;") > 0);

        return await EntryIdAsync(harness, CovenantScope.Campaign, CampaignOne, Key);
    }

    /// <summary>
    /// Confirms every queued Campaign Confirmed item through the production review service, which
    /// writes one review decision receipt per item.
    /// </summary>
    private static async Task ConfirmReviewQueueAsync(CovenantServiceHarness harness, Guid campaignId)
    {
        CovenantMemoryReviewService review = new(
            new FixedCovenantConnectionSource(harness.Fixture.Connection),
            new CovenantCompiler(),
            new MemoryReviewTokenCodec(TimeProvider.System),
            new CovenantMutationKernel(new CovenantQuotaGuard(), MemoryErasureTestKeys.Isolated()),
            new CovenantCurationKernel(),
            TimeProvider.System,
            DetachedAvailabilityRepublisher.Create());

        CovenantOperationScope scope = CovenantOperationScope.ForCampaign(campaignId);

        // A gate bound to the tier's own dataset generation, which the review service compares its
        // leases with; the harness gate serves services that never read it.
        Guid generation = await harness.Fixture.ReadDatasetGenerationAsync(Token);

        FakeCovenantAvailability availability = new();

        availability.Mutate(current => current with { DatasetGeneration = generation });

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(availability);

        CovenantReviewPageDto page;

        await using (CovenantReadLease read = (await gate.AcquireReadAsync(scope, Token)).Value)
        {
            Result<CovenantReviewPageDto> listed = await review.ListAsync(
                new CovenantReviewListRequest(CovenantScope.Campaign, campaignId, CovenantLane.Confirmed, MemoryReviewLimits.MaxPageSize, Cursor: null),
                read,
                Token);

            Assert.True(listed.IsSuccess, listed.IsFailure ? listed.Error.Message : string.Empty);

            page = listed.Value;
        }

        Assert.NotEmpty(page.Items);

        CovenantReviewBulkPrepareRequest confirm = new(
            Guid.CreateVersion7(),
            CovenantScope.Campaign,
            campaignId,
            CovenantLane.Confirmed,
            MemoryReviewAction.Confirm,
            [.. page.Items.Select(static item => new CovenantReviewDecision(item.ObservationToken, null))]);

        MemoryReviewBulkPlanDto plan;

        await using (CovenantReadLease read = (await gate.AcquireReadAsync(scope, Token)).Value)
        {
            Result<MemoryReviewBulkPlanDto> prepared = await review.PrepareAsync(confirm, read, Token);

            Assert.True(prepared.IsSuccess, prepared.IsFailure ? prepared.Error.Message : string.Empty);

            plan = prepared.Value;
        }

        await using CovenantWriteLease write = (await gate.AcquireWriteAsync(scope, Token)).Value;

        Result<MemoryReviewBulkResultDto> applied = await review.ApplyAsync(
            new CovenantReviewBulkApplyRequest(confirm, plan.PreparedPlanToken),
            write,
            Token);

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : string.Empty);
    }

    /// <summary>Every curation row of the key outside one Campaign, column by column, in a stable order.</summary>
    private static async Task<IReadOnlyList<string>> OtherScopesCurationAsync(CovenantServiceHarness harness, Guid excludedCampaign)
    {
        List<string> rows = [];

        foreach (string table in CurationTables)
        {
            await using SqliteCommand command = harness.Fixture.Connection.CreateCommand();

            command.CommandText = $"""
                SELECT * FROM {table}
                WHERE NormalizedKey = $key AND (CampaignId IS NULL OR lower(replace(CampaignId, '-', '')) <> $campaign)
                ORDER BY rowid;
                """;

            _ = command.Parameters.AddWithValue("$key", Key);

            _ = command.Parameters.AddWithValue("$campaign", excludedCampaign.ToString("N"));

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(Token);

            while (await reader.ReadAsync(Token))
            {
                string[] values = new string[reader.FieldCount];

                for (int ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                {
                    values[ordinal] = reader.GetValue(ordinal) switch
                    {
                        byte[] bytes => Convert.ToHexStringLower(bytes),
                        DBNull => "null",
                        object value => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
                    };
                }

                rows.Add($"{table}|{string.Join('|', values)}");
            }
        }

        return rows;
    }

    /// <summary>
    /// A Campaign Confirmed mask chain recorded against an epoch other than the key's binding epoch: one
    /// version, the head that points at it, and the receipt that produced it.
    /// </summary>
    private static string EarlierEpochMaskChainSql(Guid campaignId, string key, long epoch) =>
        $"""
        INSERT INTO covenant_curation_versions (
            CurationVersionId, ScopeCode, CampaignId, NormalizedKey, LaneCode, KeyEpoch, CurationKindCode,
            Revision, PredecessorVersionId, MutationId, RequestIdempotencyDigest, AuthorizationDigest,
            FinalMutationDigest, CreatedAtUtc)
        VALUES (
            'earlier-mask', 2, '{campaignId:D}', '{key}', 1, {epoch}, 3,
            1, NULL, 'earlier-mask-mutation', randomblob(32), randomblob(32),
            randomblob(32), '2026-01-01T00:00:00.0000000Z');
        INSERT INTO covenant_curation_heads (
            ScopeCode, CampaignId, NormalizedKey, LaneCode, KeyEpoch, IsPinned, IsMasked, CurrentVersionId,
            CurrentRevision, UpdatedAtUtc)
        VALUES (2, '{campaignId:D}', '{key}', 1, {epoch}, 0, 1, 'earlier-mask', 1, '2026-01-01T00:00:00.0000000Z');
        INSERT INTO covenant_curation_receipts (
            MutationId, RequestIdempotencyDigest, AuthorizationDigest, FinalMutationDigest, CurationKindCode,
            ScopeCode, CampaignId, NormalizedKey, LaneCode, KeyEpoch, OutcomeCode, ResultingVersionId,
            ResultingRevision, ResponseReceiptDigest, CommittedAtUtc)
        VALUES (
            'earlier-mask-mutation', randomblob(32), randomblob(32), randomblob(32), 3,
            2, '{campaignId:D}', '{key}', 1, {epoch}, 1, 'earlier-mask',
            1, randomblob(32), '2026-01-01T00:00:00.0000000Z');
        """;

    private static async Task<CovenantServiceHarness> StartAsync() =>
        await CovenantServiceHarness.StartAsync(Token, withAccelerator: true);

    /// <summary>One agent proposal for the key, published through the kernel the turn commit uses.</summary>
    private static async Task ProposeAsync(
        CovenantServiceHarness harness,
        Guid campaignId,
        string key,
        string content,
        ImmutableArray<CovenantMutationProvenanceLeaf>? provenance = null)
    {
        long keyEpoch = await CountAsync(
            harness,
            $"SELECT COALESCE(MAX(KeyEpoch), 0) FROM covenant_key_epochs WHERE NormalizedKey = '{key}';");

        Result<IReadOnlyList<CovenantMutationReceipt>> applied = await CovenantMutationFixture.ApplyAsync(
            harness.Fixture,
            await CovenantMutationFixture.LiveBatchAsync(
                harness.Fixture,
                Token,
                CovenantMutationFixture.AgentPropose(campaignId, key, content, 0, keyEpoch, provenance: provenance)),
            Token);

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : string.Empty);
    }

    private static async Task CurateAsync(
        CovenantServiceHarness harness,
        CovenantCurationKind kind,
        CovenantScope scope,
        Guid? campaignId,
        CovenantLane lane)
    {
        Result<CovenantCurationResultDto> curated = await harness.CurateAsync(kind, scope, campaignId, Key, Token, lane: lane);

        Assert.True(curated.IsSuccess, curated.IsFailure ? curated.Error.Message : string.Empty);
    }

    /// <summary>
    /// Runs one erase step in the caller-owned immediate transaction, under the authorization its mode
    /// runs under in production, and commits it.
    /// </summary>
    private static async Task InTransactionAsync(
        CovenantServiceHarness harness,
        CovenantEntryErasureMode mode,
        Func<SqliteConnection, SqliteTransaction, Task> work)
    {
        SqliteConnection connection = harness.Fixture.Connection;

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        using CovenantSqliteAuthorizationScope authorized = CovenantSqliteConnectionInitializer.Instance.Authorize(
            connection,
            mode is CovenantEntryErasureMode.Live
                ? CovenantSqliteAuthorizationKind.CovenantEntryErasure
                : CovenantSqliteAuthorizationKind.CovenantFamilyMaintenance);

        await work(connection, transaction);

        await transaction.CommitAsync(Token);
    }

    private static async Task<CovenantEntryErasureSubject> ReadSubjectAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid entryId)
    {
        CovenantEntryErasureSubject? subject = await CovenantEntryErasurePlan.ReadSubjectAsync(connection, transaction, entryId, Token);

        Assert.NotNull(subject);

        return subject!;
    }

    private static long Rows(CovenantEntryErasureTally tally, string table) =>
        tally.Targets.Single(target => target.Table == table).Rows;

    private static async Task<Guid> EntryIdAsync(CovenantServiceHarness harness, CovenantScope scope, Guid? campaignId, string key)
    {
        await using SqliteCommand command = harness.Fixture.Connection.CreateCommand();

        command.CommandText = """
            SELECT EntryId FROM covenant_entries
            WHERE ScopeCode = $scope AND CampaignId IS $campaign AND NormalizedKey = $key;
            """;

        _ = command.Parameters.AddWithValue("$scope", (int)scope);

        _ = command.Parameters.AddWithValue("$campaign", campaignId is { } campaign ? campaign.ToString("D") : DBNull.Value);

        _ = command.Parameters.AddWithValue("$key", key);

        return Guid.Parse((string)(await command.ExecuteScalarAsync(Token))!, CultureInfo.InvariantCulture);
    }

    private static async Task<long[]> RowIdsAsync(CovenantServiceHarness harness, Guid entryId)
    {
        await using SqliteCommand command = harness.Fixture.Connection.CreateCommand();

        command.CommandText = "SELECT SearchRowId FROM covenant_heads WHERE lower(replace(EntryId, '-', '')) = $entry ORDER BY SearchRowId;";

        _ = command.Parameters.AddWithValue("$entry", entryId.ToString("N"));

        List<long> rows = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(Token);

        while (await reader.ReadAsync(Token))
        {
            rows.Add(reader.GetInt64(0));
        }

        return [.. rows];
    }

    private static Task<long> CountAsync(CovenantServiceHarness harness, string sql) =>
        ScalarAsync(harness.Fixture.Connection, sql);

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql) =>
        Convert.ToInt64(await ScalarObjectAsync(connection, sql), CultureInfo.InvariantCulture);

    private static async Task<object?> ScalarObjectAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return await command.ExecuteScalarAsync(Token);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync(Token);
    }

    private static async Task InstallAsync(SqliteConnection connection, GrimoireSchemaVersionChainSet chains, int version)
    {
        GrimoireSchemaInstallResult result = await GrimoireSchemaTestInstaller.InstallAsync(connection, chains, 1536, Token);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, result.CovenantCanonical.Health);

        Assert.Equal(version, result.CovenantCanonical.SchemaVersion);
    }

    /// <summary>A Global Confirmed NoChange receipt as version 5 wrote one: no entry column at all.</summary>
    private static string LegacyNoChangeReceiptSql(string mutationId, string committedAtUtc) =>
        $"""
        INSERT INTO covenant_mutation_receipts (
            MutationId, RequestIdempotencyDigest, AuthorizationDigest, FinalMutationDigest, MutationKindCode,
            ScopeCode, CampaignId, TargetIdentityDigest, LaneCode, OutcomeCode, ResultingVersionId,
            ResultingLaneRevision, ResponseReceiptDigest, SourceTurnId, CommittedAtUtc)
        VALUES (
            '{mutationId}', randomblob(32), randomblob(32), randomblob(32), 1,
            1, NULL, randomblob(32), 1, 2, NULL,
            NULL, randomblob(32), NULL, '{committedAtUtc}');
        """;

    /// <summary>Commits one Global operator set through the kernel against the evolved catalog's epochs.</summary>
    private static async Task ApplyOperatorSetAsync(SqliteConnection connection, string key, string authored)
    {
        CovenantMutationBatch batch = new(
            new Guid((byte[])(await ScalarObjectAsync(connection, "SELECT DatasetGeneration FROM covenant_state WHERE StateKey = 1;"))!),
            await ScalarAsync(connection, "SELECT KeyReclamationEpoch FROM covenant_state WHERE StateKey = 1;"),
            await ScalarAsync(connection, "SELECT RegistryEpoch FROM campaign_registry_state WHERE StateKey = 1;"),
            CovenantMutationFixture.CommitTime,
            [CovenantMutationFixture.OperatorSet(CovenantOperationScope.Global, key, authored, 0, 0)]);

        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, Token);

        Result<IReadOnlyList<CovenantMutationReceipt>> applied =
            await new CovenantMutationKernel(new CovenantQuotaGuard(), MemoryErasureTestKeys.Isolated())
                .ApplyBatchAsync(batch, new CovenantMutationTransaction(connection, transaction), CovenantAgentErasureGate.None, Token);

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : string.Empty);

        await transaction.CommitAsync(Token);
    }
}
