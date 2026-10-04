using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// What the operator inspection service asks of its collaborators, counted rather than assumed.
/// </summary>
public sealed class CovenantManagementServiceTests
{
    private const int HitCount = 5;

    [Fact]
    public async Task Query_reads_heads_in_one_store_call()
    {
        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        CountingStore store = new();

        CovenantManagementService management = new(
            store,
            new CovenantLinker(),
            gate,
            new FakeCovenantAvailability(),
            new UnreachableCodec(),
            new UnreachableCampaignReader(),
            new FixedSearchIndex(),
            new CovenantSearchQueryCompiler());

        Result<CovenantInstallationReadLease> read = await gate.AcquireInstallationReadAsync(CancellationToken.None);

        Assert.True(read.IsSuccess, read.IsFailure ? read.Error.Message : null);

        await using CovenantInstallationReadLease lease = read.Value;

        Result<CovenantPageDto> page = await management.QueryAsync(
            new CovenantQueryRequest(
                CovenantCursorScopeSelection.Global,
                CampaignId: null,
                "preference",
                Lane: null,
                CovenantLifecycle.Set,
                EffectiveForCampaignId: null,
                Limit: HitCount,
                Cursor: null),
            lease,
            CancellationToken.None);

        Assert.True(page.IsSuccess, page.IsFailure ? $"{page.Error.Code}: {page.Error.Message}" : null);

        Assert.Equal(HitCount, page.Value.Items.Length);

        // The heads come back with the ranked page, in its snapshot: at most one store read serves the
        // whole page, never one per hit.
        Assert.True(store.HeadReads <= 1, $"A {HitCount}-hit page read heads {store.HeadReads} times.");
    }

    internal static CovenantHeadItem HeadFor(int index) =>
        new(
            new Guid(index + 1, 0, 0, [1, 1, 1, 1, 1, 1, 1, 1]),
            new Guid(index + 1, 1, 0, [2, 2, 2, 2, 2, 2, 2, 2]),
            CovenantScope.Global,
            CampaignId: null,
            $"preference.{index}",
            $"preference.{index}",
            CovenantLane.Confirmed,
            LaneRevision: 1,
            CovenantLifecycle.Set,
            CovenantOrigin.Operator,
            AuthoredHash: null,
            RenderedHash: null,
            CompiledByteCost: 10,
            ProvenanceCount: 0,
            CovenantOperationGateFixture.Digest(1),
            SearchRowId: index + 1,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

    private sealed class CountingStore : DelegatingCovenantStore
    {
        public CountingStore()
            : base(null!)
        {
        }

        public int HeadReads { get; private set; }

        public override ValueTask<Result<CovenantDetail>> ReadDetailAsync(
            CovenantDetailQuery query,
            ICovenantSnapshotReadLease readLease,
            CancellationToken cancellationToken)
        {
            HeadReads++;

            CovenantHeadItem head = Enumerable.Range(0, HitCount)
                .Select(HeadFor)
                .Single(candidate => candidate.NormalizedKey == query.NormalizedKey);

            return ValueTask.FromResult(Result<CovenantDetail>.Success(new CovenantDetail(
                query.Scope,
                query.NormalizedKey,
                head.EntryId,
                head,
                ProposedHead: null,
                KeyEpoch: 1,
                CovenantOperationGateFixture.DatasetGeneration,
                CanonicalSearchSequence: 12,
                CovenantCurationStateDto.None,
                CovenantCurationStateDto.None)));
        }
    }

    private sealed class FixedSearchIndex : ICovenantSearchIndex
    {
        public ValueTask<Result<CovenantSearchPage>> SearchAsync(
            CovenantSearchQuery query,
            ICovenantSnapshotReadLease readLease,
            CancellationToken cancellationToken)
        {
            ImmutableArray<CovenantSearchHit> hits =
            [
                .. Enumerable.Range(0, HitCount).Select(static index =>
                    new CovenantSearchHit(HeadFor(index), CovenantSearchMatchClass.Ranked, Score: 1d)),
            ];

            return ValueTask.FromResult(Result<CovenantSearchPage>.Success(new CovenantSearchPage(
                hits,
                NextKeyset: null,
                new CovenantSearchSourceSnapshot(
                    CovenantOperationGateFixture.DatasetGeneration,
                    CanonicalSearchSequence: 12,
                    CoreCampaignDeletionSequence: 3,
                    CovenantOperationGateFixture.DatasetGeneration,
                    AppliedSearchSequence: 12,
                    AppliedCampaignDeletionSequence: 3,
                    AcceleratorEpoch: 4),
                CovenantSearchExecutionMode.Fts,
                Truncated: false,
                CovenantSearchRebuildGuidance.None)));
        }
    }

    private sealed class UnreachableCodec : ICovenantEnvelopeCodec
    {
        public CovenantEnvelopeKeySnapshot KeySnapshot =>
            throw new NotSupportedException("A first page issues no cursor.");

        public Result<string> Encode(
            CovenantEnvelopePurpose purpose,
            ReadOnlySpan<byte> payload,
            TimeSpan lifetime,
            DateTimeOffset? issuedAtUtc = null) =>
            throw new NotSupportedException("A last page issues no cursor.");

        public Result<CovenantEnvelopeBody> Decode(CovenantEnvelopePurpose expectedPurpose, string? token) =>
            throw new NotSupportedException("A first page accepts no cursor.");
    }

    private sealed class UnreachableCampaignReader : ICampaignAvailabilityReader
    {
        public ValueTask<Result<long?>> FindAvailabilityGenerationAsync(
            Guid campaignId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("A query resolves no Campaign.");
    }
}
