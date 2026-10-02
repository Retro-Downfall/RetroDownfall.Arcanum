using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The effective disclosure read: the joined buckets plus every receipt the fold has not reached yet
/// (§10.13).
/// </summary>
/// <remarks>
/// The receipts a build without the live fold wrote sit in subject tails whose watermark is still zero
/// and appear in no bucket. A reader that only read the buckets would tell an operator that nothing
/// left this installation when the receipts say otherwise, so every suite here starts from that
/// history.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class ExternalDisclosureStateReaderTests
{

    private static readonly Guid Installation = Guid.Parse("21212121-3434-4545-8656-787878787878");

    private static readonly Guid SubjectA = Guid.Parse("a1a1a1a1-0000-4000-8000-000000000001");

    private static readonly Guid SubjectB = Guid.Parse("b2b2b2b2-0000-4000-8000-000000000002");

    private static readonly Guid SubjectC = Guid.Parse("c3c3c3c3-0000-4000-8000-000000000003");

    private static readonly Guid BootId = Guid.Parse("d4d4d4d4-0000-4000-8000-000000000004");

    private static readonly string[] CoreObjects =
    [
        "external_disclosure_receipts",
        "disclosure_subject_state",
        "external_disclosure_receipts_guard_delete",
        "external_disclosure_receipts_guard_update",
        "disclosure_subject_state_guard_delete",
        "external_disclosure_state",
    ];

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public async Task Effective_state_counts_an_unfolded_backlog_as_a_lower_bound()
    {

        await using CovenantCanonicalFixture fixture = await CreateAsync();

        await BacklogAsync(fixture, SubjectA, 2);

        CovenantDisclosureState bucket = Assert.Single(
            await ExternalDisclosureStateReader.ReadEffectiveAsync(fixture.Connection, Token));

        Assert.Equal(
            (CovenantEgressDestination.Provider, CovenantDisclosureRevocability.Nonrevocable,
                CovenantDisclosureCountKind.LowerBound, 2ul),
            (bucket.Destination, bucket.Revocability, bucket.CountKind, bucket.Count));

        // The read folds in memory only. Writing here would be a reader acting as a second producer.
        Assert.Equal(0, await ScalarAsync(fixture, "SELECT COUNT(*) FROM external_disclosure_state;"));

    }

    [Fact]
    public async Task Effective_state_is_identical_before_and_after_the_backlog_is_folded()
    {

        await using CovenantCanonicalFixture fixture = await CreateAsync();

        Result<CovenantDisclosureReceipt> live = await new CovenantDisclosureTransactionWriter(BootId)
            .AcknowledgeAsync(
                fixture.Connection,
                Draft(SubjectC, 1, 1_700_000_000_000),
                CovenantDisclosureEffectCategory.ProviderDispatch,
                PreFoldDisclosureHistory.Sensitivity,
                Token);

        Assert.True(live.IsSuccess, live.Error.Message);

        await BacklogAsync(fixture, SubjectA, 3);

        await BacklogAsync(fixture, SubjectB, 2);

        CovenantDisclosureState before = Assert.Single(
            await ExternalDisclosureStateReader.ReadEffectiveAsync(fixture.Connection, Token));

        Assert.Equal((CovenantDisclosureCountKind.LowerBound, 6ul), (before.CountKind, before.Count));

        await using (SqliteTransaction transaction = fixture.Connection.BeginTransaction(deferred: false))
        {

            Assert.Equal(
                2,
                await ExternalDisclosureStateFold.FoldAllUnfoldedAsync(
                    fixture.Connection,
                    transaction,
                    ExternalDisclosureFoldOrigin.RestoreStaging,
                    Token));

            await transaction.CommitAsync(Token);

        }

        CovenantDisclosureState after = Assert.Single(
            await ExternalDisclosureStateReader.ReadEffectiveAsync(fixture.Connection, Token));

        // Count, newest instant and Bloom all equal: folding moves receipts from the tail into the
        // bucket, and the effective read is the same before and after.
        Assert.Equal(before, after);

        Assert.Equal(
            before,
            Assert.Single(await ExternalDisclosureStateStore.ReadAllAsync(fixture.Connection, null, Token)));

        Assert.Equal(
            0,
            await ScalarAsync(
                fixture,
                "SELECT COUNT(*) FROM disclosure_subject_state WHERE LastFoldedOrdinal < LastAllocatedOrdinal;"));

    }

    [Fact]
    public async Task Effective_state_reads_persisted_buckets_and_tails_in_one_snapshot()
    {

        await using CovenantCanonicalFixture fixture = await CreateAsync();

        await BacklogAsync(fixture, SubjectA, 2);

        IReadOnlyList<CovenantDisclosureState> effective = await ExternalDisclosureStateReader.ReadEffectiveAsync(
            fixture.Connection,
            Token,
            async cancellationToken =>
            {

                // A fold that commits between the bucket read and the tail read. Read outside one
                // snapshot, the buckets come from before it and the tails from after it, and the two
                // receipts it moved are in neither.
                await using SqliteConnection other = await fixture.OpenAdditionalConnectionAsync(cancellationToken);

                await using SqliteTransaction transaction = other.BeginTransaction(deferred: false);

                Assert.Equal(
                    1,
                    await ExternalDisclosureStateFold.FoldAllUnfoldedAsync(
                        other,
                        transaction,
                        ExternalDisclosureFoldOrigin.RestoreStaging,
                        cancellationToken));

                await transaction.CommitAsync(cancellationToken);

            });

        CovenantDisclosureState bucket = Assert.Single(effective);

        Assert.Equal((CovenantDisclosureCountKind.LowerBound, 2ul), (bucket.CountKind, bucket.Count));

        Assert.Equal(
            bucket,
            Assert.Single(await ExternalDisclosureStateStore.ReadAllAsync(fixture.Connection, null, Token)));

    }

    [Fact]
    public async Task An_unrebuildable_receipt_row_is_still_counted()
    {

        await using CovenantCanonicalFixture fixture = await CreateAsync();

        CovenantDisclosureDraft draft = Draft(SubjectA, 1, 1_700_000_000_000);

        await PreFoldDisclosureHistory.InsertAsync(
            fixture.Connection,
            draft,
            1,
            Token,
            exactGenerationIds: new byte[16]);

        CovenantDisclosureState bucket = Assert.Single(
            await ExternalDisclosureStateReader.ReadEffectiveAsync(fixture.Connection, Token));

        Assert.Equal(
            (CovenantEgressDestination.Provider, CovenantDisclosureRevocability.Nonrevocable,
                CovenantDisclosureCountKind.LowerBound, 1ul),
            (bucket.Destination, bucket.Revocability, bucket.CountKind, bucket.Count));

        Assert.Contains(bucket.EvidenceBloom, static value => value != 0);

        Assert.Equal(
            CovenantDisclosureStateAlgebra.CreateEvidenceBloom(draft.EffectIdentityDigest),
            bucket.EvidenceBloom.ToArray());

    }

    /// <summary>
    /// The exposure count is read without rebuilding receipts, so it has to be pinned to the bucket
    /// read it summarizes: the nonrevocable buckets' total, lower bound when any of them is.
    /// </summary>
    [Fact]
    public async Task Exposure_is_the_nonrevocable_sum_of_the_effective_buckets()
    {

        await using CovenantCanonicalFixture fixture = await CreateAsync();

        Result<CovenantDisclosureReceipt> live = await new CovenantDisclosureTransactionWriter(BootId)
            .AcknowledgeAsync(
                fixture.Connection,
                Draft(SubjectC, 1, 1_700_000_000_000),
                CovenantDisclosureEffectCategory.ProviderDispatch,
                PreFoldDisclosureHistory.Sensitivity,
                Token);

        Assert.True(live.IsSuccess, live.Error.Message);

        await BacklogAsync(fixture, SubjectA, 2);

        await PreFoldDisclosureHistory.InsertAsync(
            fixture.Connection,
            Draft(SubjectB, 1, 1_700_000_000_000, CovenantEgressDestination.Network),
            1,
            Token);

        await PreFoldDisclosureHistory.InsertAsync(
            fixture.Connection,
            Draft(
                SubjectB,
                2,
                1_700_000_000_000,
                CovenantEgressDestination.Process,
                CovenantDisclosureRevocability.LocallyRevocable),
            2,
            Token,
            CovenantDisclosureEffectCategory.McpToolUse);

        CovenantDisclosureExposureReader exposure = new();

        await using SqliteTransaction snapshot = fixture.Connection.BeginTransaction(deferred: true);

        IReadOnlyList<CovenantDisclosureState> buckets = await ExternalDisclosureStateReader.ReadEffectiveAsync(
            fixture.Connection,
            snapshot,
            Token);

        Assert.Equal(3, buckets.Count);

        CovenantDisclosureState[] nonrevocable =
        [
            .. buckets.Where(static bucket => bucket.Revocability is CovenantDisclosureRevocability.Nonrevocable),
        ];

        CovenantDisclosureExposure expected = new(
            nonrevocable.Sum(static bucket => checked((long)bucket.Count)),
            nonrevocable.Any(static bucket => bucket.CountKind is CovenantDisclosureCountKind.LowerBound)
                ? CovenantDisclosureCountKind.LowerBound
                : CovenantDisclosureCountKind.Exact);

        Assert.Equal(new CovenantDisclosureExposure(4, CovenantDisclosureCountKind.LowerBound), expected);

        Assert.Equal(expected, (await exposure.ReadWithinAsync(fixture.Connection, snapshot, Token)).Value);

        await snapshot.RollbackAsync(Token);

        Assert.Equal(expected, (await exposure.ReadWithinAsync(fixture.Connection, null, Token)).Value);

    }

    private static Task<CovenantCanonicalFixture> CreateAsync() =>
        CovenantCanonicalFixture.CreateAsync(Token, coreObjects: CoreObjects);

    private static async Task BacklogAsync(CovenantCanonicalFixture fixture, Guid subject, int receipts)
    {

        for (int ordinal = 1; ordinal <= receipts; ordinal++)
        {

            await PreFoldDisclosureHistory.InsertAsync(
                fixture.Connection,
                Draft(subject, (byte)ordinal, 1_700_000_000_000 + (ordinal * 60_000L)),
                (ulong)ordinal,
                Token);

        }

    }

    private static CovenantDisclosureDraft Draft(
        Guid subject,
        byte effectSeed,
        long timestamp,
        CovenantEgressDestination destination = CovenantEgressDestination.Provider,
        CovenantDisclosureRevocability revocability = CovenantDisclosureRevocability.Nonrevocable) =>
        new(
            Installation,
            CovenantDisclosureSubjectKind.Turn,
            subject,
            CovenantTask6Fixture.D(effectSeed),
            destination,
            revocability,
            CovenantTask6Fixture.D(80),
            PreFoldDisclosureHistory.Sensitivity.Digest,
            null,
            CovenantTask6Fixture.D(82),
            null,
            timestamp);

    private static Task<long> ScalarAsync(CovenantCanonicalFixture fixture, string sql) =>
        CovenantCapacityFixture.ScalarAsync(fixture, sql, Token);

}
