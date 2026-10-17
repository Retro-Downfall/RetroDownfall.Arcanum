using RetroDownfall.Arcanum.Tests.Support;

using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

/// <summary>
/// The parked-sweep half of the Long Rest sweep store: worker discovery of readmittable parked clean sweeps, Atomic
/// Readmission and the Sweep Failure Budget. Each row parks the fixture's real sweep with direct SQL and then asks the real store.
/// </summary>
[Collection("Grimoire")]
public sealed class LongRestSweepReadmissionTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<string, int, int, string, bool> DiscoveryRows => new()
    {
        // code, readmit count, seconds since the park (-1 = never stamped), expectation label via the bool
        { LongRestParkCodes.Interrupted, 0, -1, "unstamped", true },

        { LongRestParkCodes.Interrupted, 0, 29, "cooling", false },

        { LongRestParkCodes.Interrupted, 0, 30, "due", true },

        { LongRestParkCodes.Interrupted, 1, 59, "cooling", false },

        { LongRestParkCodes.Interrupted, 1, 60, "due", true },

        { LongRestParkCodes.Interrupted, 2, 119, "cooling", false },

        { LongRestParkCodes.Interrupted, 2, 120, "due", true },

        { LongRestParkCodes.Interrupted, 3, 239, "cooling", false },

        { LongRestParkCodes.Interrupted, 3, 240, "due", true },

        { LongRestParkCodes.Interrupted, 4, 100_000, "exhausted", false },

        { LongRestParkCodes.WorkerUnavailable, 0, 30, "due", true },

        { ErrorCodes.LongRest.Unavailable, 0, 30, "due", true },

        { LongRestParkCodes.MaintenanceDeferred, 0, 0, "free", true },

        { LongRestParkCodes.MaintenanceDeferred, 4, 0, "free-when-exhausted", true },

        { LongRestParkCodes.ReadmitRequested, 4, 0, "operator-retry", true },

        { LongRestParkCodes.ReadmitExhausted, 0, 100_000, "needs-operator", false },

        { LongRestParkCodes.AuthorizedRequestRequired, 0, 100_000, "protected-code-on-clean", false },

        { ErrorCodes.LongRest.IntegrityFailure, 0, 100_000, "needs-operator", false },

        { ErrorCodes.LongRest.StaleInput, 0, 100_000, "restart-generation", false },

        { "long_rest.something_new", 0, 100_000, "unknown", false },
    };

    [Theory]
    [MemberData(nameof(DiscoveryRows))]
    public async Task Discovery_offers_a_parked_clean_sweep_only_while_its_fault_budget_and_cooldown_allow(
        string code, int readmitCount, int secondsSincePark, string label, bool runnable)
    {
        ManualWallClockTimeProvider clock = new(Epoch);

        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(
            clock: clock, protectedSources: false);

        await ParkAsync(f, code, readmitCount, secondsSincePark < 0 ? null : Epoch.AddSeconds(-secondsSincePark));

        LongRestRunnableSweep found = LongRestSweepHarness.Require(await Store(f, clock)
            .FindNextRunnableCleanAsync(f.Epoch.Capture().Stamp, null, CancellationToken.None));

        Assert.True(runnable == (found.OperationId == f.Operation), $"{label}: expected runnable={runnable}, found {found.OperationId}");

        if (!runnable && secondsSincePark is >= 0 and < 100_000 && LongRestFaultPolicy.CanReadmit(readmitCount))
        {
            // A cooling sweep names exactly when it becomes due, so the worker's rescan timer can wake for it.
            Assert.Equal(Epoch.AddSeconds(-secondsSincePark) + LongRestFaultPolicy.Cooldown(readmitCount), found.NextDueAt);
        }
        else
        {
            Assert.Null(found.NextDueAt);
        }
    }

    [Fact]
    public async Task Discovery_keeps_fresh_pending_requests_and_never_offers_protected_parks()
    {
        ManualWallClockTimeProvider clock = new(Epoch);

        await using LongRestMaintenanceTests.MaintenanceFixture protectedFixture = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(
            clock: clock, protectedSources: true);

        await ParkAsync(protectedFixture, LongRestParkCodes.AuthorizedRequestRequired, 0, null);

        Assert.Null(LongRestSweepHarness.Require(await Store(protectedFixture, clock)
            .FindNextRunnableCleanAsync(protectedFixture.Epoch.Capture().Stamp, null, CancellationToken.None)).OperationId);

        // The same protected row carrying a resumable clean code is still protected, so the worker never sees it.
        await ParkAsync(protectedFixture, LongRestParkCodes.Interrupted, 0, null);

        Assert.Null(LongRestSweepHarness.Require(await Store(protectedFixture, clock)
            .FindNextRunnableCleanAsync(protectedFixture.Epoch.Capture().Stamp, null, CancellationToken.None)).OperationId);
    }

    [Theory]
    [InlineData("lease-owner")]
    [InlineData("checkpoint-zero")]
    [InlineData("invalidated")]
    [InlineData("retired")]
    [InlineData("other-value-digest")]
    public async Task Discovery_refuses_a_parked_row_whose_structure_is_not_a_clean_resumable_park(string breakage)
    {
        ManualWallClockTimeProvider clock = new(Epoch);

        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(
            clock: clock, protectedSources: false);

        await ParkAsync(f, LongRestParkCodes.Interrupted, 0, null);

        Assert.Equal(f.Operation, LongRestSweepHarness.Require(await Store(f, clock)
            .FindNextRunnableCleanAsync(f.Epoch.Capture().Stamp, null, CancellationToken.None)).OperationId);

        string id = f.Operation.ToString("N");

        switch (breakage)
        {
            case "lease-owner":
                await f.H.ExecuteAsync("UPDATE LongRunningOperations SET LeaseOwner = 'someone' WHERE Id = $id;", ("$id", id));
                break;
            case "checkpoint-zero":
                await f.H.ExecuteAsync("UPDATE LongRunningOperations SET CheckpointVersion = 0 WHERE Id = $id;", ("$id", id));
                break;
            case "invalidated":
                await f.H.ExecuteAsync("UPDATE long_rest_sweeps SET PhaseCode = 7 WHERE OperationId = $id;", ("$id", id));
                break;
            case "retired":
                await f.H.ExecuteAsync("UPDATE long_rest_sweeps SET PhaseCode = 9 WHERE OperationId = $id;", ("$id", id));
                break;
            default:
                await f.H.ExecuteAsync("UPDATE long_rest_sweeps SET ConfigurationJson = json_set(ConfigurationJson, '$.valueDigest', json('{\"bytes\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=\"}')) WHERE OperationId = $id;", ("$id", id));
                break;
        }

        Assert.Null(LongRestSweepHarness.Require(await Store(f, clock)
            .FindNextRunnableCleanAsync(f.Epoch.Capture().Stamp, null, CancellationToken.None)).OperationId);
    }

    [Fact]
    public async Task Discovery_accepts_a_parked_sweep_from_another_boot_because_a_restart_is_why_it_parked()
    {
        ManualWallClockTimeProvider clock = new(Epoch);

        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(
            clock: clock, protectedSources: false);

        await ParkAsync(f, LongRestParkCodes.Interrupted, 0, null);

        LongRestConfigurationStamp prior = f.Epoch.Capture().Stamp;

        await f.RebootAsync();

        Assert.NotEqual(prior.BootId, f.Epoch.Capture().Stamp.BootId);

        Assert.Equal(prior.ValueDigest, f.Epoch.Capture().Stamp.ValueDigest);

        Assert.Equal(f.Operation, LongRestSweepHarness.Require(await Store(f, clock)
            .FindNextRunnableCleanAsync(f.Epoch.Capture().Stamp, null, CancellationToken.None)).OperationId);
    }

    [Fact]
    public async Task Discovery_cursor_skips_the_refused_candidate_for_this_pass()
    {
        ManualWallClockTimeProvider clock = new(Epoch);

        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(
            clock: clock, protectedSources: false);

        await ParkAsync(f, LongRestParkCodes.Interrupted, 0, null);

        LongRestRunnableSweep first = LongRestSweepHarness.Require(await Store(f, clock)
            .FindNextRunnableCleanAsync(f.Epoch.Capture().Stamp, null, CancellationToken.None));

        Assert.Equal(f.Operation, first.OperationId);

        Assert.Null(LongRestSweepHarness.Require(await Store(f, clock)
            .FindNextRunnableCleanAsync(f.Epoch.Capture().Stamp, f.Operation, CancellationToken.None)).OperationId);
    }

    [Theory]
    [InlineData(LongRestParkCodes.Interrupted, 0, 0, 1)]
    [InlineData(LongRestParkCodes.Interrupted, 2, 0, 3)]
    [InlineData(LongRestParkCodes.Interrupted, 3, 0, 4)]
    [InlineData(LongRestParkCodes.MaintenanceDeferred, 3, 0, 3)]
    [InlineData(LongRestParkCodes.MaintenanceDeferred, 4, 0, 4)]
    [InlineData(LongRestParkCodes.ReadmitRequested, 4, 0, 0)]
    [InlineData(LongRestParkCodes.ReadmitRequested, 0, 0, 0)]
    public async Task Atomic_readmission_moves_the_parked_row_straight_to_running_and_updates_the_budget(
        string code, int before, int secondsSincePark, int expectedBudget)
    {
        ManualWallClockTimeProvider clock = new(Epoch);

        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(
            clock: clock, protectedSources: false);

        await ParkAsync(f, code, before, Epoch.AddSeconds(-1_000 - secondsSincePark));

        LongRunningOperation parked = (await f.Operations.GetAsync(f.Operation))!;

        LongRestReadmission? readmitted = LongRestSweepHarness.Require(await Store(f, clock).TryReadmitParkedAsync(f.Operation, parked.Revision,
            f.Partition, f.Epoch.Capture().Stamp, "worker-owner", Epoch, Epoch.AddMinutes(5), CancellationToken.None));

        Assert.NotNull(readmitted);

        Assert.Equal(expectedBudget, readmitted.ReadmitCount);

        LongRunningOperation running = (await f.Operations.GetAsync(f.Operation))!;

        Same(readmitted.Operation, running);

        Assert.Equal(LongRunningOperationState.Running, running.State);

        Assert.Equal("worker-owner", running.LeaseOwner);

        Assert.Equal(Epoch.AddMinutes(5), running.LeaseExpiresAt);

        Assert.Equal(parked.AttemptCount + 1, running.AttemptCount);

        Assert.Equal(parked.Revision + 1, running.Revision);

        Assert.Null(running.TerminalErrorCode);

        Assert.Null(running.CompletedAt);

        Assert.Equal(parked.CheckpointPayload, running.CheckpointPayload);

        Assert.Equal((long)expectedBudget, await f.H.ScalarAsync("SELECT ReadmitCount FROM long_rest_sweeps;"));

        // The park clock restarts at the readmission, so a park nobody stamped cools from its last readmission.
        Assert.Equal(UtcInstantText.Format(Epoch), await f.H.ScalarAsync("SELECT ParkedAt FROM long_rest_sweeps;"));
    }

    [Fact]
    public async Task A_crash_park_nobody_stamped_cools_down_from_its_last_readmission()
    {
        ManualWallClockTimeProvider clock = new(Epoch);

        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(
            clock: clock, protectedSources: false);

        await ParkAsync(f, LongRestParkCodes.Interrupted, 0, null);

        LongRunningOperation parked = (await f.Operations.GetAsync(f.Operation))!;

        Assert.NotNull(LongRestSweepHarness.Require(await Store(f, clock).TryReadmitParkedAsync(f.Operation, parked.Revision,
            f.Partition, f.Epoch.Capture().Stamp, "worker-owner", Epoch, Epoch.AddMinutes(5), CancellationToken.None)));

        // The process dies mid-run and the pure recovery handler parks the row again. It cannot stamp the sweep, so the
        // only clock left is the readmission's own.
        await f.H.ExecuteAsync("UPDATE LongRunningOperations SET State = $attention, LeaseOwner = NULL, LeaseExpiresAt = NULL, "
            + "TerminalErrorCode = $code, Revision = Revision + 1 WHERE Id = $id;", ("$attention", (int)LongRunningOperationState.ReconciliationRequired),
            ("$code", LongRestParkCodes.Interrupted), ("$id", f.Operation.ToString("N")));

        TimeSpan cooldown = LongRestFaultPolicy.Cooldown(1);

        clock.Advance(cooldown - TimeSpan.FromSeconds(1));

        LongRestRunnableSweep cooling = LongRestSweepHarness.Require(await Store(f, clock)
            .FindNextRunnableCleanAsync(f.Epoch.Capture().Stamp, null, CancellationToken.None));

        Assert.Null(cooling.OperationId);

        Assert.Equal(Epoch + cooldown, cooling.NextDueAt);

        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(f.Operation, LongRestSweepHarness.Require(await Store(f, clock)
            .FindNextRunnableCleanAsync(f.Epoch.Capture().Stamp, null, CancellationToken.None)).OperationId);
    }

    /// <summary>
    /// A sweep that dies in the same place every time is readmitted until its budget is spent and then keeps
    /// the crash park's own code, so the one thing an operator reads for it is "interrupted", and nothing
    /// automatic or requested will move it again.
    /// </summary>
    /// <remarks>
    /// Only the coordinator's own park path turns a spent budget into <c>long_rest.readmit_exhausted</c>, and a
    /// crash never runs it: the pure recovery handler parks the row without reading the budget. So the registry's
    /// guidance for <c>long_rest.interrupted</c> cannot say "wait" without qualifying it, and what it names for this
    /// state (retry or cancel, and the refusal a new request gets) is exactly what this pins.
    /// </remarks>
    [Fact]
    public async Task A_crash_loop_that_commits_no_progress_ends_parked_as_interrupted_with_a_spent_budget_and_waits_for_an_operator()
    {
        ManualWallClockTimeProvider clock = new(Epoch);

        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(
            clock: clock, protectedSources: false);

        await ParkAsync(f, LongRestParkCodes.Interrupted, 0, null);

        DateTimeOffset now = Epoch;

        for (int readmission = 1; readmission <= LongRestFaultPolicy.MaxReadmissions; readmission++)
        {
            if (readmission > 1)
            {
                // The park is not stamped by the recovery handler, so the cooldown runs from the last readmission.
                now += LongRestFaultPolicy.Cooldown(readmission - 1);

                clock.Advance(LongRestFaultPolicy.Cooldown(readmission - 1));
            }

            LongRunningOperation parked = (await f.Operations.GetAsync(f.Operation))!;

            LongRestReadmission? readmitted = LongRestSweepHarness.Require(await Store(f, clock).TryReadmitParkedAsync(f.Operation, parked.Revision,
                f.Partition, f.Epoch.Capture().Stamp, "worker-owner", now, now.AddMinutes(5), CancellationToken.None));

            Assert.NotNull(readmitted);

            Assert.Equal(readmission, readmitted.ReadmitCount);

            // The process dies mid-run with no progress committed; its lease lapses and recovery parks the row again,
            // under the crash park's code and without touching the sweep.
            await f.H.ExecuteAsync("UPDATE LongRunningOperations SET State = $attention, LeaseOwner = NULL, LeaseExpiresAt = NULL, "
                + "TerminalErrorCode = $code, Revision = Revision + 1 WHERE Id = $id;", ("$attention", (int)LongRunningOperationState.ReconciliationRequired),
                ("$code", LongRestParkCodes.Interrupted), ("$id", f.Operation.ToString("N")));
        }

        LongRunningOperation exhausted = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, exhausted.State);

        Assert.Equal(LongRestParkCodes.Interrupted, exhausted.TerminalErrorCode);

        Assert.Equal((long)LongRestFaultPolicy.MaxReadmissions, await f.H.ScalarAsync("SELECT ReadmitCount FROM long_rest_sweeps;"));

        // However long it waits, the worker is offered nothing and no cooldown is due for it.
        clock.Advance(TimeSpan.FromDays(1));

        LongRestRunnableSweep found = LongRestSweepHarness.Require(await Store(f, clock)
            .FindNextRunnableCleanAsync(f.Epoch.Capture().Stamp, null, CancellationToken.None));

        Assert.Null(found.OperationId);

        Assert.Null(found.NextDueAt);

        Assert.Null(LongRestSweepHarness.Require(await Store(f, clock).TryReadmitParkedAsync(f.Operation, exhausted.Revision,
            f.Partition, f.Epoch.Capture().Stamp, "worker-owner", now.AddDays(1), now.AddDays(1).AddMinutes(5), CancellationToken.None)));

        // And a new request for the scope is refused, naming the operation the operator is to retry or cancel.
        Result<LongRunningOperationDto> request = await f.Coordinator.RequestAsync(f.Scope, f.Partition, CancellationToken.None);

        Assert.True(request.IsFailure);

        Assert.Equal(ErrorCodes.LongRest.SweepParked, request.Error.Code);

        Assert.Contains(f.Operation.ToString("D"), request.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Atomic_readmission_never_passes_through_Pending()
    {
        ManualWallClockTimeProvider clock = new(Epoch);

        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(
            clock: clock, protectedSources: false);

        await ParkAsync(f, LongRestParkCodes.Interrupted, 0, null);

        string id = f.Operation.ToString("N");

        // A Pending row with a prior attempt is exactly what the reconciler's expiry discovery adopts, so any transition to
        // it would let a concurrent recovery pass see a half-readmitted sweep. The trigger refuses it and logs the rest.
        await f.H.ExecuteAsync("CREATE TEMP TABLE readmission_state_log (Seq INTEGER PRIMARY KEY AUTOINCREMENT, OldState INTEGER, NewState INTEGER);");

        await f.H.ExecuteAsync($"CREATE TEMP TRIGGER readmission_log AFTER UPDATE OF State ON LongRunningOperations WHEN NEW.Id = '{id}' "
            + "BEGIN INSERT INTO readmission_state_log (OldState, NewState) VALUES (OLD.State, NEW.State); END;");

        await f.H.ExecuteAsync($"CREATE TEMP TRIGGER readmission_refuse_pending BEFORE UPDATE OF State ON LongRunningOperations WHEN NEW.Id = '{id}' "
            + $"AND NEW.State = {(int)LongRunningOperationState.Pending} BEGIN SELECT RAISE(ABORT, 'a readmission may not pass through Pending'); END;");

        LongRunningOperation parked = (await f.Operations.GetAsync(f.Operation))!;

        Assert.NotNull(LongRestSweepHarness.Require(await Store(f, clock).TryReadmitParkedAsync(f.Operation, parked.Revision,
            f.Partition, f.Epoch.Capture().Stamp, "worker-owner", Epoch, Epoch.AddMinutes(5), CancellationToken.None)));

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM readmission_state_log;"));

        Assert.Equal((long)LongRunningOperationState.ReconciliationRequired, await f.H.ScalarAsync("SELECT OldState FROM readmission_state_log;"));

        Assert.Equal((long)LongRunningOperationState.Running, await f.H.ScalarAsync("SELECT NewState FROM readmission_state_log;"));

        // Detector control: the retired route (an operator-style reset, then a lease) does trip the Pending refusal.
        await f.H.ExecuteAsync($"UPDATE LongRunningOperations SET State = {(int)LongRunningOperationState.ReconciliationRequired}, LeaseOwner = NULL, LeaseExpiresAt = NULL WHERE Id = '{id}';");

        LongRunningOperation reparked = (await f.Operations.GetAsync(f.Operation))!;

        Assert.NotNull(await Record.ExceptionAsync(() => f.Operations.ResetForRetryAsync(f.Operation, reparked.Revision, Epoch)));
    }

    [Fact]
    public async Task Atomic_readmission_is_invisible_to_expiry_discovery_before_and_after()
    {
        ManualWallClockTimeProvider clock = new(Epoch);

        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(
            clock: clock, protectedSources: false);

        await ParkAsync(f, LongRestParkCodes.Interrupted, 0, null);

        Assert.DoesNotContain(await f.Operations.FindExpiredAsync(Epoch.AddHours(1), 100), operation => operation.Id == f.Operation);

        LongRunningOperation parked = (await f.Operations.GetAsync(f.Operation))!;

        Assert.NotNull(LongRestSweepHarness.Require(await Store(f, clock).TryReadmitParkedAsync(f.Operation, parked.Revision,
            f.Partition, f.Epoch.Capture().Stamp, "worker-owner", Epoch, Epoch.AddMinutes(5), CancellationToken.None)));

        // Running under a live lease: nothing for expiry discovery to adopt, at any instant inside the lease. The instant
        // between the two writes is covered by Atomic_readmission_never_passes_through_Pending, which watches every write.
        Assert.DoesNotContain(await f.Operations.FindExpiredAsync(Epoch.AddMinutes(4), 100), operation => operation.Id == f.Operation);
    }

    [Fact]
    public async Task Atomic_readmission_refuses_when_the_budget_is_spent_or_the_cooldown_has_not_elapsed()
    {
        ManualWallClockTimeProvider clock = new(Epoch);

        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(
            clock: clock, protectedSources: false);

        // Exhausted: four readmissions are already spent.
        await ParkAsync(f, LongRestParkCodes.Interrupted, 4, Epoch.AddDays(-1));

        LongRunningOperation parked = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Null(LongRestSweepHarness.Require(await Store(f, clock).TryReadmitParkedAsync(f.Operation, parked.Revision,
            f.Partition, f.Epoch.Capture().Stamp, "worker-owner", Epoch, Epoch.AddMinutes(5), CancellationToken.None)));

        // Cooling: parked 29 seconds ago with no readmission spent.
        await ParkAsync(f, LongRestParkCodes.Interrupted, 0, Epoch.AddSeconds(-29));

        parked = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Null(LongRestSweepHarness.Require(await Store(f, clock).TryReadmitParkedAsync(f.Operation, parked.Revision,
            f.Partition, f.Epoch.Capture().Stamp, "worker-owner", Epoch, Epoch.AddMinutes(5), CancellationToken.None)));

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, (await f.Operations.GetAsync(f.Operation))!.State);

        Same(parked, (await f.Operations.GetAsync(f.Operation))!);

        // Due: one second later the same call succeeds.
        Assert.NotNull(LongRestSweepHarness.Require(await Store(f, clock).TryReadmitParkedAsync(f.Operation, parked.Revision,
            f.Partition, f.Epoch.Capture().Stamp, "worker-owner", Epoch.AddSeconds(1), Epoch.AddMinutes(5), CancellationToken.None)));
    }

    [Theory]
    [InlineData("stale-revision")]
    [InlineData("not-parked")]
    [InlineData("code-not-resumable")]
    [InlineData("other-value-digest")]
    public async Task Atomic_readmission_returns_nothing_when_the_row_moved_or_is_not_a_resumable_park(string drift)
    {
        ManualWallClockTimeProvider clock = new(Epoch);

        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(
            clock: clock, protectedSources: false);

        await ParkAsync(f, LongRestParkCodes.Interrupted, 0, null);

        LongRunningOperation parked = (await f.Operations.GetAsync(f.Operation))!;

        string id = f.Operation.ToString("N");

        long revision = parked.Revision;

        switch (drift)
        {
            case "stale-revision":
                revision--;
                break;
            case "not-parked":
                await f.H.ExecuteAsync($"UPDATE LongRunningOperations SET State = {(int)LongRunningOperationState.Running}, LeaseOwner = 'live', LeaseExpiresAt = '2099-01-01T00:00:00.0000000Z', Revision = Revision + 1 WHERE Id = '{id}';");
                revision++;
                break;
            case "code-not-resumable":
                await f.H.ExecuteAsync($"UPDATE LongRunningOperations SET TerminalErrorCode = 'LongRest.IntegrityFailure' WHERE Id = '{id}';");
                break;
            default:
                await f.H.ExecuteAsync("UPDATE long_rest_sweeps SET ConfigurationJson = json_set(ConfigurationJson, '$.valueDigest', json('{\"bytes\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=\"}')) WHERE OperationId = $id;", ("$id", id));
                break;
        }

        LongRunningOperation before = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Null(LongRestSweepHarness.Require(await Store(f, clock).TryReadmitParkedAsync(f.Operation, revision,
            f.Partition, f.Epoch.Capture().Stamp, "worker-owner", Epoch, Epoch.AddMinutes(5), CancellationToken.None)));

        Same(before, (await f.Operations.GetAsync(f.Operation))!);
    }

    [Fact]
    public async Task Atomic_readmission_fails_closed_for_a_different_scope_or_a_malformed_request()
    {
        ManualWallClockTimeProvider clock = new(Epoch);

        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(
            clock: clock, protectedSources: false);

        await ParkAsync(f, LongRestParkCodes.Interrupted, 0, null);

        LongRunningOperation parked = (await f.Operations.GetAsync(f.Operation))!;

        LongRestSweepScope other = LongRestSweepScope.Create(new(LongRestSweepScopeKind.Campaign, Guid.NewGuid())).Value;

        Result<LongRestReadmission?> wrongScope = await Store(f, clock).TryReadmitParkedAsync(f.Operation, parked.Revision, other,
            f.Epoch.Capture().Stamp, "worker-owner", Epoch, Epoch.AddMinutes(5), CancellationToken.None);

        Assert.True(wrongScope.IsFailure);

        Assert.True((await Store(f, clock).TryReadmitParkedAsync(f.Operation, parked.Revision, f.Partition, f.Epoch.Capture().Stamp,
            " ", Epoch, Epoch.AddMinutes(5), CancellationToken.None)).IsFailure);

        Assert.True((await Store(f, clock).TryReadmitParkedAsync(f.Operation, parked.Revision, f.Partition, f.Epoch.Capture().Stamp,
            "worker-owner", Epoch, Epoch, CancellationToken.None)).IsFailure);

        Assert.True((await Store(f, clock).TryReadmitParkedAsync(Guid.NewGuid(), 0, f.Partition, f.Epoch.Capture().Stamp,
            "worker-owner", Epoch, Epoch.AddMinutes(5), CancellationToken.None)).IsFailure);

        Same(parked, (await f.Operations.GetAsync(f.Operation))!);
    }

    [Fact]
    public async Task A_protected_park_is_readmitted_only_from_authorized_request_required_and_keeps_its_budget()
    {
        ManualWallClockTimeProvider clock = new(Epoch);

        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(
            clock: clock, protectedSources: true);

        await ParkAsync(f, LongRestParkCodes.Interrupted, 0, null);

        LongRunningOperation parked = (await f.Operations.GetAsync(f.Operation))!;

        // A resumable clean code does not authorise a protected readmission.
        Assert.Null(LongRestSweepHarness.Require(await Store(f, clock).TryReadmitParkedAsync(f.Operation, parked.Revision,
            f.Partition, f.Epoch.Capture().Stamp, "session-owner", Epoch, Epoch.AddMinutes(5), CancellationToken.None)));

        await ParkAsync(f, LongRestParkCodes.AuthorizedRequestRequired, 2, null);

        parked = (await f.Operations.GetAsync(f.Operation))!;

        LongRestReadmission? readmitted = LongRestSweepHarness.Require(await Store(f, clock).TryReadmitParkedAsync(f.Operation, parked.Revision,
            f.Partition, f.Epoch.Capture().Stamp, "session-owner", Epoch, Epoch.AddMinutes(5), CancellationToken.None));

        Assert.NotNull(readmitted);

        Assert.Equal(2, readmitted.ReadmitCount);

        Assert.Equal(LongRunningOperationState.Running, readmitted.Operation.State);

        Assert.Equal("session-owner", readmitted.Operation.LeaseOwner);
    }

    [Fact]
    public async Task Marking_a_park_stamps_when_the_cooldown_starts()
    {
        ManualWallClockTimeProvider clock = new(Epoch);

        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(
            clock: clock, protectedSources: false);

        Assert.Equal(DBNull.Value, await f.H.ScalarAsync("SELECT ParkedAt FROM long_rest_sweeps;"));

        Assert.True((await Store(f, clock).MarkParkedAsync(f.Operation, Epoch, CancellationToken.None)).IsSuccess);

        Assert.Equal(UtcInstantText.Format(Epoch), await f.H.ScalarAsync("SELECT ParkedAt FROM long_rest_sweeps;"));

        Assert.True((await Store(f, clock).MarkParkedAsync(Guid.Empty, Epoch, CancellationToken.None)).IsFailure);

        Assert.Equal(UtcInstantText.Format(Epoch), await f.H.ScalarAsync("SELECT ParkedAt FROM long_rest_sweeps;"));
    }

    [Fact]
    public async Task Marking_a_park_returns_the_durable_budget_it_leaves_behind_and_refuses_a_closed_sweep()
    {
        ManualWallClockTimeProvider clock = new(Epoch);

        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(
            clock: clock, protectedSources: false);

        Assert.Equal(0, LongRestSweepHarness.Require(await Store(f, clock).MarkParkedAsync(f.Operation, Epoch, CancellationToken.None)));

        // The count is read where the stamp is written, so whatever progress has done to it since the readmission shows.
        await f.H.ExecuteAsync("UPDATE long_rest_sweeps SET ReadmitCount = 3;");

        Assert.Equal(3, LongRestSweepHarness.Require(await Store(f, clock).MarkParkedAsync(f.Operation, Epoch.AddSeconds(5), CancellationToken.None)));

        Assert.Equal(UtcInstantText.Format(Epoch.AddSeconds(5)), await f.H.ScalarAsync("SELECT ParkedAt FROM long_rest_sweeps;"));

        // A retired sweep has no park to stamp: the write fails closed and leaves the row alone.
        await f.H.ExecuteAsync("UPDATE long_rest_sweeps SET PhaseCode = 9;");

        Assert.True((await Store(f, clock).MarkParkedAsync(f.Operation, Epoch.AddSeconds(9), CancellationToken.None)).IsFailure);

        Assert.Equal(UtcInstantText.Format(Epoch.AddSeconds(5)), await f.H.ScalarAsync("SELECT ParkedAt FROM long_rest_sweeps;"));
    }

    [Fact]
    public async Task The_failure_budget_columns_default_to_zero_and_unparked()
    {
        await using LongRestMaintenanceTests.MaintenanceFixture f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(protectedSources: false);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT ReadmitCount FROM long_rest_sweeps;"));

        Assert.Equal(DBNull.Value, await f.H.ScalarAsync("SELECT ParkedAt FROM long_rest_sweeps;"));

        Exception? negative = await Record.ExceptionAsync(() => f.H.ExecuteAsync("UPDATE long_rest_sweeps SET ReadmitCount = -1;"));

        Assert.NotNull(negative);

        Exception? text = await Record.ExceptionAsync(() => f.H.ExecuteAsync("UPDATE long_rest_sweeps SET ReadmitCount = 'many';"));

        Assert.NotNull(text);
    }

    private static void Same(LongRunningOperation expected, LongRunningOperation actual)
    {
        // The only array field is compared by content; every other record field must be exactly equal.
        Assert.Equal(expected with { CheckpointPayload = null }, actual with { CheckpointPayload = null });

        Assert.Equal<byte>(expected.CheckpointPayload ?? [], actual.CheckpointPayload ?? []);
    }

    private static LongRestSweepStore Store(LongRestMaintenanceTests.MaintenanceFixture f, TimeProvider clock) => new(f.H.Saga.Context, clock);

    private static Task ParkAsync(LongRestMaintenanceTests.MaintenanceFixture f, string code, int readmitCount, DateTimeOffset? parkedAt) =>
        LongRestMaintenanceTests.ParkWithSqlAsync(f, code, readmitCount, parkedAt);
}
