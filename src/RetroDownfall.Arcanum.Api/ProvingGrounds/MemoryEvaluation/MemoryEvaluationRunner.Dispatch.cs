using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;

namespace RetroDownfall.Arcanum.Api.ProvingGrounds.MemoryEvaluation;

internal sealed partial class MemoryEvaluationRunner
{
    private async Task<Result<MemoryEvaluationResponse>> ExecuteAsync(MemoryEvaluationExecutionPlan plan, MemoryEvaluationRegistration registration,
        MemoryEvaluationRequestScope scope, string owner, MemoryEvaluationOperationLifetime lifetime, MemoryEvaluationExecutionSlot.Reservation initialization, CancellationToken requestCancellation)
    {
        var progress = new Progress(plan.Request.Request.Checks, registration.PlanDigest);

        long began = clock.GetTimestamp();

        int checkpointVersion = 0;

        bool registered = false;

        bool published = false;

        MemoryEvaluationReason failure = MemoryEvaluationReason.None;

        CancellationToken ct = lifetime.Token;

        try
        {
            await CheckLiveAsync(plan, scope, registration.OperationId, owner, false, ct).ConfigureAwait(false);

            var committed = await evidence.RegisterAsync(registration, plan.Sources.Coordinates, ct).ConfigureAwait(false);

            if (committed.IsFailure)
            {
                await SettleLedgerAsync(registration.OperationId, owner, ErrorCodes.MemoryEvaluation.NotPrepared).ConfigureAwait(false);

                return committed.Error;
            }

            registered = true;

            await SaveAsync(MemoryEvaluationPhase.Prepared).ConfigureAwait(false);

            initialization.CompleteInitialization();

            MemoryEvaluationArmResult? control = null;

            MemoryEvaluationArmResult? treatment = null;

            for (int sendIndex = 0; sendIndex < plan.SendOrder.Length; sendIndex++)
            {
                int index = sendIndex;

                int pairIndex = index / 2;

                MemoryEvaluationArm arm = plan.SendOrder[index];

                await SaveAsync(MemoryEvaluationPhase.Sending).ConfigureAwait(false);

                await CheckLiveAsync(plan, scope, registration.OperationId, owner, true, ct).ConfigureAwait(false);

                Task<Result<MemoryEvaluationArmResult>> send = provider.SendAsync(plan, pairIndex, arm, UnrestrictedTurnBudget.Instance,
                    async (sendEvidence, cancellation) =>
                    {
                        try
                        {
                            CovenantDigest expectedSensitivity = arm == MemoryEvaluationArm.Control
                                ? CovenantDigests.Sensitivity(GenerationProvenance.CreateExact([]).ToDigestInput(ContentSensitivity.None))
                                : plan.SensitivityDigest;

                            if (sendEvidence.SendIndex != index || sendEvidence.PairIndex != pairIndex || sendEvidence.Arm != arm
                                || sendEvidence.DestinationDigest != plan.DestinationDigest || sendEvidence.SensitivityDigest != expectedSensitivity)
                                throw new EvaluationStopped(MemoryEvaluationReason.InvalidEvaluation);

                            await CheckLiveAsync(plan, scope, registration.OperationId, owner, true, cancellation).ConfigureAwait(false);

                            var effect = CovenantDigests.ProviderDispatchEffect(new(registration.OperationId, (ulong)index + 1, registration.PlanDigest,
                                sendEvidence.Envelope.Digest, sendEvidence.DestinationDigest));

                            var draft = new CovenantDisclosureDraft(scope.InstallationId, CovenantDisclosureSubjectKind.Operation, registration.OperationId, effect,
                                CovenantEgressDestination.Provider, CovenantDisclosureRevocability.Nonrevocable, sendEvidence.DestinationDigest, sendEvidence.SensitivityDigest,
                                null, registration.PlanDigest, null, clock.GetUtcNow().ToUnixTimeMilliseconds());

                            var acknowledged = await disclosures.AcknowledgeAsync(draft, CovenantDisclosureEffectCategory.ProviderDispatch, sendEvidence.Envelope.Sensitivity, cancellation).ConfigureAwait(false);

                            if (acknowledged.IsFailure || acknowledged.Value.Draft != draft) throw new EvaluationStopped(MemoryEvaluationReason.InvalidEvaluation);

                            await CheckLiveAsync(plan, scope, registration.OperationId, owner, true, cancellation).ConfigureAwait(false);
                        }
                        catch (EvaluationStopped stopped) { failure = stopped.Reason; throw; }
                    }, ct);

                lifetime.TrackProvider(send);

                var sent = await send.ConfigureAwait(false);

                ct.ThrowIfCancellationRequested();

                if (sent.IsFailure) throw new EvaluationStopped(failure == MemoryEvaluationReason.None ? MemoryEvaluationReason.ProviderFailure : failure);

                progress.AddSend(sent.Value);

                if (!sent.Value.IsValid || !sent.Value.ModelIdentityVerified || !sent.Value.ProviderCompleted || !sent.Value.UsageKnown || sent.Value.Answer is null)
                    throw new EvaluationStopped(sent.Value.Reason == MemoryEvaluationReason.None ? MemoryEvaluationReason.InvalidOutput : sent.Value.Reason);

                var verdicts = MemoryEvaluationDeterministicEvaluator.Evaluate(sent.Value.Answer, plan.Request.Request.Checks, ct);

                if (verdicts.IsFailure) throw new EvaluationStopped(MemoryEvaluationReason.InvalidEvaluation);

                var armResult = sent.Value with { Answer = null, Verdicts = verdicts.Value };

                if (arm == MemoryEvaluationArm.Control) control = armResult; else treatment = armResult;

                if ((index & 1) == 1)
                {
                    if (control is null || treatment is null) throw new EvaluationStopped(MemoryEvaluationReason.InvalidEvaluation);

                    progress.AddPair(control, treatment);

                    control = null;

                    treatment = null;
                }

                await SaveAsync(MemoryEvaluationPhase.Evaluating).ConfigureAwait(false);
            }

            await SaveAsync(MemoryEvaluationPhase.Finalizing).ConfigureAwait(false);

            await CheckLiveAsync(plan, scope, registration.OperationId, owner, true, ct).ConfigureAwait(false);

            var aggregate = progress.Aggregate();

            var report = new MemoryEvaluationTerminalReport(registration, clock.GetUtcNow(), MemoryEvaluationState.Completed,
                MemoryEvaluationPolicy.Classify(aggregate, registration.PairCount), aggregate, plan.Sources.Coordinates,
                progress.InputTokens, progress.OutputTokens, progress.Cost, Elapsed(), registration.Dependencies);

            var completed = await evidence.CompleteAsync(registration.EvaluationId, report, ct).ConfigureAwait(false);

            if (completed.IsFailure) return completed.Error.Code == ErrorCodes.MemoryEvaluation.NotFound ? Gone : completed.Error;

            published = true;
        }
        catch (OperationCanceledException) { failure = ct.IsCancellationRequested ? lifetime.CancellationReason : MemoryEvaluationReason.Interrupted; }
        catch (EvaluationStopped stopped) { failure = stopped.Reason; }
        catch (Exception) { failure = MemoryEvaluationReason.Interrupted; }
        finally { initialization.CompleteInitialization(); }

        if (published)
            return await ReadPublishedAsync(registration, owner, null, scope, requestCancellation).ConfigureAwait(false);

        failure = failure == MemoryEvaluationReason.None ? MemoryEvaluationReason.Interrupted : failure;

        if (registered)
        {
            // No plaintext or execution authority is needed to record incomplete evidence. Unknown
            // physical-send progress never becomes a claimed zero or a partial full-cost aggregate.
            MemoryEvaluationReason reason = failure;

            MemoryEvaluationState terminalState = reason switch
            {
                MemoryEvaluationReason.Cancelled => MemoryEvaluationState.Cancelled,
                MemoryEvaluationReason.Interrupted => MemoryEvaluationState.Interrupted,
                _ => MemoryEvaluationState.Completed
            };

            var aggregate = progress.Aggregate() with { FailureReason = reason };

            var report = new MemoryEvaluationTerminalReport(registration, clock.GetUtcNow(), terminalState,
                MemoryEvaluationPolicy.Classify(aggregate, registration.PairCount), aggregate, plan.Sources.Coordinates, null, null, null, Elapsed(), registration.Dependencies);

            var recorded = await evidence.CompleteAsync(registration.EvaluationId, report, CancellationToken.None).ConfigureAwait(false);

            if (recorded.IsSuccess)
                return await ReadPublishedAsync(registration, owner, FailureCode(failure), scope, requestCancellation).ConfigureAwait(false);

            await SettleLedgerAsync(registration.OperationId, owner, FailureCode(failure)).ConfigureAwait(false);

            return recorded.Error.Code == ErrorCodes.MemoryEvaluation.NotFound ? Gone : recorded.Error;
        }

        await SettleLedgerAsync(registration.OperationId, owner, ErrorCodes.MemoryEvaluation.NotPrepared).ConfigureAwait(false);

        return new Error(FailureCode(failure), "The evaluation ended before preparation committed.");

        long Elapsed() => Math.Max(0, (long)clock.GetElapsedTime(began).TotalMilliseconds);

        async Task SaveAsync(MemoryEvaluationPhase phase)
        {
            var checkpoint = new MemoryEvaluationCheckpoint(registration.EvaluationId, registration.OperationId, registration.RequestDigest, registration.PlanDigest,
                phase, registration.PairCount, progress.PhysicalSends, progress.Aggregate(), null, null, null, Elapsed());

            if (!await coordinator.CheckpointAsync(registration.OperationId, owner, checkpointVersion, 1, MemoryEvaluationCheckpointCodec.Encode(checkpoint), null,
                "Memory evaluation", ct).ConfigureAwait(false)) throw new EvaluationStopped(MemoryEvaluationReason.Interrupted);

            checkpointVersion = 1;
        }
    }

