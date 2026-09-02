using System.Globalization;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Cli.UX;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Hosting;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

namespace RetroDownfall.Arcanum.Cli.Commands;

internal interface IInstallationResetConfirmationPrompt
{

    Task<bool> PromptAsync(
        InstallationResetPlan plan,
        CancellationToken cancellationToken);

}

internal interface IInstallationResetApplyBoundary
{

    Task<Result<InstallationResetResult>> ApplyFullAsync(
        FullInstallationResetRequest request,
        CancellationToken cancellationToken);

    Task<Result<InstallationResetResult>> ApplyAsync(
        InstallationResetApplyRequest request,
        CancellationToken cancellationToken);

    Task<Result<InstallationResetResult>> ApplyAsync(
        InstallationResetApplyRequest request,
        InstallationResetHostHandoff? hostHandoff,
        bool onlineCompletionDurable,
        CancellationToken cancellationToken);

    Task<Result<InstallationResetResult>> ApplyFreshAsync(
        InstallationResetPlanRequest request,
        StoppedHostInstallationResetPlan confirmedPlan,
        CancellationToken cancellationToken);

}

internal interface IInstallationResetOnlinePlanValidator
{

    Task<Result<InstallationResetOnlinePlanValidation>> ValidateAsync(
        InstallationResetPlan plan,
        CancellationToken cancellationToken);

}

internal sealed record InstallationResetOnlinePlanValidation(
    DataRetentionPlan? Plan);

internal sealed class InstallationResetOnlinePlanValidator(
    ArcanumApiClient apiClient) : IInstallationResetOnlinePlanValidator
{

    public async Task<Result<InstallationResetOnlinePlanValidation>> ValidateAsync(
        InstallationResetPlan plan,
        CancellationToken cancellationToken)
    {

        InstallationResetDataPlanRequest request = plan.Scope switch
        {
            InstallationResetScope.Workspace => new InstallationResetDataPlanRequest(
                InstallationResetDataScope.Workspace,
                plan.Workspace),
            _ => new InstallationResetDataPlanRequest(InstallationResetDataScope.Global),
        };

        Result<DataRetentionPlan> online = await apiClient
            .PlanFactoryResetDataAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (online.IsFailure)
        {

            return Result<InstallationResetOnlinePlanValidation>.Failure(online.Error);

        }

        return Result<InstallationResetOnlinePlanValidation>.Success(
            new InstallationResetOnlinePlanValidation(online.Value));

    }

}

internal sealed class InstallationResetConfirmationPrompt(
    IConsoleDispatcher dispatcher,
    CliStandardInput standardInput) : IInstallationResetConfirmationPrompt
{

    public async Task<bool> PromptAsync(
        InstallationResetPlan plan,
        CancellationToken cancellationToken)
    {

        dispatcher.WriteDiagnostic(
            $"Type RESET to apply installation reset plan {plan.PlanId}:");

        string? response = await standardInput
            .ConfiguredReader
            .ReadLineAsync(cancellationToken)
            .ConfigureAwait(false);

        return string.Equals(
            response,
            "RESET",
            StringComparison.Ordinal);

    }

}

