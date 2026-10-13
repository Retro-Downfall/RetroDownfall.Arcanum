using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestMaintenanceTests
{
    [Fact]
    public Task Clean_reopen_registered_recovery_reconciles_canonical_commit_before_checkpoint_without_repeating_effects() =>
        RecoverCleanCheckpointWindowAsync(generationCompleted: false);

    [Fact]
    public Task Clean_reopen_registered_recovery_finishes_completed_generation_before_final_checkpoint_without_rewriting_history() =>
        RecoverCleanCheckpointWindowAsync(generationCompleted: true);

    private static async Task RecoverCleanCheckpointWindowAsync(bool generationCompleted)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false, exact: true);

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!);

            return Task.CompletedTask;
        };

        // Real coordinator checkpoint bytes are interrupted only after the store committed.
        string boundary = generationCompleted
            ? "(SELECT PhaseCode FROM long_rest_discovery_generations) = 6"
            : "EXISTS (SELECT 1 FROM long_rest_receipts)";

        await f.H.ExecuteAsync($"CREATE TEMP TRIGGER fixture_clean_checkpoint_crash BEFORE UPDATE OF CheckpointPayload ON LongRunningOperations WHEN {boundary} BEGIN SELECT RAISE(ABORT, 'fixture clean checkpoint crash'); END;");

        try
        {
            SqliteException crash = await Assert.ThrowsAsync<SqliteException>(() =>
                f.Coordinator.ResumeAsync(f.Operation, null, CancellationToken.None));

            Assert.Equal(19, crash.SqliteErrorCode);

            Assert.Contains("fixture clean checkpoint crash", crash.Message, StringComparison.Ordinal);
        }
        finally
        {
            await f.H.ExecuteAsync("DROP TRIGGER fixture_clean_checkpoint_crash;");
        }

        // Awaiting ResumeAsync joins its heartbeat and unwinds the real old owner/admission.
        // Retain the actual durable Running row and lagging checkpoint; do not synthesize one.
        LongRunningOperation interrupted = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(LongRunningOperationState.Running, interrupted.State);

        Assert.Null(interrupted.TerminalErrorCode);

        Assert.NotNull(interrupted.LeaseOwner);

        Assert.Equal(1, interrupted.AttemptCount);

        Assert.Equal(1, interrupted.CheckpointVersion);

        LongRestSweepCheckpoint lagging = LongRestSweepHarness.Require(
            LongRestSweepCheckpointCodec.Decode(f.Operation, interrupted.CheckpointPayload!));

        LongRestDiscoveryGeneration original = LongRestSweepHarness.Require(
            await f.H.Store.ReadGenerationMetadataAsync(f.Operation, CancellationToken.None))!;

        Assert.Equal(original.GenerationId, lagging.GenerationId);

        Assert.Equal(generationCompleted, original.Phase == LongRestSweepPhase.Completed);

        Assert.NotEqual(LongRestSweepPhase.Completed, lagging.Phase);

        Assert.True(lagging.Revision < (long)(await f.H.ScalarAsync("SELECT Revision FROM long_rest_sweeps;"))!);

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_receipts WHERE OutcomeCode = 1;"));

        Assert.Equal(2L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_receipt_inputs;"));

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_suppressions;"));

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_work_items WHERE KindCode = 3 AND DispositionCode = 2 AND ReceiptId IS NOT NULL;"));

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_work_items unit JOIN long_rest_sweeps sweep ON sweep.OperationId = unit.OperationId WHERE unit.KindCode = 3 AND unit.DispositionCode = 2 AND unit.ReceiptId IS NOT NULL AND unit.WorkDigest = sweep.CommittedWorkDigest;"));

        Assert.Equal(2, f.Handler.SendCount);

        Assert.Equal(2L, await f.H.ScalarAsync("SELECT count(*) FROM BillableOperations;"));

        Assert.Equal(2L, await f.H.ScalarAsync("SELECT sum(PaidCount) FROM long_rest_provider_effects;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_provider_effects WHERE StateCode <> 4 OR CompletionJson IS NULL;"));

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_adjudications;"));

        string originalCommittedWorkDigest = Assert.IsType<string>(await f.H.ScalarAsync("SELECT hex(CommittedWorkDigest) FROM long_rest_sweeps;"));

        object? canonical = await f.H.ScalarAsync(CleanCheckpointCanonicalSnapshotSql);

        object? finishedAnchor = await f.H.ScalarAsync(CleanCheckpointFinishedAnchorSnapshotSql);

        object? effects = await f.H.ScalarAsync(CleanCheckpointPhysicalEffectSnapshotSql);

        object? summaries = await f.H.ScalarAsync(CleanSummarySnapshotSql);

        object? bills = await f.H.ScalarAsync(CleanBillingSnapshotSql);

        object? allUnits = await f.H.ScalarAsync(CleanUnitSnapshotSql);

        object? allInputs = await f.H.ScalarAsync(CleanCheckpointInputSnapshotSql);

        object? generationBytes = await f.H.ScalarAsync(CleanGenerationSnapshotSql);

        object? sweepBytes = await f.H.ScalarAsync(CleanCheckpointSweepSnapshotSql);

        long admissionBefore = f.Admission.CurrentGeneration;

        IGrimoireClosingOwner closing = LongRestSweepHarness.Require(f.Admission.BeginOrResumeExclusive(PaidOwnershipClosingOwner()));

        try
        {
            Assert.True((await f.Admission.DrainRequestAndWorkAsync(closing, CancellationToken.None)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);

            Assert.True((await f.Admission.AbortClosingAsync(closing, _ => ValueTask.FromResult(true), CancellationToken.None)).IsSuccess);
        }
        finally
        {
            await closing.DisposeAsync();
        }

        Assert.Equal(admissionBefore + 1, f.Admission.CurrentGeneration);

        Assert.Equal(original.Fence.Configuration, f.Epoch.Capture().Stamp);

        Assert.Equal(original, LongRestSweepHarness.Require(await f.H.Store.ReadGenerationMetadataAsync(f.Operation, CancellationToken.None)));

        LongRunningOperation beforeRecovery = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(interrupted.Revision, beforeRecovery.Revision);

        Assert.Equal(interrupted.CheckpointPayload, beforeRecovery.CheckpointPayload);

        f.Payloads.Clear();

        int leasesBefore = f.RecordedAdmission.InnerWorkLeases.Count;

        int attestationsBefore = f.RecordedAdmission.DrainedAttestations.Count;

        // The existing helper expires only the durable deadline after unwind, then uses a
        // fresh runtime pass with classified acquisition, reconciler and registered handler.
        LongRunningOperationReconciliationSummary recovered = await RecoverCleanAssociationAsync(f);

        Assert.Contains(f.RecordedAdmission.InnerWorkLeases.Skip(leasesBefore), lease =>
            lease.Kind == GrimoireWorkKind.LongRunningOperationRecovery && lease.Generation == f.Admission.CurrentGeneration);

        // Recovery runs the clean sweep under a Long Rest lease of its own, minted at the current generation.
        Assert.Contains(f.RecordedAdmission.InnerWorkLeases.Skip(leasesBefore), lease =>
            lease.Kind == GrimoireWorkKind.LongRestSweep && lease.Generation == f.Admission.CurrentGeneration);

        // Every attestation presents the sweep lease recovery minted and the interval from the ORIGINAL previous admission
        // to the current one: the coordinator pre-check, then each store validation (two attestations apiece).
        RecordingGrimoireWorkAdmissionGate.DrainedAttestation[] attested = f.RecordedAdmission.DrainedAttestations.Skip(attestationsBefore).ToArray();

        int minimum = generationCompleted ? 13 : 7;

        Assert.True(attested.Length >= minimum, $"expected at least {minimum} attestations, saw {attested.Length}");

        Assert.All(attested, attestation =>
        {
            Assert.True(attestation.Attested);

            Assert.Equal(GrimoireWorkKind.LongRestSweep, attestation.Kind);

            Assert.Equal(original.Fence.AdmissionGeneration, attestation.Previous);

            Assert.Equal(f.Admission.CurrentGeneration, attestation.Current);
        });

        Assert.Equal(2, f.Handler.SendCount);

        Assert.Empty(f.Payloads);

        Assert.Equal(canonical, await f.H.ScalarAsync(CleanCheckpointCanonicalSnapshotSql));

        Assert.Equal(finishedAnchor, await f.H.ScalarAsync(CleanCheckpointFinishedAnchorSnapshotSql));

        Assert.Equal(effects, await f.H.ScalarAsync(CleanCheckpointPhysicalEffectSnapshotSql));

        Assert.Equal(summaries, await f.H.ScalarAsync(CleanSummarySnapshotSql));

        Assert.Equal(bills, await f.H.ScalarAsync(CleanBillingSnapshotSql));

        Assert.Equal(1, recovered.Completed);

        Assert.Equal(0, recovered.RequiresAttention);

        LongRunningOperation final = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(LongRunningOperationState.Completed, final.State);

        Assert.Null(final.TerminalErrorCode);

        Assert.Equal(interrupted.AttemptCount + 1, final.AttemptCount);

        LongRestSweepCheckpoint caughtUp = LongRestSweepHarness.Require(
            LongRestSweepCheckpointCodec.Decode(f.Operation, final.CheckpointPayload!));

        Assert.Equal(original.GenerationId, caughtUp.GenerationId);

        Assert.Equal(LongRestSweepPhase.Completed, caughtUp.Phase);

        Assert.Equal((long)(await f.H.ScalarAsync("SELECT Revision FROM long_rest_sweeps;"))!, caughtUp.Revision);

        Assert.Equal(await f.H.ScalarAsync("SELECT CommittedItemCursor FROM long_rest_sweeps;"), caughtUp.CommittedItemCursor);

        Assert.Equal(originalCommittedWorkDigest, await f.H.ScalarAsync("SELECT hex(CommittedWorkDigest) FROM long_rest_sweeps;"));

        Assert.Equal(originalCommittedWorkDigest, Convert.ToHexString(caughtUp.CommittedWorkDigest!.Value.Bytes));

        if (generationCompleted)
        {
            // Completed-generation reconciliation changes only the operation ledger.
            Assert.Equal(generationBytes, await f.H.ScalarAsync(CleanGenerationSnapshotSql));

            Assert.Equal(sweepBytes, await f.H.ScalarAsync(CleanCheckpointSweepSnapshotSql));

            Assert.Equal(allUnits, await f.H.ScalarAsync(CleanUnitSnapshotSql));

            Assert.Equal(allInputs, await f.H.ScalarAsync(CleanCheckpointInputSnapshotSql));
        }
    }

    private const string CleanCheckpointCanonicalSnapshotSql = """
        SELECT json_array(
            (SELECT json_group_array(json(Row)) FROM (SELECT json_array(ReceiptId, PolicyVersion, KindCode, OutcomeCode, ReasonCode,
                InputHash, OutputHash, RequestedSurvivorVersionId, SurvivorMemoryId, SurvivorVersionId, CreatedAtUtc) AS Row
                FROM long_rest_receipts ORDER BY ReceiptId)),
            (SELECT json_group_array(json(Row)) FROM (SELECT json_array(ReceiptId, Ordinal, MemoryId, VersionId, ContentHashFormatCode,
                hex(ContentHash), SnapshotHash, SnapshotJson) AS Row FROM long_rest_receipt_inputs ORDER BY ReceiptId, Ordinal)),
            (SELECT json_group_array(json(Row)) FROM (SELECT json_array(SourceVersionId, SurvivorVersionId, ReceiptId) AS Row
                FROM long_rest_suppressions ORDER BY SourceVersionId)));
        """;

    private const string CleanCheckpointFinishedAnchorSnapshotSql = """
        SELECT json_group_array(json(Row)) FROM (
            SELECT json_array(unit.WorkItemId, hex(unit.WorkDigest), unit.GenerationId, unit.OperationId, unit.KindCode, unit.ItemId,
                hex(unit.SourceDigest), hex(unit.RecipeDigest), unit.Revision, unit.DispositionCode, unit.ReceiptId,
                (SELECT json_group_array(json(InputRow)) FROM (SELECT json_array(Ordinal, MemoryId, VersionId, SourceEvidenceJson) AS InputRow
                    FROM long_rest_work_inputs WHERE WorkItemId = unit.WorkItemId ORDER BY Ordinal))) AS Row
            FROM long_rest_work_items unit WHERE unit.KindCode = 3 AND unit.ReceiptId IS NOT NULL ORDER BY unit.WorkItemId);
        """;

    private const string CleanCheckpointPhysicalEffectSnapshotSql = """
        SELECT json_group_array(json(Row)) FROM (
            SELECT json_array(WorkItemId, hex(WorkDigest), OriginalWorkJson, AttemptId, PayloadKindCode, PhysicalOrdinal, Revision, StateCode,
                hex(PayloadDigest), hex(DestinationDigest), hex(DisclosureReceiptDigest), SentCount, PaidCount, InvalidCount, FailureCount,
                InputTokens, OutputTokens, TotalTokens, CachedInputTokens, ReasoningTokens, CostUsd, CompletionJson, FailureCode, UpdatedAtUtc) AS Row
            FROM long_rest_provider_effects ORDER BY WorkItemId);
        """;

    private const string CleanCheckpointInputSnapshotSql = """
        SELECT json_group_array(json(Row)) FROM (
            SELECT json_array(WorkItemId, Ordinal, MemoryId, VersionId, SourceEvidenceJson) AS Row
            FROM long_rest_work_inputs ORDER BY WorkItemId, Ordinal);
        """;

    private const string CleanCheckpointSweepSnapshotSql = """
        SELECT json_group_array(json(Row)) FROM (
            SELECT json_array(OperationId, GenerationId, ScopeKindCode, CampaignId, hex(PartitionDigest), ConfigurationJson,
                PhaseCode, Revision, CommittedItemCursor, hex(CommittedWorkDigest), UpdatedAtUtc) AS Row
            FROM long_rest_sweeps ORDER BY OperationId);
        """;
}
