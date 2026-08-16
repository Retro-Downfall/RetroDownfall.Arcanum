using System.Collections.Immutable;
using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// Free-text inspection over current heads, through FTS5 when it is eligible and through a bounded
/// canonical scan when it is not.
/// </summary>
/// <remarks>
/// Both modes open one read transaction and read every generation fact in the same snapshot as the
/// rows. Reading eligibility separately would let a page claim an applied tuple that did not hold
/// when its rows were selected, which is exactly how stale text reaches a caller who was told search
/// was current.
///
/// <para>The index never acquires a lease and never widens one. An all-scopes query requires the
/// installation read capability; a scoped one accepts that or an exactly matching scoped lease.</para>
/// </remarks>
internal sealed class CovenantSearchIndex(ICovenantConnectionSource connections) : ICovenantSearchIndex
{

    /// <summary>
    /// The materialized candidate bound for the canonical fallback.
    /// </summary>
    internal const int FallbackCandidateLimit = CovenantLimits.MaxFallbackCandidates;

    public async ValueTask<Result<CovenantSearchPage>> SearchAsync(
        CovenantSearchQuery query,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(query);

        ArgumentNullException.ThrowIfNull(readLease);

        Result<CovenantOperationScope?> scope = ResolveSelection(query.ScopeSelection, query.CampaignId);

        if (scope.IsFailure)
        {

            return scope.Error;

        }

        Result validated = await ValidateLeaseAsync(readLease, scope.Value, cancellationToken).ConfigureAwait(false);

        if (validated.IsFailure)
        {

            return validated.Error;

        }

        SqliteConnection connection = await connections.GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                .ConfigureAwait(false);

        CovenantSearchSourceSnapshot sources = await ReadSourcesAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);

