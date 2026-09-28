using System.Data.Common;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Annals;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Lexicon;

internal sealed partial class LexiconService
{
    private const int LexiconReviewStoreCode = 2;

    private static readonly Error InvalidReviewToken = new(
        ErrorCodes.MemoryReview.InvalidToken,
        "The Lexicon review token is invalid, stale, or belongs to another queue.");

    private static readonly Error StaleReviewObservation = new(
        ErrorCodes.MemoryReview.StaleObservation,
        "The reviewed Lexicon version is no longer current.");

    private static readonly Error UnseenReviewObservation = new(
        ErrorCodes.MemoryReview.UnseenObservation,
        "The reviewed Lexicon event does not exist in this exact queue.");

    private static readonly Error ReviewIntegrityFailure = new(
        ErrorCodes.MemoryReview.IntegrityFailure,
        "The durable Lexicon review evidence is inconsistent.");

    public async Task<Result<LexiconReviewPageDto>> ListAsync(
        LexiconReviewListRequest request,
        ICovenantSnapshotReadLease? readLease,
        CancellationToken cancellationToken)
    {
        if (request is null || request.Validate().IsFailure)
        {
            return new Error(ErrorCodes.Validation.InvalidBody, "A valid Lexicon review page request is required.");
        }

        Result<LexiconInspectionResult<LexiconReviewPageDto>> result = await InInspectionSnapshotAsync(
            readLease,
            request.Scope,
            connection => ReadReviewPageAsync(connection, request, readLease, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        return result.IsFailure ? result.Error : result.Value.Value;
    }

    public async Task<Result<MemoryReviewBulkPlanDto>> PrepareAsync(
        LexiconReviewBulkPrepareRequest request,
        ICovenantSnapshotReadLease? readLease,
        CancellationToken cancellationToken)
    {
        if (request is null || request.Validate().IsFailure)
        {
            return new Error(ErrorCodes.Validation.InvalidBody, "A valid Lexicon review request is required.");
        }

        Result<LexiconInspectionResult<MemoryReviewBulkPlanDto>> result = await InInspectionSnapshotAsync(
            readLease,
            request.Scope,
            connection => PrepareReviewAsync(connection, request, readLease, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        return result.IsFailure ? result.Error : result.Value.Value;
    }

    public async Task<Result<MemoryReviewBulkResultDto>> ApplyAsync(
        LexiconReviewBulkApplyRequest request,
        CovenantWriteLease? writeLease,
        CancellationToken cancellationToken)
    {
        if (request is null || request.Validate().IsFailure)
        {
            return new Error(ErrorCodes.Validation.InvalidBody, "A valid prepared Lexicon review request is required.");
        }

        try
        {
            DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            MemoryReviewDigest orderedDigest = OrderedRequestDigest(request.Request);

            ReviewReplay? replay = await TryReadReviewReplayAsync(
                connection, request.Request, orderedDigest, cancellationToken).ConfigureAwait(false);

            if (replay is not null)
            {
                await RequireReplayAuthorityAsync(
                    replay.ContainsProtectedContent, writeLease, request.Request.Scope, cancellationToken).ConfigureAwait(false);

                return replay.Result;
            }

            Result<MemoryReviewPreparedPlanTokenFacts> decoded = _reviewTokenCodec.ReadPreparedPlan(request.PreparedPlanToken);

            if (decoded.IsFailure)
            {
                return InvalidReviewToken;
            }

            ReviewScopeFacts scope = ScopeFacts(request.Request.Scope);

            if (!Matches(decoded.Value, scope, orderedDigest))
            {
                return InvalidReviewToken;
            }

            Require(await ValidateCurationLeaseAsync(writeLease, request.Request.Scope, cancellationToken).ConfigureAwait(false));

            return await SqliteBusyRetry.ExecuteAsync(async () =>
            {
                await ExecuteNonQueryAsync(connection, cancellationToken, "BEGIN IMMEDIATE").ConfigureAwait(false);

                try
                {
                    ReviewReplay? lockedReplay = await TryReadReviewReplayAsync(
                        connection, request.Request, orderedDigest, cancellationToken).ConfigureAwait(false);

                    if (lockedReplay is not null)
                    {
                        await RequireReplayAuthorityAsync(
                            lockedReplay.ContainsProtectedContent,
                            writeLease,
                            request.Request.Scope,
                            cancellationToken).ConfigureAwait(false);

                        await ExecuteNonQueryAsync(connection, cancellationToken, "COMMIT").ConfigureAwait(false);

                        return Result<MemoryReviewBulkResultDto>.Success(lockedReplay.Result);
                    }

                    ReviewMarker marker = await ReadOrCreateReviewMarkerAsync(
                        connection, request.Request.Scope, cancellationToken).ConfigureAwait(false);

                    if (!Matches(decoded.Value, marker, scope))
                    {
                        throw new InspectionException(InvalidReviewToken);
                    }

                    List<ObservedReviewEvent> observations = await ReadObservationsAsync(
                        connection, request.Request, marker, scope, cancellationToken).ConfigureAwait(false);

                    List<MemoryReviewBulkItemResultDto> items = [];

                    Dictionary<int, MemoryReviewBulkItemResultDto> replacements = [];

                    Dictionary<int, bool> protectedItems = [];

                    for (int index = 0; index < observations.Count; index++)
                    {
                        ObservedReviewEvent observed = observations[index];

                        if (!observed.IsCurrent)
                        {
                            throw new InspectionException(StaleReviewObservation);
                        }

                        CurationState state = await ReadReviewStateAsync(
                            connection, observed.EntryId, request.Request.Scope, cancellationToken).ConfigureAwait(false);

                        protectedItems[index] = state.Label is not null;

                        if (state.Label is not null && writeLease is null)
                        {
                            throw new InspectionException(new Error(
                                ErrorCodes.Lexicon.ProtectedMutationRefused,
                                "A protected Lexicon review action requires an exact-scope write capability."));
                        }

                        ReviewActionResult actionResult = await ApplyReviewActionAsync(
                            connection,
                            request.Request.Action,
                            request.Request.Decisions[index].ReplacementContent,
                            state,
                            cancellationToken).ConfigureAwait(false);

                        MemoryReviewBulkItemResultDto item = new(
                            observed.Sequence,
                            observed.EntryId.ToString("D"),
                            observed.VersionId,
                            actionResult.Outcome,
                            actionResult.ResultingVersionId);

                        if (request.Request.Action == MemoryReviewAction.Correct
                            && actionResult.ResultingVersionId is not null)
                        {
                            replacements[index] = await ReadCorrectionOutputAsync(
                                connection, actionResult.ResultingVersionId, cancellationToken).ConfigureAwait(false);
                        }

                        items.Add(item);
                    }

                    HashSet<long> pendingAcknowledgements =
                    [
                        .. items.Select(static item => item.EventSequence),
                        .. replacements.Values.Select(static item => item.EventSequence),
                    ];

                    long reviewedThrough = await AdvanceReviewMarkerAsync(
                        connection,
                        request.Request.Scope,
                        marker,
                        pendingAcknowledgements,
                        cancellationToken).ConfigureAwait(false);

                    for (int index = 0; index < items.Count; index++)
                    {
                        await WriteReviewReceiptAsync(
                            connection,
                            request.Request,
                            index,
                            orderedDigest,
                            items[index],
                            items[index],
                            reviewedThrough,
                            protectedItems[index],
                            ReceiptKind.Original,
                            cancellationToken).ConfigureAwait(false);

                        if (replacements.TryGetValue(index, out MemoryReviewBulkItemResultDto? replacement))
                        {
                            await WriteReviewReceiptAsync(
                                connection,
                                request.Request,
                                index,
                                orderedDigest,
                                items[index],
                                replacement,
                                reviewedThrough,
                                protectedItems[index],
                                ReceiptKind.Replacement,
                                cancellationToken).ConfigureAwait(false);
                        }
                    }

                    Require(await ValidateCurationLeaseAsync(writeLease, request.Request.Scope, cancellationToken).ConfigureAwait(false));

                    await ExecuteNonQueryAsync(connection, cancellationToken, "COMMIT").ConfigureAwait(false);

                    return Result<MemoryReviewBulkResultDto>.Success(new(
                        MemoryReviewStore.Lexicon,
                        request.Request.RequestId,
                        request.Request.Action,
                        [.. items],
                        reviewedThrough,
                        Replayed: false));
                }
                catch
                {
                    await TryRollbackAsync(connection, "Lexicon memory review").ConfigureAwait(false);

                    throw;
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (InspectionException exception)
        {
            return exception.Error;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Lexicon memory review apply failed.");

            return new Error(ErrorCodes.Lexicon.WriteFailed, "The Lexicon review action could not be persisted.");
        }
    }

    private static async Task RequireReplayAuthorityAsync(
        bool containsProtectedContent,
        CovenantWriteLease? writeLease,
        LexiconCurationScope scope,
        CancellationToken cancellationToken)
    {
        Require(await ValidateCurationLeaseAsync(writeLease, scope, cancellationToken).ConfigureAwait(false));

        if (containsProtectedContent && writeLease is null)
        {
            throw new InspectionException(new Error(
                ErrorCodes.Lexicon.ProtectedMutationRefused,
                "A protected Lexicon review replay requires an exact-scope write capability."));
        }
    }

    private async Task<LexiconInspectionResult<LexiconReviewPageDto>> ReadReviewPageAsync(
        DbConnection connection,
        LexiconReviewListRequest request,
        ICovenantSnapshotReadLease? readLease,
        CancellationToken cancellationToken)
    {
        ReviewScopeFacts scope = ScopeFacts(request.Scope);

        ReviewMarker marker = await ReadOrCreateReviewMarkerAsync(connection, request.Scope, cancellationToken).ConfigureAwait(false);

        long lower = marker.ReviewedThroughSequence + 1;

        long upper = await ReadReviewUpperFrontierAsync(connection, scope, cancellationToken).ConfigureAwait(false);

        long keyset = 0;

        if (request.Cursor is not null)
        {
            Result<MemoryReviewCursorTokenFacts> decoded = _reviewTokenCodec.ReadCursor(request.Cursor);

            if (decoded.IsFailure || !Matches(decoded.Value, marker, scope))
            {
                throw new InspectionException(InvalidReviewToken);
            }

            if (!await EventIdentityMatchesAsync(
                    connection,
                    scope,
                    decoded.Value.KeysetEventSequence,
                    decoded.Value.KeysetVersionIdentity,
                    cancellationToken).ConfigureAwait(false))
            {
                throw new InspectionException(InvalidReviewToken);
            }

            lower = checked((long)decoded.Value.FrozenLowerEventSequence);
            upper = checked((long)decoded.Value.FrozenUpperEventSequence);
            keyset = checked((long)decoded.Value.KeysetEventSequence);
        }

        List<ReviewEventRow> rows = await ReadReviewEventsAsync(
            connection, scope, lower, upper, keyset, request.Limit + 1, cancellationToken).ConfigureAwait(false);

        bool truncated = rows.Count > request.Limit;

        if (truncated)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        bool protectedContent = false;

        List<LexiconReviewItemDto> items = [];

        foreach (ReviewEventRow row in rows)
        {
            ReviewCurrentState state = await ReadReviewCurrentStateAsync(
                connection, row.EntryId, request.Scope, readLease, cancellationToken).ConfigureAwait(false);

            protectedContent |= state.Label is not null;

            bool current = state.Head?.VersionId == row.VersionId;

            Result<string> observation = _reviewTokenCodec.IssueObservation(new(
                MemoryReviewStore.Lexicon,
                scope.Digest,
                marker.Generation,
                checked((ulong)marker.Revision),
                checked((ulong)lower),
                checked((ulong)upper),
                checked((ulong)row.Sequence),
                VersionDigest(row.VersionId)));

            Require(observation.IsFailure ? Result.Failure(observation.Error) : Result.Success());

            items.Add(new(
                row.Sequence,
                row.EntryId,
                row.VersionId,
                row.Revision,
                row.Operation,
                row.Origin,
                request.Scope,
                row.SourceSessionId,
                row.ContentHash,
                current,
                current ? state.Current : null,
                observation.Value));
        }

        string? next = null;

        if (truncated && items.Count > 0)
        {
            LexiconReviewItemDto last = items[^1];

            Result<string> cursor = _reviewTokenCodec.IssueCursor(new(
                MemoryReviewStore.Lexicon,
                scope.Digest,
                marker.Generation,
                checked((ulong)marker.Revision),
                checked((ulong)lower),
                checked((ulong)upper),
                checked((ulong)last.EventSequence),
                VersionDigest(last.VersionId)));

            Require(cursor.IsFailure ? Result.Failure(cursor.Error) : Result.Success());

            next = cursor.Value;
        }

        return new(
            new(
                [.. items],
                marker.ReviewedThroughSequence,
                upper,
                next,
                truncated,
                protectedContent),
            protectedContent);
    }

    private async Task<LexiconInspectionResult<MemoryReviewBulkPlanDto>> PrepareReviewAsync(
        DbConnection connection,
        LexiconReviewBulkPrepareRequest request,
        ICovenantSnapshotReadLease? readLease,
        CancellationToken cancellationToken)
    {
        ReviewScopeFacts scope = ScopeFacts(request.Scope);

        ReviewMarker marker = await ReadOrCreateReviewMarkerAsync(connection, request.Scope, cancellationToken).ConfigureAwait(false);

        List<ObservedReviewEvent> observations = await ReadObservationsAsync(
            connection, request, marker, scope, cancellationToken).ConfigureAwait(false);

        bool protectedContent = false;

        foreach (ObservedReviewEvent observed in observations)
        {
            if (!observed.IsCurrent)
            {
                throw new InspectionException(StaleReviewObservation);
            }

            CurationState state = await ReadReviewStateAsync(
                connection, observed.EntryId, request.Scope, cancellationToken).ConfigureAwait(false);

            RequireProtectedLease(state.Label, readLease);

            protectedContent |= state.Label is not null;
        }

        MemoryReviewObservationTokenFacts first = observations[0].Token;

        MemoryReviewDigest orderedDigest = OrderedRequestDigest(request);

        Result<string> prepared = _reviewTokenCodec.IssuePreparedPlan(new(
            MemoryReviewStore.Lexicon,
            scope.Digest,
            marker.Generation,
            checked((ulong)marker.Revision),
            first.FrozenLowerEventSequence,
            first.FrozenUpperEventSequence,
            orderedDigest));

        Require(prepared.IsFailure ? Result.Failure(prepared.Error) : Result.Success());

        DateTimeOffset issued = _reviewTimeProvider.GetUtcNow();

        return new(
            new(
                MemoryReviewStore.Lexicon,
                request.RequestId,
                request.Action,
                [.. observations.Select(static observed => new MemoryReviewBulkPlanItemDto(
                    observed.Sequence,
                    observed.EntryId.ToString("D"),
                    observed.VersionId,
                    observed.IsCurrent,
                    observed.Origin.ToString(),
                    observed.SourceSessionId?.ToString("D") ?? string.Empty,
                    observed.Scope.Kind.ToString()))],
                issued,
                issued + MemoryReviewLimits.TokenLifetime,
                prepared.Value),
            protectedContent);
    }

    private async Task<List<ObservedReviewEvent>> ReadObservationsAsync(
        DbConnection connection,
        LexiconReviewBulkPrepareRequest request,
        ReviewMarker marker,
        ReviewScopeFacts scope,
        CancellationToken cancellationToken)
    {
        List<ObservedReviewEvent> observations = [];

        HashSet<(ulong EventSequence, MemoryReviewDigest VersionIdentity)> exactVersions = [];

        MemoryReviewObservationTokenFacts? common = null;

        foreach (LexiconReviewDecision decision in request.Decisions)
        {
            Result<MemoryReviewObservationTokenFacts> decoded = _reviewTokenCodec.ReadObservation(decision.ObservationToken);

            if (decoded.IsFailure || !Matches(decoded.Value, marker, scope))
            {
                throw new InspectionException(InvalidReviewToken);
            }

            if (common is not null && !SameFrontier(common, decoded.Value))
            {
                throw new InspectionException(InvalidReviewToken);
            }

            if (!exactVersions.Add((decoded.Value.EventSequence, decoded.Value.VersionIdentity)))
            {
                throw new InspectionException(InvalidReviewToken);
            }

            ReviewEventRow row = await ReadReviewEventAsync(
                connection, checked((long)decoded.Value.EventSequence), scope, cancellationToken).ConfigureAwait(false);

            if (VersionDigest(row.VersionId) != decoded.Value.VersionIdentity)
            {
                throw new InspectionException(InvalidReviewToken);
            }

            observations.Add(new(
                row.Sequence,
                row.EntryId,
                row.VersionId,
                row.Origin,
                row.SourceSessionId,
                request.Scope,
                row.IsCurrent,
                decoded.Value));

            common = decoded.Value;
        }

        return observations;
    }

    private static async Task<ReviewMarker> ReadOrCreateReviewMarkerAsync(
        DbConnection connection,
        LexiconCurationScope scope,
        CancellationToken cancellationToken)
    {
        await using (DbCommand insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT OR IGNORE INTO annal_review_markers
                    (SubjectStoreCode, ScopeKindCode, CampaignId, MarkerGeneration, ReviewedThroughSequence, Revision)
                VALUES (2, @scope, @campaign, @generation, 0, 1)
                """;

            AddParameter(insert, "@scope", ScopeCode(scope));
            AddParameter(insert, "@campaign", scope.CampaignId?.ToString("D") ?? (object)DBNull.Value);
            AddParameter(insert, "@generation", Guid.NewGuid().ToByteArray());

            _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using DbCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT MarkerGeneration, ReviewedThroughSequence, Revision
            FROM annal_review_markers
            WHERE SubjectStoreCode = 2 AND ScopeKindCode = @scope
                AND ((@campaign IS NULL AND CampaignId IS NULL) OR CampaignId = @campaign)
            """;

        AddParameter(command, "@scope", ScopeCode(scope));
        AddParameter(command, "@campaign", scope.CampaignId?.ToString("D") ?? (object)DBNull.Value);

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        RequireIntegrity(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));

        byte[] generation = (byte[])reader.GetValue(0);

        RequireIntegrity(generation.Length == 16 && reader.GetInt64(1) >= 0 && reader.GetInt64(2) > 0);

        return new(new Guid(generation), reader.GetInt64(1), reader.GetInt64(2));
    }

    private static async Task<long> ReadReviewUpperFrontierAsync(
        DbConnection connection,
        ReviewScopeFacts scope,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT coalesce(max(Sequence), 0) FROM annal_review_events
            WHERE SubjectStoreCode = 2 AND ScopeKindCode = @scope
                AND ((@campaign IS NULL AND CampaignId IS NULL) OR CampaignId = @campaign)
            """;

        AddParameter(command, "@scope", scope.ScopeCode);
        AddParameter(command, "@campaign", scope.CampaignId ?? (object)DBNull.Value);

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async Task<List<ReviewEventRow>> ReadReviewEventsAsync(
        DbConnection connection,
        ReviewScopeFacts scope,
        long lower,
        long upper,
        long keyset,
        int limit,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT event.Sequence, event.VersionId, event.SubjectId,
                   version.Revision, version.OperationCode, version.OriginCode,
                   version.SourceSessionId, version.ContentHash,
                   CASE WHEN head.CurrentVersionId = event.VersionId THEN 1 ELSE 0 END
            FROM annal_review_events event
            JOIN annal_versions version ON version.VersionId = event.VersionId
            JOIN annal_heads head ON head.ClaimId = event.ClaimId
            WHERE event.SubjectStoreCode = 2 AND event.ScopeKindCode = @scope
                AND ((@campaign IS NULL AND event.CampaignId IS NULL) OR event.CampaignId = @campaign)
                AND event.Sequence BETWEEN @lower AND @upper
                AND (@keyset = 0 OR event.Sequence < @keyset)
                AND head.CurrentVersionId = event.VersionId
                AND NOT EXISTS(SELECT 1 FROM annal_review_decision_receipts receipt
                               WHERE receipt.ReviewEventSequence = event.Sequence)
            ORDER BY event.Sequence DESC LIMIT @limit
            """;

        AddParameter(command, "@scope", scope.ScopeCode);
        AddParameter(command, "@campaign", scope.CampaignId ?? (object)DBNull.Value);
        AddParameter(command, "@lower", lower);
        AddParameter(command, "@upper", upper);
        AddParameter(command, "@keyset", keyset);
        AddParameter(command, "@limit", limit);

        List<ReviewEventRow> rows = [];

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(ReadReviewEvent(reader));
        }

        return rows;
    }

    private static async Task<ReviewEventRow> ReadReviewEventAsync(
        DbConnection connection,
        long sequence,
        ReviewScopeFacts scope,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT event.Sequence, event.VersionId, event.SubjectId,
                   version.Revision, version.OperationCode, version.OriginCode,
                   version.SourceSessionId, version.ContentHash,
                   CASE WHEN head.CurrentVersionId = event.VersionId THEN 1 ELSE 0 END
            FROM annal_review_events event
            JOIN annal_versions version ON version.VersionId = event.VersionId
            JOIN annal_heads head ON head.ClaimId = event.ClaimId
            WHERE event.Sequence = @sequence AND event.SubjectStoreCode = 2 AND event.ScopeKindCode = @scope
                AND ((@campaign IS NULL AND event.CampaignId IS NULL) OR event.CampaignId = @campaign)
                AND NOT EXISTS(SELECT 1 FROM annal_review_decision_receipts receipt
                               WHERE receipt.ReviewEventSequence = event.Sequence)
            """;

        AddParameter(command, "@sequence", sequence);
        AddParameter(command, "@scope", scope.ScopeCode);
        AddParameter(command, "@campaign", scope.CampaignId ?? (object)DBNull.Value);

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InspectionException(UnseenReviewObservation);
        }

        return ReadReviewEvent(reader);
    }

    private static ReviewEventRow ReadReviewEvent(DbDataReader reader)
    {
        Guid entryId = Guid.Parse(reader.GetString(2));

        RequireIntegrity(entryId != Guid.Empty);

        return new(
            reader.GetInt64(0),
            reader.GetString(1),
            entryId,
            checked((int)reader.GetInt64(3)),
            (AnnalOperation)checked((int)reader.GetInt64(4)),
            (AnnalOrigin)checked((int)reader.GetInt64(5)),
            reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)),
            reader.IsDBNull(7) ? null : Convert.ToHexString((byte[])reader.GetValue(7)),
            reader.GetInt64(8) == 1);
    }

    private async Task<CurationState> ReadReviewStateAsync(
        DbConnection connection,
        Guid entryId,
        LexiconCurationScope scope,
        CancellationToken cancellationToken)
    {
        InspectionRow row = await ReadInspectionRowAsync(connection, entryId, cancellationToken).ConfigureAwait(false);

        RequireIntegrity(ScopeForEntry(row.Entry) == scope);

        row = row with
        {
            Entry = row.Entry with
            {
                FactProvenance = await ReadFactProvenanceAsync(connection, entryId, cancellationToken).ConfigureAwait(false),
            },
        };

        VerifyCurrentProvenance(row.Entry);

        ArtifactSensitivityLabel? label = await ReadVerifiedLabelAsync(connection, entryId, scope, cancellationToken).ConfigureAwait(false);

        AnnalClaimVersion? head = await ReadVerifiedHeadAsync(connection, row, label, cancellationToken).ConfigureAwait(false);

        AnnalClaimVersion[] history = head is null
            ? []
            : await ReadInspectionHistoryAsync(connection, head, scope, cancellationToken).ConfigureAwait(false);

        LexiconAnnalFactProvenance[] sources = head is null
            ? []
            : await ReadHistoricalSourcesAsync(connection, head.ClaimId, cancellationToken).ConfigureAwait(false);

        LexiconEntryLifecycle lifecycle = new(row.Entry.RetiredAtUtc, row.Entry.PinnedAtUtc);

        LexiconCurationTarget target = new(
            scope,
            row.Canonical.NameNormalized,
            entryId,
            row.Entry.CurationGeneration,
            LexiconSnapshotDigest.ComputeHex(row.Canonical),
            lifecycle,
            head is null
                ? new(false, null, null, null, null, null, null)
                : new(true, head.ClaimId, head.VersionId, head.Revision, head.Operation,
                    head.ContentHashFormat, head.ContentHash is null ? null : Convert.ToHexString(head.ContentHash)),
            label is null
                ? new(false, null, null, null, null)
                : new(true, label.LabelId, label.ArtifactRevision,
                    Convert.ToHexString(label.ArtifactContentDigest.Bytes), label.Provenance));

        RequireIntegrity(target.Validate().IsSuccess);

        LexiconEntryDetail detail = new(
            row.Entry,
            scope,
            head?.Origin,
            lifecycle,
            row.Entry.Eligibility,
            row.Entry.CurationGeneration,
            LexiconSnapshotDigest.ComputeHex(row.Canonical),
            target,
            history,
            sources);

        return new(row, label, head, detail);
    }

    private async Task<ReviewCurrentState> ReadReviewCurrentStateAsync(
        DbConnection connection,
        Guid entryId,
        LexiconCurationScope scope,
        ICovenantSnapshotReadLease? readLease,
        CancellationToken cancellationToken)
    {
        ArtifactSensitivityLabel? label = await ReadVerifiedLabelAsync(
            connection, entryId, scope, cancellationToken).ConfigureAwait(false);

        RequireProtectedLease(label, readLease);

        InspectionRow row = await ReadInspectionRowAsync(connection, entryId, cancellationToken).ConfigureAwait(false);

        RequireIntegrity(ScopeForEntry(row.Entry) == scope);

        AnnalClaimVersion? head = await ReadVerifiedHeadAsync(connection, row, label, cancellationToken).ConfigureAwait(false);

        LexiconEntryLifecycle lifecycle = new(row.Entry.RetiredAtUtc, row.Entry.PinnedAtUtc);

        string digest = LexiconSnapshotDigest.ComputeHex(row.Canonical);

        LexiconCurationTarget target = new(
            scope,
            row.Canonical.NameNormalized,
            entryId,
            row.Entry.CurationGeneration,
            digest,
            lifecycle,
            head is null
                ? new(false, null, null, null, null, null, null)
                : new(true, head.ClaimId, head.VersionId, head.Revision, head.Operation,
                    head.ContentHashFormat, head.ContentHash is null ? null : Convert.ToHexString(head.ContentHash)),
            label is null
                ? new(false, null, null, null, null)
                : new(true, label.LabelId, label.ArtifactRevision,
                    Convert.ToHexString(label.ArtifactContentDigest.Bytes), label.Provenance));

        RequireIntegrity(target.Validate().IsSuccess);

        return new(
            label,
            head,
            new(
                row.Entry,
                scope,
                head?.Origin,
                lifecycle,
                row.Entry.Eligibility,
                row.Entry.CurationGeneration,
                digest,
                target));
    }

    private static ReviewScopeFacts ScopeFacts(LexiconCurationScope scope)
    {
        string canonical = scope.Kind == LexiconScopeKind.Global
            ? "lexicon:global"
            : "lexicon:campaign:" + scope.CampaignId!.Value.ToString("D").ToLowerInvariant();

        return new(
            ScopeCode(scope),
            scope.CampaignId?.ToString("D"),
            new(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));
    }

    private static int ScopeCode(LexiconCurationScope scope) =>
        scope.Kind == LexiconScopeKind.Global ? 1 : 2;

    private static MemoryReviewDigest VersionDigest(string versionId) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(versionId)));

    private static async Task<bool> EventIdentityMatchesAsync(
        DbConnection connection,
        ReviewScopeFacts scope,
        ulong sequence,
        MemoryReviewDigest expectedIdentity,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT VersionId FROM annal_review_events
            WHERE Sequence = @sequence AND SubjectStoreCode = 2 AND ScopeKindCode = @scope
              AND ((@campaign IS NULL AND CampaignId IS NULL) OR CampaignId = @campaign)
            """;

        AddParameter(command, "@sequence", checked((long)sequence));
        AddParameter(command, "@scope", scope.ScopeCode);
        AddParameter(command, "@campaign", scope.CampaignId ?? (object)DBNull.Value);

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is string versionId && VersionDigest(versionId) == expectedIdentity;
    }

    private static bool Matches(
        MemoryReviewCursorTokenFacts token,
        ReviewMarker marker,
        ReviewScopeFacts scope) =>
        token.Store == MemoryReviewStore.Lexicon
        && token.CanonicalScopeDigest == scope.Digest
        && token.MarkerGeneration == marker.Generation
        && token.MarkerRevision == checked((ulong)marker.Revision);

    private static bool Matches(
        MemoryReviewObservationTokenFacts token,
        ReviewMarker marker,
        ReviewScopeFacts scope) =>
        token.Store == MemoryReviewStore.Lexicon
        && token.CanonicalScopeDigest == scope.Digest
        && token.MarkerGeneration == marker.Generation
        && token.MarkerRevision == checked((ulong)marker.Revision)
        && token.EventSequence > checked((ulong)marker.ReviewedThroughSequence);

    private static bool Matches(
        MemoryReviewPreparedPlanTokenFacts token,
        ReviewScopeFacts scope,
        MemoryReviewDigest requestDigest) =>
        token.Store == MemoryReviewStore.Lexicon
        && token.CanonicalScopeDigest == scope.Digest
        && token.OrderedRequestDigest == requestDigest;

    private static bool Matches(
        MemoryReviewPreparedPlanTokenFacts token,
        ReviewMarker marker,
        ReviewScopeFacts scope) =>
        token.Store == MemoryReviewStore.Lexicon
        && token.CanonicalScopeDigest == scope.Digest
        && token.MarkerGeneration == marker.Generation
        && token.MarkerRevision == checked((ulong)marker.Revision);

    private static bool SameFrontier(
        MemoryReviewObservationTokenFacts left,
        MemoryReviewObservationTokenFacts right) =>
        left.MarkerGeneration == right.MarkerGeneration
        && left.MarkerRevision == right.MarkerRevision
        && left.FrozenLowerEventSequence == right.FrozenLowerEventSequence
        && left.FrozenUpperEventSequence == right.FrozenUpperEventSequence;

    private static MemoryReviewDigest OrderedRequestDigest(LexiconReviewBulkPrepareRequest request)
    {
        StringBuilder canonical = new();

        AppendCanonical(canonical, "lexicon");
        AppendCanonical(canonical, request.RequestId.ToString("D"));
        AppendCanonical(canonical, ((int)request.Action).ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendCanonical(canonical, request.Scope.Kind.ToString());
        AppendCanonical(canonical, request.Scope.CampaignId?.ToString("D") ?? string.Empty);

        foreach (LexiconReviewDecision decision in request.Decisions)
        {
            AppendCanonical(canonical, decision.ObservationToken);
            AppendCanonical(canonical, decision.ReplacementContent?.Type ?? string.Empty);

            foreach (string fact in decision.ReplacementContent?.Facts ?? [])
            {
                AppendCanonical(canonical, fact);
            }

            canonical.Append(';');
        }

        return new(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void AppendCanonical(StringBuilder builder, string value) =>
        builder.Append(value.Length).Append(':').Append(value).Append('|');

    private static MemoryReviewDigest DecisionDigest(MemoryReviewDigest ordered, int index, bool output = false)
    {
        byte[] input = new byte[MemoryReviewDigest.Size + sizeof(int) + 1];

        ordered.CopyTo(input);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(input.AsSpan(MemoryReviewDigest.Size), index);
        input[^1] = output ? (byte)1 : (byte)0;

        return new(SHA256.HashData(input));
    }

    private static MemoryReviewDigest ResponseDigest(
        MemoryReviewDigest orderedDigest,
        ReceiptEnvelope envelope,
        MemoryReviewBulkItemResultDto item)
    {
        StringBuilder canonical = new();

        AppendCanonical(canonical, Convert.ToHexString(orderedDigest.Bytes));
        AppendCanonical(canonical, FormatEnvelope(envelope));
        AppendCanonical(canonical, item.EventSequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendCanonical(canonical, item.SubjectId);
        AppendCanonical(canonical, item.VersionId);
        AppendCanonical(canonical, item.Outcome);
        AppendCanonical(canonical, item.ResultingVersionId ?? string.Empty);

        return new(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static async Task WriteReviewReceiptAsync(
        DbConnection connection,
        LexiconReviewBulkPrepareRequest request,
        int index,
        MemoryReviewDigest orderedDigest,
        MemoryReviewBulkItemResultDto resultItem,
        MemoryReviewBulkItemResultDto evidenceItem,
        long reviewedThrough,
        bool protectedContent,
        ReceiptKind kind,
        CancellationToken cancellationToken)
    {
        ReceiptEnvelope envelope = new(
            request.RequestId,
            index,
            kind,
            request.Action,
            resultItem.Outcome,
            reviewedThrough,
            protectedContent,
            resultItem.ResultingVersionId);

        await using DbCommand command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO annal_review_decision_receipts
                (DecisionId, ReviewEventSequence, RequestIdempotencyDigest, DecisionCode, ResponseReceiptDigest)
            VALUES (@id, @event, @request, 1, @response)
            """;

        AddParameter(command, "@id", FormatEnvelope(envelope));
        AddParameter(command, "@event", evidenceItem.EventSequence);
        AddParameter(command, "@request", DecisionDigest(orderedDigest, index, kind == ReceiptKind.Replacement).Bytes);
        AddParameter(command, "@response", ResponseDigest(orderedDigest, envelope, evidenceItem).Bytes);

        RequireIntegrity(await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1);
    }

    private static async Task<MemoryReviewBulkItemResultDto> ReadCorrectionOutputAsync(
        DbConnection connection,
        string resultingVersion,
        CancellationToken cancellationToken)
    {
        await using DbCommand eventCommand = connection.CreateCommand();

        eventCommand.CommandText = "SELECT Sequence, SubjectId FROM annal_review_events WHERE VersionId = @version";

        AddParameter(eventCommand, "@version", resultingVersion);

        long sequence;

        string subject;

        await using (DbDataReader reader = await eventCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            RequireIntegrity(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));

            sequence = reader.GetInt64(0);
            subject = reader.GetString(1);
        }

        return new(
            sequence,
            Guid.Parse(subject).ToString("D"),
            resultingVersion,
            "auto-acknowledged",
            resultingVersion);
    }

    private static async Task<ReviewReplay?> TryReadReviewReplayAsync(
        DbConnection connection,
        LexiconReviewBulkPrepareRequest request,
        MemoryReviewDigest orderedDigest,
        CancellationToken cancellationToken)
    {
        List<StoredReceipt> receipts = await ReadStoredReceiptsAsync(
            connection, request.RequestId, cancellationToken).ConfigureAwait(false);

        if (receipts.Count == 0)
        {
            return null;
        }

        Dictionary<int, StoredReceipt> originals = [];

        Dictionary<int, StoredReceipt> replacements = [];

        long? reviewedThrough = null;

        foreach (StoredReceipt stored in receipts)
        {
            if (stored.IsCorrupt)
            {
                throw new InspectionException(ReviewIntegrityFailure);
            }

            ReceiptEnvelope envelope = stored.Envelope;

            if (envelope.RequestId != request.RequestId
                || envelope.Action != request.Action
                || envelope.Ordinal < 0
                || envelope.Ordinal >= request.Decisions.Length
                || !IsAllowedOutcome(request.Action, envelope.Outcome))
            {
                throw new InspectionException(RequestReuseFailure);
            }

            MemoryReviewDigest expectedRequest = DecisionDigest(
                orderedDigest, envelope.Ordinal, envelope.Kind == ReceiptKind.Replacement);

            if (stored.RequestDigest != expectedRequest)
            {
                throw new InspectionException(RequestReuseFailure);
            }

            if (reviewedThrough is { } expectedThrough && expectedThrough != envelope.ReviewedThrough)
            {
                throw new InspectionException(ReviewIntegrityFailure);
            }

            reviewedThrough ??= envelope.ReviewedThrough;

            Dictionary<int, StoredReceipt> destination = envelope.Kind == ReceiptKind.Original
                ? originals
                : replacements;

            if (!destination.TryAdd(envelope.Ordinal, stored))
            {
                throw new InspectionException(ReviewIntegrityFailure);
            }
        }

        if (originals.Count != request.Decisions.Length)
        {
            throw new InspectionException(RequestReuseFailure);
        }

        MemoryReviewBulkItemResultDto[] items = new MemoryReviewBulkItemResultDto[request.Decisions.Length];

        for (int index = 0; index < items.Length; index++)
        {
            if (!originals.TryGetValue(index, out StoredReceipt? original))
            {
                throw new InspectionException(RequestReuseFailure);
            }

            StoredReviewEvent? originalEvent = await ReadStoredReviewEventAsync(
                connection, request.Scope, original.EventSequence, cancellationToken).ConfigureAwait(false);

            if (originalEvent is null)
            {
                throw new InspectionException(ReviewIntegrityFailure);
            }

            MemoryReviewBulkItemResultDto item = new(
                originalEvent.Sequence,
                originalEvent.SubjectId,
                originalEvent.VersionId,
                original.Envelope.Outcome,
                original.Envelope.ResultingVersionId);

            if (original.ResponseDigest != ResponseDigest(orderedDigest, original.Envelope, item))
            {
                throw new InspectionException(ReviewIntegrityFailure);
            }

            if (replacements.TryGetValue(index, out StoredReceipt? replacement))
            {
                StoredReviewEvent? replacementEvent = await ReadStoredReviewEventAsync(
                    connection, request.Scope, replacement.EventSequence, cancellationToken).ConfigureAwait(false);

                MemoryReviewBulkItemResultDto replacementItem = new(
                    replacementEvent?.Sequence ?? -1,
                    replacementEvent?.SubjectId ?? string.Empty,
                    replacementEvent?.VersionId ?? string.Empty,
                    "auto-acknowledged",
                    replacementEvent?.VersionId);

                if (request.Action != MemoryReviewAction.Correct
                    || item.ResultingVersionId is null
                    || replacementEvent is null
                    || replacementEvent.SubjectId != item.SubjectId
                    || replacementEvent.VersionId != item.ResultingVersionId
                    || replacement.Envelope.ResultingVersionId != item.ResultingVersionId
                    || replacement.Envelope.ProtectedContent != original.Envelope.ProtectedContent
                    || replacement.ResponseDigest != ResponseDigest(orderedDigest, replacement.Envelope, replacementItem))
                {
                    throw new InspectionException(ReviewIntegrityFailure);
                }
            }
            else if (request.Action == MemoryReviewAction.Correct && item.ResultingVersionId is not null)
            {
                throw new InspectionException(ReviewIntegrityFailure);
            }
            else if (request.Action != MemoryReviewAction.Correct && replacements.Count != 0)
            {
                throw new InspectionException(ReviewIntegrityFailure);
            }

            if (!await ValidateResultingVersionAsync(
                    connection, request.Scope, request.Action, item, cancellationToken).ConfigureAwait(false))
            {
                throw new InspectionException(ReviewIntegrityFailure);
            }

            items[index] = item;
        }

        return new(
            new(
                MemoryReviewStore.Lexicon,
                request.RequestId,
                request.Action,
                items,
                reviewedThrough ?? 0,
                Replayed: true),
            originals.Values.Any(static receipt => receipt.Envelope.ProtectedContent));
    }

    private static async Task<List<StoredReceipt>> ReadStoredReceiptsAsync(
        DbConnection connection,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT DecisionId, ReviewEventSequence, RequestIdempotencyDigest, ResponseReceiptDigest
            FROM annal_review_decision_receipts
            WHERE substr(DecisionId, 1, length(@prefix)) = @prefix
            ORDER BY DecisionId
            """;

        AddParameter(command, "@prefix", requestId.ToString("N") + ":");

        List<StoredReceipt> receipts = [];

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string decisionId = reader.GetString(0);

            if (!TryParseEnvelope(decisionId, out ReceiptEnvelope? envelope))
            {
                return [StoredReceipt.Corrupt(decisionId)];
            }

            receipts.Add(new(
                envelope!,
                reader.GetInt64(1),
                new((byte[])reader.GetValue(2)),
                new((byte[])reader.GetValue(3))));
        }

        return receipts;
    }

    private static string FormatEnvelope(ReceiptEnvelope envelope) =>
        string.Join(
            ':',
            envelope.RequestId.ToString("N"),
            envelope.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture),
            envelope.Kind == ReceiptKind.Original ? "O" : "R",
            ((int)envelope.Action).ToString(System.Globalization.CultureInfo.InvariantCulture),
            envelope.Outcome,
            envelope.ReviewedThrough.ToString(System.Globalization.CultureInfo.InvariantCulture),
            envelope.ProtectedContent ? "P" : "U",
            envelope.ResultingVersionId ?? "-");

    private static bool TryParseEnvelope(string value, out ReceiptEnvelope? envelope)
    {
        string[] parts = value.Split(':', StringSplitOptions.None);

        if (parts.Length != 8
            || !Guid.TryParseExact(parts[0], "N", out Guid requestId)
            || !int.TryParse(parts[1], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int ordinal)
            || parts[2] is not ("O" or "R")
            || !int.TryParse(parts[3], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int actionCode)
            || !Enum.IsDefined((MemoryReviewAction)actionCode)
            || string.IsNullOrEmpty(parts[4])
            || !long.TryParse(parts[5], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out long reviewedThrough)
            || reviewedThrough < 0
            || parts[6] is not ("P" or "U"))
        {
            envelope = null;

            return false;
        }

        string? resultingVersion = parts[7] == "-" ? null : parts[7];

        if (resultingVersion is { Length: 0 })
        {
            envelope = null;

            return false;
        }

        envelope = new(
            requestId,
            ordinal,
            parts[2] == "O" ? ReceiptKind.Original : ReceiptKind.Replacement,
            (MemoryReviewAction)actionCode,
            parts[4],
            reviewedThrough,
            parts[6] == "P",
            resultingVersion);

        return true;
    }

    private static async Task<StoredReviewEvent?> ReadStoredReviewEventAsync(
        DbConnection connection,
        LexiconCurationScope scope,
        long sequence,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT event.Sequence, event.SubjectId, event.VersionId, version.OperationCode
            FROM annal_review_events event
            JOIN annal_versions version ON version.VersionId = event.VersionId
            WHERE event.Sequence = @sequence AND event.SubjectStoreCode = 2
              AND event.ScopeKindCode = @scope
              AND ((@campaign IS NULL AND event.CampaignId IS NULL) OR event.CampaignId = @campaign)
            """;

        AddParameter(command, "@sequence", sequence);
        AddParameter(command, "@scope", ScopeCode(scope));
        AddParameter(command, "@campaign", scope.CampaignId?.ToString("D") ?? (object)DBNull.Value);

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(
                reader.GetInt64(0),
                Guid.Parse(reader.GetString(1)).ToString("D"),
                reader.GetString(2),
                (AnnalOperation)checked((int)reader.GetInt64(3)))
            : null;
    }

    private static async Task<bool> ValidateResultingVersionAsync(
        DbConnection connection,
        LexiconCurationScope scope,
        MemoryReviewAction action,
        MemoryReviewBulkItemResultDto item,
        CancellationToken cancellationToken)
    {
        if (action is MemoryReviewAction.Confirm or MemoryReviewAction.Pin or MemoryReviewAction.Unpin)
        {
            return item.ResultingVersionId is null;
        }

        if (item.ResultingVersionId is null)
        {
            return action is MemoryReviewAction.Correct or MemoryReviewAction.Retire;
        }

        await using DbCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT event.Sequence, event.SubjectId, event.VersionId, version.OperationCode
            FROM annal_review_events event
            JOIN annal_versions version ON version.VersionId = event.VersionId
            WHERE event.VersionId = @version AND event.SubjectStoreCode = 2
              AND event.ScopeKindCode = @scope
              AND ((@campaign IS NULL AND event.CampaignId IS NULL) OR event.CampaignId = @campaign)
            """;

        AddParameter(command, "@version", item.ResultingVersionId);
        AddParameter(command, "@scope", ScopeCode(scope));
        AddParameter(command, "@campaign", scope.CampaignId?.ToString("D") ?? (object)DBNull.Value);

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        string subjectId = Guid.Parse(reader.GetString(1)).ToString("D");

        AnnalOperation operation = (AnnalOperation)checked((int)reader.GetInt64(3));

        return subjectId == item.SubjectId
            && (action != MemoryReviewAction.Retire || operation == AnnalOperation.Retire);
    }

    private static Error RequestReuseFailure => new(
        ErrorCodes.MemoryReview.RequestReuse,
        "The Lexicon review request identity was already used for different decisions.");

    private static string AppliedOutcomeForAction(MemoryReviewAction action) => action switch
    {
        MemoryReviewAction.Confirm => "acknowledged",
        MemoryReviewAction.Correct => "corrected",
        MemoryReviewAction.Retire => "retired",
        MemoryReviewAction.Pin => "pinned",
        MemoryReviewAction.Unpin => "unpinned",
        _ => throw new InvalidOperationException("Unrecognized memory-review action."),
    };

    private static string? NoOpOutcomeForAction(MemoryReviewAction action) => action switch
    {
        MemoryReviewAction.Correct => nameof(LexiconCurationOutcomeKind.Unchanged),
        MemoryReviewAction.Retire => nameof(LexiconCurationOutcomeKind.AlreadyRetired),
        MemoryReviewAction.Pin => nameof(LexiconCurationOutcomeKind.AlreadyPinned),
        MemoryReviewAction.Unpin => nameof(LexiconCurationOutcomeKind.NotPinned),
        _ => null,
    };

    private static bool IsAllowedOutcome(MemoryReviewAction action, string outcome) =>
        string.Equals(outcome, AppliedOutcomeForAction(action), StringComparison.Ordinal)
        || string.Equals(outcome, NoOpOutcomeForAction(action), StringComparison.Ordinal);

    private async Task<ReviewActionResult> ApplyReviewActionAsync(
        DbConnection connection,
        MemoryReviewAction action,
        LexiconReplacementContent? replacement,
        CurationState state,
        CancellationToken cancellationToken)
    {
        if (action == MemoryReviewAction.Confirm)
        {
            return new(AppliedOutcomeForAction(action), ResultingVersionId: null);
        }

        if (state.Row.Entry.CurationGeneration == long.MaxValue)
        {
            throw new InspectionException(new Error(
                ErrorCodes.Lexicon.CurationGenerationExhausted,
                "The Lexicon curation generation is exhausted."));
        }

        DateTimeOffset now = _reviewTimeProvider.GetUtcNow();

        now = now > state.Row.Entry.UpdatedAt ? now : state.Row.Entry.UpdatedAt.AddTicks(1);

        if (action == MemoryReviewAction.Correct)
        {
            if (state.Row.Entry.RetiredAtUtc is not null)
            {
                throw new InspectionException(new Error(
                    ErrorCodes.Lexicon.RetiredMutationRefused,
                    "A retired Lexicon entry must be reinstated before correction."));
            }

            Result<LexiconCanonicalValue> normalized = LexiconValueNormalizer.NormalizeCorrection(
                state.Row.Canonical.Name,
                replacement?.Type,
                replacement?.Facts);

            if (normalized.IsFailure)
            {
                throw new InspectionException(normalized.Error);
            }

            if (state.Row.Canonical.Type == normalized.Value.Type
                && state.Row.Canonical.Facts.SequenceEqual(normalized.Value.Facts, StringComparer.Ordinal))
            {
                return new(NoOpOutcomeForAction(action)!, ResultingVersionId: null);
            }

            await EnsureCurationBaselineAsync(connection, state, now, cancellationToken).ConfigureAwait(false);
            await ReplaceCanonicalAsync(connection, state.Row, normalized.Value, now, cancellationToken).ConfigureAwait(false);

            _ = await ReplaceFactProvenanceAsync(
                connection,
                state.Row.Entry.Id,
                state.Row.Entry.FactProvenance ?? [],
                [],
                normalized.Value.Facts,
                null,
                cancellationToken).ConfigureAwait(false);

            if (state.Label is { } label)
            {
                Result<ArtifactSensitivityLabel> replaced = await ArtifactSensitivityLedger.ReplaceLexiconWithinAsync(
                    (SqliteConnection)connection,
                    null,
                    label,
                    DerivedArtifactContentDigest.ForBytes(LexiconSnapshotDigest.Encode(normalized.Value)),
                    now,
                    cancellationToken).ConfigureAwait(false);

                if (replaced.IsFailure)
                {
                    throw new InspectionException(replaced.Error);
                }
            }

            string? correctedVersion = await AppendCurationContentAsync(
                connection,
                state,
                normalized.Value,
                AnnalOrigin.OperatorStated,
                now,
                now,
                cancellationToken).ConfigureAwait(false);

            return new(AppliedOutcomeForAction(action), correctedVersion);
        }

        bool pin = action is MemoryReviewAction.Pin or MemoryReviewAction.Unpin;

        bool set = action is MemoryReviewAction.Retire or MemoryReviewAction.Pin;

        DateTimeOffset? current = pin ? state.Row.Entry.PinnedAtUtc : state.Row.Entry.RetiredAtUtc;

        if ((current is not null) == set)
        {
            return new(NoOpOutcomeForAction(action)!, ResultingVersionId: null);
        }

        if (action == MemoryReviewAction.Retire)
        {
            await EnsureCurationBaselineAsync(connection, state, now, cancellationToken).ConfigureAwait(false);
        }

        await using (DbCommand command = connection.CreateCommand())
        {
            command.CommandText = $"""
                UPDATE lexicon_entries SET {(pin ? "PinnedAtUtc" : "RetiredAtUtc")} = @timestamp,
                    CurationGeneration = CurationGeneration + 1
                WHERE Id = @id AND CurationGeneration = @generation
                """;

            AddParameter(command, "@timestamp", set ? UtcInstantText.Format(now) : DBNull.Value);
            AddParameter(command, "@id", state.Row.Entry.Id.ToString("N"));
            AddParameter(command, "@generation", state.Row.Entry.CurationGeneration);

            RequireIntegrity(await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1);
        }

        if (action != MemoryReviewAction.Retire)
        {
            return new(AppliedOutcomeForAction(action), ResultingVersionId: null);
        }

        RequireIntegrity(await AnnalsClaimWriter.AppendRetirementAsync(
            connection,
            null,
            AnnalSubjectStore.Lexicon,
            state.Row.Entry.Id.ToString("N"),
            AnnalOrigin.OperatorStated,
            state.Detail.Scope.Kind == LexiconScopeKind.Global
                ? SagaMemoryScopeKind.Global
                : SagaMemoryScopeKind.Campaign,
            state.Detail.Scope.CampaignId?.ToString("D"),
            state.Label?.Sensitivity ?? ContentSensitivity.None,
            now,
            now,
            null,
            cancellationToken).ConfigureAwait(false));

        await using DbCommand head = connection.CreateCommand();

        head.CommandText = """
            SELECT h.CurrentVersionId FROM annal_heads h
            JOIN annal_claims c ON c.ClaimId = h.ClaimId
            WHERE c.SubjectStoreCode = 2 AND c.SubjectId = @id
            """;

        AddParameter(head, "@id", state.Row.Entry.Id.ToString("N"));

        string? retirementVersion = (string?)await head.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return new(AppliedOutcomeForAction(action), retirementVersion);
    }

    private static async Task<long> AdvanceReviewMarkerAsync(
        DbConnection connection,
        LexiconCurationScope scope,
        ReviewMarker marker,
        IReadOnlySet<long> pendingAcknowledgements,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT event.Sequence,
                   (head.CurrentVersionId <> event.VersionId OR
                    EXISTS(SELECT 1 FROM annal_review_decision_receipts receipt
                           WHERE receipt.ReviewEventSequence = event.Sequence))
            FROM annal_review_events event
            JOIN annal_heads head ON head.ClaimId = event.ClaimId
            WHERE event.SubjectStoreCode = 2 AND event.ScopeKindCode = @scope
                AND ((@campaign IS NULL AND event.CampaignId IS NULL) OR event.CampaignId = @campaign)
                AND event.Sequence > @reviewed
            ORDER BY event.Sequence
            """;

        AddParameter(command, "@scope", ScopeCode(scope));
        AddParameter(command, "@campaign", scope.CampaignId?.ToString("D") ?? (object)DBNull.Value);
        AddParameter(command, "@reviewed", marker.ReviewedThroughSequence);

        long reviewed = marker.ReviewedThroughSequence;

        await using (DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                long sequence = reader.GetInt64(0);

                if (reader.GetInt64(1) == 0 && !pendingAcknowledgements.Contains(sequence))
                {
                    break;
                }

                reviewed = sequence;
            }
        }

        if (reviewed == marker.ReviewedThroughSequence)
        {
            return reviewed;
        }

        command.Parameters.Clear();
        command.CommandText = """
            UPDATE annal_review_markers
            SET ReviewedThroughSequence = @reviewed, Revision = Revision + 1
            WHERE SubjectStoreCode = 2 AND ScopeKindCode = @scope
                AND ((@campaign IS NULL AND CampaignId IS NULL) OR CampaignId = @campaign)
                AND MarkerGeneration = @generation AND Revision = @revision
            """;

        AddParameter(command, "@reviewed", reviewed);
        AddParameter(command, "@scope", ScopeCode(scope));
        AddParameter(command, "@campaign", scope.CampaignId?.ToString("D") ?? (object)DBNull.Value);
        AddParameter(command, "@generation", marker.Generation.ToByteArray());
        AddParameter(command, "@revision", marker.Revision);

        RequireIntegrity(await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1);

        return reviewed;
    }

    private sealed record ReviewMarker(Guid Generation, long ReviewedThroughSequence, long Revision);

    private sealed record ReviewScopeFacts(int ScopeCode, string? CampaignId, MemoryReviewDigest Digest);

    private sealed record ReceiptEnvelope(
        Guid RequestId,
        int Ordinal,
        ReceiptKind Kind,
        MemoryReviewAction Action,
        string Outcome,
        long ReviewedThrough,
        bool ProtectedContent,
        string? ResultingVersionId);

    private sealed record StoredReceipt(
        ReceiptEnvelope Envelope,
        long EventSequence,
        MemoryReviewDigest RequestDigest,
        MemoryReviewDigest ResponseDigest,
        bool IsCorrupt = false)
    {
        internal static StoredReceipt Corrupt(string decisionId) =>
            new(
                new(
                    Guid.Empty,
                    -1,
                    ReceiptKind.Original,
                    MemoryReviewAction.Confirm,
                    decisionId,
                    0,
                    ProtectedContent: false,
                    ResultingVersionId: null),
                EventSequence: -1,
                RequestDigest: default,
                ResponseDigest: default,
                IsCorrupt: true);
    }

    private sealed record StoredReviewEvent(
        long Sequence,
        string SubjectId,
        string VersionId,
        AnnalOperation Operation);

    private sealed record ReviewReplay(
        MemoryReviewBulkResultDto Result,
        bool ContainsProtectedContent);

    private sealed record ReviewCurrentState(
        ArtifactSensitivityLabel? Label,
        AnnalClaimVersion? Head,
        LexiconReviewCurrentDto Current);

    private sealed record ReviewActionResult(
        string Outcome,
        string? ResultingVersionId);

    private enum ReceiptKind
    {
        Original = 1,

        Replacement = 2,
    }

    private sealed record ReviewEventRow(
        long Sequence,
        string VersionId,
        Guid EntryId,
        int Revision,
        AnnalOperation Operation,
        AnnalOrigin Origin,
        Guid? SourceSessionId,
        string? ContentHash,
        bool IsCurrent);

    private sealed record ObservedReviewEvent(
        long Sequence,
        Guid EntryId,
        string VersionId,
        AnnalOrigin Origin,
        Guid? SourceSessionId,
        LexiconCurationScope Scope,
        bool IsCurrent,
        MemoryReviewObservationTokenFacts Token);
}
