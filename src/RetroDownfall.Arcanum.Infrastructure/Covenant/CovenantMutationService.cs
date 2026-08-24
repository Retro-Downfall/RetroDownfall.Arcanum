using System.Data;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Covenant;

/// <summary>
/// The operator's two writes: prepare what a mutation would do, then commit exactly that.
/// </summary>
/// <remarks>
/// Prepare is read-only and measures the installation as it stands; commit refuses anything the
/// measurement no longer describes. The token between them is not the authority — the operator's API
/// key already established that — it is the binding that makes "you were shown this effect" and "this
/// is the effect you are committing" the same sentence.
///
/// <para>Commit is receipt-first. An exact retry of a mutation identity resolves through the durable
/// receipt the kernel already wrote, before token expiry, key version, revision, or epoch are looked
/// at, so a client that lost the response to a network failure gets its committed answer back rather
/// than a stale-token refusal for work that already happened.</para>
/// </remarks>
internal sealed class CovenantMutationService(
    ICovenantStore store,
    ICovenantCompiler compiler,
    ICovenantEnvelopeCodec codec,
    ICovenantConnectionSource connections,
    CovenantMutationKernel kernel,
    ICovenantAuthoritySnapshotProvider authority,
    TimeProvider timeProvider) : ICovenantMutationService
{

    /// <summary>How long a prepared mutation stays committable.</summary>
    /// <remarks>
    /// Short because the effect it describes is a measurement of live state. Long enough that an
    /// operator can read what they are about to do, which is the entire reason the step exists.
    /// </remarks>
    private static readonly TimeSpan PreflightLifetime = TimeSpan.FromMinutes(5);

    public async ValueTask<Result<CovenantMutationPreflightDto>> PrepareSetAsync(
        CovenantSetPrepareRequest request,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(request);

        Result validated = request.Validate();

        if (validated.IsFailure)
        {

            return validated.Error;

        }

        Result<CovenantCompiledContent> compiled = TryCompile(request.Key, request.Content);

        if (compiled.IsFailure)
        {

            return compiled.Error;

        }

        return await PrepareAsync(
                Scope(request.Scope, request.CampaignId),
                compiled.Value.NormalizedKey,
                CovenantLane.Confirmed,
                CovenantOperation.Set,
                request.MutationId,
                request.ExpectedRevision,
                request.Reactivate,
                compiled.Value,
                readLease,
                cancellationToken)
            .ConfigureAwait(false);

    }

    public async ValueTask<Result<CovenantMutationPreflightDto>> PrepareRetireAsync(
        CovenantRetirePrepareRequest request,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(request);

        Result validated = request.Validate();

        if (validated.IsFailure)
        {

            return validated.Error;

        }

        return await PrepareAsync(
                    Scope(request.Scope, request.CampaignId),
                    new CovenantKey(request.Key).Value,
                    request.Lane,
                    CovenantOperation.Retire,
                    request.MutationId,
                    request.ExpectedRevision,
                    reactivate: false,
                    compiled: null,
                    readLease,
                    cancellationToken)
                .ConfigureAwait(false);

    }

    public async ValueTask<Result<CovenantMutationResultDto>> SetAsync(
        CovenantSetRequest request,
        CovenantWriteLease writeLease,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(request);

        Result validated = request.Validate();

        if (validated.IsFailure)
        {

            return validated.Error;

        }

        Result<CovenantCompiledContent> compiled = TryCompile(request.Key, request.Content);

        return compiled.IsFailure
            ? compiled.Error
            : await ApplyAsync(
                    Scope(request.Scope, request.CampaignId),
                    compiled.Value.NormalizedKey,
                    CovenantLane.Confirmed,
                    CovenantOperation.Set,
                    request.MutationId,
                    request.ExpectedRevision,
                    request.Reactivate,
                    compiled.Value,
                    request.PreflightToken,
                    writeLease,
                    cancellationToken)
                .ConfigureAwait(false);

    }

    public async ValueTask<Result<CovenantMutationResultDto>> RetireAsync(
        CovenantRetireRequest request,
        CovenantWriteLease writeLease,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(request);

        Result validated = request.Validate();

        if (validated.IsFailure)
        {

            return validated.Error;

        }

        return await ApplyAsync(
                    Scope(request.Scope, request.CampaignId),
                    new CovenantKey(request.Key).Value,
                    request.Lane,
                    CovenantOperation.Retire,
                    request.MutationId,
                    request.ExpectedRevision,
                    reactivate: false,
                    compiled: null,
                    request.PreflightToken,
                    writeLease,
                    cancellationToken)
                .ConfigureAwait(false);

    }

    /// <summary>
    /// Measures what this mutation would do, and issues the token that binds the measurement.
    /// </summary>
    /// <remarks>
    /// Every epoch the token carries is read here, inside the same bounded read that produced the
    /// effect. Reading them separately would leave a window in which the effect an operator was shown
    /// and the epochs their token binds describe two different installations.
    /// </remarks>
    private async ValueTask<Result<CovenantMutationPreflightDto>> PrepareAsync(
        CovenantOperationScope scope,
        string normalizedKey,
        CovenantLane lane,
        CovenantOperation operation,
        Guid mutationId,
        long expectedRevision,
        bool reactivate,
        CovenantCompiledContent? compiled,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken)
    {

        Result<ulong> authorityEpoch = ResolveAuthorityEpoch();

        if (authorityEpoch.IsFailure)
        {

            return authorityEpoch.Error;

        }

        Result<CovenantMutationEffectSnapshot> effect = await store
            .ReadMutationEffectSnapshotAsync(
                new CovenantMutationEffectQuery(scope, normalizedKey, lane, operation),
                readLease,
                cancellationToken)
            .ConfigureAwait(false);

        if (effect.IsFailure)
        {

            return effect.Error;

        }

        Result<CovenantDetail> detail = await store
            .ReadDetailAsync(new CovenantDetailQuery(scope, normalizedKey), readLease, cancellationToken)
            .ConfigureAwait(false);

        if (detail.IsFailure)
        {

            return detail.Error;

        }

        // Reported from the head rather than echoed from the request: an operator comparing what they
        // asked for against what is there is the whole point of showing a current revision at all.
        long currentRevision = (lane is CovenantLane.Confirmed
            ? detail.Value.ConfirmedHead?.LaneRevision
            : detail.Value.ProposedHead?.LaneRevision) ?? 0;

        CovenantDigest requestDigest = RequestDigest(
            scope,
            normalizedKey,
            lane,
            operation,
            mutationId,
            expectedRevision,
            reactivate,
            compiled);

        DateTimeOffset issuedAt = timeProvider.GetUtcNow();

        DateTimeOffset expiresAt = issuedAt + PreflightLifetime;

        CovenantOperatorPreflightBody body = new(
            requestDigest,
            authorityEpoch.Value,
            effect.Value.DatasetGeneration,
            checked((ulong)expectedRevision),
            checked((ulong)effect.Value.KeyEpoch),
            checked((ulong)effect.Value.KeyReclamationEpoch),

            // A Global mutation reaches every Campaign, including ones created between prepare and
            // apply, so its token binds the registry epoch. A Campaign mutation reaches exactly one
            // and binding the registry would make it stale for reasons that cannot affect it.
            scope.CampaignId is null ? checked((ulong)effect.Value.CampaignRegistryEpoch) : null,
            compiled?.FragmentHash,
            effect.Value.DependentHeadVectorDigest,
            EffectDigest(effect.Value),
            issuedAt.ToUnixTimeMilliseconds(),
            expiresAt.ToUnixTimeMilliseconds());

        // The body repeats these timestamps and the commit path requires the two to agree byte for
        // byte, so the instant is stated rather than read a second time inside the codec.
        Result<string> token = codec.Encode(
            CovenantEnvelopePurpose.OperatorPreflight,
            body.Encode(),
            PreflightLifetime,
            issuedAt);

        if (token.IsFailure)
        {

            return token.Error;

        }

        return new CovenantMutationPreflightDto(
            scope.CampaignId is null ? CovenantScope.Global : CovenantScope.Campaign,
            scope.CampaignId,
            normalizedKey,
            lane,
            operation,
            mutationId,
            Hex(requestDigest),
            compiled is null ? null : Hex(compiled.AuthoredHash),
            compiled is null ? null : Hex(compiled.FragmentHash),
            compiled?.FragmentUtf8ByteCount,
            currentRevision,
            effect.Value.KeyEpoch,
            EffectDto(effect.Value, scope, compiled),
            issuedAt,
            expiresAt,
            token.Value);

    }

    /// <summary>
    /// Commits exactly the mutation a token was issued for, or refuses it.
    /// </summary>
    /// <remarks>
    /// The request digest is recomputed from the commit's own canonical fields and compared against
    /// the one inside the token. That comparison is what makes a token unusable for any request but
    /// the one it was prepared for — a client cannot carry a token from a cheap mutation onto an
    /// expensive one, because the digest names the whole request.
    /// </remarks>
    private async ValueTask<Result<CovenantMutationResultDto>> ApplyAsync(
        CovenantOperationScope scope,
        string normalizedKey,
        CovenantLane lane,
        CovenantOperation operation,
        Guid mutationId,
        long expectedRevision,
        bool reactivate,
        CovenantCompiledContent? compiled,
        string preflightToken,
        CovenantWriteLease writeLease,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(writeLease);

        Result revalidated = await writeLease.RevalidateAsync(cancellationToken).ConfigureAwait(false);

        if (revalidated.IsFailure)
        {

            return revalidated.Error;

        }

        CovenantDigest committedRequestDigest = RequestDigest(
            scope,
            normalizedKey,
            lane,
            operation,
            mutationId,
            expectedRevision,
            reactivate,
            compiled);

        // Receipt first, before the token is even looked at. A client that lost the response to a
        // network failure and retries after the five-minute lifetime has expired must get its
        // committed answer back, not a stale-token refusal for work that already happened.
        Result<CovenantMutationResultDto?> replayed = await TryReplayAsync(
                mutationId,
                committedRequestDigest,
                scope,
                normalizedKey,
                lane,
                operation,
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

        Result<CovenantEnvelopeBody> envelope = codec.Decode(
            CovenantEnvelopePurpose.OperatorPreflight,
            preflightToken);

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

        if (body.Value.RequestDigest != committedRequestDigest)
        {

            return new Error(
                ErrorCodes.Covenant.ForbiddenAuthority,
                "This Covenant preflight token was issued for a different request.");

        }

        // Issued-at and expires-at must equal the authenticated header values byte for byte before
        // either is used to judge expiry: a body that could disagree with its own header would let a
        // caller extend a token's life by editing the half the header does not cover.
        if (body.Value.IssuedAt != envelope.Value.IssuedAtUtc.ToUnixTimeMilliseconds()
            || body.Value.ExpiresAt != envelope.Value.ExpiresAtUtc.ToUnixTimeMilliseconds())
        {

            return new Error(
                ErrorCodes.Covenant.ForbiddenAuthority,
                "This Covenant preflight token is internally inconsistent.");

        }

        Result<ulong> authorityEpoch = ResolveAuthorityEpoch();

        if (authorityEpoch.IsFailure)
        {

            return authorityEpoch.Error;

        }

        if (authorityEpoch.Value != body.Value.OperatorAuthorityEpoch)
        {

            return new Error(
                ErrorCodes.Covenant.StaleSnapshot,
                "Operator authority changed after this mutation was prepared.");

        }

        Result<CovenantMutationIntent> intent = compiled is null
            ? CovenantOperatorMutationFactory.Retire(
                mutationId,
                scope,
                normalizedKey,
                normalizedKey,
                lane,
                expectedRevision,
                Binding(body.Value),
                body.Value.Digest())
            : CovenantOperatorMutationFactory.Set(
                mutationId,
                scope,
                compiled,
                expectedRevision,
                reactivate,
                Binding(body.Value),
                body.Value.Digest());

        if (intent.IsFailure)
        {

            return intent.Error;

        }

        return await CommitAsync(intent.Value, body.Value, scope, normalizedKey, lane, cancellationToken)
            .ConfigureAwait(false);

    }

    private async ValueTask<Result<CovenantMutationResultDto>> CommitAsync(
        CovenantMutationIntent intent,
        CovenantOperatorPreflightBody body,
        CovenantOperationScope scope,
        string normalizedKey,
        CovenantLane lane,
        CancellationToken cancellationToken)
    {

        SqliteConnection connection = await connections
            .GetOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);

        CovenantMutationBatch batch = new(
            body.DatasetGeneration,
            checked((long)body.KeyReclamationEpoch),
            body.CampaignRegistryEpoch is { } bodyRegistryEpoch ? checked((long)bodyRegistryEpoch) : null,
            timeProvider.GetUtcNow(),
            [intent]);

        Result<IReadOnlyList<CovenantMutationReceipt>> applied = await kernel
            .ApplyBatchAsync(
                batch,
                new CovenantMutationTransaction(connection, transaction),
                cancellationToken)
            .ConfigureAwait(false);

        if (applied.IsFailure)
        {

            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return applied.Error;

        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        CovenantMutationReceipt receipt = applied.Value[0];

        return new CovenantMutationResultDto(
            receipt.MutationId,
            receipt.Outcome,
            receipt.Kind is CovenantMutationKind.OperatorRetire or CovenantMutationKind.AgentRetire
                ? CovenantOperation.Retire
                : CovenantOperation.Set,
            scope.CampaignId is null ? CovenantScope.Global : CovenantScope.Campaign,
            scope.CampaignId,
            normalizedKey,
            lane,
            receipt.EntryId,
            receipt.ResultingVersionId,
            receipt.ResultingLaneRevision,
            Hex(receipt.RequestIdempotencyDigest),
            Hex(receipt.ResponseReceiptDigest),
            receipt.Replayed);

    }

    /// <summary>
    /// Resolves an already-committed mutation identity, or reports that this is a new request.
    /// </summary>
    /// <remarks>
    /// Keyed on the mutation identity alone, and compared against the request digest recomputed from
    /// the commit's own fields — neither of which needs the token. That is what lets a replay succeed
    /// after the token that authorized it has expired, while a different request reusing the same
    /// identity is still an idempotency conflict rather than a second commit.
    /// </remarks>
    private async ValueTask<Result<CovenantMutationResultDto?>> TryReplayAsync(
        Guid mutationId,
        CovenantDigest requestDigest,
        CovenantOperationScope scope,
        string normalizedKey,
        CovenantLane lane,
        CovenantOperation operation,
        CancellationToken cancellationToken)
    {

        SqliteConnection connection = await connections
            .GetOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT r.RequestIdempotencyDigest, r.ResponseReceiptDigest, r.OutcomeCode,
                   r.ResultingVersionId, r.ResultingLaneRevision,
                   COALESCE(
                       (SELECT v.EntryId FROM covenant_versions v WHERE v.VersionId = r.ResultingVersionId),
                       (SELECT e.EntryId FROM covenant_entries e
                        WHERE e.ScopeCode = r.ScopeCode AND e.CampaignId IS r.CampaignId
                          AND e.NormalizedKey = $key))
            FROM covenant_mutation_receipts r
            WHERE r.MutationId = $mutation;
            """;

        _ = command.Parameters.AddWithValue("$mutation", mutationId.ToString("D"));

        _ = command.Parameters.AddWithValue("$key", normalizedKey);

        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            return Result<CovenantMutationResultDto?>.Success(null);

        }

        CovenantDigest storedRequest = new((byte[])reader.GetValue(0));

        if (storedRequest != requestDigest)
        {

            return Result<CovenantMutationResultDto?>.Failure(new Error(
                ErrorCodes.Security.IdempotencyConflict,
                "This mutation identity was already used for a different Covenant request."));

        }

        return Result<CovenantMutationResultDto?>.Success(new CovenantMutationResultDto(
            mutationId,
            (CovenantMutationOutcome)reader.GetInt64(2),
            operation,
            scope.CampaignId is null ? CovenantScope.Global : CovenantScope.Campaign,
            scope.CampaignId,
            normalizedKey,
            lane,
            reader.IsDBNull(5) ? Guid.Empty : Guid.Parse(reader.GetString(5)),
            reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            Hex(storedRequest),
            Hex(new CovenantDigest((byte[])reader.GetValue(1))),
            Replayed: true));

    }

    private static CovenantOperatorMutationBinding Binding(CovenantOperatorPreflightBody body) =>
        new(
            body.DatasetGeneration,
            body.OperatorAuthorityEpoch,
            checked((long)body.NormalizedKeyDependencyEpoch),
            body.CampaignRegistryEpoch is { } registry ? checked((long)registry) : null);

    /// <summary>
    /// The request identity both halves of the protocol compute the same way.
    /// </summary>
    /// <remarks>
    /// Deliberately derived through the same factory the commit uses, so a change to what a request
    /// means cannot alter one side and leave the other issuing tokens against the old meaning.
    /// </remarks>
    private static CovenantDigest RequestDigest(
        CovenantOperationScope scope,
        string normalizedKey,
        CovenantLane lane,
        CovenantOperation operation,
        Guid mutationId,
        long expectedRevision,
        bool reactivate,
        CovenantCompiledContent? compiled) =>
        operation is CovenantOperation.Retire
            ? CovenantOperatorMutationFactory
                .Retire(
                    mutationId,
                    scope,
                    normalizedKey,
                    normalizedKey,
                    lane,
                    Math.Max(expectedRevision, 1),
                    PlaceholderBinding,
                    PlaceholderDigest)
                .Value.Authorization.RequestIdempotencyDigest
            : CovenantOperatorMutationFactory
                .Set(
                    mutationId,
                    scope,
                    compiled!,
                    expectedRevision,
                    reactivate,
                    PlaceholderBinding,
                    PlaceholderDigest)
                .Value.Authorization.RequestIdempotencyDigest;

    /// <summary>
    /// The binding used only to reach the request digest, which does not depend on it.
    /// </summary>
    /// <remarks>
    /// The request digest covers what the operator asked for; the authorization digest covers what
    /// the installation was when they asked. Only the second needs real epochs, so computing the
    /// first before the epochs are known is sound — and the constants here never reach a durable row,
    /// because every committed intent is rebuilt with the token's real binding.
    /// </remarks>
    private static readonly CovenantOperatorMutationBinding PlaceholderBinding =
        new(Guid.Parse("00000000-0000-4000-8000-000000000001"), 1, 1, 1);

    private static readonly CovenantDigest PlaceholderDigest = new(new byte[32]);

    private Result<ulong> ResolveAuthorityEpoch() =>
        authority.Current is { } snapshot && snapshot.AuthorityEpoch > 0
            ? Result<ulong>.Success(checked((ulong)snapshot.AuthorityEpoch))
            : Result<ulong>.Failure(new Error(
                ErrorCodes.Covenant.OperatorAuthorityUnavailable,
                "This installation has no established Covenant authority to mutate under."));

    /// <summary>
    /// Compiles authored content, or reports why it is not admissible.
    /// </summary>
    /// <remarks>
    /// The compiler throws on content the Unicode and grammar policies refuse, which is right for a
    /// Core invariant and wrong for a wire surface: an operator who pasted a control character should
    /// receive a typed refusal, not a 500.
    /// </remarks>
    private Result<CovenantCompiledContent> TryCompile(string key, string content)
    {

        try
        {

            return Result<CovenantCompiledContent>.Success(compiler.Compile(key, content));

        }
        catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
        {

            return Result<CovenantCompiledContent>.Failure(new Error(
                ErrorCodes.Covenant.InvalidContent,
                exception.Message));

        }

    }

    private static CovenantOperationScope Scope(CovenantScope kind, Guid? campaignId) =>
        kind is CovenantScope.Global || campaignId is not { } id
            ? CovenantOperationScope.Global
            : CovenantOperationScope.ForCampaign(id);

    /// <summary>
    /// The identity of what this mutation would do.
    /// </summary>
    /// <remarks>
    /// The dependent-head vector, unchanged. That vector is the complete set of heads whose state
    /// decides the outcome, so it already <em>is</em> the effect's identity; deriving a second digest
    /// from a truncated example list would produce a value that looked independent and was not.
    /// </remarks>
    private static CovenantDigest EffectDigest(CovenantMutationEffectSnapshot effect) =>
        effect.DependentHeadVectorDigest;

    private static CovenantMutationEffectDto EffectDto(
        CovenantMutationEffectSnapshot effect,
        CovenantOperationScope scope,
        CovenantCompiledContent? compiled) =>
        new(
            effect.LocalDecision,
            effect.AffectedCampaignCount,
            [.. effect.Examples.Select(static example => new CovenantMutationEffectExampleDto(
                example.CampaignId,
                example.Decision,
                example.HasCampaignConfirmedHead,
                example.HasCampaignProposedHead))],
            effect.ExamplesTruncated,
            AppliesToFutureCampaigns: scope.CampaignId is null,
            GlobalConfirmedResurfaces: effect.LocalDecision is CovenantEffectDecision.GlobalConfirmedResurfaces,
            ProposedBecomesEligible: effect.Lane is CovenantLane.Proposed
                && effect.LocalDecision is CovenantEffectDecision.ProposedBecomesEligible,
            ProposedRemainsReviewOnly: effect.Lane is CovenantLane.Proposed
                && effect.LocalDecision is not CovenantEffectDecision.ProposedBecomesEligible,
            compiled?.FragmentUtf8ByteCount ?? 0,
            CovenantLimits.MaxGlobalConfirmedRenderedBytes,
            Hex(effect.DependentHeadVectorDigest),
            Hex(EffectDigest(effect)));

    private static string Hex(CovenantDigest digest) => Convert.ToHexStringLower(digest.Bytes);

}
