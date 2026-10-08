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

        // The heads come back with the ranked page, in its snapshot, so the store is not asked for any
        // of them at all: not once per hit, and not once for the page.
        Assert.Equal(0, store.HeadReads);

        // And what the page carries is exactly the ranked hits, in rank order, not some other set of heads.
        CovenantHeadItem[] hits = [.. Enumerable.Range(0, HitCount).Select(HeadFor)];

        Assert.Equal(hits.Select(static hit => hit.EntryId), page.Value.Items.Select(static item => item.EntryId));

        Assert.Equal(hits.Select(static hit => hit.VersionId), page.Value.Items.Select(static item => item.VersionId));

        Assert.Equal(
            hits.Select(static hit => hit.NormalizedKey),
            page.Value.Items.Select(static item => item.Key));
    }

    /// <summary>What changed between the page that issued a cursor and the page that continued it.</summary>
    public enum Movement
    {
        Nothing,
        DatasetGeneration,
        CanonicalSequence,
        CampaignDeletionSequence,
        EnvelopeKeyVersion,
    }

    [Theory]
    [InlineData(Movement.Nothing, null)]
    [InlineData(Movement.DatasetGeneration, "Covenant.StaleCursor")]
    [InlineData(Movement.CanonicalSequence, "Covenant.StaleCursor")]
    [InlineData(Movement.CampaignDeletionSequence, "Covenant.StaleCursor")]
    [InlineData(Movement.EnvelopeKeyVersion, "Covenant.InvalidCursor")]
    public async Task A_list_cursor_continues_only_over_the_dataset_and_key_version_it_was_issued_under(
        Movement movement,
        string? expectedCode)
    {
        PagedStore store = new();

        RoundTripCodec codec = new();

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        CovenantManagementService management = PagedManagement(store, codec, gate);

        await using CovenantInstallationReadLease lease = await ReadLeaseAsync(gate);

        CovenantListRequest request = new(
            CovenantCursorScopeSelection.Global,
            CampaignId: null,
            Lane: null,
            CovenantLifecycle.Set,
            EffectiveForCampaignId: null,
            Limit: 1,
            Cursor: null);

        Result<CovenantPageDto> first = await management.ListAsync(request, lease, CancellationToken.None);

        Assert.True(first.IsSuccess, first.IsFailure ? first.Error.Message : null);

        string cursor = Assert.IsType<string>(first.Value.NextCursor);

        store.Move(movement);

        codec.Move(movement);

        Result<CovenantPageDto> second = await management.ListAsync(
            request with { Cursor = cursor },
            lease,
            CancellationToken.None);

        AssertContinuation(second.IsSuccess, second.IsFailure ? second.Error.Code : null, expectedCode);
    }

    [Theory]
    [InlineData(Movement.Nothing, null)]
    [InlineData(Movement.DatasetGeneration, "Covenant.StaleCursor")]
    [InlineData(Movement.CanonicalSequence, "Covenant.StaleCursor")]
    [InlineData(Movement.EnvelopeKeyVersion, "Covenant.InvalidCursor")]
    public async Task A_versions_cursor_continues_only_over_the_dataset_and_key_version_it_was_issued_under(
        Movement movement,
        string? expectedCode)
    {
        PagedStore store = new();

        RoundTripCodec codec = new();

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        CovenantManagementService management = PagedManagement(store, codec, gate);

        await using CovenantInstallationReadLease lease = await ReadLeaseAsync(gate);

        CovenantVersionsRequest request = new(HeadFor(0).EntryId, CovenantLane.Confirmed, Limit: 1, Cursor: null);

        Result<CovenantVersionPageDto> first = await management.VersionsAsync(request, lease, CancellationToken.None);

        Assert.True(first.IsSuccess, first.IsFailure ? first.Error.Message : null);

        string cursor = Assert.IsType<string>(first.Value.NextCursor);

        store.Move(movement);

        codec.Move(movement);

        Result<CovenantVersionPageDto> second = await management.VersionsAsync(
            request with { Cursor = cursor },
            lease,
            CancellationToken.None);

        AssertContinuation(second.IsSuccess, second.IsFailure ? second.Error.Code : null, expectedCode);
    }

    private static void AssertContinuation(bool succeeded, string? actualCode, string? expectedCode)
    {
        if (expectedCode is null)
        {
            Assert.True(succeeded, $"A cursor over an unmoved dataset was refused: {actualCode}.");

            return;
        }

        Assert.False(succeeded, "A cursor over a moved dataset was accepted.");

        Assert.Equal(expectedCode, actualCode);
    }

    private static CovenantManagementService PagedManagement(
        PagedStore store,
        RoundTripCodec codec,
        CovenantOperationGate gate) =>
        new(
            store,
            new CovenantLinker(),
            gate,
            new FakeCovenantAvailability(),
            codec,
            new UnreachableCampaignReader());

    private static async Task<CovenantInstallationReadLease> ReadLeaseAsync(CovenantOperationGate gate)
    {
        Result<CovenantInstallationReadLease> read = await gate.AcquireInstallationReadAsync(CancellationToken.None);

        Assert.True(read.IsSuccess, read.IsFailure ? read.Error.Message : null);

        return read.Value;
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

    /// <summary>
    /// A store that answers every list and version page with one head and a continuation, under a
    /// dataset a test can move between two pages.
    /// </summary>
    private sealed class PagedStore : DelegatingCovenantStore
    {
        private Guid _datasetGeneration = CovenantOperationGateFixture.DatasetGeneration;

        private long _canonicalSearchSequence = 12;

        private long _coreCampaignDeletionSequence = 3;

        public PagedStore()
            : base(null!)
        {
        }

        public void Move(Movement movement)
        {
            switch (movement)
            {
                case Movement.DatasetGeneration:
                    _datasetGeneration = new Guid("22222222-2222-4222-8222-222222222222");

                    break;

                case Movement.CanonicalSequence:
                    _canonicalSearchSequence++;

                    break;

                case Movement.CampaignDeletionSequence:
                    _coreCampaignDeletionSequence++;

                    break;
            }
        }

        public override ValueTask<Result<CovenantListPage>> ReadListPageAsync(
            CovenantListQuery query,
            ICovenantSnapshotReadLease readLease,
            CancellationToken cancellationToken)
        {
            CovenantHeadItem head = HeadFor(0);

            return ValueTask.FromResult(Result<CovenantListPage>.Success(new CovenantListPage(
                [head],
                new CovenantListKeyset(
                    (byte)CovenantScope.Global,
                    Guid.Empty,
                    head.NormalizedKey,
                    head.EntryId,
                    (byte)CovenantLane.Confirmed),
                _datasetGeneration,
                _canonicalSearchSequence,
                _coreCampaignDeletionSequence,
                Truncated: false)));
        }

        public override ValueTask<Result<CovenantVersionPage>> ReadVersionPageAsync(
            CovenantVersionQuery query,
            ICovenantSnapshotReadLease readLease,
            CancellationToken cancellationToken)
        {
            CovenantHeadItem head = HeadFor(0);

            return ValueTask.FromResult(Result<CovenantVersionPage>.Success(new CovenantVersionPage(
                [
                    new CovenantVersionItem(
                        head.VersionId,
                        head.EntryId,
                        head.Lane,
                        head.LaneRevision,
                        CovenantOperation.Set,
                        head.Origin,
                        head.AuthoredHash,
                        head.RenderedHash,
                        head.CompiledByteCost,
                        CompilerPolicyVersion: 1,
                        RendererPolicyVersion: 1,
                        PredecessorVersionId: null,
                        Guid.Empty,
                        head.ProvenanceCount,
                        head.ProvenanceDigest,
                        head.CreatedAtUtc),
                ],
                new CovenantVersionKeyset(head.LaneRevision, head.VersionId),
                _datasetGeneration,
                _canonicalSearchSequence,
                Truncated: false)));
        }
    }

    /// <summary>
    /// A codec whose token is its payload, so a cursor survives a round trip untouched and a test can
    /// move the key version the service reads between issuing a cursor and continuing it.
    /// </summary>
    private sealed class RoundTripCodec : ICovenantEnvelopeCodec
    {
        private uint _masterKeyVersion = 1;

        public CovenantEnvelopeKeySnapshot KeySnapshot =>
            new(_masterKeyVersion, CanonicalEnvelopeEpoch: 1, RecoveryEnvelopeEpoch: 1, "installation", DatasetGeneration: null);

        public void Move(Movement movement)
        {
            if (movement is Movement.EnvelopeKeyVersion)
            {
                _masterKeyVersion++;
            }
        }

        public Result<string> Encode(
            CovenantEnvelopePurpose purpose,
            ReadOnlySpan<byte> payload,
            TimeSpan lifetime,
            DateTimeOffset? issuedAtUtc = null) =>
            Result<string>.Success(Convert.ToBase64String(payload));

        public Result<CovenantEnvelopeBody> Decode(CovenantEnvelopePurpose expectedPurpose, string? token) =>
            Result<CovenantEnvelopeBody>.Success(new CovenantEnvelopeBody(
                expectedPurpose,
                _masterKeyVersion,
                EnvelopeEpoch: 1,
                Counter: 1,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddMinutes(15),
                Convert.FromBase64String(token!)));
    }

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