    private async Task<Result<MemoryEvaluationResponse>> ReadPublishedAsync(MemoryEvaluationRegistration registration, string owner,
        string? terminalError, MemoryEvaluationRequestScope scope, CancellationToken requestCancellation)
    {
        // A successful immutable report commit is authoritative. Ledger settlement can be
        // reconciled later; neither its failure nor the observer seeing our own completion
        // may enter the incomplete-report writer again.
        try
        {
            await SettleLedgerAsync(registration.OperationId, owner, terminalError).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Metadata-only recovery preserves the committed report and settles the ledger.
        }

        // Re-read retention and original request authority. The dispatch observer may have
        // cancelled after self-completion, but genuine request/revocation/shutdown still applies.
        return await GetAsync(new(registration.EvaluationId), scope, requestCancellation).ConfigureAwait(false);
    }

    private async Task CheckLiveAsync(MemoryEvaluationExecutionPlan plan, MemoryEvaluationRequestScope scope, Guid operationId, string owner, bool registered, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if ((await scope.RevalidateAsync(ct).ConfigureAwait(false)).IsFailure) throw new EvaluationStopped(MemoryEvaluationReason.StaleAuthority);

        if ((await sources.ValidateAsync(plan.Sources, scope.Lease, ct).ConfigureAwait(false)).IsFailure) throw new EvaluationStopped(MemoryEvaluationReason.StaleSource);

        if ((await provider.ValidateAsync(plan, ct).ConfigureAwait(false)).IsFailure) throw new EvaluationStopped(MemoryEvaluationReason.StaleConfiguration);

        var state = await operations.GetAsync(operationId, ct).ConfigureAwait(false);

        if (!Owned(state, owner)) throw new EvaluationStopped(state?.State == LongRunningOperationState.Cancelling ? MemoryEvaluationReason.Cancelled : MemoryEvaluationReason.Interrupted);

        if (registered)
        {
            var retained = await evidence.GetAsync(plan.Request.Request.EvaluationId, ct).ConfigureAwait(false);

            if (retained.IsFailure) throw new EvaluationStopped(MemoryEvaluationReason.Erased);

            if (retained.Value.Report is not null || retained.Value.Registration.OperationId != operationId
                || retained.Value.Registration.PlanDigest != MemoryEvaluationDigests.ExecutionPlan(plan)) throw new EvaluationStopped(MemoryEvaluationReason.InvalidEvaluation);
        }

        if ((await scope.RevalidateAsync(ct).ConfigureAwait(false)).IsFailure) throw new EvaluationStopped(MemoryEvaluationReason.StaleAuthority);

        ct.ThrowIfCancellationRequested();
    }

