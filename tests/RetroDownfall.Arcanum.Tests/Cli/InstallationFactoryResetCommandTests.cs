using System.Text.Json;

using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Cli.Commands;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Cli.UX;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Collection("GlobalConsole")]

public sealed class InstallationFactoryResetCommandTests
{

    [Theory]

    [InlineData("--workspace", InstallationResetScope.Workspace)]

    [InlineData("--global", InstallationResetScope.Global)]

    [InlineData("--all", InstallationResetScope.All)]

    public void Dry_run_plans_once_and_emits_one_plan_document(
        string scopeOption,
        InstallationResetScope expectedScope)
    {

        InstallationResetPlan plan = CreatePlan(expectedScope);

        FakeInstallationResetService service = new(
            Result<InstallationResetPlan>.Success(plan));

        CliTestResult result = RunCommand(
            service,
            ["--json", "data", "factory-reset", scopeOption, "--dry-run"]);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        InstallationResetPlanRequest request = Assert.Single(service.PlanRequests);

        Assert.Equal(expectedScope, request.Scope);

        Assert.Equal(System.Environment.CurrentDirectory, request.InvocationDirectory);

        Assert.Empty(service.ApplyRequests);

        using JsonDocument document = JsonDocument.Parse(result.Output);

        Assert.Equal(plan.PlanId, document.RootElement.GetProperty("planId").GetString());

        Assert.Equal(
            expectedScope.ToString(),
            document.RootElement.GetProperty("scope").GetString());

    }

    [Fact]
    public void Dry_run_validates_the_canonical_data_plan_through_the_authenticated_host()
    {

        InstallationResetPlan plan = CreatePlan(InstallationResetScope.Global);

        FakeInstallationResetOnlinePlanValidator validator = new();

        CliTestResult result = RunCommand(
            new FakeInstallationResetService(
                Result<InstallationResetPlan>.Success(plan)),
            ["data", "factory-reset", "--global", "--dry-run"],
            onlinePlanValidator: validator);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Equal(plan, Assert.Single(validator.Plans));

    }

    [Fact]
    public void Authenticated_host_plan_mismatch_stops_before_confirmation_or_apply()
    {

        InstallationResetPlan plan = CreatePlan(InstallationResetScope.Global);

        FakeInstallationResetService service = CreateSuccessfulService(plan);

        FakeInstallationResetOnlinePlanValidator validator = new()
        {
            Result = Result.Failure(new Error(
                ErrorCodes.Data.PlanChanged,
                "The authenticated host reported a different canonical data plan.")),
        };

        CliTestResult result = RunCommand(
            service,
            ["--yes", "data", "factory-reset", "--global", "--apply", "--force"],
            onlinePlanValidator: validator);

        Assert.Equal((int)CliExitCode.GenericError, result.ExitCode);

        Assert.Empty(service.ApplyRequests);

    }

    [Fact]
    public void Human_dry_run_lists_the_exact_targets_backups_credentials_and_exclusions()
    {

        InstallationResetPlan plan = CreatePlan(InstallationResetScope.Global) with
        {
            Targets =
            [
                new InstallationResetTargetDescriptor(
                    "configuration",
                    InstallationResetTargetRole.FileSystem,
                    "config",
                    "/state/arcanum.json",
                    DatabasePredicate: null,
                    Identity: null,
                    Rows: null,
                    Files: 1,
                    EstimatedBytes: 19),
            ],
            Credentials =
            [
                new InstallationResetCredentialSummary(
                    "master-api-key",
                    InstallationResetItemStatus.Pending),
            ],
            PreservedBackups =
            [
                new InstallationResetPreservedBackup(
                    "/state/backups/safe.arcbackup",
                    new InstallationResetFileIdentity("backup-identity", 68, 1)),
            ],
            Exclusions =
            [
                new InstallationResetExclusion(
                    "external-workspace",
                    "/work/source",
                    "Source files are never deleted."),
            ],
        };

        CliTestResult result = RunCommand(
            new FakeInstallationResetService(
                Result<InstallationResetPlan>.Success(plan)),
            ["data", "factory-reset", "--global", "--dry-run"]);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Contains("/state/arcanum.json", result.Output, StringComparison.Ordinal);

        Assert.Contains("master-api-key", result.Output, StringComparison.Ordinal);

        Assert.Contains("/state/backups/safe.arcbackup", result.Output, StringComparison.Ordinal);

        Assert.Contains("/work/source", result.Output, StringComparison.Ordinal);

        Assert.Contains("Source files are never deleted.", result.Output, StringComparison.Ordinal);

    }

