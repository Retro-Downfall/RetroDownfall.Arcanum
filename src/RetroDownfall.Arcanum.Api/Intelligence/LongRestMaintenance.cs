using System.Collections.Immutable;
using System.Security.Cryptography;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>One bounded committed unit per call; the coordinator owns root consumption and checkpoints.</summary>
internal sealed partial class LongRestMaintenance(LongRestConfigurationEpoch epoch, ILongRestDiscoveryStore discovery,
    ILongRestSweepStore sweeps, ILongRestAttemptStore attempts, LongRestLiveFenceReader facts, ICovenantOperationGate gate,
    ILongRestProvider provider, IModelTokenEstimator estimator, ICovenantDisclosureJournal disclosures,
    IServiceScopeFactory scopes, TimeProvider timeProvider) : ILongRestMaintenance, ILongRestAdmittedMaintenance
{
    public async Task<Result<LongRestMaintenanceCapability>> CreateCapabilityAsync(Guid operationId, ArcanumInvocationContext invocation,
        SessionTurnClaimLease claim, CanonicalCampaignContext scope, CovenantTurnLease lease, CancellationToken ct)
    {
        try
        {
            LongRestSweepConfiguration configuration = ((ILongRestConfigurationProvider)epoch).Capture();

            if (!configuration.Enabled || !configuration.CovenantEnabled || configuration.Recipe.IsFailure)
            {
                return Stale();
            }

            Require(await gate.ValidateTurnLeaseAsync(lease, ct).ConfigureAwait(false));

            LongRestOperationScope stored = Require(await facts.ReadScopeAsync(operationId, ct).ConfigureAwait(false));

            if (!stored.Scope.IsInitialized || stored.Scope.ProtectedPartitionDigest is null || stored.Scope.CampaignId != scope.CampaignId
                || (stored.Scope.Kind == LongRestSweepScopeKind.Campaign) != scope.IsCampaignBound
                || stored.Configuration.ValueDigest != configuration.Stamp.ValueDigest
                || stored.Configuration.BootId == configuration.Stamp.BootId && stored.Configuration != configuration.Stamp)
            {
                return Stale();
            }

            LongRestDiscoveryGeneration? generation = Require(await discovery.ReadGenerationMetadataAsync(operationId, ct).ConfigureAwait(false));

            LongRestSweepFence current = Require(await facts.CaptureAsync(scope, configuration.Stamp, generation?.Fence.InsertionHighWatermark, ct).ConfigureAwait(false));

            if (generation is not null && (generation.Scope != stored.Scope || generation.Recipe != configuration.Recipe.Value
                || generation.Fence.CanonicalScope != scope || generation.Fence.DatasetGeneration != current.DatasetGeneration
                || generation.Fence.Configuration.BootId == current.Configuration.BootId && generation.Fence != current)
                || lease.Snapshot.DatasetGeneration != current.DatasetGeneration
                || lease.Snapshot.RuntimeAuthorityGeneration != current.RuntimeAuthorityGeneration)
            {
                return Stale();
            }

            if (generation is not null)
            {
                Require(await facts.ValidateRetainedSourceMetadataAsync(generation, ct).ConfigureAwait(false));
            }

            Require(await facts.ValidateClaimAsync(claim, scope, timeProvider.GetUtcNow(), ct).ConfigureAwait(false));

            Require(await gate.ValidateTurnLeaseAsync(lease, ct).ConfigureAwait(false));

            if (!epoch.IsCurrent(configuration.Stamp))
            {
                return Stale();
            }

            return await LongRestMaintenanceCapability.CreateAsync(operationId, invocation, claim, scope, lease,
                configuration.Stamp, ct, timeProvider).ConfigureAwait(false);
        }
        catch (Refused refusal)
        {
            return refusal.Error;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or ObjectDisposedException)
        {
            return Stale();
        }
    }

    /// <summary>One bounded unit under the lease the coordinator passed by value; this maintenance never stores or disposes it.</summary>
    public async Task<Result<LongRestSweepProgress>> ProcessAdmittedAsync(Guid operationId, string workerOwner,
        LongRestMaintenanceCapability? capability, IGrimoireWorkLease admission, CancellationToken ct)
    {
        // Admission Kind Guard: every Long Rest lease is a Long Rest sweep lease, and nothing else may carry a paid unit.
        if (admission is null) throw new ArgumentNullException(nameof(admission));

        if (admission.Kind != GrimoireWorkKind.LongRestSweep) throw new ArgumentException("Long Rest work requires a Long Rest sweep admission.", nameof(admission));

        ct.ThrowIfCancellationRequested();

        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, admission.MaintenanceRevocation);

        ct = lifetime.Token;

        try
        {
            LongRestSweepConfiguration configuration = ((ILongRestConfigurationProvider)epoch).Capture();

            if (!configuration.Enabled || configuration.Recipe.IsFailure)
            {
                return Stale();
            }

            LongRestDiscoveryGeneration generation = Require(await discovery.ReadGenerationMetadataAsync(operationId, ct).ConfigureAwait(false))
                ?? throw new Refused(Stale());

            Require(await facts.ReadWorkerAsync(generation, workerOwner, timeProvider.GetUtcNow(), ct).ConfigureAwait(false));

            if (generation.Recipe != configuration.Recipe.Value || generation.Fence.Configuration != configuration.Stamp)
            {
                return Stale();
            }

            // A protected run is resumed without a capability to read content with, so its first unit is the seam that re-proves every leaf
            // in full under the capability, before anything is sent. A clean run proved its whole corpus when it resumed.
            bool entry = capability is { EntrySeamPending: true };

            // The unit has not been read yet, so it has no inputs to name: a step here asks only what every step asks.
            await EnsureCurrentAsync(generation, capability, workerOwner, entry ? LongRestVerificationDepth.Seam : LongRestVerificationDepth.Step, [], ct).ConfigureAwait(false);

            if (entry)
            {
                capability!.MarkEntrySeamProven();
            }

            if (generation.Phase == LongRestSweepPhase.Capture)
            {
                Require(await discovery.CaptureNextSourcePageAsync(generation.GenerationId, generation.Revision, capability, ct).ConfigureAwait(false));
            }
            else if (generation.Phase is LongRestSweepPhase.Hierarchy or LongRestSweepPhase.Publish)
            {
                LongRestHierarchyWork? work = Require(await discovery.ReadNextFoldAsync(generation.GenerationId, capability, ct).ConfigureAwait(false));

                if (work is not null)
                {
                    await FoldAsync(generation, work, capability, workerOwner, admission, ct).ConfigureAwait(false);
                }
                else
                {
                    generation = Require(await discovery.ReadGenerationMetadataAsync(operationId, ct).ConfigureAwait(false))!;

                    // Publishing is a seam of its own and re-proves every leaf in the transaction that opens discovery; this only asks the fence first.
                    await EnsureCurrentAsync(generation, capability, workerOwner, LongRestVerificationDepth.Step, [], ct).ConfigureAwait(false);

                    Require(await discovery.PublishAsync(generation.GenerationId, generation.Revision, generation.Fence, capability, ct).ConfigureAwait(false));
                }
            }
            else if (generation.Phase is LongRestSweepPhase.Discover or LongRestSweepPhase.Adjudicate)
            {
                LongRestSweep sweep = Require(await sweeps.BeginOrResumeAsync(operationId, generation.Scope, generation.Fence.Configuration, ct).ConfigureAwait(false));

                LongRestSweepWork? work = Require(await sweeps.ReadNextWorkAsync(sweep, capability, ct).ConfigureAwait(false));

                if (work is not null)
                {
                    await AdjudicateAsync(generation, sweep, work, capability, workerOwner, admission, ct).ConfigureAwait(false);
                }
            }
            else if (generation.Phase != LongRestSweepPhase.Completed)
            {
                return Stale();
            }

            LongRestSweep committed = Require(await sweeps.BeginOrResumeAsync(operationId, generation.Scope, generation.Fence.Configuration, ct).ConfigureAwait(false));

            return new LongRestSweepProgress(operationId, committed.GenerationId, committed.Scope.Kind, committed.Scope.CampaignId,
                committed.Phase, committed.Revision, committed.CommittedItemCursor, null, committed.Scope.ProtectedPartitionDigest);
        }
        catch (Refused refusal)
        {
            return refusal.Error;
        }
    }

    private async Task FoldAsync(LongRestDiscoveryGeneration generation, LongRestHierarchyWork work,
        LongRestMaintenanceCapability? capability, string workerOwner, IGrimoireWorkLease admission, CancellationToken ct)
    {
        LongRestAcceptedHierarchySummary? summary = work.AcceptedSummary;

        GenerationProvenance provenance = work.Fold.Children.Aggregate(GenerationProvenance.CreateExact([]), (value, child) => value.Merge(child.Provenance));

        ImmutableArray<CovenantDigest> labels = [.. work.Fold.Children.SelectMany(child => child.LabelDigests).Distinct()];

        ContentSensitivity sensitivity = generation.Scope.ProtectedPartitionDigest is null ? ContentSensitivity.None : ContentSensitivity.CovenantDerived;

        if (summary is null)
        {
            Guid node = Guid.NewGuid();

            summary = await ExecutePaidAsync(generation, Require(LongRestProviderPayload.Summary(work.Fold, generation.Recipe.ProviderInputUtf8Bytes, generation.Recipe.SummaryUtf8Bytes)), provenance, capability, workerOwner, admission, work.Leaves,
                async (paid, completion, token, committedOnly) =>
                {
                    LongRestAcceptedHierarchySummary accepted = new(generation.GenerationId, node, work.Fold,
                        work.Fold.Work.RecipeDigest, paid.Evidence.Attempt.PayloadDigest, paid.Result.Summary!,
                        DerivedArtifactContentDigest.ForText(paid.Result.Summary!), sensitivity, provenance, labels, paid.Evidence);

                    return Require(await discovery.CommitAcceptedHierarchySummaryAsync(accepted, completion, workerOwner, capability, token, committedOnly).ConfigureAwait(false));
                }, ct).ConfigureAwait(false);
        }

        // The summary the embedding is built from derives from every leaf under the fold, so these gates re-prove all of them too.
        await ExecutePaidAsync(generation, Require(LongRestProviderPayload.Embedding(work.Fold.Work, summary.Summary)), provenance, capability, workerOwner, admission, work.Leaves,
            async (embedded, completion, token, committedOnly) =>
            {
                byte[] bytes = EmbeddingBlobCodec.Encode(embedded.Result.Vector.ToArray());

                LongRestEmbeddingBinding binding = new(generation.Recipe.EmbeddingProvider, generation.Recipe.EmbeddingModel,
                    generation.Recipe.EmbeddingDimensions, new CovenantDigest(SHA256.HashData(bytes)));

                return Require(await discovery.CommitFoldAsync(new(summary, binding, embedded.Result.Vector, embedded.Evidence),
                    completion, workerOwner, capability, token, committedOnly).ConfigureAwait(false));
            }, ct).ConfigureAwait(false);
    }

    private async Task AdjudicateAsync(LongRestDiscoveryGeneration generation, LongRestSweep sweep, LongRestSweepWork work,
        LongRestMaintenanceCapability? capability, string workerOwner, IGrimoireWorkLease admission, CancellationToken ct)
    {
        // The leaves this unit acts on: each is re-proved in full at every gate that stands between them and an effect.
        string[] used = work.MemoryIds();

        if (work.AcceptedAdjudication is { } retained)
        {
            await EnsureCurrentAsync(generation, capability, workerOwner, LongRestVerificationDepth.Step, used, ct).ConfigureAwait(false);

            Require(await sweeps.CommitDecisionAsync(sweep, retained, capability, ct).ConfigureAwait(false));

            return;
        }

        if (work.Inputs.Length >= 2 && work.Inputs.All(input => input.Snapshot.ContentHash == work.Inputs[0].Snapshot.ContentHash))
        {
            await EnsureCurrentAsync(generation, capability, workerOwner, LongRestVerificationDepth.Step, used, ct).ConfigureAwait(false);

            Require(await sweeps.CommitExactDuplicatesAsync(sweep, work.Identity, capability, ct).ConfigureAwait(false));

            return;
        }

        GenerationProvenance provenance = work.Inputs.Aggregate(GenerationProvenance.CreateExact([]), (value, input) => value.Merge(input.Provenance));

        LongRestAdjudication accepted = await ExecutePaidAsync(generation,
            Require(LongRestProviderPayload.Adjudication(work.Identity, work.Inputs, generation.Recipe.ProviderInputUtf8Bytes)), provenance, capability, workerOwner, admission, used,
            async (paid, completion, token, committedOnly) =>
            {
                LongRestProtectedEvidence? protection = generation.Scope.ProtectedPartitionDigest is { } partition
                    ? new(partition, provenance, [.. work.Inputs.SelectMany(input => input.LabelDigests).Distinct()]) : null;

                LongRestAdjudication decision = new(work.Identity, work.Inputs, paid.Result.Decision!, paid.Evidence.Attempt.PayloadDigest,
                    paid.Evidence, protection);

                return Require(await sweeps.CommitAcceptedAdjudicationAsync(sweep, decision, completion, workerOwner, capability, token, committedOnly).ConfigureAwait(false));
            }, ct).ConfigureAwait(false);

        await EnsureCurrentAsync(generation, capability, workerOwner, LongRestVerificationDepth.Step, used, ct).ConfigureAwait(false);

        Require(await sweeps.CommitDecisionAsync(sweep, accepted, capability, ct).ConfigureAwait(false));
    }

    // A gate names its depth and the leaves it acts on; neither has a default, so a gate added later cannot inherit the cheap depth, or an empty set
    // of inputs, by leaving them out.
    private async Task EnsureCurrentAsync(LongRestDiscoveryGeneration generation, LongRestMaintenanceCapability? capability, string workerOwner,
        LongRestVerificationDepth depth, IReadOnlyCollection<string> inputs, CancellationToken ct, bool renewClaim = true, LongRestLiveFenceReader? independentFacts = null)
    {
        ct.ThrowIfCancellationRequested();

        LongRestLiveFenceReader current = independentFacts ?? facts;

        if (string.IsNullOrWhiteSpace(workerOwner)) throw new Refused(Stale());

        Require(await current.ReadWorkerAsync(generation, workerOwner, timeProvider.GetUtcNow(), ct).ConfigureAwait(false));

        if (generation.Scope.ProtectedPartitionDigest is not null)
        {
            if (capability is null || !epoch.Capture().CovenantEnabled)
            {
                throw new Refused(Stale());
            }

            Require(await gate.ValidateTurnLeaseAsync(capability.Authority, ct).ConfigureAwait(false));

            Require(await capability.ValidateAsync(generation.Fence.Configuration, ct).ConfigureAwait(false));

            if (renewClaim && capability.Claim.LeaseDeadlineUtc <= timeProvider.GetUtcNow().AddMinutes(2))
            {
                Require(await current.ValidateGenerationAsync(generation, capability, timeProvider.GetUtcNow(), depth, inputs, ct).ConfigureAwait(false));

                await RenewClaimAsync(capability, ct).ConfigureAwait(false);
            }
        }

        Require(await current.ValidateGenerationAsync(generation, capability, timeProvider.GetUtcNow(), depth, inputs, ct).ConfigureAwait(false));

        if (capability is not null)
        {
            Require(await gate.ValidateTurnLeaseAsync(capability.Authority, ct).ConfigureAwait(false));
        }
    }

    private static T Require<T>(Result<T> result) => result.IsSuccess ? result.Value : throw new Refused(result.Error);

    private static void Require(Result result)
    {
        if (result.IsFailure) throw new Refused(result.Error);
    }

    private static Error Stale() => new(ErrorCodes.LongRest.StaleInput, "Long Rest no longer has its exact live configuration, source or request authority.");

    private sealed class Refused(Error error) : Exception(error.Code)
    {
        internal Error Error { get; } = error;
    }

    private sealed record PaidResult(LongRestProviderResult Result, LongRestPaidEvidence Evidence);
}
