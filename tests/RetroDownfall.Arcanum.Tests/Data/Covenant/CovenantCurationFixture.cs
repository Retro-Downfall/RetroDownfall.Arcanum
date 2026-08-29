using System.Data;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// Builds curation intents and drives the kernel through one owned transaction.
/// </summary>
internal static class CovenantCurationFixture
{

    /// <summary>
    /// The authorization one curation request carries, with its request digest derived from the
    /// request itself.
    /// </summary>
    /// <remarks>
    /// Derived rather than seeded, because the request digest is the thing an idempotency conflict is
    /// detected by. A fixture that handed every intent the same constant digest would make two
    /// genuinely different requests look like a replay of one, and the conflict arm would be
    /// untestable while reading as covered.
    /// </remarks>
    internal static CovenantMutationAuthorization Authorization(CovenantCurationSubject subject, CovenantCurationKind kind, Guid mutationId, long expectedRevision)
    {

        CovenantDigest request = CovenantDigests.CurationRequest(new CurationRequestDigestInput(
            kind,
            mutationId,
            subject.Scope.Kind,
            subject.Scope.CampaignId,
            subject.NormalizedKey,
            subject.Lane,
            checked((ulong)subject.KeyEpoch),
            checked((ulong)expectedRevision)));

        CovenantDigest authorization = CovenantDigests.Authorization(new AuthorizationDigestInput(
            request,
            CovenantOperationGateFixture.CampaignOne,
            OperatorAuthorityEpoch: 1,
            NormalizedKeyDependencyEpoch: null,
            KeyReclamationEpoch: 1,
            CampaignRegistryEpoch: null,
            CovenantOperationGateFixture.Digest(9),
            WardReceiptDigest: null,
            CovenantAuthorizationMode.ApiMasterKey));

        CovenantDigest final = CovenantDigests.Mutation(new MutationDigestInput(request, authorization));

        return new CovenantMutationAuthorization(
            request,
            authorization,
            final,
            final,
            CovenantAuthorizationMode.ApiMasterKey,
            WardReceiptDigest: null,
            PreflightBodyDigest: CovenantOperationGateFixture.Digest(9));

    }

    internal static CovenantCurationIntent Pin(
        CovenantOperationScope scope,
        string key,
        long expectedRevision,
        Guid? mutationId = null,
        long keyEpoch = 0,
        CovenantLane lane = CovenantLane.Confirmed) =>
        Build(CovenantCurationKind.Pin, scope, key, lane, expectedRevision, mutationId, keyEpoch);

    internal static CovenantCurationIntent Unpin(
        CovenantOperationScope scope,
        string key,
        long expectedRevision,
        Guid? mutationId = null,
        long keyEpoch = 0,
        CovenantLane lane = CovenantLane.Confirmed) =>
        Build(CovenantCurationKind.Unpin, scope, key, lane, expectedRevision, mutationId, keyEpoch);

    internal static CovenantCurationIntent Mask(
        Guid? campaignId,
        string key,
        long expectedRevision,
        Guid? mutationId = null,
        long keyEpoch = 0) =>
        Build(
            CovenantCurationKind.Mask,
            campaignId is { } campaign ? CovenantOperationScope.ForCampaign(campaign) : CovenantOperationScope.Global,
            key,
            CovenantLane.Confirmed,
            expectedRevision,
            mutationId,
            keyEpoch);

    internal static CovenantCurationIntent Unmask(
        Guid campaignId,
        string key,
        long expectedRevision,
        Guid? mutationId = null,
        long keyEpoch = 0) =>
        Build(
            CovenantCurationKind.Unmask,
            CovenantOperationScope.ForCampaign(campaignId),
            key,
            CovenantLane.Confirmed,
            expectedRevision,
            mutationId,
            keyEpoch);

    internal static async Task<Result<CovenantCurationReceipt>> ApplyAsync(
        CovenantCanonicalFixture fixture,
        CovenantCurationCommit commit,
        CancellationToken cancellationToken,
        bool commitTransaction = true)
    {

        await using SqliteTransaction transaction = (SqliteTransaction)await fixture.Connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        CovenantMutationTransaction owned = new(fixture.Connection, transaction);

        Result<CovenantCurationReceipt> receipt =
            await new CovenantCurationKernel().ApplyAsync(commit, owned, cancellationToken);

        if (receipt.IsSuccess && commitTransaction)
        {

            await transaction.CommitAsync(cancellationToken);

        }
        else
        {

            await transaction.RollbackAsync(cancellationToken);

        }

        return receipt;

    }

    private static CovenantCurationIntent Build(
        CovenantCurationKind kind,
        CovenantOperationScope scope,
        string key,
        CovenantLane lane,
        long expectedRevision,
        Guid? mutationId,
        long keyEpoch)
    {

        CovenantCurationSubject subject = new(scope, new CovenantKey(key), lane, keyEpoch);

        Guid identity = mutationId ?? Guid.CreateVersion7();

        return new CovenantCurationIntent(
            identity,
            kind,
            subject,
            expectedRevision,
            Authorization(subject, kind, identity, expectedRevision));

    }

}