    [Fact]

    public void Headless_apply_with_both_acknowledgements_plans_once_and_binds_the_plan_id()
    {

        InstallationResetPlan plan = CreatePlan(InstallationResetScope.All);

        InstallationResetResult applied = CreateResult(
            plan,
            InstallationResetPhase.Completed,
            verificationSucceeded: true,
            resumeRequired: false);

        FakeInstallationResetService service = new(
            Result<InstallationResetPlan>.Success(plan),
            Result<InstallationResetResult>.Success(applied));

        CliTestResult result = RunCommand(
            service,
            [
                "--json",
                "--yes",
                "data",
                "factory-reset",
                "--all",
                "--apply",
                "--force",
            ]);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Single(service.PlanRequests);

        InstallationResetApplyRequest request = Assert.Single(service.ApplyRequests);

        Assert.Equal(plan.PlanId, request.ExpectedPlanId);

        Assert.Equal(service.PlanRequests[0], request.Request);

        using JsonDocument document = JsonDocument.Parse(result.Output);

        Assert.Equal(plan.PlanId, document.RootElement.GetProperty("planId").GetString());

        Assert.Equal(
            InstallationResetPhase.Completed.ToString(),
            document.RootElement.GetProperty("phase").GetString());

    }

    [Fact]
    public void Apply_runs_through_the_offline_shutdown_and_lock_boundary()
    {

        InstallationResetPlan plan = CreatePlan(InstallationResetScope.Global);

        FakeInstallationResetService service = CreateSuccessfulService(plan);

        RecordingApplyBoundary boundary = new(service);

        CliTestResult result = RunCommand(
            service,
            [
                "--yes",
                "data",
                "factory-reset",
                "--global",
                "--apply",
                "--force",
            ],
            applyBoundary: boundary);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Equal(plan.PlanId, Assert.Single(boundary.Requests).ExpectedPlanId);

    }

    [Fact]

    public void Interactive_apply_accepts_only_the_exact_reset_token()
    {

        InstallationResetPlan plan = CreatePlan(InstallationResetScope.Global);

        FakeInstallationResetService service = CreateSuccessfulService(plan);

        CliTestResult result = RunCommand(
            service,
            ["data", "factory-reset", "--global", "--apply"],
            interactive: true,
            input: "RESET\n");

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Single(service.PlanRequests);

        Assert.Single(service.ApplyRequests);

        Assert.Contains(plan.PlanId, result.Output, StringComparison.Ordinal);

        Assert.Contains("RESET", result.Error, StringComparison.Ordinal);

    }

    [Theory]

    [InlineData("")]

    [InlineData("\n")]

    [InlineData("reset\n")]

    [InlineData(" RESET\n")]

    [InlineData("RESET \n")]

    public void Interactive_apply_rejects_every_non_exact_reset_token(string input)
    {

        InstallationResetPlan plan = CreatePlan(InstallationResetScope.Global);

        FakeInstallationResetService service = CreateSuccessfulService(plan);

        CliTestResult result = RunCommand(
            service,
            ["data", "factory-reset", "--global", "--apply"],
            interactive: true,
            input: input);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Single(service.PlanRequests);

        Assert.Empty(service.ApplyRequests);

    }

    [Theory]

    [InlineData("--yes data factory-reset --all --apply")]

    [InlineData("data factory-reset --all --apply --force")]

    [InlineData("--yes data factory-reset --all --dry-run --force")]

    public void Invalid_acknowledgement_shapes_fail_before_planning(string commandLine)
    {

        FakeInstallationResetService service = CreateSuccessfulService(
            CreatePlan(InstallationResetScope.All));

        CliTestResult result = RunCommand(
            service,
            Split(commandLine));

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Empty(service.PlanRequests);

        Assert.Empty(service.ApplyRequests);

    }

