using RetroDownfall.Arcanum.Core.Cli;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Diagnostics;

/// <summary>
/// The diagnostic spine (DESIGN §4.4.2). Selection, aggregation, and the legacy status projection
/// are the parts every check and repair inherits, so they are pinned here rather than re-asserted
/// per subsystem. The invariant that matters most is containment: one misbehaving check must never
/// take down the command an operator runs precisely because their installation is broken.
/// </summary>
public sealed class DoctorDiagnosticRunnerTests
{

    [Theory]
    [InlineData(DoctorOutcome.Healthy, "ok")]
    [InlineData(DoctorOutcome.Skipped, "ok")]
    [InlineData(DoctorOutcome.Unavailable, "warn")]
    [InlineData(DoctorOutcome.Degraded, "warn")]
    [InlineData(DoctorOutcome.Unhealthy, "fail")]
    public void Legacy_status_is_derived_from_the_outcome(DoctorOutcome outcome, string expected)
    {

        Assert.Equal(expected, DoctorOutcomes.ToLegacyStatus(outcome));

    }

    [Fact]
    public void Aggregate_of_no_findings_is_healthy()
    {

        Assert.Equal(DoctorOutcome.Healthy, DoctorOutcomes.Aggregate([]));

    }

    [Fact]
    public void Aggregate_takes_the_most_severe_outcome()
    {

        Assert.Equal(
            DoctorOutcome.Unhealthy,
            DoctorOutcomes.Aggregate(
                [DoctorOutcome.Healthy, DoctorOutcome.Unhealthy, DoctorOutcome.Degraded]));

        Assert.Equal(
            DoctorOutcome.Degraded,
            DoctorOutcomes.Aggregate([DoctorOutcome.Unavailable, DoctorOutcome.Degraded]));

    }

    [Fact]
    public void Skipped_findings_never_worsen_the_aggregate()
    {

        Assert.Equal(
            DoctorOutcome.Healthy,
            DoctorOutcomes.Aggregate([DoctorOutcome.Skipped, DoctorOutcome.Healthy]));

        Assert.Equal(DoctorOutcome.Healthy, DoctorOutcomes.Aggregate([DoctorOutcome.Skipped]));

    }

    [Theory]
    [InlineData(DoctorOutcome.Healthy, false, false)]
    [InlineData(DoctorOutcome.Skipped, true, false)]
    [InlineData(DoctorOutcome.Degraded, false, false)]
    [InlineData(DoctorOutcome.Degraded, true, true)]
    [InlineData(DoctorOutcome.Unavailable, false, false)]
    [InlineData(DoctorOutcome.Unavailable, true, true)]
    [InlineData(DoctorOutcome.Unhealthy, false, true)]
    [InlineData(DoctorOutcome.Unhealthy, true, true)]
    public void Failure_requires_unhealthy_unless_strict_is_requested(
        DoctorOutcome outcome,
        bool strict,
        bool expected)
    {

        Assert.Equal(expected, DoctorOutcomes.IsFailure(outcome, strict));

    }

    [Fact]
    public async Task Run_projects_every_check_into_a_report_check_carrying_its_id_and_subsystem()
    {

        DoctorDiagnosticRunner runner = new(
            [
                new StubCheck("paths.managed_directories", "Paths", DoctorSubsystem.Paths),
                new StubCheck("grimoire.integrity", "Grimoire integrity", DoctorSubsystem.Grimoire),
            ],
            []);

        Result<DoctorReport> report = await runner.RunAsync(DoctorRunRequest.ReadOnly, CancellationToken.None);

        Assert.True(report.IsSuccess);

        Assert.Collection(
            report.Value.Checks,
            check =>
            {

                Assert.Equal("paths.managed_directories", check.Id);

                Assert.Equal(DoctorSubsystem.Paths, check.Subsystem);

                Assert.Equal("Paths", check.Name);

                Assert.Equal("ok", check.Status);

            },
            check => Assert.Equal("grimoire.integrity", check.Id));

    }

    [Fact]
    public async Task Network_checks_are_skipped_unless_the_operator_opts_in()
    {

        DoctorDiagnosticRunner runner = new(
            [new StubCheck("providers.reachability", "Providers", DoctorSubsystem.Providers, requiresNetwork: true)],
            []);

        Result<DoctorReport> offline = await runner.RunAsync(
            DoctorRunRequest.ReadOnly,
            CancellationToken.None);

        DoctorCheck skipped = Assert.Single(offline.Value.Checks);

        Assert.Equal(DoctorOutcome.Skipped, skipped.Outcome);

        Assert.Contains("--include-network", skipped.Detail);

        Result<DoctorReport> online = await runner.RunAsync(
            DoctorRunRequest.ReadOnly with { IncludeNetwork = true },
            CancellationToken.None);

        Assert.Equal(DoctorOutcome.Healthy, Assert.Single(online.Value.Checks).Outcome);

    }

