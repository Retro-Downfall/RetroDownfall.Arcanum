using System.Buffers.Binary;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.AI;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Annals;
using RetroDownfall.Arcanum.Infrastructure.Weave;

namespace RetroDownfall.Arcanum.Infrastructure.Memory;

internal sealed class SagaMemoryReviewService(
    ArcanumDbContext db,
    IWeaveService weave,
    IMemoryReviewTokenCodec tokenCodec,
    WeaveIndexAvailability availability,
    IOptionsMonitor<ArcanumSettings> options,
    TimeProvider timeProvider) : ISagaMemoryReviewService
{
    private static readonly Error InvalidToken = new(
        ErrorCodes.MemoryReview.InvalidToken,
        "The memory-review token is invalid, expired, stale, or bound to another queue.");

    private static readonly Error StaleObservation = new(
        ErrorCodes.MemoryReview.StaleObservation,
        "A reviewed Saga version or its review position changed before this operation could commit.");

    private static readonly Error UnseenObservation = new(
        ErrorCodes.MemoryReview.UnseenObservation,
        "A review decision did not identify an event observed in this exact Saga queue.");

    private static readonly Error IntegrityFailure = new(
        ErrorCodes.MemoryReview.IntegrityFailure,
        "Durable memory-review state failed its integrity contract.");

    private static readonly Error RequestReuse = new(
        ErrorCodes.MemoryReview.RequestReuse,
        "This memory-review request identity already belongs to a different decision set.");

    public async Task<Result<SagaReviewPageDto>> ListAsync(
        SagaReviewListRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Result validation = request.Validate();

        if (validation.IsFailure)
        {
            return Result<SagaReviewPageDto>.Failure(validation.Error);
        }

        MemoryReviewDigest scopeDigest = ScopeDigest(request.ScopeKind, request.CampaignId);

        Result<MemoryReviewCursorTokenFacts>? decodedCursor = request.Cursor is null
            ? null
            : tokenCodec.ReadCursor(request.Cursor);

        if (decodedCursor is { IsFailure: true } invalidCursor)
        {
            return Result<SagaReviewPageDto>.Failure(invalidCursor.Error);
        }

        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbTransaction transaction = await connection
                    .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                ReviewMarker marker = await ReadOrCreateMarkerAsync(
                    connection,
                    transaction,
                    request.ScopeKind,
                    request.CampaignId,
                    cancellationToken).ConfigureAwait(false);

                ulong lower;

                ulong upper;

                ulong? keyset;

                if (decodedCursor is { } cursorResult)
                {
                    MemoryReviewCursorTokenFacts cursor = cursorResult.Value;

                    if (!MatchesCommon(cursor, scopeDigest, marker))
                    {
                        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

                        return Result<SagaReviewPageDto>.Failure(InvalidToken);
                    }

                    bool keysetExists = await EventIdentityMatchesAsync(
                        connection,
                        transaction,
                        request.ScopeKind,
                        request.CampaignId,
                        cursor.KeysetEventSequence,
                        cursor.KeysetVersionIdentity,
                        cancellationToken).ConfigureAwait(false);

                    if (!keysetExists)
                    {
                        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

                        return Result<SagaReviewPageDto>.Failure(InvalidToken);
                    }

                    lower = cursor.FrozenLowerEventSequence;

                    upper = cursor.FrozenUpperEventSequence;

                    keyset = cursor.KeysetEventSequence;
                }
                else
                {
                    lower = checked((ulong)marker.ReviewedThroughSequence + 1UL);

                    long frozenUpper = await ReadUpperFrontierAsync(
                        connection,
                        transaction,
                        request.ScopeKind,
                        request.CampaignId,
                        cancellationToken).ConfigureAwait(false);

                    upper = frozenUpper <= 0 ? 0UL : checked((ulong)frozenUpper);

                    keyset = null;
                }

                List<SagaReviewEvent> rows = upper < lower
                    ? []
                    : await ReadPageAsync(
                        connection,
                        transaction,
                        request.ScopeKind,
                        request.CampaignId,
                        lower,
                        upper,
                        keyset,
                        request.Limit + 1,
                        cancellationToken).ConfigureAwait(false);

                bool truncated = rows.Count > request.Limit;

                if (truncated)
                {
                    rows.RemoveAt(rows.Count - 1);
                }

                SagaReviewItemDto[] items = new SagaReviewItemDto[rows.Count];

                for (int index = 0; index < rows.Count; index++)
                {
                    SagaReviewEvent row = rows[index];

                    SagaReviewCurrentDto? current = await ReadCurrentSnapshotAsync(
                        connection,
                        transaction,
                        row.SubjectId,
                        cancellationToken).ConfigureAwait(false);

                    if (current?.Claim is not { } claim
                        || claim.CurrentVersionId != row.VersionId
                        || claim.CurrentRevision != row.Revision
                        || claim.CurrentOperation != row.Operation
                        || current.Memory.ScopeKind != row.ScopeKind
                        || current.Memory.ScopeCampaignId != row.CampaignId
                        || (row.Operation == AnnalOperation.Retire
                            ? row.ContentHash is not null
                            : !string.Equals(current.ContentHash, row.ContentHash, StringComparison.Ordinal)))
                    {
                        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

                        return Result<SagaReviewPageDto>.Failure(IntegrityFailure);
                    }

                    Result<string> observation = tokenCodec.IssueObservation(
                        new MemoryReviewObservationTokenFacts(
                            MemoryReviewStore.Saga,
                            scopeDigest,
                            marker.Generation,
                            checked((ulong)marker.Revision),
                            lower,
                            upper,
                            checked((ulong)row.Sequence),
                            VersionIdentity(row.VersionId)));

                    if (observation.IsFailure)
                    {
                        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

                        return Result<SagaReviewPageDto>.Failure(observation.Error);
                    }

                    items[index] = new SagaReviewItemDto(
                        row.Sequence,
                        row.SubjectId,
                        row.VersionId,
                        row.Revision,
                        row.Operation,
                        row.Origin,
                        row.ScopeKind,
                        row.CampaignId,
                        row.SourceSessionId,
                        row.Source,
                        row.ContentHash,
                        row.IsCurrent,
                        current,
                        observation.Value);
                }

                string? nextCursor = null;

                if (truncated && rows.Count > 0)
                {
                    SagaReviewEvent last = rows[^1];

                    Result<string> issuedCursor = tokenCodec.IssueCursor(
                        new MemoryReviewCursorTokenFacts(
                            MemoryReviewStore.Saga,
                            scopeDigest,
                            marker.Generation,
                            checked((ulong)marker.Revision),
                            lower,
                            upper,
                            checked((ulong)last.Sequence),
                            VersionIdentity(last.VersionId)));

                    if (issuedCursor.IsFailure)
                    {
                        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

                        return Result<SagaReviewPageDto>.Failure(issuedCursor.Error);
                    }

                    nextCursor = issuedCursor.Value;
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return Result<SagaReviewPageDto>.Success(
                    new SagaReviewPageDto(
                        items,
                        marker.ReviewedThroughSequence,
                        checked((long)upper),
                        nextCursor,
                        truncated));
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<MemoryReviewBulkPlanDto>> PrepareAsync(
        SagaReviewBulkPrepareRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Result validation = request.Validate();

        if (validation.IsFailure)
        {
            return Result<MemoryReviewBulkPlanDto>.Failure(validation.Error);
        }

        MemoryReviewDigest scopeDigest = ScopeDigest(request.ScopeKind, request.CampaignId);

        Result<MemoryReviewObservationTokenFacts[]> observations = DecodeObservations(request, scopeDigest);

        if (observations.IsFailure)
        {
            return Result<MemoryReviewBulkPlanDto>.Failure(observations.Error);
        }

        MemoryReviewObservationTokenFacts first = observations.Value[0];

        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbTransaction transaction = await connection
                    .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                ReviewMarker? marker = await ReadMarkerAsync(
                    connection,
                    transaction,
                    request.ScopeKind,
                    request.CampaignId,
                    cancellationToken).ConfigureAwait(false);

                if (marker is null || !MatchesCommon(first, scopeDigest, marker))
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

                    return Result<MemoryReviewBulkPlanDto>.Failure(StaleObservation);
                }

                MemoryReviewBulkPlanItemDto[] items = new MemoryReviewBulkPlanItemDto[request.Decisions.Length];

                for (int index = 0; index < request.Decisions.Length; index++)
                {
                    MemoryReviewObservationTokenFacts observed = observations.Value[index];

                    SagaReviewEvent? row = await ReadEventAsync(
                        connection,
                        transaction,
                        request.ScopeKind,
                        request.CampaignId,
                        checked((long)observed.EventSequence),
                        cancellationToken).ConfigureAwait(false);

                    Error? refusal = ValidateObservedTarget(request.Action, request.Decisions[index], observed, row);

                    if (refusal is { } error)
                    {
                        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

                        return Result<MemoryReviewBulkPlanDto>.Failure(error);
                    }

                    items[index] = PlanItem(row!);
                }

                MemoryReviewDigest requestDigest = OrderedRequestDigest(request);

                Result<string> preparedToken = tokenCodec.IssuePreparedPlan(
                    new MemoryReviewPreparedPlanTokenFacts(
                        MemoryReviewStore.Saga,
                        scopeDigest,
                        first.MarkerGeneration,
                        first.MarkerRevision,
                        first.FrozenLowerEventSequence,
                        first.FrozenUpperEventSequence,
                        requestDigest));

                if (preparedToken.IsFailure)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

                    return Result<MemoryReviewBulkPlanDto>.Failure(preparedToken.Error);
                }

                DateTimeOffset issuedAt = timeProvider.GetUtcNow();

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return Result<MemoryReviewBulkPlanDto>.Success(
                    new MemoryReviewBulkPlanDto(
                        MemoryReviewStore.Saga,
                        request.RequestId,
                        request.Action,
                        items,
                        issuedAt,
                        issuedAt + MemoryReviewLimits.TokenLifetime,
                        preparedToken.Value));
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<MemoryReviewBulkResultDto>> ApplyAsync(
        SagaReviewBulkApplyRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Result validation = request.Validate();

        if (validation.IsFailure)
        {
            return Result<MemoryReviewBulkResultDto>.Failure(validation.Error);
        }

        SagaReviewBulkPrepareRequest preparedRequest = request.Request;

        MemoryReviewDigest requestDigest = OrderedRequestDigest(preparedRequest);

        ReplayLookup replay = await ReadReplayAsync(
            preparedRequest,
            requestDigest,
            cancellationToken).ConfigureAwait(false);

        if (replay.Error is { } replayError)
        {
            return Result<MemoryReviewBulkResultDto>.Failure(replayError);
        }

        if (replay.Result is { } replayed)
        {
            return Result<MemoryReviewBulkResultDto>.Success(replayed);
        }

        MemoryReviewDigest scopeDigest = ScopeDigest(preparedRequest.ScopeKind, preparedRequest.CampaignId);

        Result<MemoryReviewPreparedPlanTokenFacts> decodedPlan = tokenCodec.ReadPreparedPlan(
            request.PreparedPlanToken);

        if (decodedPlan.IsFailure)
        {
            return Result<MemoryReviewBulkResultDto>.Failure(decodedPlan.Error);
        }

        Result<MemoryReviewObservationTokenFacts[]> decodedObservations = DecodeObservations(
            preparedRequest,
            scopeDigest);

        if (decodedObservations.IsFailure)
        {
            return Result<MemoryReviewBulkResultDto>.Failure(decodedObservations.Error);
        }

        MemoryReviewPreparedPlanTokenFacts plan = decodedPlan.Value;

        MemoryReviewObservationTokenFacts firstObservation = decodedObservations.Value[0];

        if (!MatchesPreparedPlan(plan, firstObservation, scopeDigest, requestDigest))
        {
            return Result<MemoryReviewBulkResultDto>.Failure(InvalidToken);
        }

        Result<float[][]> embeddings = await PrecomputeEmbeddingsAsync(
            preparedRequest,
            cancellationToken).ConfigureAwait(false);

        if (embeddings.IsFailure)
        {
            return Result<MemoryReviewBulkResultDto>.Failure(embeddings.Error);
        }

        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbTransaction transaction = await BeginWriteTransactionAsync(
                    connection,
                    cancellationToken).ConfigureAwait(false);

                ReplayLookup racedReplay = await ReadReplayAsync(
                    connection,
                    transaction,
                    preparedRequest,
                    requestDigest,
                    cancellationToken).ConfigureAwait(false);

                if (racedReplay.Error is { } racedError)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

                    return Result<MemoryReviewBulkResultDto>.Failure(racedError);
                }

                if (racedReplay.Result is { } racedResult)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

                    return Result<MemoryReviewBulkResultDto>.Success(racedResult);
                }

                ReviewMarker? marker = await ReadMarkerAsync(
                    connection,
                    transaction,
                    preparedRequest.ScopeKind,
                    preparedRequest.CampaignId,
                    cancellationToken).ConfigureAwait(false);

                if (marker is null || !MatchesCommon(plan, scopeDigest, marker))
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

                    return Result<MemoryReviewBulkResultDto>.Failure(StaleObservation);
                }

                SagaReviewEvent[] targets = new SagaReviewEvent[preparedRequest.Decisions.Length];

                for (int index = 0; index < preparedRequest.Decisions.Length; index++)
                {
                    MemoryReviewObservationTokenFacts observed = decodedObservations.Value[index];

                    SagaReviewEvent? row = await ReadEventAsync(
                        connection,
                        transaction,
                        preparedRequest.ScopeKind,
                        preparedRequest.CampaignId,
                        checked((long)observed.EventSequence),
                        cancellationToken).ConfigureAwait(false);

                    Error? refusal = ValidateObservedTarget(
                        preparedRequest.Action,
                        preparedRequest.Decisions[index],
                        observed,
                        row);

                    if (refusal is { } error)
                    {
                        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

                        return Result<MemoryReviewBulkResultDto>.Failure(error);
                    }

                    targets[index] = row!;
                }

                DateTimeOffset changedAt = timeProvider.GetUtcNow();

                AppliedDecision[] applied = new AppliedDecision[targets.Length];

                for (int index = 0; index < targets.Length; index++)
                {
                    Result<AppliedDecision> decision = await ApplyDecisionAsync(
                        connection,
                        transaction,
                        preparedRequest.Action,
                        index,
                        targets[index],
                        preparedRequest.Decisions[index],
                        embeddings.Value[index],
                        changedAt,
                        cancellationToken).ConfigureAwait(false);

                    if (decision.IsFailure)
                    {
                        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

                        return Result<MemoryReviewBulkResultDto>.Failure(decision.Error);
                    }

                    applied[index] = decision.Value;
                }

                HashSet<long> acknowledged = applied
                    .SelectMany(static decision => decision.ReplacementEventSequence is { } replacement
                        ? new[] { decision.Target.Sequence, replacement }
                        : [decision.Target.Sequence])
                    .ToHashSet();

                long reviewedThrough = await ComputeReviewedThroughAsync(
                    connection,
                    transaction,
                    preparedRequest.ScopeKind,
                    preparedRequest.CampaignId,
                    marker.ReviewedThroughSequence,
                    acknowledged,
                    cancellationToken).ConfigureAwait(false);

                for (int index = 0; index < applied.Length; index++)
                {
                    await InsertDecisionReceiptAsync(
                        connection,
                        transaction,
                        preparedRequest,
                        requestDigest,
                        applied[index],
                        reviewedThrough,
                        cancellationToken).ConfigureAwait(false);
                }

                int markerWrites = await AdvanceMarkerAsync(
                    connection,
                    transaction,
                    preparedRequest.ScopeKind,
                    preparedRequest.CampaignId,
                    marker,
                    reviewedThrough,
                    cancellationToken).ConfigureAwait(false);

                if (markerWrites != 1)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

                    return Result<MemoryReviewBulkResultDto>.Failure(StaleObservation);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                MemoryReviewBulkItemResultDto[] items = applied
                    .Select(static decision => decision.Result)
                    .ToArray();

                return Result<MemoryReviewBulkResultDto>.Success(
                    new MemoryReviewBulkResultDto(
                        MemoryReviewStore.Saga,
                        preparedRequest.RequestId,
                        preparedRequest.Action,
                        items,
                        reviewedThrough,
                        Replayed: false));
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<float[][]>> PrecomputeEmbeddingsAsync(
        SagaReviewBulkPrepareRequest request,
        CancellationToken cancellationToken)
    {
        float[][] embeddings = new float[request.Decisions.Length][];

        if (request.Action != MemoryReviewAction.Correct)
        {
            for (int index = 0; index < embeddings.Length; index++)
            {
                embeddings[index] = [];
            }

            return Result<float[][]>.Success(embeddings);
        }

        if (!weave.IsAvailable)
        {
            return Result<float[][]>.Failure(EmbeddingUnavailableError());
        }

        int expectedDimensions = ArcanumSettingClamps.EmbeddingsDimensions(
            options.CurrentValue.Integrations.Embeddings.Dimensions);

        for (int index = 0; index < request.Decisions.Length; index++)
        {
            Result<Embedding<float>> embedded = await weave.EmbedAsync(
                request.Decisions[index].ReplacementContent!,
                cancellationToken).ConfigureAwait(false);

            if (embedded.IsFailure)
            {
                return Result<float[][]>.Failure(EmbeddingUnavailableError());
            }

            float[] vector = embedded.Value.Vector.ToArray();

            if (vector.Length != expectedDimensions)
            {
                return Result<float[][]>.Failure(EmbeddingUnavailableError());
            }

            embeddings[index] = vector;
        }

        return Result<float[][]>.Success(embeddings);
    }

    private static Error EmbeddingUnavailableError() =>
        new(
            ErrorCodes.Saga.EmbeddingUnavailable,
            "The embedding substrate cannot produce every correction vector before the review write begins.");

    private static async ValueTask<DbTransaction> BeginWriteTransactionAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        if (connection is SqliteConnection sqlite)
        {
            return sqlite.BeginTransaction(deferred: false);
        }

        return await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<AppliedDecision>> ApplyDecisionAsync(
        DbConnection connection,
        DbTransaction transaction,
        MemoryReviewAction action,
        int ordinal,
        SagaReviewEvent target,
        SagaReviewDecision decision,
        float[] embedding,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken)
    {
        string? resultingVersionId = null;

        long? replacementEventSequence = null;

        string outcome = Outcome(action);

        switch (action)
        {
            case MemoryReviewAction.Confirm:
                break;

            case MemoryReviewAction.Correct:
                byte[] current = AnnalContentDigest.ForSagaMemory(target.CurrentContent!);

                byte[] replacement = AnnalContentDigest.ForSagaMemory(decision.ReplacementContent!);

                if (CryptographicOperations.FixedTimeEquals(current, replacement))
                {
                    outcome = nameof(SagaCurationOutcomeKind.Unchanged);

                    break;
                }

                resultingVersionId = await CorrectAsync(
                    connection,
                    transaction,
                    target,
                    decision.ReplacementContent!,
                    embedding,
                    changedAt,
                    cancellationToken).ConfigureAwait(false);

                if (resultingVersionId is null)
                {
                    return Result<AppliedDecision>.Failure(IntegrityFailure);
                }

                replacementEventSequence = await ReadReviewEventSequenceAsync(
                    connection,
                    transaction,
                    resultingVersionId,
                    cancellationToken).ConfigureAwait(false);

                break;

            case MemoryReviewAction.Retire:
                if (target.RetiredAtUtc is null)
                {
                    resultingVersionId = await RetireAsync(
                        connection,
                        transaction,
                        target,
                        changedAt,
                        cancellationToken).ConfigureAwait(false);

                    if (resultingVersionId is null)
                    {
                        return Result<AppliedDecision>.Failure(IntegrityFailure);
                    }
                }
                else
                {
                    outcome = nameof(SagaCurationOutcomeKind.AlreadyRetired);
                }

                break;

            case MemoryReviewAction.Pin:
                await SetPinAsync(
                    connection,
                    transaction,
                    target.SubjectId,
                    changedAt,
                    cancellationToken).ConfigureAwait(false);

                break;

            case MemoryReviewAction.Unpin:
                await SetPinAsync(
                    connection,
                    transaction,
                    target.SubjectId,
                    changedAt: null,
                    cancellationToken).ConfigureAwait(false);

                break;

            default:
                return Result<AppliedDecision>.Failure(
                    new Error(ErrorCodes.Validation.InvalidBody, "A recognized memory review action is required."));
        }

        MemoryReviewBulkItemResultDto result = new(
            target.Sequence,
            target.SubjectId,
            target.VersionId,
            outcome,
            resultingVersionId);

        return Result<AppliedDecision>.Success(
            new AppliedDecision(ordinal, target, result, replacementEventSequence));
    }

    private async Task<string?> CorrectAsync(
        DbConnection connection,
        DbTransaction transaction,
        SagaReviewEvent target,
        string content,
        float[] embedding,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            transaction,
            cancellationToken,
            "UPDATE saga_memories SET Content = @content WHERE Id = @id",
            ("@content", content),
            ("@id", target.SubjectId)).ConfigureAwait(false);

        byte[] blob = EmbeddingBlobCodec.Encode(embedding);

        await ExecuteAsync(
            connection,
            transaction,
            cancellationToken,
            """
            INSERT OR REPLACE INTO saga_memory_embeddings (MemoryId, Embedding, Dim)
            VALUES (@id, @embedding, @dimensions)
            """,
            ("@id", target.SubjectId),
            ("@embedding", blob),
            ("@dimensions", embedding.Length)).ConfigureAwait(false);

        if (availability.IsVecAvailable)
        {
            await ExecuteAsync(
                connection,
                transaction,
                cancellationToken,
                """
                INSERT OR REPLACE INTO saga_memory_embeddings_vec (MemoryId, Embedding)
                VALUES (@id, @embedding)
                """,
                ("@id", target.SubjectId),
                ("@embedding", blob)).ConfigureAwait(false);
        }

        return await AnnalsClaimWriter.AppendCorrectionAsync(
            connection,
            transaction,
            AnnalSubjectStore.Saga,
            target.SubjectId,
            AnnalOrigin.OperatorStated,
            target.ScopeKind,
            CanonicalCampaign(target.CampaignId),
            ContentSensitivity.None,
            AnnalContentHashFormat.LegacyStoreDigest,
            AnnalContentDigest.ForSagaMemory(content),
            changedAt,
            changedAt,
            sourceSessionId: null,
            cancellationToken,
            legacySchema: true).ConfigureAwait(false);
    }

    private async Task<string?> RetireAsync(
        DbConnection connection,
        DbTransaction transaction,
        SagaReviewEvent target,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            transaction,
            cancellationToken,
            "UPDATE saga_memories SET RetiredAtUtc = @retiredAt WHERE Id = @id",
            ("@retiredAt", UtcInstantText.Format(changedAt)),
            ("@id", target.SubjectId)).ConfigureAwait(false);

        await ExecuteAsync(
            connection,
            transaction,
            cancellationToken,
            "DELETE FROM saga_memory_embeddings WHERE MemoryId = @id",
            ("@id", target.SubjectId)).ConfigureAwait(false);

        if (availability.IsVecAvailable)
        {
            await ExecuteAsync(
                connection,
                transaction,
                cancellationToken,
                "DELETE FROM saga_memory_embeddings_vec WHERE MemoryId = @id",
                ("@id", target.SubjectId)).ConfigureAwait(false);
        }

        byte[] suppressionKey = await SagaSuppressionKeyStore.ReadOrCreateAsync(
            connection,
            transaction,
            changedAt,
            cancellationToken).ConfigureAwait(false);

        byte[] suppressionDigest = SagaSuppressionDigest.Compute(
            suppressionKey,
            target.ScopeKind,
            CanonicalCampaign(target.CampaignId),
            target.CurrentContent!);

        await ExecuteAsync(
            connection,
            transaction,
            cancellationToken,
            """
            INSERT INTO saga_retirement_suppressions (
                SuppressionDigest, ScopeKindCode, CampaignId, RetiredAtUtc)
            VALUES (@digest, @scope, @campaign, @retiredAt)
            ON CONFLICT(SuppressionDigest) DO NOTHING
            """,
            ("@digest", suppressionDigest),
            ("@scope", (int)target.ScopeKind),
            ("@campaign", CanonicalCampaign(target.CampaignId)),
            ("@retiredAt", UtcInstantText.Format(changedAt))).ConfigureAwait(false);

        bool appended = await AnnalsClaimWriter.AppendRetirementAsync(
            connection,
            transaction,
            AnnalSubjectStore.Saga,
            target.SubjectId,
            AnnalOrigin.OperatorStated,
            target.ScopeKind,
            CanonicalCampaign(target.CampaignId),
            ContentSensitivity.None,
            changedAt,
            changedAt,
            sourceSessionId: null,
            cancellationToken).ConfigureAwait(false);

        if (!appended)
        {
            return null;
        }

        await using DbCommand head = connection.CreateCommand();

        head.Transaction = transaction;

        head.CommandText =
            """
            SELECT CurrentVersionId
            FROM annal_heads AS head
            JOIN annal_claims AS claim ON claim.ClaimId = head.ClaimId
            WHERE claim.SubjectStoreCode = @store AND claim.SubjectId = @id
            """;

        AddParameter(head, "@store", (int)AnnalSubjectStore.Saga);

        AddParameter(head, "@id", target.SubjectId);

        return await head.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    private static Task SetPinAsync(
        DbConnection connection,
        DbTransaction transaction,
        string subjectId,
        DateTimeOffset? changedAt,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            transaction,
            cancellationToken,
            "UPDATE saga_memories SET PinnedAtUtc = @pinnedAt WHERE Id = @id",
            ("@pinnedAt", changedAt is null ? null : UtcInstantText.Format(changedAt.Value)),
            ("@id", subjectId));

    private static async Task<long> ReadReviewEventSequenceAsync(
        DbConnection connection,
        DbTransaction transaction,
        string versionId,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "SELECT Sequence FROM annal_review_events WHERE VersionId = @version";

        AddParameter(command, "@version", versionId);

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null
            ? throw new InvalidDataException("A Saga head update did not produce its review event.")
            : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static string Outcome(MemoryReviewAction action) =>
        action switch
        {
            MemoryReviewAction.Confirm => "Confirmed",
            MemoryReviewAction.Correct => "Corrected",
            MemoryReviewAction.Retire => "Retired",
            MemoryReviewAction.Pin => "Pinned",
            MemoryReviewAction.Unpin => "Unpinned",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

    private static bool IsExpectedOutcome(MemoryReviewAction action, string outcome) =>
        string.Equals(outcome, Outcome(action), StringComparison.Ordinal)
        || action == MemoryReviewAction.Correct
            && string.Equals(outcome, nameof(SagaCurationOutcomeKind.Unchanged), StringComparison.Ordinal)
        || action == MemoryReviewAction.Retire
            && string.Equals(outcome, nameof(SagaCurationOutcomeKind.AlreadyRetired), StringComparison.Ordinal);

    private static async Task<long> ComputeReviewedThroughAsync(
        DbConnection connection,
        DbTransaction transaction,
        SagaMemoryScopeKind scopeKind,
        Guid? campaignId,
        long current,
        HashSet<long> acknowledged,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
            """
            SELECT event.Sequence,
                   EXISTS (
                       SELECT 1 FROM annal_review_decision_receipts AS receipt
                       WHERE receipt.ReviewEventSequence = event.Sequence),
                   EXISTS (
                       SELECT 1 FROM annal_heads AS current_head
                       WHERE current_head.ClaimId = event.ClaimId
                         AND current_head.CurrentVersionId <> event.VersionId)
            FROM annal_review_events AS event
            WHERE event.SubjectStoreCode = @store
              AND event.ScopeKindCode = @scope
              AND ((@campaign IS NULL AND event.CampaignId IS NULL) OR event.CampaignId = @campaign)
              AND event.Sequence > @current
            ORDER BY event.Sequence
            """;

        AddParameter(command, "@store", (int)AnnalSubjectStore.Saga);

        AddParameter(command, "@scope", (int)scopeKind);

        AddParameter(command, "@campaign", CanonicalCampaign(campaignId));

        AddParameter(command, "@current", current);

        long frontier = current;

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            long sequence = reader.GetInt64(0);

            bool reviewed = reader.GetInt32(1) == 1
                || reader.GetInt32(2) == 1
                || acknowledged.Contains(sequence);

            if (!reviewed)
            {
                break;
            }

            frontier = sequence;
        }

        return frontier;
    }

    private static async Task<int> AdvanceMarkerAsync(
        DbConnection connection,
        DbTransaction transaction,
        SagaMemoryScopeKind scopeKind,
        Guid? campaignId,
        ReviewMarker expected,
        long reviewedThrough,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
            """
            UPDATE annal_review_markers
            SET ReviewedThroughSequence = @reviewedThrough,
                Revision = Revision + 1
            WHERE SubjectStoreCode = @store
              AND ScopeKindCode = @scope
              AND ((@campaign IS NULL AND CampaignId IS NULL) OR CampaignId = @campaign)
              AND MarkerGeneration = @generation
              AND Revision = @revision
              AND ReviewedThroughSequence = @expectedThrough
            """;

        AddParameter(command, "@reviewedThrough", reviewedThrough);

        AddParameter(command, "@store", (int)AnnalSubjectStore.Saga);

        AddParameter(command, "@scope", (int)scopeKind);

        AddParameter(command, "@campaign", CanonicalCampaign(campaignId));

        AddParameter(command, "@generation", GuidBytes(expected.Generation));

        AddParameter(command, "@revision", expected.Revision);

        AddParameter(command, "@expectedThrough", expected.ReviewedThroughSequence);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertDecisionReceiptAsync(
        DbConnection connection,
        DbTransaction transaction,
        SagaReviewBulkPrepareRequest request,
        MemoryReviewDigest requestDigest,
        AppliedDecision decision,
        long reviewedThrough,
        CancellationToken cancellationToken)
    {
        ReceiptEnvelope original = new(
            request.RequestId,
            decision.Ordinal,
            ReceiptKind.Original,
            request.Action,
            decision.Result.Outcome,
            reviewedThrough,
            decision.Result.ResultingVersionId);

        await InsertReceiptAsync(
            connection,
            transaction,
            original,
            decision.Target.Sequence,
            decision.Target.SubjectId,
            decision.Target.VersionId,
            requestDigest,
            cancellationToken).ConfigureAwait(false);

        if (decision.ReplacementEventSequence is not { } replacementSequence
            || decision.Result.ResultingVersionId is not { } resultingVersionId)
        {
            return;
        }

        ReceiptEnvelope replacement = original with { Kind = ReceiptKind.Replacement };

        await InsertReceiptAsync(
            connection,
            transaction,
            replacement,
            replacementSequence,
            decision.Target.SubjectId,
            resultingVersionId,
            requestDigest,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertReceiptAsync(
        DbConnection connection,
        DbTransaction transaction,
        ReceiptEnvelope envelope,
        long eventSequence,
        string subjectId,
        string versionId,
        MemoryReviewDigest requestDigest,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
            """
            INSERT INTO annal_review_decision_receipts (
                DecisionId, ReviewEventSequence, RequestIdempotencyDigest,
                DecisionCode, ResponseReceiptDigest)
            VALUES (@decision, @event, @requestDigest, 1, @responseDigest)
            """;

        AddParameter(command, "@decision", FormatEnvelope(envelope));

        AddParameter(command, "@event", eventSequence);

        AddParameter(
            command,
            "@requestDigest",
            PerDecisionRequestDigest(requestDigest, envelope.Ordinal, envelope.Kind));

        AddParameter(
            command,
            "@responseDigest",
            ResponseDigest(requestDigest, envelope, eventSequence, subjectId, versionId));

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static byte[] PerDecisionRequestDigest(
        MemoryReviewDigest requestDigest,
        int ordinal,
        ReceiptKind kind) =>
        DigestParts(
            "saga-review-receipt-request-v1",
            Convert.ToHexString(requestDigest.Bytes),
            ordinal.ToString(CultureInfo.InvariantCulture),
            ((int)kind).ToString(CultureInfo.InvariantCulture)).Bytes;

    private static byte[] ResponseDigest(
        MemoryReviewDigest requestDigest,
        ReceiptEnvelope envelope,
        long eventSequence,
        string subjectId,
        string versionId) =>
        DigestParts(
            "saga-review-receipt-response-v1",
            Convert.ToHexString(requestDigest.Bytes),
            FormatEnvelope(envelope),
            eventSequence.ToString(CultureInfo.InvariantCulture),
            subjectId,
            versionId).Bytes;

    private static string FormatEnvelope(ReceiptEnvelope envelope) =>
        string.Join(
            ':',
            envelope.RequestId.ToString("N", CultureInfo.InvariantCulture),
            envelope.Ordinal.ToString("D2", CultureInfo.InvariantCulture),
            envelope.Kind == ReceiptKind.Original ? "O" : "R",
            ((int)envelope.Action).ToString(CultureInfo.InvariantCulture),
            envelope.Outcome,
            envelope.ReviewedThrough.ToString(CultureInfo.InvariantCulture),
            envelope.ResultingVersionId ?? "-");

    private static MemoryReviewDigest DigestParts(params string?[] parts)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach (string? part in parts)
        {
            AppendHashPart(hash, part);
        }

        return new MemoryReviewDigest(hash.GetHashAndReset());
    }

    private async Task<ReplayLookup> ReadReplayAsync(
        SagaReviewBulkPrepareRequest request,
        MemoryReviewDigest requestDigest,
        CancellationToken cancellationToken)
    {
        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        return await ReadReplayAsync(
            connection,
            transaction: null,
            request,
            requestDigest,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ReplayLookup> ReadReplayAsync(
        DbConnection connection,
        DbTransaction? transaction,
        SagaReviewBulkPrepareRequest request,
        MemoryReviewDigest requestDigest,
        CancellationToken cancellationToken)
    {
        List<StoredReceipt> receipts = await ReadReceiptsAsync(
            connection,
            transaction,
            request.RequestId,
            cancellationToken).ConfigureAwait(false);

        if (receipts.Count == 0)
        {
            return ReplayLookup.None;
        }

        Dictionary<int, StoredReceipt> originals = [];

        Dictionary<int, StoredReceipt> replacements = [];

        long? reviewedThrough = null;

        foreach (StoredReceipt stored in receipts)
        {
            if (stored.IsCorrupt)
            {
                return ReplayLookup.Failed(IntegrityFailure);
            }

            ReceiptEnvelope envelope = stored.Envelope;

            if (envelope.RequestId != request.RequestId
                || envelope.Action != request.Action
                || envelope.Ordinal < 0
                || envelope.Ordinal >= request.Decisions.Length
                || !IsExpectedOutcome(request.Action, envelope.Outcome))
            {
                return ReplayLookup.Failed(RequestReuse);
            }

            byte[] expectedRequestDigest = PerDecisionRequestDigest(
                requestDigest,
                envelope.Ordinal,
                envelope.Kind);

            if (!CryptographicOperations.FixedTimeEquals(
                    expectedRequestDigest,
                    stored.RequestIdempotencyDigest))
            {
                return ReplayLookup.Failed(RequestReuse);
            }

            if (reviewedThrough is { } expectedThrough && expectedThrough != envelope.ReviewedThrough)
            {
                return ReplayLookup.Failed(IntegrityFailure);
            }

            reviewedThrough ??= envelope.ReviewedThrough;

            Dictionary<int, StoredReceipt> destination = envelope.Kind == ReceiptKind.Original
                ? originals
                : replacements;

            if (!destination.TryAdd(envelope.Ordinal, stored))
            {
                return ReplayLookup.Failed(IntegrityFailure);
            }
        }

        if (originals.Count != request.Decisions.Length
            || request.Action != MemoryReviewAction.Correct && replacements.Count != 0)
        {
            return ReplayLookup.Failed(RequestReuse);
        }

        MemoryReviewBulkItemResultDto[] results = new MemoryReviewBulkItemResultDto[request.Decisions.Length];

        for (int ordinal = 0; ordinal < request.Decisions.Length; ordinal++)
        {
            if (!originals.TryGetValue(ordinal, out StoredReceipt? original))
            {
                return ReplayLookup.Failed(RequestReuse);
            }

            SagaReviewEvent? originalEvent = await ReadEventAsync(
                connection,
                transaction,
                request.ScopeKind,
                request.CampaignId,
                original.EventSequence,
                cancellationToken).ConfigureAwait(false);

            if (originalEvent is null)
            {
                return ReplayLookup.Failed(IntegrityFailure);
            }

            byte[] expectedResponse = ResponseDigest(
                requestDigest,
                original.Envelope,
                original.EventSequence,
                originalEvent.SubjectId,
                originalEvent.VersionId);

            if (!CryptographicOperations.FixedTimeEquals(expectedResponse, original.ResponseReceiptDigest))
            {
                return ReplayLookup.Failed(IntegrityFailure);
            }

            bool corrected = request.Action == MemoryReviewAction.Correct
                && string.Equals(original.Envelope.Outcome, Outcome(MemoryReviewAction.Correct), StringComparison.Ordinal);

            bool unchanged = request.Action == MemoryReviewAction.Correct
                && string.Equals(
                    original.Envelope.Outcome,
                    nameof(SagaCurationOutcomeKind.Unchanged),
                    StringComparison.Ordinal);

            bool retired = request.Action == MemoryReviewAction.Retire
                && string.Equals(original.Envelope.Outcome, Outcome(MemoryReviewAction.Retire), StringComparison.Ordinal);

            bool alreadyRetired = request.Action == MemoryReviewAction.Retire
                && string.Equals(
                    original.Envelope.Outcome,
                    nameof(SagaCurationOutcomeKind.AlreadyRetired),
                    StringComparison.Ordinal);

            if (corrected && original.Envelope.ResultingVersionId is null
                || unchanged && original.Envelope.ResultingVersionId is not null
                || retired && original.Envelope.ResultingVersionId is null
                || alreadyRetired
                    && (original.Envelope.ResultingVersionId is not null
                        || originalEvent.Operation != AnnalOperation.Retire))
            {
                return ReplayLookup.Failed(IntegrityFailure);
            }

            if (replacements.TryGetValue(ordinal, out StoredReceipt? replacement))
            {
                if (!corrected
                    || replacement.Envelope.ResultingVersionId is null
                    || replacement.Envelope.ResultingVersionId != original.Envelope.ResultingVersionId)
                {
                    return ReplayLookup.Failed(IntegrityFailure);
                }

                SagaReviewEvent? replacementEvent = await ReadEventAsync(
                    connection,
                    transaction,
                    request.ScopeKind,
                    request.CampaignId,
                    replacement.EventSequence,
                    cancellationToken).ConfigureAwait(false);

                if (replacementEvent is null
                    || replacementEvent.VersionId != replacement.Envelope.ResultingVersionId)
                {
                    return ReplayLookup.Failed(IntegrityFailure);
                }

                byte[] expectedReplacementResponse = ResponseDigest(
                    requestDigest,
                    replacement.Envelope,
                    replacement.EventSequence,
                    replacementEvent.SubjectId,
                    replacementEvent.VersionId);

                if (!CryptographicOperations.FixedTimeEquals(
                        expectedReplacementResponse,
                        replacement.ResponseReceiptDigest))
                {
                    return ReplayLookup.Failed(IntegrityFailure);
                }
            }
            else if (corrected)
            {
                return ReplayLookup.Failed(IntegrityFailure);
            }
            else if (request.Action == MemoryReviewAction.Retire
                && original.Envelope.ResultingVersionId is { } retirementVersionId)
            {
                SagaReviewEvent? retirementEvent = await ReadEventByVersionAsync(
                    connection,
                    transaction,
                    request.ScopeKind,
                    request.CampaignId,
                    retirementVersionId,
                    cancellationToken).ConfigureAwait(false);

                if (retirementEvent is null
                    || retirementEvent.SubjectId != originalEvent.SubjectId
                    || retirementEvent.Operation != AnnalOperation.Retire)
                {
                    return ReplayLookup.Failed(IntegrityFailure);
                }
            }
            else if (request.Action != MemoryReviewAction.Retire
                && original.Envelope.ResultingVersionId is not null)
            {
                return ReplayLookup.Failed(IntegrityFailure);
            }

            results[ordinal] = new MemoryReviewBulkItemResultDto(
                originalEvent.Sequence,
                originalEvent.SubjectId,
                originalEvent.VersionId,
                original.Envelope.Outcome,
                original.Envelope.ResultingVersionId);
        }

        return ReplayLookup.Succeeded(
            new MemoryReviewBulkResultDto(
                MemoryReviewStore.Saga,
                request.RequestId,
                request.Action,
                results,
                reviewedThrough ?? 0,
                Replayed: true));
    }

    private static async Task<List<StoredReceipt>> ReadReceiptsAsync(
        DbConnection connection,
        DbTransaction? transaction,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
            """
            SELECT DecisionId, ReviewEventSequence, RequestIdempotencyDigest, ResponseReceiptDigest
            FROM annal_review_decision_receipts
            WHERE substr(DecisionId, 1, length(@prefix)) = @prefix
            ORDER BY DecisionId
            """;

        string prefix = requestId.ToString("N", CultureInfo.InvariantCulture) + ":";

        AddParameter(command, "@prefix", prefix);

        List<StoredReceipt> receipts = [];

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string decisionId = reader.GetString(0);

            if (!TryParseEnvelope(decisionId, out ReceiptEnvelope? envelope))
            {
                return [StoredReceipt.Corrupt(decisionId)];
            }

            receipts.Add(
                new StoredReceipt(
                    envelope!,
                    reader.GetInt64(1),
                    (byte[])reader.GetValue(2),
                    (byte[])reader.GetValue(3)));
        }

        return receipts;
    }

    private static bool TryParseEnvelope(string value, out ReceiptEnvelope? envelope)
    {
        string[] parts = value.Split(':', StringSplitOptions.None);

        if (parts.Length != 7
            || !Guid.TryParseExact(parts[0], "N", out Guid requestId)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int ordinal)
            || parts[2] is not ("O" or "R")
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out int actionCode)
            || !Enum.IsDefined((MemoryReviewAction)actionCode)
            || string.IsNullOrEmpty(parts[4])
            || !long.TryParse(parts[5], NumberStyles.None, CultureInfo.InvariantCulture, out long reviewedThrough)
            || reviewedThrough < 0)
        {
            envelope = null;

            return false;
        }

        string? resultingVersion = parts[6] == "-" ? null : parts[6];

        if (resultingVersion is { Length: 0 })
        {
            envelope = null;

            return false;
        }

        envelope = new ReceiptEnvelope(
            requestId,
            ordinal,
            parts[2] == "O" ? ReceiptKind.Original : ReceiptKind.Replacement,
            (MemoryReviewAction)actionCode,
            parts[4],
            reviewedThrough,
            resultingVersion);

        return true;
    }

    private static async Task ExecuteAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken,
        string commandText,
        params (string Name, object? Value)[] parameters)
    {
        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = commandText;

        foreach ((string name, object? value) in parameters)
        {
            AddParameter(command, name, value);
        }

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private Result<MemoryReviewObservationTokenFacts[]> DecodeObservations(
        SagaReviewBulkPrepareRequest request,
        MemoryReviewDigest scopeDigest)
    {
        MemoryReviewObservationTokenFacts[] observations =
            new MemoryReviewObservationTokenFacts[request.Decisions.Length];

        HashSet<(ulong EventSequence, MemoryReviewDigest VersionIdentity)> exactVersions = [];

        MemoryReviewObservationTokenFacts? first = null;

        for (int index = 0; index < request.Decisions.Length; index++)
        {
            Result<MemoryReviewObservationTokenFacts> decoded = tokenCodec.ReadObservation(
                request.Decisions[index].ObservationToken);

            if (decoded.IsFailure)
            {
                return Result<MemoryReviewObservationTokenFacts[]>.Failure(decoded.Error);
            }

            MemoryReviewObservationTokenFacts observed = decoded.Value;

            if (observed.Store != MemoryReviewStore.Saga
                || !observed.CanonicalScopeDigest.Equals(scopeDigest)
                || first is not null && !SameWindow(first, observed)
                || !exactVersions.Add((observed.EventSequence, observed.VersionIdentity)))
            {
                return Result<MemoryReviewObservationTokenFacts[]>.Failure(InvalidToken);
            }

            first ??= observed;

            observations[index] = observed;
        }

        return Result<MemoryReviewObservationTokenFacts[]>.Success(observations);
    }

    private static Error? ValidateObservedTarget(
        MemoryReviewAction action,
        SagaReviewDecision decision,
        MemoryReviewObservationTokenFacts observed,
        SagaReviewEvent? row)
    {
        if (row is null
            || row.Reviewed
            || !VersionIdentity(row.VersionId).Equals(observed.VersionIdentity))
        {
            return UnseenObservation;
        }

        if (!row.IsCurrent)
        {
            return StaleObservation;
        }

        if (action == MemoryReviewAction.Correct)
        {
            if (row.RetiredAtUtc is not null)
            {
                return new Error(
                    ErrorCodes.Saga.AlreadyRetired,
                    "A retired Saga memory must be reinstated before it can be corrected.");
            }

            if (row.CurrentContent is null)
            {
                return StaleObservation;
            }
        }

        return null;
    }

    private static MemoryReviewBulkPlanItemDto PlanItem(SagaReviewEvent row) =>
        new(
            row.Sequence,
            row.SubjectId,
            row.VersionId,
            row.IsCurrent,
            row.Origin.ToString(),
            SourceDescription(row),
            ScopeDescription(row.ScopeKind, row.CampaignId));

    private static string SourceDescription(SagaReviewEvent row) =>
        row.Source
        ?? row.SourceSessionId?.ToString("D", CultureInfo.InvariantCulture)
        ?? row.Origin.ToString();

    private static string ScopeDescription(SagaMemoryScopeKind scopeKind, Guid? campaignId) =>
        scopeKind == SagaMemoryScopeKind.Campaign
            ? $"Campaign:{CanonicalCampaign(campaignId)}"
            : scopeKind.ToString();

    private static MemoryReviewDigest OrderedRequestDigest(SagaReviewBulkPrepareRequest request)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        AppendHashPart(hash, "saga-review-request-v1");

        AppendHashPart(hash, request.RequestId.ToString("N", CultureInfo.InvariantCulture));

        AppendHashPart(hash, ((int)request.ScopeKind).ToString(CultureInfo.InvariantCulture));

        AppendHashPart(hash, CanonicalCampaign(request.CampaignId));

        AppendHashPart(hash, ((int)request.Action).ToString(CultureInfo.InvariantCulture));

        AppendHashPart(hash, request.Decisions.Length.ToString(CultureInfo.InvariantCulture));

        foreach (SagaReviewDecision decision in request.Decisions)
        {
            AppendHashPart(hash, decision.ObservationToken);

            AppendHashPart(hash, decision.ReplacementContent);
        }

        return new MemoryReviewDigest(hash.GetHashAndReset());
    }

    private static void AppendHashPart(IncrementalHash hash, string? value)
    {
        byte[] bytes = value is null ? [] : Encoding.UTF8.GetBytes(value);

        Span<byte> length = stackalloc byte[sizeof(int)];

        BinaryPrimitives.WriteInt32BigEndian(length, value is null ? -1 : bytes.Length);

        hash.AppendData(length);

        hash.AppendData(bytes);
    }

    private static bool SameWindow(
        MemoryReviewObservationTokenFacts left,
        MemoryReviewObservationTokenFacts right) =>
        left.Store == right.Store
        && left.CanonicalScopeDigest.Equals(right.CanonicalScopeDigest)
        && left.MarkerGeneration == right.MarkerGeneration
        && left.MarkerRevision == right.MarkerRevision
        && left.FrozenLowerEventSequence == right.FrozenLowerEventSequence
        && left.FrozenUpperEventSequence == right.FrozenUpperEventSequence;

    private async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        DbConnection connection = db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        return connection;
    }

    private static async Task<ReviewMarker> ReadOrCreateMarkerAsync(
        DbConnection connection,
        DbTransaction transaction,
        SagaMemoryScopeKind scopeKind,
        Guid? campaignId,
        CancellationToken cancellationToken)
    {
        ReviewMarker? existing = await ReadMarkerAsync(
            connection,
            transaction,
            scopeKind,
            campaignId,
            cancellationToken).ConfigureAwait(false);

        if (existing is { } marker)
        {
            return marker;
        }

        Guid generation = Guid.NewGuid();

        await using DbCommand insert = connection.CreateCommand();

        insert.Transaction = transaction;

        insert.CommandText =
            """
            INSERT OR IGNORE INTO annal_review_markers (
                SubjectStoreCode, ScopeKindCode, CampaignId, MarkerGeneration,
                ReviewedThroughSequence, Revision)
            VALUES (@store, @scope, @campaign, @generation, 0, 1)
            """;

        AddParameter(insert, "@store", (int)AnnalSubjectStore.Saga);

        AddParameter(insert, "@scope", (int)scopeKind);

        AddParameter(insert, "@campaign", CanonicalCampaign(campaignId));

        AddParameter(insert, "@generation", GuidBytes(generation));

        _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return await ReadMarkerAsync(
            connection,
            transaction,
            scopeKind,
            campaignId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The Saga review marker could not be created or reread.");
    }

    private static async Task<ReviewMarker?> ReadMarkerAsync(
        DbConnection connection,
        DbTransaction transaction,
        SagaMemoryScopeKind scopeKind,
        Guid? campaignId,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
            """
            SELECT MarkerGeneration, ReviewedThroughSequence, Revision
            FROM annal_review_markers
            WHERE SubjectStoreCode = @store
              AND ScopeKindCode = @scope
              AND ((@campaign IS NULL AND CampaignId IS NULL) OR CampaignId = @campaign)
            """;

        AddParameter(command, "@store", (int)AnnalSubjectStore.Saga);

        AddParameter(command, "@scope", (int)scopeKind);

        AddParameter(command, "@campaign", CanonicalCampaign(campaignId));

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ReviewMarker(
            new Guid((byte[])reader.GetValue(0), bigEndian: true),
            reader.GetInt64(1),
            reader.GetInt64(2));
    }

    private static async Task<long> ReadUpperFrontierAsync(
        DbConnection connection,
        DbTransaction transaction,
        SagaMemoryScopeKind scopeKind,
        Guid? campaignId,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
            """
            SELECT COALESCE(MAX(Sequence), 0)
            FROM annal_review_events
            WHERE SubjectStoreCode = @store
              AND ScopeKindCode = @scope
              AND ((@campaign IS NULL AND CampaignId IS NULL) OR CampaignId = @campaign)
            """;

        AddParameter(command, "@store", (int)AnnalSubjectStore.Saga);

        AddParameter(command, "@scope", (int)scopeKind);

        AddParameter(command, "@campaign", CanonicalCampaign(campaignId));

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task<SagaReviewCurrentDto?> ReadCurrentSnapshotAsync(
        DbConnection connection,
        DbTransaction transaction,
        string subjectId,
        CancellationToken cancellationToken)
    {
        SagaMemoryDto memory;

        bool hasEmbedding;

        await using (DbCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;

            command.CommandText =
                """
                SELECT memory.Id, memory.Content, memory.CreatedAt, memory.SessionId,
                       memory.Tags, memory.Source,
                       provenance.SessionId, provenance.AttachmentId, provenance.LogicalKey,
                       provenance.Version, provenance.ContentHash, provenance.MaterializedAt,
                       provenance.SourceType,
                       EXISTS (
                           SELECT 1 FROM SessionAttachments AS attachment
                           WHERE attachment.Id = provenance.AttachmentId
                             AND attachment.State = 'Bound'),
                       memory.ScopeKindCode, memory.CampaignId,
                       memory.RetiredAtUtc, memory.PinnedAtUtc,
                       EXISTS (
                           SELECT 1 FROM saga_memory_embeddings AS embedding
                           WHERE embedding.MemoryId = memory.Id)
                FROM saga_memories AS memory
                LEFT JOIN saga_memory_attachment_provenance AS provenance
                  ON provenance.MemoryId = memory.Id
                WHERE memory.Id = @id
                """;

            AddParameter(command, "@id", subjectId);

            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            AttachmentMemoryProvenance? provenance = reader.IsDBNull(6)
                ? null
                : new AttachmentMemoryProvenance(
                    Guid.Parse(reader.GetString(6)),
                    Guid.Parse(reader.GetString(7)),
                    reader.GetString(8),
                    reader.GetInt32(9),
                    reader.GetString(10),
                    UtcInstantText.Parse(reader.GetString(11)),
                    reader.GetString(12),
                    reader.GetInt32(13) == 1
                        ? AttachmentSourceAvailability.Available
                        : AttachmentSourceAvailability.Unavailable);

            memory = new SagaMemoryDto(
                reader.GetString(0),
                reader.GetString(1),
                UtcInstantText.Parse(reader.GetString(2)),
                reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                provenance,
                (SagaMemoryScopeKind)reader.GetInt32(14),
                reader.IsDBNull(15) ? null : Guid.Parse(reader.GetString(15)),
                reader.IsDBNull(16) ? null : UtcInstantText.Parse(reader.GetString(16)),
                reader.IsDBNull(17) ? null : UtcInstantText.Parse(reader.GetString(17)));

            hasEmbedding = reader.GetInt32(18) == 1;
        }

        AnnalClaimHead? claim = await ReadClaimHeadAsync(
            connection,
            transaction,
            subjectId,
            cancellationToken).ConfigureAwait(false);

        SagaMemoryLifecycle lifecycle = new(memory.RetiredAtUtc, memory.PinnedAtUtc);

        SagaRetrievalEligibility eligibility = memory.RetiredAtUtc is not null
            ? SagaRetrievalEligibility.Retired
            : memory.ScopeKind is SagaMemoryScopeKind.Unclassified or SagaMemoryScopeKind.LegacyUnresolved
                ? SagaRetrievalEligibility.OwnershipUnresolved
                : !hasEmbedding
                    ? SagaRetrievalEligibility.EmbeddingMissing
                    : SagaRetrievalEligibility.Eligible;

        return new SagaReviewCurrentDto(
            memory,
            Convert.ToHexString(AnnalContentDigest.ForSagaMemory(memory.Content)),
            lifecycle,
            eligibility,
            claim);
    }

    private static async Task<AnnalClaimHead?> ReadClaimHeadAsync(
        DbConnection connection,
        DbTransaction transaction,
        string subjectId,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
            """
            SELECT claim.ClaimId, claim.SubjectStoreCode, claim.SubjectId,
                   head.CurrentVersionId, head.CurrentRevision, head.CurrentOperationCode,
                   head.UpdatedAtUtc
            FROM annal_claims AS claim
            JOIN annal_heads AS head ON head.ClaimId = claim.ClaimId
            WHERE claim.SubjectStoreCode = @store AND claim.SubjectId = @id
            """;

        AddParameter(command, "@store", (int)AnnalSubjectStore.Saga);

        AddParameter(command, "@id", subjectId);

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new AnnalClaimHead(
            reader.GetString(0),
            (AnnalSubjectStore)reader.GetInt32(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4),
            (AnnalOperation)reader.GetInt32(5),
            UtcInstantText.Parse(reader.GetString(6)));
    }

    private static async Task<List<SagaReviewEvent>> ReadPageAsync(
        DbConnection connection,
        DbTransaction transaction,
        SagaMemoryScopeKind scopeKind,
        Guid? campaignId,
        ulong lower,
        ulong upper,
        ulong? keyset,
        int limit,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
            """
            SELECT event.Sequence, event.SubjectId, event.VersionId, version.Revision,
                   event.OperationCode, event.OriginCode, event.ScopeKindCode, event.CampaignId,
                   event.SourceSessionId, memory.Source, version.ContentHash,
                   CASE WHEN head.CurrentVersionId = event.VersionId THEN 1 ELSE 0 END,
                   memory.Content, memory.CreatedAt, memory.SessionId, memory.Tags,
                   memory.RetiredAtUtc, memory.PinnedAtUtc,
                   EXISTS (
                       SELECT 1 FROM annal_review_decision_receipts AS reviewed
                       WHERE reviewed.ReviewEventSequence = event.Sequence)
            FROM annal_review_events AS event
            JOIN annal_versions AS version ON version.VersionId = event.VersionId
            LEFT JOIN annal_heads AS head ON head.ClaimId = event.ClaimId
            LEFT JOIN saga_memories AS memory ON memory.Id = event.SubjectId
            WHERE event.SubjectStoreCode = @store
              AND event.ScopeKindCode = @scope
              AND ((@campaign IS NULL AND event.CampaignId IS NULL) OR event.CampaignId = @campaign)
              AND event.Sequence >= @lower
              AND event.Sequence <= @upper
              AND (@keyset IS NULL OR event.Sequence < @keyset)
              AND head.CurrentVersionId = event.VersionId
              AND NOT EXISTS (
                  SELECT 1 FROM annal_review_decision_receipts AS receipt
                  WHERE receipt.ReviewEventSequence = event.Sequence)
            ORDER BY event.Sequence DESC
            LIMIT @limit
            """;

        AddParameter(command, "@store", (int)AnnalSubjectStore.Saga);

        AddParameter(command, "@scope", (int)scopeKind);

        AddParameter(command, "@campaign", CanonicalCampaign(campaignId));

        AddParameter(command, "@lower", checked((long)lower));

        AddParameter(command, "@upper", checked((long)upper));

        AddParameter(command, "@keyset", keyset is null ? null : checked((long)keyset.Value));

        AddParameter(command, "@limit", limit);

        List<SagaReviewEvent> rows = [];

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(ReadReviewEvent(reader));
        }

        return rows;
    }

    private static async Task<SagaReviewEvent?> ReadEventAsync(
        DbConnection connection,
        DbTransaction? transaction,
        SagaMemoryScopeKind scopeKind,
        Guid? campaignId,
        long sequence,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
            """
            SELECT event.Sequence, event.SubjectId, event.VersionId, version.Revision,
                   event.OperationCode, event.OriginCode, event.ScopeKindCode, event.CampaignId,
                   event.SourceSessionId, memory.Source, version.ContentHash,
                   CASE WHEN head.CurrentVersionId = event.VersionId THEN 1 ELSE 0 END,
                   memory.Content, memory.CreatedAt, memory.SessionId, memory.Tags,
                   memory.RetiredAtUtc, memory.PinnedAtUtc,
                   EXISTS (
                       SELECT 1 FROM annal_review_decision_receipts AS reviewed
                       WHERE reviewed.ReviewEventSequence = event.Sequence)
            FROM annal_review_events AS event
            JOIN annal_versions AS version ON version.VersionId = event.VersionId
            LEFT JOIN annal_heads AS head ON head.ClaimId = event.ClaimId
            LEFT JOIN saga_memories AS memory ON memory.Id = event.SubjectId
            WHERE event.Sequence = @sequence
              AND event.SubjectStoreCode = @store
              AND event.ScopeKindCode = @scope
              AND ((@campaign IS NULL AND event.CampaignId IS NULL) OR event.CampaignId = @campaign)
            """;

        AddParameter(command, "@sequence", sequence);

        AddParameter(command, "@store", (int)AnnalSubjectStore.Saga);

        AddParameter(command, "@scope", (int)scopeKind);

        AddParameter(command, "@campaign", CanonicalCampaign(campaignId));

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadReviewEvent(reader)
            : null;
    }

    private static async Task<SagaReviewEvent?> ReadEventByVersionAsync(
        DbConnection connection,
        DbTransaction? transaction,
        SagaMemoryScopeKind scopeKind,
        Guid? campaignId,
        string versionId,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
            """
            SELECT Sequence
            FROM annal_review_events
            WHERE VersionId = @version
              AND SubjectStoreCode = @store
              AND ScopeKindCode = @scope
              AND ((@campaign IS NULL AND CampaignId IS NULL) OR CampaignId = @campaign)
            """;

        AddParameter(command, "@version", versionId);

        AddParameter(command, "@store", (int)AnnalSubjectStore.Saga);

        AddParameter(command, "@scope", (int)scopeKind);

        AddParameter(command, "@campaign", CanonicalCampaign(campaignId));

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null
            ? null
            : await ReadEventAsync(
                connection,
                transaction,
                scopeKind,
                campaignId,
                Convert.ToInt64(value, CultureInfo.InvariantCulture),
                cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> EventIdentityMatchesAsync(
        DbConnection connection,
        DbTransaction transaction,
        SagaMemoryScopeKind scopeKind,
        Guid? campaignId,
        ulong sequence,
        MemoryReviewDigest versionIdentity,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
            """
            SELECT VersionId
            FROM annal_review_events
            WHERE Sequence = @sequence
              AND SubjectStoreCode = @store
              AND ScopeKindCode = @scope
              AND ((@campaign IS NULL AND CampaignId IS NULL) OR CampaignId = @campaign)
            """;

        AddParameter(command, "@sequence", checked((long)sequence));

        AddParameter(command, "@store", (int)AnnalSubjectStore.Saga);

        AddParameter(command, "@scope", (int)scopeKind);

        AddParameter(command, "@campaign", CanonicalCampaign(campaignId));

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is string versionId && VersionIdentity(versionId).Equals(versionIdentity);
    }

    private static SagaReviewEvent ReadReviewEvent(DbDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            (AnnalOperation)reader.GetInt32(4),
            (AnnalOrigin)reader.GetInt32(5),
            (SagaMemoryScopeKind)reader.GetInt32(6),
            reader.IsDBNull(7) ? null : Guid.Parse(reader.GetString(7)),
            reader.IsDBNull(8) ? null : Guid.Parse(reader.GetString(8)),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : Convert.ToHexString((byte[])reader.GetValue(10)),
            reader.GetInt32(11) == 1,
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : UtcInstantText.Parse(reader.GetString(13)),
            reader.IsDBNull(14) ? null : Guid.Parse(reader.GetString(14)),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.IsDBNull(16) ? null : UtcInstantText.Parse(reader.GetString(16)),
            reader.IsDBNull(17) ? null : UtcInstantText.Parse(reader.GetString(17)),
            reader.GetInt32(18) == 1);

    private static bool MatchesCommon(
        MemoryReviewCursorTokenFacts facts,
        MemoryReviewDigest scopeDigest,
        ReviewMarker marker) =>
        facts.Store == MemoryReviewStore.Saga
        && facts.CanonicalScopeDigest.Equals(scopeDigest)
        && facts.MarkerGeneration == marker.Generation
        && facts.MarkerRevision == checked((ulong)marker.Revision)
        && facts.FrozenLowerEventSequence == checked((ulong)marker.ReviewedThroughSequence + 1UL);

    private static bool MatchesCommon(
        MemoryReviewObservationTokenFacts facts,
        MemoryReviewDigest scopeDigest,
        ReviewMarker marker) =>
        facts.Store == MemoryReviewStore.Saga
        && facts.CanonicalScopeDigest.Equals(scopeDigest)
        && facts.MarkerGeneration == marker.Generation
        && facts.MarkerRevision == checked((ulong)marker.Revision)
        && facts.FrozenLowerEventSequence == checked((ulong)marker.ReviewedThroughSequence + 1UL);

    private static bool MatchesCommon(
        MemoryReviewPreparedPlanTokenFacts facts,
        MemoryReviewDigest scopeDigest,
        ReviewMarker marker) =>
        facts.Store == MemoryReviewStore.Saga
        && facts.CanonicalScopeDigest.Equals(scopeDigest)
        && facts.MarkerGeneration == marker.Generation
        && facts.MarkerRevision == checked((ulong)marker.Revision)
        && facts.FrozenLowerEventSequence == checked((ulong)marker.ReviewedThroughSequence + 1UL);

    private static bool MatchesPreparedPlan(
        MemoryReviewPreparedPlanTokenFacts plan,
        MemoryReviewObservationTokenFacts observation,
        MemoryReviewDigest scopeDigest,
        MemoryReviewDigest requestDigest) =>
        plan.Store == MemoryReviewStore.Saga
        && plan.CanonicalScopeDigest.Equals(scopeDigest)
        && plan.OrderedRequestDigest.Equals(requestDigest)
        && plan.MarkerGeneration == observation.MarkerGeneration
        && plan.MarkerRevision == observation.MarkerRevision
        && plan.FrozenLowerEventSequence == observation.FrozenLowerEventSequence
        && plan.FrozenUpperEventSequence == observation.FrozenUpperEventSequence;

    private static MemoryReviewDigest ScopeDigest(SagaMemoryScopeKind scopeKind, Guid? campaignId) =>
        Digest($"saga\n{(int)scopeKind}\n{CanonicalCampaign(campaignId) ?? "-"}");

    private static MemoryReviewDigest VersionIdentity(string versionId) =>
        Digest($"saga-version\n{versionId}");

    private static MemoryReviewDigest Digest(string value) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string? CanonicalCampaign(Guid? campaignId) =>
        campaignId?.ToString("D", CultureInfo.InvariantCulture).ToUpperInvariant();

    private static byte[] GuidBytes(Guid value)
    {
        byte[] bytes = new byte[16];

        if (!value.TryWriteBytes(bytes, bigEndian: true, out int written) || written != bytes.Length)
        {
            throw new InvalidOperationException("The review-marker generation could not be encoded.");
        }

        return bytes;
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        DbParameter parameter = command.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value ?? DBNull.Value;

        _ = command.Parameters.Add(parameter);
    }

    private sealed record ReviewMarker(Guid Generation, long ReviewedThroughSequence, long Revision);

    private sealed record AppliedDecision(
        int Ordinal,
        SagaReviewEvent Target,
        MemoryReviewBulkItemResultDto Result,
        long? ReplacementEventSequence);

    private sealed record ReceiptEnvelope(
        Guid RequestId,
        int Ordinal,
        ReceiptKind Kind,
        MemoryReviewAction Action,
        string Outcome,
        long ReviewedThrough,
        string? ResultingVersionId);

    private sealed record StoredReceipt(
        ReceiptEnvelope Envelope,
        long EventSequence,
        byte[] RequestIdempotencyDigest,
        byte[] ResponseReceiptDigest,
        bool IsCorrupt = false)
    {
        internal static StoredReceipt Corrupt(string decisionId) =>
            new(
                new ReceiptEnvelope(
                    Guid.Empty,
                    -1,
                    ReceiptKind.Original,
                    MemoryReviewAction.Confirm,
                    decisionId,
                    0,
                    ResultingVersionId: null),
                EventSequence: -1,
                RequestIdempotencyDigest: [],
                ResponseReceiptDigest: [],
                IsCorrupt: true);
    }

    private sealed record ReplayLookup(MemoryReviewBulkResultDto? Result, Error? Error)
    {
        internal static ReplayLookup None { get; } = new(Result: null, Error: null);

        internal static ReplayLookup Succeeded(MemoryReviewBulkResultDto result) =>
            new(result, Error: null);

        internal static ReplayLookup Failed(Error error) =>
            new(Result: null, error);
    }

    private enum ReceiptKind
    {
        Original = 1,

        Replacement = 2,
    }

    private sealed record SagaReviewEvent(
        long Sequence,
        string SubjectId,
        string VersionId,
        int Revision,
        AnnalOperation Operation,
        AnnalOrigin Origin,
        SagaMemoryScopeKind ScopeKind,
        Guid? CampaignId,
        Guid? SourceSessionId,
        string? Source,
        string? ContentHash,
        bool IsCurrent,
        string? CurrentContent,
        DateTimeOffset? CreatedAtUtc,
        Guid? MemorySessionId,
        string? Tags,
        DateTimeOffset? RetiredAtUtc,
        DateTimeOffset? PinnedAtUtc,
        bool Reviewed);
}
