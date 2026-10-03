using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

using Xunit.Sdk;

namespace RetroDownfall.Arcanum.Tests.Memory;

/// <summary>
/// The cross-store snapshot the isolation suite judges every memory verb by: which tables it reads,
/// which store each row belongs to, and when two snapshots say that only one store changed.
/// </summary>
/// <remarks>
/// The subject here is the helper, not a store, so these tests seed raw rows straight into a copy of
/// the head catalog. A store's own writers are what the isolation suite drives.
/// </remarks>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class MemoryStoreSnapshotTests(GrimoireFixture fixture) : IAsyncLifetime
{
    private const string Fingerprints = "memory_erasure_fingerprints";

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    private static CancellationToken Token => CancellationToken.None;

    private SqliteConnection Connection => (SqliteConnection)_db!.Database.GetDbConnection();

    public async Task InitializeAsync()
    {
        if (!GrimoireFixture.SqlCipherAvailable)
        {
            return;
        }

        _dbPath = fixture.CopyDatabase();

        _db = fixture.CreateContext(_dbPath);

        await _db.Database.OpenConnectionAsync(Token);
    }

    public async Task DisposeAsync()
    {
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }

        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    /// <summary>
    /// Discovery reads the live catalog, so a table the head schema gains is either partitioned or
    /// refused, never silently left out of the comparison.
    /// </summary>
    [SkippableFact]
    public async Task Every_memory_table_in_the_head_catalog_has_a_partition_rule()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        MemoryStoreSnapshot snapshot = await MemoryStoreSnapshot.CaptureAsync(Connection, Token);

        string[] expected = await DiscoveredTablesAsync();

        Assert.Equal(expected, snapshot.Tables.Order(StringComparer.Ordinal));

        foreach (string table in (string[])
            [
                "saga_memories",
                "lexicon_entries",
                "covenant_entries",
                "annal_claims",
                "memory_erasure_fingerprints",
                "memory_erasure_receipt_subjects",
                "artifact_sensitivity",
                "external_disclosure_receipts",
            ])
        {
            Assert.Contains(table, snapshot.Tables);
        }

        // A full-text index is read through the shadow tables that hold its tokens, never through the
        // virtual table, which would only answer with the content it indexes.
        Assert.DoesNotContain("lexicon_fts", snapshot.Tables);

        Assert.Contains("lexicon_fts_data", snapshot.Tables);

        // Every store owns tables of its own in the catalog, so no family's comparison is empty by
        // construction. The Shared family has no table of its own: it is the remainder of the one
        // table split by artifact kind.
        foreach (MemoryStoreFamily family in (MemoryStoreFamily[])[MemoryStoreFamily.Saga, MemoryStoreFamily.Lexicon, MemoryStoreFamily.Covenant])
        {
            Assert.True(
                snapshot.Tables.Any(table => MemoryStoreSnapshot.RuleFor(table) is MemoryStorePartitionRule.Whole whole && whole.Family == family),
                $"No discovered table belongs wholly to the {family} family.");
        }

        Assert.IsType<MemoryStorePartitionRule.ByCode>(MemoryStoreSnapshot.RuleFor("artifact_sensitivity"));
    }

    /// <summary>
    /// The tables every store writes into are split row by row: the Annals by their subject store, the
    /// erasure evidence by its store code, a receipt's subjects with their receipt, a version with its
    /// claim, and a sensitivity label by the kind of artifact it labels.
    /// </summary>
    [SkippableFact]
    public async Task Shared_tables_are_partitioned_by_their_store_code()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await ExecuteAsync(
            """
            INSERT INTO annal_claims (ClaimId, SubjectStoreCode, SubjectId, CreatedAtUtc)
            VALUES ('saga-claim', 1, 'saga-subject', '2026-09-01T00:00:00.0000000Z'),
                   ('lexicon-claim', 2, 'lexicon-subject', '2026-09-01T00:00:00.0000000Z');

            INSERT INTO annal_versions (
                VersionId, ClaimId, Revision, OperationCode, OriginCode, ScopeKindCode, CampaignId,
                SensitivityCode, ContentHash, ValidFromUtc, ValidToUtc, RecordedAtUtc, PredecessorVersionId)
            VALUES ('lexicon-version', 'lexicon-claim', 1, 1, 1, 1, NULL,
                    0, zeroblob(32), '2026-09-01T00:00:00.0000000Z', NULL, '2026-09-01T00:00:00.0000000Z', NULL);

            INSERT INTO memory_erasure_fingerprints (Fingerprint, StoreCode, KeyId)
            VALUES (randomblob(32), 1, zeroblob(16)),
                   (randomblob(32), 2, zeroblob(16)),
                   (randomblob(32), 3, zeroblob(16));

            INSERT INTO memory_erasure_receipts (
                MutationId, StoreCode, KeyId, RequestDigest, EffectDigest, ErasedItemCount, RemovedRowCount,
                RemovedLabelCount, RemovedRetirementSuppressionCount, AuthorshipEvidenceCode, ContextEvidenceCode,
                EmbeddingEvidenceCode, BackupEvidenceCode, OtherExternalEvidenceCode, RetainedCopiesMask,
                ScrubStateCode, ScrubPendingReasonMask)
            VALUES ('0D2F9C44-6A55-4A50-9E1B-6B1D0C3F7A10', 3, zeroblob(16), zeroblob(32), zeroblob(32), 1, 1,
                    0, 0, 1, 1, 1, 1, 1, 0, 2, 0);

            INSERT INTO memory_erasure_receipt_subjects (MutationId, SubjectDigest)
            VALUES ('0D2F9C44-6A55-4A50-9E1B-6B1D0C3F7A10', randomblob(32));
            """);

        await LabelAsync(6, "6B7C4A3E-0F0D-4C55-8E57-2D4C7B7C1A01");

        await LabelAsync(7, "6B7C4A3E-0F0D-4C55-8E57-2D4C7B7C1A02");

        await LabelAsync(1, "6B7C4A3E-0F0D-4C55-8E57-2D4C7B7C1A03");

        MemoryStoreSnapshot snapshot = await MemoryStoreSnapshot.CaptureAsync(Connection, Token);

        Assert.Equal(1, snapshot.CountRows(MemoryStoreFamily.Saga, "annal_claims"));

        Assert.Equal(1, snapshot.CountRows(MemoryStoreFamily.Lexicon, "annal_claims"));

        Assert.Equal(1, snapshot.CountRows(MemoryStoreFamily.Lexicon, "annal_versions"));

        Assert.Equal(0, snapshot.CountRows(MemoryStoreFamily.Saga, "annal_versions"));

        Assert.Equal(1, snapshot.CountRows(MemoryStoreFamily.Covenant, Fingerprints));

        Assert.Equal(1, snapshot.CountRows(MemoryStoreFamily.Saga, Fingerprints));

        Assert.Equal(1, snapshot.CountRows(MemoryStoreFamily.Lexicon, Fingerprints));

        Assert.Equal(1, snapshot.CountRows(MemoryStoreFamily.Lexicon, "memory_erasure_receipts"));

        Assert.Equal(1, snapshot.CountRows(MemoryStoreFamily.Lexicon, "memory_erasure_receipt_subjects"));

        foreach (MemoryStoreFamily other in (MemoryStoreFamily[])[MemoryStoreFamily.Saga, MemoryStoreFamily.Covenant, MemoryStoreFamily.Shared])
        {
            Assert.Equal(0, snapshot.CountRows(other, "memory_erasure_receipt_subjects"));
        }

        Assert.Equal(1, snapshot.CountRows(MemoryStoreFamily.Saga, "artifact_sensitivity"));

        Assert.Equal(1, snapshot.CountRows(MemoryStoreFamily.Lexicon, "artifact_sensitivity"));

        Assert.Equal(1, snapshot.CountRows(MemoryStoreFamily.Shared, "artifact_sensitivity"));

        Assert.Equal(0, snapshot.CountRows(MemoryStoreFamily.Covenant, "artifact_sensitivity"));
    }

    /// <summary>
    /// A shared Annals or erasure-evidence table the helper does not know how to split is refused by
    /// name, because guessing its store would let a cross-store write hide inside the wrong family.
    /// </summary>
    [Fact]
    public void An_unclassified_annal_or_erasure_table_fails_loudly()
    {
        InvalidOperationException annal = Assert.Throws<InvalidOperationException>(
            static () => MemoryStoreSnapshot.RuleFor("annal_future"));

        Assert.Contains("annal_future", annal.Message, StringComparison.Ordinal);

        InvalidOperationException evidence = Assert.Throws<InvalidOperationException>(
            static () => MemoryStoreSnapshot.RuleFor("memory_erasure_future"));

        Assert.Contains("memory_erasure_future", evidence.Message, StringComparison.Ordinal);
    }

    /// <summary>A verb that changed nothing proves nothing about isolation, so it fails.</summary>
    [Fact]
    public void AssertOnlyChanged_fails_when_the_target_did_not_change()
    {
        MemoryStoreSnapshot before = Snapshot(saga: ["saga_memories:a"], lexicon: ["lexicon_entries:b"]);

        MemoryStoreSnapshot after = Snapshot(saga: ["saga_memories:a"], lexicon: ["lexicon_entries:b"]);

        XunitException failed = Assert.ThrowsAny<XunitException>(
            () => MemoryStoreSnapshot.AssertOnlyChanged(before, after, MemoryStoreFamily.Saga));

        Assert.Contains("Saga", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AssertOnlyChanged_fails_when_another_family_changed()
    {
        MemoryStoreSnapshot before = Snapshot(saga: ["saga_memories:a"], lexicon: ["lexicon_entries:b"]);

        MemoryStoreSnapshot after = Snapshot(saga: ["saga_memories:a2"], lexicon: ["lexicon_entries:b2"]);

        XunitException failed = Assert.ThrowsAny<XunitException>(
            () => MemoryStoreSnapshot.AssertOnlyChanged(before, after, MemoryStoreFamily.Saga));

        Assert.Contains("Lexicon", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AssertOnlyChanged_passes_when_only_the_target_changed()
    {
        MemoryStoreSnapshot before = Snapshot(saga: ["saga_memories:a"], lexicon: ["lexicon_entries:b"]);

        MemoryStoreSnapshot after = Snapshot(saga: ["saga_memories:a", "saga_memories:c"], lexicon: ["lexicon_entries:b"]);

        MemoryStoreSnapshot.AssertOnlyChanged(before, after, MemoryStoreFamily.Saga);
    }

    private static MemoryStoreSnapshot Snapshot(string[] saga, string[] lexicon) =>
        new(
            new Dictionary<MemoryStoreFamily, IReadOnlyList<string>>
            {
                [MemoryStoreFamily.Saga] = saga,
                [MemoryStoreFamily.Lexicon] = lexicon,
                [MemoryStoreFamily.Covenant] = ["covenant_entries:c"],
                [MemoryStoreFamily.Shared] = [],
            },
            new HashSet<string>(StringComparer.Ordinal) { "saga_memories", "lexicon_entries", "covenant_entries" });

    /// <summary>
    /// The tables the discovery rule selects, read independently of the helper: every ordinary table
    /// whose name starts with a memory store's prefix, and the five shared evidence tables by name.
    /// </summary>
    private async Task<string[]> DiscoveredTablesAsync()
    {
        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText =
            """
            SELECT name FROM sqlite_master
            WHERE type = 'table'
              AND sql NOT LIKE 'CREATE VIRTUAL TABLE%'
              AND (substr(name, 1, 5) = 'saga_'
                OR substr(name, 1, 8) = 'lexicon_'
                OR substr(name, 1, 9) = 'covenant_'
                OR substr(name, 1, 6) = 'annal_'
                OR substr(name, 1, 15) = 'memory_erasure_'
                OR name IN (
                    'artifact_sensitivity',
                    'external_disclosure_receipts',
                    'disclosure_subject_state',
                    'external_disclosure_state',
                    'disclosure_subject_aggregates'));
            """;

        List<string> names = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(Token);

        while (await reader.ReadAsync(Token))
        {
            names.Add(reader.GetString(0));
        }

        return [.. names.Order(StringComparer.Ordinal)];
    }

    private async Task LabelAsync(int kind, string labelId)
    {
        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText =
            """
            INSERT INTO artifact_sensitivity (
                LabelId, ArtifactKindCode, ArtifactId, SensitivityCode, ProvenanceModeCode, ExactGenerationIds,
                GenerationBloom, SessionId, CampaignId, TurnId, ArtifactRevision, ArtifactContentDigest,
                SensitivityDigest, ProducingPlanDigest, ProducingAdmissionDigest, ProducingMaintenanceReceiptDigest,
                ArtifactLabelDigest, CreatedAtUtc)
            VALUES ($label, $kind, $label, 1, 1, randomblob(16),
                    NULL, NULL, NULL, NULL, 1, zeroblob(32),
                    zeroblob(32), NULL, NULL, NULL,
                    zeroblob(32), '2026-09-01T00:00:00.0000000Z');
            """;

        _ = command.Parameters.AddWithValue("$label", labelId);

        _ = command.Parameters.AddWithValue("$kind", kind);

        _ = await command.ExecuteNonQueryAsync(Token);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync(Token);
    }
}
