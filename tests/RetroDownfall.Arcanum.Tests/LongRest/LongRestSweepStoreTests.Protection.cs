using System.Text.Json;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestSweepStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_receipt_associated_with_another_operation_cannot_explain_frozen_projection_changes(bool protect)
    {
        await using Fixture fixture = await Fixture.CreateAsync(protect, sourceCount: 24, duplicates: true);

        LongRestSweep sweep = await fixture.BeginAsync();

        LongRestSweepWork work = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, fixture.Capability, CancellationToken.None))!;

        LongRestSweepHarness.Require(await fixture.Store.CommitExactDuplicatesAsync(sweep, work.Identity, fixture.Capability, CancellationToken.None));

        Guid other = Guid.NewGuid();

        await fixture.H.ExecuteAsync("INSERT INTO LongRunningOperations (Id, Kind, State, RecoveryPolicy, CreatedAt, PublicSummary) VALUES ($id, 'long-rest-sweep', 1, 1, '2026-10-10T00:00:00.0000000Z', 'other');", ("$id", other.ToString("N")));

        // Persisted-association corruption: the real receipt exists, but its completed unit no longer belongs to this operation.
        await fixture.H.ExecuteAsync("UPDATE long_rest_work_items SET OperationId = $other WHERE KindCode = 3 AND DispositionCode = 2;", ("$other", other.ToString("N")));

        LongRestTarget disjoint = await fixture.H.TargetAsync("10000000-0000-0000-0000-000000000024");

        Assert.DoesNotContain(work.Inputs, input => input.Target == disjoint);

        long changes = (long)(await fixture.H.ScalarAsync("SELECT total_changes();"))!;

        Assert.True((await fixture.H.Store.DiscoverAsync(fixture.Generation.GenerationId, disjoint, fixture.Capability, CancellationToken.None)).IsFailure);

        Assert.Equal(changes, await fixture.H.ScalarAsync("SELECT total_changes();"));

        Assert.Equal(0, fixture.AdjudicationSends);
    }

    [Fact]
    public async Task Protected_kernel_without_current_context_refuses_and_v1_keeps_Protected_NoChange()
    {
        await using Fixture fixture = await Fixture.CreateAsync(true, duplicates: true);

        LongRestSweep sweep = await fixture.BeginAsync();

        LongRestSweepWork work = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, fixture.Capability, CancellationToken.None))!;

        LongRestRequest request = new(LongRestTransformationKind.ExactDuplicates, work.Inputs.Select(leaf => leaf.Target).ToArray());

        LongRestProtectedEvidence protection = new(work.Identity.Scope.ProtectedPartitionDigest!.Value, work.Inputs[0].Provenance, [.. work.Inputs.SelectMany(leaf => leaf.LabelDigests)]);

        SagaLongRestService service = new(fixture.H.Saga.Context, TimeProvider.System);

        long changes = (long)(await fixture.H.ScalarAsync("SELECT total_changes();"))!;

        await using (SqliteTransaction transaction = fixture.H.Connection.BeginTransaction(deferred: false))
        {
            Assert.True((await service.ApplyInTransactionAsync(fixture.H.Connection, transaction, request, protection, CancellationToken.None)).IsFailure);

            Assert.True((await service.ApplyInTransactionAsync(fixture.H.Connection, transaction, request, protection, null, CancellationToken.None)).IsFailure);
        }

        Assert.Equal(changes, await fixture.H.ScalarAsync("SELECT total_changes();"));

        LongRestReceipt old = LongRestSweepHarness.Require(await service.ApplyAsync(request, CancellationToken.None));

        Assert.Equal(1, old.PolicyVersion);

        Assert.Equal(LongRestReason.Protected, old.Reason);

        LongRestSweepHarness.Require(await fixture.Store.CommitExactDuplicatesAsync(sweep, work.Identity, fixture.Capability, CancellationToken.None));

        Assert.Equal(old.ReceiptId, LongRestSweepHarness.Require(await service.GetReceiptAsync(old.ReceiptId, CancellationToken.None)).ReceiptId);

        Assert.Equal(2L, await fixture.H.ScalarAsync("SELECT count(DISTINCT PolicyVersion) FROM long_rest_receipts;"));
    }

    [Fact]
    public async Task Protected_SQL_suppression_guard_requires_exact_v2_companions()
    {
        await using Fixture fixture = await Fixture.CreateAsync(true, duplicates: true);

        LongRestSweep sweep = await fixture.BeginAsync();

        LongRestSweepWork work = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, fixture.Capability, CancellationToken.None))!;

        LongRestRequest request = new(LongRestTransformationKind.ExactDuplicates, work.Inputs.Select(leaf => leaf.Target).ToArray());

        LongRestReceipt receipt = LongRestSweepHarness.Require(ProtectedLongRestPolicy.Evaluate(request, work.Inputs,
            new(work.Identity.Scope.ProtectedPartitionDigest!.Value, work.Inputs[0].Provenance, [.. work.Inputs.SelectMany(leaf => leaf.LabelDigests)])));

        await using (SqliteTransaction transaction = fixture.H.Connection.BeginTransaction(deferred: false))
        {
            await ExecuteTestSqlAsync(fixture.H, transaction, "INSERT INTO long_rest_receipts (ReceiptId, PolicyVersion, KindCode, OutcomeCode, ReasonCode, InputHash, OutputHash, SurvivorMemoryId, SurvivorVersionId, CreatedAtUtc) VALUES ($receipt, 2, 1, 1, 0, $input, $output, $memory, $version, '2026-10-10T00:00:00.0000000Z');",
                ("$receipt", receipt.ReceiptId), ("$input", receipt.InputHash), ("$output", receipt.OutputHash), ("$memory", receipt.SurvivorMemoryId), ("$version", receipt.SurvivorVersionId));

            for (int index = 0; index < work.Inputs.Length; index++)
            {
                LongRestSnapshot snapshot = work.Inputs[index].Snapshot;

                await ExecuteTestSqlAsync(fixture.H, transaction, "INSERT INTO long_rest_receipt_inputs VALUES ($receipt, $ordinal, $memory, $version, $format, $hash, $input, $snapshot);",
                    ("$receipt", receipt.ReceiptId), ("$ordinal", index + 1), ("$memory", snapshot.MemoryId), ("$version", snapshot.VersionId),
                    ("$format", (int)snapshot.ContentHashFormat), ("$hash", Convert.FromHexString(snapshot.ContentHash)), ("$input", receipt.InputHash),
                    ("$snapshot", JsonSerializer.Serialize(snapshot, LongRestJsonContext.Default.LongRestSnapshot)));
            }

            SqliteException error = await Assert.ThrowsAsync<SqliteException>(() => ExecuteTestSqlAsync(fixture.H, transaction,
                "INSERT INTO long_rest_suppressions VALUES ($source, $survivor, $receipt);", ("$source", receipt.SupersededVersionIds[0]), ("$survivor", receipt.SurvivorVersionId), ("$receipt", receipt.ReceiptId)));

            Assert.Contains("Long Rest suppression requires", error.Message, StringComparison.Ordinal);

            await transaction.RollbackAsync();
        }

        Assert.Equal(0L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_receipts;"));

        Assert.Equal(0L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_suppressions;"));
    }

    [Theory]
    [InlineData("expired-claim")]
    [InlineData("revoked-capability")]
    [InlineData("source-owner")]
    public async Task Protected_commit_validates_durable_claim_and_capability_before_materializing_accepted_inputs(string fault)
    {
        await using Fixture fixture = await Fixture.CreateAsync(true, campaign: true);

        LongRestSweep sweep = await fixture.BeginAsync();

        LongRestSweepWork work = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, fixture.Capability, CancellationToken.None))!;

        LongRestAdjudication accepted = await fixture.AcceptAsync(sweep, work);

        int materializations = 0;

        fixture.H.Connection.CreateFunction<string, string>("task4_observe_evidence", json =>
        {
            materializations++;

            return json;
        });

        // Observe actual EvidenceJson projection through a temporary read-only view, preserving the real stored row.
        await fixture.H.ExecuteAsync("CREATE TEMP VIEW long_rest_adjudications AS SELECT WorkItemId, WorkDigest, DecisionKindCode, SurvivorVersionId, PayloadDigest, task4_observe_evidence(EvidenceJson) AS EvidenceJson, CreatedAtUtc FROM main.long_rest_adjudications;");

        if (fault == "expired-claim")
        {
            await fixture.H.ExecuteAsync("UPDATE session_turn_claims SET LeaseDeadlineUtc = '2000-01-01T00:00:00.0000000Z';");
        }
        else if (fault == "revoked-capability")
        {
            await fixture.Authority.DisposeAsync();
        }
        else
        {
            using var authorization = RetroDownfall.Arcanum.Infrastructure.Data.Covenant.CovenantSqliteConnectionInitializer.Instance.Authorize(
                fixture.H.Connection, RetroDownfall.Arcanum.Infrastructure.Data.Covenant.CovenantSqliteAuthorizationKind.OwnerCleanup);

            await fixture.H.ExecuteAsync("DELETE FROM session_campaign_bindings WHERE SessionId = $session;", ("$session", work.Inputs[0].Snapshot.SourceSessionId!.Value.ToString("D").ToUpperInvariant()));
        }

        long changes = (long)(await fixture.H.ScalarAsync("SELECT total_changes();"))!;

        Assert.True((await fixture.Store.CommitDecisionAsync(sweep, accepted, fixture.Capability, CancellationToken.None)).IsFailure);

        Assert.Equal(0, materializations);

        Assert.Equal(changes, await fixture.H.ScalarAsync("SELECT total_changes();"));

        Assert.Equal(0L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_receipts;"));
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("content-digest")]
    [InlineData("owner")]
    [InlineData("provenance")]
    [InlineData("mixed-clean")]
    [InlineData("plaintext")]
    [InlineData("vector")]
    [InlineData("partition")]
    public async Task Protected_adjudication_requires_the_exact_current_per_target_manifest(string fault)
    {
        await using Fixture fixture = await Fixture.CreateAsync(true);

        LongRestSweep sweep = await fixture.BeginAsync();

        LongRestSweepWork work = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, fixture.Capability, CancellationToken.None))!;

        LongRestAdjudication candidate = await fixture.AcceptAsync(sweep, work, persist: false);

        LongRestSourceLeaf leaf = work.Inputs[0];

        ArtifactSensitivityLabel original = leaf.CanonicalLabel!;

        ArtifactSensitivityLabel changed = new(original.LabelId, original.ArtifactKind, original.ArtifactId,
            fault == "owner" ? Guid.NewGuid() : original.SessionId, original.CampaignId, original.TurnId,
            fault == "revision" ? original.ArtifactRevision + 1 : original.ArtifactRevision,
            fault == "content-digest" ? new CovenantDigest(new byte[32]) : original.ArtifactContentDigest,
            original.Sensitivity, fault == "provenance" ? GenerationProvenance.CreateExact([Guid.NewGuid()]) : original.Provenance,
            original.ProducingPlanDigest, original.ProducingAdmissionDigest, original.ProducingMaintenanceReceiptDigest, original.CreatedAt);

        LongRestSourceLeaf replacement = leaf with
        {
            CanonicalLabel = changed,
            LabelDigests = [changed.LabelDigest],
            Provenance = changed.Provenance,
            SagaRevision = changed.ArtifactRevision,
            SagaContentDigest = changed.ArtifactContentDigest,
        };

        if (fault == "mixed-clean")
        {
            replacement = leaf with { CanonicalLabel = null, LabelDigests = [], Sensitivity = ContentSensitivity.None,
                Provenance = GenerationProvenance.CreateExact([]), SagaRevision = null, SagaContentDigest = null };
        }
        else if (fault == "plaintext")
        {
            replacement = leaf with { Content = "different retained plaintext" };
        }
        else if (fault == "vector")
        {
            replacement = leaf with { Vector = leaf.Vector.SetItem(0, leaf.Vector[0] + 1) };
        }

        candidate = candidate with { Inputs = candidate.Inputs.SetItem(0, replacement) };

        if (fault == "partition")
        {
            candidate = candidate with { ProtectedEvidence = candidate.ProtectedEvidence! with { PartitionDigest = new CovenantDigest(new byte[32]) } };
        }

        long changes = (long)(await fixture.H.ScalarAsync("SELECT total_changes();"))!;

        Assert.True((await fixture.Store.CommitAcceptedAdjudicationAsync(sweep, candidate, fixture.Capability, CancellationToken.None)).IsFailure);

        Assert.Equal(changes, await fixture.H.ScalarAsync("SELECT total_changes();"));

        Assert.Equal(0L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_adjudications;"));

        Assert.Equal(0L, await fixture.H.ScalarAsync("SELECT count(*) FROM long_rest_receipts;"));

        Assert.Equal(1, fixture.AdjudicationSends);
    }

    private static async Task ExecuteTestSqlAsync(LongRestSweepHarness h, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        await using SqliteCommand command = h.Connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync();
    }
}
