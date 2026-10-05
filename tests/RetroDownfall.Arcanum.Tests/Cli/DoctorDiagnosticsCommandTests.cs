using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Core.Cli;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// End-to-end contract for the expanded <c>arcanum doctor</c> surface (issue #33). The three
/// invariants worth pinning at this level are the ones a unit test cannot reach: the pre-#33
/// <c>--json</c> document still parses and still carries its original check names, an unrecognized
/// id is a closed configuration error rather than a silently smaller diagnostic, and a repair
/// changes nothing without an explicit, confirmed <c>--apply</c>.
/// </summary>
[Collection("GlobalConsole")]
public sealed class DoctorDiagnosticsCommandTests : IDisposable
{
    private readonly string _testHome =
        Path.Combine(Path.GetTempPath(), "arcanum-tests", $"doctor-diag-{Guid.NewGuid():N}");

    private readonly Dictionary<string, string?> _originalEnvironment = new();

    public DoctorDiagnosticsCommandTests()
    {
        Directory.CreateDirectory(_testHome);

        SetEnvironment("ASPNETCORE_ENVIRONMENT", "Testing");

        SetEnvironment("DOTNET_ENVIRONMENT", "Testing");

        SetEnvironment("ARCANUM_TEST_HOME", _testHome);
    }

    [Fact]
    public async Task Every_check_in_the_report_carries_a_stable_id_and_subsystem()
    {
        CliTestResult result = await CliTestHarness.RunAsync(BuildServices(), ["doctor", "--json"]);

        DoctorReport report = Deserialize(result.Output);

        Assert.All(report.Checks, check => Assert.False(string.IsNullOrWhiteSpace(check.Id)));

        Assert.All(report.Checks, check => Assert.NotNull(check.Subsystem));

        Assert.All(report.Checks, check => Assert.NotNull(check.Outcome));

        // The id namespace is the same vocabulary --only and --skip resolve against.
        Assert.All(
            report.Checks,
            check => Assert.StartsWith(
                check.Subsystem!.Value.ToString().ToLowerInvariant() + ".",
                check.Id,
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_report_carries_an_aggregate_outcome_alongside_the_legacy_healthy_flag()
    {
        CliTestResult result = await CliTestHarness.RunAsync(BuildServices(), ["doctor", "--json"]);

        DoctorReport report = Deserialize(result.Output);

        Assert.NotNull(report.Outcome);

        Assert.Equal(
            DoctorOutcomes.Aggregate(report.Checks.Select(check => check.Outcome!.Value)),
            report.Outcome);
    }

    [Fact]
    public async Task Every_check_that_needs_attention_names_a_command_the_operator_can_run()
    {
        CliTestResult result = await CliTestHarness.RunAsync(BuildServices(), ["doctor", "--json"]);

        DoctorReport report = Deserialize(result.Output);

        IReadOnlyList<DoctorCheck> failing =
            [.. report.Checks.Where(check => check.Outcome == DoctorOutcome.Unhealthy)];

        Assert.All(
            failing,
            check => Assert.All(
                check.Remedies ?? [],
                remedy => Assert.StartsWith("arcanum ", remedy.Command, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Only_runs_a_single_subsystem()
    {
        CliTestResult result = await CliTestHarness.RunAsync(
            BuildServices(),
            ["doctor", "--json", "--only", "runtime"]);

        DoctorReport report = Deserialize(result.Output);

        Assert.NotEmpty(report.Checks);

        Assert.All(report.Checks, check => Assert.Equal(DoctorSubsystem.Runtime, check.Subsystem));
    }

    [Fact]
    public async Task Only_a_subsystem_covers_its_legacy_panel_checks_too()
    {
        CliTestResult result = await CliTestHarness.RunAsync(
            BuildServices(),
            ["doctor", "--json", "--only", "runtime"]);

        DoctorReport report = Deserialize(result.Output);

        // runtime.tool_child_sandbox is one of the pre-#33 panel checks. A selector that silently
        // dropped it would report a smaller diagnostic than the operator asked for.
        Assert.Contains(report.Checks, check => check.Id == "runtime.tool_child_sandbox");

        Assert.Contains(report.Checks, check => check.Id == "runtime.pid_file");

        Assert.All(report.Checks, check => Assert.Equal(DoctorSubsystem.Runtime, check.Subsystem));
    }

    [Fact]
    public async Task Skip_removes_only_the_named_subsystem_and_keeps_every_other_legacy_check()
    {
        CliTestResult unfiltered = await CliTestHarness.RunAsync(BuildServices(), ["doctor", "--json"]);

        CliTestResult skipped = await CliTestHarness.RunAsync(
            BuildServices(),
            ["doctor", "--json", "--skip", "mcp"]);

        DoctorReport all = Deserialize(unfiltered.Output);

        DoctorReport withoutMcp = Deserialize(skipped.Output);

        Assert.DoesNotContain(withoutMcp.Checks, check => check.Subsystem == DoctorSubsystem.Mcp);

        Assert.Equal(
            all.Checks.Count(check => check.Subsystem != DoctorSubsystem.Mcp),
            withoutMcp.Checks.Count);

        // The pre-#33 checks are not one indivisible block: skipping MCP must not take them all out.
        Assert.Contains(withoutMcp.Checks, check => check.Id == "system.version");
    }

    [Fact]
    public async Task A_selector_naming_a_legacy_check_id_is_recognized()
    {
        CliTestResult result = await CliTestHarness.RunAsync(
            BuildServices(),
            ["doctor", "--json", "--only", "system.tokenizer"]);

        DoctorReport report = Deserialize(result.Output);

        Assert.Equal("system.tokenizer", Assert.Single(report.Checks).Id);
    }

    [Fact]
    public async Task An_unrecognized_id_exits_with_the_configuration_error_code()
    {
        CliTestResult result = await CliTestHarness.RunAsync(
            BuildServices(),
            ["doctor", "--only", "runtime.not_a_real_check"]);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Contains("arcanum doctor list", result.Error);
    }

    [Fact]
    public async Task Apply_without_a_repair_selector_is_rejected_rather_than_repairing_everything()
    {
        CliTestResult result = await CliTestHarness.RunAsync(BuildServices(), ["doctor", "--apply"]);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Contains("--repair", result.Error);
    }

    [Fact]
    public async Task Network_probes_do_not_run_unless_the_operator_opts_in()
    {
        CliTestResult result = await CliTestHarness.RunAsync(BuildServices(), ["doctor", "--json"]);

        DoctorReport report = Deserialize(result.Output);

        DoctorCheck providers = Assert.Single(
            report.Checks,
            check => check.Id == "providers.reachability");

        Assert.Equal(DoctorOutcome.Skipped, providers.Outcome);
    }

    [Fact]
    public async Task List_emits_one_json_document_naming_every_id()
    {
        CliTestResult result = await CliTestHarness.RunAsync(BuildServices(), ["doctor", "list", "--json"]);

        DoctorCatalog? catalog = JsonSerializer.Deserialize(
            result.Output,
            DoctorReportJsonContext.Default.DoctorCatalog);

        Assert.NotNull(catalog);

        Assert.NotEmpty(catalog.Checks);

        Assert.NotEmpty(catalog.Repairs);

        // Every repair must be discoverable and must name a detector that is itself listed.
        Assert.All(
            catalog.Repairs,
            repair => Assert.All(
                repair.DetectorIds,
                detectorId => Assert.Contains(catalog.Checks, check => check.Id == detectorId)));
    }

    [Fact]
    public async Task Explain_describes_a_repair_and_the_detector_that_justifies_it()
    {
        CliTestResult result = await CliTestHarness.RunAsync(
            BuildServices(),
            ["doctor", "explain", "permissions.apply_owner_only", "--json"]);

        DoctorCatalog? catalog = JsonSerializer.Deserialize(
            result.Output,
            DoctorReportJsonContext.Default.DoctorCatalog);

        Assert.NotNull(catalog);

        DoctorCatalogRepair repair = Assert.Single(catalog.Repairs);

        Assert.Equal("permissions.apply_owner_only", repair.Id);

        Assert.Contains("permissions.posture", repair.DetectorIds);
    }

    [Fact]
    public async Task Explain_rejects_an_unknown_id_with_the_configuration_error_code()
    {
        CliTestResult result = await CliTestHarness.RunAsync(
            BuildServices(),
            ["doctor", "explain", "nope.not_real"]);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Contains("arcanum doctor list", result.Error);
    }

    [Fact]
    public async Task A_repair_without_apply_reports_a_plan_and_changes_nothing()
    {
        CliTestResult result = await CliTestHarness.RunAsync(
            BuildServices(),
            ["doctor", "--json", "--repair", "paths.create_managed_directories"]);

        DoctorReport report = Deserialize(result.Output);

        DoctorRepairResult repair = Assert.Single(report.Repairs ?? []);

        Assert.Equal(DoctorRepairState.Planned, repair.State);

        Assert.NotEmpty(repair.Steps);

        Assert.False(Directory.Exists(ArcanumPaths.AttachmentsDirectory));
    }

    [Fact]
    public async Task A_declined_repair_is_a_success_that_changed_nothing()
    {
        ServiceCollection services = BuildServices();

        services.RemoveAll<IConfirmationPrompt>();

        services.AddSingleton<IConfirmationPrompt>(new StubConfirmationPrompt(false));

        CliTestResult result = await CliTestHarness.RunAsync(
            services,
            ["doctor", "--repair", "paths.create_managed_directories", "--apply"]);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Contains("Nothing was changed", result.Error);
    }

    [Fact]
    public async Task A_confirmed_repair_applies_and_a_second_run_converges()
    {
        ServiceCollection services = BuildServices();

        services.RemoveAll<IConfirmationPrompt>();

        services.AddSingleton<IConfirmationPrompt>(new StubConfirmationPrompt(true));

        CliTestResult first = await CliTestHarness.RunAsync(
            services,
            ["doctor", "--json", "--repair", "paths.create_managed_directories", "--apply"]);

        DoctorRepairResult applied = Assert.Single(Deserialize(first.Output).Repairs ?? []);

        Assert.Equal(DoctorRepairState.Applied, applied.State);

        Assert.True(Directory.Exists(ArcanumPaths.AttachmentsDirectory));

        ServiceCollection again = BuildServices();

        again.RemoveAll<IConfirmationPrompt>();

        again.AddSingleton<IConfirmationPrompt>(new StubConfirmationPrompt(true));

        CliTestResult second = await CliTestHarness.RunAsync(
            again,
            ["doctor", "--json", "--repair", "paths.create_managed_directories", "--apply"]);

        DoctorRepairResult converged = Assert.Single(Deserialize(second.Output).Repairs ?? []);

        Assert.Equal(DoctorRepairState.AlreadyConverged, converged.State);

        Assert.Empty(converged.Steps);
    }

    /// <summary>
    /// R-338: Ctrl+C between two repairs must not discard the first one's result. The operator has to be
    /// told what already changed on disk, so the loop stops, the report still carries the applied set, the
    /// repair that never ran stays a plan, and the exit status is the cancellation code.
    /// </summary>
    [Fact]
    public async Task Cancel_between_repairs_reports_the_repairs_already_applied()
    {
        CancellableInitialization initialization = new();

        ScriptedRepair first = new("paths.applies_then_cancels", initialization.Cancel);

        ScriptedRepair second = new("paths.never_reached", afterApply: null);

        ServiceCollection services = BuildServices(initialization);

        services.AddSingleton<IDoctorRepair>(first);

        services.AddSingleton<IDoctorRepair>(second);

        services.RemoveAll<IConfirmationPrompt>();

        services.AddSingleton<IConfirmationPrompt>(new StubConfirmationPrompt(true));

        CliTestResult result = await CliTestHarness.RunAsync(
            services,
            [
                "doctor",
                "--json",
                "--repair",
                first.Id,
                "--repair",
                second.Id,
                "--apply",
            ]);

        Assert.Equal((int)CliExitCode.Cancelled, result.ExitCode);

        DoctorReport report = Deserialize(result.Output);

        DoctorRepairResult applied = Assert.Single(
            report.Repairs ?? [],
            repair => repair.RepairId == first.Id);

        Assert.Equal(DoctorRepairState.Applied, applied.State);

        DoctorRepairResult untouched = Assert.Single(
            report.Repairs ?? [],
            repair => repair.RepairId == second.Id);

        Assert.Equal(DoctorRepairState.Planned, untouched.State);

        Assert.Equal(1, first.ApplyCount);

        Assert.Equal(0, second.ApplyCount);
    }

    /// <summary>
    /// R-338: a repair interrupted part-way may have changed something, so it is recorded as failed with
    /// the cancellation named rather than dropped, and the repair that never started stays a plan.
    /// </summary>
    [Fact]
    public async Task Cancel_while_a_repair_runs_records_it_as_failed_and_stops_the_loop()
    {
        CancellableInitialization initialization = new();

        ScriptedRepair interrupted = new(
            "paths.interrupted_mid_apply",
            initialization.Cancel,
            throwWhenCancelledInApply: true);

        ScriptedRepair never = new("paths.never_started", afterApply: null);

        CliTestResult result = await RunScriptedRepairsAsync(initialization, interrupted, never);

        Assert.Equal((int)CliExitCode.Cancelled, result.ExitCode);

        DoctorReport report = Deserialize(result.Output);

        DoctorRepairResult failed = Assert.Single(
            report.Repairs ?? [],
            repair => repair.RepairId == interrupted.Id);

        Assert.Equal(DoctorRepairState.Failed, failed.State);

        Assert.Contains("Cancelled while the repair was running", failed.Summary, StringComparison.Ordinal);

        Assert.Equal(nameof(OperationCanceledException), failed.Failure);

        Assert.False(report.Healthy);

        DoctorRepairResult untouched = Assert.Single(
            report.Repairs ?? [],
            repair => repair.RepairId == never.Id);

        Assert.Equal(DoctorRepairState.Planned, untouched.State);

        Assert.Equal(0, never.ApplyCount);
    }

    /// <summary>
    /// R-338: cancelling while a repair is being revalidated under the lease changes nothing, so that
    /// repair simply stays the plan it was and is never applied.
    /// </summary>
    [Fact]
    public async Task Cancel_during_revalidation_leaves_the_repair_a_plan_and_applies_nothing()
    {
        CancellableInitialization initialization = new();

        ScriptedRepair revalidating = new(
            "paths.cancelled_while_revalidating",
            initialization.Cancel,
            cancelDuringPlanWhen: () => initialization.HoldsExclusiveLease);

        CliTestResult result = await RunScriptedRepairsAsync(initialization, revalidating);

        Assert.Equal((int)CliExitCode.Cancelled, result.ExitCode);

        DoctorRepairResult plan = Assert.Single(Deserialize(result.Output).Repairs ?? []);

        Assert.Equal(DoctorRepairState.Planned, plan.State);

        Assert.Equal(0, revalidating.ApplyCount);

        // Planned for the confirmation, for the report, and then cancelled while revalidating.
        Assert.True(revalidating.PlanCount >= 3, $"Planned {revalidating.PlanCount} times.");
    }

    /// <summary>
    /// R-338: when the stop lands after every requested repair finished (while the lease is being
    /// released), the report and the message say so instead of claiming the rest were not run.
    /// </summary>
    [Fact]
    public async Task Cancel_while_the_lease_is_released_says_every_repair_had_finished()
    {
        using CancellationTokenSource caller = new();

        ScriptedRepair only = new("paths.finishes_before_release", afterApply: null);

        ServiceCollection services = BuildServices(new ReleaseCancelsInitialization(caller));

        services.AddSingleton<IDoctorRepair>(only);

        services.RemoveAll<IConfirmationPrompt>();

        services.AddSingleton<IConfirmationPrompt>(new StubConfirmationPrompt(true));

        using ServiceProvider provider = services.BuildServiceProvider();

        TextWriter priorOut = Console.Out;

        TextWriter priorError = Console.Error;

        StringWriter output = new();

        StringWriter error = new();

        Console.SetOut(output);

        Console.SetError(error);

        int exitCode;

        try
        {
            exitCode = await provider.GetRequiredService<RetroDownfall.Arcanum.Cli.Commands.DoctorCommand>().Run(
                new DoctorRunRequest([], [], false, [only.Id], true, false),
                fixPermissions: false,
                json: true,
                caller.Token);
        }
        finally
        {
            Console.SetOut(priorOut);

            Console.SetError(priorError);
        }

        Assert.Equal((int)CliExitCode.Cancelled, exitCode);

        DoctorRepairResult applied = Assert.Single(Deserialize(output.ToString()).Repairs ?? []);

        Assert.Equal(DoctorRepairState.Applied, applied.State);

        Assert.Contains("every requested repair had finished", error.ToString(), StringComparison.Ordinal);

        Assert.DoesNotContain("were not run", error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// R-338: a cancellation that lands before <c>--fix-permissions</c> reached its repair must not erase
    /// that repair from the report; it stays the plan it was.
    /// </summary>
    [Fact]
    public async Task Cancel_before_the_permissions_repair_ran_keeps_it_in_the_report_as_a_plan()
    {
        CancellableInitialization initialization = new();

        ScriptedRepair first = new("paths.cancels_before_permissions", initialization.Cancel);

        ServiceCollection services = BuildServices(initialization);

        services.AddSingleton<IDoctorRepair>(first);

        services.RemoveAll<IConfirmationPrompt>();

        services.AddSingleton<IConfirmationPrompt>(new StubConfirmationPrompt(true));

        CliTestResult result = await CliTestHarness.RunAsync(
            services,
            [
                "doctor",
                "--json",
                "--fix-permissions",
                "--apply",
                "--repair",
                first.Id,
                "--repair",
                "permissions.apply_owner_only",
            ]);

        Assert.Equal((int)CliExitCode.Cancelled, result.ExitCode);

        DoctorReport report = Deserialize(result.Output);

        Assert.Contains(
            report.Repairs ?? [],
            repair => repair.RepairId == first.Id && repair.State == DoctorRepairState.Applied);

        DoctorRepairResult permissions = Assert.Single(
            report.Repairs ?? [],
            repair => repair.RepairId == "permissions.apply_owner_only");

        Assert.NotEqual(DoctorRepairState.Applied, permissions.State);
    }

    private async Task<CliTestResult> RunScriptedRepairsAsync(
        CancellableInitialization initialization,
        params ScriptedRepair[] repairs)
    {
        ServiceCollection services = BuildServices(initialization);

        foreach (ScriptedRepair repair in repairs)
        {
            services.AddSingleton<IDoctorRepair>(repair);
        }

        services.RemoveAll<IConfirmationPrompt>();

        services.AddSingleton<IConfirmationPrompt>(new StubConfirmationPrompt(true));

        return await CliTestHarness.RunAsync(
            services,
            [
                "doctor",
                "--json",
                .. repairs.SelectMany(static repair => (string[])["--repair", repair.Id]),
                "--apply",
            ]);
    }

    [Fact]
    public async Task A_repair_does_not_report_its_own_exclusive_lock_as_external_contention()
    {
        ServiceCollection services = BuildServices();

        services.RemoveAll<IConfirmationPrompt>();

        services.AddSingleton<IConfirmationPrompt>(new StubConfirmationPrompt(true));

        CliTestResult result = await CliTestHarness.RunAsync(
            services,
            [
                "doctor",
                "--json",
                "--only",
                "runtime.maintenance_lock",
                "--repair",
                "paths.create_managed_directories",
                "--apply",
            ]);

        DoctorReport report = Deserialize(result.Output);

        DoctorCheck maintenanceLock = Assert.Single(report.Checks);

        Assert.Equal(DoctorOutcome.Healthy, maintenanceLock.Outcome);

        Assert.DoesNotContain(
            "held",
            maintenanceLock.Detail ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);

        Assert.True(Directory.Exists(ArcanumPaths.AttachmentsDirectory));
    }

    [Theory]
    [InlineData("A running host owns the maintenance lock.")]
    [InlineData("The maintenance lock topology is unsafe.")]
    [InlineData("An installation factory reset is active.")]
    public async Task Refused_exclusive_ownership_blocks_mkdir_pid_and_permission_repairs(
        string refusal)
    {
        RecordingGrimoireCliInitialization directoriesInitialization = new(refusal);

        ServiceCollection directoriesServices = BuildServices(directoriesInitialization);

        directoriesServices.RemoveAll<IConfirmationPrompt>();

        directoriesServices.AddSingleton<IConfirmationPrompt>(new StubConfirmationPrompt(true));

        CliTestResult directoriesResult = await CliTestHarness.RunAsync(
            directoriesServices,
            [
                "doctor",
                "--repair",
                "paths.create_managed_directories",
                "--apply",
            ]);

        Assert.Equal((int)CliExitCode.GenericError, directoriesResult.ExitCode);

        Assert.False(Directory.Exists(ArcanumPaths.AttachmentsDirectory));

        Assert.Equal(1, directoriesInitialization.ExclusiveCalls);

        Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        string pidPath = Path.Combine(ArcanumPaths.GrimoireDirectory, "arcanum.pid");

        await File.WriteAllTextAsync(pidPath, "not-a-pid");

        RecordingGrimoireCliInitialization pidInitialization = new(refusal);

        ServiceCollection pidServices = BuildServices(pidInitialization);

        pidServices.RemoveAll<IConfirmationPrompt>();

        pidServices.AddSingleton<IConfirmationPrompt>(new StubConfirmationPrompt(true));

        CliTestResult pidResult = await CliTestHarness.RunAsync(
            pidServices,
            [
                "doctor",
                "--repair",
                "runtime.remove_stale_pid",
                "--apply",
            ]);

        Assert.Equal((int)CliExitCode.GenericError, pidResult.ExitCode);

        Assert.Equal("not-a-pid", await File.ReadAllTextAsync(pidPath));

        Assert.Equal(1, pidInitialization.ExclusiveCalls);

        await File.WriteAllTextAsync(ArcanumPaths.ConfigurationFile, "{}");

        UnixFileMode? originalMode = null;

        if (!OperatingSystem.IsWindows())
        {
            originalMode = UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.GroupRead
                | UnixFileMode.OtherRead;

            File.SetUnixFileMode(ArcanumPaths.ConfigurationFile, originalMode.Value);
        }

        RecordingGrimoireCliInitialization permissionsInitialization = new(refusal);

        CliTestResult permissionsResult = await CliTestHarness.RunAsync(
            BuildServices(permissionsInitialization),
            ["doctor", "--fix-permissions"]);

        Assert.Equal((int)CliExitCode.GenericError, permissionsResult.ExitCode);

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                originalMode!.Value,
                File.GetUnixFileMode(ArcanumPaths.ConfigurationFile));
        }

        Assert.Equal(1, permissionsInitialization.ExclusiveCalls);
    }

    [Fact]
    public async Task Doctor_plans_and_catalog_operations_remain_outside_exclusive_ownership()
    {
        RecordingGrimoireCliInitialization initialization = new(
            "Read-only doctor operations must not enter the writer boundary.");

        _ = await CliTestHarness.RunAsync(
            BuildServices(initialization),
            ["doctor", "--json", "--repair", "paths.create_managed_directories"]);

        _ = await CliTestHarness.RunAsync(
            BuildServices(initialization),
            ["doctor", "list", "--json"]);

        _ = await CliTestHarness.RunAsync(
            BuildServices(initialization),
            ["doctor", "explain", "paths.create_managed_directories", "--json"]);

        Assert.Equal(0, initialization.ExclusiveCalls);
    }

    [Fact]
    public async Task Fix_permissions_with_json_emits_the_typed_report_rather_than_a_text_payload()
    {
        CliTestResult result = await CliTestHarness.RunAsync(
            BuildServices(),
            ["doctor", "--json", "--fix-permissions"]);

        DoctorReport report = Deserialize(result.Output);

        Assert.NotEmpty(report.Checks);

        DoctorRepairResult repair = Assert.Single(report.Repairs ?? []);

        Assert.Equal("permissions.apply_owner_only", repair.RepairId);

        Assert.NotEqual(DoctorRepairState.Planned, repair.State);
    }

    [Fact]
    public async Task No_finding_or_remedy_ever_carries_a_credential_value()
    {
        const string secretValue = "sk-live-must-never-appear";

        ServiceCollection services = BuildServices();

        services.AddSingleton<ISecretStore>(new SecretBearingStore(secretValue));

        CliTestResult result = await CliTestHarness.RunAsync(services, ["doctor", "--json"]);

        Assert.DoesNotContain(secretValue, result.Output);

        Assert.DoesNotContain(secretValue, result.Error);
    }

    [Fact]
    public async Task A_repair_id_passed_to_only_is_rejected_instead_of_reporting_an_empty_all_clear()
    {
        CliTestResult result = await CliTestHarness.RunAsync(
            BuildServices(),
            ["doctor", "--only", "permissions.apply_owner_only"]);

        // 'arcanum doctor list' prints repair ids and check ids in one document, so picking the
        // wrong column is the expected mistake. Accepting it would run zero checks and report a
        // clean bill of health that a CI gate would then trust.
        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Contains("--repair permissions.apply_owner_only", result.Error);
    }

    [Fact]
    public async Task A_selection_that_matches_nothing_is_rejected_instead_of_reporting_healthy()
    {
        CliTestResult result = await CliTestHarness.RunAsync(
            BuildServices(),
            ["doctor", "--only", "paths", "--skip", "paths"]);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Contains("selects no diagnostics", result.Error);
    }

    [Fact]
    public async Task Fix_permissions_does_not_force_apply_another_repair_or_skip_its_confirmation()
    {
        ServiceCollection services = BuildServices();

        services.RemoveAll<IConfirmationPrompt>();

        RecordingConfirmationPrompt prompt = new(answer: false);

        services.AddSingleton<IConfirmationPrompt>(prompt);

        CliTestResult result = await CliTestHarness.RunAsync(
            services,
            ["doctor", "--json", "--fix-permissions", "--repair", "paths.create_managed_directories"]);

        // Without --apply the co-named repair must stay a plan, and the alias must not become a
        // back door that applies it unconfirmed.
        DoctorReport report = Deserialize(result.Output);

        DoctorRepairResult directories = Assert.Single(
            report.Repairs ?? [],
            repair => repair.RepairId == "paths.create_managed_directories");

        Assert.Equal(DoctorRepairState.Planned, directories.State);

        Assert.False(Directory.Exists(ArcanumPaths.AttachmentsDirectory));

        Assert.Equal(0, prompt.Invocations);
    }

    [Fact]
    public async Task Fix_permissions_keeps_exiting_zero_when_other_checks_are_unhealthy()
    {
        // Pre-#33 this always returned 0. A fresh installation legitimately has failing local checks,
        // so scripts that harden permissions must not start failing because the report grew.
        CliTestResult report = await CliTestHarness.RunAsync(BuildServices(), ["doctor", "--json"]);

        Assert.Equal(
            DoctorOutcome.Unhealthy,
            Deserialize(report.Output).Outcome);

        CliTestResult fix = await CliTestHarness.RunAsync(
            BuildServices(),
            ["doctor", "--json", "--fix-permissions"]);

        Assert.Equal((int)CliExitCode.Success, fix.ExitCode);
    }

    [Fact]
    public async Task An_absent_optional_configuration_file_is_skipped_so_strict_passes_a_default_install()
    {
        CliTestResult result = await CliTestHarness.RunAsync(
            BuildServices(),
            ["doctor", "--json", "--only", "configuration.file", "--only", "mcp.global_config"]);

        DoctorReport report = Deserialize(result.Output);

        Assert.All(report.Checks, check => Assert.Equal(DoctorOutcome.Skipped, check.Outcome));

        Assert.All(report.Checks, check => Assert.Equal("ok", check.Status));

        CliTestResult strict = await CliTestHarness.RunAsync(
            BuildServices(),
            ["doctor", "--only", "configuration.file", "--only", "mcp.global_config", "--strict"]);

        Assert.Equal((int)CliExitCode.Success, strict.ExitCode);
    }

    [Fact]
    public async Task A_filtered_run_does_not_probe_the_host_it_was_told_to_skip()
    {
        ServiceCollection services = BuildServices();

        CountingHttpClientFactory counter = new();

        services.AddSingleton<IHttpClientFactory>(counter);

        _ = await CliTestHarness.RunAsync(services, ["doctor", "--json", "--only", "runtime"]);

        // '--only runtime' excludes both host checks, so the diagnostic must not reach the network
        // at all — filtering results after paying for them is not filtering.
        Assert.Equal(0, counter.Sends);
    }

    /// <summary>
    /// The plan shown before an <c>--apply</c> confirmation is built from the same request, so it
    /// must inherit <c>--only</c>/<c>--skip</c>. Discarding them there re-runs exactly the probes
    /// per-check gating exists to avoid, and does it before the operator has even agreed.
    /// </summary>
    [Fact]
    public async Task The_confirmation_preview_honours_the_filters_the_operator_supplied()
    {
        ServiceCollection services = BuildServices();

        services.RemoveAll<IConfirmationPrompt>();

        services.AddSingleton<IConfirmationPrompt>(new StubConfirmationPrompt(true));

        CountingHttpClientFactory counter = new();

        services.AddSingleton<IHttpClientFactory>(counter);

        _ = await CliTestHarness.RunAsync(
            services,
            [
                "doctor",
                "--json",
                "--only",
                "runtime",
                "--repair",
                "paths.create_managed_directories",
                "--apply",
            ]);

        Assert.Equal(0, counter.Sends);
    }

    private ServiceCollection BuildServices(
        IGrimoireCliInitialization? initialization = null)
    {
        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        if (initialization is not null)
        {
            services.RemoveAll<IGrimoireCliInitialization>();

            services.AddSingleton<IGrimoireCliInitialization>(initialization);
        }

        services.AddSingleton<ISecretStore>(new NullSecretStore());

        // Deterministic: never depend on whether a local 'arcanum serve' happens to be listening.
        services.AddSingleton<IHttpClientFactory>(new ConnectionRefusedHttpClientFactory());

        return services;
    }

    /// <summary>Hands the exclusive callback a token the test can cancel, as Ctrl+C would.</summary>
    private sealed class CancellableInitialization : IGrimoireCliInitialization, IServiceProvider
    {
        private CancellationTokenSource? _source;

        /// <summary>True while an exclusive operation is running, as it is during the revalidation and apply.</summary>
        public bool HoldsExclusiveLease { get; private set; }

        public void Cancel() => _source?.Cancel();

        public async Task<T> RunExclusiveAsync<T>(
            Func<IServiceProvider, CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken)
        {
            using CancellationTokenSource source =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            _source = source;

            HoldsExclusiveLease = true;

            try
            {
                return await operation(this, source.Token).ConfigureAwait(false);
            }
            finally
            {
                HoldsExclusiveLease = false;
            }
        }

        public Task<T> RunExclusiveWithBootstrapAsync<T>(
            Func<IServiceProvider, CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Doctor repairs do not bootstrap the Grimoire.");

        public object? GetService(Type serviceType) => null;
    }

    /// <summary>
    /// Runs the repairs, then reports a cancellation as Ctrl+C would while the lease is being released: after
    /// the operation returned, on the caller's own token.
    /// </summary>
    private sealed class ReleaseCancelsInitialization(CancellationTokenSource caller) : IGrimoireCliInitialization, IServiceProvider
    {
        public async Task<T> RunExclusiveAsync<T>(
            Func<IServiceProvider, CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken)
        {
            T result = await operation(this, cancellationToken).ConfigureAwait(false);

            await caller.CancelAsync().ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            return result;
        }

        public Task<T> RunExclusiveWithBootstrapAsync<T>(
            Func<IServiceProvider, CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Doctor repairs do not bootstrap the Grimoire.");

        public object? GetService(Type serviceType) => null;
    }

    /// <param name="id">The repair id.</param>
    /// <param name="afterApply">Runs inside the apply (and inside a plan call selected by <paramref name="cancelDuringPlanWhen"/>), typically to cancel.</param>
    /// <param name="throwWhenCancelledInApply">
    /// When true the apply throws <see cref="OperationCanceledException"/> after <paramref name="afterApply"/>
    /// if its token was cancelled, as a repair that is interrupted part-way would.
    /// </param>
    /// <param name="cancelDuringPlanWhen">
    /// Decides, per plan call, whether that call cancels and throws. The report is planned more than once
    /// (the confirmation preview, the report, the revalidation under the exclusive lease), so a test selects
    /// the revalidation by asking whether the exclusive lease is held rather than by counting calls.
    /// </param>
    private sealed class ScriptedRepair(
        string id,
        Action? afterApply,
        bool throwWhenCancelledInApply = false,
        Func<bool>? cancelDuringPlanWhen = null) : IDoctorRepair
    {
        public int ApplyCount { get; private set; }

        public int PlanCount { get; private set; }

        public string Id => id;

        public DoctorSubsystem Subsystem => DoctorSubsystem.Paths;

        public IReadOnlyList<string> DetectorIds => ["paths.managed_directories"];

        public string Description => "Scripted repair for cancellation tests.";

        public Task<DoctorRepairResult> PlanAsync(CancellationToken cancellationToken)
        {
            PlanCount++;

            if (cancelDuringPlanWhen?.Invoke() == true)
            {
                afterApply?.Invoke();

                cancellationToken.ThrowIfCancellationRequested();
            }

            return Task.FromResult(
                new DoctorRepairResult(
                    id,
                    DoctorRepairState.Planned,
                    "Would do the scripted work.",
                    [new DoctorRepairStep("scripted", "pending", "done")],
                    null));
        }

        public Task<DoctorRepairResult> ApplyAsync(CancellationToken cancellationToken)
        {
            ApplyCount++;

            if (cancelDuringPlanWhen is null)
            {
                afterApply?.Invoke();
            }

            if (throwWhenCancelledInApply)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return Task.FromResult(
                new DoctorRepairResult(
                    id,
                    DoctorRepairState.Applied,
                    "Did the scripted work.",
                    [new DoctorRepairStep("scripted", "pending", "done")],
                    null));
        }
    }

    private static DoctorReport Deserialize(string output)
    {
        DoctorReport? report = JsonSerializer.Deserialize(
            output,
            DoctorReportJsonContext.Default.DoctorReport);

        Assert.NotNull(report);

        return report;
    }

    private sealed class RecordingConfirmationPrompt(bool answer) : IConfirmationPrompt
    {
        public int Invocations { get; private set; }

        public Task<bool> PromptForConfirmationAsync(
            string question,
            CancellationToken cancellationToken = default)
        {
            Invocations++;

            return Task.FromResult(answer);
        }
    }

    private sealed class CountingHttpClientFactory : IHttpClientFactory
    {
        private readonly CountingHandler _handler = new();

        public int Sends => _handler.Sends;

        public HttpClient CreateClient(string name) =>
            new(_handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost:5001/"),
            };
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private int _sends;

        public int Sends => _sends;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _sends);

            throw new HttpRequestException(
                "Connection refused",
                new SocketException((int)SocketError.ConnectionRefused));
        }
    }

    private sealed class StubConfirmationPrompt(bool answer) : IConfirmationPrompt
    {
        public Task<bool> PromptForConfirmationAsync(
            string question,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(answer);
    }

    private sealed class NullSecretStore : ISecretStore
    {
        public Task<string?> GetApiKeyAsync() =>
            throw new InvalidOperationException("Doctor must use Peek for the master key.");

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            throw new InvalidOperationException("Doctor must use Peek for the master key.");

        public Task<SecretStoreReadResult> PeekApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Missing());

        public Task SaveApiKeyAsync(string apiKey) =>
            throw new InvalidOperationException("Doctor must not persist the master key.");

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => Task.CompletedTask;
    }

    private sealed class SecretBearingStore(string secret) : ISecretStore
    {
        public Task<string?> GetApiKeyAsync() =>
            throw new InvalidOperationException("Doctor must use Peek for the master key.");

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            throw new InvalidOperationException("Doctor must use Peek for the master key.");

        public Task<SecretStoreReadResult> PeekApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Ok(secret));

        public Task SaveApiKeyAsync(string apiKey) =>
            throw new InvalidOperationException("Doctor must not persist the master key.");

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult<string?>(secret);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => Task.CompletedTask;
    }

    private sealed class ConnectionRefusedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new ConnectionRefusedHandler(), disposeHandler: true)
            {
                BaseAddress = new Uri("http://localhost:5001/"),
            };
    }

    private sealed class ConnectionRefusedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException(
                "Connection refused",
                new SocketException((int)SocketError.ConnectionRefused));
    }

    public void Dispose()
    {
        foreach (KeyValuePair<string, string?> entry in _originalEnvironment)
        {
            global::System.Environment.SetEnvironmentVariable(entry.Key, entry.Value);
        }

        if (Directory.Exists(_testHome))
        {
            Directory.Delete(_testHome, recursive: true);
        }
    }

    private void SetEnvironment(string name, string value)
    {
        _originalEnvironment[name] = global::System.Environment.GetEnvironmentVariable(name);

        global::System.Environment.SetEnvironmentVariable(name, value);
    }
}
