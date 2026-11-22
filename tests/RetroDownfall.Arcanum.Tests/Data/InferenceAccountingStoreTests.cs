using System.Data.Common;
using RetroDownfall.Arcanum.Api.Intelligence;
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
public sealed class InferenceAccountingStoreTests : IAsyncLifetime
{
    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    public InferenceAccountingStoreTests(GrimoireFixture fixture)
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
            SqliteConnection connection =
                (SqliteConnection)_db.Database.GetDbConnection();
            await _db.DisposeAsync();
            SqliteConnection.ClearPool(connection);
        }

        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [SkippableFact]
    public async Task OrdinaryRunPersistsWhileTheLegacyReservationStoreWouldRefuse()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        TurnRunWriter writer = new(_db!);

        DbConnection connection = _db!.Database.GetDbConnection();

        await using (DbCommand trigger = connection.CreateCommand())
        {
            trigger.CommandText = "CREATE TEMP TRIGGER FailNewReservation BEFORE INSERT ON BudgetReservations BEGIN SELECT RAISE(ABORT, 'unexpected reservation'); END;";

            await trigger.ExecuteNonQueryAsync();
        }

        TurnAccountingHandle handle = await TurnAccountingHandle.BeginAsync(writer, null,
            "test", "terminal-disposition", "ordinary-accounting", CancellationToken.None);

        await handle.CompleteAsync(writer, InferenceRunStatus.Completed, CancellationToken.None);

        await using DbCommand state = connection.CreateCommand();

        state.CommandText = "SELECT run.Status, run.CompletedAt, (SELECT count(*) FROM BudgetReservations WHERE RunId = run.Id) FROM InferenceRuns run WHERE run.Id = @id";

        DbParameter id = state.CreateParameter();

        id.ParameterName = "@id";

        id.Value = handle.RunId!.Value.ToString("N");

        state.Parameters.Add(id);

        await using DbDataReader row = await state.ExecuteReaderAsync();

        Assert.True(await row.ReadAsync());

        Assert.Equal((int)InferenceRunStatus.Completed, row.GetInt32(0));

        Assert.False(row.IsDBNull(1));

        Assert.Equal(0L, row.GetInt64(2));
    }

    [SkippableFact]
    public async Task ClaimStore_FingerprintMismatch_ReturnsConflict()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        IdempotencyClaimStore store = new(_db!);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        IdempotencyClaimAcquireResult first = await store.TryAcquireAsync(
            new IdempotencyClaimAcquireRequest("claim-a", "fp-1", "owner-1", now.AddMinutes(5), now));

        Assert.True(first.Acquired);

        IdempotencyClaimAcquireResult conflict = await store.TryAcquireAsync(
            new IdempotencyClaimAcquireRequest("claim-a", "fp-2", "owner-2", now.AddMinutes(5), now));

        Assert.True(conflict.Conflict);
        Assert.False(conflict.Acquired);
    }

    [SkippableFact]
    public async Task ClaimStore_Complete_RequiresTerminalAndReplays()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        IdempotencyClaimStore store = new(_db!);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        IdempotencyClaimAcquireResult acquired = await store.TryAcquireAsync(
            new IdempotencyClaimAcquireRequest("claim-complete", "fp-c", "owner", now.AddMinutes(5), now));

        Assert.True(acquired.Acquired);

        await store.CompleteAsync(
            acquired.Claim.Id,
            "owner",
            200,
            "application/json",
            "{\"partial\":true}",
            terminalStreamValid: false,
            runId: null);

        IdempotencyClaim? abandoned = await store.TryGetAsync("claim-complete");

        Assert.NotNull(abandoned);
        Assert.Equal(IdempotencyClaimState.Abandoned, abandoned.State);

        IdempotencyClaimAcquireResult reacquired = await store.TryAcquireAsync(
            new IdempotencyClaimAcquireRequest("claim-complete", "fp-c", "owner-2", now.AddMinutes(5), now));

        Assert.True(reacquired.Acquired);

        await store.CompleteAsync(
            reacquired.Claim.Id,
            "owner-2",
            200,
            "application/json",
            "{\"ok\":true}",
            terminalStreamValid: true,
            runId: null);

        IdempotencyClaim? loaded = await store.TryGetAsync("claim-complete");

        Assert.NotNull(loaded);
        Assert.Equal(IdempotencyClaimState.Completed, loaded.State);
        Assert.True(loaded.TerminalStreamComplete);
        Assert.Equal("{\"ok\":true}", loaded.ResponseBody);
    }

    [SkippableFact]
    public async Task TurnRunWriter_RecordsBillableOperation()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        TurnRunWriter runs = new(_db!);

        Guid runId = await runs.StartRunAsync(new InferenceRunStart(
            RequestId: "bill-1",
            SessionId: null,
            Surface: "test",
            Purpose: "chat",
            IdempotencyClaimId: null,
            StartedAt: DateTimeOffset.UtcNow));

        Guid opId = await runs.RecordBillableOperationAsync(new BillableOperationRecord(
            runId,
            BillableOperationType.Chat,
            Provider: "test",
            Model: "m",
            Purpose: "chat",
            StartedAt: DateTimeOffset.UtcNow,
            CompletedAt: DateTimeOffset.UtcNow,
            InputTokens: 10,
            OutputTokens: 5,
            ReasoningTokens: 3,
            CachedTokens: 2,
            PricingSnapshotJson: """{"InputPer1M":1,"OutputPer1M":2,"ReasoningPer1M":3,"CachedPer1M":0.5}""",
            ActualCostUsd: 0.01m,
            Status: BillableOperationStatus.Completed,
            ProviderRequestId: null));

        Assert.NotEqual(Guid.Empty, opId);

        DbConnection connection = _db!.Database.GetDbConnection();
        await using (DbCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """SELECT "ReasoningTokens" FROM "BillableOperations" WHERE "Id" = @id;""";
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = "@id";
            parameter.Value = opId.ToString("N");
            command.Parameters.Add(parameter);

            object? stored = await command.ExecuteScalarAsync();

            Assert.Equal(3L, Convert.ToInt64(stored, System.Globalization.CultureInfo.InvariantCulture));
        }

        await runs.CompleteRunAsync(runId, InferenceRunStatus.Completed);

        BudgetReservationService reservations = new(_db!);

        decimal committed = await reservations.GetTodayCommittedSpendAsync();

        Assert.Equal(0.01m, committed);
    }
}