    internal static string FailureCode(MemoryEvaluationReason reason) => reason switch
    {
        MemoryEvaluationReason.None => ErrorCodes.MemoryEvaluation.Interrupted,
        MemoryEvaluationReason.MixedOutcomes => ErrorCodes.MemoryEvaluation.MixedOutcomes,
        MemoryEvaluationReason.NotPrepared => ErrorCodes.MemoryEvaluation.NotPrepared,
        MemoryEvaluationReason.Interrupted => ErrorCodes.MemoryEvaluation.Interrupted,
        MemoryEvaluationReason.Cancelled => ErrorCodes.MemoryEvaluation.Cancelled,
        MemoryEvaluationReason.StaleAuthority => ErrorCodes.MemoryEvaluation.StaleAuthority,
        MemoryEvaluationReason.StaleSource => ErrorCodes.MemoryEvaluation.StaleSource,
        MemoryEvaluationReason.StaleConfiguration => ErrorCodes.MemoryEvaluation.StaleConfiguration,
        MemoryEvaluationReason.InvalidEvaluation => ErrorCodes.MemoryEvaluation.InvalidEvaluation,
        MemoryEvaluationReason.InvalidOutput => ErrorCodes.MemoryEvaluation.InvalidOutput,
        MemoryEvaluationReason.ModelMismatch => ErrorCodes.MemoryEvaluation.ModelMismatch,
        MemoryEvaluationReason.ProviderFailure => ErrorCodes.MemoryEvaluation.ProviderFailure,
        MemoryEvaluationReason.UsageFailure => ErrorCodes.MemoryEvaluation.UsageFailure,
        MemoryEvaluationReason.AccountingFailure => ErrorCodes.MemoryEvaluation.AccountingFailure,
        MemoryEvaluationReason.Erased => ErrorCodes.MemoryEvaluation.Erased,
        MemoryEvaluationReason.Incomplete => ErrorCodes.MemoryEvaluation.Incomplete,
        _ => ErrorCodes.MemoryEvaluation.InvalidEvaluation
    };

    private async Task SettleLedgerAsync(Guid operationId, string owner, string? error)
    {
        for (int retry = 0; retry < 3; retry++)
        {
            var current = await operations.GetAsync(operationId, CancellationToken.None).ConfigureAwait(false);

            if (current is null || current.LeaseOwner != owner || current.State is not (LongRunningOperationState.Running or LongRunningOperationState.Waiting or LongRunningOperationState.Cancelling)) return;

            bool settled = error is null
                ? await coordinator.CompleteAsync(operationId, owner, current.Revision, CancellationToken.None).ConfigureAwait(false)
                : await coordinator.FailAsync(operationId, owner, current.Revision, error, CancellationToken.None).ConfigureAwait(false);

            if (settled) return;
        }
    }

    private sealed class EvaluationStopped(MemoryEvaluationReason reason) : Exception("Evaluation authority or evidence is no longer valid.")
    {
        internal MemoryEvaluationReason Reason { get; } = reason;
    }
}
