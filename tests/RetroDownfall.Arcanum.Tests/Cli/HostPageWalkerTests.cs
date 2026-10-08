using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// The shared walk over a host list: it follows the host's continuation to the end, and a list that cannot
/// say whether it continues is read in a way that proves it rather than guesses.
/// </summary>
public sealed class HostPageWalkerTests
{
    private static Func<int, int, CancellationToken, Task<Result<string[]>>> Store(int rowCount, List<(int Limit, int Offset)> calls) =>
        (limit, offset, _) =>
        {
            calls.Add((limit, offset));

            string[] rows = [.. Enumerable.Range(offset, Math.Max(0, Math.Min(limit, rowCount - offset))).Select(static index => $"row-{index}")];

            return Task.FromResult(Result<string[]>.Success(rows));
        };

    [Theory]
    [InlineData(12, 3)]
    [InlineData(10, 3)]
    [InlineData(4, 1)]
    [InlineData(0, 1)]
    public async Task Following_reads_pages_until_one_is_short(int rowCount, int expectedCalls)
    {
        List<(int Limit, int Offset)> calls = [];

        Result<HostListing<string>> result = await HostPageWalker.ReadRowsAsync(
            "test list",
            firstRow: 0,
            singlePageRows: null,
            hostMaxRows: 100,
            followPageRows: 5,
            Store(rowCount, calls),
            static row => row,
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(rowCount, result.Value.Items.Length);

        Assert.False(result.Value.MoreAvailable);

        Assert.Equal(expectedCalls, calls.Count);
    }

    [Fact]
    public async Task Following_starts_at_the_row_it_was_given()
    {
        List<(int Limit, int Offset)> calls = [];

        Result<HostListing<string>> result = await HostPageWalker.ReadRowsAsync(
            "test list",
            firstRow: 7,
            singlePageRows: null,
            hostMaxRows: 100,
            followPageRows: 5,
            Store(12, calls),
            static row => row,
            CancellationToken.None);

        Assert.Equal(["row-7", "row-8", "row-9", "row-10", "row-11"], result.Value.Items);

        // The page that held the last five rows was full, so only the empty page after it proves it was the last.
        Assert.Equal([(5, 7), (5, 12)], calls);
    }

    [Fact]
    public async Task A_host_that_ignores_the_offset_is_a_no_progress_fault_with_nothing_listed()
    {
        int calls = 0;

        Result<HostListing<string>> result = await HostPageWalker.ReadRowsAsync<string>(
            "test list",
            firstRow: 0,
            singlePageRows: null,
            hostMaxRows: 100,
            followPageRows: 3,
            (_, _, _) =>
            {
                calls++;

                return Task.FromResult(
                    calls > 20
                        ? Result<string[]>.Failure(new Error("Test.Runaway", "The walk never ended."))
                        : Result<string[]>.Success(["same-0", "same-1", "same-2"]));
            },
            static row => row,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Api.PaginationNoProgress", result.Error.Code);

        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(12, 5, 5, true)]
    [InlineData(5, 5, 5, false)]
    [InlineData(3, 5, 3, false)]
    public async Task A_single_page_reads_one_row_past_what_it_shows(int rowCount, int requested, int expectedShown, bool expectedMore)
    {
        List<(int Limit, int Offset)> calls = [];

        Result<HostListing<string>> result = await HostPageWalker.ReadRowsAsync(
            "test list",
            firstRow: 0,
            singlePageRows: requested,
            hostMaxRows: 100,
            followPageRows: 5,
            Store(rowCount, calls),
            static row => row,
            CancellationToken.None);

        Assert.Equal(expectedShown, result.Value.Items.Length);

        Assert.Equal(expectedMore, result.Value.MoreAvailable);

        Assert.Equal([(requested + 1, 0)], calls);
    }

    /// <summary>
    /// At the host's own row ceiling the extra row cannot be asked for in the same request, so a full page
    /// is not evidence that more exist: the next row is asked for, and only a row that exists says so.
    /// </summary>
    [Theory]
    [InlineData(5, false)]
    [InlineData(6, true)]
    public async Task A_page_at_the_host_ceiling_probes_for_the_next_row_instead_of_guessing(int rowCount, bool expectedMore)
    {
        List<(int Limit, int Offset)> calls = [];

        Result<HostListing<string>> result = await HostPageWalker.ReadRowsAsync(
            "test list",
            firstRow: 0,
            singlePageRows: 50,
            hostMaxRows: 5,
            followPageRows: 5,
            Store(rowCount, calls),
            static row => row,
            CancellationToken.None);

        Assert.Equal(5, result.Value.Items.Length);

        Assert.Equal(expectedMore, result.Value.MoreAvailable);

        Assert.Equal([(5, 0), (1, 5)], calls);
    }

    [Fact]
    public async Task A_short_page_at_the_host_ceiling_needs_no_probe()
    {
        List<(int Limit, int Offset)> calls = [];

        Result<HostListing<string>> result = await HostPageWalker.ReadRowsAsync(
            "test list",
            firstRow: 0,
            singlePageRows: 5,
            hostMaxRows: 5,
            followPageRows: 5,
            Store(3, calls),
            static row => row,
            CancellationToken.None);

        Assert.False(result.Value.MoreAvailable);

        Assert.Equal([(5, 0)], calls);
    }

    [Fact]
    public async Task A_failed_probe_is_the_listings_failure()
    {
        int calls = 0;

        Result<HostListing<string>> result = await HostPageWalker.ReadRowsAsync<string>(
            "test list",
            firstRow: 0,
            singlePageRows: 5,
            hostMaxRows: 5,
            followPageRows: 5,
            (_, _, _) => Task.FromResult(
                ++calls == 1
                    ? Result<string[]>.Success(["a", "b", "c", "d", "e"])
                    : Result<string[]>.Failure(new Error("Connection.Unreachable", "down"))),
            static row => row,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Connection.Unreachable", result.Error.Code);
    }

    /// <summary>
    /// A host that answers the first offset page with the offset it just served has not advanced; the
    /// walk refuses it after the first page rather than reading that page a second time to find out.
    /// </summary>
    [Fact]
    public async Task An_offset_cursor_equal_to_the_first_pages_own_offset_is_refused_at_once()
    {
        int calls = 0;

        Result<HostListing<string>> result = await HostPageWalker.ReadAsync<string, int>(
            "test list",
            singlePage: false,
            (offset, _) =>
            {
                calls++;

                return Task.FromResult(
                    calls > 5
                        ? Result<HostPage<string, int>>.Failure(new Error("Test.Runaway", "The walk never ended."))
                        : Result<HostPage<string, int>>.Success(new HostPage<string, int>(["a"], HasMore: true, Next: 0)));
            },
            CancellationToken.None,
            firstPageCursor: 0);

        Assert.True(result.IsFailure);

        Assert.Equal("Api.PaginationNoProgress", result.Error.Code);

        Assert.Equal(1, calls);
    }

    [Fact]
    public void The_no_progress_fault_is_one_typed_error_for_every_walk()
    {
        Error error = HostPageWalker.NoProgressError("test list", "returned a continuation it had already returned", 7);

        Assert.Equal("Api.PaginationNoProgress", error.Code);

        Assert.Contains("test list", error.Message, StringComparison.Ordinal);

        Assert.Contains("7 row(s)", error.Message, StringComparison.Ordinal);

        Assert.Contains("Nothing was printed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_offset_page_without_a_named_continuation_is_followed_from_the_rows_already_read()
    {
        int calls = 0;

        Result<HostListing<string>> result = await HostPageWalker.ReadAsync<string, int>(
            "test list",
            singlePage: false,
            (offset, _) =>
            {
                calls++;

                return Task.FromResult(
                    HostPageWalker.ByOffset(
                        Result<ListPageResult<string>>.Success(
                            offset is null
                                ? new ListPageResult<string>(["a", "b"], true)
                                : new ListPageResult<string>(["c"], false)),
                        offset));
            },
            CancellationToken.None,
            firstPageCursor: 0);

        Assert.Equal(["a", "b", "c"], result.Value.Items);

        Assert.Equal(2, calls);
    }
}
