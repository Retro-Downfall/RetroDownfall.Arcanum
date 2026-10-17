using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.LongRest;

[Collection("Grimoire")]
public sealed class LongRestTerminalReservationRefusalTests
{
    [Fact]
    public async Task A_process_owner_blocks_terminal_replacement_without_changes_then_release_permits_a_fresh_request()
    {
        // Catches bypassing a live process claim or releasing another owner's claim on refusal.
        await using var f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        await SettleAsync(f, LongRunningOperationState.Failed);

        LongRunningOperationOwnership ownership = new();

        Assert.True(ownership.TryClaim(f.Operation, out Guid token));

        string[] before = await SnapshotAsync(f);

        try
        {
            Assert.True((await AdmitAsync(f, ownership)).IsFailure);

            Assert.Equal(before, await SnapshotAsync(f));

            Assert.True(ownership.IsClaimedBy(f.Operation, token));
        }
        finally
        {
            Assert.True(ownership.Release(f.Operation, token));
        }

        LongRunningOperation fresh = Require(await AdmitAsync(f, ownership));

        Assert.NotEqual(f.Operation, fresh.Id);

        Assert.Equal(LongRunningOperationState.Pending, fresh.State);

        Assert.False(ownership.IsClaimed(f.Operation));

        Assert.Equal(0, f.Handler.SendCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Either_retained_terminal_lease_field_refuses_replacement_without_partial_retirement(bool ownerField)
    {
        // Catches treating terminal state alone as proof that a durable lease was released.
        await using var f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        await SettleAsync(f, LongRunningOperationState.Abandoned);

        await f.H.ExecuteAsync("UPDATE LongRunningOperations SET LeaseOwner = $owner, LeaseExpiresAt = $expiry WHERE Id = $id;",
            ("$owner", ownerField ? "retained-owner" : null),
            ("$expiry", ownerField ? null : UtcInstantText.Format(f.Clock.GetUtcNow().AddMinutes(5))),
            ("$id", f.Operation.ToString("N")));

        LongRunningOperationOwnership ownership = new();

        string[] before = await SnapshotAsync(f);

        Assert.True((await AdmitAsync(f, ownership)).IsFailure);

        Assert.Equal(before, await SnapshotAsync(f));

        Assert.False(ownership.IsClaimed(f.Operation));

        await f.H.ExecuteAsync("UPDATE LongRunningOperations SET LeaseOwner = NULL, LeaseExpiresAt = NULL WHERE Id = $id;",
            ("$id", f.Operation.ToString("N")));

        Assert.NotEqual(f.Operation, Require(await AdmitAsync(f, ownership)).Id);

        Assert.Equal(0, f.Handler.SendCount);
    }

    [Fact]
    public async Task Fresh_reservation_insert_failure_rolls_back_both_retirements_and_paid_history_then_allows_retry()
    {
        // Catches committing either retirement before fresh admission, evidence purge, an
        // orphan new operation, or leaking the exact process claim on a failed transaction.
        await using var f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: true, protectedSources: false);

        // Two bounded real units capture and fold the two-source fixture. The transport is
        // the fixture's counting SDK handler; native attempt/accepted rows are production stores.
        Require(await f.ProcessAsync(f.Operation, "fixture-worker", null, CancellationToken.None));

        Require(await f.ProcessAsync(f.Operation, "fixture-worker", null, CancellationToken.None));

        Assert.True((long)(await f.H.ScalarAsync("SELECT sum(PaidCount) FROM long_rest_provider_effects;"))! > 0);

        Assert.True((long)(await f.H.ScalarAsync("SELECT count(*) FROM long_rest_accepted_summaries;"))! > 0);

        await SettleAsync(f, LongRunningOperationState.Failed);

        LongRunningOperationOwnership ownership = new();

        await f.H.ExecuteAsync("CREATE TEMP TRIGGER fail_fresh_long_rest_reservation BEFORE INSERT ON long_rest_sweeps "
            + "BEGIN SELECT RAISE(ABORT, 'fixture fresh reservation failure'); END;");

        string[] before = await SnapshotAsync(f);

        long attemptedChanges = (long)(await f.H.ScalarAsync("SELECT total_changes();"))!;

        try
        {
            Assert.True((await AdmitAsync(f, ownership)).IsFailure);

            // total_changes witnesses successful statements before the injected insert abort;
            // it is not a committed-write count. The exact persisted snapshot proves rollback.
            Assert.True((long)(await f.H.ScalarAsync("SELECT total_changes();"))! > attemptedChanges);

            Assert.Equal(before, await SnapshotAsync(f));

            Assert.False(ownership.IsClaimed(f.Operation));
        }
        finally
        {
            await f.H.ExecuteAsync("DROP TRIGGER fail_fresh_long_rest_reservation;");
        }

        LongRunningOperation fresh = Require(await AdmitAsync(f, ownership));

        Assert.NotEqual(f.Operation, fresh.Id);

        Assert.Equal(LongRunningOperationState.Pending, fresh.State);

        Assert.False(ownership.IsClaimed(f.Operation));

        Assert.Equal(2L, await f.H.ScalarAsync("SELECT count(*) FROM LongRunningOperations WHERE Kind = 'long-rest-sweep';"));
    }

    private static async Task SettleAsync(LongRestMaintenanceTests.MaintenanceFixture f, LongRunningOperationState state)
    {
        LongRunningOperation operation = (await f.Operations.GetAsync(f.Operation))!;

        Assert.True(await f.Operations.TryTransitionAsync(operation.Id, operation.Revision, operation.LeaseOwner,
            state, f.Clock.GetUtcNow(), "long_rest.fixture_terminal"));
    }

    private static async Task<Result<LongRunningOperation>> AdmitAsync(LongRestMaintenanceTests.MaintenanceFixture f,
        LongRunningOperationOwnership ownership)
    {
        FixedCovenantConnectionSource connections = new(f.H.Connection);

        LongRestLiveFenceReader facts = new(f.H.Saga.Context, f.Runtime, f.RecordedAdmission, f.Epoch,
            new CampaignAvailabilityReader(connections), new CampaignPathIdentityReader(connections, null!));

        LongRestSweepFence fence = Require(await facts.CaptureAsync(f.Scope, f.Epoch.Capture().Stamp, null, CancellationToken.None));

        LongRestSweepStore sweeps = new(f.H.Saga.Context, f.Clock);

        return await sweeps.AdmitRequestAsync(f.Partition, fence, facts, ownership, ((ILongRestConfigurationProvider)f.Epoch).Capture().Recipe.Value, CancellationToken.None);
    }

    private static T Require<T>(Result<T> result)
    {
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : "");

        return result.Value;
    }