    [Fact]

    public void Headless_apply_without_acknowledgements_never_applies()
    {

        FakeInstallationResetService service = CreateSuccessfulService(
            CreatePlan(InstallationResetScope.Workspace));

        CliTestResult result = RunCommand(
            service,
            ["data", "factory-reset", "--workspace", "--apply"]);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Single(service.PlanRequests);

        Assert.Empty(service.ApplyRequests);

        Assert.Contains("--yes", result.Error, StringComparison.Ordinal);

        Assert.Contains("--force", result.Error, StringComparison.Ordinal);

    }

    [Fact]

    public void Resumable_apply_result_is_emitted_and_returns_generic_error()
    {

        InstallationResetPlan plan = CreatePlan(InstallationResetScope.Global);

        InstallationResetResult partial = CreateResult(
            plan,
            InstallationResetPhase.DataResetComplete,
            verificationSucceeded: false,
            resumeRequired: true,
            errorCode: ErrorCodes.Data.RecoveryRequired);

        FakeInstallationResetService service = new(
            Result<InstallationResetPlan>.Success(plan),
            Result<InstallationResetResult>.Success(partial));

        CliTestResult result = RunCommand(
            service,
            [
                "--json",
                "--yes",
                "data",
                "factory-reset",
                "--global",
                "--apply",
                "--force",
            ]);

        Assert.Equal((int)CliExitCode.GenericError, result.ExitCode);

        using JsonDocument document = JsonDocument.Parse(result.Output);

        Assert.True(document.RootElement.GetProperty("resumeRequired").GetBoolean());

        Assert.Equal(
            ErrorCodes.Data.RecoveryRequired,
            document.RootElement.GetProperty("errorCode").GetString());

    }

    [Fact]
    public void Active_reset_resumes_with_the_durable_plan_id_without_replanning_or_prompting()
    {

        InstallationResetPlan plan = CreatePlan(InstallationResetScope.Global);

        InstallationResetResult completed = CreateResult(
            plan,
            InstallationResetPhase.Completed,
            verificationSucceeded: true,
            resumeRequired: false);

        FakeInstallationResetService service = new(
            Result<InstallationResetPlan>.Failure(
                new Error("Test.PlanMustNotRun", "Resume must not replan.")),
            Result<InstallationResetResult>.Success(completed));

        CliTestResult result = RunCommand(
            service,
            ["data", "factory-reset", "--global", "--apply"],
            interactive: true,
            input: "wrong confirmation must remain unread\n",
            activeReset: new ActiveInstallationReset(
                InstallationResetScope.Global,
                WorkspaceRoot: null,
                plan.PlanId));

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Empty(service.PlanRequests);

        InstallationResetApplyRequest request = Assert.Single(service.ApplyRequests);

        Assert.Equal(plan.PlanId, request.ExpectedPlanId);

        Assert.DoesNotContain("Type RESET", result.Error, StringComparison.Ordinal);

    }

    [Fact]

    public void Connection_plan_failure_emits_one_cli_error_document_and_returns_network_error()
    {

        FakeInstallationResetService service = new(
            Result<InstallationResetPlan>.Failure(
                new Error(
                    ErrorCodes.Connection.Unreachable,
                    "The local host could not be reached.")));

        CliTestResult result = RunCommand(
            service,
            ["--json", "data", "factory-reset", "--global", "--dry-run"]);

        Assert.Equal((int)CliExitCode.NetworkError, result.ExitCode);

        Assert.Single(service.PlanRequests);

        Assert.Empty(service.ApplyRequests);

        using JsonDocument document = JsonDocument.Parse(result.Output);

        Assert.Equal(
            (int)CliExitCode.NetworkError,
            document.RootElement.GetProperty("exitCode").GetInt32());

        Assert.Contains(
            ErrorCodes.Connection.Unreachable,
            document.RootElement.GetProperty("error").GetString(),
            StringComparison.Ordinal);

    }

    [Fact]

