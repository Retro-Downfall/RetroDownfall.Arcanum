using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Backup;
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
    /// The disclosure histories the exposure count and the effective buckets have to agree on.
    /// </summary>
    public enum DisclosureHistory
    {

        /// <summary>Every receipt folded live: nothing to weaken.</summary>
        FullyFolded,

        /// <summary>A live subject, two pre-fold subjects, and a locally revocable tail receipt.</summary>
        MixedPreFold,

        /// <summary>One receipt folded live, then a pre-fold receipt past the watermark.</summary>
        PartiallyFolded,

        /// <summary>A pre-fold receipt whose instant no supported format parses.</summary>
        UnparseableInstant,

        /// <summary>A receipt numbered past its unfolded subject's last allocated ordinal.</summary>
        StrayPastAllocation,

        /// <summary>
        /// A database restore staging wrote: a pre-fold backlog folded by the staged fold, the
        /// destination's bucket joined in, and a live receipt folded on top after the restore.
        /// </summary>
        RestoreStaged,

    }

    /// <summary>
    /// The exposure count is read without rebuilding receipts, so it has to be pinned to the bucket
    /// read it summarizes: the nonrevocable buckets' total, lower bound when any of them is. Each row
    /// is a fold state in which two independently written statements could disagree, and each also
    /// pins the literal total, so the two cannot drift together.
    /// </summary>
    [Theory]
    [InlineData(DisclosureHistory.FullyFolded, 2, CovenantDisclosureCountKind.Exact)]
    [InlineData(DisclosureHistory.MixedPreFold, 4, CovenantDisclosureCountKind.LowerBound)]
    [InlineData(DisclosureHistory.PartiallyFolded, 2, CovenantDisclosureCountKind.LowerBound)]
    [InlineData(DisclosureHistory.UnparseableInstant, 2, CovenantDisclosureCountKind.LowerBound)]
    [InlineData(DisclosureHistory.StrayPastAllocation, 3, CovenantDisclosureCountKind.LowerBound)]
    [InlineData(DisclosureHistory.RestoreStaged, 4, CovenantDisclosureCountKind.LowerBound)]
    public async Task Exposure_is_the_nonrevocable_sum_of_the_effective_buckets(
        DisclosureHistory history,
        long expectedAttempts,
        CovenantDisclosureCountKind expectedKind)
    {

        await using CovenantCanonicalFixture fixture = await CreateAsync();

        await SeedHistoryAsync(fixture, history);

        CovenantDisclosureExposure expected = new(expectedAttempts, expectedKind);

        CovenantDisclosureExposureReader exposure = new();

        await using (SqliteTransaction snapshot = fixture.Connection.BeginTransaction(deferred: true))
        {

            IReadOnlyList<CovenantDisclosureState> buckets = await ExternalDisclosureStateReader
                .ReadEffectiveAsync(fixture.Connection, snapshot, Token);

            Assert.Equal(expected, NonrevocableSum(buckets));

            Assert.Equal(expected, (await exposure.ReadWithinAsync(fixture.Connection, snapshot, Token)).Value);

            await snapshot.RollbackAsync(Token);

        }

        Assert.Equal(
            expected,
            NonrevocableSum(await ExternalDisclosureStateReader.ReadEffectiveAsync(fixture.Connection, Token)));

        Assert.Equal(expected, (await exposure.ReadWithinAsync(fixture.Connection, null, Token)).Value);

    }

    private static async Task SeedHistoryAsync(CovenantCanonicalFixture fixture, DisclosureHistory history)
    {

        switch (history)
        {

            case DisclosureHistory.FullyFolded:
            {

                await AcknowledgeAsync(fixture, Draft(SubjectC, 1, 1_700_000_000_000));

                await AcknowledgeAsync(fixture, Draft(SubjectC, 2, 1_700_000_060_000));

                await AcknowledgeAsync(
                    fixture,
                    Draft(
                        SubjectC,
                        3,
                        1_700_000_120_000,
                        CovenantEgressDestination.Process,
                        CovenantDisclosureRevocability.LocallyRevocable),
                    CovenantDisclosureEffectCategory.McpToolUse);

                return;

            }

            case DisclosureHistory.MixedPreFold:
            {

                await AcknowledgeAsync(fixture, Draft(SubjectC, 1, 1_700_000_000_000));

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

                return;

            }

            case DisclosureHistory.PartiallyFolded:
            {

                await AcknowledgeAsync(fixture, Draft(SubjectA, 1, 1_700_000_000_000));

                await PreFoldDisclosureHistory.InsertAsync(
                    fixture.Connection,
                    Draft(SubjectA, 2, 1_700_000_060_000),
                    2,
                    Token);

                // The state under test, proven rather than assumed: folded through one, allocated to two.
                Assert.Equal(
                    1,
                    await ScalarAsync(
                        fixture,
                        "SELECT COUNT(*) FROM disclosure_subject_state WHERE LastFoldedOrdinal = 1 AND LastAllocatedOrdinal = 2;"));

                return;

            }

            case DisclosureHistory.UnparseableInstant:
            {

                await AcknowledgeAsync(fixture, Draft(SubjectC, 1, 1_700_000_000_000));

                await PreFoldDisclosureHistory.InsertAsync(
                    fixture.Connection,
                    Draft(SubjectA, 1, 1_700_000_000_000, CovenantEgressDestination.ExternalMcp),
                    1,
                    Token,
                    disclosedAtUtc: "not an instant");

                return;

            }

            case DisclosureHistory.StrayPastAllocation:
            {

                await BacklogAsync(fixture, SubjectA, 2);

                await PreFoldDisclosureHistory.InsertAsync(
                    fixture.Connection,
                    Draft(SubjectA, 7, 1_700_000_420_000),
                    7,
                    Token,
                    allocate: false);

                Assert.Equal(
                    1,
                    await ScalarAsync(
                        fixture,
                        "SELECT COUNT(*) FROM disclosure_subject_state WHERE LastFoldedOrdinal = 0 AND LastAllocatedOrdinal = 2;"));

                return;

            }

            case DisclosureHistory.RestoreStaged:
            {

                await BacklogAsync(fixture, SubjectA, 2);

                // Exactly the staged evidence step's disclosure half: fold every unfolded tail as a lower
                // bound, then join the destination's effective bucket, in one transaction.
                await using (SqliteTransaction staging = fixture.Connection.BeginTransaction(deferred: false))
                {

                    Assert.Equal(
                        1,
                        await ExternalDisclosureStateFold.FoldAllUnfoldedAsync(
                            fixture.Connection,
                            staging,
                            ExternalDisclosureFoldOrigin.RestoreStaging,
                            Token));

                    Result<int> joined = await CovenantDisclosureStateJoiner.JoinIntoStagedAsync(
                        fixture.Connection,
                        staging,
                        [
                            new CovenantDisclosureState(
                                CovenantEgressDestination.Provider,
                                CovenantDisclosureRevocability.Nonrevocable,
                                CovenantDisclosureCountKind.Exact,
                                everOccurred: true,
                                count: 3,
                                maximumTimestamp: 1_700_000_000_000,
                                CovenantDisclosureStateAlgebra.CreateEvidenceBloom(CovenantTask6Fixture.D(90))),
                        ],
                        TimeProvider.System,
                        Token);

                    Assert.True(joined.IsSuccess, joined.IsFailure ? joined.Error.Message : null);

                    await staging.CommitAsync(Token);

                }

                Assert.Equal(
                    0,
                    await ScalarAsync(
                        fixture,
                        "SELECT COUNT(*) FROM disclosure_subject_state WHERE LastFoldedOrdinal < LastAllocatedOrdinal;"));

                await AcknowledgeAsync(fixture, Draft(SubjectC, 1, 1_700_000_180_000));

                return;

            }

            default:
                throw new ArgumentOutOfRangeException(nameof(history), history, null);

        }

    }

    private static async Task AcknowledgeAsync(
        CovenantCanonicalFixture fixture,
        CovenantDisclosureDraft draft,
        CovenantDisclosureEffectCategory category = CovenantDisclosureEffectCategory.ProviderDispatch)
    {

        Result<CovenantDisclosureReceipt> acknowledged = await new CovenantDisclosureTransactionWriter(BootId)
            .AcknowledgeAsync(fixture.Connection, draft, category, PreFoldDisclosureHistory.Sensitivity, Token);

        Assert.True(acknowledged.IsSuccess, acknowledged.Error.Message);

    }

    private static CovenantDisclosureExposure NonrevocableSum(IReadOnlyList<CovenantDisclosureState> buckets)
    {

        CovenantDisclosureState[] nonrevocable =
        [
            .. buckets.Where(static bucket => bucket.Revocability is CovenantDisclosureRevocability.Nonrevocable),
        ];

        return new CovenantDisclosureExposure(
            nonrevocable.Sum(static bucket => checked((long)bucket.Count)),
            nonrevocable.Any(static bucket => bucket.CountKind is CovenantDisclosureCountKind.LowerBound)
                ? CovenantDisclosureCountKind.LowerBound
                : CovenantDisclosureCountKind.Exact);

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
