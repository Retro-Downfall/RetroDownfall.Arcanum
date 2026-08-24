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
