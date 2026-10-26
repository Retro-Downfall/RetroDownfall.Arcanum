using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

namespace RetroDownfall.Arcanum.Api.Intelligence;

internal sealed partial class LongRestMaintenance
{
    /// <param name="used">
    /// Every leaf the request is built from, however it reaches the request: an adjudication's inputs, or everything under a fold's children. Each is
    /// re-proved in full at every gate that stands between it and the send, so no gate is left to take a leaf on trust because a request lists few.
    /// </param>
    private async Task<T> ExecutePaidAsync<T>(LongRestDiscoveryGeneration generation, LongRestProviderPayload payload,
        GenerationProvenance provenance, LongRestMaintenanceCapability? capability, string workerOwner, IGrimoireWorkLease admission,
        IReadOnlyCollection<string> used, Func<PaidResult, LongRestDispatchCompletion, CancellationToken, bool, Task<T>> accept, CancellationToken ct)
    {
        // A paid unit derives from at least one leaf. One that names none would have every gate below prove nothing about its request.
        if (used.Count == 0) throw new Refused(Stale());

        await EnsureCurrentAsync(generation, capability, workerOwner, LongRestVerificationDepth.Step, used, ct).ConfigureAwait(false);

        if (!admission.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? group) || group is null)
        {
            ct.ThrowIfCancellationRequested();

            (capability?.Revocation ?? CancellationToken.None).ThrowIfCancellationRequested();

            if (admission.MaintenanceRevocationPending)
            {
                throw new LongRestMaintenanceDeferredException(admission.MaintenanceRevocation);
            }

            throw new Refused(Stale());
        }

        await using IGrimoireExternalEffectGroup ownedEffect = group;

        LongRestConfigurationSnapshot snapshot = epoch.Capture();

        if (snapshot.Stamp != generation.Fence.Configuration)
        {
            throw new Refused(Stale());
        }

        LongRestProviderSelection selected = Require(payload.Kind == LongRestProviderPayloadKind.Embedding
            ? snapshot.ResolveEmbedding() : snapshot.ResolveChat());

        CovenantDigest payloadDigest = Require(LongRestProviderCallFreezer.PayloadDigest(payload, selected.Provider, selected.Model, selected.Dimensions));

        CovenantDigest destination = payload.Kind == LongRestProviderPayloadKind.Embedding
            ? generation.Recipe.EmbeddingDestinationDigest : generation.Recipe.SummaryDestinationDigest;

        LongRestProviderAdmission providerAdmission = payload.Kind == LongRestProviderPayloadKind.Embedding
            ? new LongRestProviderAdmission.Embedding(selected.Provider, selected.Model, selected.Dimensions,
                estimator.EstimateLongRestEmbedding(selected.Provider, selected.Model, payload).TokenCount, payloadDigest)
            : new LongRestProviderAdmission.Chat(new(selected.Provider, selected.Model, 2048, 0,
                estimator.EstimateLongRestChat(selected.Provider, selected.Model, payload)));

        // Admit-then-Prepare: the deterministic local refusals this layer can see (the rendered size above, the context window here) happen
        // before any provider-effect row or accounting run exists. A refusal only the provider layer can see (the daily budget, a credential)
        // comes after the attempt is prepared; the attempt is then withdrawn below, so none leaves a Prepared row for readmission to meet.
        Require(ModelCallExecutor.CheckLongRestWindow(providerAdmission));

        LongRestDispatchAttempt attempt = Require(await attempts.PrepareAttemptAsync(payload.Work, payload.Kind, payloadDigest, destination, ct).ConfigureAwait(false));

        using CancellationTokenSource dispatch = CancellationTokenSource.CreateLinkedTokenSource(ct, admission.MaintenanceRevocation,
            capability?.Revocation ?? CancellationToken.None);

        using CancellationTokenSource stop = new();

        int sent = 0;

