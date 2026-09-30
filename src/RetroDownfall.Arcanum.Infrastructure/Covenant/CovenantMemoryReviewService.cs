using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Covenant;

/// <summary>
/// Reviews exact immutable Covenant versions through one generation-bound, scoped queue.
/// </summary>
internal sealed class CovenantMemoryReviewService(
    ICovenantConnectionSource connections,
    ICovenantCompiler compiler,
    IMemoryReviewTokenCodec tokenCodec,
    CovenantMutationKernel mutationKernel,
    CovenantCurationKernel curationKernel,
    TimeProvider timeProvider) : ICovenantMemoryReviewService
{
    private static readonly Error InvalidToken = new(
        ErrorCodes.MemoryReview.InvalidToken,
        "The memory-review token is invalid, expired, stale, or bound to another queue.");

    private static readonly Error StaleObservation = new(
        ErrorCodes.MemoryReview.StaleObservation,
        "A reviewed Covenant version or its review position changed before this operation could commit.");

    private static readonly Error UnseenObservation = new(
        ErrorCodes.MemoryReview.UnseenObservation,
        "A review decision did not identify an event observed in this exact Covenant queue.");

    private static readonly Error IntegrityFailure = new(
        ErrorCodes.MemoryReview.IntegrityFailure,
        "Durable Covenant review state failed its integrity contract.");

    private static readonly Error RequestReuse = new(
        ErrorCodes.MemoryReview.RequestReuse,
        "This memory-review request identity already belongs to a different decision set.");

    public async ValueTask<Result<CovenantReviewPageDto>> ListAsync(
        CovenantReviewListRequest request,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(readLease);

        Result validation = request.Validate();

        if (validation.IsFailure)
        {
            return validation.Error;
        }

        Result lease = await ValidateReadLeaseAsync(
            readLease,
            request.Scope,
            request.CampaignId,
            cancellationToken).ConfigureAwait(false);

        if (lease.IsFailure)
        {
            return lease.Error;
        }

        MemoryReviewDigest scopeDigest = ScopeDigest(request.Scope, request.CampaignId, request.Lane);

        Result<MemoryReviewCursorTokenFacts>? decodedCursor = request.Cursor is null
            ? null
            : tokenCodec.ReadCursor(request.Cursor);

        if (decodedCursor is { IsFailure: true } invalidCursor)
        {
            return invalidCursor.Error;
        }

        SqliteConnection connection = await connections
            .GetOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        CanonicalState state = await ReadCanonicalStateAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);

        if (readLease.Snapshot.DatasetGeneration != state.DatasetGeneration)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return StaleObservation;
        }

        ReviewMarker marker = await ReadOrCreateMarkerAsync(
            connection,
            transaction,
            state.DatasetGeneration,
            request.Scope,
            request.CampaignId,
            request.Lane,
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
                return InvalidToken;
            }

            bool keysetMatches = await EventIdentityMatchesAsync(
                connection,
                transaction,
                marker.Generation,
                request.Scope,
                request.CampaignId,
                request.Lane,
                cursor.KeysetEventSequence,
                cursor.KeysetVersionIdentity,
                cancellationToken).ConfigureAwait(false);

            if (!keysetMatches)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return InvalidToken;
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
                marker.Generation,
                request.Scope,
                request.CampaignId,
                request.Lane,
                cancellationToken).ConfigureAwait(false);

            upper = frozenUpper <= 0 ? 0UL : checked((ulong)frozenUpper);
            keyset = null;
        }

        List<CovenantReviewEvent> rows = upper < lower
            ? []
            : await ReadPageAsync(
                connection,
                transaction,
                marker.Generation,
                request.Scope,
                request.CampaignId,
                request.Lane,
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

        CovenantReviewItemDto[] items = new CovenantReviewItemDto[rows.Count];

        for (int index = 0; index < rows.Count; index++)
        {
            CovenantReviewEvent row = rows[index];

            CovenantSourceDto[] sources = await ReadSourcesAsync(
                connection,
                transaction,
                row.VersionId,
                cancellationToken).ConfigureAwait(false);

            if (sources.LongLength != row.ProvenanceCount)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return IntegrityFailure;
            }

            Result<string> observation = tokenCodec.IssueObservation(
                new MemoryReviewObservationTokenFacts(
                    MemoryReviewStore.Covenant,
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
                return observation.Error;
            }

            items[index] = ReviewItem(row, sources, observation.Value);
        }

        string? nextCursor = null;

        if (truncated && rows.Count > 0)
        {
            CovenantReviewEvent last = rows[^1];

            Result<string> issued = tokenCodec.IssueCursor(
                new MemoryReviewCursorTokenFacts(
                    MemoryReviewStore.Covenant,
                    scopeDigest,
                    marker.Generation,
                    checked((ulong)marker.Revision),
                    lower,
                    upper,
                    checked((ulong)last.Sequence),
                    VersionIdentity(last.VersionId)));

            if (issued.IsFailure)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return issued.Error;
            }

            nextCursor = issued.Value;
        }

        Result currentLease = await readLease.RevalidateAsync(cancellationToken).ConfigureAwait(false);

        if (currentLease.IsFailure)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return currentLease.Error;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new CovenantReviewPageDto(
            items,
            marker.ReviewedThroughSequence,
            checked((long)upper),
            nextCursor,
            truncated);
    }

    public async ValueTask<Result<MemoryReviewBulkPlanDto>> PrepareAsync(
        CovenantReviewBulkPrepareRequest request,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(readLease);

        Result validation = request.Validate();

        if (validation.IsFailure)
        {
            return validation.Error;
        }

        if (request.Action == MemoryReviewAction.Correct && request.Lane != CovenantLane.Confirmed)
        {
            return new Error(
                ErrorCodes.Validation.InvalidBody,
                "A Covenant correction authors the Confirmed lane; Proposed content must be retired or confirmed separately.");
        }

        Result lease = await ValidateReadLeaseAsync(
            readLease,
            request.Scope,
            request.CampaignId,
            cancellationToken).ConfigureAwait(false);

        if (lease.IsFailure)
        {
            return lease.Error;
        }

        MemoryReviewDigest scopeDigest = ScopeDigest(request.Scope, request.CampaignId, request.Lane);

        Result<MemoryReviewObservationTokenFacts[]> decoded = DecodeObservations(request, scopeDigest);

        if (decoded.IsFailure)
        {
            return decoded.Error;
        }

        MemoryReviewObservationTokenFacts first = decoded.Value[0];

        SqliteConnection connection = await connections
            .GetOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        CanonicalState state = await ReadCanonicalStateAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);

        ReviewMarker? marker = await ReadMarkerAsync(
            connection,
            transaction,
            first.MarkerGeneration,
            request.Scope,
            request.CampaignId,
            request.Lane,
            cancellationToken).ConfigureAwait(false);

        if (marker is null
            || !MatchesCommon(first, scopeDigest, marker)
            || marker.Generation != state.DatasetGeneration
            || readLease.Snapshot.DatasetGeneration != state.DatasetGeneration)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return StaleObservation;
        }

        if (readLease.Snapshot.DatasetGeneration != marker.Generation)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return StaleObservation;
        }

        MemoryReviewBulkPlanItemDto[] items = new MemoryReviewBulkPlanItemDto[request.Decisions.Length];

        for (int index = 0; index < request.Decisions.Length; index++)
        {
            MemoryReviewObservationTokenFacts observed = decoded.Value[index];

            CovenantReviewEvent? row = await ReadEventAsync(
                connection,
                transaction,
                marker.Generation,
                request.Scope,
                request.CampaignId,
                request.Lane,
                checked((long)observed.EventSequence),
                cancellationToken).ConfigureAwait(false);

            Error? refusal = ValidateObservedTarget(request.Action, request.Decisions[index], observed, row);

            if (refusal is { } error)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return error;
            }

            items[index] = PlanItem(row!);
        }

        MemoryReviewDigest requestDigest = OrderedRequestDigest(request);

        Result<string> preparedToken = tokenCodec.IssuePreparedPlan(
            new MemoryReviewPreparedPlanTokenFacts(
                MemoryReviewStore.Covenant,
                scopeDigest,
                first.MarkerGeneration,
                first.MarkerRevision,
                first.FrozenLowerEventSequence,
                first.FrozenUpperEventSequence,
                requestDigest));

        if (preparedToken.IsFailure)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return preparedToken.Error;
        }

        Result currentLease = await readLease.RevalidateAsync(cancellationToken).ConfigureAwait(false);

        if (currentLease.IsFailure)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return currentLease.Error;
        }

        DateTimeOffset issuedAt = timeProvider.GetUtcNow();

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new MemoryReviewBulkPlanDto(
            MemoryReviewStore.Covenant,
            request.RequestId,
            request.Action,
            items,
            issuedAt,
            issuedAt + MemoryReviewLimits.TokenLifetime,
            preparedToken.Value);
    }

    public async ValueTask<Result<MemoryReviewBulkResultDto>> ApplyAsync(
        CovenantReviewBulkApplyRequest request,
        CovenantWriteLease writeLease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(writeLease);

        Result validation = request.Validate();

        if (validation.IsFailure)
        {
            return validation.Error;
        }

        CovenantReviewBulkPrepareRequest preparedRequest = request.Request;

        if (preparedRequest.Action == MemoryReviewAction.Correct
            && preparedRequest.Lane != CovenantLane.Confirmed)
        {
            return new Error(
                ErrorCodes.Validation.InvalidBody,
                "A Covenant correction authors the Confirmed lane; Proposed content must be retired or confirmed separately.");
        }

        Result lease = await ValidateWriteLeaseAsync(
            writeLease,
            preparedRequest.Scope,
            preparedRequest.CampaignId,
            cancellationToken).ConfigureAwait(false);

        if (lease.IsFailure)
        {
            return lease.Error;
        }

        MemoryReviewDigest requestDigest = OrderedRequestDigest(preparedRequest);

        SqliteConnection connection = await connections
            .GetOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        ReplayLookup replay = await ReadReplayAsync(
            connection,
            transaction: null,
            preparedRequest,
            requestDigest,
            cancellationToken).ConfigureAwait(false);

        if (replay.Error is { } replayError)
        {
            return replayError;
        }

        if (replay.Result is { } replayed)
        {
            Result replayLease = await writeLease.RevalidateAsync(cancellationToken).ConfigureAwait(false);
            return replayLease.IsFailure ? replayLease.Error : replayed;
        }

        MemoryReviewDigest scopeDigest = ScopeDigest(
            preparedRequest.Scope,
            preparedRequest.CampaignId,
            preparedRequest.Lane);

        Result<MemoryReviewPreparedPlanTokenFacts> decodedPlan = tokenCodec.ReadPreparedPlan(
            request.PreparedPlanToken);

        if (decodedPlan.IsFailure)
        {
            return decodedPlan.Error;
        }

        Result<MemoryReviewObservationTokenFacts[]> decodedObservations = DecodeObservations(
            preparedRequest,
            scopeDigest);

        if (decodedObservations.IsFailure)
        {
            return decodedObservations.Error;
        }

        MemoryReviewPreparedPlanTokenFacts plan = decodedPlan.Value;
        MemoryReviewObservationTokenFacts first = decodedObservations.Value[0];

        if (!MatchesPreparedPlan(plan, first, scopeDigest, requestDigest))
        {
            return InvalidToken;
        }

        // Read from the latch before BEGIN, never inside it, and disposed only after the transaction
        // ends. Review decisions are operator intents, which the kernel never refuses through it.
        using CovenantAgentErasureGate erasureGate = mutationKernel.CaptureErasureGate();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        ReplayLookup racedReplay = await ReadReplayAsync(
            connection,
            transaction,
            preparedRequest,
            requestDigest,
            cancellationToken).ConfigureAwait(false);

        if (racedReplay.Error is { } racedError)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return racedError;
        }

        if (racedReplay.Result is { } racedResult)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            Result racedLease = await writeLease.RevalidateAsync(cancellationToken).ConfigureAwait(false);
            return racedLease.IsFailure ? racedLease.Error : racedResult;
        }

        CanonicalState state = await ReadCanonicalStateAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);

        ReviewMarker? marker = await ReadMarkerAsync(
            connection,
            transaction,
            state.DatasetGeneration,
            preparedRequest.Scope,
            preparedRequest.CampaignId,
            preparedRequest.Lane,
            cancellationToken).ConfigureAwait(false);

        if (marker is null
            || !MatchesCommon(plan, scopeDigest, marker)
            || writeLease.Snapshot.DatasetGeneration != state.DatasetGeneration)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return StaleObservation;
        }

        CovenantReviewEvent[] targets = new CovenantReviewEvent[preparedRequest.Decisions.Length];

        for (int index = 0; index < preparedRequest.Decisions.Length; index++)
        {
            MemoryReviewObservationTokenFacts observed = decodedObservations.Value[index];

            CovenantReviewEvent? row = await ReadEventAsync(
                connection,
                transaction,
                marker.Generation,
                preparedRequest.Scope,
                preparedRequest.CampaignId,
                preparedRequest.Lane,
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
                return error;
            }

            targets[index] = row!;
        }

        DateTimeOffset committedAt = timeProvider.GetUtcNow();
        AppliedDecision[] applied = new AppliedDecision[targets.Length];
        CovenantMutationTransaction kernelTransaction = new(connection, transaction);

        for (int index = 0; index < targets.Length; index++)
        {
            Result<AppliedDecision> decision = await ApplyDecisionAsync(
                kernelTransaction,
                erasureGate,
                state,
                writeLease.Snapshot,
                preparedRequest,
                requestDigest,
                index,
                targets[index],
                committedAt,
                cancellationToken).ConfigureAwait(false);

            if (decision.IsFailure)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return decision.Error;
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
            marker.Generation,
            preparedRequest.Scope,
            preparedRequest.CampaignId,
            preparedRequest.Lane,
            marker.ReviewedThroughSequence,
            acknowledged,
            cancellationToken).ConfigureAwait(false);

        for (int index = 0; index < applied.Length; index++)
        {
            await InsertDecisionReceiptAsync(
                connection,
                transaction,
                marker.Generation,
                preparedRequest,
                requestDigest,
                applied[index],
                reviewedThrough,
                cancellationToken).ConfigureAwait(false);
        }

        int markerWrites = await AdvanceMarkerAsync(
            connection,
            transaction,
            preparedRequest.Scope,
            preparedRequest.CampaignId,
            preparedRequest.Lane,
            marker,
            reviewedThrough,
            cancellationToken).ConfigureAwait(false);

        if (markerWrites != 1)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return StaleObservation;
        }

        Result currentLease = await writeLease.RevalidateAsync(cancellationToken).ConfigureAwait(false);

        if (currentLease.IsFailure)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return currentLease.Error;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new MemoryReviewBulkResultDto(
            MemoryReviewStore.Covenant,
            preparedRequest.RequestId,
            preparedRequest.Action,
            [.. applied.Select(static decision => decision.Result)],
            reviewedThrough,
            Replayed: false);
    }

    private static async ValueTask<Result> ValidateReadLeaseAsync(
        ICovenantSnapshotReadLease lease,
        CovenantScope scope,
        Guid? campaignId,
        CancellationToken cancellationToken)
    {
        CovenantOperationLeaseSnapshot snapshot = lease.Snapshot;

        if (snapshot.Kind != CovenantLeaseKind.Read
            || snapshot.Coverage != CovenantLeaseCoverage.Scoped
            || !MatchesScope(snapshot.Scope, scope, campaignId)
            || snapshot.DatasetGeneration is null)
        {
            return new Error(
                ErrorCodes.Covenant.InvalidScope,
                "A Covenant review read requires a generation-bound lease over that exact scope.");
        }

        return await lease.RevalidateAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<Result> ValidateWriteLeaseAsync(
        CovenantWriteLease lease,
        CovenantScope scope,
        Guid? campaignId,
        CancellationToken cancellationToken)
    {
        CovenantOperationLeaseSnapshot snapshot = lease.Snapshot;

        if (snapshot.Kind != CovenantLeaseKind.Write
            || snapshot.Coverage != CovenantLeaseCoverage.Scoped
            || !MatchesScope(snapshot.Scope, scope, campaignId)
            || snapshot.DatasetGeneration is null)
        {
            return new Error(
                ErrorCodes.Covenant.InvalidScope,
                "A Covenant review write requires a generation-bound lease over that exact scope.");
        }

        return await lease.RevalidateAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool MatchesScope(
        CovenantOperationScope? leaseScope,
        CovenantScope scope,
        Guid? campaignId) =>
        leaseScope is { IsInitialized: true } exact
        && exact.Kind == scope
        && exact.CampaignId == campaignId;

    private Result<MemoryReviewObservationTokenFacts[]> DecodeObservations(
        CovenantReviewBulkPrepareRequest request,
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
                return decoded.Error;
            }

            MemoryReviewObservationTokenFacts observed = decoded.Value;

            if (observed.Store != MemoryReviewStore.Covenant
                || !observed.CanonicalScopeDigest.Equals(scopeDigest)
                || first is not null && !SameWindow(first, observed)
                || !exactVersions.Add((observed.EventSequence, observed.VersionIdentity)))
            {
                return InvalidToken;
            }

            first ??= observed;
            observations[index] = observed;
        }

        return observations;
    }

    private Error? ValidateObservedTarget(
        MemoryReviewAction action,
        CovenantReviewDecision decision,
        MemoryReviewObservationTokenFacts observed,
        CovenantReviewEvent? row)
    {
        if (row is null
            || row.Reviewed
            || !VersionIdentity(row.VersionId).Equals(observed.VersionIdentity))
        {
            return UnseenObservation;
        }

        // Every bulk action, including Confirm, names the exact current version it observed.
        if (!row.IsCurrent)
        {
            return StaleObservation;
        }

        if (action == MemoryReviewAction.Correct)
        {
            if (row.Operation == CovenantOperation.Retire || row.CompiledContent is null)
            {
                return new Error(
                    ErrorCodes.Covenant.LifecycleConflict,
                    "A retired Covenant entry must be reactivated through its ordinary mutation workflow.");
            }

            try
            {
                _ = compiler.Compile(row.Key, decision.ReplacementContent!);
            }
            catch (ArgumentException invalid)
            {
                return new Error(ErrorCodes.Validation.InvalidBody, invalid.Message);
            }
        }

        return null;
    }

    private static MemoryReviewBulkPlanItemDto PlanItem(CovenantReviewEvent row) =>
        new(
            row.Sequence,
            Canonical(row.EntryId),
            Canonical(row.VersionId),
            row.IsCurrent,
            row.Origin.ToString(),
            SourceDescription(row),
            ScopeDescription(row.Scope, row.CampaignId, row.Lane));

    private static CovenantReviewItemDto ReviewItem(
        CovenantReviewEvent row,
        CovenantSourceDto[] sources,
        string observationToken) =>
        new(
            row.Sequence,
            row.EntryId,
            row.VersionId,
            row.Operation,
            row.Origin,
            row.Scope,
            row.CampaignId,
            row.Lane,
            row.Key,
            row.CompiledContent ?? string.Empty,
            row.SourceTurnId,
            row.SourceToolCallId,
            row.IsCurrent,
            row.IsCurrent ? Head(row) : null,
            sources,
            observationToken);

    private static CovenantHeadDto Head(CovenantReviewEvent row) =>
        new(
            row.EntryId,
            row.VersionId,
            row.Scope,
            row.CampaignId,
            row.Key,
            row.Lane,
            row.LaneRevision,
            row.Operation == CovenantOperation.Set ? CovenantLifecycle.Set : CovenantLifecycle.Retired,
            row.Origin,
            row.AuthoredHash is { } authored ? Hex(authored) : null,
            row.RenderedHash is { } rendered ? Hex(rendered) : null,
            row.CompiledByteCost,
            row.ProvenanceCount,
            Hex(row.ProvenanceDigest),
            row.CreatedAtUtc,
            row.UpdatedAtUtc,
            CovenantEffectiveShadowState.NotEvaluated,
            CovenantEffectiveMaterialization.NotEvaluated);

    private static string SourceDescription(CovenantReviewEvent row) =>
        row.SourceTurnId is { } turn
            ? row.SourceToolCallId is { Length: > 0 } call
                ? $"Turn:{Canonical(turn)}/Tool:{call}"
                : $"Turn:{Canonical(turn)}"
            : row.Origin.ToString();

    private static string ScopeDescription(CovenantScope scope, Guid? campaignId, CovenantLane lane) =>
        scope == CovenantScope.Campaign
            ? $"Campaign:{Canonical(campaignId!.Value)}/{lane}"
            : $"Global/{lane}";

    private static bool SameWindow(
        MemoryReviewObservationTokenFacts left,
        MemoryReviewObservationTokenFacts right) =>
        left.Store == right.Store
        && left.CanonicalScopeDigest.Equals(right.CanonicalScopeDigest)
        && left.MarkerGeneration == right.MarkerGeneration
        && left.MarkerRevision == right.MarkerRevision
        && left.FrozenLowerEventSequence == right.FrozenLowerEventSequence
        && left.FrozenUpperEventSequence == right.FrozenUpperEventSequence;

    private static bool MatchesCommon(
        MemoryReviewCursorTokenFacts facts,
        MemoryReviewDigest scopeDigest,
        ReviewMarker marker) =>
        facts.Store == MemoryReviewStore.Covenant
        && facts.CanonicalScopeDigest.Equals(scopeDigest)
        && facts.MarkerGeneration == marker.Generation
        && facts.MarkerRevision == checked((ulong)marker.Revision)
        && facts.FrozenLowerEventSequence == checked((ulong)marker.ReviewedThroughSequence + 1UL);

    private static bool MatchesCommon(
        MemoryReviewObservationTokenFacts facts,
        MemoryReviewDigest scopeDigest,
        ReviewMarker marker) =>
        facts.Store == MemoryReviewStore.Covenant
        && facts.CanonicalScopeDigest.Equals(scopeDigest)
        && facts.MarkerGeneration == marker.Generation
        && facts.MarkerRevision == checked((ulong)marker.Revision)
        && facts.FrozenLowerEventSequence == checked((ulong)marker.ReviewedThroughSequence + 1UL);

    private static bool MatchesCommon(
        MemoryReviewPreparedPlanTokenFacts facts,
        MemoryReviewDigest scopeDigest,
        ReviewMarker marker) =>
        facts.Store == MemoryReviewStore.Covenant
        && facts.CanonicalScopeDigest.Equals(scopeDigest)
        && facts.MarkerGeneration == marker.Generation
        && facts.MarkerRevision == checked((ulong)marker.Revision)
        && facts.FrozenLowerEventSequence == checked((ulong)marker.ReviewedThroughSequence + 1UL);

    private static bool MatchesPreparedPlan(
        MemoryReviewPreparedPlanTokenFacts plan,
        MemoryReviewObservationTokenFacts observation,
        MemoryReviewDigest scopeDigest,
        MemoryReviewDigest requestDigest) =>
        plan.Store == MemoryReviewStore.Covenant
        && plan.CanonicalScopeDigest.Equals(scopeDigest)
        && plan.OrderedRequestDigest.Equals(requestDigest)
        && plan.MarkerGeneration == observation.MarkerGeneration
        && plan.MarkerRevision == observation.MarkerRevision
        && plan.FrozenLowerEventSequence == observation.FrozenLowerEventSequence
        && plan.FrozenUpperEventSequence == observation.FrozenUpperEventSequence;

    private static MemoryReviewDigest ScopeDigest(
        CovenantScope scope,
        Guid? campaignId,
        CovenantLane lane) =>
        Digest($"covenant\n{(int)scope}\n{CanonicalCampaign(campaignId) ?? "-"}\n{(int)lane}");

    private static MemoryReviewDigest VersionIdentity(Guid versionId) =>
        Digest($"covenant-version\n{Canonical(versionId)}");

    private static MemoryReviewDigest OrderedRequestDigest(CovenantReviewBulkPrepareRequest request)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        AppendHashPart(hash, "covenant-review-request-v1");
        AppendHashPart(hash, request.RequestId.ToString("N", CultureInfo.InvariantCulture));
        AppendHashPart(hash, ((int)request.Scope).ToString(CultureInfo.InvariantCulture));
        AppendHashPart(hash, CanonicalCampaign(request.CampaignId));
        AppendHashPart(hash, ((int)request.Lane).ToString(CultureInfo.InvariantCulture));
        AppendHashPart(hash, ((int)request.Action).ToString(CultureInfo.InvariantCulture));
        AppendHashPart(hash, request.Decisions.Length.ToString(CultureInfo.InvariantCulture));

        foreach (CovenantReviewDecision decision in request.Decisions)
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

    private static MemoryReviewDigest Digest(string value) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Canonical(Guid value) =>
        value.ToString("D", CultureInfo.InvariantCulture);

    private static string? CanonicalCampaign(Guid? campaignId) =>
        campaignId is { } present ? Canonical(present) : null;

    private static string Hex(CovenantDigest digest) => Convert.ToHexString(digest.Bytes);

    private static Guid GuidFromBlob(object value) => new((byte[])value);

    private static byte[] GuidBytes(Guid value) => value.ToByteArray();

    private static DateTimeOffset Instant(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static async ValueTask<CanonicalState> ReadCanonicalStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT DatasetGeneration, KeyReclamationEpoch FROM covenant_state WHERE StateKey = 1;";

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("Covenant canonical state is absent.");
        }

        return new CanonicalState(GuidFromBlob(reader.GetValue(0)), reader.GetInt64(1));
    }

    private static async ValueTask<ReviewMarker> ReadOrCreateMarkerAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid datasetGeneration,
        CovenantScope scope,
        Guid? campaignId,
        CovenantLane lane,
        CancellationToken cancellationToken)
    {
        ReviewMarker? marker = await ReadMarkerAsync(
            connection,
            transaction,
            datasetGeneration,
            scope,
            campaignId,
            lane,
            cancellationToken).ConfigureAwait(false);

        if (marker is not null)
        {
            return marker;
        }

        await using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT OR IGNORE INTO covenant_review_markers (
                DatasetGeneration, ScopeCode, CampaignId, LaneCode,
                ReviewedThroughSequence, Revision)
            VALUES ($generation, $scope, $campaign, $lane, 0, 1);
            """;

        Bind(insert, "$generation", GuidBytes(datasetGeneration));
        Bind(insert, "$scope", (int)scope);
        Bind(insert, "$campaign", CanonicalCampaign(campaignId));
        Bind(insert, "$lane", (int)lane);

        _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return await ReadMarkerAsync(
            connection,
            transaction,
            datasetGeneration,
            scope,
            campaignId,
            lane,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The Covenant review marker could not be created.");
    }

    private static async ValueTask<ReviewMarker?> ReadMarkerAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid datasetGeneration,
        CovenantScope scope,
        Guid? campaignId,
        CovenantLane lane,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT DatasetGeneration, ReviewedThroughSequence, Revision
            FROM covenant_review_markers
            WHERE DatasetGeneration = $generation
              AND ScopeCode = $scope
              AND CampaignId IS $campaign
              AND LaneCode = $lane;
            """;

        Bind(command, "$generation", GuidBytes(datasetGeneration));
        Bind(command, "$scope", (int)scope);
        Bind(command, "$campaign", CanonicalCampaign(campaignId));
        Bind(command, "$lane", (int)lane);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ReviewMarker(GuidFromBlob(reader.GetValue(0)), reader.GetInt64(1), reader.GetInt64(2))
            : null;
    }

    private static async ValueTask<long> ReadUpperFrontierAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid datasetGeneration,
        CovenantScope scope,
        Guid? campaignId,
        CovenantLane lane,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT COALESCE(MAX(Sequence), 0)
            FROM covenant_review_events
            WHERE DatasetGeneration = $generation
              AND ScopeCode = $scope
              AND CampaignId IS $campaign
              AND LaneCode = $lane;
            """;

        Bind(command, "$generation", GuidBytes(datasetGeneration));
        Bind(command, "$scope", (int)scope);
        Bind(command, "$campaign", CanonicalCampaign(campaignId));
        Bind(command, "$lane", (int)lane);

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async ValueTask<List<CovenantReviewEvent>> ReadPageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid datasetGeneration,
        CovenantScope scope,
        Guid? campaignId,
        CovenantLane lane,
        ulong lower,
        ulong upper,
        ulong? keyset,
        int limit,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = EventSelect +
            """

            WHERE event.DatasetGeneration = $generation
              AND event.ScopeCode = $scope
              AND event.CampaignId IS $campaign
              AND event.LaneCode = $lane
              AND event.Sequence >= $lower
              AND event.Sequence <= $upper
              AND ($keyset IS NULL OR event.Sequence < $keyset)
              AND head.CurrentVersionId = event.VersionId
              AND NOT EXISTS (
                  SELECT 1 FROM covenant_review_decision_receipts receipt
                  WHERE receipt.ReviewEventSequence = event.Sequence)
            ORDER BY event.Sequence DESC
            LIMIT $limit;
            """;

        BindQueue(command, datasetGeneration, scope, campaignId, lane);
        Bind(command, "$lower", checked((long)lower));
        Bind(command, "$upper", checked((long)upper));
        Bind(command, "$keyset", keyset is { } present ? checked((long)present) : null);
        Bind(command, "$limit", limit);

        List<CovenantReviewEvent> rows = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(ReadEventRow(reader));
        }

        return rows;
    }

    private static async ValueTask<CovenantReviewEvent?> ReadEventAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid datasetGeneration,
        CovenantScope scope,
        Guid? campaignId,
        CovenantLane lane,
        long eventSequence,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = EventSelect +
            """

            WHERE event.DatasetGeneration = $generation
              AND event.ScopeCode = $scope
              AND event.CampaignId IS $campaign
              AND event.LaneCode = $lane
              AND event.Sequence = $sequence;
            """;

        BindQueue(command, datasetGeneration, scope, campaignId, lane);
        Bind(command, "$sequence", eventSequence);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadEventRow(reader)
            : null;
    }

    private static async ValueTask<bool> EventIdentityMatchesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid datasetGeneration,
        CovenantScope scope,
        Guid? campaignId,
        CovenantLane lane,
        ulong eventSequence,
        MemoryReviewDigest expectedIdentity,
        CancellationToken cancellationToken)
    {
        CovenantReviewEvent? row = await ReadEventAsync(
            connection,
            transaction,
            datasetGeneration,
            scope,
            campaignId,
            lane,
            checked((long)eventSequence),
            cancellationToken).ConfigureAwait(false);

        return row is not null && VersionIdentity(row.VersionId).Equals(expectedIdentity);
    }

    private static async ValueTask<CovenantSourceDto[]> ReadSourcesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid versionId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT Ordinal, AttachmentId, AttachmentVersionIdentity, LogicalKey, ContentHash,
                   SourceRangeKindCode, SourceStart, SourceEnd, SourceTurnId, MaterializationReference
            FROM covenant_version_attachment_provenance
            WHERE VersionId = $version
            ORDER BY Ordinal;
            """;

        Bind(command, "$version", versionId.ToString("D", CultureInfo.InvariantCulture));

        List<CovenantSourceDto> sources = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            sources.Add(
                new CovenantSourceDto(
                    reader.GetInt32(0),
                    Guid.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                    reader.GetString(2),
                    reader.GetString(3),
                    Convert.ToHexString((byte[])reader.GetValue(4)),
                    (CovenantMaterializationSourceRange)reader.GetInt32(5),
                    reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    reader.IsDBNull(7) ? null : reader.GetInt64(7),
                    reader.IsDBNull(8)
                        ? null
                        : Guid.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
                    reader.IsDBNull(9) ? null : reader.GetString(9)));
        }

        return [.. sources];
    }

    private const string EventSelect =
        """
        SELECT event.Sequence, event.EntryId, event.VersionId,
               event.OperationCode, event.OriginCode, event.ScopeCode, event.CampaignId,
               event.LaneCode, entry.NormalizedKey, version.CompiledContent,
               event.SourceTurnId, event.SourceToolCallId, version.LaneRevision,
               version.AuthoredHash, version.RenderedHash, version.CompiledByteCost,
               version.AttachmentProvenanceCount, version.AttachmentProvenanceDigest,
               entry.CreatedAtUtc, head.UpdatedAtUtc,
               CASE WHEN head.CurrentVersionId = event.VersionId THEN 1 ELSE 0 END,
               EXISTS (
                   SELECT 1 FROM covenant_review_decision_receipts reviewed
                   WHERE reviewed.ReviewEventSequence = event.Sequence)
        FROM covenant_review_events event
        JOIN covenant_versions version ON version.VersionId = event.VersionId
        JOIN covenant_entries entry ON entry.EntryId = event.EntryId
        LEFT JOIN covenant_heads head
          ON head.EntryId = event.EntryId AND head.LaneCode = event.LaneCode
        """;

    private static CovenantReviewEvent ReadEventRow(SqliteDataReader reader) =>
        new(
            reader.GetInt64(0),
            Guid.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
            Guid.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
            (CovenantOperation)reader.GetInt32(3),
            (CovenantOrigin)reader.GetInt32(4),
            (CovenantScope)reader.GetInt32(5),
            reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
            (CovenantLane)reader.GetInt32(7),
            reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : Guid.Parse(reader.GetString(10), CultureInfo.InvariantCulture),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.GetInt64(12),
            reader.IsDBNull(13) ? null : new CovenantDigest((byte[])reader.GetValue(13)),
            reader.IsDBNull(14) ? null : new CovenantDigest((byte[])reader.GetValue(14)),
            reader.GetInt64(15),
            reader.GetInt64(16),
            new CovenantDigest((byte[])reader.GetValue(17)),
            Instant(reader.GetString(18)),
            Instant(reader.GetString(19)),
            reader.GetInt32(20) == 1,
            reader.GetInt32(21) == 1);

    private static void BindQueue(
        SqliteCommand command,
        Guid datasetGeneration,
        CovenantScope scope,
        Guid? campaignId,
        CovenantLane lane)
    {
        Bind(command, "$generation", GuidBytes(datasetGeneration));
        Bind(command, "$scope", (int)scope);
        Bind(command, "$campaign", CanonicalCampaign(campaignId));
        Bind(command, "$lane", (int)lane);
    }

    private async ValueTask<Result<AppliedDecision>> ApplyDecisionAsync(
        CovenantMutationTransaction transaction,
        CovenantAgentErasureGate erasureGate,
        CanonicalState state,
        CovenantOperationLeaseSnapshot lease,
        CovenantReviewBulkPrepareRequest request,
        MemoryReviewDigest requestDigest,
        int ordinal,
        CovenantReviewEvent target,
        DateTimeOffset committedAt,
        CancellationToken cancellationToken)
    {
        Guid? resultingVersionId = null;
        long? autoAcknowledgedEventSequence = null;
        string outcome = Outcome(request.Action);

        switch (request.Action)
        {
            case MemoryReviewAction.Confirm:
                break;

            case MemoryReviewAction.Correct:
            {
                CovenantCompiledContent compiled;

                try
                {
                    compiled = compiler.Compile(target.Key, request.Decisions[ordinal].ReplacementContent!);
                }
                catch (ArgumentException invalid)
                {
                    return new Error(ErrorCodes.Validation.InvalidBody, invalid.Message);
                }

                Result<CovenantMutationReceipt> mutation = await ApplyMutationAsync(
                    transaction,
                    erasureGate,
                    state,
                    lease,
                    request,
                    requestDigest,
                    ordinal,
                    target,
                    compiled,
                    committedAt,
                    cancellationToken).ConfigureAwait(false);

                if (mutation.IsFailure)
                {
                    return mutation.Error;
                }

                resultingVersionId = mutation.Value.ResultingVersionId;

                outcome = Outcome(request.Action, mutation.Value.Outcome);

                if (mutation.Value.Outcome == CovenantMutationOutcome.Applied
                    && resultingVersionId is null)
                {
                    return IntegrityFailure;
                }

                if (resultingVersionId is { } correctedVersion)
                {
                    autoAcknowledgedEventSequence = await ReadReviewEventSequenceAsync(
                        transaction.Connection,
                        transaction.Transaction,
                        correctedVersion,
                        cancellationToken).ConfigureAwait(false);
                }

                break;
            }

            case MemoryReviewAction.Retire:
            {
                Result<CovenantMutationReceipt> mutation = await ApplyMutationAsync(
                    transaction,
                    erasureGate,
                    state,
                    lease,
                    request,
                    requestDigest,
                    ordinal,
                    target,
                    compiled: null,
                    committedAt,
                    cancellationToken).ConfigureAwait(false);

                if (mutation.IsFailure)
                {
                    return mutation.Error;
                }

                resultingVersionId = mutation.Value.ResultingVersionId;
                outcome = Outcome(request.Action, mutation.Value.Outcome);
                break;
            }

            case MemoryReviewAction.Pin:
            case MemoryReviewAction.Unpin:
            {
                Result<CovenantCurationReceipt> curation = await ApplyCurationAsync(
                    transaction,
                    state,
                    lease,
                    request,
                    requestDigest,
                    ordinal,
                    target,
                    committedAt,
                    cancellationToken).ConfigureAwait(false);

                if (curation.IsFailure)
                {
                    return curation.Error;
                }

                resultingVersionId = curation.Value.ResultingVersionId;
                outcome = Outcome(request.Action, curation.Value.Outcome);
                break;
            }

            default:
                return new Error(
                    ErrorCodes.Validation.InvalidBody,
                    "A recognized memory review action is required.");
        }

        MemoryReviewBulkItemResultDto result = new(
            target.Sequence,
            Canonical(target.EntryId),
            Canonical(target.VersionId),
            outcome,
            resultingVersionId is { } resulting ? Canonical(resulting) : null);

        return new AppliedDecision(ordinal, target, result, autoAcknowledgedEventSequence);
    }

    private async ValueTask<Result<CovenantMutationReceipt>> ApplyMutationAsync(
        CovenantMutationTransaction transaction,
        CovenantAgentErasureGate erasureGate,
        CanonicalState state,
        CovenantOperationLeaseSnapshot lease,
        CovenantReviewBulkPrepareRequest request,
        MemoryReviewDigest requestDigest,
        int ordinal,
        CovenantReviewEvent target,
        CovenantCompiledContent? compiled,
        DateTimeOffset committedAt,
        CancellationToken cancellationToken)
    {
        CovenantKeyEpochPair epochs = await CovenantKeyEpochs.ReadAsync(transaction, target.Key, cancellationToken)
            .ConfigureAwait(false);

        long? registryEpoch = target.Scope == CovenantScope.Global
            ? await ReadCampaignRegistryEpochAsync(transaction, cancellationToken).ConfigureAwait(false)
            : null;

        CovenantOperatorMutationBinding binding = new(
            state.DatasetGeneration,
            checked((ulong)lease.AuthorityEpoch),
            epochs.Dependency,
            registryEpoch);

        Guid mutationId = DerivedMutationId(request.RequestId, ordinal, "mutation");
        CovenantDigest preflightDigest = ReviewDigest(requestDigest, ordinal, "preflight");
        CovenantOperationScope scope = OperationScope(target.Scope, target.CampaignId);

        Result<CovenantMutationIntent> intent = compiled is null
            ? CovenantOperatorMutationFactory.Retire(
                mutationId,
                scope,
                target.Key,
                target.Key,
                target.Lane,
                target.LaneRevision,
                binding,
                preflightDigest)
            : CovenantOperatorMutationFactory.Set(
                mutationId,
                scope,
                compiled,
                target.LaneRevision,
                reactivate: false,
                binding,
                preflightDigest);

        if (intent.IsFailure)
        {
            return intent.Error;
        }

        CovenantMutationBatch batch = new(
            state.DatasetGeneration,
            state.KeyReclamationEpoch,
            registryEpoch,
            committedAt,
            ImmutableArray.Create(intent.Value));

        Result<IReadOnlyList<CovenantMutationReceipt>> applied = await mutationKernel
            .ApplyBatchAsync(batch, transaction, erasureGate, cancellationToken)
            .ConfigureAwait(false);

        if (applied.IsFailure)
        {
            return applied.Error;
        }

        return applied.Value.Count == 1 ? applied.Value[0] : IntegrityFailure;
    }

    private async ValueTask<Result<CovenantCurationReceipt>> ApplyCurationAsync(
        CovenantMutationTransaction transaction,
        CanonicalState state,
        CovenantOperationLeaseSnapshot lease,
        CovenantReviewBulkPrepareRequest request,
        MemoryReviewDigest requestDigest,
        int ordinal,
        CovenantReviewEvent target,
        DateTimeOffset committedAt,
        CancellationToken cancellationToken)
    {
        // Both epochs are read inside the write transaction and both are stated. The kernel compares
        // the dependency epoch and checks the binding epoch it reads against the one asserted here, so
        // the curation head this pin is measured against below is the one the kernel then writes.
        CovenantKeyEpochPair epochs = await CovenantKeyEpochs.ReadAsync(transaction, target.Key, cancellationToken)
            .ConfigureAwait(false);

        CovenantCurationSubject subject = new(
            OperationScope(target.Scope, target.CampaignId),
            new CovenantKey(target.Key),
            target.Lane,
            epochs.Dependency,
            epochs.Binding);

        long revision = await ReadCurationRevisionAsync(transaction, subject, cancellationToken)
            .ConfigureAwait(false);

        CovenantOperatorMutationBinding binding = new(
            state.DatasetGeneration,
            checked((ulong)lease.AuthorityEpoch),
            state.KeyReclamationEpoch,
            CampaignRegistryEpoch: null);

        Result<CovenantCurationIntent> intent = CovenantOperatorCurationFactory.Curate(
            DerivedMutationId(request.RequestId, ordinal, "curation"),
            request.Action == MemoryReviewAction.Pin
                ? CovenantCurationKind.Pin
                : CovenantCurationKind.Unpin,
            subject,
            revision,
            binding,
            ReviewDigest(requestDigest, ordinal, "preflight"));

        if (intent.IsFailure)
        {
            return intent.Error;
        }

        CovenantCurationCommit commit = new(
            state.DatasetGeneration,
            state.KeyReclamationEpoch,
            committedAt,
            intent.Value);

        return await curationKernel.ApplyAsync(commit, transaction, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<long> ReadCampaignRegistryEpochAsync(
        CovenantMutationTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = transaction.CreateCommand();
        command.CommandText = "SELECT RegistryEpoch FROM campaign_registry_state WHERE StateKey = 1;";
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The revision of the subject's current curation head, or zero when it has none.
    /// </summary>
    /// <remarks>
    /// Looked up by the binding epoch, which is the epoch a curation head is recorded under. The
    /// dependency epoch moves on every write to the key, so a head sought by it would be missed after
    /// the first such write, and the pin or unpin would then be refused as a revision conflict.
    /// </remarks>
    private static async ValueTask<long> ReadCurationRevisionAsync(
        CovenantMutationTransaction transaction,
        CovenantCurationSubject subject,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = transaction.CreateCommand();
        command.CommandText =
            """
            SELECT COALESCE(MAX(CurrentRevision), 0)
            FROM covenant_curation_heads
            WHERE ScopeCode = $scope AND CampaignId IS $campaign
              AND NormalizedKey = $key AND LaneCode = $lane AND KeyEpoch = $epoch;
            """;
        Bind(command, "$scope", (int)subject.Scope.Kind);
        Bind(command, "$campaign", CanonicalCampaign(subject.Scope.CampaignId));
        Bind(command, "$key", subject.NormalizedKey.Value);
        Bind(command, "$lane", (int)subject.Lane);
        Bind(
            command,
            "$epoch",
            subject.KeyBindingEpoch
                ?? throw new InvalidOperationException("A curation revision is read under a resolved binding epoch."));
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async ValueTask<long> ReadReviewEventSequenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid versionId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT Sequence FROM covenant_review_events WHERE VersionId = $version;";
        Bind(command, "$version", versionId.ToString("D", CultureInfo.InvariantCulture));
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull
            ? throw new InvalidDataException("A Covenant head update did not produce its review event.")
            : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async ValueTask<long> ComputeReviewedThroughAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid datasetGeneration,
        CovenantScope scope,
        Guid? campaignId,
        CovenantLane lane,
        long current,
        HashSet<long> acknowledged,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT event.Sequence,
                   CASE WHEN head.CurrentVersionId = event.VersionId THEN 1 ELSE 0 END,
                   EXISTS (
                       SELECT 1 FROM covenant_review_decision_receipts receipt
                       WHERE receipt.ReviewEventSequence = event.Sequence)
            FROM covenant_review_events event
            LEFT JOIN covenant_heads head
              ON head.EntryId = event.EntryId AND head.LaneCode = event.LaneCode
            WHERE event.DatasetGeneration = $generation
              AND event.ScopeCode = $scope
              AND event.CampaignId IS $campaign
              AND event.LaneCode = $lane
              AND event.Sequence > $current
            ORDER BY event.Sequence;
            """;
        BindQueue(command, datasetGeneration, scope, campaignId, lane);
        Bind(command, "$current", current);

        long reviewedThrough = current;

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            long sequence = reader.GetInt64(0);
            bool currentHead = reader.GetInt32(1) == 1;
            bool receipted = reader.GetInt32(2) == 1;

            // Superseded events are resolved without a fabricated Confirm receipt. The scan only
            // contains this exact scope and lane, so unrelated interleaving sequences never block it.
            if (!acknowledged.Contains(sequence) && !receipted && currentHead)
            {
                break;
            }

            reviewedThrough = sequence;
        }

        return reviewedThrough;
    }

    private static async ValueTask<int> AdvanceMarkerAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CovenantScope scope,
        Guid? campaignId,
        CovenantLane lane,
        ReviewMarker expected,
        long reviewedThrough,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE covenant_review_markers
            SET ReviewedThroughSequence = $through, Revision = Revision + 1
            WHERE DatasetGeneration = $generation
              AND ScopeCode = $scope
              AND CampaignId IS $campaign
              AND LaneCode = $lane
              AND ReviewedThroughSequence = $expectedThrough
              AND Revision = $revision;
            """;
        BindQueue(command, expected.Generation, scope, campaignId, lane);
        Bind(command, "$through", reviewedThrough);
        Bind(command, "$expectedThrough", expected.ReviewedThroughSequence);
        Bind(command, "$revision", expected.Revision);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertDecisionReceiptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid datasetGeneration,
        CovenantReviewBulkPrepareRequest request,
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
            datasetGeneration,
            original,
            decision.Target.Sequence,
            decision.Target.EntryId,
            decision.Target.VersionId,
            requestDigest,
            cancellationToken).ConfigureAwait(false);

        if (decision.ReplacementEventSequence is not { } replacementSequence
            || decision.Result.ResultingVersionId is not { } resultingVersionText
            || !Guid.TryParse(resultingVersionText, out Guid resultingVersion))
        {
            return;
        }

        ReceiptEnvelope replacement = original with { Kind = ReceiptKind.Replacement };

        await InsertReceiptAsync(
            connection,
            transaction,
            datasetGeneration,
            replacement,
            replacementSequence,
            decision.Target.EntryId,
            resultingVersion,
            requestDigest,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertReceiptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid datasetGeneration,
        ReceiptEnvelope envelope,
        long eventSequence,
        Guid entryId,
        Guid versionId,
        MemoryReviewDigest requestDigest,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO covenant_review_decision_receipts (
                DecisionId, DatasetGeneration, ReviewEventSequence,
                RequestIdempotencyDigest, DecisionCode, ResponseReceiptDigest)
            VALUES ($decision, $generation, $event, $request, 1, $response);
            """;
        Bind(command, "$decision", FormatEnvelope(envelope));
        Bind(command, "$generation", GuidBytes(datasetGeneration));
        Bind(command, "$event", eventSequence);
        Bind(command, "$request", PerDecisionRequestDigest(requestDigest, envelope.Ordinal, envelope.Kind));
        Bind(
            command,
            "$response",
            ResponseDigest(requestDigest, envelope, eventSequence, entryId, versionId));
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    private static string Outcome(MemoryReviewAction action, CovenantMutationOutcome mutationOutcome) =>
        mutationOutcome switch
        {
            CovenantMutationOutcome.Applied => Outcome(action),
            CovenantMutationOutcome.NoChange => nameof(CovenantMutationOutcome.NoChange),
            _ => throw new ArgumentOutOfRangeException(nameof(mutationOutcome)),
        };

    private static bool IsExpectedOutcome(MemoryReviewAction action, string outcome) =>
        string.Equals(outcome, Outcome(action), StringComparison.Ordinal)
        || action != MemoryReviewAction.Confirm
            && string.Equals(outcome, nameof(CovenantMutationOutcome.NoChange), StringComparison.Ordinal);

    private static CovenantOperationScope OperationScope(CovenantScope scope, Guid? campaignId) =>
        scope == CovenantScope.Global
            ? CovenantOperationScope.Global
            : CovenantOperationScope.ForCampaign(campaignId!.Value);

    private static Guid DerivedMutationId(Guid requestId, int ordinal, string kind)
    {
        byte[] digest = SHA256.HashData(
            Encoding.UTF8.GetBytes($"covenant-review:{requestId:N}:{ordinal}:{kind}"));
        return new Guid(digest.AsSpan(0, 16));
    }

    private static CovenantDigest ReviewDigest(
        MemoryReviewDigest requestDigest,
        int ordinal,
        string purpose) =>
        new(DigestParts(
            "covenant-review-kernel-v1",
            Convert.ToHexString(requestDigest.Bytes),
            ordinal.ToString(CultureInfo.InvariantCulture),
            purpose));

    private static byte[] PerDecisionRequestDigest(
        MemoryReviewDigest requestDigest,
        int ordinal,
        ReceiptKind kind) =>
        DigestParts(
            "covenant-review-receipt-request-v1",
            Convert.ToHexString(requestDigest.Bytes),
            ordinal.ToString(CultureInfo.InvariantCulture),
            ((int)kind).ToString(CultureInfo.InvariantCulture));

    private static byte[] ResponseDigest(
        MemoryReviewDigest requestDigest,
        ReceiptEnvelope envelope,
        long eventSequence,
        Guid entryId,
        Guid versionId) =>
        DigestParts(
            "covenant-review-receipt-response-v1",
            Convert.ToHexString(requestDigest.Bytes),
            FormatEnvelope(envelope),
            eventSequence.ToString(CultureInfo.InvariantCulture),
            Canonical(entryId),
            Canonical(versionId));

    private static byte[] DigestParts(params string?[] parts)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string? part in parts)
        {
            AppendHashPart(hash, part);
        }

        return hash.GetHashAndReset();
    }

    private static string FormatEnvelope(ReceiptEnvelope envelope) =>
        string.Join(
            ':',
            envelope.RequestId.ToString("N", CultureInfo.InvariantCulture),
            envelope.Ordinal.ToString(CultureInfo.InvariantCulture),
            envelope.Kind == ReceiptKind.Original ? "O" : "R",
            ((int)envelope.Action).ToString(CultureInfo.InvariantCulture),
            envelope.Outcome,
            envelope.ReviewedThrough.ToString(CultureInfo.InvariantCulture),
            envelope.ResultingVersionId ?? "-");

    private static async ValueTask<ReplayLookup> ReadReplayAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CovenantReviewBulkPrepareRequest request,
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

        Guid currentGeneration = await ReadDatasetGenerationAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);

        Dictionary<int, StoredReceipt> originals = [];
        Dictionary<int, StoredReceipt> replacements = [];
        long? reviewedThrough = null;

        foreach (StoredReceipt stored in receipts)
        {
            if (stored.IsCorrupt || stored.DatasetGeneration != currentGeneration)
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

            byte[] expectedRequest = PerDecisionRequestDigest(
                requestDigest,
                envelope.Ordinal,
                envelope.Kind);

            if (!CryptographicOperations.FixedTimeEquals(expectedRequest, stored.RequestIdempotencyDigest))
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

            CovenantReviewEvent? originalEvent = await ReadEventAsync(
                connection,
                transaction,
                currentGeneration,
                request.Scope,
                request.CampaignId,
                request.Lane,
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
                originalEvent.EntryId,
                originalEvent.VersionId);

            if (!CryptographicOperations.FixedTimeEquals(expectedResponse, original.ResponseReceiptDigest))
            {
                return ReplayLookup.Failed(IntegrityFailure);
            }

            bool validResult = await ResultingVersionExistsAsync(
                connection,
                transaction,
                request.Action,
                original.Envelope.Outcome,
                original.Envelope.ResultingVersionId,
                cancellationToken).ConfigureAwait(false);

            if (!validResult)
            {
                return ReplayLookup.Failed(IntegrityFailure);
            }

            if (replacements.TryGetValue(ordinal, out StoredReceipt? replacement))
            {
                bool corrected = request.Action == MemoryReviewAction.Correct
                    && string.Equals(
                        original.Envelope.Outcome,
                        Outcome(MemoryReviewAction.Correct),
                        StringComparison.Ordinal);

                if (!corrected
                    || replacement.Envelope.ResultingVersionId is null
                    || replacement.Envelope.ResultingVersionId != original.Envelope.ResultingVersionId)
                {
                    return ReplayLookup.Failed(IntegrityFailure);
                }

                CovenantReviewEvent? replacementEvent = await ReadEventAsync(
                    connection,
                    transaction,
                    currentGeneration,
                    request.Scope,
                    request.CampaignId,
                    request.Lane,
                    replacement.EventSequence,
                    cancellationToken).ConfigureAwait(false);

                if (replacementEvent is null
                    || !string.Equals(
                        Canonical(replacementEvent.VersionId),
                        replacement.Envelope.ResultingVersionId,
                        StringComparison.Ordinal))
                {
                    return ReplayLookup.Failed(IntegrityFailure);
                }

                byte[] replacementResponse = ResponseDigest(
                    requestDigest,
                    replacement.Envelope,
                    replacement.EventSequence,
                    replacementEvent.EntryId,
                    replacementEvent.VersionId);

                if (!CryptographicOperations.FixedTimeEquals(
                    replacementResponse,
                    replacement.ResponseReceiptDigest))
                {
                    return ReplayLookup.Failed(IntegrityFailure);
                }
            }
            else if (request.Action == MemoryReviewAction.Correct
                && string.Equals(
                    original.Envelope.Outcome,
                    Outcome(MemoryReviewAction.Correct),
                    StringComparison.Ordinal))
            {
                return ReplayLookup.Failed(IntegrityFailure);
            }

            results[ordinal] = new MemoryReviewBulkItemResultDto(
                originalEvent.Sequence,
                Canonical(originalEvent.EntryId),
                Canonical(originalEvent.VersionId),
                original.Envelope.Outcome,
                original.Envelope.ResultingVersionId);
        }

        return ReplayLookup.Succeeded(
            new MemoryReviewBulkResultDto(
                MemoryReviewStore.Covenant,
                request.RequestId,
                request.Action,
                results,
                reviewedThrough ?? 0,
                Replayed: true));
    }

    private static async ValueTask<List<StoredReceipt>> ReadReceiptsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT DecisionId, DatasetGeneration, ReviewEventSequence,
                   RequestIdempotencyDigest, ResponseReceiptDigest
            FROM covenant_review_decision_receipts
            WHERE substr(DecisionId, 1, length($prefix)) = $prefix
            ORDER BY DecisionId;
            """;
        Bind(command, "$prefix", requestId.ToString("N", CultureInfo.InvariantCulture) + ":");

        List<StoredReceipt> receipts = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

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
                    GuidFromBlob(reader.GetValue(1)),
                    reader.GetInt64(2),
                    (byte[])reader.GetValue(3),
                    (byte[])reader.GetValue(4)));
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
            || !long.TryParse(parts[5], NumberStyles.None, CultureInfo.InvariantCulture, out long through)
            || through < 0)
        {
            envelope = null;
            return false;
        }

        string? resultingVersion = parts[6] == "-" ? null : parts[6];

        if (resultingVersion is { } version
            && (!Guid.TryParse(version, out Guid parsed) || !string.Equals(Canonical(parsed), version, StringComparison.Ordinal)))
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
            through,
            resultingVersion);
        return true;
    }

    private static async ValueTask<Guid> ReadDatasetGenerationAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT DatasetGeneration FROM covenant_state WHERE StateKey = 1;";
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull
            ? throw new InvalidDataException("Covenant canonical state is absent.")
            : GuidFromBlob(value);
    }

    private static async ValueTask<bool> ResultingVersionExistsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        MemoryReviewAction action,
        string outcome,
        string? resultingVersionId,
        CancellationToken cancellationToken)
    {
        if (string.Equals(outcome, nameof(CovenantMutationOutcome.NoChange), StringComparison.Ordinal))
        {
            return action != MemoryReviewAction.Confirm && resultingVersionId is null;
        }

        if (!string.Equals(outcome, Outcome(action), StringComparison.Ordinal))
        {
            return false;
        }

        if (action == MemoryReviewAction.Confirm)
        {
            return resultingVersionId is null;
        }

        if (resultingVersionId is null)
        {
            return false;
        }

        if (!Guid.TryParse(resultingVersionId, out Guid parsed))
        {
            return false;
        }

        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT CASE
                WHEN $canonical = 1 THEN EXISTS (
                    SELECT 1 FROM covenant_versions WHERE VersionId = $version)
                ELSE EXISTS (
                    SELECT 1 FROM covenant_curation_versions WHERE CurationVersionId = $version)
            END;
            """;
        Bind(
            command,
            "$canonical",
            action is MemoryReviewAction.Correct or MemoryReviewAction.Retire ? 1 : 0);
        Bind(command, "$version", parsed.ToString("D", CultureInfo.InvariantCulture));
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture) == 1;
    }

    private static void Bind(SqliteCommand command, string name, object? value) =>
        _ = command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private sealed record CanonicalState(Guid DatasetGeneration, long KeyReclamationEpoch);

    private sealed record ReviewMarker(Guid Generation, long ReviewedThroughSequence, long Revision);

    private sealed record AppliedDecision(
        int Ordinal,
        CovenantReviewEvent Target,
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
        Guid DatasetGeneration,
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
                Guid.Empty,
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

    private sealed record CovenantReviewEvent(
        long Sequence,
        Guid EntryId,
        Guid VersionId,
        CovenantOperation Operation,
        CovenantOrigin Origin,
        CovenantScope Scope,
        Guid? CampaignId,
        CovenantLane Lane,
        string Key,
        string? CompiledContent,
        Guid? SourceTurnId,
        string? SourceToolCallId,
        long LaneRevision,
        CovenantDigest? AuthoredHash,
        CovenantDigest? RenderedHash,
        long CompiledByteCost,
        long ProvenanceCount,
        CovenantDigest ProvenanceDigest,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc,
        bool IsCurrent,
        bool Reviewed);
}
