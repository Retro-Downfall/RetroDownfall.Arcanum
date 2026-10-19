using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Infrastructure.Data.MemoryEvaluation;

namespace RetroDownfall.Arcanum.Api.ProvingGrounds.MemoryEvaluation;

internal sealed partial class MemoryEvaluationRunner(
    IMemoryEvaluationSourceReader sources, IMemoryEvaluationEvidenceStore evidence, IMemoryEvaluationProvider provider,
    ILongRunningOperationStore operations, ILongRunningOperationCoordinator coordinator, ICovenantDisclosureJournal disclosures,
    IServiceScopeFactory scopes, MemoryEvaluationExecutionSlot executionSlot, LongRunningOperationOwnership ownership,
    TimeProvider clock, IHostApplicationLifetime host)
{
    private static readonly Error Gone = new(ErrorCodes.MemoryEvaluation.Gone, "The retained evaluation evidence has been erased or is absent.");

    private static readonly Error Missing = new(ErrorCodes.MemoryEvaluation.NotFound, "No evaluation identity or retained evidence was found.");

    private static readonly Error Conflict = new(ErrorCodes.Security.IdempotencyConflict, "This evaluation identity already names a different request.");

    private static readonly Error Busy = new(ErrorCodes.MemoryEvaluation.Busy, "An evaluation already holds the installation execution slot.");

    public async Task<Result<MemoryEvaluationResponse>> RunAsync(MemoryEvaluationRequest request, MemoryEvaluationRequestScope scope, CancellationToken ct)
    {
        var validated = MemoryEvaluationValidator.Validate(request);

        if (validated.IsFailure) return validated.Error;

        request = validated.Value.Request;

        var authorized = await scope.RevalidateAsync(ct).ConfigureAwait(false);

        if (authorized.IsFailure) return authorized.Error;

        if (scope.Invocation.Campaign?.CampaignId != request.Scope.CampaignId) return ScopeError();

        CovenantDigest requestDigest = MemoryEvaluationDigests.Request(validated.Value);

        var previous = await operations.FindByRequestedOperationIdAsync(request.EvaluationId, ct).ConfigureAwait(false);

        if (previous is not null) return await ReplayAsync(previous, requestDigest, scope, ct).ConfigureAwait(false);

        MemoryEvaluationExecutionSlot.Reservation? reservation = executionSlot.TryAcquire(request.EvaluationId);

        if (reservation is null) return Busy;

        var initialization = reservation;

        Guid operationId = Guid.Empty;

        Guid claim = Guid.Empty;

        MemoryEvaluationOperationLifetime? lifetime = null;

        try
        {
            previous = await operations.FindByRequestedOperationIdAsync(request.EvaluationId, ct).ConfigureAwait(false);

            if (previous is not null)
            {
                initialization.CompleteInitialization();

                return await ReplayAsync(previous, requestDigest, scope, ct).ConfigureAwait(false);
            }

            foreach (var state in new[] { LongRunningOperationState.Pending, LongRunningOperationState.Running, LongRunningOperationState.Waiting, LongRunningOperationState.Cancelling })
                if ((await operations.ListAsync(new(LongRunningOperationKinds.MemoryEvaluation, state, 1), ct).ConfigureAwait(false)).Count != 0) return Busy;

            using var preparation = CancellationTokenSource.CreateLinkedTokenSource(ct, scope.RequestAborted, scope.Revocation, host.ApplicationStopping);
            CancellationToken prepareToken = preparation.Token;

            var read = await sources.ReadAsync(validated.Value, scope.Invocation, scope.Lease, prepareToken).ConfigureAwait(false);

            if (read.IsFailure) return read.Error;

            var layout = MemoryEvaluationPromptComposer.Compose(validated.Value, read.Value);

            if (layout.IsFailure) return layout.Error;

            var prompts = MemoryEvaluationPromptPolicy.Derive(validated.Value, read.Value, layout.Value);

            if (prompts.IsFailure) return prompts.Error;

            var prepared = await provider.PrepareAsync(validated.Value, read.Value, prompts.Value, prepareToken).ConfigureAwait(false);

            if (prepared.IsFailure) return prepared.Error;

            MemoryEvaluationExecutionPlan plan = prepared.Value;

            CovenantDigest planDigest = MemoryEvaluationDigests.ExecutionPlan(plan);

            string owner = "memory-evaluation:" + Guid.NewGuid().ToString("N");

            var start = await coordinator.StartWithRequestIdentityAsync(new(LongRunningOperationKinds.MemoryEvaluation, LongRunningOperationRecoveryPolicy.AbandonSafely,
                "Memory evaluation", clock.GetUtcNow()), new(request.EvaluationId, requestDigest, planDigest), owner, MemoryEvaluationOperationLifetime.LeaseDuration, prepareToken).ConfigureAwait(false);

            if (start.IsFailure) return start.Error;

            if (start.Value.Outcome != LongRunningOperationRequestIdentityOutcome.Created)
            {
                initialization.CompleteInitialization();

                previous = await operations.FindByRequestedOperationIdAsync(request.EvaluationId, prepareToken).ConfigureAwait(false);

                return previous is null ? Gone : await ReplayAsync(previous, requestDigest, scope, prepareToken).ConfigureAwait(false);
            }

            LongRunningOperation? operation = start.Value.Operation;

            if (!Owned(operation, owner)) return Busy;

            operationId = operation!.Id;

            if (!ownership.TryClaim(operationId, out claim)) return Busy;

            lifetime = new(scopes, operationId, owner, clock, reservation, preparation.Token, host.ApplicationStopping, scope.Revocation);

            reservation = null;

            return await ExecuteAsync(plan, Registration(plan, operationId, requestDigest, planDigest), scope, owner, lifetime, initialization, prepareToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        { return new Error(ErrorCodes.MemoryEvaluation.Cancelled, "The evaluation request was cancelled."); }
        catch (Exception)
        { return new Error(ErrorCodes.MemoryEvaluation.Interrupted, "The evaluation could not finish; retained metadata can be reconciled safely."); }
        finally
        {
            initialization.CompleteInitialization();

            if (lifetime is not null) await lifetime.DisposeAsync().ConfigureAwait(false);

            if (claim != Guid.Empty) ownership.Release(operationId, claim);

            reservation?.Dispose();
        }
    }

    public async Task<Result<MemoryEvaluationResponse>> GetAsync(MemoryEvaluationGetRequest request, MemoryEvaluationRequestScope scope, CancellationToken ct)
    {
        if (request.EvaluationId == Guid.Empty) return Missing;

        var valid = await scope.RevalidateAsync(ct).ConfigureAwait(false);

        if (valid.IsFailure) return valid.Error;

        var retained = await ReadOwnedAsync(request.EvaluationId, scope, ct).ConfigureAwait(false);

        if (retained.IsFailure) return retained.Error;

        return Response(retained.Value);
    }

    public async Task<Result<MemoryEvaluationDeleteResult>> DeleteAsync(MemoryEvaluationDeleteRequest request, MemoryEvaluationRequestScope scope, CancellationToken ct)
    {
        var valid = await scope.RevalidateAsync(ct).ConfigureAwait(false);

        if (valid.IsFailure) return valid.Error;

        var retained = await ReadOwnedAsync(request.EvaluationId, scope, ct).ConfigureAwait(false);

        if (retained.IsFailure && !(scope.Invocation.Campaign?.CampaignId is null
            && retained.Error.Code == ErrorCodes.MemoryEvaluation.Gone)) return retained.Error;

        // Erasure removes the old scope along with the root. A genuine current Global scope
        // may repeat cleanup for a retained exact evaluation identity; never invent old ownership.

        valid = await scope.RevalidateAsync(ct).ConfigureAwait(false);

        return valid.IsFailure ? valid.Error : await evidence.DeleteAsync(request, ct).ConfigureAwait(false);
    }

    private bool Owned(LongRunningOperation? operation, string owner) => operation is not null
        && operation.Kind == LongRunningOperationKinds.MemoryEvaluation && operation.RecoveryPolicy == LongRunningOperationRecoveryPolicy.AbandonSafely
        && operation.State == LongRunningOperationState.Running && operation.LeaseOwner == owner
        && operation.LeaseExpiresAt > clock.GetUtcNow();

    private async Task<Result<MemoryEvaluationResponse>> ReplayAsync(LongRunningOperationRequestIdentityMatch match, CovenantDigest digest, MemoryEvaluationRequestScope scope, CancellationToken ct)
    {
        if (match.Identity.ApplyRequestDigest != digest || match.Operation.Kind != LongRunningOperationKinds.MemoryEvaluation) return Conflict;

        var retained = await ReadOwnedAsync(match.Identity.RequestedOperationId, scope, ct).ConfigureAwait(false);

        if (retained.IsFailure) return retained.Error;

        if (retained.Value.Registration.OperationId != match.Operation.Id || retained.Value.Registration.PlanDigest != match.Identity.EffectDigest) return Conflict;

        return Response(retained.Value);
    }

    internal async Task<Result<MemoryEvaluationOwnership>> ReadOwnershipAsync(Guid id, CancellationToken ct)
    {
        await executionSlot.WaitForInitializationAsync(id, ct).ConfigureAwait(false);

        var owner = await evidence.ReadOwnershipAsync(id, ct).ConfigureAwait(false);

        if (owner.IsSuccess || owner.Error.Code != ErrorCodes.MemoryEvaluation.NotFound) return owner;

        var identity = await operations.FindByRequestedOperationIdAsync(id, ct).ConfigureAwait(false);

        // This is a coherent pre-creation observation. An endpoint must retain it through
        // authority validation, rather than mixing it with a subsequently created scope.
        if (identity?.Operation.Kind != LongRunningOperationKinds.MemoryEvaluation) return Missing;

        // The durable identity cannot precede its keyed initialization slot. If creation
        // crossed the first absent read, settle publication and read ownership again once.
        await executionSlot.WaitForInitializationAsync(id, ct).ConfigureAwait(false);

        owner = await evidence.ReadOwnershipAsync(id, ct).ConfigureAwait(false);

        return owner.IsFailure && owner.Error.Code == ErrorCodes.MemoryEvaluation.NotFound ? Gone : owner;
    }

    private async Task<Result<MemoryEvaluationStoredEvidence>> ReadOwnedAsync(Guid id, MemoryEvaluationRequestScope scope, CancellationToken ct)
    {
        var owner = await ReadOwnershipAsync(id, ct).ConfigureAwait(false);

        var valid = await scope.RevalidateAsync(ct).ConfigureAwait(false);

        if (valid.IsFailure) return valid.Error;

        if (owner.IsFailure) return owner.Error;

        if (owner.Value.CampaignId != scope.Invocation.Campaign?.CampaignId || owner.Value.DatasetGeneration != scope.Lease.Snapshot.DatasetGeneration) return ScopeError();

        var read = await evidence.GetAsync(id, ct).ConfigureAwait(false);

        if (read.IsFailure)
        {
            if (read.Error.Code != ErrorCodes.MemoryEvaluation.NotFound) return read.Error;

            // Ownership was present in this observation; disappearance now is erasure,
            // not an initial-publication gap. Retained exact identity distinguishes 410/404.
            var identity = await operations.FindByRequestedOperationIdAsync(id, ct).ConfigureAwait(false);

            valid = await scope.RevalidateAsync(ct).ConfigureAwait(false);

            return valid.IsFailure ? valid.Error : identity?.Operation.Kind == LongRunningOperationKinds.MemoryEvaluation ? Gone : Missing;
        }

        valid = await scope.RevalidateAsync(ct).ConfigureAwait(false);

        return valid.IsFailure ? valid.Error : read.Value;
    }

    private static MemoryEvaluationResponse Response(MemoryEvaluationStoredEvidence retained) => new(retained.Registration.EvaluationId,
        retained.Registration.OperationId, retained.Report?.State ?? MemoryEvaluationState.Running,
        retained.Report?.Decision.Reason ?? MemoryEvaluationReason.None, retained.Report, retained.ReportDigest);

    private static Error ScopeError() => new(ErrorCodes.MemoryEvaluation.ScopeMismatch, "Evaluation ownership does not match the admitted scope and dataset.");
}
