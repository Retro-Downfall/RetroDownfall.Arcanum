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
    public static async Task<Result<HostListing<TItem>>> ReadAsync<TItem, TCursor>(
        string listName,
        bool singlePage,
        Func<TCursor?, CancellationToken, Task<Result<HostPage<TItem, TCursor>>>> readPageAsync,
        CancellationToken cancellationToken)
        where TCursor : struct
    {
        ArgumentNullException.ThrowIfNull(readPageAsync);

        List<TItem> all = [];

        HashSet<TCursor> seen = [];

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

    private static Result<HostListing<TItem>> NoProgress<TItem>(string listName, string reason, int rowsRead) =>
        Result<HostListing<TItem>>.Failure(
            new Error(
                "Api.PaginationNoProgress",
                $"The host's {listName} {reason} after {rowsRead} row(s), so the list cannot be completed. "
                + "Nothing was printed; retry after repairing or upgrading the host."));
}
