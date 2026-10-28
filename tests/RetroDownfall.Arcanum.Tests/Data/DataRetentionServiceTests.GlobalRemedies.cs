using System.Reflection;

using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableTheory]
    [InlineData(LongRunningOperationKinds.DataRetentionMutation, 4, 1, null)]
    [InlineData(LongRunningOperationKinds.DataRetentionFactoryReset, 2, 1, null)]
    [InlineData(LongRunningOperationKinds.DataRetentionMutation, 4, 7, "Data.ReconciliationFailed")]
    [InlineData(LongRunningOperationKinds.DataRetentionFactoryReset, 2, 7, "Data.ReconciliationFailed")]
    [InlineData(LongRunningOperationKinds.DataRetentionMutation, 4, 7, "Covenant.MaintenanceFailed")]
    [InlineData(LongRunningOperationKinds.DataRetentionFactoryReset, 2, 7, "Covenant.MaintenanceFailed")]
    public async Task Global_reset_owner_bound_conflict_preserves_exact_owner_recovery_guidance(
        string kind, int version, int state, string? terminalCode)
    {
        RequireSqlCipher();

        Guid id = await SeedGlobalRemedyOperationAsync(kind, version, state, terminalCode);

        LongRunningOperationStore store = new(_db!, TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation? before = await store.GetAsync(id);

        DataRetentionPlan plan = await CreateService().PlanAsync(new(DataRetentionOperation.FactoryReset));

        DataRetentionConflict conflict = Assert.Single(plan.Conflicts, row => row.ResourceId == id.ToString("D"));

        Assert.Equal("Data.ActiveOperation", conflict.Code);

        Assert.Contains("authenticated offline transition", conflict.Message, StringComparison.Ordinal);

        Assert.Contains("same operation", conflict.Message, StringComparison.Ordinal);

        Assert.Contains("does not repair", conflict.Message, StringComparison.Ordinal);

        Assert.Contains(id.ToString("D"), conflict.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("operation cancel", conflict.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("operation retry", conflict.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("private-stored-content", conflict.Message, StringComparison.Ordinal);

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            MethodInfo recheck = typeof(DataRetentionService).GetMethod("ReadFactoryConflictsInTransactionAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            Guid excluded = Guid.NewGuid();

            Task<DataRetentionConflict[]> read = Assert.IsType<Task<DataRetentionConflict[]>>(
                recheck.Invoke(CreateService(), [connection, transaction, excluded, CancellationToken.None]), exactMatch: false);

            DataRetentionConflict checkedConflict = Assert.Single(await read, row => row.ResourceId == id.ToString("D"));

            Assert.Equal(conflict, checkedConflict);

            Task<DataRetentionConflict[]> excludedRead = Assert.IsType<Task<DataRetentionConflict[]>>(
                recheck.Invoke(CreateService(), [connection, transaction, id, CancellationToken.None]), exactMatch: false);

            Assert.DoesNotContain(await excludedRead, row => row.ResourceId == id.ToString("D"));

            await transaction.RollbackAsync();
        }

        LongRunningOperation after = Assert.IsType<LongRunningOperation>(await store.GetAsync(id));

        Assert.Equal(before! with { CheckpointPayload = null }, after with { CheckpointPayload = null });

        Assert.Equal(before.CheckpointPayload, after.CheckpointPayload);
    }

    [SkippableTheory]
    [InlineData(LongRunningOperationKinds.LongRestSweep, 0, 0, null, "Pending")]
    [InlineData(LongRunningOperationKinds.Subagent, 0, 1, null, "operation show")]
    [InlineData(LongRunningOperationKinds.DataRetentionPrune, 3, 7, "Data.ReconciliationFailed", "owning recovery")]
    [InlineData(LongRunningOperationKinds.LongRestSweep, 1, 7, "LongRest.TransportUnknown", "Unknown provider usage")]
    [InlineData(LongRunningOperationKinds.LongRestSweep, 1, 7, "long_rest.authorized_request_required", "fresh eligible authenticated Session")]
    [InlineData(LongRunningOperationKinds.LongRestSweep, 1, 7, "long_rest.interrupted", "hosted worker")]
    [InlineData("private-stored-content-unknown-kind", 73, 7, "private-stored-content-error", "unsupported")]
    [InlineData(LongRunningOperationKinds.DataRetentionMutation, 99, 7, "Data.ReconciliationFailed", "unsupported")]
    public async Task Global_reset_guidance_respects_recorded_recovery_shape(
        string kind, int version, int state, string? terminalCode, string expected)
    {
        RequireSqlCipher();

        Guid id = await SeedGlobalRemedyOperationAsync(kind, version, state, terminalCode);

        DataRetentionPlan plan = await CreateService().PlanAsync(new(DataRetentionOperation.FactoryReset));

        DataRetentionConflict conflict = Assert.Single(plan.Conflicts, row => row.ResourceId == id.ToString("D"));

        Assert.Contains(expected, conflict.Message, StringComparison.Ordinal);

        Assert.Contains(id.ToString("D"), conflict.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("operation retry", conflict.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("private-stored-content", conflict.Message, StringComparison.Ordinal);

        if (state == 0)
        {
            Assert.Equal("Data.OperationNeedsAttention", conflict.Code);

            Assert.Contains("operation cancel", conflict.Message, StringComparison.Ordinal);
        }
    }

    [SkippableTheory]
    [InlineData(LongRunningOperationKinds.DataRetentionMutation, 4)]
    [InlineData(LongRunningOperationKinds.DataRetentionFactoryReset, 2)]
    public async Task Codec_valid_owner_checkpoint_remains_diagnostic_evidence_without_generic_mutation_authority(string kind, int version)
    {
        RequireSqlCipher();

        Guid id = await SeedGlobalRemedyOperationAsync(kind, version, 1, null);

        Guid source = Guid.NewGuid();

        Guid target = Guid.NewGuid();

        CovenantOfflineTransitionEpochsV1 epochs = new(1, 2, 3);

        CovenantOfflineTransitionEpochsV1 successor = new(2, 3, 4);

        byte[] payload;

        if (kind == LongRunningOperationKinds.DataRetentionMutation)
        {
            payload = CovenantRecoveryCheckpointCodec.Encode(new CovenantOfflineTransitionLaunchV4(
                4, id, kind, nameof(LongRunningOperationRecoveryPolicy.ReconcileAndComplete),
                CovenantExclusiveOperation.CovenantReset, new string('a', 64), source, target, epochs, successor, 0));

            Assert.True(CovenantRecoveryCheckpointCodec.DecodeCovenantOfflineTransitionLaunch(payload).IsSuccess);
        }
        else
        {
            payload = CovenantRecoveryCheckpointCodec.Encode(new DataRetentionFactoryTransitionLaunchV2(
                2, id, kind, nameof(LongRunningOperationRecoveryPolicy.RestartIdempotently),
                CovenantExclusiveOperation.HealthyCatalogFactoryErasure, new string('a', 64), source, target, epochs, successor, 0));

            Assert.True(CovenantRecoveryCheckpointCodec.DecodeDataRetentionFactoryTransitionLaunch(payload).IsSuccess);
        }

        await ExecuteAsync("UPDATE LongRunningOperations SET CheckpointPayload = @payload WHERE Id = @id",
            ("@payload", payload), ("@id", id.ToString("N")));

        DataRetentionPlan plan = await CreateService().PlanAsync(new(DataRetentionOperation.FactoryReset));

        DataRetentionConflict conflict = Assert.Single(plan.Conflicts, row => row.ResourceId == id.ToString("D"));

        Assert.Contains("authenticated offline transition", conflict.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("operation cancel", conflict.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("operation retry", conflict.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(source.ToString("D"), conflict.Message, StringComparison.Ordinal);

        LongRunningOperationStore store = new(_db!, TestOrdinaryConnectionFactory.For(_db!));

        Assert.Equal(payload, (await store.GetAsync(id))!.CheckpointPayload);
    }

    [SkippableFact]
    public async Task Native_bounded_Session_conflict_projection_preserves_each_exact_wire_reason()
    {
        RequireSqlCipher();

        (Guid sessionId, _) = await SeedSessionAsync(pinned: false);

        Guid operationId = await SeedGlobalRemedyOperationAsync(LongRunningOperationKinds.Subagent, 0, 1, null);

        Guid runId = Guid.NewGuid();

        Guid reservationId = Guid.NewGuid();

        await ExecuteAsync("UPDATE LongRunningOperations SET SessionId = @session WHERE Id = @id",
            ("@session", sessionId.ToString("N")), ("@id", operationId.ToString("N")));

        await ExecuteAsync(
            """
            INSERT INTO InferenceRuns (Id, RequestId, SessionId, Surface, Purpose, StartedAt, Status)
            VALUES (@id, 'native-reason-projection', @session, 'chat', 'chat', @now, 0)
            """,
            ("@id", runId.ToString("N")), ("@session", sessionId.ToString("N")), ("@now", OldTimestamp));

        await ExecuteAsync(
            """
            INSERT INTO BudgetReservations (Id, RunId, BudgetPeriod, Status, ExpiresAt, CreatedAt, UpdatedAt)
            VALUES (@id, @run, '2026-10-16', 0, @now, @now, @now)
            """,
            ("@id", reservationId.ToString("N")), ("@run", runId.ToString("N")), ("@now", OldTimestamp));

        List<DataRetentionConflict> conflicts = [];

        MethodInfo projection = typeof(DataRetentionService).GetMethod("AddBoundedSessionConflictDiagnosticsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        Task<int> read = Assert.IsType<Task<int>>(projection.Invoke(CreateService(),
            [sessionId, 3, conflicts, CancellationToken.None]), exactMatch: false);

        Assert.Equal(0, await read);

        Assert.Equal(new[]
        {
            "Data.ActiveOperation:" + operationId.ToString("D"),
            "Data.InferenceRunActive:" + runId.ToString("D"),
            "Data.BudgetReservationOutstanding:" + reservationId.ToString("D"),
        }, conflicts.Select(row => row.Code + ":" + row.ResourceId));
    }

    [SkippableTheory]
    [InlineData(LongRunningOperationKinds.DataRetentionMutation, 4, 1, null)]
    [InlineData(LongRunningOperationKinds.DataRetentionFactoryReset, 2, 7, "Data.ReconciliationFailed")]
    [InlineData(LongRunningOperationKinds.LongRestSweep, 0, 0, null)]
    [InlineData(LongRunningOperationKinds.LongRestSweep, 1, 7, "LongRest.TransportUnknown")]
    [InlineData(LongRunningOperationKinds.LongRestSweep, 1, 7, "long_rest.authorized_request_required")]
    [InlineData("private-stored-content-unsupported", 99, 7, null)]
    [InlineData(LongRunningOperationKinds.Subagent, 0, 1, null)]
    [InlineData(LongRunningOperationKinds.DataRetentionPrune, 3, 7, "Data.ReconciliationFailed")]
    public async Task Global_conflict_orders_conditional_host_start_before_HTTP_inspection_and_actions(
        string kind, int version, int state, string? terminalCode)
    {
        RequireSqlCipher();

        Guid id = await SeedGlobalRemedyOperationAsync(kind, version, state, terminalCode);

        DataRetentionPlan plan = await CreateService().PlanAsync(new(DataRetentionOperation.FactoryReset));

        string message = Assert.Single(plan.Conflicts, row => row.ResourceId == id.ToString("D")).Message;

        Assert.Contains("If the host is stopped", message, StringComparison.Ordinal);

        int start = message.IndexOf("start it with 'arcanum serve'", StringComparison.Ordinal);

        int inspect = message.IndexOf("arcanum operation show", StringComparison.Ordinal);

        Assert.True(start >= 0 && inspect > start, message);

        int action = message.IndexOf("arcanum operation cancel", StringComparison.Ordinal);

        Assert.True(action < 0 || action > start, message);

        Assert.Contains("If startup cannot serve", message, StringComparison.Ordinal);

        Assert.Contains("preserve", message, StringComparison.Ordinal);
    }

    private async Task<Guid> SeedGlobalRemedyOperationAsync(string kind, int version, int state, string? terminalCode)
    {
        Guid id = Guid.NewGuid();

        await ExecuteAsync(
            """
            INSERT INTO LongRunningOperations
                (Id, Kind, State, RecoveryPolicy, CreatedAt, PublicSummary, AttemptCount,
                 CheckpointVersion, CheckpointPayload, CheckpointReference, TerminalErrorCode, Revision)
            VALUES (@id, @kind, @state, 1, @now, 'private-stored-content-summary', 0,
                    @version, @payload, 'private-stored-content-reference', @error, 0)
            """,
            ("@id", id.ToString("N")),
            ("@kind", kind),
            ("@state", state),
            ("@now", "2026-10-16T00:00:00.0000000Z"),
            ("@version", version),
            ("@payload", new byte[] { 1, 2, 3 }),
            ("@error", (object?)terminalCode ?? DBNull.Value));

        return id;
    }
}