        bool acceleratorInstalled = await AcceleratorInstalledAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);

        Result<CovenantSearchPage> page;

        if (acceleratorInstalled && sources.AcceleratorEligible)
        {

            page = await SearchFtsAsync(connection, transaction, query, scope.Value, sources, cancellationToken)
                .ConfigureAwait(false);

            if (page.IsSuccess)
            {

                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return page;

            }

        }

        page = await SearchFallbackAsync(
                connection,
                transaction,
                query,
                scope.Value,
                sources,
                acceleratorInstalled,
                cancellationToken)
            .ConfigureAwait(false);

        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

        return page;

    }

    private static async ValueTask<Result<CovenantSearchPage>> SearchFtsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CovenantSearchQuery query,
        CovenantOperationScope? scope,
        CovenantSearchSourceSnapshot sources,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = CovenantSearchSql.FtsPage(scope, query.Lane is not null, query.Lifecycle, query.After is not null);

        BindCommonFilters(command, query, scope);

        Bind(command, "$match", query.Terms.MatchExpression);

        try
        {

            ImmutableArray<CovenantSearchHit> hits = await ReadHitsAsync(
                    command,
                    query.EffectivePageSize + 1,
                    cancellationToken)
                .ConfigureAwait(false);

            return BuildPage(hits, query, sources, CovenantSearchExecutionMode.Fts, truncated: false);

        }
        catch (SqliteException exception)
        {

            // A corrupt, locked, or version-mismatched index is a degradation, not a failure: the
            // caller still gets an answer, from canonical, with explicit guidance.
            return new Error(ErrorCodes.Covenant.Unavailable, exception.Message);

        }

    }

    private static async ValueTask<Result<CovenantSearchPage>> SearchFallbackAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CovenantSearchQuery query,
        CovenantOperationScope? scope,
        CovenantSearchSourceSnapshot sources,
        bool acceleratorInstalled,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = CovenantSearchSql.FallbackPage(
            scope,
            query.Lane is not null,
            query.Lifecycle,
            query.Terms.LikePatterns.Length,
            query.After is not null);

        BindCommonFilters(command, query, scope);

        for (int index = 0; index < query.Terms.LikePatterns.Length; index++)
        {

            Bind(command, $"$like{index}", query.Terms.LikePatterns[index]);

        }

        long candidates = await CountCandidatesAsync(connection, transaction, query, scope, cancellationToken)
            .ConfigureAwait(false);

        ImmutableArray<CovenantSearchHit> hits = await ReadHitsAsync(
                command,
                query.EffectivePageSize + 1,
                cancellationToken)
            .ConfigureAwait(false);

        // Truncation is about the candidate set, not the page: a caller told "no more results" when
        // 2,048 heads were examined out of 5,000 would draw a false conclusion.
        bool truncated = candidates > FallbackCandidateLimit;

        return BuildPage(
            hits,
            query,
            sources,
            CovenantSearchExecutionMode.CanonicalFallback,
            truncated,
            acceleratorInstalled);

    }

    private static Result<CovenantSearchPage> BuildPage(
        ImmutableArray<CovenantSearchHit> hits,
        CovenantSearchQuery query,
        CovenantSearchSourceSnapshot sources,
        CovenantSearchExecutionMode mode,
        bool truncated,
        bool acceleratorInstalled = true)
    {

        bool hasMore = hits.Length > query.EffectivePageSize;

        ImmutableArray<CovenantSearchHit> page = hasMore
            ? hits[..query.EffectivePageSize]
            : hits;

        CovenantSearchKeyset? next = hasMore && !page.IsEmpty
            ? ToKeyset(page[^1])
            : null;

        CovenantSearchRebuildGuidance guidance = mode == CovenantSearchExecutionMode.Fts
            ? CovenantSearchRebuildGuidance.None
            : !acceleratorInstalled
                ? CovenantSearchRebuildGuidance.AcceleratorUnavailable
                // A null applied tuple is an accelerator that has not published yet, which the
                // synchronization worker resolves on its next pass. A published tuple naming another
                // dataset is the case no delta can reconcile.
                : sources.AppliedDatasetGeneration is null
                    || sources.AppliedDatasetGeneration == sources.DatasetGeneration
                    ? CovenantSearchRebuildGuidance.WaitForSynchronization
                    : CovenantSearchRebuildGuidance.RebuildRequired;

        return new CovenantSearchPage(page, next, sources, mode, truncated, guidance);

    }

    private static CovenantSearchKeyset ToKeyset(CovenantSearchHit hit) =>
        new(
            hit.MatchClass,
            CovenantSearchKeyset.EncodeScore(hit.Score) is { IsSuccess: true } bits ? bits.Value : 0UL,
            hit.EntryId,
            hit.VersionId);

    private static async ValueTask<ImmutableArray<CovenantSearchHit>> ReadHitsAsync(
        SqliteCommand command,
        int limit,
        CancellationToken cancellationToken)
    {

        Bind(command, "$limit", limit);

        List<CovenantSearchHit> hits = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            hits.Add(
                new CovenantSearchHit(
                    Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                    Guid.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                    (CovenantScope)reader.GetInt32(2),
                    reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                    (CovenantLane)reader.GetInt32(4),
                    (CovenantLifecycle)reader.GetInt32(5),
                    reader.GetString(6),
                    (CovenantSearchMatchClass)reader.GetInt32(7),
                    reader.GetDouble(8),
                    reader.GetInt64(9)));

        }

        return [.. hits];

    }

    private static async ValueTask<long> CountCandidatesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CovenantSearchQuery query,
        CovenantOperationScope? scope,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = CovenantSearchSql.FallbackCandidateCount(scope, query.Lane is not null, query.Lifecycle);

        BindCommonFilters(command, query, scope);

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return Convert.ToInt64(value, CultureInfo.InvariantCulture);

    }

    private static void BindCommonFilters(
        SqliteCommand command,
        CovenantSearchQuery query,
        CovenantOperationScope? scope)
    {

        if (scope is { Kind: CovenantScope.Campaign } campaign)
        {

            Bind(command, "$campaign", campaign.CampaignId!.Value.ToString("D"));

        }

        if (query.Lane is { } lane)
        {

            Bind(command, "$lane", (int)lane);

        }

        // Exact and prefix classes come from the compiled terms, never from raw caller text.
        Bind(
            command,
            "$exactKey",
            query.Terms.NormalizedTerms.Length == 1 ? query.Terms.NormalizedTerms[0] : " ");

        Bind(command, "$prefixKey", TrimTrailingWildcard(query.Terms.LikePatterns[0]));

        if (query.After is { } after)
        {

            Bind(command, "$afterClass", (int)after.MatchClass);

            Bind(command, "$afterScore", after.Score);

            Bind(command, "$afterEntry", after.EntryId.ToString("D"));

            Bind(command, "$afterVersion", after.VersionId.ToString("D"));

        }

    }

    /// <summary>
    /// Turns the term's contains-pattern into a starts-with pattern for the prefix match class.
    /// </summary>
    private static string TrimTrailingWildcard(string containsPattern) =>
        containsPattern.Length >= 2
            ? containsPattern[1..]
            : containsPattern;

    private static async ValueTask<CovenantSearchSourceSnapshot> ReadSourcesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = CovenantSearchSql.Sources();

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        return new CovenantSearchSourceSnapshot(
            new Guid((byte[])reader.GetValue(0)),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.IsDBNull(3) ? null : new Guid((byte[])reader.GetValue(3)),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetInt64(5),
            checked((ulong)reader.GetInt64(6)));

    }

    private static async ValueTask<bool> AcceleratorInstalledAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE "name" IN ('covenant_fts', 'covenant_search_documents');
            """;

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return Convert.ToInt64(value, CultureInfo.InvariantCulture) == 2;

    }

    private static Result<CovenantOperationScope?> ResolveSelection(
        CovenantCursorScopeSelection selection,
        Guid? campaignId) =>
        selection switch
        {
            CovenantCursorScopeSelection.Global when campaignId is null =>
                Result<CovenantOperationScope?>.Success(CovenantOperationScope.Global),

            CovenantCursorScopeSelection.Campaign when campaignId is { } present && present != Guid.Empty =>
                Result<CovenantOperationScope?>.Success(CovenantOperationScope.ForCampaign(present)),

            CovenantCursorScopeSelection.AllScopes when campaignId is null =>
                Result<CovenantOperationScope?>.Success(null),

            _ => Result<CovenantOperationScope?>.Failure(
                new Error(
                    ErrorCodes.Covenant.InvalidScope,
                    "The Covenant scope selection and Campaign identity do not agree.")),
        };

    private static async ValueTask<Result> ValidateLeaseAsync(
        ICovenantSnapshotReadLease readLease,
        CovenantOperationScope? required,
        CancellationToken cancellationToken)
    {

        Result revalidated = await readLease.RevalidateAsync(cancellationToken).ConfigureAwait(false);

        if (revalidated.IsFailure)
        {

            return revalidated;

        }

        if (readLease.Snapshot.Coverage == CovenantLeaseCoverage.Installation)
        {

            return Result.Success();

        }

        if (required is null)
        {

            return new Error(
                ErrorCodes.Covenant.ForbiddenAuthority,
                "An all-scopes Covenant search requires the installation read capability.");

        }

        CovenantOperationScope held = readLease.Snapshot.Scope!.Value;

        return held.Kind == required.Value.Kind && held.CampaignId == required.Value.CampaignId
            ? Result.Success()
            : new Error(
                ErrorCodes.Covenant.ForbiddenAuthority,
                "This Covenant lease does not cover the scope the search names.");

    }

    private static void Bind(SqliteCommand command, string name, object value)
    {

        if (!command.Parameters.Contains(name))
        {

            _ = command.Parameters.AddWithValue(name, value);

        }

    }

}
