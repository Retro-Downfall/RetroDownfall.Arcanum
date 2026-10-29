using System.Data.Common;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using SQLitePCL;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class BudgetReservationServiceTests
{
    [SkippableTheory]
    [InlineData(false, 32)]
    [InlineData(true, 32)]
    [InlineData(false, 10000)]
    [InlineData(true, 10000)]
    [InlineData(false, 30000)]
    [InlineData(true, 30000)]
    public async Task Stable_day_folds_committed_rows_before_the_writer_and_keeps_the_exact_decision(bool adjust, int count)
    {
        RequireSqlCipher();

        DateTimeOffset now = new(2035, 6, 7, 12, 0, 0, TimeSpan.Zero);

        Guid run = await StartRunAsync("writer-snapshot", now);

        await SeedSnapshotBillsAsync(run, now, count);

        BudgetReservationService service = CreateService(new BudgetPolicySettings { Enabled = true, DailyLimitUsd = 10m });

        service.UtcNowForTesting = () => now;

        string period = BudgetReservationService.UtcBudgetPeriod(now);

        BudgetReservation? existing = adjust ? await ReserveAsync(service, 0.25m, now.AddHours(1), period) : null;

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        using BudgetNativeTrace trace = new(connection);

        Result result = adjust
            ? await service.AdjustAsync(existing!.Id, 0.5m)
            : (await service.ReserveAsync(new(run, 0.5m, now.AddHours(1), period))).IsSuccess ? Result.Success() : Result.Failure(new Error("test", "reservation refused"));

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        trace.AssertNoCallbackFault();

        NativeBudgetStatement[] folds = trace.Statements.Where(static s => s.IsCommittedFold).ToArray();

        Assert.Equal(0.5m, await service.GetTodayOutstandingReservationsAsync());

        Assert.Equal(count * 0.0001m, await service.GetTodayCommittedSpendAsync());

        _snapshotOutput.WriteLine($"RV110 {(adjust ? "Adjust" : "Reserve")} {count}: " + string.Join("; ", folds.Select(static f => $"rows={f.Rows}, vm={f.VmSteps}, inside={f.InsideTransaction}")));

        Assert.Single(folds);

        Assert.Equal(count, folds[0].Rows);

        Assert.True(folds[0].VmSteps > count);

        Assert.False(folds[0].InsideTransaction, $"All {folds[0].Rows} committed rows/{folds[0].VmSteps} VM steps were read inside the writer.");

        Assert.Equal(1, trace.Statements.Count(static s => s.Sql.StartsWith("BEGIN IMMEDIATE", StringComparison.OrdinalIgnoreCase)));

        Assert.Equal(1L, await CountReservationsAsync());

        await AssertConnectionIsNotInATransactionAsync();
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Held_committed_read_allows_an_actual_independent_writer_before_the_budget_decision(bool adjust)
    {
        RequireSqlCipher();

        DateTimeOffset now = new(2035, 6, 7, 12, 0, 0, TimeSpan.Zero);

        Guid run = await StartRunAsync("held-budget-read", now);

        await SeedSnapshotBillsAsync(run, now, 32);

        BudgetReservationService service = CreateService(new BudgetPolicySettings { Enabled = true, DailyLimitUsd = 10m });

        service.UtcNowForTesting = () => now;

        string period = BudgetReservationService.UtcBudgetPeriod(now);

        BudgetReservation? existing = adjust ? await ReserveAsync(service, 0.25m, now.AddHours(1), period) : null;

        await using ArcanumDbContext otherDb = _fixture.CreateContext(_dbPath);

        await otherDb.Database.OpenConnectionAsync();

        SqliteConnection otherConnection = (SqliteConnection)otherDb.Database.GetDbConnection();

        otherConnection.DefaultTimeout = 1;

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));

        using ManualResetEventSlim release = new();

        TaskCompletionSource atRead = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource writerAttempted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using BudgetNativeTrace trace = new((SqliteConnection)_db!.Database.GetDbConnection());

        using BudgetNativeTrace otherTrace = new(otherConnection);

        int holds = 0;

        trace.Started = item =>
        {
            if (!item.IsCommittedFold || Interlocked.Increment(ref holds) != 1) return;

            atRead.TrySetResult();

            release.Wait(timeout.Token);
        };

        otherTrace.Started = item =>
        {
            if (item.Sql.Contains("INSERT INTO \"InferenceRuns\"", StringComparison.Ordinal)) writerAttempted.TrySetResult();
        };

        Task<Result> operation = Task.Factory.StartNew(async () => adjust
                ? await service.AdjustAsync(existing!.Id, 0.5m, timeout.Token)
                : (await service.ReserveAsync(new(run, 0.5m, now.AddHours(1), period), timeout.Token)).IsSuccess ? Result.Success() : Result.Failure(new Error("test", "reservation refused")),
            timeout.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

        Task<Guid>? writer = null;

        bool committedBeforeRelease = false;

        try
        {
            await atRead.Task.WaitAsync(TimeSpan.FromSeconds(30));

            writer = Task.Factory.StartNew(() => new TurnRunWriter(otherDb).StartRunAsync(new("independent-writer", null, "test", "test", null, now), timeout.Token),
                timeout.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

            await writerAttempted.Task.WaitAsync(TimeSpan.FromSeconds(30));

            try
            {
                _ = await writer.WaitAsync(TimeSpan.FromSeconds(10));

                committedBeforeRelease = true;
            }
            catch (TimeoutException)
            {
                // Release the held read in finally before the committedBeforeRelease assertion
                // reports the blocked writer; neither background task may be stranded by the failure.
            }
        }
        finally
        {
            release.Set();

            Result result = await operation.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

            if (writer is not null) _ = await writer.WaitAsync(TimeSpan.FromSeconds(30));
        }

        trace.AssertNoCallbackFault();

        otherTrace.AssertNoCallbackFault();

        _snapshotOutput.WriteLine($"Independent writer committed while actual committed-cost SELECT held: {committedBeforeRelease}; "
            + string.Join("; ", trace.Statements.Where(static f => f.IsCommittedFold).Select(static f => $"rows={f.Rows}, vm={f.VmSteps}, inside={f.InsideTransaction}")));

        Assert.True(committedBeforeRelease, "The actual independent writer was blocked for the entire held committed-cost read.");

        Assert.Equal(0.5m, await service.GetTodayOutstandingReservationsAsync());

        await AssertConnectionIsNotInATransactionAsync();
    }

    [SkippableTheory]
    [InlineData(false, "external-during-read")]
    [InlineData(true, "external-during-read")]
    [InlineData(false, "external-after-read")]
    [InlineData(true, "external-after-read")]
    [InlineData(false, "own-write")]
    [InlineData(true, "own-write")]
    [InlineData(false, "own-rollback")]
    [InlineData(true, "own-rollback")]
    [InlineData(false, "external-rollback")]
    [InlineData(true, "external-rollback")]
    [InlineData(false, "schema")]
    [InlineData(true, "schema")]
    public async Task Committed_read_ahead_revalidates_real_native_changes_and_falls_back(bool adjust, string change)
    {
        RequireSqlCipher();

        DateTimeOffset now = new(2035, 6, 7, 12, 0, 0, TimeSpan.Zero);

        Guid run = await StartRunAsync("snapshot-fences", now);

        await SeedSnapshotBillsAsync(run, now, 1);

        BudgetReservationService service = CreateService(new BudgetPolicySettings { Enabled = true, DailyLimitUsd = 1m });

        service.UtcNowForTesting = () => now;

        string period = BudgetReservationService.UtcBudgetPeriod(now);

        BudgetReservation? existing = adjust ? await ReserveAsync(service, 0.25m, now.AddHours(1), period) : null;

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await using ArcanumDbContext other = _fixture.CreateContext(_dbPath);

        SqliteConnection otherConnection = (SqliteConnection)other.Database.GetDbConnection();

        using BudgetNativeTrace trace = new(connection);

        async Task ChangeAsync()
        {
            if (change == "schema")
            {
                await SnapshotSqlAsync(connection, "CREATE INDEX rv110_schema_probe ON BillableOperations(ActualCostUsd)");
            }
            else if (change.EndsWith("rollback", StringComparison.Ordinal))
            {
                SqliteConnection target = change.StartsWith("own", StringComparison.Ordinal) ? connection : otherConnection;

                await using SqliteTransaction transaction = target.BeginTransaction(deferred: false);

                await SnapshotSqlAsync(target, "UPDATE BillableOperations SET ActualCostUsd = '0.75'", transaction);

                await transaction.RollbackAsync();
            }
            else
            {
                await SnapshotSqlAsync(change == "own-write" ? connection : otherConnection,
                    "UPDATE BillableOperations SET ActualCostUsd = '0.75'");
            }
        }

        if (change == "external-during-read")
        {
            int done = 0;

            trace.RowObserved = statement =>
            {
                if (statement.IsCommittedFold && Interlocked.Exchange(ref done, 1) == 0) ChangeAsync().GetAwaiter().GetResult();
            };
        }
        else service.BeforeWriteTransactionForTesting = _ => ChangeAsync();

        bool success;

        try
        {
            success = adjust ? (await service.AdjustAsync(existing!.Id, 0.5m)).IsSuccess
                : (await service.ReserveAsync(new(run, 0.5m, now.AddHours(1), period))).IsSuccess;
        }
        finally
        {
            if (change == "schema") await SnapshotSqlAsync(connection, "DROP INDEX IF EXISTS rv110_schema_probe");
        }

        trace.AssertNoCallbackFault();

        bool monetaryChange = change is "external-during-read" or "external-after-read" or "own-write";

        Assert.Equal(!monetaryChange, success);

        NativeBudgetStatement[] folds = trace.Statements.Where(static f => f.IsCommittedFold).ToArray();

        bool fallback = change != "external-rollback";

        Assert.Equal(fallback ? 2 : 1, folds.Length);

        Assert.All(folds, f => Assert.Equal(1, f.Rows));

        Assert.False(folds[0].InsideTransaction);

        Assert.Equal(fallback, folds[^1].InsideTransaction);

        _snapshotOutput.WriteLine($"{change}/{adjust}: " + string.Join("; ", folds.Select(static f => $"rows={f.Rows}, vm={f.VmSteps}, inside={f.InsideTransaction}")));

        Assert.Equal(monetaryChange ? adjust ? 0.25m : 0m : 0.5m, await service.GetTodayOutstandingReservationsAsync());

        await AssertConnectionIsNotInATransactionAsync();
    }

    [SkippableFact]
    public async Task Pooled_close_reopen_invalidates_the_hint_even_when_native_object_and_stamps_are_reused()
    {
        RequireSqlCipher();

        DateTimeOffset now = new(2035, 6, 7, 12, 0, 0, TimeSpan.Zero);

        Guid run = await StartRunAsync("pooled-hint", now);

        await SeedSnapshotBillsAsync(run, now, 32);

        await using ArcanumDbContext pooled = _fixture.CreateContext(_dbPath, pooled: true);

        SqliteConnection connection = (SqliteConnection)pooled.Database.GetDbConnection();

        object? handle = connection.Handle;

        string before = await SnapshotStampAsync(connection);

        BudgetReservationService service = CreateService(new BudgetPolicySettings { Enabled = true, DailyLimitUsd = 1m }, pooled);

        using BudgetNativeTrace first = new(connection);

        BudgetNativeTrace? reopened = null;

        bool reused = false;

        string? after = null;

        service.BeforeWriteTransactionForTesting = async _ =>
        {
            first.Dispose();

            await pooled.Database.CloseConnectionAsync();

            await pooled.Database.OpenConnectionAsync();

            reused = ReferenceEquals(handle, connection.Handle);

            after = await SnapshotStampAsync(connection);

            reopened = new(connection);
        };

        try
        {
            Result<BudgetReservation> result = await service.ReserveAsync(new(run, 0.5m, now.AddHours(1), BudgetReservationService.UtcBudgetPeriod(now)));

            Assert.True(result.IsSuccess);

            Assert.True(reused, "This control requires an actually reused pooled native object.");

            Assert.Equal(before, after);

            first.AssertNoCallbackFault();

            Assert.NotNull(reopened);

            reopened.AssertNoCallbackFault();

            NativeBudgetStatement[] outside = first.Statements.Where(static f => f.IsCommittedFold).ToArray();

            NativeBudgetStatement[] inside = reopened.Statements.Where(static f => f.IsCommittedFold).ToArray();

            Assert.Single(outside);

            Assert.Single(inside);

            Assert.False(outside[0].InsideTransaction);

            Assert.True(inside[0].InsideTransaction);

            Assert.Equal(32, outside[0].Rows);

            Assert.Equal(32, inside[0].Rows);

            _snapshotOutput.WriteLine($"pooled reused={reused}, equalStamp={before == after}; outside={outside[0].Rows}/{outside[0].VmSteps}, fallback={inside[0].Rows}/{inside[0].VmSteps}");
        }
        finally
        {
            reopened?.Dispose();

            SqliteConnection.ClearPool(connection);
        }
    }

    [SkippableTheory]
    [InlineData("bad", false)]
    [InlineData("bad", true)]
    [InlineData("precision", false)]
    [InlineData("precision", true)]
    [InlineData("day-overflow", true)]
    public async Task Speculative_bad_money_never_breaks_an_authoritative_adjust_noop_or_hides_an_authoritative_fault(string kind, bool noop)
    {
        RequireSqlCipher();

        DateTimeOffset now = new(2035, 6, 7, 12, 0, 0, TimeSpan.Zero);

        Guid run = await StartRunAsync("bad-speculative-fold", now);

        await SeedSnapshotBillsAsync(run, now, 1);

        BudgetReservationService service = CreateService(new BudgetPolicySettings { Enabled = true, DailyLimitUsd = 1m });

        service.UtcNowForTesting = () => now;

        BudgetReservation existing = await ReserveAsync(service, 0.5m, now.AddHours(1), BudgetReservationService.UtcBudgetPeriod(now));

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        if (kind == "day-overflow") await SnapshotSqlAsync(connection, "UPDATE BudgetReservations SET BudgetPeriod = '9999-12-31'");
        else
        {
            await SnapshotSqlAsync(connection, kind == "bad" ? "UPDATE BillableOperations SET ActualCostUsd = 'bad'"
                : "UPDATE BillableOperations SET ActualCostUsd = '79228162514264337593543950335'");

            if (kind == "precision") await InsertBillableOperationAsync(run, now.AddTicks(1), 0.0001m);
        }

        using BudgetNativeTrace trace = new(connection);

        if (noop)
        {
            Result result = await service.AdjustAsync(existing.Id, 0.5m);

            Assert.True(result.IsSuccess);

            Assert.DoesNotContain(trace.Statements, static f => f.IsCommittedFold && f.InsideTransaction);
        }
        else
        {
            Exception? exception = await Record.ExceptionAsync(() => service.AdjustAsync(existing.Id, 0.75m));

            Assert.NotNull(exception);

            if (kind == "bad") Assert.IsType<FormatException>(exception);
            else Assert.IsType<OverflowException>(exception);

            Assert.Contains(trace.Statements, static f => f.IsCommittedFold && f.InsideTransaction);
        }

        Assert.Equal(kind == "day-overflow" ? 0m : 0.5m, await service.GetTodayOutstandingReservationsAsync());

        await AssertConnectionIsNotInATransactionAsync();
    }

    [SkippableTheory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task An_interrupted_optional_stamp_falls_back_to_the_actual_writer_fold(int interruptedStamp)
    {
        RequireSqlCipher();

        DateTimeOffset now = new(2035, 6, 7, 12, 0, 0, TimeSpan.Zero);

        Guid run = await StartRunAsync("missing-proof", now);

        await SeedSnapshotBillsAsync(run, now, 32);

        BudgetReservationService service = CreateService(new BudgetPolicySettings { Enabled = true, DailyLimitUsd = 1m });

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        using BudgetNativeTrace trace = new(connection);

        int stamps = 0;

        trace.Started = statement =>
        {
            if (statement.Sql.Contains("pragma_data_version", StringComparison.Ordinal) && ++stamps == interruptedStamp) raw.sqlite3_interrupt(connection.Handle);
        };

        Result<BudgetReservation> result = await service.ReserveAsync(new(run, 0.5m, now.AddHours(1), BudgetReservationService.UtcBudgetPeriod(now)));

        Assert.True(result.IsSuccess);

        trace.AssertNoCallbackFault();

        NativeBudgetStatement[] folds = trace.Statements.Where(static f => f.IsCommittedFold).ToArray();

        Assert.Equal(interruptedStamp == 1 ? 1 : 2, folds.Length);

        Assert.True(folds[^1].InsideTransaction);

        Assert.Equal(32, folds[^1].Rows);

        await AssertConnectionIsNotInATransactionAsync();
    }

    [SkippableFact]
    public async Task A_noninteger_native_change_stamp_is_not_narrowed_into_a_valid_proof()
    {
        RequireSqlCipher();

        DateTimeOffset now = new(2035, 6, 7, 12, 0, 0, TimeSpan.Zero);

        Guid run = await StartRunAsync("invalid-proof", now);

        await SeedSnapshotBillsAsync(run, now, 32);

        BudgetReservationService service = CreateService(new BudgetPolicySettings { Enabled = true, DailyLimitUsd = 1m });

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        connection.CreateFunction("total_changes", () => 7.5d);

        using BudgetNativeTrace trace = new(connection);

        Assert.True((await service.ReserveAsync(new(run, 0.5m, now.AddHours(1), BudgetReservationService.UtcBudgetPeriod(now)))).IsSuccess);

        NativeBudgetStatement fold = Assert.Single(trace.Statements, static f => f.IsCommittedFold);

        Assert.True(fold.InsideTransaction);

        Assert.Equal(32, fold.Rows);

        await AssertConnectionIsNotInATransactionAsync();
    }

    [SkippableTheory]
    [InlineData("stamp")]
    [InlineData("fold")]
    [InlineData("before-writer")]
    [InlineData("fallback")]
    public async Task Cancellation_at_each_hint_boundary_cannot_become_a_budget_grant_and_leaves_connection_usable(string stage)
    {
        RequireSqlCipher();

        DateTimeOffset now = new(2035, 6, 7, 12, 0, 0, TimeSpan.Zero);

        Guid run = await StartRunAsync("cancel-hint", now);

        await SeedSnapshotBillsAsync(run, now, 32);

        BudgetReservationService service = CreateService(new BudgetPolicySettings { Enabled = true, DailyLimitUsd = 10m });

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        using CancellationTokenSource cancellation = new();

        using BudgetNativeTrace trace = new(connection);

        int done = 0;

        void Cancel()
        {
            if (Interlocked.Exchange(ref done, 1) != 0) return;

            cancellation.Cancel();

            raw.sqlite3_interrupt(connection.Handle);
        }

        trace.Started = statement =>
        {
            if (stage == "stamp" && statement.Sql.Contains("pragma_data_version", StringComparison.Ordinal)) Cancel();
        };

        trace.RowObserved = statement =>
        {
            if (statement.IsCommittedFold && (stage == "fold" || stage == "fallback" && statement.InsideTransaction)) Cancel();
        };

        if (stage == "before-writer") service.BeforeWriteTransactionForTesting = _ =>
        {
            Cancel();

            return Task.CompletedTask;
        };

        if (stage == "fallback") service.BeforeWriteTransactionForTesting = _ => SnapshotSqlAsync(connection, "UPDATE BillableOperations SET ActualCostUsd = '0.0002'");

        Exception? fault = await Record.ExceptionAsync(() => service.ReserveAsync(new(run, 0.5m, now.AddHours(1), BudgetReservationService.UtcBudgetPeriod(now)), cancellation.Token));

        Assert.NotNull(fault);

        Assert.True(fault is OperationCanceledException or SqliteException, fault.GetType().FullName);

        Assert.True(cancellation.IsCancellationRequested);

        // Native reader interruption returns its own canceled token; the unchanged spend-read
        // control characterizes that driver behavior. Pre-reader cancellation retains the caller token.
        if (fault is OperationCanceledException canceled && stage is "stamp" or "before-writer")
            Assert.Equal(cancellation.Token, canceled.CancellationToken);

        trace.AssertNoCallbackFault();

        Assert.Equal(0L, await CountReservationsAsync());

        await AssertConnectionIsNotInATransactionAsync();

        service.BeforeWriteTransactionForTesting = null;

        Assert.True((await service.ReserveAsync(new(run, 0.1m, now.AddHours(1), BudgetReservationService.UtcBudgetPeriod(now)))).IsSuccess);

        Assert.Equal(1L, await CountReservationsAsync());
    }

    [SkippableTheory]
    [InlineData("missing")]
    [InlineData("released")]
    [InlineData("reconciled")]
    public async Task A_stale_reservation_hint_cannot_turn_current_row_noops_into_money_errors(string state)
    {
        RequireSqlCipher();

        DateTimeOffset now = new(2035, 6, 7, 12, 0, 0, TimeSpan.Zero);

        Guid run = await StartRunAsync("noop-hint", now);

        await SeedSnapshotBillsAsync(run, now, 1);

        BudgetReservationService service = CreateService(new BudgetPolicySettings { Enabled = true, DailyLimitUsd = 1m });

        service.UtcNowForTesting = () => now;

        BudgetReservation existing = await ReserveAsync(service, 0.25m, now.AddHours(1), BudgetReservationService.UtcBudgetPeriod(now));

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await SnapshotSqlAsync(connection, "UPDATE BillableOperations SET ActualCostUsd = 'bad'");

        service.BeforeWriteTransactionForTesting = _ => SnapshotSqlAsync(connection, state switch
        {
            "missing" => "DELETE FROM BudgetReservations",
            "released" => "UPDATE BudgetReservations SET Status = 2",
            _ => "UPDATE BudgetReservations SET Status = 1",
        });

        using BudgetNativeTrace trace = new(connection);

        Assert.True((await service.AdjustAsync(existing.Id, 0.5m)).IsSuccess);

        Assert.DoesNotContain(trace.Statements, static f => f.IsCommittedFold && f.InsideTransaction);

        Assert.Equal(0m, await service.GetTodayOutstandingReservationsAsync());

        Assert.Equal(state == "missing" ? 0L : 1L, await CountReservationsAsync());

        await AssertConnectionIsNotInATransactionAsync();
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Adjust_uses_the_actual_period_and_retains_the_rare_second_day_writer_fold(bool changePeriodAfterHint)
    {
        RequireSqlCipher();

        DateTimeOffset now = new(2035, 6, 7, 12, 0, 0, TimeSpan.Zero);

        Guid run = await StartRunAsync("day-hint", now);

        await SeedSnapshotBillsAsync(run, now, 1);

        await InsertBillableOperationAsync(run, now.AddDays(1), 0.8m);

        BudgetReservationService service = CreateService(new BudgetPolicySettings { Enabled = true, DailyLimitUsd = 1m });

        BudgetReservation existing = await ReserveAsync(service, 0.25m, now.AddHours(1), BudgetReservationService.UtcBudgetPeriod(now));

        int clockReads = 0;

        service.UtcNowForTesting = () =>
        {
            clockReads++;

            return now.AddDays(1);
        };

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await using ArcanumDbContext other = _fixture.CreateContext(_dbPath);

        using BudgetNativeTrace trace = new(connection);

        int changed = 0;

        trace.Started = statement =>
        {
            // Change the period after its hint SELECT, before the first bracket stamp is read.
            if (changePeriodAfterHint && statement.Sql.Contains("pragma_data_version", StringComparison.Ordinal)
                && Interlocked.Exchange(ref changed, 1) == 0)
                SnapshotSqlAsync((SqliteConnection)other.Database.GetDbConnection(), "UPDATE BudgetReservations SET BudgetPeriod = '2035-06-08'").GetAwaiter().GetResult();
        };

        Result result = await service.AdjustAsync(existing.Id, 0.5m);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Budget.Exceeded, result.Error.Code);

        NativeBudgetStatement[] folds = trace.Statements.Where(static f => f.IsCommittedFold).ToArray();

        Assert.Equal(2, folds.Length);

        Assert.False(folds[0].InsideTransaction);

        Assert.True(folds[1].InsideTransaction);

        Assert.All(folds, static f => Assert.Equal(1, f.Rows));

        Assert.Equal(changePeriodAfterHint ? 0 : 1, clockReads);

        Assert.Equal(changePeriodAfterHint ? 0.25m : 0m, await service.GetTodayOutstandingReservationsAsync());

        trace.AssertNoCallbackFault();

        _snapshotOutput.WriteLine($"periodChanged={changePeriodAfterHint}: " + string.Join("; ", folds.Select(static f => $"rows={f.Rows}, vm={f.VmSteps}, inside={f.InsideTransaction}")));

        await AssertConnectionIsNotInATransactionAsync();
    }

    [SkippableFact]
    public async Task Unchanged_native_spend_fold_characterizes_reader_cancellation_without_inventing_token_identity()
    {
        RequireSqlCipher();

        DateTimeOffset now = new(2035, 6, 7, 12, 0, 0, TimeSpan.Zero);

        Guid run = await StartRunAsync("native-reader-cancel", now);

        await SeedSnapshotBillsAsync(run, now, 32);

        BudgetReservationService service = CreateService(new BudgetPolicySettings { Enabled = true, DailyLimitUsd = 10m });

        service.UtcNowForTesting = () => now;

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        using CancellationTokenSource cancellation = new();

        using BudgetNativeTrace trace = new(connection);

        int done = 0;

        trace.RowObserved = statement =>
        {
            if (!statement.IsCommittedFold || Interlocked.Exchange(ref done, 1) != 0) return;

            cancellation.Cancel();

            raw.sqlite3_interrupt(connection.Handle);
        };

        Exception? fault = await Record.ExceptionAsync(() => service.GetTodayCommittedSpendAsync(cancellation.Token));

        Assert.NotNull(fault);

        Assert.True(fault is OperationCanceledException or SqliteException);

        Assert.True(cancellation.IsCancellationRequested);

        if (fault is OperationCanceledException canceled)
            _snapshotOutput.WriteLine($"unchanged spend-read driver cancellation: {fault.GetType().Name}, tokenEqual={canceled.CancellationToken == cancellation.Token}, tokenCanBeCanceled={canceled.CancellationToken.CanBeCanceled}");

        Assert.Equal(0L, await CountReservationsAsync());

        await AssertConnectionIsNotInATransactionAsync();
    }

    [SkippableTheory]
    [InlineData("0.5")]
    [InlineData("0.25")]
    [InlineData("0")]
    [InlineData("-1")]
    public async Task A_stable_no_raise_adjustment_does_not_walk_the_bill_ledger(string requestedText)
    {
        RequireSqlCipher();

        DateTimeOffset now = new(2035, 6, 7, 12, 0, 0, TimeSpan.Zero);

        Guid run = await StartRunAsync("cheap-noop", now);

        await SeedSnapshotBillsAsync(run, now, 10000);

        BudgetReservationService service = CreateService(new BudgetPolicySettings { Enabled = true, DailyLimitUsd = 10m });

        service.UtcNowForTesting = () => now;

        BudgetReservation existing = await ReserveAsync(service, 0.5m, now.AddHours(1), BudgetReservationService.UtcBudgetPeriod(now));

        using BudgetNativeTrace trace = new((SqliteConnection)_db!.Database.GetDbConnection());

        Result result = await service.AdjustAsync(existing.Id, decimal.Parse(requestedText, System.Globalization.CultureInfo.InvariantCulture));

        Assert.True(result.IsSuccess);

        Assert.DoesNotContain(trace.Statements, static statement => statement.IsCommittedFold);

        Assert.Equal(0.5m, await service.GetTodayOutstandingReservationsAsync());

        await AssertConnectionIsNotInATransactionAsync();
    }

    [SkippableFact]
    public async Task A_no_raise_hint_does_not_skip_an_actual_writer_owned_raise_or_its_budget_refusal()
    {
        RequireSqlCipher();

        DateTimeOffset now = new(2035, 6, 7, 12, 0, 0, TimeSpan.Zero);

        Guid run = await StartRunAsync("noop-hint-race", now);

        await SeedSnapshotBillsAsync(run, now, 1);

        BudgetReservationService service = CreateService(new BudgetPolicySettings { Enabled = true, DailyLimitUsd = 1m });

        service.UtcNowForTesting = () => now;

        BudgetReservation existing = await ReserveAsync(service, 0.5m, now.AddHours(1), BudgetReservationService.UtcBudgetPeriod(now));

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        service.BeforeWriteTransactionForTesting = _ => SnapshotSqlAsync(connection,
            "UPDATE BillableOperations SET ActualCostUsd = '0.75'; UPDATE BudgetReservations SET ReservedUsd = '0.25'");

        using BudgetNativeTrace trace = new(connection);

        Result result = await service.AdjustAsync(existing.Id, 0.5m);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Budget.Exceeded, result.Error.Code);

        Assert.Contains(trace.Statements, static statement => statement.IsCommittedFold && statement.InsideTransaction);

        Assert.Equal(0.25m, await service.GetTodayOutstandingReservationsAsync());

        await AssertConnectionIsNotInATransactionAsync();
    }

    [SkippableFact]
    public async Task A_hint_requiring_a_raise_with_an_overflowing_day_cannot_break_a_later_authoritative_noop()
    {
        RequireSqlCipher();

        DateTimeOffset now = new(2035, 6, 7, 12, 0, 0, TimeSpan.Zero);

        Guid run = await StartRunAsync("overflow-hint-noop", now);

        await SeedSnapshotBillsAsync(run, now, 1);

        BudgetReservationService service = CreateService(new BudgetPolicySettings { Enabled = true, DailyLimitUsd = 1m });

        BudgetReservation existing = await ReserveAsync(service, 0.25m, now.AddHours(1), BudgetReservationService.UtcBudgetPeriod(now));

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await SnapshotSqlAsync(connection, "UPDATE BudgetReservations SET BudgetPeriod = '9999-12-31'");

        service.BeforeWriteTransactionForTesting = _ => SnapshotSqlAsync(connection, "UPDATE BudgetReservations SET Status = 2");

        Assert.True((await service.AdjustAsync(existing.Id, 0.5m)).IsSuccess);

        Assert.Equal(1L, await CountReservationsAsync());

        await AssertConnectionIsNotInATransactionAsync();
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task External_bill_insertion_and_deletion_invalidate_the_actual_read_snapshot(bool delete)
    {
        RequireSqlCipher();

        DateTimeOffset now = new(2035, 6, 7, 12, 0, 0, TimeSpan.Zero);

        Guid run = await StartRunAsync("bill-write-fence", now);

        await SeedSnapshotBillsAsync(run, now, 1);

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        if (delete) await SnapshotSqlAsync(connection, "UPDATE BillableOperations SET ActualCostUsd = '0.75'");

        BudgetReservationService service = CreateService(new BudgetPolicySettings { Enabled = true, DailyLimitUsd = 1m });

        service.UtcNowForTesting = () => now;

        await using ArcanumDbContext other = _fixture.CreateContext(_dbPath);

        SqliteConnection otherConnection = (SqliteConnection)other.Database.GetDbConnection();

        using BudgetNativeTrace trace = new(connection);

        int written = 0;

        trace.RowObserved = statement =>
        {
            if (!statement.IsCommittedFold || Interlocked.Exchange(ref written, 1) != 0) return;

            string sql = delete ? "DELETE FROM BillableOperations" :
                "INSERT INTO BillableOperations (Id, RunId, OperationType, Provider, Model, Purpose, StartedAt, CompletedAt, InputTokens, OutputTokens, ReasoningTokens, CachedTokens, PricingSnapshotJson, ActualCostUsd, Status) "
                + "SELECT 'ffeeddccbbaa99887766554433221100', RunId, OperationType, Provider, Model, Purpose, StartedAt, CompletedAt, InputTokens, OutputTokens, ReasoningTokens, CachedTokens, PricingSnapshotJson, '0.75', Status FROM BillableOperations LIMIT 1";

            SnapshotSqlAsync(otherConnection, sql).GetAwaiter().GetResult();
        };

        Result<BudgetReservation> result = await service.ReserveAsync(new(run, 0.5m, now.AddHours(1), BudgetReservationService.UtcBudgetPeriod(now)));

        Assert.Equal(delete, result.IsSuccess);

        if (!delete) Assert.Equal(ErrorCodes.Budget.Exceeded, result.Error.Code);

        trace.AssertNoCallbackFault();

        NativeBudgetStatement[] folds = trace.Statements.Where(static row => row.IsCommittedFold).ToArray();

        Assert.Equal(2, folds.Length);

        Assert.False(folds[0].InsideTransaction);

        Assert.Equal(1, folds[0].Rows);

        Assert.True(folds[1].InsideTransaction);

        Assert.Equal(delete ? 0 : 2, folds[1].Rows);

        _snapshotOutput.WriteLine($"external {(delete ? "delete" : "insert")}: old rows={folds[0].Rows}/{folds[0].VmSteps}, actual writer rows={folds[1].Rows}/{folds[1].VmSteps}");

        Assert.Equal(delete ? 0.5m : 0m, await service.GetTodayOutstandingReservationsAsync());

        await AssertConnectionIsNotInATransactionAsync();
    }

    private static async Task SnapshotSqlAsync(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> SnapshotStampAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT (SELECT schema_version FROM pragma_schema_version), (SELECT data_version FROM pragma_data_version), total_changes();";

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());

        return $"{reader.GetInt64(0)}:{reader.GetInt64(1)}:{reader.GetInt64(2)}";
    }

    private async Task SeedSnapshotBillsAsync(Guid run, DateTimeOffset now, int count)
    {
        await using DbCommand cmd = _db!.Database.GetDbConnection().CreateCommand();

        cmd.CommandText = """
            WITH RECURSIVE bills(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM bills WHERE n < @count)
            INSERT INTO "BillableOperations"
                ("Id", "RunId", "OperationType", "Provider", "Model", "Purpose", "StartedAt", "CompletedAt",
                 "InputTokens", "OutputTokens", "ReasoningTokens", "CachedTokens", "PricingSnapshotJson", "ActualCostUsd", "Status")
            SELECT printf('%032x', n), @run, 0, 'test', 'model', 'writer-snapshot', @now, @now,
                1, 1, 0, 0, '{}', '0.0001', 0 FROM bills;
            """;

        AddParameter(cmd, "@count", count);

        AddParameter(cmd, "@run", run.ToString("N"));

        AddParameter(cmd, "@now", UtcInstantText.Format(now));

        Assert.Equal(count, await cmd.ExecuteNonQueryAsync());
    }

    private sealed class NativeBudgetStatement(string sql, bool insideTransaction)
    {
        internal string Sql { get; } = sql;

        internal bool InsideTransaction { get; } = insideTransaction;

        internal bool IsCommittedFold => Sql.Contains("SELECT \"ActualCostUsd\"", StringComparison.Ordinal)
            && Sql.Contains("FROM \"BillableOperations\"", StringComparison.Ordinal);

        internal int Rows { get; set; }

        internal int VmSteps { get; set; }
    }

    private sealed class BudgetNativeTrace : IDisposable
    {
        private readonly SqliteConnection _connection;

        private readonly NativeTraceCallback _callback;

        private readonly Dictionary<nint, NativeBudgetStatement> _active = [];

        private Exception? _callbackFault;

        private bool _disposed;

        internal List<NativeBudgetStatement> Statements { get; } = [];

        internal Action<NativeBudgetStatement>? Started { get; set; }

        internal Action<NativeBudgetStatement>? RowObserved { get; set; }

        internal BudgetNativeTrace(SqliteConnection connection)
        {
            _connection = connection;

            _callback = Observe;

            Assert.Equal(0, TraceNative(connection.Handle!.DangerousGetHandle(), 7, _callback, 0));
        }

        private int Observe(uint kind, nint context, nint statement, nint data)
        {
            try
            {
                if (kind == 1)
                {
                    NativeBudgetStatement item = new(Marshal.PtrToStringUTF8(data) ?? string.Empty, raw.sqlite3_get_autocommit(_connection.Handle) == 0);

                    _active[statement] = item;

                    Statements.Add(item);

                    Started?.Invoke(item);
                }
                else if (_active.TryGetValue(statement, out NativeBudgetStatement? item))
                {
                    if (kind == 4)
                    {
                        item.Rows++;

                        RowObserved?.Invoke(item);
                    }

                    if (kind == 2)
                    {
                        item.VmSteps = StatementStatus(statement, raw.SQLITE_STMTSTATUS_VM_STEP, 1);

                        _active.Remove(statement);
                    }
                }
            }
            catch (Exception exception)
            {
                _callbackFault ??= exception;
            }

            return 0;
        }

        internal void AssertNoCallbackFault() => Assert.Null(_callbackFault);

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;

            Assert.Equal(0, TraceNative(_connection.Handle!.DangerousGetHandle(), 0, null, 0));
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NativeTraceCallback(uint kind, nint context, nint statement, nint data);

        [DllImport("e_sqlcipher", EntryPoint = "sqlite3_trace_v2", CallingConvention = CallingConvention.Cdecl)]
        private static extern int TraceNative(nint connection, uint mask, NativeTraceCallback? callback, nint context);

        [DllImport("e_sqlcipher", EntryPoint = "sqlite3_stmt_status", CallingConvention = CallingConvention.Cdecl)]
        private static extern int StatementStatus(nint statement, int operation, int reset);
    }
}
