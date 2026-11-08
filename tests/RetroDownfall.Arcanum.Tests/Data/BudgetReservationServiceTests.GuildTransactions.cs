using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class BudgetReservationServiceTests
{
    [SkippableTheory]
    [InlineData(false, 10)]
    [InlineData(true, 0)]
    public async Task GuildTransaction_DisabledDailyGateReturnsNoLink(bool enabled, int limit)
    {
        RequireSqlCipher();

        BudgetReservationService service = CreateService(new BudgetPolicySettings { Enabled = enabled, DailyLimitUsd = limit });

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await connection.OpenAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        Result<BudgetReservation?> result = await service.ReserveWithinAsync(connection, transaction,
            new(Guid.NewGuid(), 3m, DateTimeOffset.UtcNow.AddHours(1), BudgetReservationService.UtcBudgetPeriod(DateTimeOffset.UtcNow)));

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Null(result.Value);

        await transaction.CommitAsync();

        Assert.Equal(0, await CountReservationsAsync());
    }

    [SkippableFact]
    public async Task GuildTransaction_ReservationAndDispatchPreparationRollbackTogether()
    {
        RequireSqlCipher();

        BudgetReservationService service = CreateService(new BudgetPolicySettings { Enabled = true, DailyLimitUsd = 10m });

        DateTimeOffset now = DateTimeOffset.UtcNow;

        Guid run = await StartRunAsync("guild-atomic", now);

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            Result<BudgetReservation?> reserved = await service.ReserveWithinAsync(connection, transaction,
                new(run, 3m, now.AddHours(1), BudgetReservationService.UtcBudgetPeriod(now)));

            Assert.True(reserved.IsSuccess, reserved.Error.Message);

            Assert.NotNull(reserved.Value);

            Result prepared = await service.PrepareWithinAsync(connection, transaction, reserved.Value.Id, run, 4m, 0m, now.AddHours(2));

            Assert.True(prepared.IsSuccess, prepared.Error.Message);

            Assert.True((await service.MarkPossiblyDispatchedWithinAsync(connection, transaction, reserved.Value.Id, run)).IsSuccess);

            await transaction.RollbackAsync();
        }

        Assert.Equal(0, await CountReservationsAsync());

        Assert.Equal(0m, await service.GetTodayOutstandingReservationsAsync());

        Assert.Equal(0m, await service.GetTodayCommittedSpendAsync());
    }

    [SkippableFact]
    public async Task GuildTransaction_PreparationChecksRolloverEvenWithoutARaise()
    {
        RequireSqlCipher();

        DateTimeOffset today = DateTimeOffset.UtcNow;

        DateTimeOffset yesterday = today.AddDays(-1);

        BudgetReservationService service = CreateService(new BudgetPolicySettings { Enabled = true, DailyLimitUsd = 10m });

        service.UtcNowForTesting = () => today;

        Guid run = await StartRunAsync("guild-rollover", yesterday);

        BudgetReservation held = (await service.ReserveAsync(new(run, 6m, today.AddHours(1), BudgetReservationService.UtcBudgetPeriod(yesterday)))).Value;

        await InsertBillableOperationAsync(run, today, 5m);

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        Result result = await service.PrepareWithinAsync(connection, transaction, held.Id, run, 6m, 0m, today.AddHours(2));

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Budget.Exceeded, result.Error.Code);

        await transaction.RollbackAsync();

        Assert.Equal((BudgetReservationStatus.Reserved, 0m), await ReadReservationStateAsync(held.Id));
    }

    [SkippableFact]
    public async Task GuildTransaction_UncertainExposureFromYesterdayStillRefusesToday()
    {
        RequireSqlCipher();

        DateTimeOffset today = DateTimeOffset.UtcNow;

        BudgetReservationService service = CreateService(new BudgetPolicySettings { Enabled = true, DailyLimitUsd = 10m });

        Guid run = await StartRunAsync("guild-unknown", today.AddDays(-1));

        BudgetReservation held = (await service.ReserveAsync(new(run, 8m, today.AddHours(1), BudgetReservationService.UtcBudgetPeriod(today.AddDays(-1))))).Value;

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await using (SqliteTransaction mark = connection.BeginTransaction(deferred: false))
        {
            Assert.True((await service.MarkPossiblyDispatchedWithinAsync(connection, mark, held.Id, run)).IsSuccess);

            await mark.CommitAsync();
        }

        await using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            Result<BudgetReservation?> result = await service.ReserveWithinAsync(connection, transaction,
                new(run, 3m, today.AddHours(1), BudgetReservationService.UtcBudgetPeriod(today)));

            Assert.True(result.IsFailure);

            Assert.Equal(ErrorCodes.Budget.Exceeded, result.Error.Code);

            await transaction.RollbackAsync();
        }

        await service.ReleaseAsync(held.Id);

        await service.SweepExpiredAsync(today.AddDays(2));

        Assert.Equal(8m, await service.GetTodayOutstandingReservationsAsync());

        Assert.Equal(0m, await service.GetTodayCommittedSpendAsync());
    }

    [SkippableFact]
    public async Task GuildTransaction_PreparationRequiresTheExactActiveOwner()
    {
        RequireSqlCipher();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        BudgetReservationService service = CreateService(new BudgetPolicySettings { Enabled = true, DailyLimitUsd = 10m });

        Guid run = await StartRunAsync("guild-owner", now);

        BudgetReservation held = (await service.ReserveAsync(new(run, 2m, now.AddHours(1), BudgetReservationService.UtcBudgetPeriod(now)))).Value;

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            Result result = await service.PrepareWithinAsync(connection, transaction, held.Id, Guid.NewGuid(), 4m, 0m, now.AddHours(2));

            Assert.True(result.IsFailure);

            Assert.True((await service.MarkPossiblyDispatchedWithinAsync(connection, transaction, held.Id, Guid.NewGuid())).IsFailure);

            await transaction.RollbackAsync();
        }

        await service.ReleaseAsync(held.Id);

        await using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            Result result = await service.PrepareWithinAsync(connection, transaction, held.Id, run, 4m, 0m, now.AddHours(2));

            Assert.True(result.IsFailure);

            await transaction.RollbackAsync();
        }
    }

    [SkippableFact]
    public async Task GuildTransaction_PreparationRaisesRenewsAndNeverLowersAnOwnedHold()
    {
        RequireSqlCipher();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        BudgetReservationService service = CreateService(new BudgetPolicySettings { Enabled = true, DailyLimitUsd = 10m });

        Guid run = await StartRunAsync("guild-renew", now);

        BudgetReservation held = (await service.ReserveAsync(new(run, 2m, now.AddHours(1), BudgetReservationService.UtcBudgetPeriod(now)))).Value;

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            Assert.True((await service.PrepareWithinAsync(connection, transaction, held.Id, run, 4m, 0m, now.AddHours(2))).IsSuccess);

            Assert.True((await service.PrepareWithinAsync(connection, transaction, held.Id, run, 1m, 0m, now)).IsSuccess);

            await transaction.CommitAsync();
        }

        Assert.Equal(4m, await service.GetTodayOutstandingReservationsAsync());

        Assert.Equal(UtcInstantText.Format(now.AddHours(2)), await ReadExpiresAtAsync(held.Id));
    }

    [SkippableFact]
    public async Task GuildTransaction_UncertainDispositionRequiresCompleteUsageProofAndNeverWritesABill()
    {
        RequireSqlCipher();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        BudgetReservationService service = CreateService(new BudgetPolicySettings { Enabled = true, DailyLimitUsd = 10m });

        Guid run = await StartRunAsync("guild-disposition", now);

        BudgetReservation held = (await service.ReserveAsync(new(run, 6m, now.AddHours(1), BudgetReservationService.UtcBudgetPeriod(now)))).Value;

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            Assert.True((await service.MarkPossiblyDispatchedWithinAsync(connection, transaction, held.Id, run)).IsSuccess);

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

        BudgetReservationService service = CreateService(new BudgetPolicySettings { Enabled = true, DailyLimitUsd = 10m });

        Guid run = await StartRunAsync("guild-release", now);

        BudgetReservation held = (await service.ReserveAsync(new(run, 6m, now.AddHours(1), BudgetReservationService.UtcBudgetPeriod(now)))).Value;

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

        BudgetReservationService service = CreateService(new BudgetPolicySettings { Enabled = true, DailyLimitUsd = 20m });

        Guid run = await StartRunAsync("guild-partial", now.AddDays(-1));

        BudgetReservation held = (await service.ReserveAsync(new(run, 6m, now.AddHours(1), BudgetReservationService.UtcBudgetPeriod(now.AddDays(-1))))).Value;

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

        BudgetReservationService service = CreateService(new BudgetPolicySettings { Enabled = true, DailyLimitUsd = 10m });

        Guid run = await StartRunAsync("guild-final-monotonic", now);

        BudgetReservation held = (await service.ReserveAsync(new(run, 6m, now.AddHours(1), BudgetReservationService.UtcBudgetPeriod(now)))).Value;

        await service.MarkPossiblyDispatchedAsync(held.Id, run);

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        Assert.True((await service.RecordPartialUsageWithinAsync(connection, transaction, held.Id, run, 2m)).IsSuccess);

        Assert.True((await service.ReconcileWithinAsync(connection, transaction, held.Id, run, 1m, completeUsageProven: true)).IsFailure);

        await transaction.CommitAsync();

        Assert.Equal((BudgetReservationStatus.UncertainPaid, 2m), await ReadReservationStateAsync(held.Id));
    }

    [SkippableFact]
    public async Task GuildTransaction_DisablingDailyGateDoesNotRaiseAnExistingHold()
    {
        RequireSqlCipher();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        BudgetPolicySettings budget = new() { Enabled = true, DailyLimitUsd = 10m };

        BudgetReservationService service = CreateService(budget);

        Guid run = await StartRunAsync("guild-disabled-later", now);

        BudgetReservation held = (await service.ReserveAsync(new(run, 2m, now.AddHours(1), BudgetReservationService.UtcBudgetPeriod(now)))).Value;

        budget.Enabled = false;

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            Assert.True((await service.PrepareWithinAsync(connection, transaction, held.Id, run, 20m, 20m, now.AddHours(2))).IsSuccess);

            await transaction.CommitAsync();
        }

        Assert.Equal(2m, await service.GetTodayOutstandingReservationsAsync());

        Assert.Equal(UtcInstantText.Format(now.AddHours(2)), await ReadExpiresAtAsync(held.Id));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuildTransaction_PhysicalBoundaryRechecksRolloverBeforeUncertainMark(bool blocked)
    {
        RequireSqlCipher();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        BudgetReservationService service = CreateService(new BudgetPolicySettings { Enabled = true, DailyLimitUsd = 10m });

        service.UtcNowForTesting = () => now;

        Guid run = await StartRunAsync("guild-boundary", now.AddDays(-1));

        BudgetReservation held = (await service.ReserveAsync(new(run, 6m, now.AddMinutes(-1), BudgetReservationService.UtcBudgetPeriod(now.AddDays(-1))))).Value;

        if (blocked) await InsertBillableOperationAsync(run, now, 5m);

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await using (SqliteTransaction transaction = connection.BeginTransaction(deferred: false))
        {
            Result result = await service.RecheckBeforeDispatchWithinAsync(connection, transaction, held.Id, run);

            Assert.Equal(!blocked, result.IsSuccess);

            if (blocked) Assert.Equal(ErrorCodes.Budget.Exceeded, result.Error.Code);

            await transaction.CommitAsync();
        }

        Assert.Equal((blocked ? BudgetReservationStatus.Reserved : BudgetReservationStatus.UncertainPaid, 0m), await ReadReservationStateAsync(held.Id));

        Assert.Equal(UtcInstantText.Format(blocked ? now.AddMinutes(-1) : now.AddHours(1)), await ReadExpiresAtAsync(held.Id));
    }

    [SkippableTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task GuildTransaction_ExplicitNoDispatchProofCannotEraseMeasuredPartialUsage(bool exposed, bool partial)
    {
        RequireSqlCipher();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        BudgetReservationService service = CreateService(new BudgetPolicySettings { Enabled = true, DailyLimitUsd = 10m });

        Guid run = await StartRunAsync("guild-proven-unsent", now);

        BudgetReservation held = (await service.ReserveAsync(new(run, 6m, now.AddHours(1), BudgetReservationService.UtcBudgetPeriod(now)))).Value;

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

}
