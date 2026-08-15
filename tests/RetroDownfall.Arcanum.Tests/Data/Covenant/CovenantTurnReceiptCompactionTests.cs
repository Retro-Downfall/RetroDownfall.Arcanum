using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// Bounded folding of a Session's turn-receipt tail into its guarded aggregate row.
/// </summary>
public sealed class CovenantTurnReceiptCompactionTests
{

    private static readonly Guid SessionId = new("eeeeeeee-1111-4111-8111-111111111111");

    private static readonly DateTimeOffset Origin = new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public async Task A_tail_within_its_bound_folds_nothing()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCapacityFixture.CreateAsync(Token);

        await CovenantCapacityFixture.AddSessionAsync(fixture, SessionId, Token);

        await SeedReceiptsAsync(fixture, CovenantLimits.MaxTurnReceiptsPerSession);

        Assert.Equal(0, await FoldAsync(fixture));

        Assert.Equal(
            0,
            await CovenantCapacityFixture.ScalarAsync(
                fixture,
                "SELECT COUNT(*) FROM covenant_turn_receipt_aggregate;",
                Token));

    }

    [Fact]
    public async Task An_overflowing_tail_folds_from_its_oldest_end()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCapacityFixture.CreateAsync(Token);

        await CovenantCapacityFixture.AddSessionAsync(fixture, SessionId, Token);

        await SeedReceiptsAsync(fixture, CovenantLimits.MaxTurnReceiptsPerSession + 5);

        Assert.Equal(5, await FoldAsync(fixture));

        Assert.Equal(
            CovenantLimits.MaxTurnReceiptsPerSession,
            await CovenantCapacityFixture.ScalarAsync(fixture, "SELECT COUNT(*) FROM covenant_turn_receipts;", Token));

        Assert.Equal(
            5,
            await CovenantCapacityFixture.ScalarAsync(
                fixture,
                "SELECT CoveredCount FROM covenant_turn_receipt_aggregate;",
                Token));

        Assert.Equal(
            50,
            await CovenantCapacityFixture.ScalarAsync(
                fixture,
                "SELECT ConfirmedTokenTotal FROM covenant_turn_receipt_aggregate;",
                Token));

        Assert.Equal(
            5,
            await CovenantCapacityFixture.ScalarAsync(
                fixture,
                "SELECT CompletedOutcomeCount FROM covenant_turn_receipt_aggregate;",
                Token));

        // The oldest five went, so the surviving tail starts after them.
        Assert.Equal(
            0,
            await CovenantCapacityFixture.ScalarAsync(
                fixture,
                $"SELECT COUNT(*) FROM covenant_turn_receipts WHERE CreatedAtUtc < '{Iso(Origin.AddMinutes(5))}';",
                Token));

    }

    [Fact]
    public async Task One_fold_never_moves_more_than_its_bound()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCapacityFixture.CreateAsync(Token);

        await CovenantCapacityFixture.AddSessionAsync(fixture, SessionId, Token);

        await SeedReceiptsAsync(fixture, CovenantLimits.MaxTurnReceiptsPerSession + 300);

        // The write path must never perform an unbounded fold, so an overflow of 300 takes three
        // calls rather than one long one.
        Assert.Equal(CovenantTurnReceiptCompactor.MaxReceiptsPerFold, await FoldAsync(fixture));

        Assert.Equal(CovenantTurnReceiptCompactor.MaxReceiptsPerFold, await FoldAsync(fixture));

        Assert.Equal(300 - (2 * CovenantTurnReceiptCompactor.MaxReceiptsPerFold), await FoldAsync(fixture));

        Assert.Equal(0, await FoldAsync(fixture));

        Assert.Equal(
            300,
            await CovenantCapacityFixture.ScalarAsync(
                fixture,
                "SELECT CoveredCount FROM covenant_turn_receipt_aggregate;",
                Token));

    }

    [Fact]
    public async Task Folding_is_order_sensitive_and_produces_one_row_per_session()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCapacityFixture.CreateAsync(Token);

        await CovenantCapacityFixture.AddSessionAsync(fixture, SessionId, Token);

        await SeedReceiptsAsync(fixture, CovenantLimits.MaxTurnReceiptsPerSession + 2);

        _ = await FoldAsync(fixture);

        long firstChain = await CovenantCapacityFixture.ScalarAsync(
            fixture,
            "SELECT length(ChainDigest) FROM covenant_turn_receipt_aggregate;",
            Token);

        Assert.Equal(32, firstChain);

        Assert.Equal(
            1,
            await CovenantCapacityFixture.ScalarAsync(
                fixture,
                "SELECT COUNT(*) FROM covenant_turn_receipt_aggregate;",
                Token));

        Assert.Equal(
            1,
            await CovenantCapacityFixture.ScalarAsync(
                fixture,
                "SELECT COUNT(DISTINCT SessionId) FROM covenant_turn_receipt_aggregate;",
                Token));

    }

    [Fact]
    public async Task An_uncommitted_fold_leaves_the_tail_untouched()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCapacityFixture.CreateAsync(Token);

        await CovenantCapacityFixture.AddSessionAsync(fixture, SessionId, Token);

        await SeedReceiptsAsync(fixture, CovenantLimits.MaxTurnReceiptsPerSession + 3);

        _ = await CovenantCapacityFixture.InTransactionAsync(
            fixture,
            transaction => new CovenantTurnReceiptCompactor().FoldAsync(SessionId, transaction, Token).AsTask(),
            Token,
            commit: false);

        Assert.Equal(
            CovenantLimits.MaxTurnReceiptsPerSession + 3,
            await CovenantCapacityFixture.ScalarAsync(fixture, "SELECT COUNT(*) FROM covenant_turn_receipts;", Token));

        Assert.Equal(
            0,
            await CovenantCapacityFixture.ScalarAsync(
                fixture,
                "SELECT COUNT(*) FROM covenant_turn_receipt_aggregate;",
                Token));

    }

    private static async Task<int> FoldAsync(CovenantCanonicalFixture fixture)
    {

        Result<int> folded = await CovenantCapacityFixture.InTransactionAsync(
            fixture,
            transaction => new CovenantTurnReceiptCompactor().FoldAsync(SessionId, transaction, Token).AsTask(),
            Token);

        Assert.True(folded.IsSuccess, folded.IsFailure ? folded.Error.Message : null);

        return folded.Value;

    }

    private static async Task SeedReceiptsAsync(CovenantCanonicalFixture fixture, int count)
    {

        for (int index = 0; index < count; index++)
        {

            await CovenantCapacityFixture.AddTurnReceiptAsync(
                fixture,
                SessionId,
                new Guid($"ffffffff-0000-4000-8000-{index:000000000000}"),
                Origin.AddMinutes(index),
                CovenantFinalOutcome.Completed,
                Token);

        }

    }

    private static string Iso(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", System.Globalization.CultureInfo.InvariantCulture);

}