    public void Cancellation_uses_the_standard_cancelled_exit_and_one_error_document()
    {

        FakeInstallationResetService service = new(
            Result<InstallationResetPlan>.Success(
                CreatePlan(InstallationResetScope.Global)),
            planException: new OperationCanceledException());

        CliTestResult result = RunCommand(
            service,
            ["--json", "data", "factory-reset", "--global", "--dry-run"]);

        Assert.Equal((int)CliExitCode.Cancelled, result.ExitCode);

        using JsonDocument document = JsonDocument.Parse(result.Output);

        Assert.Equal(
            (int)CliExitCode.Cancelled,
            document.RootElement.GetProperty("exitCode").GetInt32());

    }

    [Fact]
    public void Resumable_cancellation_result_uses_exit_130_and_the_typed_result_payload()
    {

        InstallationResetPlan plan = CreatePlan(InstallationResetScope.Global);

        InstallationResetResult cancelled = CreateResult(
            plan,
            InstallationResetPhase.Prepared,
            verificationSucceeded: false,
            resumeRequired: true,
            errorCode: ErrorCodes.Data.RecoveryRequired) with
        {
            Verification = new InstallationResetVerification(
                false,
                [
                    new InstallationResetIssueSummary(
                        ErrorCodes.Data.RecoveryRequired,
                        "Installation reset was cancelled after its active record was published."),
                ]),
        };

        CliTestResult result = RunCommand(
            new FakeInstallationResetService(
                Result<InstallationResetPlan>.Success(plan),
                Result<InstallationResetResult>.Success(cancelled)),
            ["--json", "--yes", "data", "factory-reset", "--global", "--apply", "--force"]);

        Assert.Equal((int)CliExitCode.Cancelled, result.ExitCode);

        using JsonDocument output = JsonDocument.Parse(result.Output);

        Assert.Equal(
            cancelled.OperationId,
            output.RootElement.GetProperty("operationId").GetGuid());

        Assert.Equal(
            ErrorCodes.Data.RecoveryRequired,
            output.RootElement.GetProperty("errorCode").GetString());

    }

    [Theory]

    [InlineData(typeof(InstallationResetPlan))]

    [InlineData(typeof(InstallationResetResult))]

    public void Installation_reset_output_types_are_registered_for_cli_source_generation(
        Type type)
    {

        Assert.NotNull(CliJsonContext.Default.GetTypeInfo(type));

    }

    private static CliTestResult RunCommand(
        IInstallationResetService service,
        string[] args,
        bool interactive = false,
        string? input = null,
        IInstallationResetApplyBoundary? applyBoundary = null,
        ActiveInstallationReset? activeReset = null,
        IInstallationResetOnlinePlanValidator? onlinePlanValidator = null)
    {

        ServiceCollection services = new();

        CliApplicationFactory.ConfigureCliServices(
            services,
            new ConfigurationManager());

        services.RemoveAll<IInstallationResetService>();

        services.AddSingleton(service);

        services.RemoveAll<IInstallationResetApplyBoundary>();

        services.AddSingleton(
            applyBoundary ?? new RecordingApplyBoundary(service));

        services.RemoveAll<IInstallationResetOnlinePlanValidator>();

        services.AddSingleton(
            onlinePlanValidator ?? new FakeInstallationResetOnlinePlanValidator());

        services.RemoveAll<IInstallationStartupProbe>();

        services.AddSingleton<IInstallationStartupProbe>(
            new FakeStartupProbe(activeReset));

        services.RemoveAll<ICliEnvironment>();

        services.AddSingleton<ICliEnvironment>(
            new FakeCliEnvironment(interactive));

        services.RemoveAll<CliStandardInput>();

        services.AddSingleton(
            new CliStandardInput(new StringReader(input ?? string.Empty)));

        return CliTestHarness
            .RunAsync(services, args, input)
            .GetAwaiter()
            .GetResult();

    }

    private sealed class FakeInstallationResetOnlinePlanValidator
        : IInstallationResetOnlinePlanValidator
    {

        public List<InstallationResetPlan> Plans { get; } = [];

        public Result Result { get; set; } = Result.Success();

        public Task<Result> ValidateAsync(
            InstallationResetPlan plan,
            CancellationToken cancellationToken)
        {

            Plans.Add(plan);

            return Task.FromResult(Result);

        }

    }

