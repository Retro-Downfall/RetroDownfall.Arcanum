using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Theory]
    [InlineData("InputTokens", "1.5")]
    [InlineData("OutputTokens", "'not-tokens'")]
    [InlineData("Status", "0.5")]
    public async Task Physical_bill_noninteger_evidence_is_refused_without_coercion(string column, string malformed)
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildBudgetLedger ledger = new();

        f.UseBudgetParticipant(ledger);

        GuildQuestStored row = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(row);

        Guid run = Guid.NewGuid(), bill = Guid.NewGuid(), dispatch = Guid.NewGuid();

        Assert.True((await f.Store.PrepareDispatchAsync(driver, dispatch, ReceiptDigest("malformed-bill"), run, false, default)).IsSuccess);

        await using SqliteConnection connection = await f.OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        GuildBudgetEffectRequest request = new(Guid.NewGuid(), row.Quest.Id, dispatch, run,
            GuildBudgetEffectKind.LocalProvider, bill, null, 5m, 20);

        Assert.True((await ledger.PrepareEffectWithinAsync(connection, transaction, request, default)).IsSuccess);

        Assert.True((await ledger.ExposeAsync(connection, transaction, row.Quest.Id, row.AllocationAccountId, dispatch, run, default)).IsSuccess);

        await SeedPhysicalBillAsync(connection, transaction, run, bill);

        await using (SqliteCommand corrupt = connection.CreateCommand())
        {
            corrupt.Transaction = transaction;

            corrupt.CommandText = $"UPDATE BillableOperations SET {column}={malformed} WHERE Id=$bill";

            corrupt.Parameters.AddWithValue("$bill", bill.ToString("N"));

            _ = await corrupt.ExecuteNonQueryAsync();
        }

        Result<GuildBudgetEffect> result = await GuildBudgetPhysicalAccounting.ApplyLocalBillWithinAsync(
            connection, transaction, ledger, request.EffectId, Guid.NewGuid(), default);

        Assert.Equal(ErrorCodes.Guild.InvalidRequest, result.Error.Code);

        GuildBudgetSnapshot after = (await ledger.ReadWithinAsync(connection, transaction, row.AllocationAccountId, default)).Value;

        Assert.Equal(0m, after.KnownUsd);

        Assert.Equal(5m, after.UnresolvedHolds.Usd.Value);
    }

    [Fact]
    public async Task Physical_bill_write_and_guild_receipt_roll_back_together()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildBudgetLedger ledger = new();

        f.UseBudgetParticipant(ledger);

        GuildQuestStored row = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(row);

        Guid run = Guid.NewGuid(), bill = Guid.NewGuid(), dispatch = Guid.NewGuid();

        Assert.True((await f.Store.PrepareDispatchAsync(driver, dispatch, ReceiptDigest("atomic-bill"), run, false, default)).IsSuccess);

        await using SqliteConnection connection = await f.OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        await using (SqliteCommand seed = connection.CreateCommand())
        {
            seed.Transaction = transaction;

            seed.CommandText = "INSERT INTO InferenceRuns(Id,RequestId,Surface,Purpose,StartedAt,Status) VALUES($run,'physical','test','test',$now,0)";

            seed.Parameters.AddWithValue("$run", run.ToString("N"));

            seed.Parameters.AddWithValue("$now", UtcInstantText.Format(DateTimeOffset.UtcNow));

            _ = await seed.ExecuteNonQueryAsync();
        }

        GuildBudgetEffectRequest request = new(Guid.NewGuid(), row.Quest.Id, dispatch, run,
            GuildBudgetEffectKind.LocalProvider, bill, null, 5m, 20);

        Assert.True((await ledger.PrepareEffectWithinAsync(connection, transaction, request, default)).IsSuccess);

        Assert.True((await ledger.ExposeAsync(connection, transaction, row.Quest.Id, row.AllocationAccountId, dispatch, run, default)).IsSuccess);

        BillableOperationRecord actual = new(run, BillableOperationType.Chat, "fixture", "fixture", "test",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 10, 4, 2, 7, "{}", 2.5m, BillableOperationStatus.Completed, null);

        Assert.Equal(bill, await TurnRunWriter.RecordBillableOperationWithinAsync(connection, transaction, bill, actual, default));

        Result<GuildBudgetEffect> applied = await GuildBudgetPhysicalAccounting.ApplyLocalBillWithinAsync(
            connection, transaction, ledger, request.EffectId, Guid.NewGuid(), default);

        Assert.True(applied.IsSuccess, applied.Error.Message);

        await transaction.RollbackAsync();

        Assert.Equal(0L, await f.CountAsync("BillableOperations"));

        Assert.Equal(0L, await f.CountAsync("guild_budget_evidence"));

        Assert.Equal(0L, await f.CountAsync("guild_budget_effects"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Physical_bill_settlement_reconciles_its_one_real_daily_reservation_in_the_evidence_transaction(bool upwardCorrection)
    {
        await using Fixture f = await Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        BudgetReservationService daily = new(db, new TestOptionsMonitor<ArcanumSettings>(new()
        {
            Cost = new CostSettings { Budget = new BudgetPolicySettings { Enabled = true, DailyLimitUsd = 10m } },
        }));

        GuildBudgetLedger ledger = new(daily);

        f.UseBudgetParticipant(ledger);

        GuildQuestStored row = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(row);

        Guid run = Guid.NewGuid(), bill = Guid.NewGuid(), dispatch = Guid.NewGuid();

        Assert.True((await f.Store.PrepareDispatchAsync(driver, dispatch, ReceiptDigest("settle-real-daily"), run, false, default)).IsSuccess);

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        await using (SqliteCommand seed = connection.CreateCommand())
        {
            seed.Transaction = transaction;

            seed.CommandText = "INSERT INTO InferenceRuns(Id,RequestId,Surface,Purpose,StartedAt,Status) VALUES($run,'physical','test','test',$now,0)";

            seed.Parameters.AddWithValue("$run", run.ToString("N"));

            seed.Parameters.AddWithValue("$now", UtcInstantText.Format(DateTimeOffset.UtcNow));

            _ = await seed.ExecuteNonQueryAsync();
        }

        GuildBudgetEffectRequest request = new(Guid.NewGuid(), row.Quest.Id, dispatch, run,
            GuildBudgetEffectKind.LocalProvider, bill, null, 5m, 20);

        Result<GuildBudgetEffect> prepared = await GuildBudgetPhysicalAccounting.PrepareLocalWithinAsync(connection,
            transaction, ledger, daily, request, DateTimeOffset.UtcNow.AddHours(1), default);

        Assert.True(prepared.IsSuccess, prepared.Error.Message);

        Assert.True((await ledger.ExposeAsync(connection, transaction, row.Quest.Id, row.AllocationAccountId, dispatch, run, default)).IsSuccess);

        await SeedPhysicalBillAsync(connection, transaction, run, bill);

        Result<GuildBudgetEffect> applied = await GuildBudgetPhysicalAccounting.ApplyLocalBillWithinAsync(
            connection, transaction, ledger, request.EffectId, Guid.NewGuid(), default, daily);

        Assert.True(applied.IsSuccess, applied.Error.Message);

        if (upwardCorrection)
        {
            await using SqliteCommand correction = connection.CreateCommand();

            correction.Transaction = transaction;

            correction.CommandText = "UPDATE BillableOperations SET ActualCostUsd='6' WHERE Id=$bill";

            correction.Parameters.AddWithValue("$bill", bill.ToString("N"));

            Assert.Equal(1, await correction.ExecuteNonQueryAsync());

            Guid correctionReceipt = Guid.NewGuid();

            Result<GuildBudgetEffect> corrected = await GuildBudgetPhysicalAccounting.ApplyLocalBillWithinAsync(
                connection, transaction, ledger, request.EffectId, correctionReceipt, default, daily);

            Assert.True(corrected.IsSuccess, corrected.Error.Message);

            Assert.True((await GuildBudgetPhysicalAccounting.ApplyLocalBillWithinAsync(
                connection, transaction, ledger, request.EffectId, correctionReceipt, default, daily)).IsSuccess);

            GuildBudgetSnapshot snapshot = (await ledger.ReadWithinAsync(connection, transaction, row.AllocationAccountId, default)).Value;

            Assert.Equal(6m, snapshot.KnownUsd);

            Assert.True(snapshot.CapabilityFault);

            Result<Guid> child = await ledger.AllocateAsync(connection, transaction, Guid.NewGuid(), row.AllocationAccountId,
                f.Host.Grant with { Budget = new(new(GuildBudgetMode.Finite, 1m), new(GuildBudgetMode.Finite, 1)) }, default);

            Assert.Equal(ErrorCodes.Guild.GrantExceeded, child.Error.Code);

            correction.CommandText = "SELECT COUNT(*) FROM BillableOperations WHERE Id=$bill";

            Assert.Equal(1L, await correction.ExecuteScalarAsync());

            correction.CommandText = "SELECT COUNT(*) FROM guild_budget_evidence";

            Assert.Equal(2L, await correction.ExecuteScalarAsync());
        }

        await using SqliteCommand reservation = connection.CreateCommand();

        reservation.Transaction = transaction;

        reservation.CommandText = "SELECT Status,ReconciledUsd FROM BudgetReservations WHERE Id=$id";

        reservation.Parameters.AddWithValue("$id", prepared.Value.Request.ReservationId!.Value.ToString("N"));

        await using SqliteDataReader reader = await reservation.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());

        Assert.Equal(1, reader.GetInt32(0));

        Assert.Equal(upwardCorrection ? 6m : 2.5m, ExactUsdText.Read(reader, 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Physical_preparation_links_only_real_daily_reservations_and_rolls_back_with_its_effect(bool dailyEnabled)
    {
        await using Fixture f = await Fixture.CreateAsync();

        f.Host.Grant = f.Host.Grant with { Budget = new(new(GuildBudgetMode.Finite, 10m), new(GuildBudgetMode.Finite, 100)) };

        GuildBudgetLedger ledger = new();

        f.UseBudgetParticipant(ledger);

        GuildQuestStored row = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request with { Grant = f.Host.Grant }, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(row);

        Guid run = Guid.NewGuid(), dispatch = Guid.NewGuid();

        Assert.True((await f.Store.PrepareDispatchAsync(driver, dispatch, ReceiptDigest("daily-physical"), run, false, default)).IsSuccess);

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        BudgetReservationService daily = new(db, new TestOptionsMonitor<ArcanumSettings>(new()
        {
            Cost = new CostSettings { Budget = new BudgetPolicySettings { Enabled = dailyEnabled, DailyLimitUsd = 10m } },
        }));

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        await using (SqliteCommand seed = connection.CreateCommand())
        {
            seed.Transaction = transaction;

            seed.CommandText = "INSERT INTO InferenceRuns(Id,RequestId,Surface,Purpose,StartedAt,Status) VALUES($run,'physical','test','test',$now,0)";

            seed.Parameters.AddWithValue("$run", run.ToString("N"));

            seed.Parameters.AddWithValue("$now", UtcInstantText.Format(DateTimeOffset.UtcNow));

            _ = await seed.ExecuteNonQueryAsync();
        }

        GuildBudgetEffectRequest request = new(Guid.NewGuid(), row.Quest.Id, dispatch, run,
            GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, 3m, 30);

        Result<GuildBudgetEffect> prepared = await GuildBudgetPhysicalAccounting.PrepareLocalWithinAsync(connection,
            transaction, ledger, daily, request, DateTimeOffset.UtcNow.AddHours(1), default);

        Assert.True(prepared.IsSuccess, prepared.Error.Message);

        Assert.Equal(dailyEnabled, prepared.Value.Request.ReservationId is not null);

        Result<GuildBudgetEffect> replay = await GuildBudgetPhysicalAccounting.PrepareLocalWithinAsync(connection,
            transaction, ledger, daily, request, DateTimeOffset.UtcNow.AddHours(1), default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(prepared.Value.Request.ReservationId, replay.Value.Request.ReservationId);

        Assert.Equal(7m, (await ledger.ReadWithinAsync(connection, transaction, row.AllocationAccountId, default)).Value.Remaining.Usd.Value);

        await transaction.RollbackAsync();

        Assert.Equal(0L, await f.CountAsync("BudgetReservations"));

        Assert.Equal(0L, await f.CountAsync("guild_budget_effects"));

        Assert.Equal(1L, await f.CountAsync("guild_budget_accounts"));
    }

    [Fact]
    public async Task Physical_bill_is_copied_once_without_cached_or_reasoning_token_duplication_and_survives_run_retention()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildBudgetLedger ledger = new();

        f.UseBudgetParticipant(ledger);

        GuildQuestStored row = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(row);

        Guid run = Guid.NewGuid(), bill = Guid.NewGuid(), dispatch = Guid.NewGuid();

        Assert.True((await f.Store.PrepareDispatchAsync(driver, dispatch, ReceiptDigest("physical-bill"), run, false, default)).IsSuccess);

        GuildBudgetEffectRequest request = new(Guid.NewGuid(), row.Quest.Id, dispatch, run,
            GuildBudgetEffectKind.LocalProvider, bill, null, 5m, 20);

        await using SqliteConnection connection = await f.OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        Assert.True((await ledger.PrepareEffectWithinAsync(connection, transaction, request, default)).IsSuccess);

        Assert.True((await ledger.ExposeAsync(connection, transaction, row.Quest.Id, row.AllocationAccountId, dispatch, run, default)).IsSuccess);

        await SeedPhysicalBillAsync(connection, transaction, run, bill);

        Guid receipt = Guid.NewGuid();

        Result<GuildBudgetEffect> applied = await GuildBudgetPhysicalAccounting.ApplyLocalBillWithinAsync(
            connection, transaction, ledger, request.EffectId, receipt, default);

        Assert.True(applied.IsSuccess, applied.Error.Message);

        Assert.Equal(2.5m, applied.Value.KnownUsd);

        Assert.Equal(14L, applied.Value.KnownTokens);

        Assert.False(applied.Value.UnknownUsd);

        Assert.Equal(applied.Value.Revision, (await GuildBudgetPhysicalAccounting.ApplyLocalBillWithinAsync(
            connection, transaction, ledger, request.EffectId, receipt, default)).Value.Revision);

        await using SqliteCommand delete = connection.CreateCommand();

        delete.Transaction = transaction;

        delete.CommandText = "DELETE FROM InferenceRuns WHERE Id=$run";

        delete.Parameters.AddWithValue("$run", run.ToString("N"));

        Assert.Equal(1, await delete.ExecuteNonQueryAsync());

        GuildBudgetSnapshot afterRetention = (await ledger.ReadWithinAsync(connection, transaction, row.AllocationAccountId, default)).Value;

        Assert.Equal(2.5m, afterRetention.KnownUsd);

        Assert.Equal(14L, afterRetention.KnownTokens);

        Assert.Equal(1L, afterRetention.PricedCount);

        await transaction.CommitAsync();

        Assert.Equal(1L, await f.CountAsync("guild_budget_evidence"));
    }

    [Fact]
    public async Task Physical_bill_from_another_run_cannot_settle_the_quest_hold()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildBudgetLedger ledger = new();

        f.UseBudgetParticipant(ledger);

        GuildQuestStored row = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(row);

        Guid run = Guid.NewGuid(), bill = Guid.NewGuid(), dispatch = Guid.NewGuid();

        Assert.True((await f.Store.PrepareDispatchAsync(driver, dispatch, ReceiptDigest("wrong-run"), run, false, default)).IsSuccess);

        GuildBudgetEffectRequest request = new(Guid.NewGuid(), row.Quest.Id, dispatch, run,
            GuildBudgetEffectKind.LocalProvider, bill, null, 5m, 20);

        await using SqliteConnection connection = await f.OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        Assert.True((await ledger.PrepareEffectWithinAsync(connection, transaction, request, default)).IsSuccess);

        await SeedPhysicalBillAsync(connection, transaction, Guid.NewGuid(), bill);

        Result<GuildBudgetEffect> result = await GuildBudgetPhysicalAccounting.ApplyLocalBillWithinAsync(
            connection, transaction, ledger, request.EffectId, Guid.NewGuid(), default);

        Assert.Equal(ErrorCodes.Guild.Forbidden, result.Error.Code);

        Assert.Equal(5m, (await ledger.ReadWithinAsync(connection, transaction, row.AllocationAccountId, default)).Value.UnresolvedHolds.Usd.Value);
    }

    private static async Task SeedPhysicalBillAsync(SqliteConnection connection, SqliteTransaction transaction, Guid run, Guid bill)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO InferenceRuns(Id,RequestId,Surface,Purpose,StartedAt,Status)
                VALUES($run,'budget-test','test','test','2026-01-01T00:00:00.0000000Z',1)
                ON CONFLICT(Id) DO UPDATE SET Status=1;
            INSERT INTO BillableOperations(Id,RunId,OperationType,Provider,Model,Purpose,StartedAt,CompletedAt,
                InputTokens,OutputTokens,ReasoningTokens,CachedTokens,PricingSnapshotJson,ActualCostUsd,Status)
                VALUES($bill,$run,0,'fixture','fixture','test','2026-01-01T00:00:00.0000000Z',
                    '2026-01-01T00:00:00.0000000Z',10,4,2,7,'{}','2.5',0);
            """;

        command.Parameters.AddWithValue("$run", run.ToString("N"));

        command.Parameters.AddWithValue("$bill", bill.ToString("N"));

        _ = await command.ExecuteNonQueryAsync();
    }
}
