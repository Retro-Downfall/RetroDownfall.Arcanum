using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class BatchAccountingRecoveryStoreTests : IAsyncLifetime
{
    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    public BatchAccountingRecoveryStoreTests(GrimoireFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync()
    {
        _dbPath = _fixture.CopyDatabase();
        _db = _fixture.CreateContext(_dbPath);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_db is not null)
        {
            SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();
            await _db.DisposeAsync();
            SqliteConnection.ClearPool(connection);
        }

        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [SkippableFact]
    public async Task TryRecoverAsync_ReconcilesExactRunningRunsAndIsIdempotent()
    {
        RequireSqlCipher();

        Guid batchId = Guid.NewGuid();
        await InsertBatchAsync(batchId, BatchStatuses.InProgress);

        TurnRunWriter runs = new(_db!);
        Guid runId = await StartBatchRunAsync(runs, batchId);
        Guid secondRunId = await StartBatchRunAsync(runs, batchId);
        BudgetReservation reservation = await ReserveAsync(runId);
        BudgetReservation secondReservation = await ReserveAsync(secondRunId);

        await RecordCostAsync(runs, runId, 0.1000000000000000000000000001m);
        await RecordCostAsync(runs, runId, 0.2000000000000000000000000002m);
        await RecordCostAsync(runs, secondRunId, 0.4m);

        Guid unrelatedRunId = await runs.StartRunAsync(new InferenceRunStart(
            RequestId: $"batch-{Guid.NewGuid():N}",
            SessionId: null,
            Surface: "batch",
            Purpose: "batch",
            IdempotencyClaimId: null,
            StartedAt: DateTimeOffset.UtcNow));

        Guid wrongSurfaceRunId = await runs.StartRunAsync(new InferenceRunStart(
            RequestId: $"batch-{batchId:N}",
            SessionId: null,
            Surface: "chat",
            Purpose: "batch",
            IdempotencyClaimId: null,
            StartedAt: DateTimeOffset.UtcNow));

        Guid wrongPurposeRunId = await runs.StartRunAsync(new InferenceRunStart(
            RequestId: $"batch-{batchId:N}",
            SessionId: null,
            Surface: "batch",
            Purpose: "chat",
            IdempotencyClaimId: null,
            StartedAt: DateTimeOffset.UtcNow));

        BatchAccountingRecoveryStore store = new(_db!, TimeProvider.System);

        BatchAccountingRecoveryClaimStatus recovered = await store
            .ClaimRecoveryAsync(batchId, CancellationToken.None);

        Assert.Equal(BatchAccountingRecoveryClaimStatus.Claimed, recovered);
        Assert.Equal(BatchStatuses.InProgress, await ReadBatchStatusAsync(batchId));
        Assert.Equal(InferenceRunStatus.Abandoned, await ReadRunStatusAsync(runId));
        Assert.Equal(InferenceRunStatus.Abandoned, await ReadRunStatusAsync(secondRunId));
        Assert.Equal(InferenceRunStatus.Running, await ReadRunStatusAsync(unrelatedRunId));
        Assert.Equal(InferenceRunStatus.Running, await ReadRunStatusAsync(wrongSurfaceRunId));
        Assert.Equal(InferenceRunStatus.Running, await ReadRunStatusAsync(wrongPurposeRunId));

        (BudgetReservationStatus status, decimal reconciledUsd) =
            await ReadReservationAsync(reservation.Id);

        Assert.Equal(BudgetReservationStatus.Reconciled, status);
        Assert.Equal(0.3000000000000000000000000003m, reconciledUsd);

        (BudgetReservationStatus secondStatus, decimal secondReconciledUsd) =
            await ReadReservationAsync(secondReservation.Id);

        Assert.Equal(BudgetReservationStatus.Reconciled, secondStatus);
        Assert.Equal(0.4m, secondReconciledUsd);

        BatchRepository batches = new(_db!);

        Assert.False(await batches.TryCompareAndSetStatusAsync(
            batchId,
            BatchStatuses.InProgress,
            BatchStatuses.Cancelled,
            DateTimeOffset.UtcNow,
            outputFileId: null,
            errorFileId: null,
            CancellationToken.None));

        Assert.Equal(BatchStatuses.InProgress, await ReadBatchStatusAsync(batchId));

        Assert.Equal(
            BatchAccountingRecoveryClaimStatus.Resumable,
            await store.ClaimRecoveryAsync(batchId, CancellationToken.None));

        Assert.True(await store.TryCompleteRecoveryAsync(
            batchId,
            BatchAccountingRecoveryTarget.Requeue,
            CancellationToken.None));

        Assert.Equal(BatchStatuses.Validating, await ReadBatchStatusAsync(batchId));
        Assert.Equal(
            BatchAccountingRecoveryClaimStatus.NotRecoverable,
            await store.ClaimRecoveryAsync(batchId, CancellationToken.None));
    }

    [SkippableFact]
    public async Task RecoveryClaimBlocksGeneralStatusAndLineMutations()
    {
        RequireSqlCipher();

        Guid batchId = Guid.NewGuid();
        await InsertBatchAsync(batchId, BatchStatuses.InProgress);

        BatchAccountingRecoveryStore store = new(_db!, TimeProvider.System);

        Assert.Equal(
            BatchAccountingRecoveryClaimStatus.Claimed,
            await store.ClaimRecoveryAsync(batchId, CancellationToken.None));

        BatchRepository batches = new(_db!);

        Assert.False(await batches.TryCompareAndSetStatusAsync(
            batchId,
            BatchStatuses.InProgress,
            BatchStatuses.Cancelled,
            DateTimeOffset.UtcNow,
            outputFileId: null,
            errorFileId: null,
            CancellationToken.None));

        Assert.False(await batches.TryBeginLineAsync(
            batchId,
            lineNumber: 1,
            customId: "provider-line",
            CancellationToken.None));

        Assert.False(await batches.TryRecordTerminalLineAsync(
            batchId,
            lineNumber: 2,
            customId: "local-line",
            BatchLineOutputKind.Error,
            BatchRequestOutcome.Failed,
            "{}",
            CancellationToken.None));

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            batches.UpdateStatusAsync(
                batchId,
                BatchStatuses.Completed,
                DateTimeOffset.UtcNow,
                outputFileId: null,
                errorFileId: null,
                CancellationToken.None));

        Assert.Equal(BatchStatuses.InProgress, await ReadBatchStatusAsync(batchId));
        Assert.Empty(await batches.ListLineCheckpointsAsync(
            batchId,
            firstLine: 1,
            lastLine: 2,
            CancellationToken.None));
    }

    [SkippableFact]
    public async Task TryRecoverAsync_ConcurrentBatchStatusChangeLeavesAccountingUntouched()
    {
        RequireSqlCipher();

        Guid batchId = Guid.NewGuid();
        await InsertBatchAsync(batchId, BatchStatuses.Completed);

        TurnRunWriter runs = new(_db!);
        Guid runId = await StartBatchRunAsync(runs, batchId);
        BudgetReservation reservation = await ReserveAsync(runId);

        BatchAccountingRecoveryStore store = new(_db!, TimeProvider.System);

        Assert.Equal(
            BatchAccountingRecoveryClaimStatus.NotRecoverable,
            await store.ClaimRecoveryAsync(batchId, CancellationToken.None));

        Assert.Equal(InferenceRunStatus.Running, await ReadRunStatusAsync(runId));

        (BudgetReservationStatus status, decimal reconciledUsd) =
            await ReadReservationAsync(reservation.Id);

        Assert.Equal(BudgetReservationStatus.Reserved, status);
        Assert.Equal(0m, reconciledUsd);
    }

    [SkippableFact]
    public async Task TryRecoverAsync_AccountingFailureRollsBackBatchClaimAndEveryDisposition()
    {
        RequireSqlCipher();

        Guid batchId = Guid.NewGuid();
        await InsertBatchAsync(batchId, BatchStatuses.InProgress);

        TurnRunWriter runs = new(_db!);
        Guid runId = await StartBatchRunAsync(runs, batchId);
        BudgetReservation reservation = await ReserveAsync(runId);

        await RecordCostAsync(runs, runId, decimal.MaxValue);
        await RecordCostAsync(runs, runId, 1m);

        BatchAccountingRecoveryStore store = new(_db!, TimeProvider.System);

        _ = await Assert.ThrowsAsync<OverflowException>(() =>
            store.ClaimRecoveryAsync(batchId, CancellationToken.None));

        Assert.Equal(BatchStatuses.InProgress, await ReadBatchStatusAsync(batchId));
        Assert.Equal(InferenceRunStatus.Running, await ReadRunStatusAsync(runId));

        (BudgetReservationStatus status, decimal reconciledUsd) =
            await ReadReservationAsync(reservation.Id);

        Assert.Equal(BudgetReservationStatus.Reserved, status);
        Assert.Equal(0m, reconciledUsd);
    }

    [SkippableFact]
    public async Task OrphanedClaimCannotCompleteAndIsCleanedForTerminalBatch()
    {
        RequireSqlCipher();

        Guid batchId = Guid.NewGuid();
        await InsertBatchAsync(batchId, BatchStatuses.InProgress);

        BatchAccountingRecoveryStore store = new(_db!, TimeProvider.System);

        Assert.Equal(
            BatchAccountingRecoveryClaimStatus.Claimed,
            await store.ClaimRecoveryAsync(batchId, CancellationToken.None));

        await using (DbCommand update = await CreateCommandAsync(
                         "UPDATE \"Batches\" SET \"Status\" = @status WHERE \"Id\" = @id;"))
        {
            AddParameter(update, "@status", BatchStatuses.Completed);
            AddParameter(update, "@id", batchId.ToString("N"));
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        Assert.False(await store.TryCompleteRecoveryAsync(
            batchId,
            BatchAccountingRecoveryTarget.Requeue,
            CancellationToken.None));

        Assert.Equal(1L, await ReadRecoveryClaimCountAsync(batchId));
        Assert.Equal(
            BatchAccountingRecoveryClaimStatus.NotRecoverable,
            await store.ClaimRecoveryAsync(batchId, CancellationToken.None));
        Assert.Equal(0L, await ReadRecoveryClaimCountAsync(batchId));
        Assert.Equal(BatchStatuses.Completed, await ReadBatchStatusAsync(batchId));
    }

    private async Task InsertBatchAsync(Guid batchId, string status)
    {
        await using DbCommand command = await CreateCommandAsync(
            """
            INSERT INTO "Batches"
                ("Id", "InputFileId", "Endpoint", "Status", "CreatedAt")
            VALUES
                (@id, @inputFileId, '/v1/chat/completions', @status, @createdAt);
            """);

        AddParameter(command, "@id", batchId.ToString("N"));
        AddParameter(command, "@inputFileId", Guid.NewGuid().ToString("N"));
        AddParameter(command, "@status", status);
        AddParameter(command, "@createdAt", UtcInstantText.Format(DateTimeOffset.UtcNow));

        _ = await command.ExecuteNonQueryAsync();
    }

    private static Task<Guid> StartBatchRunAsync(TurnRunWriter runs, Guid batchId) =>
        runs.StartRunAsync(new InferenceRunStart(
            RequestId: $"batch-{batchId:N}",
            SessionId: null,
            Surface: "batch",
            Purpose: "batch",
            IdempotencyClaimId: null,
            StartedAt: DateTimeOffset.UtcNow));

    private async Task<BudgetReservation> ReserveAsync(Guid runId)
    {
        BudgetReservationService reservations = new(
            _db!,
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings
            {
                Cost = new CostSettings
                {
                    Budget = new BudgetPolicySettings
                    {
                        Enabled = true,
                        DailyLimitUsd = 100m,
                    },
                },
            }));

        Result<BudgetReservation> result = await reservations.ReserveAsync(
            new BudgetReservationRequest(
                runId,
                ReservedUsd: 10m,
                ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
                BudgetPeriod: BudgetReservationService.UtcBudgetPeriod(DateTimeOffset.UtcNow)));

        Assert.True(result.IsSuccess, result.Error.Message);

        return result.Value;
    }

    private static Task<Guid> RecordCostAsync(TurnRunWriter runs, Guid runId, decimal cost) =>
        runs.RecordBillableOperationAsync(new BillableOperationRecord(
            runId,
            BillableOperationType.Chat,
            Provider: "test",
            Model: "model",
            Purpose: "batch-line",
            StartedAt: DateTimeOffset.UtcNow,
            CompletedAt: DateTimeOffset.UtcNow,
            InputTokens: 0,
            OutputTokens: 0,
            ReasoningTokens: 0,
            CachedTokens: 0,
            PricingSnapshotJson: "{}",
            ActualCostUsd: cost,
            Status: BillableOperationStatus.Completed,
            ProviderRequestId: null));

    private async Task<string> ReadBatchStatusAsync(Guid batchId)
    {
        await using DbCommand command = await CreateCommandAsync(
            "SELECT \"Status\" FROM \"Batches\" WHERE \"Id\" = @id;");
        AddParameter(command, "@id", batchId.ToString("N"));

        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private async Task<long> ReadRecoveryClaimCountAsync(Guid batchId)
    {
        await using DbCommand command = await CreateCommandAsync(
            "SELECT COUNT(*) FROM \"BatchAccountingRecoveryClaims\" WHERE \"BatchId\" = @id;");
        AddParameter(command, "@id", batchId.ToString("N"));

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<InferenceRunStatus> ReadRunStatusAsync(Guid runId)
    {
        await using DbCommand command = await CreateCommandAsync(
            "SELECT \"Status\" FROM \"InferenceRuns\" WHERE \"Id\" = @id;");
        AddParameter(command, "@id", runId.ToString("N"));

        return (InferenceRunStatus)Convert.ToInt32(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<(BudgetReservationStatus Status, decimal ReconciledUsd)> ReadReservationAsync(
        Guid reservationId)
    {
        await using DbCommand command = await CreateCommandAsync(
            "SELECT \"Status\", \"ReconciledUsd\" FROM \"BudgetReservations\" WHERE \"Id\" = @id;");
        AddParameter(command, "@id", reservationId.ToString("N"));

        await using DbDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        return ((BudgetReservationStatus)reader.GetInt32(0), ExactUsdText.Read(reader, 1));
    }

    private async Task<DbCommand> CreateCommandAsync(string commandText)
    {
        DbConnection connection = _db!.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await _db.Database.OpenConnectionAsync();
        }

        DbCommand command = connection.CreateCommand();
        command.CommandText = commandText;

        return command;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        _ = command.Parameters.Add(parameter);
    }

    private static void RequireSqlCipher() =>
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
}