    private static async Task<string[]> SnapshotAsync(LongRestMaintenanceTests.MaintenanceFixture f)
    {
        // Ordered full-table records detect partial retirement, orphans and evidence changes.
        // Checkpoint null is distinct from a zero-length blob; no record byte[] equality is used.
        string[] queries =
        [
            """
            SELECT json_group_array(json_array(Id, Kind, State, RecoveryPolicy, RootOperationId, ParentOperationId, SessionId,
                RunId, InferenceRunId, BudgetReservationId, IdempotencyClaimId, CreatedAt, StartedAt, HeartbeatAt, CompletedAt,
                LeaseOwner, LeaseExpiresAt, AttemptCount, CheckpointVersion,
                CASE WHEN CheckpointPayload IS NULL THEN NULL ELSE hex(CheckpointPayload) END,
                CheckpointReference, PublicSummary, TerminalErrorCode, Revision))
            FROM (SELECT * FROM LongRunningOperations ORDER BY Id);
            """,
            """
            SELECT json_group_array(json_array(OperationId, GenerationId, ScopeKindCode, CampaignId, hex(PartitionDigest),
                ConfigurationJson, PhaseCode, Revision, CommittedItemCursor, DiscoveryCursor, hex(CommittedWorkDigest), UpdatedAtUtc))
            FROM (SELECT * FROM long_rest_sweeps ORDER BY OperationId);
            """,
            """
            SELECT json_group_array(json_array(GenerationId, OperationId, ScopeKindCode, CampaignId, hex(PartitionDigest),
                InsertionHighWatermark, FenceJson, RecipeJson, hex(RecipeDigest), PhaseCode, Revision, CaptureCursor,
                FoldLayer, FoldGroup, Published, CreatedAtUtc))
            FROM (SELECT * FROM long_rest_discovery_generations ORDER BY GenerationId);
            """,
            """
            SELECT json_group_array(json_array(WorkItemId, hex(WorkDigest), GenerationId, OperationId, KindCode, ItemId,
                hex(SourceDigest), hex(RecipeDigest), Revision, DispositionCode, ReceiptId))
            FROM (SELECT * FROM long_rest_work_items ORDER BY WorkItemId);
            """,
            """
            SELECT json_group_array(json_array(WorkItemId, Ordinal, MemoryId, VersionId, SourceEvidenceJson))
            FROM (SELECT * FROM long_rest_work_inputs ORDER BY WorkItemId, Ordinal);
            """,
            """
            SELECT json_group_array(json_array(WorkItemId, hex(WorkDigest), OriginalWorkJson, AttemptId, PayloadKindCode,
                PhysicalOrdinal, Revision, StateCode, hex(PayloadDigest), hex(DestinationDigest), hex(DisclosureReceiptDigest),
                SentCount, PaidCount, InvalidCount, FailureCount, InputTokens, OutputTokens, TotalTokens, CachedInputTokens,
                ReasoningTokens, CostUsd, CompletionJson, FailureCode, UpdatedAtUtc))
            FROM (SELECT * FROM long_rest_provider_effects ORDER BY WorkItemId);
            """,
            """
            SELECT json_group_array(json_array(WorkItemId, hex(WorkDigest), GenerationId, NodeId, hex(MembershipDigest),
                hex(SourceDigest), hex(RecipeDigest), hex(PayloadDigest), Summary, hex(SummaryDigest), EvidenceJson, CreatedAtUtc))
            FROM (SELECT * FROM long_rest_accepted_summaries ORDER BY WorkItemId);
            """,
        ];

        List<string> snapshot = [];

        foreach (string query in queries)
        {
            snapshot.Add(Assert.IsType<string>(await f.H.ScalarAsync(query)));
        }

        return snapshot.ToArray();
    }
}