        Task heartbeat = KeepClaimAliveAsync(generation, capability, workerOwner, dispatch, stop.Token);

        try
        {
            try
            {
                async Task BeforeSend(CancellationToken token)
                {
                    if (Interlocked.Exchange(ref sent, 1) != 0)
                    {
                        throw new Refused(Stale());
                    }

                    await EnsureCurrentAsync(generation, capability, workerOwner, LongRestVerificationDepth.Step, used, token).ConfigureAwait(false);

                    Require(await attempts.ValidateAttemptAsync(attempt, token).ConfigureAwait(false));

                    if (generation.Scope.ProtectedPartitionDigest is not null)
                    {
                        LongRestDispatchCapability child = Require(await capability!.CreateDispatchAsync(payload.Work, payloadDigest,
                            destination, attempt.PhysicalOrdinal, generation.Fence.Configuration, token).ConfigureAwait(false));

                        ProviderCallSensitivity sensitivity = new(ContentSensitivity.CovenantDerived, provenance,
                            CovenantDigests.Sensitivity(provenance.ToDigestInput(ContentSensitivity.CovenantDerived)));

                        SessionTurnClaimLease claim = capability.Claim;

                        CovenantDisclosureDraft draft = new(claim.Claim.OriginInstallationId, CovenantDisclosureSubjectKind.Turn,
                            claim.Claim.ClaimId, CovenantDigests.MaintenanceDispatchEffect(new(claim.Claim.ClaimId,
                                CovenantMaintenanceStep.LongRest, attempt.PhysicalOrdinal, payloadDigest, destination)),
                            CovenantEgressDestination.Provider, CovenantDisclosureRevocability.Nonrevocable, destination, sensitivity.Digest,
                            null, null, null, timeProvider.GetUtcNow().ToUnixTimeMilliseconds());

                        CovenantDisclosureReceipt receipt = Require(await disclosures.AcknowledgeAsync(draft,
                            CovenantDisclosureEffectCategory.MaintenanceAttempt, sensitivity, token).ConfigureAwait(false));

                        attempt = Require(await attempts.RecordDisclosureAsync(attempt, receipt.Digest, token).ConfigureAwait(false));

                        // Durable evidence never replaces the independently current source, claim and lease.
                        await EnsureCurrentAsync(generation, capability, workerOwner, LongRestVerificationDepth.Step, used, token).ConfigureAwait(false);

                        Require(await attempts.ValidateAttemptAsync(attempt, token).ConfigureAwait(false));

                        Require(await child.ConsumeAsync(payload.Work, payloadDigest, destination, attempt.PhysicalOrdinal,
                            generation.Fence.Configuration, token).ConfigureAwait(false));
                    }

                    attempt = Require(await attempts.MarkSentAsync(attempt, token).ConfigureAwait(false));

                    await EnsureCurrentAsync(generation, capability, workerOwner, LongRestVerificationDepth.Step, used, token).ConfigureAwait(false);

                    Require(await attempts.ValidateAttemptAsync(attempt, token).ConfigureAwait(false));
                }

                Result<LongRestProviderResult> result;

                try
                {
                    result = await provider.ExecuteAsync(payload, UnrestrictedTurnBudget.Instance,
                        BeforeSend, providerAdmission, dispatch.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException && attempt.State == LongRestDispatchAttemptState.Sent)
                {
                    // Unknown-Usage Settlement: the frame that owns a physical send closes it when the call ends without a result.
                    // Cancellation never settles; a cancelled Sent attempt stays Sent for the reboot settlement.
                    await SettleUnresolvedAsync(attempt, LongRestUnresolvedSend.UsageUnknown).ConfigureAwait(false);

                    throw;
                }
                catch (Exception) when (attempt.State == LongRestDispatchAttemptState.Prepared)
                {
                    // The call ended, even by cancellation, before the physical send was recorded, so nothing reached the provider.
                    await ReleaseUnsentAsync(attempt).ConfigureAwait(false);

                    throw;
                }

                if (result.IsFailure && attempt.State == LongRestDispatchAttemptState.Sent)
                {
                    // Only a failure raised before any request byte is known not to have reached the provider.
                    await SettleUnresolvedAsync(attempt, result.Error.Code == ErrorCodes.LongRest.ProviderUnreachable
                        ? LongRestUnresolvedSend.NotSent : LongRestUnresolvedSend.UsageUnknown).ConfigureAwait(false);
                }
                else if (result.IsFailure && attempt.State == LongRestDispatchAttemptState.Prepared)
                {
                    // A local refusal after the attempt was prepared (the daily budget, a credential, a stale configuration) ended the call
                    // before any send: the attempt is withdrawn, or a later same-boot clean readmission would meet it and refuse.
                    await ReleaseUnsentAsync(attempt).ConfigureAwait(false);
                }

                // A transport without a reported result is settled above, inside this still-held group, as unknown usage. When that
                // best-effort write is refused it remains Sent and the reboot settlement closes it. Normal billing alone cannot
                // reconstruct either usage or publication authority for this physical attempt.
                LongRestProviderResult output = Require(result);

                decimal cost = CostCalculator.CalculateCost(output.Usage.InputTokens, output.Usage.OutputTokens,
                    output.Usage.CachedInputTokens, output.Usage.ReasoningTokens, selected.Pricing);

                LongRestDispatchCompletion completion = new(LongRestDispatchAttemptState.Accepted, output.Usage, cost, null);

                LongRestDispatchAttempt accepted = attempt with { State = LongRestDispatchAttemptState.Accepted, Revision = checked(attempt.Revision + 1) };

                PaidResult proposed = new(output, new(accepted, output.Usage.InputTokens, output.Usage.OutputTokens, cost));

                try
                {
                    await EnsureCurrentAsync(generation, capability, workerOwner, LongRestVerificationDepth.Step, used, dispatch.Token).ConfigureAwait(false);

                    Require(await attempts.ValidateAttemptAsync(attempt, dispatch.Token).ConfigureAwait(false));

                    if (output.Disposition != LongRestProviderResultDisposition.Valid)
                    {
                        // The provider answered and billed, and the answer is not an instance of the closed shape: that is LongRest.InvalidOutput,
                        // the code the fault policy and the documentation name for it, and not an invalid request, which would say the caller's own
                        // request was malformed.
                        throw new Refused(new Error(output.InvalidReason ?? ErrorCodes.LongRest.InvalidOutput, "The paid Long Rest response could not be applied."));
                    }

                    // The owning store completes Sent and retains the artifact in one transaction.
                    return await accept(proposed, completion, dispatch.Token, false).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    if (output.Disposition == LongRestProviderResultDisposition.Valid)
                    {
                        try
                        {
                            // An uncancelled read-only exact replay resolves a committed local outcome;
                            // this mode refuses rather than writing when the artifact is absent.
                            return await accept(proposed, completion, CancellationToken.None, true).ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            // An unavailable/mismatched retained outcome grants no projection authority.
                        }
                    }

                    // This is accounting only, and the original exact Sent CAS must still exist.
                    // An already committed acceptance is immutable: this cannot downgrade it or
                    // increment its counters. Removed/replaced work is never recreated.
                    _ = await attempts.CompleteAttemptAsync(attempt, new(LongRestDispatchAttemptState.Invalid,
                        output.Usage, cost, output.InvalidReason ?? ErrorCodes.LongRest.CurrentnessLost), CancellationToken.None).ConfigureAwait(false);

                    throw;
                }
            }
            finally
            {
                await stop.CancelAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            await heartbeat.ConfigureAwait(false);
        }
    }

    // Best effort by design: the original failure is what the caller rethrows, and an unsettled Sent attempt is still settled by the
    // reboot rule. The write runs under the still-held external-effect group, with no usage and no cancellation token of its own.
    private async Task SettleUnresolvedAsync(LongRestDispatchAttempt attempt, LongRestUnresolvedSend outcome)
    {
        try
        {
            Result<LongRestDispatchAttempt> settled = await attempts.SettleUnresolvedSendAsync(attempt, outcome, CancellationToken.None).ConfigureAwait(false);

            if (settled.IsFailure) LogCleanupFailure(attempt, "long_rest.settlement_failed");
        }
        catch (Exception)
        {
            LogCleanupFailure(attempt, "long_rest.settlement_failed");
        }
    }

    // Best effort, like the settlement above: the original refusal is what the caller rethrows, and an attempt that could not be
    // withdrawn is left Prepared for the reboot rule. The write runs under the still-held external-effect group.
    private async Task ReleaseUnsentAsync(LongRestDispatchAttempt attempt)
    {
        try
        {
            Result released = await attempts.ReleaseUnsentAttemptAsync(attempt, CancellationToken.None).ConfigureAwait(false);

            if (released.IsFailure) LogCleanupFailure(attempt, "long_rest.release_unsent_failed");
        }
        catch (Exception)
        {
            LogCleanupFailure(attempt, "long_rest.release_unsent_failed");
        }
    }

    private void LogCleanupFailure(LongRestDispatchAttempt attempt, string code) =>
        logger.LogWarning("Long Rest recovery write failed: {Code}; operation {OperationId}, attempt {AttemptId}. The retained attempt requires recovery; the original failure is preserved.",
            code, attempt.Work.OperationId, attempt.AttemptId);

    private async Task RenewClaimAsync(LongRestMaintenanceCapability capability, CancellationToken ct, ILongRestAttemptStore? independent = null)
    {
        if (independent is null)
        {
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();

            await RenewClaimAsync(capability, ct, scope.ServiceProvider.GetRequiredService<ILongRestAttemptStore>()).ConfigureAwait(false);

            return;
        }

        SessionTurnClaimLease renewed = Require(await independent.RenewClaimAsync(capability.Claim, capability.CanonicalScope, ct).ConfigureAwait(false));

        if (!epoch.IsCurrent(capability.Configuration)) throw new Refused(Stale());

        Require(await capability.RefreshClaimAsync(renewed, capability.Configuration, ct).ConfigureAwait(false));
    }

    private async Task KeepClaimAliveAsync(LongRestDiscoveryGeneration generation, LongRestMaintenanceCapability? capability,
        string workerOwner, CancellationTokenSource dispatch, CancellationToken stoppingToken)
    {
        if (capability is null)
        {
            return;
        }

        try
        {
            using PeriodicTimer timer = new(TimeSpan.FromMinutes(1), timeProvider);

            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();

                LongRestLiveFenceReader independent = scope.ServiceProvider.GetRequiredService<LongRestLiveFenceReader>();

                // The heartbeat keeps the claim, the lease and the fence current while a send is in flight. It acts on no leaf, so it names none: the
                // gates around the send are the ones that re-prove them.
                await EnsureCurrentAsync(generation, capability, workerOwner, LongRestVerificationDepth.Step, [], stoppingToken, renewClaim: false, independent).ConfigureAwait(false);

                await RenewClaimAsync(capability, stoppingToken, scope.ServiceProvider.GetRequiredService<ILongRestAttemptStore>()).ConfigureAwait(false);

                await EnsureCurrentAsync(generation, capability, workerOwner, LongRestVerificationDepth.Step, [], stoppingToken, renewClaim: false, independent).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            logger.LogWarning("Long Rest claim heartbeat failed: {Code}; operation {OperationId}. Dispatch is cancelled; retained work requires recovery.",
                "long_rest.claim_heartbeat_failed", generation.OperationId);

            await dispatch.CancelAsync().ConfigureAwait(false);
        }
    }
}
