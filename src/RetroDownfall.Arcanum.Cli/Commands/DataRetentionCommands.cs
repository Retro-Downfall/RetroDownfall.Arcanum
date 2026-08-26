using System.Globalization;

using System.Text;

using System.Text.Json.Serialization.Metadata;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Cli.UX;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Cli.Commands;

internal sealed class DataRetentionCommands(
    ArcanumApiClient apiClient,
    IConsoleDispatcher dispatcher,
    ICliInvocationContext invocationContext,
    IConfirmationPrompt confirmationPrompt,
    CovenantExternalRetentionDisclosureWriter disclosureWriter)
{

    public async Task<int> Status(CancellationToken cancellationToken)
    {

        Result<DataRetentionStatus> result = await apiClient
            .GetDataRetentionStatusAsync(cancellationToken)
            .ConfigureAwait(false);

        return WriteResult(
            result,
            ArcanumJsonContext.Default.DataRetentionStatus,
            WriteStatus);

    }

    public async Task<int> RetentionShow(CancellationToken cancellationToken)
    {

        Result<RetentionSettings> result = await apiClient
            .GetDataRetentionSettingsAsync(cancellationToken)
            .ConfigureAwait(false);

        return WriteResult(
            result,
            ArcanumJsonContext.Default.RetentionSettings,
            WriteSettings);

    }

    public async Task<int> RetentionSet(
        string dataClass,
        string value,
        CancellationToken cancellationToken)
    {

        if (!DataRetentionDataClassParser.TryParse(
                dataClass,
                out _))
        {

            dispatcher.WriteDiagnostic(
                "<data-class> must name a supported retention data class.");

            return (int)CliExitCode.ConfigurationError;

        }

        if (!TryParseRetentionValue(value, out bool enabled, out int days))
        {

            dispatcher.WriteDiagnostic(
                "<days|disabled> must be an integer number of days or 'disabled'.");

            return (int)CliExitCode.ConfigurationError;

        }

        if (!await ConfirmMutationAsync(
                $"Update the {dataClass} retention rule?",
                cancellationToken)
            .ConfigureAwait(false))
        {

            dispatcher.WriteDiagnostic("Retention update cancelled.");

            return (int)CliExitCode.Success;

        }

        RetentionRuleUpdateRequest request = new(
            dataClass,
            enabled,
            enabled ? days : null);

        Result<RetentionSettings> result = await apiClient
            .UpdateDataRetentionRuleAsync(request, cancellationToken)
            .ConfigureAwait(false);

        return WriteResult(
            result,
            ArcanumJsonContext.Default.RetentionSettings,
            WriteSettings);

    }

    public async Task<int> Prune(
        bool dryRun,
        bool apply,
        CancellationToken cancellationToken)
    {

        if (dryRun == apply)
        {

            dispatcher.WriteDiagnostic(
                "Choose exactly one of --dry-run or --apply.");

            return (int)CliExitCode.ConfigurationError;

        }

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        if (dryRun)
        {

            Result<DataRetentionPlan> plan = await apiClient
                .PlanDataPruneAsync(request, cancellationToken)
                .ConfigureAwait(false);

            return WriteResult(
                plan,
                ArcanumJsonContext.Default.DataRetentionPlan,
                WritePlan);

        }

        Result<DataRetentionPlan> preview = await apiClient
            .PlanDataPruneAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (preview.IsFailure)
        {

            return WriteError(preview.Error);

        }

        DataRetentionPlan exactPlan = preview.Value;

        if (!invocationContext.Options.Json)
        {

            WritePlan(exactPlan);

        }

        if (!await ConfirmMutationAsync(
                $"Apply prune plan {exactPlan.PlanId} "
                + $"({FormatCount(exactPlan.Rows)} rows, "
                + $"{FormatCount(exactPlan.Files)} files, "
                + $"{FormatCount(exactPlan.EstimatedBytes)} bytes)?",
                cancellationToken)
            .ConfigureAwait(false))
        {

            dispatcher.WriteDiagnostic("Retention pruning cancelled.");

            return (int)CliExitCode.Success;

        }

        Result<DataRetentionApplyResult> result = await apiClient
            .ApplyDataPruneAsync(
                new DataRetentionApplyRequest(request, exactPlan.PlanId),
                cancellationToken)
            .ConfigureAwait(false);

        return WriteApplyResult(result);

    }

    public Task<int> DeleteSession(
        Guid sessionId,
        CancellationToken cancellationToken) =>
        ConfirmAndApply(
            $"Delete session {sessionId:D} and its session-scoped data?",
            "Session deletion cancelled.",
            token => apiClient.DeleteDataSessionAsync(sessionId, token),
            cancellationToken);

    public Task<int> DeleteAttachment(
        Guid attachmentId,
        CancellationToken cancellationToken) =>
        ConfirmAndApply(
            $"Delete attachment {attachmentId:D}, its bytes, chunks, and embeddings?",
            "Attachment deletion cancelled.",
            token => apiClient.DeleteDataAttachmentAsync(attachmentId, token),
            cancellationToken);

    /// <param name="campaign">
    /// Optional Campaign GUID. Only Saga and Lexicon record an owning Campaign, so only those two
    /// scopes accept it; every other Campaign's memories, and every installation-scoped one, are left
    /// exactly where they were.
    /// </param>
    public async Task<int> ResetMemory(
        string scope,
        string? campaign,
        CancellationToken cancellationToken)
    {

        if (!TryParseMemoryScope(scope, out MemoryResetScope parsedScope))
        {

            dispatcher.WriteDiagnostic(
                "--scope must be entry, attachments, workspace, saga, lexicon, or covenant.");

            return (int)CliExitCode.ConfigurationError;

        }

        Guid? campaignId = null;

        if (!string.IsNullOrWhiteSpace(campaign))
        {

            if (!Guid.TryParse(campaign, out Guid parsedCampaign))
            {

                dispatcher.WriteDiagnostic("--campaign must be a GUID.");

                return (int)CliExitCode.ConfigurationError;

            }

            campaignId = parsedCampaign;

        }

        // Written before the prompt, and before any operation starts. The order is the contract: an
        // operator has to be told what local erasure cannot revoke while they can still decline, and a
        // refusal after the disclosure must leave nothing started (§10.20.2).
        Result<DataRetentionPlan> preview = await apiClient
            .PlanDataMemoryResetAsync(
                new MemoryResetRequest(parsedScope, CampaignId: campaignId),
                cancellationToken)
            .ConfigureAwait(false);

        if (preview.IsFailure)
        {

            return WriteError(preview.Error);

        }

        disclosureWriter.Write(preview.Value.Covenant);

        return await ConfirmAndApply(
                campaignId is { } confirmCampaign
                    ? $"Reset the {scope} memory scope for campaign {confirmCampaign:D}?"
                    : $"Reset the {scope} memory scope?",
                "Memory reset cancelled.",
                token => apiClient.ResetDataMemoryAsync(
                    new MemoryResetRequest(parsedScope, preview.Value.PlanId, campaignId),
                    token),
                cancellationToken)
            .ConfigureAwait(false);

    }

    private async Task<int> ConfirmAndApply(
        string question,
        string cancelledMessage,
        Func<CancellationToken, Task<Result<DataRetentionApplyResult>>> action,
        CancellationToken cancellationToken)
    {

        if (!await ConfirmMutationAsync(question, cancellationToken)
                .ConfigureAwait(false))
        {

            dispatcher.WriteDiagnostic(cancelledMessage);

            return (int)CliExitCode.Success;

        }

        Result<DataRetentionApplyResult> result = await action(cancellationToken)
            .ConfigureAwait(false);

        return WriteApplyResult(result);

    }

    private Task<bool> ConfirmMutationAsync(
        string question,
        CancellationToken cancellationToken) =>
        confirmationPrompt.PromptForConfirmationAsync(
            question,
            cancellationToken);

    private int WriteApplyResult(Result<DataRetentionApplyResult> result) =>
        WriteResult(
            result,
            ArcanumJsonContext.Default.DataRetentionApplyResult,
            WriteApplySummary);

    private int WriteResult<T>(
        Result<T> result,
        JsonTypeInfo<T> typeInfo,
        Action<T> writeHuman)
    {

        if (result.IsFailure)
        {

            return WriteError(result.Error);

        }

        if (invocationContext.Options.Json)
        {

            dispatcher.WriteJson(result.Value, typeInfo);

        }
        else
        {

            writeHuman(result.Value);

        }

        return (int)CliExitCode.Success;

    }

    private void WriteStatus(DataRetentionStatus status)
    {

        dispatcher.WritePayload("Data retention status");

        dispatcher.WritePayload($"Generated: {status.GeneratedAt:O}");

        dispatcher.WritePayload(
            $"Total: {FormatCount(status.Rows)} rows, "
            + $"{FormatCount(status.Files)} files, "
            + $"{FormatCount(status.EstimatedBytes)} bytes");

        foreach (DataRetentionStatusItem item in status.Items)
        {

            string policy = item.PolicyEnabled
                ? $"enabled, {item.RetentionDays?.ToString(CultureInfo.InvariantCulture) ?? "unspecified"} days"
                : "disabled";

            dispatcher.WritePayload(
                $"  {FormatName(item.DataClass)}: "
                + $"{FormatCount(item.Rows)} rows, "
                + $"{FormatCount(item.Files)} files, "
                + $"{FormatCount(item.EstimatedBytes)} bytes; "
                + $"policy {policy}; store {item.Store}; provenance {item.Provenance}");

        }

        if (status.PreservedOutsideSelectedRoot.Length > 0)
        {

            dispatcher.WritePayload(
                "Preserved outside selected root: "
                + string.Join(", ", status.PreservedOutsideSelectedRoot));

        }

    }

    private void WriteSettings(RetentionSettings settings)
    {

        dispatcher.WritePayload("Retention settings");

        dispatcher.WritePayload(
            $"Automatic sweeps: {FormatEnabled(settings.AutomaticSweepsEnabled)}");

        dispatcher.WritePayload(
            $"Sweep interval: {FormatCount(settings.SweepIntervalHours)} hours; "
            + "eligible work continues through internal checkpoints until complete or cancelled");

        dispatcher.WritePayload(
            $"Accounting minimum: {FormatCount(settings.AccountingMinimumDays)} days; "
            + $"protected sessions: {FormatCount(settings.ProtectedSessionIds.Length)}");

        WriteRule("active-sessions", settings.ActiveSessions);

        WriteRule("archived-sessions", settings.ArchivedSessions);

        WriteRule("entries", settings.Entries);

        WriteRule("attachments", settings.Attachments);

        WriteRule("uploaded-files", settings.UploadedFiles);

        WriteRule("completed-batches", settings.CompletedBatches);

        WriteRule("saga-memories", settings.SagaMemories);

        WriteRule("lexicon-entries", settings.LexiconEntries);

        WriteRule("workspace-indexes", settings.WorkspaceIndexes);

        WriteRule("session-entry-embeddings", settings.SessionEntryEmbeddings);

        WriteRule("audit-logs", settings.AuditLogs);

        WriteRule("guardrail-logs", settings.GuardrailLogs);

        WriteRule("idempotency-claims", settings.IdempotencyClaims);

        WriteRule("accounting", settings.Accounting);

        WriteRule("long-running-operations", settings.LongRunningOperations);

        WriteRule("sanctum-breaches", settings.SanctumBreaches);

        WriteRule("daemon-history", settings.DaemonHistory);

    }

    private void WriteRule(
        string name,
        RetentionRuleSettings rule) =>
        dispatcher.WritePayload(
            $"  {name}: {FormatEnabled(rule.Enabled)}, "
            + $"{FormatCount(rule.Days)} days");

    private void WritePlan(DataRetentionPlan plan)
    {

        dispatcher.WritePayload($"Prune plan {plan.PlanId}");

        dispatcher.WritePayload($"Generated: {plan.GeneratedAt:O}");

        dispatcher.WritePayload(
            $"Candidates: {FormatCount(plan.Rows)} rows, "
            + $"{FormatCount(plan.Files)} files, "
            + $"{FormatCount(plan.EstimatedBytes)} bytes, "
            + $"{FormatCount(plan.DerivedRecords)} derived records");

        dispatcher.WritePayload(
            $"Blockers: {FormatCount(plan.Blockers.Length)}; "
            + $"conflicts: {FormatCount(plan.Conflicts.Length)}; "
            + $"confirmation required: {(plan.RequiresConfirmation ? "yes" : "no")}");

        foreach (DataRetentionPlanItem item in plan.Items)
        {

            dispatcher.WritePayload(
                $"  {FormatName(item.DataClass)}: "
                + $"{FormatCount(item.Rows)} rows, "
                + $"{FormatCount(item.Files)} files, "
                + $"{FormatCount(item.EstimatedBytes)} bytes, "
                + $"{FormatCount(item.DerivedRecords)} derived records");

        }

        foreach (DataRetentionBlocker blocker in plan.Blockers)
        {

            dispatcher.WritePayload(
                $"  Blocked {FormatName(blocker.DataClass)} "
                + $"{blocker.ResourceId}: {blocker.ReasonCode} - {blocker.Message}");

        }

        foreach (DataRetentionConflict conflict in plan.Conflicts)
        {

            dispatcher.WritePayload(
                $"  Conflict {conflict.ResourceId}: "
                + $"{conflict.Code} - {conflict.Message}");

        }

    }

    private void WriteApplySummary(DataRetentionApplyResult result)
    {

        dispatcher.WritePayload("Apply complete");

        dispatcher.WritePayload($"Operation: {result.OperationId:D}");

        dispatcher.WritePayload($"Plan: {result.PlanId}");

        dispatcher.WritePayload(
            $"Deleted: {FormatCount(result.RowsDeleted)} rows, "
            + $"{FormatCount(result.FilesDeleted)} files, "
            + $"{FormatCount(result.EstimatedBytesDeleted)} bytes, "
            + $"{FormatCount(result.DerivedRecordsDeleted)} derived records");

        dispatcher.WritePayload(
            $"Reconciled: {(result.Reconciled ? "yes" : "no")}; "
            + $"blockers: {FormatCount(result.Blockers.Length)}; "
            + $"conflicts: {FormatCount(result.Conflicts.Length)}");

        foreach (DataRetentionBlocker blocker in result.Blockers)
        {

            dispatcher.WritePayload(
                $"  Blocked {FormatName(blocker.DataClass)} "
                + $"{blocker.ResourceId}: {blocker.ReasonCode} - {blocker.Message}");

        }

        foreach (DataRetentionConflict conflict in result.Conflicts)
        {

            dispatcher.WritePayload(
                $"  Conflict {conflict.ResourceId}: "
                + $"{conflict.Code} - {conflict.Message}");

        }

    }

    private static string FormatCount(long value) =>
        value.ToString("N0", CultureInfo.InvariantCulture);

    private static string FormatEnabled(bool enabled) =>
        enabled ? "enabled" : "disabled";

    private static string FormatName(RetentionDataClass dataClass)
    {

        string name = dataClass.ToString();

        StringBuilder formatted = new(name.Length + 4);

        for (int index = 0; index < name.Length; index++)
        {

            char character = name[index];

            if (index > 0 && char.IsUpper(character))
            {

                formatted.Append('-');

            }

            formatted.Append(char.ToLowerInvariant(character));

        }

        return formatted.ToString();

    }

    private int WriteError(Error error)
    {

        dispatcher.WriteDiagnostic($"{error.Code}: {error.Message}");

        return (int)CliExitCode.GenericError;

    }

    private static bool TryParseRetentionValue(
        string value,
        out bool enabled,
        out int days)
    {

        if (string.Equals(
                value,
                "disabled",
                StringComparison.OrdinalIgnoreCase))
        {

            enabled = false;

            days = 0;

            return true;

        }

        enabled = true;

        return int.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out days);

    }

    private static bool TryParseMemoryScope(
        string scope,
        out MemoryResetScope parsedScope)
    {

        parsedScope = default;

        string normalized = scope
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);

        return normalized.Length > 0
            && normalized.All(char.IsLetter)
            && Enum.TryParse(
            normalized,
            ignoreCase: true,
            out parsedScope)
            && Enum.IsDefined(parsedScope);

    }

}