    [Fact]
    public async Task Only_selects_by_check_id_or_by_subsystem()
    {

        DoctorDiagnosticRunner runner = new(
            [
                new StubCheck("paths.managed_directories", "Paths", DoctorSubsystem.Paths),
                new StubCheck("grimoire.integrity", "Grimoire", DoctorSubsystem.Grimoire),
                new StubCheck("grimoire.wal_size", "WAL", DoctorSubsystem.Grimoire),
            ],
            []);

        Result<DoctorReport> byId = await runner.RunAsync(
            DoctorRunRequest.ReadOnly with { Only = ["paths.managed_directories"] },
            CancellationToken.None);

        Assert.Equal("paths.managed_directories", Assert.Single(byId.Value.Checks).Id);

        Result<DoctorReport> bySubsystem = await runner.RunAsync(
            DoctorRunRequest.ReadOnly with { Only = ["grimoire"] },
            CancellationToken.None);

        Assert.Equal(2, bySubsystem.Value.Checks.Count);

    }

    [Fact]
    public async Task Skip_removes_the_named_check_and_keeps_the_rest()
    {

        DoctorDiagnosticRunner runner = new(
            [
                new StubCheck("paths.managed_directories", "Paths", DoctorSubsystem.Paths),
                new StubCheck("grimoire.integrity", "Grimoire", DoctorSubsystem.Grimoire),
            ],
            []);

        Result<DoctorReport> report = await runner.RunAsync(
            DoctorRunRequest.ReadOnly with { Skip = ["Grimoire"] },
            CancellationToken.None);

        Assert.Equal("paths.managed_directories", Assert.Single(report.Value.Checks).Id);

    }

    [Fact]
    public async Task An_unrecognized_selector_fails_and_names_the_discovery_command()
    {

        DoctorDiagnosticRunner runner = new(
            [new StubCheck("paths.managed_directories", "Paths", DoctorSubsystem.Paths)],
            []);

        Result<DoctorReport> report = await runner.RunAsync(
            DoctorRunRequest.ReadOnly with { Only = ["paths.nope"] },
            CancellationToken.None);

        Assert.True(report.IsFailure);

        Assert.Equal(DoctorErrors.UnknownSelectorCode, report.Error.Code);

        Assert.Contains("arcanum doctor list", report.Error.Message);

        Assert.Contains("paths.nope", report.Error.Message);

    }

    [Fact]
    public async Task A_check_that_throws_degrades_to_unavailable_instead_of_killing_the_command()
    {

        DoctorDiagnosticRunner runner = new(
            [
                new ThrowingCheck("grimoire.integrity", DoctorSubsystem.Grimoire),
                new StubCheck("paths.managed_directories", "Paths", DoctorSubsystem.Paths),
            ],
            []);

        Result<DoctorReport> report = await runner.RunAsync(
            DoctorRunRequest.ReadOnly,
            CancellationToken.None);

        Assert.True(report.IsSuccess);

        Assert.Equal(2, report.Value.Checks.Count);

        DoctorCheck failed = report.Value.Checks[0];

        Assert.Equal(DoctorOutcome.Unavailable, failed.Outcome);

        Assert.Contains("InvalidOperationException", failed.Detail);

    }

    [Fact]
    public async Task A_check_that_throws_never_leaks_the_exception_message()
    {

        DoctorDiagnosticRunner runner = new(
            [new ThrowingCheck("grimoire.integrity", DoctorSubsystem.Grimoire)],
            []);

        Result<DoctorReport> report = await runner.RunAsync(
            DoctorRunRequest.ReadOnly,
            CancellationToken.None);

        Assert.DoesNotContain(ThrowingCheck.SecretBearingMessage, Assert.Single(report.Value.Checks).Detail);

    }