    private sealed class RecordingApplyBoundary(
        IInstallationResetService service) : IInstallationResetApplyBoundary
    {

        public List<InstallationResetApplyRequest> Requests { get; } = [];

        public Task<Result<InstallationResetResult>> ApplyAsync(
            InstallationResetApplyRequest request,
            CancellationToken cancellationToken)
        {

            Requests.Add(request);

            return service.ApplyAsync(request, cancellationToken);

        }

    }

    private static string[] Split(string commandLine) =>
        commandLine.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static FakeInstallationResetService CreateSuccessfulService(
        InstallationResetPlan plan) =>
        new(
            Result<InstallationResetPlan>.Success(plan),
            Result<InstallationResetResult>.Success(
                CreateResult(
                    plan,
                    InstallationResetPhase.Completed,
                    verificationSucceeded: true,
                    resumeRequired: false)));

    private static InstallationResetPlan CreatePlan(InstallationResetScope scope) =>
        new(
            "installation-plan-50",
            scope,
            Workspace: null,
            new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero),
            DataInventoryAvailable: true,
            CredentialInventoryAvailable: true,
            Targets: [],
            Credentials: [],
            PreservedBackups: [],
            Exclusions: [],
            Blockers: [],
            Rows: 12,
            Files: 3,
            EstimatedBytes: 4_096,
            new InstallationResetAcceptedBinding(
                "binding-50",
                SelectedRoots: [],
                ExcludedRoots: [],
                PreservedBackups: [],
                CredentialAccounts: [],
                DataPlanIds: ["data-plan-50"]));

    private static InstallationResetResult CreateResult(
        InstallationResetPlan plan,
        InstallationResetPhase phase,
        bool verificationSucceeded,
        bool resumeRequired,
        string? errorCode = null) =>
        new(
            Guid.Parse("50505050-5050-5050-5050-505050505050"),
            plan.PlanId,
            plan.Scope,
            phase,
            PointOfNoReturn: true,
            RowsDeleted: 12,
            FilesDeleted: 3,
            EstimatedBytesDeleted: 4_096,
            CredentialResults: [],
            PreservedBackups: [],
            new InstallationResetVerification(
                verificationSucceeded,
                RemainingIssues: []),
            resumeRequired,
            errorCode);

    private sealed class FakeInstallationResetService(
        Result<InstallationResetPlan> planResult,
        Result<InstallationResetResult>? applyResult = null,
        Exception? planException = null) : IInstallationResetService
    {

        public List<InstallationResetPlanRequest> PlanRequests { get; } = [];

        public List<InstallationResetApplyRequest> ApplyRequests { get; } = [];

        public Task<Result<InstallationResetPlan>> PlanAsync(
            InstallationResetPlanRequest request,
            CancellationToken cancellationToken = default)
        {

            PlanRequests.Add(request);

            if (planException is not null)
            {

                return Task.FromException<Result<InstallationResetPlan>>(
                    planException);

            }

            return Task.FromResult(planResult);

        }

        public Task<Result<InstallationResetResult>> ApplyAsync(
            InstallationResetApplyRequest request,
            CancellationToken cancellationToken = default)
        {

            ApplyRequests.Add(request);

            return Task.FromResult(
                applyResult
                ?? Result<InstallationResetResult>.Failure(
                    new Error("Test.ApplyMissing", "No apply result was configured.")));

        }

    }

    private sealed class FakeCliEnvironment(bool interactive) : ICliEnvironment
    {

        public bool IsInteractive => interactive;

        public bool ColorEnabled => false;

        public bool ShouldShowManaBar => false;

    }

    private sealed class FakeStartupProbe(
        ActiveInstallationReset? activeReset) : IInstallationStartupProbe
    {

        public Task<Result<ActiveInstallationReset?>> ReadActiveResetAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<ActiveInstallationReset?>.Success(activeReset));

        public Result<bool> IsFreshInstallation() =>
            Result<bool>.Success(false);

    }

}
