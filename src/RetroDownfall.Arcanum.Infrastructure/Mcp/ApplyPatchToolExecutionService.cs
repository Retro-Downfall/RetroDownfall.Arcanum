using System.Text;
using System.Text.Json;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

internal sealed record ApplyPatchToolExecutionResponse(
    string SerializedResult);

internal sealed class ApplyPatchToolExecutionService
{

    private readonly string _workspaceRoot;

    private readonly WorkspacePatchSettings _settings;

    private readonly long _outputBudgetBytes;

    private readonly McpJsonSerializerContext _json;

    private readonly Func<string, MultiFileCommitCoordinator> _coordinatorFactory;

    private readonly TimeProvider _timeProvider;

    internal ApplyPatchToolExecutionService(
        string workspaceRoot,
        WorkspacePatchSettings settings,
        long outputBudgetBytes,
        McpJsonSerializerContext json,
        Func<string, MultiFileCommitCoordinator>? coordinatorFactory = null,
        TimeProvider? timeProvider = null)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        ArgumentNullException.ThrowIfNull(settings);

        ArgumentNullException.ThrowIfNull(json);

        if (outputBudgetBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(outputBudgetBytes));
        }

        _workspaceRoot = Path.GetFullPath(workspaceRoot);

        _settings =
            ArcanumSettingClamps.NormalizeWorkspacePatchSettings(
                settings);

        _outputBudgetBytes = outputBudgetBytes;

        _json = json;

        _timeProvider = timeProvider ?? TimeProvider.System;

        _coordinatorFactory = coordinatorFactory
            ?? (root => new MultiFileCommitCoordinator(
                root,
                new MultiFileCommitCoordinatorOptions
                {
                    MaxStagingBytesPerFile =
                        _settings.MaxStagingBytesPerFile,
                    MaxTotalStagingBytes =
                        _settings.MaxTotalStagingBytes,
                    CleanupTimeout = TimeSpan.FromMilliseconds(
                        _settings.RecoveryTimeoutMilliseconds),
                }));

    }

    internal async Task<ApplyPatchToolExecutionResponse> ExecuteAsync(
        ApplyPatchParams request,
        ApplyPatchInvocationContext context,
        CancellationToken cancellationToken)
    {

        return await ExecuteCoreAsync(
            request,
            context,
            cancellationToken,
            cancellationToken).ConfigureAwait(false);

    }

    private async Task<ApplyPatchToolExecutionResponse> ExecuteCoreAsync(
        ApplyPatchParams request,
        ApplyPatchInvocationContext context,
        CancellationToken cancellationToken,
        CancellationToken handoffCancellationToken)
    {

        ArgumentNullException.ThrowIfNull(request);

        ArgumentNullException.ThrowIfNull(context);

        cancellationToken.ThrowIfCancellationRequested();

        ValidateInvocationContext(context);

        ToolInteractionReceipt receipt =
            ToolInteractionReceiptDerivation.Derive(context.Identity);

        ApplyPatchReceiptProbeResult probe =
            await context.Sink.ProbeAsync(
                new ApplyPatchReceiptProbe(
                    context.SessionId,
                    receipt,
                    context.Identity.ProviderToolCallId,
                    ToolRiskClassifier.ApplyPatchToolName,
                    context.SerializedArguments,
                    context.ModelUsed,
                    context.CreatedAt),
                cancellationToken).ConfigureAwait(false);

        if (probe.Outcome == ApplyPatchReceiptProbeOutcome.Replayed)
        {

            if (probe.SerializedResult is null)
            {

                throw new ApplyPatchReceiptHandoffException(
                    MandatoryToolInteractionAppendOutcome.Ambiguous);

            }

            context.RecordHandoffOutcome(
                MandatoryToolInteractionAppendOutcome.RecoveredCommitted);

            return Normal(probe.SerializedResult);

        }

        if (probe.Outcome == ApplyPatchReceiptProbeOutcome.Mismatched)
        {

            return Normal(
                SerializeBounded(
                    ReceiptPreflightFailure(
                        "receipt_mismatch",
                        "This logical apply_patch invocation was already used with a different immutable payload.")));

        }

        if (probe.Outcome != ApplyPatchReceiptProbeOutcome.NotFound)
        {

            throw new ApplyPatchReceiptHandoffException(
                MandatoryToolInteractionAppendOutcome.Ambiguous);

        }

        UnifiedDiffParseResult parsed = UnifiedDiffParser.Parse(
            request.Patch,
            _settings,
            cancellationToken);

        if (!parsed.Success)
        {
            return Normal(
                SerializeBounded(
                    new WorkspacePatchToolResultEnvelope
                    {
                        Status = "invalid_patch",
                        Code = parsed.Code,
                        Message = parsed.Message,
                    }));
        }

        WorkspacePatchPlanResult planning =
            await new WorkspacePatchPlanner(
                _settings).PlanAsync(
                _workspaceRoot,
                parsed.Manifest!,
                cancellationToken).ConfigureAwait(false);

        if (!planning.Success)
        {
            return Normal(
                SerializeBounded(
                    new WorkspacePatchToolResultEnvelope
                    {
                        Status = "conflict",
                        Code = planning.Code,
                        Message = planning.Message,
                        Diagnostic = planning.Diagnostic,
                    }));
        }

        WorkspacePatchPlan plan = planning.Plan!;

        if (request.DryRun)
        {
            return Normal(
                SerializeBounded(
                    BuildResult(
                        plan,
                        status: "dry_run",
                        fileStatus: "planned")));
        }

        string serializedResult = SerializeBounded(
            BuildResult(
                plan,
                status: "ok",
                fileStatus: "applied"));

        ApplyPatchReceiptPreflightResult preflight =
            await context.Sink.PreflightAsync(
                new ApplyPatchReceiptPreflight(
                    context.SessionId,
                    receipt,
                    context.Identity.ProviderToolCallId,
                    ToolRiskClassifier.ApplyPatchToolName,
                    context.SerializedArguments,
                    serializedResult,
                    context.ModelUsed,
                    context.CreatedAt),
                cancellationToken).ConfigureAwait(false);

        if (preflight.Outcome == ApplyPatchReceiptPreflightOutcome.Replayed)
        {

            if (preflight.SerializedResult is null)
            {

                throw new ApplyPatchReceiptHandoffException(
                    MandatoryToolInteractionAppendOutcome.Ambiguous);

            }

            context.RecordHandoffOutcome(
                MandatoryToolInteractionAppendOutcome.RecoveredCommitted);

            return Normal(preflight.SerializedResult);

        }

        if (preflight.Outcome == ApplyPatchReceiptPreflightOutcome.Rejected)
        {

            return Normal(
                SerializeBounded(
                    ReceiptPreflightFailure(
                        "receipt_capacity",
                        "The exact mandatory apply_patch receipt exceeds the current session entry capacity.")));

        }

        if (preflight.Outcome == ApplyPatchReceiptPreflightOutcome.Mismatched)
        {

            return Normal(
                SerializeBounded(
                    ReceiptPreflightFailure(
                        "receipt_mismatch",
                        "This logical apply_patch invocation was already used with a different immutable payload.")));

        }

        if (preflight.Outcome != ApplyPatchReceiptPreflightOutcome.Admitted)
        {

            throw new ApplyPatchReceiptHandoffException(
                MandatoryToolInteractionAppendOutcome.Ambiguous);

        }

        cancellationToken.ThrowIfCancellationRequested();

        MultiFileCommitCoordinator coordinator =
            _coordinatorFactory(_workspaceRoot);

        WorkspaceCommitResult commit;

        try
        {
            commit = await coordinator.CommitAsync(
                plan.CommitOperations,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException cancellation)
        {
            WorkspaceCommitRecovery? recovery =
                cancellation.Data[nameof(WorkspaceCommitRecovery)]
                    as WorkspaceCommitRecovery;

            if (recovery is not null)
            {
                string recoveryResult = SerializeBounded(
                    BuildRollbackIncompleteResult(
                        plan,
                        recovery,
                        "Patch execution was canceled and rollback could not safely restore every path."));

                await PersistCancellationRecoveryReceiptAsync(
                    context,
                    receipt,
                    recoveryResult,
                    recovery,
                    cancellation).ConfigureAwait(false);
            }

            throw;
        }

        if (commit.Status != WorkspaceCommitStatus.Committed)
        {
            WorkspacePatchToolResultEnvelope failure =
                BuildCommitFailureResult(plan, commit);
            string failureResult = SerializeBounded(failure);

            if (commit.Status == WorkspaceCommitStatus.RollbackIncomplete)
            {
                _ = await PersistRecoveryReceiptAsync(
                    context,
                    receipt,
                    failureResult,
                    commit.Recovery).ConfigureAwait(false);
            }

            return Normal(failureResult);
        }

        if (commit.Transaction is null)
        {
            throw new InvalidOperationException(
                "A committed workspace transaction did not expose its reversible receipt.");
        }

        PendingApplyPatchReceipt pending = new(
            context.SessionId,
            receipt,
            context.Identity.ProviderToolCallId,
            ToolRiskClassifier.ApplyPatchToolName,
            context.SerializedArguments,
            serializedResult,
            context.ModelUsed,
            context.CreatedAt,
            commit.Recovery,
            commit.Transaction);

        ApplyPatchPendingReceiptHandoffResult handoff;

        try
        {
            handoff = await context.Sink.HandoffAsync(
                pending,
                handoffCancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException cancellation)
        {
            MandatoryToolInteractionAppendOutcome outcome =
                TryReadCancellationOutcome(cancellation)
                ?? MandatoryToolInteractionAppendOutcome.Ambiguous;

            WorkspaceCommitRecovery? recovery =
                cancellation.Data[nameof(WorkspaceCommitRecovery)]
                    as WorkspaceCommitRecovery;

            if (outcome == MandatoryToolInteractionAppendOutcome.Failed
                && recovery is not null)
            {
                string recoveryResult = SerializeBounded(
                    BuildRollbackIncompleteResult(plan, recovery));

                await PersistCancellationRecoveryReceiptAsync(
                    context,
                    receipt,
                    recoveryResult,
                    recovery,
                    cancellation).ConfigureAwait(false);
            }
            else
            {
                context.RecordCancellationOutcome(outcome);

                if (outcome == MandatoryToolInteractionAppendOutcome.Ambiguous
                    && pending.Recovery is not null
                    && !cancellation.Data.Contains(
                        nameof(WorkspaceCommitRecovery)))
                {
                    cancellation.Data[nameof(WorkspaceCommitRecovery)] =
                        pending.Recovery;
                }
            }

            throw;
        }
        catch (Exception exception)
        {
            context.RecordHandoffOutcome(
                MandatoryToolInteractionAppendOutcome.Ambiguous);

            throw new ApplyPatchReceiptHandoffException(
                MandatoryToolInteractionAppendOutcome.Ambiguous,
                exception,
                pending.Recovery);
        }

        context.RecordHandoffOutcome(handoff.Outcome);

        if (handoff.Outcome is MandatoryToolInteractionAppendOutcome.NewlyCommitted
            or MandatoryToolInteractionAppendOutcome.RecoveredCommitted)
        {
            return Normal(serializedResult);
        }

        if (handoff.Outcome == MandatoryToolInteractionAppendOutcome.Failed)
        {
            WorkspaceRollbackResult rollback = handoff.Rollback
                ?? await pending.RollbackAsync(
                    CancellationToken.None).ConfigureAwait(false);

            WorkspacePatchToolResultEnvelope failure = rollback.Complete
                ? BuildReceiptFailureResult(plan)
                : BuildRollbackIncompleteResult(
                    plan,
                    rollback.Recovery ?? pending.Recovery);
            string failureResult = SerializeBounded(failure);

            if (!rollback.Complete)
            {
                _ = await PersistRecoveryReceiptAsync(
                    context,
                    receipt,
                    failureResult,
                    rollback.Recovery ?? pending.Recovery)
                    .ConfigureAwait(false);
            }

            return Normal(failureResult);
        }

        throw new ApplyPatchReceiptHandoffException(
            handoff.Outcome,
            recovery: pending.Recovery);

    }

    private async Task PersistCancellationRecoveryReceiptAsync(
        ApplyPatchInvocationContext context,
        ToolInteractionReceipt receipt,
        string serializedResult,
        WorkspaceCommitRecovery recovery,
        OperationCanceledException cancellation)
    {

        try
        {
            MandatoryToolInteractionAppendOutcome outcome =
                await PersistRecoveryReceiptAsync(
                    context,
                    receipt,
                    serializedResult,
                    recovery).ConfigureAwait(false);

            context.RecordCancellationOutcome(outcome);
            cancellation.Data[
                nameof(MandatoryToolInteractionAppendOutcome)] = outcome;
        }
        catch (ApplyPatchReceiptHandoffException handoff)
        {
            context.RecordCancellationOutcome(handoff.Outcome);
            cancellation.Data[
                nameof(MandatoryToolInteractionAppendOutcome)] =
                handoff.Outcome;
        }

    }

    private async Task<MandatoryToolInteractionAppendOutcome>
        PersistRecoveryReceiptAsync(
            ApplyPatchInvocationContext context,
            ToolInteractionReceipt receipt,
            string serializedResult,
            WorkspaceCommitRecovery? recovery)
    {

        using CancellationTokenSource persistenceDeadline = new(
            TimeSpan.FromMilliseconds(
                Math.Max(1, _settings.RecoveryTimeoutMilliseconds)),
            _timeProvider);

        MandatoryToolInteractionAppendOutcome outcome;

        try
        {
            outcome = await context.Sink.PersistRecoveryReceiptAsync(
                new ApplyPatchRecoveryReceipt(
                    context.SessionId,
                    receipt,
                    context.Identity.ProviderToolCallId,
                    ToolRiskClassifier.ApplyPatchToolName,
                    context.SerializedArguments,
                    serializedResult,
                    context.ModelUsed,
                    context.CreatedAt),
                persistenceDeadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException cancellation)
        {
            outcome = TryReadCancellationOutcome(cancellation)
                ?? MandatoryToolInteractionAppendOutcome.Ambiguous;
        }
        catch (Exception exception)
        {
            context.RecordHandoffOutcome(
                MandatoryToolInteractionAppendOutcome.Ambiguous);
            context.RequireTurnFailure();
            throw new ApplyPatchReceiptHandoffException(
                MandatoryToolInteractionAppendOutcome.Ambiguous,
                exception,
                recovery);
        }

        context.RecordHandoffOutcome(outcome);

        if (outcome is MandatoryToolInteractionAppendOutcome.NewlyCommitted
            or MandatoryToolInteractionAppendOutcome.RecoveredCommitted)
        {
            return outcome;
        }

        context.RequireTurnFailure();
        throw new ApplyPatchReceiptHandoffException(
            outcome,
            recovery: recovery);

    }

    private static ApplyPatchToolExecutionResponse Normal(string result) =>
        new(result);

    private static WorkspacePatchToolResultEnvelope ReceiptPreflightFailure(
        string code,
        string message) =>
        new()
        {
            Status = "conflict",
            Code = code,
            Message = message,
        };

    private WorkspacePatchToolResultEnvelope BuildResult(
        WorkspacePatchPlan plan,
        string status,
        string fileStatus)
    {

        WorkspacePatchToolResultItem[] files = plan.Files
            .Select(file =>
                new WorkspacePatchToolResultItem(
                    file.Path,
                    OperationName(file.Operation),
                    fileStatus,
                    file.AppliedHunks)
                {
                    Hunks = file.Hunks
                        .ToArray(),
                })
            .ToArray();

        return new WorkspacePatchToolResultEnvelope
        {
            Status = status,
            Files = files,
            TotalFileCount = plan.Files.Count,
            OmittedFileCount = plan.Files.Count - files.Length,
            Truncated = files.Length != plan.Files.Count,
        };

    }

    private WorkspacePatchToolResultEnvelope BuildCommitFailureResult(
        WorkspacePatchPlan plan,
        WorkspaceCommitResult commit)
    {

        string code = commit.Status == WorkspaceCommitStatus.RollbackIncomplete
            ? "rollback_incomplete"
            : commit.Failure switch
            {
                WorkspaceCommitFailure.ConcurrentModification =>
                    "concurrent_edit",
                WorkspaceCommitFailure.Validation =>
                    "validation_failed",
                WorkspaceCommitFailure.StagingFailed =>
                    "staging_failed",
                WorkspaceCommitFailure.CommitFailed =>
                    "commit_failed",
                _ =>
                    "commit_failed",
            };

        WorkspacePatchToolResultEnvelope result = BuildResult(
            plan,
            status: commit.Status == WorkspaceCommitStatus.RollbackIncomplete
                ? "rollback_incomplete"
                : "conflict",
            fileStatus: "not_applied");

        string[] affectedPaths =
            WorkspaceRelativePath.NormalizeDistinctOrdered(
                commit.Recovery?.AffectedPaths);
        string[] artifactPaths =
            WorkspaceRelativePath.NormalizeDistinctOrdered(
                commit.Recovery?.ArtifactPaths);

        return result with
        {
            Code = code,
            Message = commit.Status == WorkspaceCommitStatus.RollbackIncomplete
                ? "Rollback could not safely restore every affected path; operator recovery is required."
                : "The workspace changed or commit validation failed before the transaction could complete.",
            AffectedPaths = affectedPaths,
            TotalAffectedPathCount = affectedPaths.Length,
            RecoveryArtifactPaths = artifactPaths,
            TotalRecoveryArtifactPathCount = artifactPaths.Length,
        };

    }

    private WorkspacePatchToolResultEnvelope BuildRollbackIncompleteResult(
        WorkspacePatchPlan plan,
        WorkspaceCommitRecovery? recovery,
        string message =
            "The pending receipt handoff failed and rollback could not safely restore every path.")
    {

        WorkspacePatchToolResultEnvelope result = BuildResult(
            plan,
            status: "rollback_incomplete",
            fileStatus: "recovery_required");

        string[] affectedPaths =
            WorkspaceRelativePath.NormalizeDistinctOrdered(
                recovery?.AffectedPaths);
        string[] artifactPaths =
            WorkspaceRelativePath.NormalizeDistinctOrdered(
                recovery?.ArtifactPaths);

        return result with
        {
            Code = "rollback_incomplete",
            Message = message,
            AffectedPaths = affectedPaths,
            TotalAffectedPathCount = affectedPaths.Length,
            RecoveryArtifactPaths = artifactPaths,
            TotalRecoveryArtifactPathCount = artifactPaths.Length,
        };

    }

    private WorkspacePatchToolResultEnvelope BuildReceiptFailureResult(
        WorkspacePatchPlan plan) =>
        BuildResult(
            plan,
            status: "conflict",
            fileStatus: "not_applied") with
        {
            Code = "receipt_failed",
            Message =
                "The mandatory apply_patch receipt could not be persisted; the workspace changes were rolled back.",
        };

    private static MandatoryToolInteractionAppendOutcome?
        TryReadCancellationOutcome(OperationCanceledException cancellation)
    {

        object? value =
            cancellation.Data[nameof(MandatoryToolInteractionAppendOutcome)];

        return value is MandatoryToolInteractionAppendOutcome outcome
            ? outcome
            : null;

    }

    private string SerializeBounded(
        WorkspacePatchToolResultEnvelope result)
    {

        string serialized = JsonSerializer.Serialize(
            result,
            _json.WorkspacePatchToolResultEnvelope);

        if (Encoding.UTF8.GetByteCount(serialized) <= _outputBudgetBytes)
        {
            return serialized;
        }

        int affectedPathCount = result.AffectedPaths.Length;
        int recoveryArtifactPathCount =
            result.RecoveryArtifactPaths.Length;
        string? filePrefixFit = SerializeLargestFittingFilePrefix(
            result,
            affectedPathCount,
            recoveryArtifactPathCount);

        if (filePrefixFit is not null)
        {
            return filePrefixFit;
        }

        int fileCount = 0;
        bool omitMessage = false;

        while (true)
        {
            WorkspacePatchToolResultEnvelope source = omitMessage
                ? result with { Message = null }
                : result;
            WorkspacePatchToolResultEnvelope candidate =
                source.RetainLeadingItems(
                    fileCount,
                    affectedPathCount,
                    recoveryArtifactPathCount);

            serialized = JsonSerializer.Serialize(
                candidate,
                _json.WorkspacePatchToolResultEnvelope);

            if (Encoding.UTF8.GetByteCount(serialized)
                <= _outputBudgetBytes)
            {
                return serialized;
            }

            if (affectedPathCount > 1
                || recoveryArtifactPathCount > 1)
            {
                bool trimAffected =
                    affectedPathCount > 1
                    && (recoveryArtifactPathCount <= 1
                        || Encoding.UTF8.GetByteCount(
                            result.AffectedPaths[affectedPathCount - 1])
                        >= Encoding.UTF8.GetByteCount(
                            result.RecoveryArtifactPaths[
                                recoveryArtifactPathCount - 1]));

                if (trimAffected)
                {
                    affectedPathCount--;
                }
                else
                {
                    recoveryArtifactPathCount--;
                }

                continue;
            }

            if (!omitMessage && result.Message is not null)
            {
                omitMessage = true;
                continue;
            }

            if (affectedPathCount > 0)
            {
                affectedPathCount--;
                continue;
            }

            if (recoveryArtifactPathCount > 0)
            {
                recoveryArtifactPathCount--;
                continue;
            }

            break;
        }

        return JsonSerializer.Serialize(
            new MinimalStructuredToolResultEnvelope(),
            _json.MinimalStructuredToolResultEnvelope);

    }

    private string? SerializeLargestFittingFilePrefix(
        WorkspacePatchToolResultEnvelope result,
        int affectedPathCount,
        int recoveryArtifactPathCount)
    {
        int low = 0;
        int high = result.Files.Length - 1;
        string? bestFit = null;

        while (low <= high)
        {
            int fileCount = low + ((high - low) / 2);
            WorkspacePatchToolResultEnvelope candidate =
                result.RetainLeadingItems(
                    fileCount,
                    affectedPathCount,
                    recoveryArtifactPathCount);
            string serialized = JsonSerializer.Serialize(
                candidate,
                _json.WorkspacePatchToolResultEnvelope);

            if (Encoding.UTF8.GetByteCount(serialized)
                <= _outputBudgetBytes)
            {
                bestFit = serialized;
                low = fileCount + 1;
            }
            else
            {
                high = fileCount - 1;
            }
        }

        return bestFit;
    }

    private static void ValidateInvocationContext(
        ApplyPatchInvocationContext context)
    {

        if (context.SessionId == Guid.Empty
            || context.AssistantEntryId == Guid.Empty
            || string.IsNullOrWhiteSpace(context.SerializedArguments)
            || string.IsNullOrWhiteSpace(context.ModelUsed)
            || context.Identity is null
            || !string.Equals(
                context.Identity.ToolName,
                ToolRiskClassifier.ApplyPatchToolName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "apply_patch requires a bound persisted invocation context.");
        }

    }

    private static string OperationName(
        UnifiedDiffOperationKind operation) =>
        operation switch
        {
            UnifiedDiffOperationKind.Create => "create",
            UnifiedDiffOperationKind.Modify => "modify",
            UnifiedDiffOperationKind.Rename => "rename",
            UnifiedDiffOperationKind.Delete => "delete",
            _ => "unknown",
        };
}
