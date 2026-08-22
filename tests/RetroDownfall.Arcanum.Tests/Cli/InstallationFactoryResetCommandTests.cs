using System.Net;

using System.Text.Json;

using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Cli.Commands;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Cli.UX;

using RetroDownfall.Arcanum.Api.Models;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Collection("GlobalConsole")]

public sealed class InstallationFactoryResetCommandTests
{

    [Fact]

    public void Full_apply_reads_the_attestation_before_startup_and_preserves_the_signed_operation()
    {

        Guid operationId = Guid.Parse("61616161-6161-4161-8161-616161616161");

        InstallationResetPlan plan = CreatePlan(InstallationResetScope.All) with
        {
            Blockers =
            [
                new InstallationResetIssueSummary(
                    ErrorCodes.Data.ExternalRemediationRequired,
                    "External remediation is required."),
            ],
        };

        FullInstallationResetExternalRemediationAttestation attestation =
            CreateAttestation(operationId);

        List<string> events = [];

        RecordingAttestationReader reader = new(attestation, events);

        RecordingStartupProbe startup = new(activeReset: null, events);

        RecordingApplyBoundary boundary = new(CreateSuccessfulService(plan));

        CliTestResult result = RunCommand(
            CreateSuccessfulService(plan),
            [
                "--yes",
                "data",
                "factory-reset",
                "--all",
                "--apply",
                "--force",
                "--external-remediation-attestation",
                "/must-not-be-disclosed/remediation.json",
            ],
            applyBoundary: boundary,
            startupProbe: startup,
            attestationReader: reader);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Equal(1, reader.ReadCount);

        Assert.Equal(1, startup.ReadCount);

        Assert.Equal(["attestation", "startup"], events);

        FullInstallationResetRequest request = Assert.Single(boundary.FullRequests);

        Assert.Equal(operationId, request.OperationId);

        Assert.Equal(operationId, request.ExternalRemediation.OperationId);

        Assert.Equal(plan.PlanId, request.Apply.ExpectedPlanId);

        Assert.DoesNotContain("must-not-be-disclosed", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("must-not-be-disclosed", result.Error, StringComparison.Ordinal);

        Assert.DoesNotContain(attestation.NonceBase64Url, result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain(attestation.SignatureBase64Url, result.Error, StringComparison.Ordinal);

    }

    [Fact]

    public void Full_resume_rejects_a_different_active_operation_before_the_boundary()
    {

        Guid signedOperationId = Guid.Parse("64646464-6464-4464-8464-646464646464");

        InstallationResetPlan plan = CreatePlan(InstallationResetScope.All);

        RecordingApplyBoundary boundary = new(CreateSuccessfulService(plan));

        RecordingAttestationReader reader = new(CreateAttestation(signedOperationId));

        CliTestResult result = RunCommand(
            CreateSuccessfulService(plan),
            [
                "--yes",
                "data",
                "factory-reset",
                "--all",
                "--apply",
                "--force",
                "--external-remediation-attestation",
                "/must-not-be-disclosed/remediation.json",
            ],
            applyBoundary: boundary,
            activeReset: new ActiveInstallationReset(
                InstallationResetScope.All,
                WorkspaceRoot: null,
                plan.PlanId,
                OperationId: Guid.Parse("65656565-6565-4565-8565-656565656565"),
                RequiresExternalRemediationAttestation: true),
            attestationReader: reader);

        Assert.Equal((int)CliExitCode.GenericError, result.ExitCode);

        Assert.Empty(boundary.FullRequests);

        Assert.DoesNotContain("must-not-be-disclosed", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("must-not-be-disclosed", result.Error, StringComparison.Ordinal);

    }

    [Fact]
    public void Authenticated_full_claim_without_an_attestation_never_enters_ordinary_resume()
    {

        InstallationResetPlan plan = CreatePlan(InstallationResetScope.All);

        FakeInstallationResetService service = new(
            Result<InstallationResetPlan>.Failure(new Error(
                "Test.PlanMustNotRun",
                "An authenticated full claim must not replan.")),
            Result<InstallationResetResult>.Success(CreateResult(
                plan,
                InstallationResetPhase.Completed,
                verificationSucceeded: true,
                resumeRequired: false)));

        RecordingApplyBoundary boundary = new(service);

        CliTestResult result = RunCommand(
            service,
            [
                "--yes",
                "data",
                "factory-reset",
                "--all",
                "--apply",
                "--force",
            ],
            applyBoundary: boundary,
            activeReset: new ActiveInstallationReset(
                InstallationResetScope.All,
                WorkspaceRoot: null,
                plan.PlanId,
                OperationId: Guid.Parse(
                    "66666666-6666-4666-8666-666666666666"),
                RequiresExternalRemediationAttestation: true));

        Assert.Equal((int)CliExitCode.GenericError, result.ExitCode);

        Assert.Empty(boundary.Requests);

        Assert.Empty(boundary.FreshCalls);

        Assert.Empty(boundary.FullRequests);

        Assert.Empty(service.PlanRequests);

        Assert.Empty(service.ApplyRequests);

        Assert.DoesNotContain("66666666", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("66666666", result.Error, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Online_validator_accepts_the_authenticated_Covenant_plan_with_a_different_id()
    {

        InstallationResetPlan localPlan = CreatePlan(InstallationResetScope.Global);

        DataRetentionPlan onlinePlan = CreateOnlinePlan() with
        {
            PlanId = "covenant-plan-51",
        };

        InstallationResetOnlinePlanValidator validator = new(
            CreateApiClient(
                _ => CreateDataPlanResponse(onlinePlan),
                apiKey: "test-key"));

        Result<InstallationResetOnlinePlanValidation> result = await validator
            .ValidateAsync(localPlan, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.Equal(onlinePlan.PlanId, result.Value.Plan?.PlanId);

        Assert.Equal(onlinePlan.CandidateIds, result.Value.Plan?.CandidateIds);

    }

    [Theory]

    [InlineData(true, ErrorCodes.Connection.Unreachable)]

    [InlineData(false, ErrorCodes.Security.MissingApiKey)]

    public async Task Online_validator_requires_an_authenticated_global_inventory(
        bool unreachable,
        string expectedCode)
    {

        ArcanumApiClient client = unreachable
            ? CreateApiClient(
                _ => throw new HttpRequestException("Host unavailable."),
                apiKey: "test-key")
            : CreateApiClient(
                _ => throw new InvalidOperationException("HTTP must not run without a key."),
                apiKey: null);

        InstallationResetOnlinePlanValidator validator = new(client);

        Result<InstallationResetOnlinePlanValidation> result = await validator
            .ValidateAsync(
                CreatePlan(InstallationResetScope.Global),
                CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(expectedCode, result.Error.Code);

    }

    [Fact]
    public void Global_dry_run_outputs_only_the_plan_rebound_to_the_authenticated_inventory()
    {

        InstallationResetPlan localPlan = CreatePlan(InstallationResetScope.Global);

        DataRetentionPlan onlinePlan = CreateOnlinePlan() with
        {
            PlanId = "covenant-plan-51",
        };

        InstallationResetPlan reboundPlan = localPlan with
        {
            PlanId = "installation-plan-rebound-51",
            AcceptedBinding = localPlan.AcceptedBinding with
            {
                DataPlanIds = [onlinePlan.PlanId],
            },
        };

        FakeInstallationResetOnlineDataHandoff handoff = new()
        {
            BindResult = Result<InstallationResetPlan>.Success(reboundPlan),
        };

        FakeInstallationResetOnlinePlanValidator validator = new()
        {
            Result = Result<InstallationResetOnlinePlanValidation>.Success(
                new InstallationResetOnlinePlanValidation(onlinePlan)),
        };

        RecordingApplyBoundary boundary = new(CreateSuccessfulService(reboundPlan));

        CliTestResult result = RunCommand(
            CreateSuccessfulService(localPlan),
            ["--json", "data", "factory-reset", "--global", "--dry-run"],
            applyBoundary: boundary,
            onlinePlanValidator: validator,
            onlineDataHandoff: handoff);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        OnlineBindCall bind = Assert.Single(handoff.BindCalls);

        Assert.Equal(InstallationResetScope.Global, bind.Request.Scope);

        Assert.Same(localPlan, bind.LocalPlan);

        Assert.Same(onlinePlan, bind.OnlinePlan);

        Assert.Empty(boundary.FreshCalls);

        using JsonDocument document = JsonDocument.Parse(result.Output);

        Assert.Equal(
            reboundPlan.PlanId,
            document.RootElement.GetProperty("planId").GetString());

        Assert.Equal(
            onlinePlan.PlanId,
            document.RootElement
                .GetProperty("acceptedBinding")
                .GetProperty("dataPlanIds")[0]
                .GetString());

    }

    [Fact]
    public void Global_apply_passes_only_the_rebound_plan_to_the_fresh_boundary()
    {

        InstallationResetPlan localPlan = CreatePlan(InstallationResetScope.Global);

        DataRetentionPlan onlinePlan = CreateOnlinePlan() with
        {
            PlanId = "covenant-plan-52",
        };

        InstallationResetPlan reboundPlan = localPlan with
        {
            PlanId = "installation-plan-rebound-52",
            AcceptedBinding = localPlan.AcceptedBinding with
            {
                DataPlanIds = [onlinePlan.PlanId],
            },
        };

        FakeInstallationResetOnlineDataHandoff handoff = new()
        {
            BindResult = Result<InstallationResetPlan>.Success(reboundPlan),
        };

        FakeInstallationResetOnlinePlanValidator validator = new()
        {
            Result = Result<InstallationResetOnlinePlanValidation>.Success(
                new InstallationResetOnlinePlanValidation(onlinePlan)),
        };

        RecordingApplyBoundary boundary = new(CreateSuccessfulService(reboundPlan));

        CliTestResult result = RunCommand(
            CreateSuccessfulService(localPlan),
            ["--yes", "data", "factory-reset", "--global", "--apply", "--force"],
            applyBoundary: boundary,
            onlinePlanValidator: validator,
            onlineDataHandoff: handoff);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        FreshApplyCall fresh = Assert.Single(boundary.FreshCalls);

        Assert.Equal(InstallationResetScope.Global, fresh.Request.Scope);

        Assert.Same(reboundPlan, fresh.ConfirmedPlan);

        Assert.Empty(boundary.Requests);

    }

    [Fact]
    public void Binding_failure_stops_before_disclosure_confirmation_or_active_publication()
    {

        InstallationResetPlan localPlan = CreatePlan(InstallationResetScope.Global);

        DataRetentionPlan onlinePlan = CreateOnlinePlan() with
        {
            Covenant = new DataRetentionCovenantInventory(
                Rows: 1,
                ManagedFiles: 1,
                LocalArtifacts: 1,
                AffectedSessions: 1,
                PossibleDisclosures: 1,
                DisclosureCountKind: CovenantDisclosureCountKind.Exact),
        };

        FakeInstallationResetOnlineDataHandoff handoff = new()
        {
            BindResult = Result<InstallationResetPlan>.Failure(new Error(
                ErrorCodes.Data.PlanChanged,
                "The authenticated candidates changed.")),
        };

        RecordingConfirmationPrompt prompt = new();

        RecordingApplyBoundary boundary = new(CreateSuccessfulService(localPlan));

        CliTestResult result = RunCommand(
            CreateSuccessfulService(localPlan),
            ["data", "factory-reset", "--global", "--apply"],
            interactive: true,
            input: "RESET\n",
            applyBoundary: boundary,
            onlinePlanValidator: new FakeInstallationResetOnlinePlanValidator
            {
                Result = Result<InstallationResetOnlinePlanValidation>.Success(
                    new InstallationResetOnlinePlanValidation(onlinePlan)),
            },
            onlineDataHandoff: handoff,
            confirmationPrompt: prompt);

        Assert.Equal((int)CliExitCode.GenericError, result.ExitCode);

        Assert.DoesNotContain(
            CovenantExternalRetentionDisclosure.DestructiveOperationText,
            result.Error,
            StringComparison.Ordinal);

        Assert.Equal(0, prompt.CallCount);

        Assert.Empty(boundary.FreshCalls);

        Assert.Empty(boundary.Requests);

    }

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
            Result = Result<InstallationResetOnlinePlanValidation>.Failure(new Error(
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
    public void Automated_global_apply_discloses_the_matching_online_inventory_without_breaking_json()
    {

        InstallationResetPlan plan = CreatePlan(InstallationResetScope.Global);

        DataRetentionPlan onlinePlan = CreateOnlinePlan() with
        {
            Covenant = new DataRetentionCovenantInventory(
                Rows: 4,
                ManagedFiles: 3,
                LocalArtifacts: 2,
                AffectedSessions: 1,
                PossibleDisclosures: 2,
                DisclosureCountKind: CovenantDisclosureCountKind.LowerBound),
        };

        FakeInstallationResetOnlinePlanValidator validator = new()
        {
            Result = Result<InstallationResetOnlinePlanValidation>.Success(
                new InstallationResetOnlinePlanValidation(onlinePlan)),
        };

        FakeInstallationResetService service = CreateSuccessfulService(plan);

        CliTestResult result = RunCommand(
            service,
            ["--json", "--yes", "data", "factory-reset", "--global", "--apply", "--force"],
            onlinePlanValidator: validator);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        int disclosure = result.Error.IndexOf(
            CovenantExternalRetentionDisclosure.DestructiveOperationText,
            StringComparison.Ordinal);

        int count = result.Error.IndexOf("at least 2 physical attempts", StringComparison.Ordinal);

        int guide = result.Error.IndexOf(
            "Retention guidance: README.md#covenant-provider-retention-and-deletion",
            StringComparison.Ordinal);

        Assert.True(disclosure >= 0);

        Assert.True(count > disclosure);

        Assert.True(guide > count);

        Assert.Single(service.PlanRequests);

        using JsonDocument document = JsonDocument.Parse(result.Output);

        Assert.Equal(plan.PlanId, document.RootElement.GetProperty("planId").GetString());

    }

    [Fact]
    public void Automated_all_apply_discloses_the_matching_global_online_inventory()
    {

        InstallationResetPlan plan = CreatePlan(InstallationResetScope.All);

        DataRetentionPlan onlinePlan = CreateOnlinePlan() with
        {
            Covenant = new DataRetentionCovenantInventory(
                Rows: 4,
                ManagedFiles: 3,
                LocalArtifacts: 2,
                AffectedSessions: 1,
                PossibleDisclosures: 2,
                DisclosureCountKind: CovenantDisclosureCountKind.LowerBound),
        };

        FakeInstallationResetOnlinePlanValidator validator = new()
        {
            Result = Result<InstallationResetOnlinePlanValidation>.Success(
                new InstallationResetOnlinePlanValidation(onlinePlan)),
        };

        FakeInstallationResetService service = CreateSuccessfulService(plan);

        CliTestResult result = RunCommand(
            service,
            ["--json", "--yes", "data", "factory-reset", "--all", "--apply", "--force"],
            onlinePlanValidator: validator);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Contains(
            CovenantExternalRetentionDisclosure.DestructiveOperationText,
            result.Error,
            StringComparison.Ordinal);

        Assert.Contains("at least 2 physical attempts", result.Error, StringComparison.Ordinal);

        Assert.Contains(
            "Retention guidance: README.md#covenant-provider-retention-and-deletion",
            result.Error,
            StringComparison.Ordinal);

        Assert.Single(service.ApplyRequests);

        using JsonDocument document = JsonDocument.Parse(result.Output);

        Assert.Equal(plan.PlanId, document.RootElement.GetProperty("planId").GetString());

    }

    [Fact]
    public void Global_apply_decline_follows_disclosure_without_starting_apply()
    {

        InstallationResetPlan plan = CreatePlan(InstallationResetScope.Global);

        DataRetentionPlan onlinePlan = CreateOnlinePlan() with
        {
            Covenant = new DataRetentionCovenantInventory(
                Rows: 4,
                ManagedFiles: 3,
                LocalArtifacts: 2,
                AffectedSessions: 1,
                PossibleDisclosures: 1,
                DisclosureCountKind: CovenantDisclosureCountKind.Exact),
        };

        FakeInstallationResetOnlinePlanValidator validator = new()
        {
            Result = Result<InstallationResetOnlinePlanValidation>.Success(
                new InstallationResetOnlinePlanValidation(onlinePlan)),
        };

        FakeInstallationResetService service = CreateSuccessfulService(plan);

        CliTestResult result = RunCommand(
            service,
            ["data", "factory-reset", "--global", "--apply"],
            interactive: true,
            input: "decline\n",
            onlinePlanValidator: validator);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Empty(service.ApplyRequests);

        int disclosure = result.Error.IndexOf(
            CovenantExternalRetentionDisclosure.DestructiveOperationText,
            StringComparison.Ordinal);

        int count = result.Error.IndexOf("exactly 1 physical attempt", StringComparison.Ordinal);

        int guide = result.Error.IndexOf(
            "Retention guidance: README.md#covenant-provider-retention-and-deletion",
            StringComparison.Ordinal);

        int prompt = result.Error.IndexOf("Type RESET", StringComparison.Ordinal);

        Assert.True(disclosure >= 0);

        Assert.True(count > disclosure);

        Assert.True(guide > count);

        Assert.True(prompt > guide);

    }

    [Fact]
    public void Interactive_all_apply_decline_follows_disclosure_without_starting_apply()
    {

        InstallationResetPlan plan = CreatePlan(InstallationResetScope.All);

        DataRetentionPlan onlinePlan = CreateOnlinePlan() with
        {
            Covenant = new DataRetentionCovenantInventory(
                Rows: 4,
                ManagedFiles: 3,
                LocalArtifacts: 2,
                AffectedSessions: 1,
                PossibleDisclosures: 1,
                DisclosureCountKind: CovenantDisclosureCountKind.Exact),
        };

        FakeInstallationResetOnlinePlanValidator validator = new()
        {
            Result = Result<InstallationResetOnlinePlanValidation>.Success(
                new InstallationResetOnlinePlanValidation(onlinePlan)),
        };

        FakeInstallationResetService service = CreateSuccessfulService(plan);

        CliTestResult result = RunCommand(
            service,
            ["data", "factory-reset", "--all", "--apply"],
            interactive: true,
            input: "decline\n",
            onlinePlanValidator: validator);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Empty(service.ApplyRequests);

        int disclosure = result.Error.IndexOf(
            CovenantExternalRetentionDisclosure.DestructiveOperationText,
            StringComparison.Ordinal);

        int count = result.Error.IndexOf("exactly 1 physical attempt", StringComparison.Ordinal);

        int guide = result.Error.IndexOf(
            "Retention guidance: README.md#covenant-provider-retention-and-deletion",
            StringComparison.Ordinal);

        int prompt = result.Error.IndexOf("Type RESET", StringComparison.Ordinal);

        Assert.True(disclosure >= 0);

        Assert.True(count > disclosure);

        Assert.True(guide > count);

        Assert.True(prompt > guide);

    }

    [Fact]
    public void Global_apply_without_an_authenticated_inventory_stops_before_confirmation_or_apply()
    {

        InstallationResetPlan plan = CreatePlan(InstallationResetScope.Global);

        FakeInstallationResetService service = CreateSuccessfulService(plan);

        RecordingConfirmationPrompt prompt = new();

        RecordingApplyBoundary boundary = new(service);

        CliTestResult result = RunCommand(
            service,
            ["data", "factory-reset", "--global", "--apply"],
            interactive: true,
            input: "RESET\n",
            applyBoundary: boundary,
            onlinePlanValidator: new FakeInstallationResetOnlinePlanValidator
            {
                Result = Result<InstallationResetOnlinePlanValidation>.Success(
                    new InstallationResetOnlinePlanValidation(null)),
            },
            confirmationPrompt: prompt);

        Assert.Equal((int)CliExitCode.GenericError, result.ExitCode);

        Assert.Equal(0, prompt.CallCount);

        Assert.Empty(boundary.FreshCalls);

        Assert.Empty(boundary.Requests);

        Assert.DoesNotContain(
            CovenantExternalRetentionDisclosure.DestructiveOperationText,
            result.Error,
            StringComparison.Ordinal);

    }

    [Fact]
    public void Global_dry_run_without_a_Covenant_aggregate_fails_before_binding_or_output()
    {

        InstallationResetPlan plan = CreatePlan(InstallationResetScope.Global);

        FakeInstallationResetOnlineDataHandoff handoff = new();

        CliTestResult result = RunCommand(
            new FakeInstallationResetService(
                Result<InstallationResetPlan>.Success(plan)),
            ["--json", "data", "factory-reset", "--global", "--dry-run"],
            onlinePlanValidator: new FakeInstallationResetOnlinePlanValidator
            {
                Result = Result<InstallationResetOnlinePlanValidation>.Success(
                    new InstallationResetOnlinePlanValidation(
                        CreateOnlinePlan() with { Covenant = null })),
            },
            onlineDataHandoff: handoff);

        Assert.Equal((int)CliExitCode.GenericError, result.ExitCode);

        Assert.Empty(handoff.BindCalls);

        using JsonDocument error = JsonDocument.Parse(result.Output);

        Assert.Contains(
            ErrorCodes.Data.InventoryUnavailable,
            error.RootElement.GetProperty("error").GetString(),
            StringComparison.Ordinal);

    }

    [Fact]
    public void Workspace_dry_run_keeps_the_offline_plan_without_online_validation_or_binding()
    {

        InstallationResetPlan plan = CreatePlan(InstallationResetScope.Workspace);

        ThrowingOnlinePlanValidator validator = new();

        FakeInstallationResetOnlineDataHandoff handoff = new()
        {
            BindResult = Result<InstallationResetPlan>.Failure(new Error(
                "Test.BindMustNotRun",
                "Workspace reset must not enter the online handoff.")),
        };

        CliTestResult result = RunCommand(
            new FakeInstallationResetService(
                Result<InstallationResetPlan>.Success(plan)),
            ["--json", "data", "factory-reset", "--workspace", "--dry-run"],
            onlinePlanValidator: validator,
            onlineDataHandoff: handoff);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Equal(0, validator.CallCount);

        Assert.Empty(handoff.BindCalls);

        using JsonDocument document = JsonDocument.Parse(result.Output);

        Assert.Equal(plan.PlanId, document.RootElement.GetProperty("planId").GetString());

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

        Assert.Equal(plan, Assert.Single(boundary.FreshCalls).ConfirmedPlan);

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
        IInstallationResetOnlinePlanValidator? onlinePlanValidator = null,
        IInstallationResetOnlineDataHandoff? onlineDataHandoff = null,
        IInstallationResetConfirmationPrompt? confirmationPrompt = null,
        IInstallationStartupProbe? startupProbe = null,
        IFullInstallationResetAttestationFileReader? attestationReader = null)
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

        services.RemoveAll<IInstallationResetOnlineDataHandoff>();

        services.AddSingleton(
            onlineDataHandoff ?? new FakeInstallationResetOnlineDataHandoff());

        if (confirmationPrompt is not null)
        {

            services.RemoveAll<IInstallationResetConfirmationPrompt>();

            services.AddSingleton(confirmationPrompt);

        }

        services.RemoveAll<IInstallationStartupProbe>();

        services.AddSingleton<IInstallationStartupProbe>(
            startupProbe ?? new FakeStartupProbe(activeReset));

        if (attestationReader is not null)
        {

            services.RemoveAll<IFullInstallationResetAttestationFileReader>();

            services.AddSingleton<IFullInstallationResetAttestationFileReader>(
                attestationReader);

        }

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

        public Result<InstallationResetOnlinePlanValidation> Result { get; set; } =
            Result<InstallationResetOnlinePlanValidation>.Success(
                new InstallationResetOnlinePlanValidation(CreateOnlinePlan()));

        public Task<Result<InstallationResetOnlinePlanValidation>> ValidateAsync(
            InstallationResetPlan plan,
            CancellationToken cancellationToken)
        {

            Plans.Add(plan);

            return Task.FromResult(Result);

        }

    }

    private sealed class ThrowingOnlinePlanValidator
        : IInstallationResetOnlinePlanValidator
    {

        public int CallCount { get; private set; }

        public Task<Result<InstallationResetOnlinePlanValidation>> ValidateAsync(
            InstallationResetPlan plan,
            CancellationToken cancellationToken)
        {

            CallCount++;

            throw new InvalidOperationException(
                "Workspace reset must not query the authenticated host.");

        }

    }

    private sealed record OnlineBindCall(
        InstallationResetPlanRequest Request,
        InstallationResetPlan LocalPlan,
        DataRetentionPlan OnlinePlan);

    private sealed class FakeInstallationResetOnlineDataHandoff
        : IInstallationResetOnlineDataHandoff
    {

        public List<OnlineBindCall> BindCalls { get; } = [];

        public Result<InstallationResetPlan>? BindResult { get; set; }

        public Result<InstallationResetPlan> BindOnlineDataPlan(
            InstallationResetPlanRequest request,
            InstallationResetPlan localPlan,
            DataRetentionPlan onlinePlan)
        {

            BindCalls.Add(new OnlineBindCall(request, localPlan, onlinePlan));

            return BindResult
                ?? Result<InstallationResetPlan>.Success(localPlan);

        }

        public Result<InstallationResetHostHandoff> CreateHostHandoff(
            InstallationResetApplyRequest request,
            InstallationResetPlan confirmedPlan) =>
            throw new InvalidOperationException(
                "The command must prepare through the apply boundary.");

        public Task<Result<InstallationResetHostHandoff?>> ReadAsync(
            InstallationResetApplyRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "The command must resume through the apply boundary.");

    }

    private sealed class RecordingConfirmationPrompt : IInstallationResetConfirmationPrompt
    {

        public int CallCount { get; private set; }

        public Task<bool> PromptAsync(
            InstallationResetPlan plan,
            CancellationToken cancellationToken)
        {

            CallCount++;

            return Task.FromResult(true);

        }

    }

    private sealed record FreshApplyCall(
        InstallationResetPlanRequest Request,
        InstallationResetPlan ConfirmedPlan);

    private sealed class RecordingApplyBoundary(
        IInstallationResetService service) : IInstallationResetApplyBoundary
    {

        public List<InstallationResetApplyRequest> Requests { get; } = [];

        public List<FreshApplyCall> FreshCalls { get; } = [];

        public List<FullInstallationResetRequest> FullRequests { get; } = [];

        public Task<Result<InstallationResetResult>> ApplyFullAsync(
            FullInstallationResetRequest request,
            CancellationToken cancellationToken)
        {

            FullRequests.Add(request);

            return service.ApplyAsync(request.Apply, cancellationToken);

        }

        public Task<Result<InstallationResetResult>> ApplyAsync(
            InstallationResetApplyRequest request,
            CancellationToken cancellationToken)
        {

            Requests.Add(request);

            return service.ApplyAsync(request, cancellationToken);

        }

        public Task<Result<InstallationResetResult>> ApplyAsync(
            InstallationResetApplyRequest request,
            InstallationResetHostHandoff? hostHandoff,
            bool onlineCompletionDurable,
            CancellationToken cancellationToken)
        {

            Requests.Add(request);

            return service.ApplyAsync(request, cancellationToken);

        }

        public Task<Result<InstallationResetResult>> ApplyFreshAsync(
            InstallationResetPlanRequest request,
            InstallationResetPlan confirmedPlan,
            CancellationToken cancellationToken)
        {

            FreshCalls.Add(new FreshApplyCall(request, confirmedPlan));

            return service.ApplyAsync(
                new InstallationResetApplyRequest(request, confirmedPlan.PlanId),
                cancellationToken);

        }

    }

    private static string[] Split(string commandLine) =>
        commandLine.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static ArcanumApiClient CreateApiClient(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        string? apiKey) =>
        new(
            new SingleHttpClientFactory(new DelegateHttpMessageHandler(responder)),
            new FakeSecretStore(apiKey));

    private static HttpResponseMessage CreateDataPlanResponse(
        DataRetentionPlan plan)
    {

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            new ApiResponse<DataRetentionPlan>(plan, true, null),
            ArcanumJsonContext.Default.ApiResponseDataRetentionPlan);

        HttpResponseMessage response = new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload),
        };

        response.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        return response;

    }

    private sealed class SingleHttpClientFactory(
        HttpMessageHandler handler) : IHttpClientFactory
    {

        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost:5001/"),
                Timeout = Timeout.InfiniteTimeSpan,
            };

    }

    private sealed class DelegateHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));

    }

    private sealed class FakeSecretStore(string? apiKey) : ISecretStore
    {

        public Task<string?> GetApiKeyAsync() =>
            Task.FromResult(apiKey);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(
                string.IsNullOrWhiteSpace(apiKey)
                    ? SecretStoreReadResult.Missing()
                    : SecretStoreReadResult.Ok(apiKey));

        public Task SaveApiKeyAsync(string value) =>
            Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() =>
            Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) =>
            Task.CompletedTask;

    }

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

    private static DataRetentionPlan CreateOnlinePlan() =>
        new(
            "data-plan-50",
            new DataRetentionRequest(DataRetentionOperation.FactoryReset),
            new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero),
            Items: [],
            Blockers: [],
            Conflicts: [],
            Rows: 12,
            Files: 3,
            EstimatedBytes: 4_096,
            DerivedRecords: 0,
            CandidateIds: [],
            RequiresConfirmation: true,
            Covenant: new DataRetentionCovenantInventory(
                Rows: 0,
                ManagedFiles: 0,
                LocalArtifacts: 0,
                AffectedSessions: 0,
                PossibleDisclosures: 0,
                DisclosureCountKind: CovenantDisclosureCountKind.Exact));

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

