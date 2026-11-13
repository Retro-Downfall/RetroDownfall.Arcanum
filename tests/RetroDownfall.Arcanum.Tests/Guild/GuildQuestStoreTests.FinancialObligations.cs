using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Unknown_complete_effect_blocks_only_finite_axes_even_when_residual_hold_is_zero(bool finiteUsd, bool finiteTokens)
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildBudgetLedger ledger = new();

        f.UseBudgetParticipant(ledger);

        GuildBudgetGrant grant = new(finiteUsd ? new(GuildBudgetMode.Finite, 1m) : new(GuildBudgetMode.Unlimited),
            finiteTokens ? new(GuildBudgetMode.Finite, 10) : new(GuildBudgetMode.Unlimited));

        f.Host.Grant = f.Request.Grant with { Budget = grant };

        var issued = await f.Store.IssueAsync(f.Caller, f.Identity, f.Request with { Grant = f.Host.Grant }, default);

        Assert.True(issued.IsSuccess, issued.Error.Message);

        GuildQuestStored row = issued.Value;

        GuildQuestDriver driver = await f.DriverAsync(row);

        Guid dispatch = Guid.NewGuid(), accounting = Guid.NewGuid();

        Assert.True((await f.Store.PrepareDispatchAsync(driver, dispatch, ReceiptDigest("finite-axes"), accounting, false, default)).IsSuccess);

        await using SqliteConnection connection = await f.OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        GuildBudgetEffectRequest request = new(Guid.NewGuid(), row.Quest.Id, dispatch, accounting,
            GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, 1m, 10);

        var prepared = await ledger.PrepareEffectWithinAsync(connection, transaction, request, default);

        Assert.True(prepared.IsSuccess, prepared.Error.Message);

        Assert.True((await ledger.ExposeAsync(connection, transaction, row.Quest.Id, row.AllocationAccountId, dispatch, accounting, default)).IsSuccess);

        Assert.True((await ledger.ApplyEvidenceWithinAsync(connection, transaction, request.EffectId,
            new(Guid.NewGuid(), ReceiptDigest("finite-axes-evidence"), 1m, 10, true, true, true), default)).IsSuccess);

        var read = await ledger.ReadWithinAsync(connection, transaction, row.AllocationAccountId, default);

        Assert.True(read.IsSuccess, read.Error.Message);

        Assert.True(read.Value.UnknownUsd);

        Assert.True(read.Value.UnknownTokens);

        Assert.False(read.Value.PhysicalBlocked);

        Assert.Equal(finiteUsd, read.Value.UsdBlocked);

        Assert.Equal(finiteTokens, read.Value.TokensBlocked);

        Assert.Equal(0m, read.Value.UnresolvedHolds.Usd.Value);

        Assert.Equal(0L, read.Value.UnresolvedHolds.Tokens.Value);

        Guid resultId = Guid.NewGuid();

        await using (SqliteCommand terminal = connection.CreateCommand())
        {
            terminal.Transaction = transaction;

            terminal.CommandText = """
                INSERT INTO guild_result_receipts(QuestId,ResultId,Digest,OutcomeCode,NoDispatchProven,WorkStoppedProven,MutationId,AcceptedRevision,CreatedAtUtc)
                VALUES($quest,$result,zeroblob(32),3,0,1,$mutation,1,'2026-01-01T00:00:00.0000000Z')
                """;

            terminal.Parameters.AddWithValue("$quest", row.Quest.Id.ToString("D").ToUpperInvariant());

            terminal.Parameters.AddWithValue("$result", resultId.ToString("D").ToUpperInvariant());

            terminal.Parameters.AddWithValue("$mutation", Guid.NewGuid().ToString("D").ToUpperInvariant());

            Assert.Equal(1, await terminal.ExecuteNonQueryAsync());
        }

        GuildResultReceipt receipt = new(row.Quest.Id, resultId, ReceiptDigest("terminal"), null, QuestState.Completed, null);

        var settled = await ledger.SettleAsync(connection, transaction, row.Quest.Id, row.AllocationAccountId, receipt, default);

        Assert.True(settled.IsSuccess, settled.Error.Message);

        Assert.Equal(finiteUsd || finiteTokens ? GuildAccountingState.Uncertain : GuildAccountingState.Settled, settled.Value);

        await using SqliteCommand corrupt = connection.CreateCommand();

        corrupt.Transaction = transaction;

        corrupt.CommandText = "DELETE FROM guild_budget_evidence WHERE EffectId=$effect";

        corrupt.Parameters.AddWithValue("$effect", request.EffectId.ToString("D").ToUpperInvariant());

        Assert.Equal(1, await corrupt.ExecuteNonQueryAsync());

        Assert.True((await ledger.ReadWithinAsync(connection, transaction, row.AllocationAccountId, default)).IsFailure);
    }
    [Fact]
    public void Physical_obligations_separate_known_usage_bounds_and_financial_axes()
    {
        var unbounded = GuildBudgetObligations.Classify(GuildBudgetEffectState.Prepared, false, false,
            false, false, false, null, null, 0m, 0);

        Assert.True(unbounded.PhysicalPending && unbounded.UsdBlocked && unbounded.TokensBlocked);

        Assert.Null(unbounded.HoldUsd);

        Assert.Null(unbounded.HoldTokens);

        var exposed = GuildBudgetObligations.Classify(GuildBudgetEffectState.PossiblyDispatched, false, false,
            false, false, false, 2m, 10, 0.5m, 2);

        Assert.True(exposed.PhysicalPending && exposed.UsdBlocked && exposed.TokensBlocked);

        Assert.Equal(1.5m, exposed.HoldUsd);

        Assert.Equal(8, exposed.HoldTokens);

        var linked = GuildBudgetObligations.Classify(GuildBudgetEffectState.Complete, false, false,
            true, true, false, 2m, null, 0.5m, 0);

        Assert.False(linked.PhysicalPending);

        Assert.True(linked.UsdBlocked);

        Assert.False(linked.TokensBlocked);

        Assert.Equal(1.5m, linked.HoldUsd);

        var finiteTokens = GuildBudgetObligations.Classify(GuildBudgetEffectState.Complete, false, true,
            false, false, true, null, 10, 0m, 2);

        Assert.False(finiteTokens.UsdBlocked);

        Assert.True(finiteTokens.TokensBlocked);

        Assert.Equal(8, finiteTokens.HoldTokens);

        var unrestricted = GuildBudgetObligations.Classify(GuildBudgetEffectState.Complete, false, false,
            false, true, true, null, null, 0m, 0);

        Assert.False(unrestricted.PhysicalPending || unrestricted.UsdBlocked || unrestricted.TokensBlocked);

        Assert.Equal(0m, unrestricted.HoldUsd);

        Assert.Equal(0, unrestricted.HoldTokens);

        var unsent = GuildBudgetObligations.Classify(GuildBudgetEffectState.ProvenNoDispatch, true, true,
            true, false, false, 2m, 10, 0m, 0);

        Assert.False(unsent.PhysicalPending || unsent.UsdBlocked || unsent.TokensBlocked);
    }

    [Fact]
    public async Task Reservation_row_validation_rejects_wrong_identity_amount_and_disposition()
    {
        await using Fixture f = await Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        const string reservation = "11111111-1111-1111-1111-111111111111";

        const string run = "22222222-2222-2222-2222-222222222222";

        const string other = "33333333-3333-3333-3333-333333333333";

        (string Name, Action<object[]> Change, bool Expected)[] cases =
        [
            ("complete", _ => { }, true),
            ("missing actual reservation", row => row[7] = DBNull.Value, false),
            ("missing actual run", row => row[8] = DBNull.Value, false),
            ("missing status", row => row[9] = DBNull.Value, false),
            ("missing reserved", row => row[10] = DBNull.Value, false),
            ("missing reconciled", row => row[11] = DBNull.Value, false),
            ("wrong storage types", row => row[12] = 0L, false),
            ("malformed reservation", row => row[1] = "bad", false),
            ("empty reservation", row => row[1] = Guid.Empty.ToString("D"), false),
            ("malformed run", row => row[2] = "bad", false),
            ("empty run", row => row[2] = Guid.Empty.ToString("D"), false),
            ("wrong reservation", row => row[7] = other, false),
            ("wrong run", row => row[8] = other, false),
            ("missing bound", row => row[4] = DBNull.Value, false),
            ("negative bound", row => row[4] = "-1", false),
            ("negative known", row => row[5] = "-1", false),
            ("reserved below bound", row => row[10] = "1", false),
            ("negative reconciled", row => row[11] = "-1", false),
            ("unknown USD", row => row[6] = 1L, false),
            ("wrong disposition", row => row[9] = 4L, false),
            ("different amount", row => row[11] = "1.4", false),
            ("no dispatch released", row => { row[3] = 3L; row[5] = "0"; row[9] = 2L; row[11] = "0"; }, true),
            ("no dispatch reconciled", row => { row[3] = 3L; row[5] = "0"; row[11] = "0"; }, true),
            ("no dispatch with paid amount", row => { row[3] = 3L; row[11] = "0"; }, false),
            ("no dispatch with reconciled amount", row => { row[3] = 3L; row[5] = "0"; }, false),
            ("no dispatch uncertain", row => { row[3] = 3L; row[5] = "0"; row[9] = 4L; row[11] = "0"; }, false),
            ("still exposed", row => row[3] = 1L, false),
        ];

        foreach ((string name, Action<object[]> change, bool expected) in cases)
        {
            object[] row = [other, reservation, run, 2L, "2", "1.5", 0L, reservation, run, 1L, "2", "1.5", 1L];

            change(row);

            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = "SELECT " + string.Join(',', Enumerable.Range(0, row.Length).Select(index => "$p" + index));

            for (int index = 0; index < row.Length; index++) command.Parameters.AddWithValue("$p" + index, row[index]);

            await using SqliteDataReader reader = await command.ExecuteReaderAsync();

            Assert.True(await reader.ReadAsync());

            Assert.True(GuildBudgetObligations.ValidReservationRow(reader, 0) == expected, name);
        }
    }

    [Fact]
    public async Task Reservation_validation_skips_census_without_any_link()
    {
        await using Fixture f = await Fixture.CreateAsync();

        f.UseBudgetParticipant(new GuildBudgetLedger());

        GuildQuestStored row = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        await using SqliteConnection connection = await f.OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction();

        List<string> statements = [];

        SQLitePCL.raw.sqlite3_trace(connection.Handle, (object? _, string sql) => statements.Add(sql), null);

        try
        {
            Assert.True(await GuildBudgetObligations.ReservationsClearWithinAsync(connection, transaction, row.AllocationAccountId, default));
        }
        finally
        {
            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.strdelegate_trace)null!, null);
        }

        Assert.Single(statements);

        Assert.DoesNotContain(statements, sql => sql.Contains("BudgetReservations", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reservation_validation_uses_live_owned_disposition_and_detects_cross_run_aliases()
    {
        await using Fixture f = await Fixture.CreateAsync();

        await using SqliteConnection connection = await f.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        BudgetPolicySettings settings = new() { Enabled = true, DailyLimitUsd = 10m };

        BudgetReservationService daily = new(db, new TestOptionsMonitor<ArcanumSettings>(new() { Cost = new() { Budget = settings } }));

        GuildBudgetLedger ledger = new(daily);

        f.UseBudgetParticipant(ledger);

        GuildQuestStored row = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(row);

        Guid run = Guid.NewGuid(), bill = Guid.NewGuid(), dispatch = Guid.NewGuid();

        Assert.True((await f.Store.PrepareDispatchAsync(driver, dispatch, ReceiptDigest("reservation-live"), run, false, default)).IsSuccess);

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        await using (SqliteCommand seed = connection.CreateCommand())
        {
            seed.Transaction = transaction;

            seed.CommandText = "INSERT INTO InferenceRuns(Id,RequestId,Surface,Purpose,StartedAt,Status) VALUES($run,'reservation-live','test','test',$now,0)";

            seed.Parameters.AddWithValue("$run", run.ToString("N"));

            seed.Parameters.AddWithValue("$now", UtcInstantText.Format(DateTimeOffset.UtcNow));

            await seed.ExecuteNonQueryAsync();
        }

        GuildBudgetEffectRequest request = new(Guid.NewGuid(), row.Quest.Id, dispatch, run,
            GuildBudgetEffectKind.LocalProvider, bill, null, 5m, 20);

        var prepared = await GuildBudgetPhysicalAccounting.PrepareLocalWithinAsync(connection, transaction, ledger, daily,
            request, DateTimeOffset.UtcNow.AddHours(1), default);

        Assert.True(prepared.IsSuccess, prepared.Error.Message);

        Assert.NotNull(prepared.Value.Request.ReservationId);

        Assert.True((await ledger.ExposeAsync(connection, transaction, row.Quest.Id, row.AllocationAccountId, dispatch, run, default)).IsSuccess);

        await SeedPhysicalBillAsync(connection, transaction, run, bill);

        var applied = await GuildBudgetPhysicalAccounting.ApplyLocalBillWithinAsync(connection, transaction, ledger,
            request.EffectId, Guid.NewGuid(), default, daily);

        Assert.True(applied.IsSuccess, applied.Error.Message);

        settings.Enabled = false;

        Assert.True(await GuildBudgetObligations.ReservationsClearWithinAsync(connection, transaction, row.AllocationAccountId, default));

        await using SqliteCommand mutate = connection.CreateCommand();

        mutate.Transaction = transaction;

        mutate.Parameters.AddWithValue("$id", prepared.Value.Request.ReservationId!.Value.ToString("N"));

        mutate.CommandText = "UPDATE BudgetReservations SET Status=4 WHERE Id=$id";

        Assert.Equal(1, await mutate.ExecuteNonQueryAsync());

        Assert.False(await GuildBudgetObligations.ReservationsClearWithinAsync(connection, transaction, row.AllocationAccountId, default));

        mutate.CommandText = "UPDATE BudgetReservations SET Status=1,ReconciledUsd='0' WHERE Id=$id";

        Assert.Equal(1, await mutate.ExecuteNonQueryAsync());

        Assert.False(await GuildBudgetObligations.ReservationsClearWithinAsync(connection, transaction, row.AllocationAccountId, default));

        mutate.CommandText = "UPDATE BudgetReservations SET ReconciledUsd='2.5' WHERE Id=$id";

        Assert.Equal(1, await mutate.ExecuteNonQueryAsync());

        Assert.True(await GuildBudgetObligations.ReservationsClearWithinAsync(connection, transaction, row.AllocationAccountId, default));

        mutate.CommandText = """
            INSERT INTO BudgetReservations(Id,RunId,BudgetPeriod,Status,ExpiresAt,CreatedAt,UpdatedAt,ReservedUsd,ReconciledUsd)
            SELECT $alias,$other, BudgetPeriod,Status,ExpiresAt,CreatedAt,UpdatedAt,ReservedUsd,ReconciledUsd FROM BudgetReservations WHERE Id=$id
            """;

        mutate.Parameters.AddWithValue("$alias", prepared.Value.Request.ReservationId.Value.ToString("D").ToUpperInvariant());

        mutate.Parameters.AddWithValue("$other", Guid.NewGuid().ToString("N"));

        Assert.Equal(1, await mutate.ExecuteNonQueryAsync());

        Assert.False(await GuildBudgetObligations.ReservationsClearWithinAsync(connection, transaction, row.AllocationAccountId, default));

        await using SqliteCommand plan = connection.CreateCommand();

        plan.Transaction = transaction;

        plan.CommandText = "EXPLAIN QUERY PLAN " + GuildBudgetObligations.ReservationValidationSql;

        plan.Parameters.AddWithValue("$account", row.AllocationAccountId.ToString("D").ToUpperInvariant());

        List<string> details = [];

        await using (SqliteDataReader reader = await plan.ExecuteReaderAsync())
            while (await reader.ReadAsync()) details.Add(reader.GetString(3));

        Assert.Contains(details, value => value.Contains("IX_BudgetReservations_Id_Norm", StringComparison.Ordinal));

        Assert.DoesNotContain(details, value => value.Contains("MATERIALIZE reservations", StringComparison.OrdinalIgnoreCase));
    }

}
