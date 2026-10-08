using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Cli.Services;

/// <summary>
/// One page of a host list and the continuation that reaches the next one.
/// </summary>
/// <typeparam name="TItem">The listed row.</typeparam>
/// <typeparam name="TCursor">The continuation: a row offset or an updated-at timestamp.</typeparam>
/// <param name="Items">The rows of this page.</param>
/// <param name="HasMore">Whether the host reports rows beyond this page.</param>
/// <param name="Next">The continuation the host named, or the one derived from the page when it named none.</param>
internal sealed record HostPage<TItem, TCursor>(TItem[] Items, bool HasMore, TCursor? Next)
    where TCursor : struct;

/// <summary>
/// The rows read from a host list and whether the host holds more than those rows.
/// </summary>
/// <param name="Items">Every row read.</param>
/// <param name="MoreAvailable">Whether the host reported rows beyond them, which only a single-page read leaves behind.</param>
internal sealed record HostListing<TItem>(TItem[] Items, bool MoreAvailable);

/// <summary>
/// Reads a host list, following its continuation until it is exhausted.
/// </summary>
/// <remarks>
/// A list that shows only the first server page looks complete and is not: the host bounds every list
/// to one page and reports the rest through <c>HasMore</c> and a continuation. This follows that
/// continuation, so an operator deciding what to delete or cancel is looking at every row. A
/// continuation that never advances is a fault, not a reason to loop: the host repeating a cursor, or
/// reporting more rows without naming one, ends the read with <c>Api.PaginationNoProgress</c> and no
/// partial listing, so nothing is printed that could be mistaken for the whole.
/// </remarks>
internal static class HostPageWalker
{
    /// <summary>
    /// Reads a list from its first row.
    /// </summary>
    /// <param name="listName">What the list is, named in the no-progress fault.</param>
    /// <param name="singlePage">
    /// Read exactly one page, which is what an operator-supplied <c>--limit</c> asks for, and report
    /// whether the host holds more.
    /// </param>
    /// <param name="readPageAsync">Reads the page at a continuation; <see langword="null"/> is the first page.</param>
    /// <param name="cancellationToken">Stops the read between and during pages.</param>
    /// <param name="firstPageCursor">
    /// The continuation value that addresses the first page, when the cursor has one (offset <c>0</c> for an
    /// offset-paged list). A host that answers the first page with that same value as its continuation has
    /// not advanced, so it is refused at once rather than after the first page is read a second time.
    /// </param>
    public static async Task<Result<HostListing<TItem>>> ReadAsync<TItem, TCursor>(
        string listName,
        bool singlePage,
        Func<TCursor?, CancellationToken, Task<Result<HostPage<TItem, TCursor>>>> readPageAsync,
        CancellationToken cancellationToken,
        TCursor? firstPageCursor = null)
        where TCursor : struct
    {
        ArgumentNullException.ThrowIfNull(readPageAsync);

        List<TItem> all = [];

        HashSet<TCursor> seen = [];

        if (firstPageCursor is { } first)
        {
            seen.Add(first);
        }

        TCursor? cursor = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Result<HostPage<TItem, TCursor>> page = await readPageAsync(cursor, cancellationToken).ConfigureAwait(false);

            if (page.IsFailure)
            {
                return Result<HostListing<TItem>>.Failure(page.Error);
            }

            all.AddRange(page.Value.Items);

            if (!page.Value.HasMore)
            {
                return Result<HostListing<TItem>>.Success(new HostListing<TItem>([.. all], MoreAvailable: false));
            }

            if (singlePage)
            {
                return Result<HostListing<TItem>>.Success(new HostListing<TItem>([.. all], MoreAvailable: true));
            }

            if (page.Value.Next is not { } next)
            {
                return NoProgress<TItem>(listName, "reported more rows without naming where they continue", all.Count);
            }

            if (!seen.Add(next))
            {
                return NoProgress<TItem>(listName, "returned a continuation it had already returned", all.Count);
            }

            cursor = next;
        }
    }

    /// <summary>
    /// Reads a list the host answers with a bare array and no continuation, by row offset.
    /// </summary>
    /// <remarks>
    /// <para>With nothing to follow, the only proof that rows remain is a row that exists. Following reads
    /// pages until one is short, so the last page is known to be the last. A single page reads one row past
    /// what it shows, and at the host's own row ceiling, where that extra row cannot be asked for in the
    /// same request, reads one row beyond it in a second: either way "more rows exist" is a fact the host
    /// reported, not a guess from a full page.</para>
    /// <para>A host that ignores the offset would hand back the same page for ever, so a page that opens
    /// with the same row as the page before it ends the read with <c>Api.PaginationNoProgress</c> and nothing
    /// printed.</para>
    /// </remarks>
    /// <param name="listName">What the list is, named in the no-progress fault.</param>
    /// <param name="firstRow">The offset of the first row to read.</param>
    /// <param name="singlePageRows">
    /// How many rows to read when the operator asked for exactly one page of that size, or
    /// <see langword="null"/> to follow the list to its end.
    /// </param>
    /// <param name="hostMaxRows">The most rows the host returns for one request; it clamps a larger limit.</param>
    /// <param name="followPageRows">The page size used when following, bounded by <paramref name="hostMaxRows"/>.</param>
    /// <param name="readRowsAsync">Reads the rows at <c>(limit, offset)</c>.</param>
    /// <param name="identity">A row's identity, which tells a repeated page from a new one.</param>
    /// <param name="cancellationToken">Stops the read between and during pages.</param>
    public static async Task<Result<HostListing<TItem>>> ReadRowsAsync<TItem>(
        string listName,
        int firstRow,
        int? singlePageRows,
        int hostMaxRows,
        int followPageRows,
        Func<int, int, CancellationToken, Task<Result<TItem[]>>> readRowsAsync,
        Func<TItem, string> identity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readRowsAsync);

        ArgumentNullException.ThrowIfNull(identity);

        if (singlePageRows is { } requested)
        {
            int shown = Math.Clamp(requested, 1, hostMaxRows);

            bool probeSeparately = shown >= hostMaxRows;

            Result<TItem[]> page = await readRowsAsync(
                    probeSeparately ? hostMaxRows : shown + 1,
                    firstRow,
                    cancellationToken)
                .ConfigureAwait(false);

            if (page.IsFailure)
            {
                return Result<HostListing<TItem>>.Failure(page.Error);
            }

            bool more = page.Value.Length > shown;

            if (probeSeparately && page.Value.Length >= shown)
            {
                Result<TItem[]> probe = await readRowsAsync(1, firstRow + shown, cancellationToken).ConfigureAwait(false);

                if (probe.IsFailure)
                {
                    return Result<HostListing<TItem>>.Failure(probe.Error);
                }

                more = probe.Value.Length > 0;
            }

            return Result<HostListing<TItem>>.Success(
                new HostListing<TItem>([.. page.Value.Take(shown)], more));
        }

        int pageRows = Math.Clamp(followPageRows, 1, hostMaxRows);

        List<TItem> all = [];

        string? previousFirst = null;

        int offset = firstRow;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Result<TItem[]> page = await readRowsAsync(pageRows, offset, cancellationToken).ConfigureAwait(false);

            if (page.IsFailure)
            {
                return Result<HostListing<TItem>>.Failure(page.Error);
            }

            if (page.Value.Length > 0)
            {
                string first = identity(page.Value[0]);

                if (first == previousFirst)
                {
                    return NoProgress<TItem>(listName, "returned the same rows for a new offset", all.Count);
                }

                previousFirst = first;
            }

            all.AddRange(page.Value.Take(pageRows));

            if (page.Value.Length < pageRows)
            {
                return Result<HostListing<TItem>>.Success(new HostListing<TItem>([.. all], MoreAvailable: false));
            }

            offset += pageRows;
        }
    }

    /// <summary>Adapts an offset-paged <see cref="ListPageResult{T}"/> for <see cref="ReadAsync"/>.</summary>
    /// <param name="result">The host's answer for the page that began at <paramref name="offset"/>.</param>
    /// <param name="offset">Where that page began; <see langword="null"/> for the first page.</param>
    public static Result<HostPage<TItem, int>> ByOffset<TItem>(
        Result<ListPageResult<TItem>> result,
        int? offset)
    {
        if (result.IsFailure)
        {
            return Result<HostPage<TItem, int>>.Failure(result.Error);
        }

        ListPageResult<TItem> page = result.Value;

        int? next = page.NextOffset
            ?? (page.Items.Length > 0 ? (offset ?? 0) + page.Items.Length : null);

        return Result<HostPage<TItem, int>>.Success(new HostPage<TItem, int>(page.Items, page.HasMore, next));
    }

    /// <summary>Adapts an updated-at-paged <see cref="ListPageResult{T}"/> for <see cref="ReadAsync"/>.</summary>
    /// <param name="result">The host's answer for the page.</param>
    /// <param name="updatedAt">A row's last-update instant, the cursor the host pages by.</param>
    public static Result<HostPage<TItem, DateTimeOffset>> ByUpdatedAt<TItem>(
        Result<ListPageResult<TItem>> result,
        Func<TItem, DateTimeOffset> updatedAt)
    {
        if (result.IsFailure)
        {
            return Result<HostPage<TItem, DateTimeOffset>>.Failure(result.Error);
        }

        ListPageResult<TItem> page = result.Value;

        DateTimeOffset? next = page.NextBeforeUpdatedAt
            ?? (page.Items.Length > 0 ? updatedAt(page.Items[^1]) : null);

        return Result<HostPage<TItem, DateTimeOffset>>.Success(new HostPage<TItem, DateTimeOffset>(page.Items, page.HasMore, next));
    }

    /// <summary>Adapts a session query page for <see cref="ReadAsync"/>.</summary>
    /// <param name="result">The host's answer for the page.</param>
    public static Result<HostPage<SessionSummaryDto, DateTimeOffset>> BySessions(Result<SessionQueryResult> result)
    {
        if (result.IsFailure)
        {
            return Result<HostPage<SessionSummaryDto, DateTimeOffset>>.Failure(result.Error);
        }

        SessionQueryResult page = result.Value;

        DateTimeOffset? next = page.NextBeforeUpdatedAt
            ?? (page.Summaries.Length > 0 ? page.Summaries[^1].UpdatedAt : null);

        return Result<HostPage<SessionSummaryDto, DateTimeOffset>>.Success(
            new HostPage<SessionSummaryDto, DateTimeOffset>(page.Summaries, page.HasMore, next));
    }

    /// <summary>
    /// The fault for a list whose continuation cannot advance: the host repeated a cursor, or reported more
    /// rows without naming where they continue.
    /// </summary>
    /// <param name="listName">What the list is.</param>
    /// <param name="reason">What the host did, in words that follow "the host's list".</param>
    /// <param name="rowsRead">How many rows had been read when the fault was found.</param>
    public static Error NoProgressError(string listName, string reason, int rowsRead) =>
        new(
            "Api.PaginationNoProgress",
            $"The host's {listName} {reason} after {rowsRead} row(s), so the list cannot be completed. "
            + "Nothing was printed; retry after repairing or upgrading the host.");

    private static Result<HostListing<TItem>> NoProgress<TItem>(string listName, string reason, int rowsRead) =>
        Result<HostListing<TItem>>.Failure(NoProgressError(listName, reason, rowsRead));
}