internal sealed class InstallationFactoryResetCommand(
    IInstallationResetService resetService,
    IInstallationStartupProbe startupProbe,
    IConsoleDispatcher dispatcher,
    ICliInvocationContext invocationContext,
    ICliEnvironment environment,
    IInstallationResetConfirmationPrompt confirmationPrompt,
    IInstallationResetApplyBoundary applyBoundary,
    IInstallationResetOnlinePlanValidator onlinePlanValidator,
    IInstallationResetOnlineDataHandoff onlineDataHandoff,
    IFullInstallationResetAttestationFileReader attestationReader,
    CovenantExternalRetentionDisclosureWriter disclosureWriter,
    IGrimoireCliStoppedHostInitialization stoppedHostInitialization)
{

    public async Task<int> Execute(
        bool workspace,
        bool global,
        bool all,
        bool dryRun,
        bool apply,
        bool force,
        string? externalRemediationAttestationPath,
        CancellationToken cancellationToken)
    {

        string? validationError = ValidateShape(
            workspace,
            global,
            all,
            dryRun,
            apply,
            force,
            invocationContext.Options.Yes);

        if (validationError is not null)
        {

            return Fail(
                validationError,
                CliExitCode.ConfigurationError);

        }

        bool externalRemediationRequested =
            externalRemediationAttestationPath is not null;

        if (externalRemediationRequested && (!all || !apply))
        {

            return Fail(
                "External remediation authorization is valid only with --all and --apply.",
                CliExitCode.ConfigurationError);

        }

        FullInstallationResetExternalRemediationAttestation? externalRemediation = null;

        if (externalRemediationRequested)
        {

            Result<FullInstallationResetExternalRemediationAttestation> read =
                await attestationReader
                    .ReadAsync(
                        externalRemediationAttestationPath!,
                        cancellationToken)
                    .ConfigureAwait(false);

            if (read.IsFailure)
            {

                return Fail(read.Error);

            }

            externalRemediation = read.Value;

        }

        InstallationResetPlanRequest request = new(
            ResolveScope(workspace, global),
            System.Environment.CurrentDirectory);

        Result<ActiveInstallationReset?> activeRead = await startupProbe
            .ReadActiveResetAsync(cancellationToken)
            .ConfigureAwait(false);

        if (activeRead.IsFailure)
        {

            return Fail(activeRead.Error);

        }

        if (apply && activeRead.Value is { } active)
        {

            if (active.Scope != request.Scope)
            {

                return Fail(new Error(
                    ErrorCodes.Data.ResetInProgress,
                    "A different installation reset owns the active operation."));

            }

            if (active.RequiresExternalRemediationAttestation
                && externalRemediation is null)
            {

                return Fail(new Error(
                    ErrorCodes.Data.ExternalRemediationRequired,
                    "External remediation is required for this installation reset."));

            }

            if (!active.RequiresExternalRemediationAttestation
                && externalRemediation is not null)
            {

                return Fail(new Error(
                    ErrorCodes.Data.ExternalRemediationInvalid,
                    "The external remediation attestation could not be verified."));

            }

            if (externalRemediation is not null
                && active.OperationId != externalRemediation.OperationId)
            {

                return Fail(new Error(
                    ErrorCodes.Data.ExternalRemediationInvalid,
                    "The external remediation attestation could not be verified."));

            }

            InstallationResetApplyRequest applyRequest = new(
                request,
                active.PlanId);

            Result<InstallationResetResult> resumed = externalRemediation is null
                ? await applyBoundary
                    .ApplyAsync(
                        applyRequest,
                        active.HostHandoff,
                        active.OnlineDataCompletionDurable,
                        cancellationToken)
                    .ConfigureAwait(false)
                : await applyBoundary
                    .ApplyFullAsync(
                        new FullInstallationResetRequest(
                            externalRemediation.OperationId,
                            applyRequest,
                            externalRemediation),
                        cancellationToken)
                    .ConfigureAwait(false);

            if (resumed.IsFailure)
            {

                return Fail(resumed.Error);

            }

            WriteResult(resumed.Value);

            if (IsCancellationResult(resumed.Value))
            {

                return (int)CliExitCode.Cancelled;

            }

            return IsCompleted(resumed.Value)
                ? (int)CliExitCode.Success
                : (int)CliExitCode.GenericError;

        }

        StoppedHostInstallationResetPlan? stoppedHostPlan = null;

        Result<InstallationResetPlan> planned;

        if (externalRemediation is null)
        {

            Result<StoppedHostInstallationResetPlan> local =
                await stoppedHostInitialization.RunAsync(
                    (provider, issuer, token) => provider
                        .GetRequiredService<IInstallationResetStoppedHostPlanner>()
                        .PlanUnderStoppedHostLockAsync(
                            request,
                            issuer,
                            token),
                    cancellationToken).ConfigureAwait(false);

            if (local.IsFailure)
            {

                return Fail(local.Error);

            }

            stoppedHostPlan = local.Value;

            planned = Result<InstallationResetPlan>.Success(
                stoppedHostPlan.Plan);

        }
        else
        {

            planned = await resetService
                .PlanAsync(request, cancellationToken)
                .ConfigureAwait(false);

        }

        if (planned.IsFailure)
        {

            return Fail(planned.Error);

        }

        InstallationResetPlan plan = planned.Value;

        DataRetentionPlan? onlinePlan = null;

        if (externalRemediation is not null
            && plan.Scope is InstallationResetScope.Global or InstallationResetScope.All)
        {

            Result<InstallationResetOnlinePlanValidation> onlineValidation =
                await onlinePlanValidator
                    .ValidateAsync(plan, cancellationToken)
                    .ConfigureAwait(false);

            if (onlineValidation.IsFailure)
            {

                return Fail(onlineValidation.Error);

            }

            onlinePlan = onlineValidation.Value.Plan;

            if (onlinePlan?.Covenant is null)
            {

                return Fail(new Error(
                    ErrorCodes.Data.InventoryUnavailable,
                    "The authenticated host Covenant inventory is unavailable."));

            }

            Result<InstallationResetPlan> rebound = onlineDataHandoff
                .BindOnlineDataPlan(request, plan, onlinePlan);

            if (rebound.IsFailure)
            {

                return Fail(rebound.Error);

            }

            plan = rebound.Value;

        }

        if (dryRun)
        {

            WritePlan(plan);

            return (int)CliExitCode.Success;

        }

        InstallationResetIssueSummary? blocker = plan.Blockers.FirstOrDefault(
            issue => externalRemediation is null
                || !string.Equals(
                    issue.Code,
                    ErrorCodes.Data.ExternalRemediationRequired,
                    StringComparison.Ordinal));

        if (blocker is not null)
        {

            return Fail(new Error(
                ErrorCodes.Data.Blocked,
                blocker.Message));

        }

        bool acknowledgedWithoutPrompt =
            invocationContext.Options.Yes && force;

        if (plan.Scope is InstallationResetScope.Global or InstallationResetScope.All)
        {

            disclosureWriter.Write(
                stoppedHostPlan?.CovenantDisclosure
                    ?? onlinePlan!.Covenant);

        }

        if (!acknowledgedWithoutPrompt)
        {

            bool promptAvailable = environment.IsInteractive
                && !invocationContext.Options.Json
                && !invocationContext.Options.Print;

            if (!promptAvailable)
            {

                return Fail(
                    "Confirmation is unavailable in headless mode. Pass --yes and --force together.",
                    CliExitCode.ConfigurationError);

            }

            WriteHumanPlan(plan);

            bool confirmed = await confirmationPrompt
                .PromptAsync(plan, cancellationToken)
                .ConfigureAwait(false);

            if (!confirmed)
            {

                return Fail(
                    "Installation reset confirmation was not accepted. Type exactly RESET to continue.",
                    CliExitCode.ConfigurationError);

            }

        }

        Result<InstallationResetResult> applied = externalRemediation is null
            ? await applyBoundary
                .ApplyFreshAsync(
                    request,
                    stoppedHostPlan!,
                    cancellationToken)
                .ConfigureAwait(false)
            : await applyBoundary
                .ApplyFullAsync(
                    new FullInstallationResetRequest(
                        externalRemediation.OperationId,
                        new InstallationResetApplyRequest(request, plan.PlanId),
                        externalRemediation),
                    cancellationToken)
                .ConfigureAwait(false);

        if (applied.IsFailure)
        {

            return Fail(applied.Error);

        }

        InstallationResetResult result = applied.Value;

        WriteResult(result);

        if (IsCancellationResult(result))
        {

            return (int)CliExitCode.Cancelled;

        }

        return IsCompleted(result)
            ? (int)CliExitCode.Success
            : (int)CliExitCode.GenericError;

    }

    public Task<int> Execute(
        bool workspace,
        bool global,
        bool all,
        bool dryRun,
        bool apply,
        bool force,
        CancellationToken cancellationToken) =>
        Execute(
            workspace,
            global,
            all,
            dryRun,
            apply,
            force,
            externalRemediationAttestationPath: null,
            cancellationToken);

    private static string? ValidateShape(
        bool workspace,
        bool global,
        bool all,
        bool dryRun,
        bool apply,
        bool force,
        bool yes)
    {

        int scopes = (workspace ? 1 : 0)
            + (global ? 1 : 0)
            + (all ? 1 : 0);

        if (scopes != 1)
        {

            return "Select exactly one reset scope: --workspace, --global, or --all.";

        }

        if (dryRun == apply)
        {

            return "Select exactly one reset mode: --dry-run or --apply.";

        }

        if (force && !apply)
        {

            return "--force is valid only with --apply.";

        }

        if (yes != force)
        {

            return "Noninteractive reset acknowledgement requires --yes and --force together.";

        }

        return null;

    }

    private static InstallationResetScope ResolveScope(
        bool workspace,
        bool global)
    {

        if (workspace)
        {

            return InstallationResetScope.Workspace;

        }

        return global
            ? InstallationResetScope.Global
            : InstallationResetScope.All;

    }

    private void WritePlan(InstallationResetPlan plan)
    {

        if (invocationContext.Options.Json)
        {

            dispatcher.WriteJson(
                plan,
                CliJsonContext.Default.InstallationResetPlan);

            return;

        }

        WriteHumanPlan(plan);

    }

    private void WriteHumanPlan(InstallationResetPlan plan)
    {

        dispatcher.WritePayload($"Installation reset plan {plan.PlanId}");

        dispatcher.WritePayload($"Scope: {FormatName(plan.Scope)}");

        dispatcher.WritePayload($"Generated: {plan.GeneratedAt:O}");

        dispatcher.WritePayload(
            $"Candidates: {FormatCount(plan.Rows)} rows, "
            + $"{FormatCount(plan.Files)} files, "
            + $"{FormatCount(plan.EstimatedBytes)} bytes");

        dispatcher.WritePayload(
            $"Targets: {FormatCount(plan.Targets.Length)}; "
            + $"credentials: {FormatCount(plan.Credentials.Length)}; "
            + $"blockers: {FormatCount(plan.Blockers.Length)}");

        dispatcher.WritePayload(
            $"Data inventory available: {FormatBoolean(plan.DataInventoryAvailable)}; "
            + $"credential inventory available: {FormatBoolean(plan.CredentialInventoryAvailable)}");

        foreach (InstallationResetTargetDescriptor target in plan.Targets)
        {

            string authority = target.CanonicalPath
                ?? target.DatabasePredicate
                ?? target.ResourceId;

            dispatcher.WritePayload(
                $"Target [{FormatName(target.Role)}]: {authority}");

        }

        foreach (InstallationResetCredentialSummary credential in plan.Credentials)
        {

            dispatcher.WritePayload(
                $"Credential [{FormatName(credential.Status)}]: {credential.Account}");

        }

        foreach (InstallationResetPreservedBackup backup in plan.PreservedBackups)
        {

            dispatcher.WritePayload(
                $"Preserved backup: {backup.CanonicalPath}");

        }

        foreach (InstallationResetExclusion exclusion in plan.Exclusions)
        {

            dispatcher.WritePayload(
                $"Excluded: {exclusion.ResourceId} ({exclusion.Reason})");

        }

        foreach (InstallationResetIssueSummary blocker in plan.Blockers)
        {

            dispatcher.WritePayload(
                $"Blocker [{blocker.Code}]: {blocker.Message}");

        }

    }

    private void WriteResult(InstallationResetResult result)
    {

        if (invocationContext.Options.Json)
        {

            dispatcher.WriteJson(
                result,
                CliJsonContext.Default.InstallationResetResult);

            return;

        }

        dispatcher.WritePayload("Installation reset result");

        dispatcher.WritePayload($"Operation: {result.OperationId:D}");

        dispatcher.WritePayload($"Plan: {result.PlanId}");

        dispatcher.WritePayload($"Scope: {FormatName(result.Scope)}");

        dispatcher.WritePayload($"Phase: {FormatName(result.Phase)}");

        dispatcher.WritePayload(
            $"Deleted: {FormatCount(result.RowsDeleted)} rows, "
            + $"{FormatCount(result.FilesDeleted)} files, "
            + $"{FormatCount(result.EstimatedBytesDeleted)} bytes");

        dispatcher.WritePayload(
            $"Verification passed: {FormatBoolean(result.Verification.Succeeded)}; "
            + $"recovery required: {FormatBoolean(result.ResumeRequired)}");

        if (!string.IsNullOrWhiteSpace(result.ErrorCode))
        {

            dispatcher.WritePayload($"Error: {result.ErrorCode}");

        }

    }

    private int Fail(Error error)
    {

        CliExitCode exitCode = error.Code.StartsWith(
            "Connection.",
            StringComparison.Ordinal)
            ? CliExitCode.NetworkError
            : CliExitCode.GenericError;

        return Fail(
            $"{error.Code}: {error.Message}",
            exitCode);

    }

    private int Fail(string message, CliExitCode exitCode)
    {

        dispatcher.WriteDiagnostic(message);

        if (invocationContext.Options.Json)
        {

            dispatcher.WriteJson(
                new CliErrorPayload(message, (int)exitCode),
                CliJsonContext.Default.CliErrorPayload);

        }

        return (int)exitCode;

    }

    private static bool IsCompleted(InstallationResetResult result) =>
        result.Phase == InstallationResetPhase.Completed
        && result.Verification.Succeeded
        && !result.ResumeRequired
        && string.IsNullOrWhiteSpace(result.ErrorCode);

    private static bool IsCancellationResult(InstallationResetResult result) =>
        result.ResumeRequired
        && result.Verification.RemainingIssues.Any(static issue =>
            string.Equals(
                issue.Code,
                ErrorCodes.Data.RecoveryRequired,
                StringComparison.Ordinal)
            && issue.Message.Contains(
                "cancel",
                StringComparison.OrdinalIgnoreCase));

    private static string FormatCount(long? value) =>
        value?.ToString("N0", CultureInfo.InvariantCulture) ?? "unknown";

    private static string FormatBoolean(bool value) =>
        value ? "yes" : "no";

    private static string FormatName<T>(T value)
        where T : struct, Enum
    {

        string name = value.ToString();

        List<char> formatted = new(name.Length + 4);

        for (int index = 0; index < name.Length; index++)
        {

            char character = name[index];

            if (index > 0 && char.IsUpper(character))
            {

                formatted.Add('-');

            }

            formatted.Add(char.ToLowerInvariant(character));

        }

        return new string([.. formatted]);

    }

}