        public Task<Result<InstallationResetResult>> ApplyFullAsync(
            FullInstallationResetRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<InstallationResetResult>.Failure(new Error(
                ErrorCodes.Data.ControlPathUnavailable,
                "The test service does not own the full-reset lock boundary.")));

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

    private sealed class RecordingStartupProbe(
        ActiveInstallationReset? activeReset,
        List<string>? events = null) : IInstallationStartupProbe
    {

        public int ReadCount { get; private set; }

        public Task<Result<ActiveInstallationReset?>> ReadActiveResetAsync(
            CancellationToken cancellationToken = default)
        {

            ReadCount++;

            events?.Add("startup");

            return Task.FromResult(
                Result<ActiveInstallationReset?>.Success(activeReset));

        }

        public Result<bool> IsFreshInstallation() =>
            Result<bool>.Success(false);

    }

    private sealed class RecordingAttestationReader(
        FullInstallationResetExternalRemediationAttestation attestation,
        List<string>? events = null)
        : IFullInstallationResetAttestationFileReader
    {

        public int ReadCount { get; private set; }

        public Task<Result<FullInstallationResetExternalRemediationAttestation>> ReadAsync(
            string path,
            CancellationToken cancellationToken)
        {

            ReadCount++;

            events?.Add("attestation");

            return Task.FromResult(
                Result<FullInstallationResetExternalRemediationAttestation>.Success(
                    attestation));

        }

    }

    private static FullInstallationResetExternalRemediationAttestation CreateAttestation(
        Guid operationId) =>
        new(
            Version: 1,
            operationId,
            InstallationId: Guid.Parse("62626262-6262-4262-8262-626262626262"),
            HostToolsTransitionId: Guid.Parse("63636363-6363-4363-8363-636363636363"),
            TaintMasterKeyVersion: ulong.MaxValue,
            AuthorityFingerprint: Digest(0x10),
            DatabaseMarkerDigest: Digest(0x20),
            OsMarkerDigest: Digest(0x30),
            RemediationActionDigest: Digest(0x40),
            NonceBase64Url: "AAECAwQFBgcICQoLDA0ODw",
            Issuer: "RetroDownfall.Remediation.v1",
            IssuedAtUtc: new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero),
            ExpiresAtUtc: new DateTimeOffset(2026, 8, 22, 13, 0, 0, TimeSpan.Zero),
            SignatureBase64Url: new string('A', 86));

    private static CovenantDigest Digest(byte value) =>
        new(Enumerable.Repeat(value, 32).ToArray());

}
