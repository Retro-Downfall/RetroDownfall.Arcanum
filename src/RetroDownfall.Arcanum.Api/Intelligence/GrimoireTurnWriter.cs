using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;

using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Telemetry;

using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Grimoire begin/finalize/discard and interrupt-cleanup side-effects shared by buffered and
/// streaming inference paths in <see cref="WizardIntelligenceProvider"/>.
/// </summary>
public sealed class GrimoireTurnWriter(
    IGrimoireRepository grimoire,
    ISessionTurnBeginStore turnBeginStore,
    SessionEventHub sessionEventHub,
    ILogger<GrimoireTurnWriter> logger,
    IGrimoireTurnCommitter? turnCommitter = null)
{

    public const string PublicFinalizeFailureMessage =
        "The conversation reply could not be saved. Please try again.";

    public sealed class TurnHandle
    {

        public Guid? AssistantEntryId { get; internal set; }

        public Guid? SessionId { get; internal set; }

        public bool IsFinalized { get; internal set; }

    }

    /// <summary>
    /// Begins a buffered turn, or reports exactly why it could not.
    /// </summary>
    /// <remarks>
    /// Returns a <see cref="Result{T}"/> rather than a best-effort handle. The old contract caught every
    /// begin failure and returned an empty handle, so a deleted Campaign, a missing Session, or a
    /// binding mismatch all produced an ordinary-looking turn that simply persisted nothing — and the
    /// operator saw a normal answer to a conversation that no longer existed (§10.12).
    /// </remarks>
    public Task<Result<TurnHandle>> BeginBufferedAssistantReplyAsync(
        PingRequest request,
        ArcanumInvocationContext invocationContext,
        string prompt,
        string targetModel,
        CancellationToken cancellationToken) =>
        BeginAssistantReplyCoreAsync(
            request,
            invocationContext,
            prompt,
            targetModel,
            cancellationToken,
            "Grimoire could not begin assistant reply for model {ModelName}.");

    /// <inheritdoc cref="BeginBufferedAssistantReplyAsync"/>
    public Task<Result<TurnHandle>> BeginStreamedAssistantReplyAsync(
        PingRequest request,
        ArcanumInvocationContext invocationContext,
        string prompt,
        string targetModel,
        CancellationToken cancellationToken) =>
        BeginAssistantReplyCoreAsync(
            request,
            invocationContext,
            prompt,
            targetModel,
            cancellationToken,
            "Grimoire could not start streamed session persistence for model {ModelName}.");

    /// <summary>
    /// Persists the assistant entry, then publishes to the session event hub.
    /// Returns <see langword="false"/> when the database finalize fails (callers should emit failure).
    /// Hub publication failure after a successful DB write is warning-only.
    /// </summary>
    public async Task<bool> TryFinalizeBufferedAssistantEntryAsync(
        TurnHandle handle,
        string finalText,
        string targetModel,
        CancellationToken cancellationToken,
        ProviderCallSensitivity? sensitivity = null)
    {

        return await TryFinalizeAssistantEntryCoreAsync(
            handle,
            finalText,
            targetModel,
            cancellationToken,
            "Grimoire could not finalize assistant entry for model {ModelName}.",
            sensitivity).ConfigureAwait(false);

    }

    /// <inheritdoc cref="TryFinalizeBufferedAssistantEntryAsync"/>
    public async Task<bool> TryFinalizeStreamedAssistantEntryAsync(
        TurnHandle handle,
        string finalText,
        string targetModel,
        CancellationToken cancellationToken,
        ProviderCallSensitivity? sensitivity = null)
    {

        return await TryFinalizeAssistantEntryCoreAsync(
            handle,
            finalText,
            targetModel,
            cancellationToken,
            "Grimoire could not finalize streamed assistant entry for model {ModelName}.",
            sensitivity).ConfigureAwait(false);

    }

    public async Task<bool> ResolveInterruptedAsync(
        TurnHandle handle,
        string? streamedContent,
        CancellationToken cancellationToken,
        ProviderCallSensitivity? sensitivity = null)
    {

        if (handle.AssistantEntryId is not { } entryId)
        {

            return true;

        }

        try
        {

            if (!string.IsNullOrEmpty(streamedContent))
            {

                // An interrupted protected stream is the case most likely to lose its label: the reply
                // is partial, the turn is unwinding, and the obvious thing to do is persist what
                // arrived. Persisting it unlabelled would launder exactly the taint the completed path
                // is careful to record, so the partial content takes the same committed arm.
                Result<bool> committed = await TryCommitProtectedAsync(
                        handle,
                        entryId,
                        streamedContent,
                        sensitivity,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (committed.IsFailure)
                {

                    logger.LogError(
                        "An interrupted Covenant-derived reply could not be committed with its label: {ErrorCode}.",
                        committed.Error.Code);

                    return false;

                }

                if (!committed.Value)
                {

                    await grimoire
                        .FinalizeAssistantEntryAsync(entryId, streamedContent, cancellationToken)
                        .ConfigureAwait(false);

                }

            }
            else
            {

                await grimoire
                    .DiscardAssistantEntryAsync(entryId, cancellationToken)
                    .ConfigureAwait(false);

            }

            return true;

        }
        catch (OperationCanceledException)
        {

            throw;

        }
        catch (Exception ex)
        {

            logger.LogWarning(
                ex,
                "Grimoire could not resolve interrupted assistant entry {AssistantEntryId}.",
                entryId);

            return false;

        }
    }

    public async Task<bool> ResolveInterruptedAndMarkFinalizedAsync(
        TurnHandle handle,
        string? streamedContent,
        CancellationToken cancellationToken,
        ProviderCallSensitivity? sensitivity = null)
    {

        if (handle.IsFinalized)
        {

            return true;

        }

        bool resolved = await ResolveInterruptedAsync(
            handle,
            streamedContent,
            cancellationToken,
            sensitivity).ConfigureAwait(false);

        if (resolved)
        {
            handle.IsFinalized = true;
        }

        return resolved;

    }

    public async Task TryResolveInterruptedOnStreamExitAsync(
        TurnHandle handle,
        string? streamedContent,
        ProviderCallSensitivity? sensitivity = null)
    {

        if (handle.IsFinalized || handle.AssistantEntryId is null)
        {

            return;

        }

        try
        {

            _ = await ResolveInterruptedAndMarkFinalizedAsync(
                handle,
                streamedContent,
                CancellationToken.None,
                sensitivity).ConfigureAwait(false);

        }
        catch (Exception ex)
        {

            logger.LogWarning(
                ex,
                "Grimoire could not resolve interrupted streamed assistant entry during cleanup.");

        }

    }

    public async Task TryAppendToolInteractionAsync(
        Guid? sessionId,
        string toolName,
        string arguments,
        string result,
        string modelUsed,
        CancellationToken cancellationToken)
    {

        if (!sessionId.HasValue)
        {

            return;

        }

        try
        {

            await grimoire
                .AppendToolInteractionAsync(
                    sessionId.Value,
                    toolName,
                    arguments,
                    result,
                    modelUsed,
                    cancellationToken)
                .ConfigureAwait(false);

            try
            {

                await PublishLatestSavedEntriesAsync(sessionId.Value, 2, cancellationToken).ConfigureAwait(false);

            }
            catch (OperationCanceledException)
            {

                throw;

            }
            catch (Exception ex)
            {

                logger.LogWarning(
                    ex,
                    "Session event hub could not publish tool interaction for tool {ToolName}.",
                    toolName);

            }

        }
        catch (OperationCanceledException)
        {

            throw;

        }
        catch (Exception ex)
        {

            logger.LogWarning(ex, "Grimoire could not append tool interaction for tool {ToolName}.", toolName);

        }

    }

    internal async ValueTask<ApplyPatchReceiptProbeResult>
        ProbeApplyPatchReceiptAsync(
            ApplyPatchReceiptProbe probe,
            CancellationToken cancellationToken)
    {

        MandatoryToolInteractionProbeResult result = await grimoire
            .ProbeMandatoryToolInteractionAsync(
                new MandatoryToolInteractionProbe(
                    probe.SessionId,
                    probe.Receipt,
                    probe.ToolCallId,
                    probe.ToolName,
                    probe.SerializedArguments,
                    probe.ModelUsed,
                    probe.CreatedAt),
                cancellationToken)
            .ConfigureAwait(false);

        if (result.Outcome
            == MandatoryToolInteractionProbeOutcome.Replayed)
        {
            try
            {
                await PublishSavedEntryByIdAsync(
                    probe.SessionId,
                    probe.Receipt.CallEntryId,
                    cancellationToken).ConfigureAwait(false);
                await PublishSavedEntryByIdAsync(
                    probe.SessionId,
                    probe.Receipt.ResultEntryId,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Session event hub could not publish recovered mandatory apply_patch receipt {ReceiptId}.",
                    probe.Receipt.Id);
            }
        }

        return new ApplyPatchReceiptProbeResult(
            result.Outcome switch
            {
                MandatoryToolInteractionProbeOutcome.NotFound =>
                    ApplyPatchReceiptProbeOutcome.NotFound,
                MandatoryToolInteractionProbeOutcome.Replayed =>
                    ApplyPatchReceiptProbeOutcome.Replayed,
                MandatoryToolInteractionProbeOutcome.Mismatched =>
                    ApplyPatchReceiptProbeOutcome.Mismatched,
                _ =>
                    ApplyPatchReceiptProbeOutcome.Unavailable,
            },
            result.Result);

    }

    internal async ValueTask<ApplyPatchReceiptPreflightResult>
        PreflightApplyPatchReceiptAsync(
            ApplyPatchReceiptPreflight preflight,
            CancellationToken cancellationToken)
    {

        MandatoryToolInteractionPreflightResult result = await grimoire
            .PreflightMandatoryToolInteractionAsync(
                new MandatoryToolInteraction(
                    preflight.SessionId,
                    preflight.Receipt,
                    preflight.ToolCallId,
                    preflight.ToolName,
                    preflight.SerializedArguments,
                    preflight.SerializedResult,
                    preflight.ModelUsed,
                    preflight.CreatedAt),
                cancellationToken)
            .ConfigureAwait(false);

        return new ApplyPatchReceiptPreflightResult(
            result.Outcome switch
            {
                MandatoryToolInteractionPreflightOutcome.Admitted =>
                    ApplyPatchReceiptPreflightOutcome.Admitted,
                MandatoryToolInteractionPreflightOutcome.Replayed =>
                    ApplyPatchReceiptPreflightOutcome.Replayed,
                MandatoryToolInteractionPreflightOutcome.Rejected =>
                    ApplyPatchReceiptPreflightOutcome.Rejected,
                MandatoryToolInteractionPreflightOutcome.Mismatched =>
                    ApplyPatchReceiptPreflightOutcome.Mismatched,
                _ =>
                    ApplyPatchReceiptPreflightOutcome.Unavailable,
            },
            result.Result);

    }

    internal async ValueTask<MandatoryToolInteractionAppendOutcome>
        PersistApplyPatchRecoveryReceiptAsync(
            ApplyPatchRecoveryReceipt receipt,
            CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(receipt);

        MandatoryToolInteraction interaction = new(
            receipt.SessionId,
            receipt.Receipt,
            receipt.ToolCallId,
            receipt.ToolName,
            receipt.SerializedArguments,
            receipt.SerializedResult,
            receipt.ModelUsed,
            receipt.CreatedAt);

        MandatoryToolInteractionAppendOutcome outcome;

        try
        {
            MandatoryToolInteractionAppendResult append = await grimoire
                .AppendMandatoryToolInteractionAsync(
                    interaction,
                    cancellationToken)
                .ConfigureAwait(false);

            outcome = append.Outcome;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Mandatory apply_patch recovery receipt {ReceiptId} could not be classified.",
                receipt.Receipt.Id);
            return MandatoryToolInteractionAppendOutcome.Ambiguous;
        }

        if (outcome is MandatoryToolInteractionAppendOutcome.NewlyCommitted
            or MandatoryToolInteractionAppendOutcome.RecoveredCommitted)
        {
            try
            {
                await TryPublishMandatoryToolInteractionAsync(
                    receipt.SessionId,
                    receipt.Receipt,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException cancellation)
            {
                cancellation.Data[
                    nameof(MandatoryToolInteractionAppendOutcome)] = outcome;
                throw;
            }
        }

        return outcome;

    }

    internal async ValueTask<ApplyPatchPendingReceiptHandoffResult>
        HandlePendingApplyPatchReceiptAsync(
            PendingApplyPatchReceipt pending,
            CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(pending);

        MandatoryToolInteraction interaction = new(
            pending.SessionId,
            pending.Receipt,
            pending.ToolCallId,
            pending.ToolName,
            pending.SerializedArguments,
            pending.SerializedResult,
            pending.ModelUsed,
            pending.CreatedAt);

        MandatoryToolInteractionAppendOutcome outcome;

        try
        {
            MandatoryToolInteractionAppendResult append = await grimoire
                .AppendMandatoryToolInteractionAsync(
                    interaction,
                    cancellationToken)
                .ConfigureAwait(false);

            outcome = append.Outcome;
        }
        catch (OperationCanceledException cancellation)
        {
            outcome = ReadCancellationOutcome(cancellation)
                ?? MandatoryToolInteractionAppendOutcome.Ambiguous;

            ApplyPatchPendingReceiptHandoffResult resolved =
                await ResolvePendingReceiptOutcomeAsync(
                    pending,
                    outcome).ConfigureAwait(false);

            AttachRecovery(cancellation, resolved);
            cancellation.Data[nameof(MandatoryToolInteractionAppendOutcome)] =
                outcome;

            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Mandatory apply_patch receipt {ReceiptId} could not be classified; relative recovery artifacts were retained: {RecoveryArtifactPaths}.",
                pending.Receipt.Id,
                FormatRecoveryPaths(pending.Recovery?.ArtifactPaths));

            outcome = MandatoryToolInteractionAppendOutcome.Ambiguous;
        }

        ApplyPatchPendingReceiptHandoffResult result =
            await ResolvePendingReceiptOutcomeAsync(
                pending,
                outcome).ConfigureAwait(false);

        if (outcome is MandatoryToolInteractionAppendOutcome.NewlyCommitted
            or MandatoryToolInteractionAppendOutcome.RecoveredCommitted)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                OperationCanceledException cancellation =
                    new(cancellationToken);
                cancellation.Data[nameof(MandatoryToolInteractionAppendOutcome)] =
                    outcome;
                AttachRecovery(cancellation, result);
                throw cancellation;
            }

            try
            {
                await TryPublishMandatoryToolInteractionAsync(
                    pending,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException cancellation)
            {
                cancellation.Data[nameof(MandatoryToolInteractionAppendOutcome)] =
                    outcome;
                AttachRecovery(cancellation, result);
                throw;
            }
        }

        return result;

    }

    private async ValueTask<ApplyPatchPendingReceiptHandoffResult>
        ResolvePendingReceiptOutcomeAsync(
            PendingApplyPatchReceipt pending,
            MandatoryToolInteractionAppendOutcome outcome)
    {

        if (outcome is MandatoryToolInteractionAppendOutcome.NewlyCommitted
            or MandatoryToolInteractionAppendOutcome.RecoveredCommitted)
        {
            WorkspaceArtifactCleanupResult cleanup =
                await pending.MarkIrreversibleAsync(
                    CancellationToken.None).ConfigureAwait(false);

            ArcanumMetrics.ApplyPatchArtifactCleanupTotal.Add(
                1,
                new KeyValuePair<string, object?>(
                    "outcome",
                    cleanup.Complete ? "complete" : "retained"));

            if (!cleanup.Complete)
            {
                logger.LogWarning(
                    "Committed apply_patch receipt {ReceiptId} retained {ArtifactCount} recovery artifacts after bounded cleanup: {RecoveryArtifactPaths}.",
                    pending.Receipt.Id,
                    cleanup.RetainedArtifactPaths.Count,
                    FormatRecoveryPaths(cleanup.RetainedArtifactPaths));
            }

            return new ApplyPatchPendingReceiptHandoffResult(
                outcome,
                cleanup,
                Rollback: null);
        }

        if (outcome == MandatoryToolInteractionAppendOutcome.Failed)
        {
            WorkspaceRollbackResult rollback =
                await pending.RollbackAsync(
                    CancellationToken.None).ConfigureAwait(false);

            if (!rollback.Complete)
            {
                logger.LogWarning(
                    "Failed apply_patch receipt {ReceiptId} could not fully roll back; operator recovery is required for relative paths {AffectedPaths} using artifacts {RecoveryArtifactPaths}.",
                    pending.Receipt.Id,
                    FormatRecoveryPaths(rollback.Recovery?.AffectedPaths),
                    FormatRecoveryPaths(rollback.Recovery?.ArtifactPaths));

                await pending.AbandonAsync().ConfigureAwait(false);
            }

            return new ApplyPatchPendingReceiptHandoffResult(
                outcome,
                Cleanup: null,
                rollback);
        }

        await pending.AbandonAsync().ConfigureAwait(false);

        logger.LogError(
            "Ambiguous apply_patch receipt {ReceiptId} retained applied workspace changes; operator recovery artifacts: {RecoveryArtifactPaths}.",
            pending.Receipt.Id,
            FormatRecoveryPaths(pending.Recovery?.ArtifactPaths));

        return new ApplyPatchPendingReceiptHandoffResult(
            MandatoryToolInteractionAppendOutcome.Ambiguous,
            Cleanup: null,
            Rollback: null);

    }

    private Task TryPublishMandatoryToolInteractionAsync(
        PendingApplyPatchReceipt pending,
        CancellationToken cancellationToken) =>
        TryPublishMandatoryToolInteractionAsync(
            pending.SessionId,
            pending.Receipt,
            cancellationToken);

    private async Task TryPublishMandatoryToolInteractionAsync(
        Guid sessionId,
        ToolInteractionReceipt receipt,
        CancellationToken cancellationToken)
    {

        try
        {
            await PublishSavedEntryByIdAsync(
                sessionId,
                receipt.CallEntryId,
                cancellationToken).ConfigureAwait(false);
            await PublishSavedEntryByIdAsync(
                sessionId,
                receipt.ResultEntryId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Session event hub could not publish mandatory apply_patch receipt {ReceiptId}.",
                receipt.Id);
        }

    }

    private static MandatoryToolInteractionAppendOutcome?
        ReadCancellationOutcome(OperationCanceledException cancellation) =>
        cancellation.Data[nameof(MandatoryToolInteractionAppendOutcome)]
            is MandatoryToolInteractionAppendOutcome outcome
            ? outcome
            : null;

    private static void AttachRecovery(
        OperationCanceledException cancellation,
        ApplyPatchPendingReceiptHandoffResult result)
    {

        WorkspaceCommitRecovery? recovery =
            result.Rollback?.Recovery;

        if (recovery is not null)
        {
            cancellation.Data[nameof(WorkspaceCommitRecovery)] = recovery;
        }

    }

    private static string FormatRecoveryPaths(
        IReadOnlyList<string>? paths)
    {
        string[] normalized =
            WorkspaceRelativePath.NormalizeDistinctOrdered(paths);

        return normalized.Length == 0
            ? "[none]"
            : string.Join(", ", normalized);
    }

    private async Task<Result<TurnHandle>> BeginAssistantReplyCoreAsync(
        PingRequest request,
        ArcanumInvocationContext invocationContext,
        string prompt,
        string targetModel,
        CancellationToken cancellationToken,
        string beginFailureLogMessage)
    {

        ArgumentNullException.ThrowIfNull(invocationContext);

        // A stateless turn has no durable Session by construction, so there is nothing to begin and
        // nothing to fail. It is the one legitimate handle-free success.
        if (IsStateless(request))
        {
            return Result<TurnHandle>.Success(new TurnHandle());
        }

        CanonicalCampaignContext campaign = invocationContext.Campaign ?? CanonicalCampaignContext.GlobalOnly;

        // A request naming a Session must use that Session or fail; only a request naming none may
        // create one, and it creates it bound to the Campaign the resolver already decided.
        Result<Guid> sessionId = request.SessionId is { } existing && existing != Guid.Empty
            ? Result<Guid>.Success(existing)
            : await turnBeginStore
                .CreateBoundSessionAsync(campaign, prompt, cancellationToken)
                .ConfigureAwait(false);

        if (sessionId.IsFailure)
        {

            logger.LogWarning(beginFailureLogMessage, targetModel);

            return sessionId.Error;

        }

        Result<AssistantReplyBeginReceipt> receipt = await turnBeginStore
            .BeginAssistantReplyAsync(sessionId.Value, campaign, prompt, targetModel, cancellationToken)
            .ConfigureAwait(false);

        if (receipt.IsFailure)
        {

            logger.LogWarning(beginFailureLogMessage, targetModel);

            return receipt.Error;

        }

        TurnHandle handle = new()
        {
            SessionId = receipt.Value.SessionId,
            AssistantEntryId = receipt.Value.AssistantEntryId,
        };

        try
        {

            await PublishLatestSavedEntriesAsync(receipt.Value.SessionId, 2, cancellationToken)
                .ConfigureAwait(false);

        }
        catch (OperationCanceledException)
        {

            throw;

        }
        catch (Exception ex)
        {

            // Publication is best-effort on purpose: the rows are already committed, and failing the
            // turn here would discard a durable answer over an event nobody is required to receive.
            logger.LogWarning(
                ex,
                "Session event hub could not publish begin-assistant entries for model {ModelName}.",
                targetModel);

        }

        return Result<TurnHandle>.Success(handle);

    }

    /// <summary>
    /// Commits a Covenant-derived reply and its sensitivity label in one transaction.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="false"/> — "not mine" — for the ordinary reply, which continues through the
    /// unchanged finalize path. Only a reply the turn already proved Covenant-derived comes here, and
    /// for that reply the label is not optional decoration: the next turn decides whether it owes a
    /// disclosure by reading exactly this row, so a response persisted without its label is a response
    /// that silently launders taint out of the Session.
    ///
    /// <para>Failure is a refusal, not a downgrade. Writing the content without the label would be the
    /// one outcome worse than losing the reply.</para>
    /// </remarks>
    private async Task<Result<bool>> TryCommitProtectedAsync(
        TurnHandle handle,
        Guid finalizeId,
        string finalText,
        ProviderCallSensitivity? sensitivity,
        CancellationToken cancellationToken)
    {

        if (turnCommitter is null
            || sensitivity is not { Level: ContentSensitivity.CovenantDerived }
            || handle.SessionId is not { } sessionId)
        {

            return Result<bool>.Success(false);

        }

        Result<TurnCommitReceipt> committed = await turnCommitter
            .CommitTurnAsync(
                new TurnCommitRequest(
                    finalizeId,
                    sessionId,
                    AssistantFinalizationOutcome.Committed,
                    finalText,
                    RequestIdentity(sessionId, finalizeId),
                    sensitivity.Level,
                    sensitivity.Provenance),
                cancellationToken)
            .ConfigureAwait(false);

        return committed.IsFailure
            ? Result<bool>.Failure(committed.Error)
            : Result<bool>.Success(true);

    }

    /// <summary>
    /// The content-free identity this finalization replays against.
    /// </summary>
    /// <remarks>
    /// Derived from the Session and the assistant placeholder rather than from the reply text, because
    /// the whole point of the one-shot guard is that a retry of the same turn resolves through the
    /// stored outcome instead of running a second turn — and a digest over the text would make every
    /// regenerated wording look like a different request.
    /// </remarks>
    private static CovenantDigest RequestIdentity(Guid sessionId, Guid assistantEntryId) =>
        new(System.Security.Cryptography.SHA256.HashData(
        [
            .. System.Text.Encoding.ASCII.GetBytes("Arcanum.Covenant.TurnCommitRequestIdentity.v1"),
            0x00,
            .. sessionId.ToByteArray(),
            .. assistantEntryId.ToByteArray(),
        ]));

    private async Task<bool> TryFinalizeAssistantEntryCoreAsync(
        TurnHandle handle,
        string finalText,
        string targetModel,
        CancellationToken cancellationToken,
        string finalizeFailureLogMessage,
        ProviderCallSensitivity? sensitivity = null)
    {

        if (handle.AssistantEntryId is not { } finalizeId)
        {

            return true;

        }

        try
        {

            Result<bool> committed = await TryCommitProtectedAsync(
                    handle,
                    finalizeId,
                    finalText,
                    sensitivity,
                    cancellationToken)
                .ConfigureAwait(false);

            if (committed.IsFailure)
            {

                logger.LogError(
                    "A Covenant-derived reply could not be committed with its label: {ErrorCode}.",
                    committed.Error.Code);

                return false;

            }

            if (!committed.Value)
            {

                await grimoire
                    .FinalizeAssistantEntryAsync(finalizeId, finalText, cancellationToken)
                    .ConfigureAwait(false);

            }

            // Persistence succeeded — mark immediately so callers treat the turn as saved even if
            // hub publication fails below.
            handle.IsFinalized = true;

            if (handle.SessionId is { } publishSessionId)
            {

                try
                {

                    await PublishSavedEntryByIdAsync(publishSessionId, finalizeId, cancellationToken)
                        .ConfigureAwait(false);

                }
                catch (OperationCanceledException)
                {

                    throw;

                }
                catch (Exception ex)
                {

                    logger.LogWarning(
                        ex,
                        "Session event hub could not publish finalized assistant entry {AssistantEntryId} for model {ModelName}.",
                        finalizeId,
                        targetModel);

                }

            }

            return true;

        }
        catch (OperationCanceledException)
        {

            throw;

        }
        catch (Exception ex)
        {

            logger.LogWarning(ex, finalizeFailureLogMessage, targetModel);

            try
            {

                await grimoire
                    .DiscardAssistantEntryAsync(finalizeId, CancellationToken.None)
                    .ConfigureAwait(false);

            }
            catch (Exception cleanupEx)
            {

                logger.LogWarning(
                    cleanupEx,
                    "Grimoire could not resolve interrupted assistant entry {AssistantEntryId} after finalize failure.",
                    finalizeId);

            }

            handle.IsFinalized = true;

            return false;

        }

    }

    private async Task PublishLatestSavedEntriesAsync(Guid sessionId, int takeLast, CancellationToken cancellationToken)
    {

        List<GrimoireEntryDto>? entries = await grimoire
            .GetRecentSessionEntriesAsync(sessionId, takeLast, cancellationToken)
            .ConfigureAwait(false);

        if (entries is null || entries.Count == 0)
        {

            return;

        }

        foreach (GrimoireEntryDto dto in entries)
        {

            sessionEventHub.Publish(sessionId, ToEntry(dto, sessionId));

        }

    }

    private async Task PublishSavedEntryByIdAsync(Guid sessionId, Guid entryId, CancellationToken cancellationToken)
    {

        GrimoireEntryDto? dto = await grimoire
            .GetEntryByIdAsync(sessionId, entryId, cancellationToken)
            .ConfigureAwait(false);

        if (dto is null)
        {

            return;

        }

        sessionEventHub.Publish(sessionId, ToEntry(dto, sessionId));

    }

    private static Entry ToEntry(GrimoireEntryDto dto, Guid sessionId) =>
        new()
        {

            Id = dto.Id,

            SessionId = sessionId,

            Role = dto.Role,

            Content = dto.Content,

            ModelUsed = dto.ModelUsed,

            CreatedAt = dto.CreatedAt,

        };

    private static bool IsStateless(PingRequest request) =>
        request.StatelessMessages is { Count: > 0 };

}
