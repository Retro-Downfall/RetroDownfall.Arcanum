using System.Data;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Covenant;

/// <summary>
/// The operator's curation path: measure what a pin, unpin, mask or unmask would do, then commit
/// exactly that.
/// </summary>
/// <remarks>
/// A separate file rather than a separate service. It is the same authority, the same token protocol,
/// and the same receipt-first commit as the write path beside it, and a second service that could
/// prepare a change this one commits would be a second opinion about one authority.
/// </remarks>
internal sealed partial class CovenantMutationService
{

    public async ValueTask<Result<CovenantCurationPreflightDto>> PrepareCurationAsync(
        CovenantCurationPrepareRequest request,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(request);

        Result validated = request.Validate();

        if (validated.IsFailure)
        {

            return validated.Error;

        }

        Result<ulong> authorityEpoch = ResolveAuthorityEpoch();

        if (authorityEpoch.IsFailure)
        {

            return authorityEpoch.Error;

        }

        CovenantOperationScope scope = Scope(request.Scope, request.CampaignId);

        string normalizedKey = new CovenantKey(request.Key).Value;

        Result<CovenantCurationEffectSnapshot> effect = await store
            .ReadCurationEffectSnapshotAsync(
                new CovenantCurationEffectQuery(scope, normalizedKey, request.Lane, request.Kind),
                readLease,
                cancellationToken)
            .ConfigureAwait(false);

        if (effect.IsFailure)
        {

            return effect.Error;

        }

        CovenantCurationSubject subject = new(
            scope,
            new CovenantKey(normalizedKey),
            request.Lane,
            effect.Value.KeyEpoch);

        CovenantDigest requestDigest = CovenantOperatorCurationFactory.RequestDigest(
            request.MutationId,
            request.Kind,
            subject,
            request.ExpectedRevision);

        CovenantCurationState projected = Project(request.Kind, effect.Value.Current);

        DateTimeOffset issuedAt = timeProvider.GetUtcNow();

        DateTimeOffset expiresAt = issuedAt + PreflightLifetime;

        CovenantOperatorPreflightBody body = new(
            requestDigest,
            authorityEpoch.Value,
            effect.Value.DatasetGeneration,
            checked((ulong)request.ExpectedRevision),
            checked((ulong)effect.Value.KeyEpoch),
            checked((ulong)effect.Value.KeyReclamationEpoch),

            // A curation change names one subject and reaches no Campaign it did not name, so binding
            // the Campaign registry would make it stale for reasons that cannot affect it.
            CampaignRegistryEpoch: null,
            CompiledArtifactDigest: null,
            DependentHeads(effect.Value, subject),
            Effect(request.Kind, effect.Value, projected),
            issuedAt.ToUnixTimeMilliseconds(),
            expiresAt.ToUnixTimeMilliseconds());

        Result<string> token = codec.Encode(
            CovenantEnvelopePurpose.OperatorPreflight,
            body.Encode(),
            PreflightLifetime,
            issuedAt);

        if (token.IsFailure)
        {

            return token.Error;

        }

        return new CovenantCurationPreflightDto(
            request.Kind,
            scope.CampaignId is null ? CovenantScope.Global : CovenantScope.Campaign,
            scope.CampaignId,
            normalizedKey,
            request.Lane,
            request.MutationId,
            Hex(requestDigest),
            effect.Value.Current.IsPinned,
            effect.Value.Current.IsMasked,
            effect.Value.Current.Revision,
            request.ExpectedRevision,
            effect.Value.KeyEpoch,
            effect.Value.GlobalConfirmedSuppressed,
            effect.Value.GlobalConfirmedResurfaces,
            projected.IsPinned != effect.Value.Current.IsPinned || projected.IsMasked != effect.Value.Current.IsMasked,
            issuedAt,
            expiresAt,
            token.Value);

    }

