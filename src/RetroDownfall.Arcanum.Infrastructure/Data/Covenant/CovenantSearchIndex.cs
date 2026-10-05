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

    /// <summary>
    /// The exact-key operand of a query with more than one term, which names no one key.
    /// </summary>
    /// <remarks>
    /// A Covenant key matches <c>[a-z0-9][a-z0-9._-]{0,127}</c>, so no key can equal a NUL and the
    /// exact-key comparison matches nothing. It is spelled as an escape: a raw NUL byte in a source
    /// file is one an editor, a diff tool or a formatter is free to drop or mangle without a word.
    /// </remarks>
    private const string NoExactKey = "\0";

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

        (CovenantSearchSourceSnapshot sources, bool rebuildOwed) = await ReadSourcesAsync(
                connection,
                transaction,
                cancellationToken)
            .ConfigureAwait(false);

        bool acceleratorInstalled = await AcceleratorInstalledAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);

        Result<CovenantSearchPage> page;

        // The published tier state is a fact no row carries: a tier the host found degraded still has its
        // objects and a current tuple, and status already reports it as answering from the fallback.
        if (acceleratorInstalled && sources.AcceleratorEligible && query.Accelerator is CovenantCapabilityState.Healthy)
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
                rebuildOwed,
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
            ImmutableArray<Candidate> candidates = await ReadCandidatesAsync(
                    command,
                    query.EffectivePageSize + 1,
                    cancellationToken)
                .ConfigureAwait(false);

            // An answer from the index is the synchronized one, which asks nothing of the caller.
            return await BuildPageAsync(
                    connection,
                    transaction,
                    candidates,
                    query,
                    sources,
                    CovenantSearchExecutionMode.Fts,
                    truncated: false,
                    CovenantSearchRebuildGuidance.None,
                    cancellationToken)
                .ConfigureAwait(false);
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
        bool rebuildOwed,
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

            Bind(command, $"$term{index}", query.Terms.NormalizedTerms[index]);
        }

        long candidateCount = await CountCandidatesAsync(connection, transaction, query, scope, cancellationToken)
            .ConfigureAwait(false);

        ImmutableArray<Candidate> candidates = await ReadCandidatesAsync(
                command,
                query.EffectivePageSize + 1,
                cancellationToken)
            .ConfigureAwait(false);

        // Truncation is about the candidate set, not the page: a caller told "no more results" when
        // 2,048 heads were examined out of 5,000 would draw a false conclusion.
        bool truncated = candidateCount > FallbackCandidateLimit;

        // The same rule the status route applies, over the facts this page was answered from. A page the
        // canonical scan answered was not answered by the index, so it is never the synchronized case. The
        // published tier state joins the rows' own facts exactly where status reads it: an unavailable tier
        // cannot be waited out, and the outbox only continues behind a healthy one.
        CovenantSearchRebuildGuidance guidance = CovenantSearchHealthRule.Guidance(
            acceleratorUnavailable: !acceleratorInstalled || query.Accelerator is CovenantCapabilityState.Unavailable,
            synchronized: false,
            outboxCanContinue: acceleratorInstalled
                && query.Accelerator is CovenantCapabilityState.Healthy
                && sources.AppliedDatasetGeneration == sources.DatasetGeneration,
            rebuildOwed: rebuildOwed);

        return await BuildPageAsync(
                connection,
                transaction,
                candidates,
                query,
                sources,
                CovenantSearchExecutionMode.CanonicalFallback,
                truncated,
                guidance,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Trims the candidates to the page and reads each surviving head, in the same transaction.
    /// </summary>
    /// <remarks>
    /// The heads are what the caller renders, so they are read here rather than by one detail read per
    /// hit afterwards: a later transaction could observe a different dataset than the rows it is
    /// completing, and would then have to refuse a first page as stale.
    /// </remarks>
    private static async ValueTask<Result<CovenantSearchPage>> BuildPageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ImmutableArray<Candidate> candidates,
        CovenantSearchQuery query,
        CovenantSearchSourceSnapshot sources,
        CovenantSearchExecutionMode mode,
        bool truncated,
        CovenantSearchRebuildGuidance guidance,
        CancellationToken cancellationToken)
    {
        bool hasMore = candidates.Length > query.EffectivePageSize;

        ImmutableArray<Candidate> page = hasMore
            ? candidates[..query.EffectivePageSize]
            : candidates;

        CovenantSearchKeyset? next = hasMore && !page.IsEmpty
            ? ToKeyset(page[^1])
            : null;

        Dictionary<Guid, CovenantHeadItem> heads = await ReadHeadsAsync(
                connection,
                transaction,
                page,
                cancellationToken)
            .ConfigureAwait(false);

        List<CovenantSearchHit> hits = new(page.Length);

        foreach (Candidate candidate in page)
        {
            // A document whose head is no longer that version is a row the index and canonical disagree
            // about. Inside one transaction that is an index that is not what it claims to be, so the
            // FTS caller falls back to canonical and the fallback, which has no such rows, never sees it.
            if (!heads.TryGetValue(candidate.VersionId, out CovenantHeadItem? head))
            {
                return new Error(ErrorCodes.Covenant.Unavailable, "A search hit no longer names a current head.");
            }

            hits.Add(new CovenantSearchHit(head, candidate.MatchClass, candidate.Score));
        }

        return new CovenantSearchPage([.. hits], next, sources, mode, truncated, guidance);
    }

    private static async ValueTask<Dictionary<Guid, CovenantHeadItem>> ReadHeadsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ImmutableArray<Candidate> page,
        CancellationToken cancellationToken)
    {
        Dictionary<Guid, CovenantHeadItem> heads = [];

        if (page.IsEmpty)
        {
            return heads;
        }

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = CovenantStoreSql.HeadsByVersion(page.Length);

        for (int index = 0; index < page.Length; index++)
        {
            Bind(command, $"$version{index}", page[index].VersionId.ToString("D"));
        }

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            CovenantHeadItem head = CovenantStore.MaterializeHeadItem(reader, offset: 0);

            heads[head.VersionId] = head;
        }

        return heads;
    }

    private static CovenantSearchKeyset ToKeyset(Candidate candidate) =>
        new(
            candidate.MatchClass,
            CovenantSearchKeyset.EncodeScore(candidate.Score) is { IsSuccess: true } bits ? bits.Value : 0UL,
            candidate.EntryId,
            candidate.VersionId);

    private static async ValueTask<ImmutableArray<Candidate>> ReadCandidatesAsync(
        SqliteCommand command,
        int limit,
        CancellationToken cancellationToken)
    {
        Bind(command, "$limit", limit);

        List<Candidate> candidates = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            candidates.Add(
                new Candidate(
                    Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                    Guid.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                    (CovenantSearchMatchClass)reader.GetInt32(7),
                    reader.GetDouble(8)));
        }

        return [.. candidates];
    }

    /// <summary>One ranked row, before its head is read.</summary>
    private readonly record struct Candidate(
        Guid EntryId,
        Guid VersionId,
        CovenantSearchMatchClass MatchClass,
        double Score);

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
            query.Terms.NormalizedTerms.Length == 1 ? query.Terms.NormalizedTerms[0] : NoExactKey);

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

    /// <summary>
    /// Reads the generation facts and the persisted rebuild debt in one statement, so both belong to the
    /// same snapshot as the rows the page is answered from.
    /// </summary>
    /// <remarks>
    /// The debt is not part of <see cref="CovenantSearchSourceSnapshot"/>, which a cursor encodes and which
    /// therefore decides when a cursor goes stale. A rebuild state change moves no row a page returns, so it
    /// must not invalidate one.
    /// </remarks>
    private static async ValueTask<(CovenantSearchSourceSnapshot Sources, bool RebuildOwed)> ReadSourcesAsync(
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

        CovenantSearchSourceSnapshot sources = new(
            new Guid((byte[])reader.GetValue(0)),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.IsDBNull(3) ? null : new Guid((byte[])reader.GetValue(3)),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetInt64(5),
            checked((ulong)reader.GetInt64(6)));

        // Idle is the only rebuild state that owes nothing, which is the rule the status publisher applies.
        return (sources, (CovenantFtsRebuildState)reader.GetInt32(7) != CovenantFtsRebuildState.Idle);
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
