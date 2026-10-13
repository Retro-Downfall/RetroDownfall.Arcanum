using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestSchemaEvolutionTests
{
    [Fact]
    public async Task Backfill_cursor_and_source_rows_commit_together()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, CoreSchemaVersionSeventeenFixture.ChainSet(), 64, CancellationToken.None);

        await ExecuteAsync(connection, """
            WITH RECURSIVE numbers(value) AS (VALUES(1) UNION ALL SELECT value + 1 FROM numbers WHERE value < 260)
            INSERT INTO saga_memories (Id, Content, CreatedAt, ScopeKindCode)
            SELECT printf('memory-%03d', value), 'source', $at, 1 FROM numbers;
            """, ("$at", At));

        GrimoireSchemaInstaller installer = GrimoireSchemaTestInstaller.Create();

        GrimoireSchemaInitializationContext context = GrimoireSchemaTestInstaller.CreateContext();

        GrimoireSchemaInstallResult installed = await installer.InstallAsync(connection, 64, context, CancellationToken.None);

        Assert.Equal(GrimoireSchemaTierHealth.TransitionIncomplete, installed.Core.Health);

        GrimoireSchemaTransitionJournalRow journal = (await GrimoireSchemaTransitionJournal.ReadAsync(connection, null, GrimoireSchemaTransactionTier.Core, CancellationToken.None))!;

        GrimoireSchemaBackfillRunner runner = new(installer, TimeProvider.System);

        GrimoireSchemaVersionChain chain = GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core);

        // Refuse exactly the journal write after the bounded source insertions. Both must roll back.
        await ExecuteAsync(connection, "CREATE TEMP TRIGGER fail_backfill_cursor BEFORE UPDATE OF BackfillCursor ON grimoire_schema_transitions BEGIN SELECT RAISE(ABORT, 'injected cursor failure'); END;");

        await Assert.ThrowsAsync<SqliteException>(() => runner.AdvanceAsync(connection, chain, journal, context, 1, CancellationToken.None));

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT count(*) FROM long_rest_sources;"));

        Assert.Equal(journal, await GrimoireSchemaTransitionJournal.ReadAsync(connection, null, GrimoireSchemaTransactionTier.Core, CancellationToken.None));

        await ExecuteAsync(connection, "DROP TRIGGER fail_backfill_cursor;");

        GrimoireSchemaBackfillProgress batch = await runner.AdvanceAsync(connection, chain, journal, context, 1, CancellationToken.None);

        Assert.Equal(128, batch.RowsProcessed);

        Assert.False(batch.StepComplete);

        Assert.Equal(128L, await ScalarAsync(connection, "SELECT count(*) FROM long_rest_sources;"));

        GrimoireSchemaTransitionJournalRow checkpoint = (await GrimoireSchemaTransitionJournal.ReadAsync(connection, null, GrimoireSchemaTransactionTier.Core, CancellationToken.None))!;

        Assert.Equal("memory-128", checkpoint.BackfillCursor);

        Assert.Equal(128, checkpoint.BackfillRowsProcessed);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, (await InstallHeadAsync(connection)).Core.Health);

        Assert.Equal(260L, await ScalarAsync(connection, "SELECT count(*) FROM long_rest_sources;"));
    }

    [Fact]
    public async Task Moving_a_current_head_invalidates_both_former_and_new_incoming_dependency_targets()
    {
        await using Scratch scratch = await Scratch.StartAsync();

        await MemoryAsync(scratch.Connection, "old-target");

        await MemoryAsync(scratch.Connection, "new-target");

        await MemoryAsync(scratch.Connection, "dependent");

        await ExecuteAsync(scratch.Connection, """
            INSERT INTO annal_dependencies SELECT 'dependent-v1', dependent.Sequence, 'old-target-v1', target.Sequence, 1, 1, $at
            FROM annal_versions dependent JOIN annal_versions target
            WHERE dependent.VersionId = 'dependent-v1' AND target.VersionId = 'old-target-v1';
            INSERT INTO annal_versions (VersionId, ClaimId, Revision, OperationCode, OriginCode, ScopeKindCode, SensitivityCode,
                ContentHash, ValidFromUtc, RecordedAtUtc, PredecessorVersionId)
            VALUES ('dependent-v2', 'dependent-claim', 2, 2, 1, 1, 0, zeroblob(32), $at, $at, 'dependent-v1');
            INSERT INTO annal_dependencies SELECT 'dependent-v2', dependent.Sequence, 'new-target-v1', target.Sequence, 1, 1, $at
            FROM annal_versions dependent JOIN annal_versions target
            WHERE dependent.VersionId = 'dependent-v2' AND target.VersionId = 'new-target-v1';
            """, ("$at", At));

        long oldEpoch = (long)(await ScalarAsync(scratch.Connection, "SELECT MutationEpoch FROM long_rest_sources WHERE MemoryId = 'old-target';"))!;

        long newEpoch = (long)(await ScalarAsync(scratch.Connection, "SELECT MutationEpoch FROM long_rest_sources WHERE MemoryId = 'new-target';"))!;

        await ExecuteAsync(scratch.Connection, "UPDATE annal_heads SET CurrentVersionId = 'dependent-v2', CurrentRevision = 2, CurrentOperationCode = 2 WHERE ClaimId = 'dependent-claim';");

        Assert.Equal(oldEpoch + 1, await ScalarAsync(scratch.Connection, "SELECT MutationEpoch FROM long_rest_sources WHERE MemoryId = 'old-target';"));

        Assert.Equal(newEpoch + 1, await ScalarAsync(scratch.Connection, "SELECT MutationEpoch FROM long_rest_sources WHERE MemoryId = 'new-target';"));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("label")]
    [InlineData("revision")]
    [InlineData("partition")]
    [InlineData("missing")]
    public async Task V2_suppression_requires_each_exact_retained_canonical_label(string defect)
    {
        await using Scratch scratch = await Scratch.StartAsync();

        SqliteConnection connection = scratch.Connection;

        await MemoryAsync(connection, "source", sensitivity: 1);

        await MemoryAsync(connection, "survivor", sensitivity: 1);

        await ExecuteAsync(connection, """
            INSERT INTO artifact_sensitivity (LabelId, ArtifactKindCode, ArtifactId, SensitivityCode, ProvenanceModeCode,
                ExactGenerationIds, ArtifactRevision, ArtifactContentDigest, SensitivityDigest, ArtifactLabelDigest, CreatedAtUtc)
            VALUES ('source-label', 6, 'SOURCE', 1, 1, zeroblob(16), 37, zeroblob(32), zeroblob(32), zeroblob(32), $at),
                ('survivor-label', 6, 'SURVIVOR', 1, 1, zeroblob(16), 0, zeroblob(32), zeroblob(32), zeroblob(32), $at);
            INSERT INTO long_rest_receipts (ReceiptId, PolicyVersion, KindCode, OutcomeCode, ReasonCode, InputHash, OutputHash,
                SurvivorMemoryId, SurvivorVersionId, CreatedAtUtc)
            VALUES ('r', 2, 1, 1, 0, $hash, $hash, 'survivor', 'survivor-v1', $at);
            """, ("$at", At), ("$hash", InputHash));

        await InputAsync(connection, "r", "source", 1);

        await InputAsync(connection, "r", "survivor", 2);

        await ExecuteAsync(connection, """
            INSERT INTO long_rest_protected_receipt_evidence (ReceiptId, PartitionDigest, ProvenanceModeCode,
                ExactGenerationIds, WorkDigest, EvidenceDigest) VALUES ('r', zeroblob(32), 1, zeroblob(16), zeroblob(32), zeroblob(32));
            INSERT INTO long_rest_protected_receipt_inputs (ReceiptId, Ordinal, MemoryId, ClaimId, VersionId, LabelId,
                LabelDigest, SagaRevision, SagaContentDigest, SensitivityDigest, LabelJson)
            VALUES ('r', 1, 'source', 'source-claim', 'source-v1', $label, zeroblob(32), $revision, zeroblob(32), $partition, '{}'),
                ('r', 2, 'survivor', 'survivor-claim', 'survivor-v1', 'survivor-label', zeroblob(32), 0, zeroblob(32), zeroblob(32), '{}');
            """, ("$label", defect == "label" ? "wrong" : "source-label"), ("$revision", defect == "revision" ? 38 : 37),
            ("$partition", defect == "partition" ? Enumerable.Repeat((byte)1, 32).ToArray() : new byte[32]));

        if (defect == "missing")
        {
            await ExecuteAsync(connection, "DELETE FROM long_rest_protected_receipt_inputs WHERE Ordinal = 1;");
        }

        long epoch = (long)(await ScalarAsync(connection, "SELECT MutationEpoch FROM long_rest_sources WHERE MemoryId = 'source';"))!;

        if (defect == "valid")
        {
            await SuppressionAsync(connection, "source", "survivor", "r");

            Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM long_rest_suppressions;"));

            Assert.Equal(epoch, await ScalarAsync(connection, "SELECT MutationEpoch FROM long_rest_sources WHERE MemoryId = 'source';"));
        }
        else
        {
            await Assert.ThrowsAsync<SqliteException>(() => SuppressionAsync(connection, "source", "survivor", "r"));
        }
    }
}