    public async ValueTask<Result<CovenantCurationResultDto>> CurateAsync(
        CovenantCurationRequest request,
        CovenantWriteLease writeLease,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(request);

        ArgumentNullException.ThrowIfNull(writeLease);

        Result validated = request.Validate();

        if (validated.IsFailure)
        {

            return validated.Error;

        }

        Result revalidated = await writeLease.RevalidateAsync(cancellationToken).ConfigureAwait(false);

        if (revalidated.IsFailure)
        {

            return revalidated.Error;

        }

        CovenantOperationScope scope = Scope(request.Scope, request.CampaignId);

        string normalizedKey = new CovenantKey(request.Key).Value;

        // Receipt first, before the token is decoded at all. The codec refuses an expired token, so a
        // client that lost its response and retried after the five-minute lifetime would otherwise be
        // told its committed change was stale. The receipt is found by the mutation identity alone and
        // checked against the request's own fields, so a replay needs nothing the token carries.
        Result<CovenantCurationResultDto?> replayed = await TryReplayCurationAsync(
                request,
                scope,
                normalizedKey,
                cancellationToken)
            .ConfigureAwait(false);

        if (replayed.IsFailure)
        {

            return replayed.Error;

        }

        if (replayed.Value is { } committed)
        {

            return committed;

        }

        Result<ulong> authorityEpoch = ResolveAuthorityEpoch();

        if (authorityEpoch.IsFailure)
        {

            return authorityEpoch.Error;

        }

        Result<CovenantEnvelopeBody> envelope =
            codec.Decode(CovenantEnvelopePurpose.OperatorPreflight, request.PreflightToken);

        if (envelope.IsFailure)
        {

            return envelope.Error;

        }

        Result<CovenantOperatorPreflightBody> body =
            CovenantOperatorPreflightBody.TryDecode(envelope.Value.Payload);

        if (body.IsFailure)
        {

            return body.Error;

        }

        // The subject's dependency epoch is the token's, never the request's. A commit that could
        // assert its own epoch would authorize itself against a world the preflight never read. The
        // binding epoch is left for the kernel, which reads it under the write lock.
        CovenantCurationSubject subject = new(
            scope,
            new CovenantKey(normalizedKey),
            request.Lane,
            checked((long)body.Value.NormalizedKeyDependencyEpoch));

        CovenantDigest committedRequestDigest = CovenantOperatorCurationFactory.RequestDigest(
            request.MutationId,
            request.Kind,
            subject,
            request.ExpectedRevision);

        if (body.Value.RequestDigest != committedRequestDigest)
        {

            return new Error(
                ErrorCodes.Covenant.ForbiddenAuthority,
                "This Covenant preflight token was issued for a different request.");

        }

        // The token's own lifetime is not judged here. The codec already refused an expired token when
        // it decoded this one, so what is left to check is that the body and its envelope agree.
        if (body.Value.IssuedAt != envelope.Value.IssuedAtUtc.ToUnixTimeMilliseconds()
            || body.Value.ExpiresAt != envelope.Value.ExpiresAtUtc.ToUnixTimeMilliseconds())
        {

            return new Error(
                ErrorCodes.Covenant.ForbiddenAuthority,
                "This Covenant preflight token is internally inconsistent.");

        }

        if (authorityEpoch.Value != body.Value.OperatorAuthorityEpoch)
        {

            return new Error(
                ErrorCodes.Covenant.StaleSnapshot,
                "Operator authority changed after this curation change was prepared.");

        }

        Result<CovenantCurationIntent> intent = CovenantOperatorCurationFactory.Curate(
            request.MutationId,
            request.Kind,
            subject,
            request.ExpectedRevision,
            Binding(body.Value),
            body.Value.Digest());

        if (intent.IsFailure)
        {

            return intent.Error;

        }

        return await CommitCurationAsync(intent.Value, body.Value, cancellationToken).ConfigureAwait(false);

    }

    private async ValueTask<Result<CovenantCurationResultDto>> CommitCurationAsync(
        CovenantCurationIntent intent,
        CovenantOperatorPreflightBody body,
        CancellationToken cancellationToken)
    {

        SqliteConnection connection = await connections
            .GetOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);

        CovenantCurationCommit commit = new(
            body.DatasetGeneration,
            checked((long)body.KeyReclamationEpoch),
            timeProvider.GetUtcNow(),
            intent);

        Result<CovenantCurationReceipt> applied = await curationKernel
            .ApplyAsync(commit, new CovenantMutationTransaction(connection, transaction), cancellationToken)
            .ConfigureAwait(false);

        if (applied.IsFailure)
        {

            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return applied.Error;

        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return Dto(applied.Value);

    }

