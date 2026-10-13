using Microsoft.Extensions.DependencyInjection;

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
    private async Task<T> ExecutePaidAsync<T>(LongRestDiscoveryGeneration generation, LongRestProviderPayload payload,
        GenerationProvenance provenance, LongRestMaintenanceCapability? capability, string workerOwner,
        Func<PaidResult, LongRestDispatchCompletion, CancellationToken, bool, Task<T>> accept, CancellationToken ct)
    {
        await EnsureCurrentAsync(generation, capability, workerOwner, ct).ConfigureAwait(false);

        IGrimoireWorkLease ownedWork = workAdmission.Lease;

        if (!ownedWork.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? group) || group is null)
        {
            ct.ThrowIfCancellationRequested();

            (capability?.Revocation ?? CancellationToken.None).ThrowIfCancellationRequested();

            if (ownedWork.MaintenanceRevocationPending)
            {
                throw new LongRestMaintenanceDeferredException(ownedWork.MaintenanceRevocation);
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

        LongRestDispatchAttempt attempt = Require(await attempts.PrepareAttemptAsync(payload.Work, payload.Kind, payloadDigest, destination, ct).ConfigureAwait(false));

        using CancellationTokenSource dispatch = CancellationTokenSource.CreateLinkedTokenSource(ct, ownedWork.MaintenanceRevocation,
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

                    await EnsureCurrentAsync(generation, capability, workerOwner, token).ConfigureAwait(false);

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
                        await EnsureCurrentAsync(generation, capability, workerOwner, token).ConfigureAwait(false);

                        Require(await attempts.ValidateAttemptAsync(attempt, token).ConfigureAwait(false));

                        Require(await child.ConsumeAsync(payload.Work, payloadDigest, destination, attempt.PhysicalOrdinal,
                            generation.Fence.Configuration, token).ConfigureAwait(false));
                    }

                    attempt = Require(await attempts.MarkSentAsync(attempt, token).ConfigureAwait(false));

                    await EnsureCurrentAsync(generation, capability, workerOwner, token).ConfigureAwait(false);

                    Require(await attempts.ValidateAttemptAsync(attempt, token).ConfigureAwait(false));
                }

                Result<LongRestProviderResult> result = await provider.ExecuteAsync(payload, UnrestrictedTurnBudget.Instance,
                    BeforeSend, providerAdmission, dispatch.Token).ConfigureAwait(false);

                // A transport without a reported result remains unresolved Sent. Normal billing alone
                // cannot reconstruct either usage or publication authority for this physical attempt.
                LongRestProviderResult output = Require(result);

                decimal cost = CostCalculator.CalculateCost(output.Usage.InputTokens, output.Usage.OutputTokens,
                    output.Usage.CachedInputTokens, output.Usage.ReasoningTokens, selected.Pricing);

                LongRestDispatchCompletion completion = new(LongRestDispatchAttemptState.Accepted, output.Usage, cost, null);

                LongRestDispatchAttempt accepted = attempt with { State = LongRestDispatchAttemptState.Accepted, Revision = checked(attempt.Revision + 1) };

                PaidResult proposed = new(output, new(accepted, output.Usage.InputTokens, output.Usage.OutputTokens, cost));

                try
                {
                    await EnsureCurrentAsync(generation, capability, workerOwner, dispatch.Token).ConfigureAwait(false);

                    Require(await attempts.ValidateAttemptAsync(attempt, dispatch.Token).ConfigureAwait(false));

                    if (output.Disposition != LongRestProviderResultDisposition.Valid)
                    {
                        throw new Refused(new Error(ErrorCodes.LongRest.InvalidRequest, "The paid Long Rest response could not be applied."));
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

                await EnsureCurrentAsync(generation, capability, workerOwner, stoppingToken, renewClaim: false, independent).ConfigureAwait(false);

                await RenewClaimAsync(capability, stoppingToken, scope.ServiceProvider.GetRequiredService<ILongRestAttemptStore>()).ConfigureAwait(false);

                await EnsureCurrentAsync(generation, capability, workerOwner, stoppingToken, renewClaim: false, independent).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            await dispatch.CancelAsync().ConfigureAwait(false);
        }
    }
}
