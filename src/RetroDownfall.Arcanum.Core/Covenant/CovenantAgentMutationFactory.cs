using System.Collections.Immutable;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// Builds the two agent-authored mutation intents from a live tool capability.
/// </summary>
/// <remarks>
/// Every field an agent could otherwise assert is derived here from the capability, never read from
/// the tool arguments: the scope is the turn's canonical Campaign, the lane is Proposed for a
/// proposal, the origin is AgentProposed for a proposal and AgentApproved for the operator-approved
/// retirement, the turn and tool-call identities come from the capability, and the provenance is the
/// exact materialization snapshot of the provider call that emitted the request. Agent-originated
/// Global mutation is therefore unrepresentable rather than merely refused (§10.14).
/// </remarks>
public static class CovenantAgentMutationFactory
{

    /// <summary>
    /// Stages one proposal into the Campaign's Proposed lane.
    /// </summary>
    public static Result<CovenantMutationIntent> Propose(
        CovenantToolInvocationContext capability,
        CovenantCompiledContent compiled,
        long expectedLaneRevision,
        long expectedKeyEpoch,
        CovenantDigest toolInputDigest)
    {

        ArgumentNullException.ThrowIfNull(capability);

        ArgumentNullException.ThrowIfNull(compiled);

        Result<ImmutableArray<CovenantMutationProvenanceLeaf>> provenance = ResolveProvenance(capability);

        if (provenance.IsFailure)
        {
            return Result<CovenantMutationIntent>.Failure(provenance.Error);
        }

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
            capability,
            CovenantMutationKind.AgentPropose,
            CovenantOperation.Set,
            CovenantOrigin.AgentProposed,
            new CovenantKey(compiled.NormalizedKey),
            compiled.NormalizedKey,
            CovenantLane.Proposed,
            expectedLaneRevision,
            expectedKeyEpoch,
            artifact,
            provenance.Value,
            toolInputDigest);

    }

    /// <summary>
    /// Stages one retirement against the exact target its Ward was shown.
    /// </summary>
    /// <remarks>
    /// The target, its lane, and its expected revision all come from the capability's preflight, so
    /// the staged tombstone is provably the thing the operator approved rather than whatever the
    /// model named afterwards.
    ///
    /// <para>The origin is <see cref="CovenantOrigin.AgentApproved"/> and not
    /// <see cref="CovenantOrigin.AgentProposed"/>: the model asked, but the operator authorized, and
    /// that is the one origin permitted to carry Ward evidence into a durable version (§5).</para>
    /// </remarks>
    public static Result<CovenantMutationIntent> Retire(
        CovenantToolInvocationContext capability,
        CovenantDigest toolInputDigest)
    {

        ArgumentNullException.ThrowIfNull(capability);

        if (capability.RetirementPreflight is not { } preflight
            || capability.WardReceipt is not { } ward)
        {
            return Result<CovenantMutationIntent>.Failure(new Error(
                ErrorCodes.Covenant.ForbiddenAuthority,
                "This capability carries no resolved retirement target and no operator consent."));
        }

        if (!ward.IsApproved)
        {
            return Result<CovenantMutationIntent>.Failure(new Error(
                ErrorCodes.Covenant.ForbiddenAuthority,
                "The operator did not approve this Covenant retirement."));
        }

        return Build(
            capability,
            CovenantMutationKind.AgentRetire,
            CovenantOperation.Retire,
            CovenantOrigin.AgentApproved,
            new CovenantKey(preflight.NormalizedKey),
            preflight.NormalizedKey,
            preflight.Lane,
            preflight.TargetLaneRevision,
            preflight.KeyEpoch,
            artifact: null,
            [],
            toolInputDigest);

    }

    /// <summary>
    /// Maps the provider call's materialization snapshot onto immutable provenance leaves.
    /// </summary>
    /// <remarks>
    /// Fails closed on an unprovenanced snapshot: content whose origin the platform could not account
    /// for must not become durable memory just because the model asked. The bound is the same one the
    /// intent enforces, checked here so the failure carries a Covenant code rather than an exception.
    /// </remarks>
    private static Result<ImmutableArray<CovenantMutationProvenanceLeaf>> ResolveProvenance(
        CovenantToolInvocationContext capability)
    {

        ProviderCallMaterializationSnapshot snapshot = capability.CallMaterialization;

        if (snapshot.Unprovenanced)
        {
            return Result<ImmutableArray<CovenantMutationProvenanceLeaf>>.Failure(new Error(
                ErrorCodes.Covenant.ForbiddenAuthority,
                "This provider call materialized content the platform could not attribute, so it cannot author durable memory."));
        }

        if (snapshot.Sources.Length > CovenantLimits.MaxAttachmentSourcesPerAgentMutation)
        {
            return Result<ImmutableArray<CovenantMutationProvenanceLeaf>>.Failure(new Error(
                ErrorCodes.Covenant.CapacityExceeded,
                "This provider call exceeded the attachment-provenance bound for one agent mutation."));
        }

        ImmutableArray<CovenantMutationProvenanceLeaf>.Builder leaves =
            ImmutableArray.CreateBuilder<CovenantMutationProvenanceLeaf>(snapshot.Sources.Length);

        for (int index = 0; index < snapshot.Sources.Length; index++)
        {

            MaterializationSourceDigestInput source = snapshot.Sources[index];

            leaves.Add(new CovenantMutationProvenanceLeaf(
                index,
                source.AttachmentId,
                source.AttachmentVersionId,
                source.LogicalKey,
                source.ContentDigest,
                source.SourceRange,
                source.SourceStart,
                source.SourceEnd,
                capability.LogicalTurnId,
                MaterializationReference: null));

        }

        return Result<ImmutableArray<CovenantMutationProvenanceLeaf>>.Success(leaves.ToImmutable());

    }

    private static Result<CovenantMutationIntent> Build(
        CovenantToolInvocationContext capability,
        CovenantMutationKind kind,
        CovenantOperation operation,
        CovenantOrigin origin,
        CovenantKey normalizedKey,
        string authoredKey,
        CovenantLane lane,
        long expectedLaneRevision,
        long expectedKeyEpoch,
        CovenantMutationArtifact? artifact,
        ImmutableArray<CovenantMutationProvenanceLeaf> provenance,
        CovenantDigest toolInputDigest)
    {

        if (capability.Campaign.CampaignId is not { } campaignId)
        {
            return Result<CovenantMutationIntent>.Failure(new Error(
                ErrorCodes.Covenant.InvalidScope,
                "An agent mutation requires the turn's canonical Campaign."));
        }

        CovenantOperationScope scope = CovenantOperationScope.ForCampaign(campaignId);

        CovenantDigest requestDigest = CovenantDigests.MutationRequest(new MutationRequestDigestInput(
            kind,
            capability.MutationId,
            CovenantScope.Campaign,
            campaignId,
            normalizedKey,
            lane,
            operation,
            checked((ulong)expectedLaneRevision),
            Reactivation: false,
            origin,
            artifact?.AuthoredHash,
            artifact?.RenderedHash,
            (uint)(artifact?.CompilerPolicyVersion ?? CovenantCompiler.CompilerPolicyVersion),
            capability.BasePlanDigest,
            capability.ProducingAdmission.Digest,
            [.. provenance.Select(static leaf => leaf.ContentHash)]));

        // No operator authority epoch: an agent mutation is authorized by the turn's admission and,
        // for a retirement, by the Ward receipt — never by operator authority, which models never
        // hold. The key-reclamation epoch is bound because the intent expects it, and evidence that
        // omitted it would not cover the one fact publication compares the key against.
        CovenantDigest authorizationDigest = CovenantDigests.Authorization(new AuthorizationDigestInput(
            requestDigest,
            capability.ProducingAdmission.Plan.Snapshot.DatasetGeneration.Value,
            OperatorAuthorityEpoch: null,
            NormalizedKeyDependencyEpoch: null,
            checked((ulong)expectedKeyEpoch),
            CampaignRegistryEpoch: null,
            capability.RetirementPreflight?.PreflightBodyDigest,
            capability.WardReceipt?.Digest,
            capability.WardReceipt?.Mode ?? CovenantAuthorizationMode.None));

        CovenantDigest finalMutationDigest = CovenantDigests.Mutation(
            new MutationDigestInput(requestDigest, authorizationDigest));

        return Result<CovenantMutationIntent>.Success(new CovenantMutationIntent(
            capability.MutationId,
            kind,
            operation,
            origin,
            new CovenantMutationTarget(scope, normalizedKey, authoredKey, lane, requestDigest),
            expectedLaneRevision,
            reactivate: false,
            expectedKeyEpoch,
            artifact,
            provenance,
            new CovenantMutationAuthorization(
                requestDigest,
                authorizationDigest,
                finalMutationDigest,
                // The MCP path has no HTTP response body to bind, so the durable receipt binds the
                // exact tool call whose answer reported this staging instead.
                CovenantDigestPair.Combine(finalMutationDigest, toolInputDigest),
                capability.WardReceipt?.Mode ?? CovenantAuthorizationMode.None,
                capability.WardReceipt?.Digest,
                capability.RetirementPreflight?.PreflightBodyDigest),
            capability.LogicalTurnId,
            capability.ToolCallId,
            capability.BasePlanDigest,
            capability.ProducingAdmission.Digest));

    }

}