    /// <summary>
    /// Resolves an already-committed curation identity, or reports that this is a new request.
    /// </summary>
    /// <remarks>
    /// Runs in a read transaction of its own, so a replay costs no write and no exclusive acquisition.
    /// The kernel resolves the same replay inside its own transaction; this is the cheaper path that
    /// keeps a lost-response retry from opening one at all.
    ///
    /// <para>It takes no token and no digest computed from one. The request digest binds the key's
    /// dependency epoch, which the request itself does not carry: a first commit takes it from the
    /// authenticated token, and a replay takes it from the receipt that commit wrote. Recomputing the
    /// digest from the request's fields and the stored epoch is what lets an exact retry be answered
    /// after its token expired, while the same identity carrying any changed field still disagrees
    /// with the stored digest and is refused as a conflict.</para>
    ///
    /// <para>The state it reports is the subject's current one, joined on the key's current binding
    /// epoch. The stored dependency epoch is only a digest input; no head is ever recorded under it.
    /// </para>
    /// </remarks>
    private async ValueTask<Result<CovenantCurationResultDto?>> TryReplayCurationAsync(
        CovenantCurationRequest request,
        CovenantOperationScope scope,
        string normalizedKey,
        CancellationToken cancellationToken)
    {

        SqliteConnection connection = await connections
            .GetOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT r.RequestIdempotencyDigest, r.ResponseReceiptDigest, r.OutcomeCode,
                   r.ResultingVersionId, r.ResultingRevision,
                   COALESCE(h.IsPinned, 0), COALESCE(h.IsMasked, 0),
                   r.KeyEpoch
            FROM covenant_curation_receipts r
            LEFT JOIN covenant_curation_heads h
                ON h.CampaignId IS r.CampaignId AND h.NormalizedKey = r.NormalizedKey
                   AND h.LaneCode = r.LaneCode
                   AND h.KeyEpoch = COALESCE((SELECT IncarnationEpoch FROM covenant_key_epochs WHERE NormalizedKey = r.NormalizedKey), 0)
            WHERE r.MutationId = $mutation;
            """;

        _ = command.Parameters.AddWithValue("$mutation", request.MutationId.ToString("D"));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            return Result<CovenantCurationResultDto?>.Success(null);

        }

        CovenantDigest storedRequest = new((byte[])reader.GetValue(0));

        CovenantDigest requestDigest = CovenantOperatorCurationFactory.RequestDigest(
            request.MutationId,
            request.Kind,
            new CovenantCurationSubject(scope, new CovenantKey(normalizedKey), request.Lane, reader.GetInt64(7)),
            request.ExpectedRevision);

        if (storedRequest != requestDigest)
        {

            return new Error(
                "Security.IdempotencyConflict",
                "This Covenant curation ID was already used with different client input.");

        }

        return Result<CovenantCurationResultDto?>.Success(new CovenantCurationResultDto(
            request.MutationId,
            (CovenantMutationOutcome)reader.GetInt32(2),
            request.Kind,
            scope.CampaignId is null ? CovenantScope.Global : CovenantScope.Campaign,
            scope.CampaignId,
            normalizedKey,
            request.Lane,
            reader.GetInt32(5) == 1,
            reader.GetInt32(6) == 1,
            reader.IsDBNull(3)
                ? null
                : Guid.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            Hex(storedRequest),
            Hex(new CovenantDigest((byte[])reader.GetValue(1))),
            Replayed: true));

    }

    private static CovenantCurationResultDto Dto(CovenantCurationReceipt receipt) =>
        new(
            receipt.MutationId,
            receipt.Outcome,
            receipt.Kind,
            receipt.Subject.Scope.CampaignId is null ? CovenantScope.Global : CovenantScope.Campaign,
            receipt.Subject.Scope.CampaignId,
            receipt.Subject.NormalizedKey.Value,
            receipt.Subject.Lane,
            receipt.ResultingState.IsPinned,
            receipt.ResultingState.IsMasked,
            receipt.ResultingVersionId,
            receipt.ResultingRevision,
            Hex(receipt.RequestIdempotencyDigest),
            Hex(receipt.ResponseReceiptDigest),
            receipt.Replayed);

    /// <summary>
    /// The state one kind would leave, without an intent to ask.
    /// </summary>
    /// <remarks>
    /// The preflight has a kind and a current state and no validated intent yet, so the transition is
    /// stated once here and consumed by <see cref="CovenantCurationIntent.Project"/> as well. Two
    /// implementations of one transition is how a preview and a commit come to disagree.
    /// </remarks>
    private static CovenantCurationState Project(CovenantCurationKind kind, CovenantCurationState current) =>
        kind switch
        {
            CovenantCurationKind.Pin => current with { IsPinned = true },
            CovenantCurationKind.Unpin => current with { IsPinned = false },
            CovenantCurationKind.Mask => current with { IsMasked = true },
            _ => current with { IsMasked = false },
        };

    private static CovenantDigest DependentHeads(
        CovenantCurationEffectSnapshot effect,
        CovenantCurationSubject subject) =>
        CovenantDigests.CurationDependentHeads(new CurationDependentHeadsDigestInput(
            subject.Scope.Kind,
            subject.Scope.CampaignId,
            subject.NormalizedKey,
            subject.Lane,
            checked((ulong)subject.KeyDependencyEpoch),
            effect.GlobalConfirmedHeadExists,
            effect.ScopedConfirmedHeadExists));

    private static CovenantDigest Effect(
        CovenantCurationKind kind,
        CovenantCurationEffectSnapshot effect,
        CovenantCurationState projected) =>
        CovenantDigests.CurationEffect(new CurationEffectDigestInput(
            kind,
            checked((ulong)effect.Current.Revision),
            effect.Current.IsPinned,
            effect.Current.IsMasked,
            projected.IsPinned,
            projected.IsMasked,
            effect.GlobalConfirmedSuppressed,
            effect.GlobalConfirmedResurfaces));

}
