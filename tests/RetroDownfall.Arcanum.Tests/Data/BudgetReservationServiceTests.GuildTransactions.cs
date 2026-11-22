using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class BudgetReservationServiceTests
{
    [SkippableFact]
    public async Task GuildTransaction_UncertainDispositionRequiresCompleteUsageProofAndNeverWritesABill()
    {
        RequireSqlCipher();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        BudgetReservationService service = CreateService();

        Guid run = await StartRunAsync("guild-disposition", now);

        BudgetReservation held = await LegacyBudgetFixture.SeedAsync(_db!, new(run, 6m, now.AddHours(1), BudgetReservationService.UtcBudgetPeriod(now)));

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            Assert.True((await service.ExposeHistoricalReservationWithinAsync(connection, transaction, held.Id, run)).IsSuccess);

            Assert.True((await service.ReleaseWithinAsync(connection, transaction, held.Id, run)).IsFailure);

            Assert.True((await service.ReconcileWithinAsync(connection, transaction, held.Id, run, 2m, completeUsageProven: false)).IsFailure);

            await transaction.CommitAsync();
        }

        Assert.Equal(6m, await service.GetTodayOutstandingReservationsAsync());

        await InsertBillableOperationAsync(run, now, 2m);

        await using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            Assert.True((await service.ReconcileWithinAsync(connection, transaction, held.Id, Guid.NewGuid(), 2m, completeUsageProven: true)).IsFailure);

            Assert.True((await service.ReconcileWithinAsync(connection, transaction, held.Id, run, 2m, completeUsageProven: true)).IsSuccess);

            Assert.True((await service.ReconcileWithinAsync(connection, transaction, held.Id, run, 2m, completeUsageProven: true)).IsSuccess);

            Assert.True((await service.ReconcileWithinAsync(connection, transaction, held.Id, run, 3m, completeUsageProven: false)).IsFailure);

            Assert.True((await service.ReconcileWithinAsync(connection, transaction, held.Id, run, 3m, completeUsageProven: true)).IsSuccess);

            Assert.True((await service.ReconcileWithinAsync(connection, transaction, held.Id, run, 2m, completeUsageProven: true)).IsFailure);

            await transaction.CommitAsync();
        }

        Assert.Equal(0m, await service.GetTodayOutstandingReservationsAsync());

        Assert.Equal(2m, await service.GetTodayCommittedSpendAsync());

        Assert.Equal((BudgetReservationStatus.Reconciled, 3m), await ReadReservationStateAsync(held.Id));
    }

    [SkippableFact]
    public async Task GuildTransaction_ProvenUnsentReleaseIsOwnedAndTransactional()
    {
        RequireSqlCipher();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        BudgetReservationService service = CreateService();

        Guid run = await StartRunAsync("guild-release", now);

        BudgetReservation held = await LegacyBudgetFixture.SeedAsync(_db!, new(run, 6m, now.AddHours(1), BudgetReservationService.UtcBudgetPeriod(now)));

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            Assert.True((await service.ReleaseWithinAsync(connection, transaction, held.Id, Guid.NewGuid())).IsFailure);

            Assert.True((await service.ReleaseWithinAsync(connection, transaction, held.Id, run)).IsSuccess);

            await transaction.RollbackAsync();
        }

        Assert.Equal(6m, await service.GetTodayOutstandingReservationsAsync());

        await using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            Assert.True((await service.ReleaseWithinAsync(connection, transaction, held.Id, run)).IsSuccess);

            Assert.True((await service.ReleaseWithinAsync(connection, transaction, held.Id, run)).IsSuccess);

            await transaction.CommitAsync();
        }

        Assert.Equal(0m, await service.GetTodayOutstandingReservationsAsync());
    }

    [SkippableTheory]
    [InlineData("2", "4")]
    [InlineData("8", "0")]
    public async Task GuildTransaction_PartialKnownUsageKeepsOnlyRemainingUncertainExposure(string knownText, string outstandingText)
    {
        RequireSqlCipher();

        decimal known = decimal.Parse(knownText, System.Globalization.CultureInfo.InvariantCulture);

        decimal outstanding = decimal.Parse(outstandingText, System.Globalization.CultureInfo.InvariantCulture);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        BudgetReservationService service = CreateService();

        Guid run = await StartRunAsync("guild-partial", now.AddDays(-1));

        BudgetReservation held = await LegacyBudgetFixture.SeedAsync(_db!, new(run, 6m, now.AddHours(1), BudgetReservationService.UtcBudgetPeriod(now.AddDays(-1))));

        await service.MarkPossiblyDispatchedAsync(held.Id, run);

        await InsertBillableOperationAsync(run, now, known);

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            Assert.True((await service.RecordPartialUsageWithinAsync(connection, transaction, held.Id, Guid.NewGuid(), known)).IsFailure);

            Assert.True((await service.RecordPartialUsageWithinAsync(connection, transaction, held.Id, run, known)).IsSuccess);

            Assert.True((await service.RecordPartialUsageWithinAsync(connection, transaction, held.Id, run, known)).IsSuccess);

            Assert.True((await service.RecordPartialUsageWithinAsync(connection, transaction, held.Id, run, 1m)).IsFailure);

            await transaction.RollbackAsync();
        }

        Assert.Equal((BudgetReservationStatus.UncertainPaid, 0m), await ReadReservationStateAsync(held.Id));

        await using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            Assert.True((await service.RecordPartialUsageWithinAsync(connection, transaction, held.Id, run, known)).IsSuccess);

            await transaction.CommitAsync();
        }

        await service.ReleaseAsync(held.Id);

        await service.SweepExpiredAsync(now.AddDays(2));

        Assert.Equal((BudgetReservationStatus.UncertainPaid, known), await ReadReservationStateAsync(held.Id));

        Assert.Equal(outstanding, await service.GetTodayOutstandingReservationsAsync());

        Assert.Equal(known, await service.GetTodayCommittedSpendAsync());
    }

    [SkippableFact]
    public async Task GuildTransaction_CompleteProofCannotEraseAlreadyMeasuredPartialUsage()
    {
        RequireSqlCipher();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        BudgetReservationService service = CreateService();

        Guid run = await StartRunAsync("guild-final-monotonic", now);

        BudgetReservation held = await LegacyBudgetFixture.SeedAsync(_db!, new(run, 6m, now.AddHours(1), BudgetReservationService.UtcBudgetPeriod(now)));

        await service.MarkPossiblyDispatchedAsync(held.Id, run);

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        Assert.True((await service.RecordPartialUsageWithinAsync(connection, transaction, held.Id, run, 2m)).IsSuccess);

        Assert.True((await service.ReconcileWithinAsync(connection, transaction, held.Id, run, 1m, completeUsageProven: true)).IsFailure);

        await transaction.CommitAsync();

        Assert.Equal((BudgetReservationStatus.UncertainPaid, 2m), await ReadReservationStateAsync(held.Id));
    }

    [SkippableTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task GuildTransaction_ExplicitNoDispatchProofCannotEraseMeasuredPartialUsage(bool exposed, bool partial)
    {
        RequireSqlCipher();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        BudgetReservationService service = CreateService();

        Guid run = await StartRunAsync("guild-proven-unsent", now);

        BudgetReservation held = await LegacyBudgetFixture.SeedAsync(_db!, new(run, 6m, now.AddHours(1), BudgetReservationService.UtcBudgetPeriod(now)));

        if (exposed) await service.MarkPossiblyDispatchedAsync(held.Id, run);

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            if (partial) Assert.True((await service.RecordPartialUsageWithinAsync(connection, transaction, held.Id, run, 2m)).IsSuccess);

            Result result = await service.ReleaseProvenNoDispatchWithinAsync(connection, transaction, held.Id, run);

            Assert.Equal(!partial, result.IsSuccess);

            if (!partial) Assert.True((await service.ReleaseProvenNoDispatchWithinAsync(connection, transaction, held.Id, run)).IsSuccess);

            await transaction.CommitAsync();
        }

        BudgetReservationStatus expected = partial ? BudgetReservationStatus.UncertainPaid
            : exposed ? BudgetReservationStatus.Reconciled : BudgetReservationStatus.Released;

        Assert.Equal((expected, partial ? 2m : 0m), await ReadReservationStateAsync(held.Id));

        Assert.Equal(0m, await service.GetTodayCommittedSpendAsync());
    }

    [SkippableFact]
    public async Task GuildTransaction_UnknownOwnersAndTerminalStatusesCannotAdmitOrDiscardLiability()
    {
        RequireSqlCipher();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        BudgetReservationService service = CreateService();

        service.UtcNowForTesting = () => now;

        Guid run = await StartRunAsync("guild-invalid-status", now);

        BudgetReservation held = await LegacyBudgetFixture.SeedAsync(_db!, new(run, 2m, now.AddHours(1), BudgetReservationService.UtcBudgetPeriod(now)));

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        Guid wrongRun = Guid.NewGuid();

        Assert.Equal(ErrorCodes.Guild.ReconciliationRequired,
            (await service.ExposeHistoricalReservationWithinAsync(connection, transaction, held.Id, wrongRun)).Error.Code);

        Assert.Equal(ErrorCodes.Guild.ReconciliationRequired,
            (await service.ReleaseProvenNoDispatchWithinAsync(connection, transaction, held.Id, wrongRun)).Error.Code);

        Assert.True((await service.ExposeHistoricalReservationWithinAsync(connection, transaction, held.Id, run)).IsSuccess);

        Assert.Equal(ErrorCodes.Guild.ReconciliationRequired,
            (await service.ExposeHistoricalReservationWithinAsync(connection, transaction, held.Id, run)).Error.Code);

        foreach (int status in new[] { (int)BudgetReservationStatus.Released, (int)BudgetReservationStatus.Expired, 99 })
        {
            await using SqliteCommand command = connection.CreateCommand();

            command.Transaction = transaction;

            command.CommandText = "UPDATE BudgetReservations SET Status=$status WHERE Id=$id";

            command.Parameters.AddWithValue("$status", status);

            command.Parameters.AddWithValue("$id", held.Id.ToString("N"));

            Assert.Equal(1, await command.ExecuteNonQueryAsync());

            Assert.Equal(ErrorCodes.Guild.ReconciliationRequired,
                (await service.ExposeHistoricalReservationWithinAsync(connection, transaction, held.Id, run)).Error.Code);

            Assert.Equal(ErrorCodes.Guild.ReconciliationRequired,
                (await service.ReconcileWithinAsync(connection, transaction, held.Id, run, 0m, true)).Error.Code);

            Result release = await service.ReleaseProvenNoDispatchWithinAsync(connection, transaction, held.Id, run);

            if (status == (int)BudgetReservationStatus.Released) Assert.True(release.IsSuccess);
            else Assert.Equal(ErrorCodes.Guild.ReconciliationRequired, release.Error.Code);
        }

        await transaction.RollbackAsync();

        Assert.Equal((BudgetReservationStatus.Reserved, 0m), await ReadReservationStateAsync(held.Id));
    }

    [SkippableFact]
    public async Task GuildTransaction_SuppressedSqlUpdateFailsClosedAndKeepsTheOwnedHold()
    {
        RequireSqlCipher();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        BudgetReservationService service = CreateService();

        service.UtcNowForTesting = () => now;

        Guid run = await StartRunAsync("guild-suppressed-update", now);

        BudgetReservation held = await LegacyBudgetFixture.SeedAsync(_db!, new(run, 2m, now.AddHours(1), BudgetReservationService.UtcBudgetPeriod(now)));

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            await using SqliteCommand command = connection.CreateCommand();

            command.Transaction = transaction;

            // An adversarial trigger silently suppresses a write: success requires one changed row.
            command.CommandText = "CREATE TEMP TRIGGER reject_guild_update BEFORE UPDATE OF Status ON BudgetReservations BEGIN SELECT RAISE(IGNORE); END";

            await command.ExecuteNonQueryAsync();

            Result result = await service.ExposeHistoricalReservationWithinAsync(connection, transaction, held.Id, run);

            Assert.Equal(ErrorCodes.Guild.ReconciliationRequired, result.Error.Code);

            await transaction.RollbackAsync();
        }

        Assert.Equal((BudgetReservationStatus.Reserved, 0m), await ReadReservationStateAsync(held.Id));

        Assert.Equal(UtcInstantText.Format(now.AddHours(1)), await ReadExpiresAtAsync(held.Id));

        Assert.Equal(2m, await service.GetTodayOutstandingReservationsAsync());
    }

    [SkippableFact]
    public async Task GuildTransaction_HistoricalExposureRejectsTransactionFromDifferentConnection()
    {
        RequireSqlCipher();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        BudgetReservationService service = CreateService();

        service.UtcNowForTesting = () => now;

        Guid run = await StartRunAsync("guild-wrong-transaction", now);

        BudgetReservation held = await LegacyBudgetFixture.SeedAsync(_db!,
            new(run, 1m, now.AddHours(1), BudgetReservationService.UtcBudgetPeriod(now)));

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await using ArcanumDbContext otherContext = _fixture.CreateContext(_dbPath);

        SqliteConnection other = (SqliteConnection)otherContext.Database.GetDbConnection();

        await other.OpenAsync();

        await using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await service.ExposeHistoricalReservationWithinAsync(other, transaction, held.Id, run));

            await transaction.RollbackAsync();
        }

        Assert.Equal((BudgetReservationStatus.Reserved, 0m), await ReadReservationStateAsync(held.Id));
    }
}