    [Fact]
    public async Task Operator_cancellation_is_never_swallowed_into_a_finding()
    {

        using CancellationTokenSource cancellation = new();

        await cancellation.CancelAsync();

        DoctorDiagnosticRunner runner = new(
            [new CancellingCheck("grimoire.integrity", DoctorSubsystem.Grimoire)],
            []);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunAsync(DoctorRunRequest.ReadOnly, cancellation.Token));

    }

    [Fact]
    public async Task Report_health_follows_the_aggregate_and_honors_strict()
    {

        DoctorDiagnosticRunner runner = new(
            [new StubCheck("runtime.pid_file", "PID", DoctorSubsystem.Runtime, outcome: DoctorOutcome.Degraded)],
            []);

        Result<DoctorReport> lenient = await runner.RunAsync(
            DoctorRunRequest.ReadOnly,
            CancellationToken.None);

        Assert.True(lenient.Value.Healthy);

        Assert.Equal(DoctorOutcome.Degraded, lenient.Value.Outcome);

        Result<DoctorReport> strict = await runner.RunAsync(
            DoctorRunRequest.ReadOnly with { Strict = true },
            CancellationToken.None);

        Assert.False(strict.Value.Healthy);

    }

    [Fact]
    public async Task A_repair_selector_without_apply_plans_only()
    {

        StubRepair repair = new("runtime.remove_stale_pid", ["runtime.pid_file"]);

        DoctorDiagnosticRunner runner = new(
            [new StubCheck("runtime.pid_file", "PID", DoctorSubsystem.Runtime)],
            [repair]);

        Result<DoctorReport> report = await runner.RunAsync(
            DoctorRunRequest.ReadOnly with { Repairs = ["runtime.remove_stale_pid"] },
            CancellationToken.None);

        Assert.Equal(0, repair.AppliedCount);

        Assert.Equal(1, repair.PlannedCount);

        DoctorRepairResult result = Assert.Single(report.Value.Repairs ?? []);

        Assert.Equal(DoctorRepairState.Planned, result.State);

    }

    [Fact]
    public async Task Apply_runs_each_selected_repair_exactly_once()
    {

        StubRepair pid = new("runtime.remove_stale_pid", ["runtime.pid_file"]);

        StubRepair permissions = new("permissions.apply_owner_only", ["permissions.posture"]);

        DoctorDiagnosticRunner runner = new(
            [
                new StubCheck("runtime.pid_file", "PID", DoctorSubsystem.Runtime),
                new StubCheck("permissions.posture", "Permissions", DoctorSubsystem.Permissions),
            ],
            [pid, permissions]);

        Result<DoctorReport> report = await runner.RunAsync(
            DoctorRunRequest.ReadOnly with
            {
                Repairs = ["runtime.remove_stale_pid", "permissions.apply_owner_only"],
                Apply = true,
            },
            CancellationToken.None);

        Assert.Equal(1, pid.AppliedCount);

        Assert.Equal(1, permissions.AppliedCount);

        Assert.Equal(2, (report.Value.Repairs ?? []).Count);

    }

    [Fact]
    public async Task A_failed_repair_makes_the_report_unhealthy()
    {

        DoctorDiagnosticRunner runner = new(
            [new StubCheck("runtime.pid_file", "PID", DoctorSubsystem.Runtime)],
            [new StubRepair("runtime.remove_stale_pid", ["runtime.pid_file"], failOnApply: true)]);

        Result<DoctorReport> report = await runner.RunAsync(
            DoctorRunRequest.ReadOnly with { Repairs = ["runtime.remove_stale_pid"], Apply = true },
            CancellationToken.None);

        Assert.False(report.Value.Healthy);

        Assert.Equal(DoctorRepairState.Failed, Assert.Single(report.Value.Repairs ?? []).State);

    }

    [Fact]
    public async Task A_repair_that_throws_is_reported_as_failed_without_leaking_its_message()
    {

        DoctorDiagnosticRunner runner = new(
            [new StubCheck("runtime.pid_file", "PID", DoctorSubsystem.Runtime)],
            [new StubRepair("runtime.remove_stale_pid", ["runtime.pid_file"], throwOnApply: true)]);

        Result<DoctorReport> report = await runner.RunAsync(
            DoctorRunRequest.ReadOnly with { Repairs = ["runtime.remove_stale_pid"], Apply = true },
            CancellationToken.None);

        DoctorRepairResult result = Assert.Single(report.Value.Repairs ?? []);

        Assert.Equal(DoctorRepairState.Failed, result.State);

        Assert.DoesNotContain(StubRepair.SecretBearingMessage, result.Failure);

    }

    [Fact]
    public async Task An_unrecognized_repair_id_fails_and_names_the_discovery_command()
    {

        DoctorDiagnosticRunner runner = new([], []);

        Result<DoctorReport> report = await runner.RunAsync(
            DoctorRunRequest.ReadOnly with { Repairs = ["runtime.nope"] },
            CancellationToken.None);

        Assert.True(report.IsFailure);

        Assert.Equal(DoctorErrors.UnknownSelectorCode, report.Error.Code);

        Assert.Contains("arcanum doctor list", report.Error.Message);

    }

    [Fact]
    public async Task Apply_without_a_repair_selector_is_rejected_rather_than_repairing_everything()
    {

        DoctorDiagnosticRunner runner = new(
            [new StubCheck("runtime.pid_file", "PID", DoctorSubsystem.Runtime)],
            [new StubRepair("runtime.remove_stale_pid", ["runtime.pid_file"])]);

        Result<DoctorReport> report = await runner.RunAsync(
            DoctorRunRequest.ReadOnly with { Apply = true },
            CancellationToken.None);

        Assert.True(report.IsFailure);

        Assert.Equal(DoctorErrors.ApplyWithoutRepairCode, report.Error.Code);

    }

    [Fact]
    public void Duplicate_check_ids_are_rejected_at_construction()
    {

        Assert.Throws<InvalidOperationException>(() => new DoctorDiagnosticRunner(
            [
                new StubCheck("runtime.pid_file", "PID", DoctorSubsystem.Runtime),
                new StubCheck("runtime.pid_file", "PID again", DoctorSubsystem.Runtime),
            ],
            []));

    }

    [Fact]
    public void A_repair_whose_detector_is_not_registered_is_rejected_at_construction()
    {

        Assert.Throws<InvalidOperationException>(() => new DoctorDiagnosticRunner(
            [new StubCheck("runtime.pid_file", "PID", DoctorSubsystem.Runtime)],
            [new StubRepair("runtime.remove_stale_pid", ["runtime.absent_detector"])]));

    }

    [Fact]
    public void Every_check_id_uses_its_subsystem_as_the_namespace()
    {

        Assert.Throws<InvalidOperationException>(() => new DoctorDiagnosticRunner(
            [new StubCheck("grimoire.integrity", "Mislabelled", DoctorSubsystem.Runtime)],
            []));

    }

    private sealed class StubCheck(
        string id,
        string name,
        DoctorSubsystem subsystem,
        bool requiresNetwork = false,
        DoctorOutcome outcome = DoctorOutcome.Healthy) : IDoctorCheck
    {

        public string Id => id;

        public string Name => name;

        public DoctorSubsystem Subsystem => subsystem;

        public bool RequiresNetwork => requiresNetwork;

        public Task<DoctorFinding> InspectAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new DoctorFinding(outcome, $"{id} inspected"));

    }

    private sealed class ThrowingCheck(string id, DoctorSubsystem subsystem) : IDoctorCheck
    {

        internal const string SecretBearingMessage = "passphrase=hunter2";

        public string Id => id;

        public string Name => id;

        public DoctorSubsystem Subsystem => subsystem;

        public bool RequiresNetwork => false;

        public Task<DoctorFinding> InspectAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException(SecretBearingMessage);

    }

    private sealed class CancellingCheck(string id, DoctorSubsystem subsystem) : IDoctorCheck
    {

        public string Id => id;

        public string Name => id;

        public DoctorSubsystem Subsystem => subsystem;

        public bool RequiresNetwork => false;

        public Task<DoctorFinding> InspectAsync(CancellationToken cancellationToken)
        {

            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(new DoctorFinding(DoctorOutcome.Healthy, "unreachable"));

        }

    }

    private sealed class StubRepair(
        string id,
        IReadOnlyList<string> detectorIds,
        bool failOnApply = false,
        bool throwOnApply = false) : IDoctorRepair
    {

        internal const string SecretBearingMessage = "apiKey=sk-live-secret";

        public string Id => id;

        public DoctorSubsystem Subsystem =>
            Enum.Parse<DoctorSubsystem>(id[..id.IndexOf('.', StringComparison.Ordinal)], ignoreCase: true);

        public IReadOnlyList<string> DetectorIds => detectorIds;

        public string Description => $"stub repair {id}";

        public int PlannedCount { get; private set; }

        public int AppliedCount { get; private set; }

        public Task<DoctorRepairResult> PlanAsync(CancellationToken cancellationToken)
        {

            PlannedCount++;

            return Task.FromResult(
                new DoctorRepairResult(id, DoctorRepairState.Planned, "one change planned", [], null));

        }

        public Task<DoctorRepairResult> ApplyAsync(CancellationToken cancellationToken)
        {

            AppliedCount++;

            if (throwOnApply)
            {

                throw new IOException(SecretBearingMessage);

            }

            return Task.FromResult(
                failOnApply
                    ? new DoctorRepairResult(id, DoctorRepairState.Failed, "could not apply", [], "denied")
                    : new DoctorRepairResult(id, DoctorRepairState.Applied, "applied", [], null));

        }

    }

}
