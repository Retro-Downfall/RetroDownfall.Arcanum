using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// The state one operator mutation is prepared against, and must still find at commit.
/// </summary>
/// <remarks>
/// Every value here is read from the installation, never from the request. A request that could
/// assert its own dataset generation or key epoch could authorize itself against a world that has
/// since moved, which is the whole failure the optimistic-concurrency contract exists to prevent.
/// </remarks>
public sealed record CovenantOperatorMutationBinding(
    Guid DatasetGeneration,
    ulong OperatorAuthorityEpoch,
    long ExpectedKeyEpoch,
    long? CampaignRegistryEpoch);

/// <summary>
/// Builds the two operator-authored mutation intents.
/// </summary>
/// <remarks>
/// The mirror of <see cref="CovenantAgentMutationFactory"/>, and deliberately a separate type. The
/// two paths agree on almost nothing that matters: an operator may write Global, may write the
/// Confirmed lane, may reactivate a tombstoned key, and carries an authority epoch instead of an
/// admission receipt. Folding them into one builder with nullable everything would make each of
/// those a runtime rule rather than a shape, and "the agent wrote Global" would become a validation
/// bug rather than an unrepresentable state.
///
/// <para>Pure and Core-owned so that the digests an operator's receipt binds are computed in exactly
/// one place, whichever surface — HTTP, CLI, or a later one — carried the request.</para>
/// </remarks>
public static class CovenantOperatorMutationFactory
{

    /// <summary>
    /// Builds one operator authoring of the Confirmed lane.
    /// </summary>
    /// <remarks>
    /// There is no lane parameter. The Confirmed lane is the only one an operator authors, and the
    /// Proposed lane belongs to the agent; taking a lane here would turn "an operator wrote Proposed"
    /// into a rule somebody has to remember to check.
    /// </remarks>
    public static Result<CovenantMutationIntent> Set(
        Guid mutationId,
        CovenantOperationScope scope,
        CovenantCompiledContent compiled,
        long expectedLaneRevision,
        bool reactivate,
        CovenantOperatorMutationBinding binding,
        CovenantDigest preflightBodyDigest)
    {

        ArgumentNullException.ThrowIfNull(compiled);

        ArgumentNullException.ThrowIfNull(binding);

        CovenantMutationArtifact artifact = new(
            compiled.AuthoredContent,
            compiled.Fragment,
            compiled.AuthoredHash,
            compiled.FragmentHash,
            compiled.FragmentUtf8ByteCount,
            compiled.RequiredFenceLength,
            compiled.CompilerPolicyVersion,
            compiled.RendererPolicyVersion);

        return Build(
            mutationId,
            CovenantMutationKind.OperatorSet,
            CovenantOperation.Set,
            scope,
            new CovenantKey(compiled.NormalizedKey),
            compiled.NormalizedKey,
            CovenantLane.Confirmed,
            expectedLaneRevision,
            reactivate,
            artifact,
            binding,
            preflightBodyDigest);

    }

    /// <summary>
    /// Builds one operator retirement of an exact lane head.
    /// </summary>
    /// <remarks>
    /// This is the path that can retire a Proposed entry the operator never approved, which the agent
    /// path deliberately cannot reach: the model may only retire what its own turn was shown.
    /// </remarks>
    public static Result<CovenantMutationIntent> Retire(
        Guid mutationId,
        CovenantOperationScope scope,
        string normalizedKey,
        string authoredKey,
        CovenantLane lane,
        long expectedLaneRevision,
        CovenantOperatorMutationBinding binding,
        CovenantDigest preflightBodyDigest)
    {

        ArgumentNullException.ThrowIfNull(binding);

        if (expectedLaneRevision <= 0)
        {

            return Result<CovenantMutationIntent>.Failure(new Error(
                ErrorCodes.Covenant.RevisionConflict,
                "A retirement names an existing lane head, so its expected revision cannot be zero."));

        }

        return Build(
            mutationId,
            CovenantMutationKind.OperatorRetire,
            CovenantOperation.Retire,
            scope,
            new CovenantKey(normalizedKey),
            authoredKey,
            lane,
            expectedLaneRevision,
            reactivate: false,
            artifact: null,
            binding,
            preflightBodyDigest);

    }

    private static Result<CovenantMutationIntent> Build(
        Guid mutationId,
        CovenantMutationKind kind,
        CovenantOperation operation,
        CovenantOperationScope scope,
        CovenantKey normalizedKey,
        string authoredKey,
        CovenantLane lane,
        long expectedLaneRevision,
        bool reactivate,
        CovenantMutationArtifact? artifact,
        CovenantOperatorMutationBinding binding,
        CovenantDigest preflightBodyDigest)
    {

        CovenantScope scopeKind = scope.CampaignId is null ? CovenantScope.Global : CovenantScope.Campaign;

        CovenantDigest requestDigest = CovenantDigests.MutationRequest(new MutationRequestDigestInput(
            kind,
            mutationId,
            scopeKind,
            scope.CampaignId,
            normalizedKey,
            lane,
            operation,
            checked((ulong)expectedLaneRevision),
            reactivate,
            CovenantOrigin.Operator,
            artifact?.AuthoredHash,
            artifact?.RenderedHash,
            (uint)(artifact?.CompilerPolicyVersion ?? CovenantCompiler.CompilerPolicyVersion),
            BasePlanDigest: null,
            AdmissionDigest: null,

            // An operator mutation has no attachment provenance: the content came from a person, not
            // from something the platform materialized on a model's behalf.
            []));

        // The operator authority epoch is the field that makes this an operator mutation rather than
        // an agent one, and a Global mutation additionally binds the Campaign registry epoch: Global
        // semantics reach every Campaign, including ones created between prepare and apply.
        CovenantDigest authorizationDigest = CovenantDigests.Authorization(new AuthorizationDigestInput(
            requestDigest,
            binding.DatasetGeneration,
            binding.OperatorAuthorityEpoch,
            NormalizedKeyDependencyEpoch: null,
            checked((ulong)binding.ExpectedKeyEpoch),
            binding.CampaignRegistryEpoch is { } registry ? checked((ulong)registry) : null,
            preflightBodyDigest,
            WardReceiptDigest: null,
            CovenantAuthorizationMode.ApiMasterKey));

        CovenantDigest finalMutationDigest = CovenantDigests.Mutation(
            new MutationDigestInput(requestDigest, authorizationDigest));

        return Result<CovenantMutationIntent>.Success(new CovenantMutationIntent(
            mutationId,
            kind,
            operation,
            CovenantOrigin.Operator,
            new CovenantMutationTarget(scope, normalizedKey, authoredKey, lane, requestDigest),
            expectedLaneRevision,
            reactivate,
            binding.ExpectedKeyEpoch,
            artifact,
            [],
            new CovenantMutationAuthorization(
                requestDigest,
                authorizationDigest,
                finalMutationDigest,

                // The operator path answers over HTTP, so the receipt binds the mutation itself: the
                // response body is derived from this receipt rather than the other way round.
                finalMutationDigest,
                CovenantAuthorizationMode.ApiMasterKey,
                WardReceiptDigest: null,
                preflightBodyDigest),
            sourceTurnId: null,
            sourceToolCallId: null,
            basePlanDigest: null,
            admissionReceiptDigest: null));

    }

}
