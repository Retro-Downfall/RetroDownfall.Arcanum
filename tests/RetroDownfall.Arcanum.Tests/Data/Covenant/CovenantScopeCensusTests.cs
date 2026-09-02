using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The content-free census behind <c>memory status</c>, against a real encrypted canonical tier.
/// </summary>
/// <remarks>
/// The counts an operator reads have to come from the rows that actually exist. A fake store would
/// let the census and the storage agree about a world neither of them read — and this is the surface
/// where "you have no standing preferences" is the most damaging thing to get wrong.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class CovenantScopeCensusTests
{

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public async Task An_empty_installation_reports_no_rows_rather_than_zeroed_ones()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        CovenantScopeCensus census = await CensusAsync(fixture);

        // A row of zeroes would read as "measured, and there are none of these"; absence reads as
        // "this installation holds nothing", which is the truthful answer for an empty tier.
        Assert.Empty(census.Rows);

        Assert.Equal(0, census.GlobalConfirmedRenderedBytes);

    }

    [Fact]
    public async Task Heads_are_counted_by_scope_lane_and_lifecycle()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        await fixture.AddCampaignAsync(CovenantOperationGateFixture.CampaignOne, "Census", Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "preference.builds",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Run build commands from the repository root.",
            Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CovenantOperationGateFixture.CampaignOne,
            "preference.style",
            CovenantLane.Proposed,
            CovenantOperation.Set,
            "Prefers terse commit subjects.",
            Token);

        // The two Campaign heads differ only by lane, so a census that grouped by scope alone would
        // fold them together and tell an operator they had one Campaign entry instead of two in
        // different standings.
        _ = await fixture.SeedHeadAsync(
            CovenantScope.Campaign,
            CovenantOperationGateFixture.CampaignOne,
            "preference.review",
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            "Review the diff before every commit.",
            Token);

        CovenantScopeCensus census = await CensusAsync(fixture);

        Assert.Equal(3, census.Rows.Length);

        long campaignConfirmedBytes = RowBytes(census, CovenantScope.Campaign, CovenantLane.Confirmed);

        long campaignProposedBytes = RowBytes(census, CovenantScope.Campaign, CovenantLane.Proposed);

        // The two Campaign contents are deliberately different lengths, so a census that routed both
        // Campaign lanes into one section total would have to disagree with one of these. Asserting
        // only that Campaign Confirmed is zero — as an installation holding no such head can — would
        // accept exactly that merge.
        Assert.True(campaignConfirmedBytes > 0);

        Assert.True(campaignProposedBytes > 0);

        Assert.NotEqual(campaignConfirmedBytes, campaignProposedBytes);

        Assert.Equal(campaignConfirmedBytes, census.MaxCampaignConfirmedRenderedBytes);

        Assert.Equal(campaignProposedBytes, census.MaxCampaignProposedRenderedBytes);

        Assert.Equal(
            RowBytes(census, CovenantScope.Global, CovenantLane.Confirmed),
            census.GlobalConfirmedRenderedBytes);

        Assert.Contains(
            census.Rows,
            row => row is
            {
                Scope: CovenantScope.Campaign,
                Lane: CovenantLane.Confirmed,
                Lifecycle: CovenantLifecycle.Set,
                Count: 1,
            });

        Assert.Contains(
            census.Rows,
            row => row is
            {
                Scope: CovenantScope.Global,
                Lane: CovenantLane.Confirmed,
                Lifecycle: CovenantLifecycle.Set,
                Count: 1,
            });

        Assert.Contains(
            census.Rows,
            row => row is
            {
                Scope: CovenantScope.Campaign,
                Lane: CovenantLane.Proposed,
                Lifecycle: CovenantLifecycle.Set,
                Count: 1,
            });

    }

    [Fact]
    public async Task A_retired_head_is_counted_as_retired_and_records_no_bytes_it_could_spend()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "preference.gone",
            CovenantLane.Confirmed,
            CovenantOperation.Retire,
            "Retired.",
            Token);

        CovenantScopeCensus census = await CensusAsync(fixture);

        CovenantScopeCensusRow row = Assert.Single(census.Rows);

        // The lifecycle is derived from the current operation rather than stored a second time, so a
        // tombstone has to read as retired without a column of its own.
        Assert.Equal(CovenantLifecycle.Retired, row.Lifecycle);

        Assert.Equal(0, row.RenderedBytes);

        Assert.Equal(0, census.GlobalConfirmedRenderedBytes);

    }

    [Fact]
    public async Task The_tier_refuses_to_record_bytes_against_a_tombstone()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        _ = await fixture.SeedHeadAsync(
            CovenantScope.Global,
            null,
            "preference.gone",
            CovenantLane.Confirmed,
            CovenantOperation.Retire,
            "Retired.",
            Token);

        // This is why the census sums every row's bytes without filtering retired ones out. A filter
        // would be a branch nothing can reach, and would read to the next person as though a drifted
        // tombstone were a state the totals had to defend against. The storage is the defence, so it
        // is the storage that gets asserted.
        SqliteException refused = await Assert.ThrowsAsync<SqliteException>(async () =>
        {

            await using SqliteCommand command = fixture.Connection.CreateCommand();

            command.CommandText = "UPDATE covenant_versions SET CompiledByteCost = 4096;";

            _ = await command.ExecuteNonQueryAsync(Token);

        });

        Assert.Contains("append-only", refused.Message, StringComparison.Ordinal);

        await using SqliteCommand head = fixture.Connection.CreateCommand();

        head.CommandText = "UPDATE covenant_heads SET CompiledByteCost = 4096;";

        SqliteException pinned = await Assert.ThrowsAsync<SqliteException>(() => head.ExecuteNonQueryAsync(Token));

        Assert.Contains("compiled byte cost of its current version", pinned.Message, StringComparison.Ordinal);

    }

    /// <summary>
    /// The Campaign figure is the largest Campaign's, because that is what the ceiling bounds.
    /// </summary>
    /// <remarks>
    /// The number is printed beside a per-placement, per-turn ceiling over one Campaign's rendered
    /// section. Summing every Campaign's bytes into it made ten Campaigns at ten percent read as
    /// nearly full — an operator would go pruning entries that were never close to the limit, and
    /// would have no way to find out which Campaign the figure was even about.
    /// </remarks>
    [Fact]
    public async Task Two_campaigns_under_the_ceiling_never_sum_into_a_figure_that_reads_as_breaching_it()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        await fixture.AddCampaignAsync(CovenantOperationGateFixture.CampaignOne, "First", Token);

        await fixture.AddCampaignAsync(CovenantOperationGateFixture.CampaignTwo, "Second", Token);

        // Two entries per Campaign rather than one large one: a single authored value is capped well
        // below the section ceiling, so one entry per Campaign could never build a sum that crosses it.
        string content = new('x', 1_500);

        foreach ((Guid campaignId, string prefix) in new[]
        {
            (CovenantOperationGateFixture.CampaignOne, "first"),
            (CovenantOperationGateFixture.CampaignTwo, "second"),
        })
        {

            for (int index = 0; index < 2; index++)
            {

                _ = await fixture.SeedHeadAsync(
                    CovenantScope.Campaign,
                    campaignId,
                    $"preference.{prefix}.{index}",
                    CovenantLane.Confirmed,
                    CovenantOperation.Set,
                    content,
                    Token);

            }

        }

        CovenantScopeCensus census = await CensusAsync(fixture);

        long installationWide = census.Rows
            .Where(static row => row is { Scope: CovenantScope.Campaign, Lane: CovenantLane.Confirmed })
            .Sum(static row => row.RenderedBytes);

        // The precondition the case rests on: the old installation-wide figure breaches the ceiling
        // printed beside it. Without this the assertion below would pass on an installation where
        // nothing could have breached it either way.
        Assert.True(
            installationWide > CovenantLimits.MaxCampaignConfirmedRenderedBytes,
            $"The two Campaigns must sum past the ceiling; they summed to {installationWide}.");

        Assert.True(
            census.MaxCampaignConfirmedRenderedBytes < CovenantLimits.MaxCampaignConfirmedRenderedBytes,
            $"No single Campaign breaches the ceiling, so the reported figure must not: "
            + $"{census.MaxCampaignConfirmedRenderedBytes}.");

        // And it is a real Campaign's total, not a zero that would pass the line above for free. Two
        // Campaigns hold equal shares, so the largest is at least half.
        Assert.True(census.MaxCampaignConfirmedRenderedBytes >= installationWide / 2);

    }

    [Fact]
    public async Task A_scoped_lease_cannot_take_a_census_that_crosses_every_campaign()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantReadLease scoped =
            (await gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).Value;

        Result<CovenantScopeCensus> refused = await fixture.Store
            .ReadScopeCensusAsync(scoped, Token);

        // A scoped lease could only ever answer for its own scope, and a census that silently omitted
        // the rest would understate what the installation holds.
        Assert.True(refused.IsFailure);

    }

    [Fact]
    public async Task A_canonical_tier_that_cannot_be_read_returns_a_failure_rather_than_throwing()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        await using (SqliteCommand drop = fixture.Connection.CreateCommand())
        {

            drop.CommandText = "DROP TABLE covenant_heads;";

            _ = await drop.ExecuteNonQueryAsync(Token);

        }

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantInstallationReadLease lease =
            (await gate.AcquireInstallationReadAsync(Token)).Value;

        // Status is a read-only request an operator makes to find out whether their memory works. A
        // storage failure that escaped this Result-returning port would unwind through the endpoint
        // instead of reaching the degradation the management service already renders.
        Result<CovenantScopeCensus> read = await fixture.Store.ReadScopeCensusAsync(lease, Token);

        Assert.True(read.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.Unavailable, read.Error.Code);

    }

    /// <summary>
    /// W15-2: the finally block's rollback must run on <see cref="CancellationToken.None"/>, not the
    /// caller's token. When the caller's token is already cancelled by the time the finally block
    /// runs, rolling back on that token throws a fresh <see cref="OperationCanceledException"/> that
    /// discards whatever the try block had already built — a well-formed <see cref="Result{T}"/>,
    /// success or failure — and unwinds the read-only status request the method's own contract says
    /// must never unwind.
    /// </summary>
    [Fact]
    public async Task ReadScopeCensusAsync_still_returns_a_result_when_the_token_cancels_before_the_rollback()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantInstallationReadLease lease =
            (await gate.AcquireInstallationReadAsync(Token)).Value;

        using CancellationTokenSource cts = new();

        // Cancels exactly where the census readers have already drained and the finally block is the
        // only thing left standing between the built result and the caller, matching the finding's
        // own interleaving.
        fixture.Store.BeforeReadScopeCensusRollbackForTesting = cts.Cancel;

        Result<CovenantScopeCensus> census = await fixture.Store.ReadScopeCensusAsync(lease, cts.Token);

        Assert.True(census.IsSuccess, census.IsFailure ? census.Error.Message : string.Empty);

    }

    private static long RowBytes(CovenantScopeCensus census, CovenantScope scope, CovenantLane lane) =>
        census.Rows.Single(row => row.Scope == scope && row.Lane == lane).RenderedBytes;

    private static async Task<CovenantScopeCensus> CensusAsync(CovenantCanonicalFixture fixture)
    {

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantInstallationReadLease lease =
            (await gate.AcquireInstallationReadAsync(Token)).Value;

        Result<CovenantScopeCensus> census = await fixture.Store.ReadScopeCensusAsync(lease, Token);

        Assert.True(census.IsSuccess, census.IsFailure ? census.Error.Message : string.Empty);

        return census.Value;

    }

}
