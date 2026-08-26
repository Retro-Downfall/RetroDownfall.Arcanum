using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// Builds the one operator-authored curation intent.
/// </summary>
/// <remarks>
/// The mirror of <see cref="CovenantOperatorMutationFactory"/>, and there is deliberately no agent
/// counterpart. Curation is what an operator does <i>about</i> the agent — a pin exists to refuse the
/// model's authorship — so a factory the model could reach would be a factory for undoing the thing.
///
/// <para>Pure and Core-owned so that the digests an operator's receipt binds are computed in exactly
/// one place, whichever surface carried the request.</para>
/// </remarks>
public static class CovenantOperatorCurationFactory
{

    public static Result<CovenantCurationIntent> Curate(
        Guid mutationId,
        CovenantCurationKind kind,
        CovenantCurationSubject subject,
        long expectedRevision,
        CovenantOperatorMutationBinding binding,
        CovenantDigest preflightBodyDigest)
    {

        ArgumentNullException.ThrowIfNull(subject);

        ArgumentNullException.ThrowIfNull(binding);

        CovenantDigest requestDigest = RequestDigest(mutationId, kind, subject, expectedRevision);

        // The operator authority epoch is the field that makes this an operator change, and there is
        // no other kind. A curation change binds no Campaign registry epoch: a pin or a mask names one
        // subject and reaches no Campaign it did not name, so binding the registry would make it stale
        // for reasons that cannot affect it.
        CovenantDigest authorizationDigest = CovenantDigests.Authorization(new AuthorizationDigestInput(
            requestDigest,
            binding.DatasetGeneration,
            binding.OperatorAuthorityEpoch,
            NormalizedKeyDependencyEpoch: checked((ulong)subject.KeyEpoch),
            checked((ulong)binding.ExpectedKeyEpoch),
            CampaignRegistryEpoch: null,
            preflightBodyDigest,
            WardReceiptDigest: null,
            CovenantAuthorizationMode.ApiMasterKey));

        CovenantDigest finalDigest = CovenantDigests.Mutation(
            new MutationDigestInput(requestDigest, authorizationDigest));

        try
        {

            return Result<CovenantCurationIntent>.Success(new CovenantCurationIntent(
                mutationId,
                kind,
                subject,
                expectedRevision,
                new CovenantMutationAuthorization(
                    requestDigest,
                    authorizationDigest,
                    finalDigest,

                    // The operator path answers over HTTP, so the receipt binds the change itself: the
                    // response body is derived from this receipt rather than the other way round.
                    finalDigest,
                    CovenantAuthorizationMode.ApiMasterKey,
                    WardReceiptDigest: null,
                    preflightBodyDigest)));

        }
        catch (ArgumentException refused)
        {

            return Result<CovenantCurationIntent>.Failure(new Error(
                ErrorCodes.Covenant.InvalidScope,
                refused.Message));

        }

    }

    /// <summary>
    /// The request digest a commit recomputes from its own fields, with no token in hand.
    /// </summary>
    /// <remarks>
    /// Exposed so the commit path can resolve an already-committed identity before it decodes a token
    /// at all. A client that lost its response and retried after the token expired then receives its
    /// committed answer rather than a stale-token refusal for work that already happened.
    /// </remarks>
    public static CovenantDigest RequestDigest(
        Guid mutationId,
        CovenantCurationKind kind,
        CovenantCurationSubject subject,
        long expectedRevision)
    {

        ArgumentNullException.ThrowIfNull(subject);

        return CovenantDigests.CurationRequest(new CurationRequestDigestInput(
            kind,
            mutationId,
            subject.Scope.Kind,
            subject.Scope.CampaignId,
            subject.NormalizedKey,
            subject.Lane,
            checked((ulong)subject.KeyEpoch),
            checked((ulong)expectedRevision)));

    }

}
