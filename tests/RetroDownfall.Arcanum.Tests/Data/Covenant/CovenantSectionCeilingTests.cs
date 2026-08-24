using System.Globalization;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The Section ceilings, enforced where an operator can still be told about them.
/// </summary>
/// <remarks>
/// One entry may be 2,048 authored bytes and a Section may be 4,096 rendered bytes, so two ordinary
/// writes -- each individually legal, each accepted without complaint -- are enough to assemble a
/// Section the renderer refuses. The damage is not confined to the write: the placement stops
/// rendering for every turn afterwards, which is the Covenant silently ceasing to exist rather than
/// a mutation failing.
/// </remarks>
public sealed class CovenantSectionCeilingTests
{

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public async Task A_write_that_would_render_its_section_past_the_byte_ceiling_is_refused()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        // Two of these fit the 4,096-byte Global Confirmed Section and the third cannot, so the
        // refusal lands on a batch every scope-wide quota would have waved through.
        string content = new('a', 2_000);

        Assert.True(await SetAsync(fixture, "global.bulk0", content));

        Assert.True(await SetAsync(fixture, "global.bulk1", content));

        Result<IReadOnlyList<CovenantMutationReceipt>> refused = await ApplyAsync(fixture, "global.bulk2", content);

        Assert.True(refused.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.CapacityExceeded, refused.Error.Code);

    }

    [Fact]
    public async Task An_installation_that_accepted_every_write_still_renders_its_covenant()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        string content = new('a', 2_000);

        _ = await SetAsync(fixture, "global.bulk0", content);

        _ = await SetAsync(fixture, "global.bulk1", content);

        _ = await SetAsync(fixture, "global.bulk2", content);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantReadLease lease =
            (await gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).Value;

        Result<CovenantTurnSnapshot> snapshot = await fixture.Store.ReadTurnSnapshotAsync(
            CanonicalCampaignContext.GlobalOnly,
            lease,
            Token);

        // The point of refusing at write time is that the turn path never has to meet a Section it
        // cannot render. Whatever the store accepted has to link.
        Result<CovenantTurnPlan> linked = new CovenantLinker().Link(snapshot.Value);

        Assert.True(linked.IsSuccess, linked.IsFailure ? linked.Error.Message : null);

    }

    [Fact]
    public async Task Rewriting_an_entry_is_charged_once_rather_than_twice()
    {

        await using CovenantCanonicalFixture fixture = await CovenantCanonicalFixture.CreateAsync(Token);

        // Without subtracting what the key already occupies, a guard would read this edit as adding a
        // second 2,040-byte entry and refuse every ordinary correction to a large preference.
        Assert.True(await SetAsync(fixture, "global.large", new string('a', 2_040)));

        Result<IReadOnlyList<CovenantMutationReceipt>> rewritten = await ApplyAsync(
            fixture,
            "global.large",
            new string('b', 2_040),
            expectedRevision: 1);

        Assert.True(rewritten.IsSuccess, rewritten.IsFailure ? rewritten.Error.Message : null);

    }

    [Fact]
    public void The_write_path_and_the_render_path_size_a_section_identically()
    {

        // The two enforcement points are only safe while they agree. A Proposed Section carries two
        // fences plus "text\n" and a closing newline; a Confirmed Section is a bare concatenation.
        Assert.Equal(
            2_012L,
            CovenantSectionCapacity.RenderedBytes(CovenantPlacement.CampaignProposed, 1, 2_000, 3));

        Assert.Equal(
            2_000L,
            CovenantSectionCapacity.RenderedBytes(CovenantPlacement.GlobalConfirmed, 1, 2_000, 9));

        Assert.Equal(
            0L,
            CovenantSectionCapacity.RenderedBytes(CovenantPlacement.CampaignProposed, 0, 0, 3));

    }

    private static async Task<bool> SetAsync(CovenantCanonicalFixture fixture, string key, string content)
    {

        Result<IReadOnlyList<CovenantMutationReceipt>> applied = await ApplyAsync(fixture, key, content);

        return applied.IsSuccess;

    }

    private static async Task<Result<IReadOnlyList<CovenantMutationReceipt>>> ApplyAsync(
        CovenantCanonicalFixture fixture,
        string key,
        string content,
        long expectedRevision = 0) =>
        await CovenantMutationFixture.ApplyAsync(
            fixture,
            await CovenantMutationFixture.LiveBatchAsync(
                fixture,
                Token,
                CovenantMutationFixture.OperatorSet(
                    CovenantOperationScope.Global,
                    key,
                    content,
                    expectedRevision,
                    await KeyEpochAsync(fixture, key))),
            Token);

    /// <summary>
    /// The key-reclamation epoch a second write to the same key has to bind.
    /// </summary>
    /// <remarks>
    /// A rewrite is refused as a stale snapshot unless it names the epoch it will actually meet, so a
    /// test that hard-codes zero fails for a reason that has nothing to do with Section capacity.
    /// </remarks>
    private static async Task<long> KeyEpochAsync(CovenantCanonicalFixture fixture, string key)
    {

        await using SqliteCommand command = fixture.Connection.CreateCommand();

        command.CommandText = "SELECT COALESCE(MAX(KeyEpoch), 0) FROM covenant_key_epochs WHERE NormalizedKey = $key;";

        _ = command.Parameters.AddWithValue("$key", key);

        return Convert.ToInt64(await command.ExecuteScalarAsync(Token), CultureInfo.InvariantCulture);

    }

}
