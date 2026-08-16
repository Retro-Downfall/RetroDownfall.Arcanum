using System.Text;
using RetroDownfall.Arcanum.Tests.Support;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

/// <summary>
/// Publishing a committed transition: fresh keys and a fresh snapshot, or nothing at all.
/// </summary>
public sealed class CovenantAuthorityTransitionPublisherTests
{

    private static readonly Guid Installation = Guid.Parse("2C4A5E3B-9F17-4D0C-8A6E-1B3D5F70921A");

    private static readonly Guid Dataset = Guid.Parse("0D1E2F30-4152-4637-8899-AABBCCDDEEFF");

    private static readonly Guid NextDataset = Guid.Parse("11223344-5566-4778-899A-BBCCDDEEFF00");

    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_committed_reset_switches_dataset_keys_and_rejects_every_old_token()
    {

        using Harness harness = Harness.Create();

        string cursor = harness.Codec.Encode(
            CovenantEnvelopePurpose.Cursor,
            [1],
            TimeSpan.FromMinutes(30)).Value;

        OperatorAuthorityContext before = harness.Issuer.Issue(CovenantAuthorityRequirement.CovenantManage).Value;

        Assert.True(harness.Codec.Decode(CovenantEnvelopePurpose.Cursor, cursor).IsSuccess);
        Assert.True(harness.Issuer.Revalidate(before).IsSuccess);

        Result published = await harness.Publisher.PublishCommittedAsync(
            Transition(authorityEpoch: 12, canonicalEnvelopeEpoch: 4, dataset: NextDataset),
            harness.Lease,
            CancellationToken.None);

        Assert.True(published.IsSuccess);

        Assert.False(harness.Codec.Decode(CovenantEnvelopePurpose.Cursor, cursor).IsSuccess);

        Result revalidated = harness.Issuer.Revalidate(before);

        Assert.False(revalidated.IsSuccess);
        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, revalidated.Error.Code);

        // The new generation is fully usable, which is what "publish before reopening admission" buys.
        Assert.True(harness.Codec.Encode(CovenantEnvelopePurpose.Cursor, [1], TimeSpan.FromMinutes(1)).IsSuccess);
        Assert.True(harness.Issuer.Issue(CovenantAuthorityRequirement.CovenantManage).IsSuccess);

    }

    [Fact]
    public async Task A_revoked_lease_publishes_nothing()
    {

        using Harness harness = Harness.Create();

        harness.Lease.Live = false;

        string cursor = harness.Codec.Encode(
            CovenantEnvelopePurpose.Cursor,
            [1],
            TimeSpan.FromMinutes(30)).Value;

        Result published = await harness.Publisher.PublishCommittedAsync(
            Transition(authorityEpoch: 12, canonicalEnvelopeEpoch: 4, dataset: NextDataset),
            harness.Lease,
            CancellationToken.None);

        Assert.False(published.IsSuccess);

        // Old keys and the old snapshot survive intact, so the caller's gate stays the only thing
        // keeping work out rather than the process being left with no keys at all.
        Assert.True(harness.Codec.Decode(CovenantEnvelopePurpose.Cursor, cursor).IsSuccess);
        Assert.Equal(11, harness.Authority.Current!.AuthorityEpoch);

    }

    [Fact]
    public async Task A_transition_cannot_move_an_authority_counter_backwards()
    {

        using Harness harness = Harness.Create();

        foreach (CovenantCommittedAuthorityTransition regression in new[]
        {
            Transition(authorityEpoch: 10),
            Transition(masterKeyVersion: 3),
            Transition(recoveryEnvelopeEpoch: 1),
        })
        {

            Result published = await harness.Publisher.PublishCommittedAsync(
                regression,
                harness.Lease,
                CancellationToken.None);

            Assert.False(published.IsSuccess);
            Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, published.Error.Code);

        }

        Assert.Equal(11, harness.Authority.Current!.AuthorityEpoch);

    }

    [Fact]
    public async Task A_transition_cannot_change_the_installation_identity()
    {

        using Harness harness = Harness.Create();

        Result published = await harness.Publisher.PublishCommittedAsync(
            new CovenantCommittedAuthorityTransition(
                Guid.NewGuid().ToString().ToUpperInvariant(),
                authorityEpoch: 12,
                masterKeyVersion: 4,
                canonicalEnvelopeEpoch: 4,
                recoveryEnvelopeEpoch: 2,
                capabilityGeneration: 1,
                datasetGeneration: NextDataset,
                covenantEnabled: true),
            harness.Lease,
            CancellationToken.None);

        Assert.False(published.IsSuccess);
        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, published.Error.Code);

    }

    [Fact]
    public void A_committed_transition_validates_its_own_shape()
    {

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Transition(authorityEpoch: 0));

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => Transition(masterKeyVersion: 0));

        _ = Assert.Throws<ArgumentException>(() => Transition(dataset: Guid.Empty));

    }

    private static CovenantCommittedAuthorityTransition Transition(
        long authorityEpoch = 11,
        uint masterKeyVersion = 4,
        long canonicalEnvelopeEpoch = 3,
        long recoveryEnvelopeEpoch = 2,
        Guid? dataset = null) =>
        new(
            Installation.ToString().ToUpperInvariant(),
            authorityEpoch,
            masterKeyVersion,
            canonicalEnvelopeEpoch,
            recoveryEnvelopeEpoch,
            capabilityGeneration: 1,
            datasetGeneration: dataset ?? Dataset,
            covenantEnabled: true);

    private sealed class Harness : IDisposable
    {

        private Harness(
            CovenantEnvelopeMasterKeyProvider keys,
            CovenantAuthoritySnapshotProvider authority,
            CovenantEnvelopeCodec codec,
            OperatorAuthorityContextIssuer issuer,
            CovenantAuthorityTransitionPublisher publisher,
            StubExclusiveLease lease)
        {

            Keys = keys;

            Authority = authority;

            Codec = codec;

            Issuer = issuer;

            Publisher = publisher;

            Lease = lease;

        }

        public CovenantEnvelopeMasterKeyProvider Keys { get; }

        public CovenantAuthoritySnapshotProvider Authority { get; }

        public CovenantEnvelopeCodec Codec { get; }

        public OperatorAuthorityContextIssuer Issuer { get; }

        public CovenantAuthorityTransitionPublisher Publisher { get; }

        public StubExclusiveLease Lease { get; }

        public static Harness Create()
        {

            CovenantEnvelopeMasterKeyProvider keys = new();

            _ = keys.Initialize(Encoding.UTF8.GetBytes("master-key-material"), Transition());

            CovenantAuthoritySnapshotProvider authority = new();

            authority.Publish(
                new CovenantAuthoritySnapshot(
                    Installation.ToString().ToUpperInvariant(),
                    AuthorityEpoch: 11,
                    MasterKeyVersion: 4,
                    RecoveryEnvelopeEpoch: 2,
                    CovenantHostToolsState.Clean,
                    null));

            return new Harness(
                keys,
                authority,
                new CovenantEnvelopeCodec(keys, FakeClock(Now)),
                new OperatorAuthorityContextIssuer(authority),
                new CovenantAuthorityTransitionPublisher(keys, authority),
                new StubExclusiveLease());

        }

        public void Dispose() => Keys.Dispose();

    }

    private sealed class StubExclusiveLease : ICovenantExclusiveOperationLease
    {

        public bool Live { get; set; } = true;

        public CovenantOperationLeaseSnapshot Snapshot { get; } = new(
            RegistrationId: Guid.Parse("5F6E7D8C-9B0A-4132-8455-667788990011"),
            Kind: CovenantLeaseKind.Exclusive,
            Coverage: CovenantLeaseCoverage.Installation,
            Scope: null,
            DatasetGeneration: Dataset,
            CapabilityGeneration: 1,
            AuthorityEpoch: 11,
            CanonicalSequence: 0,
            CampaignAvailabilityGeneration: null,
            CampaignPathRevision: null,
            AcceleratorEpoch: null,
            AppliedCampaignDeletionSequence: null,
            RecoveryOwner: null,
            CleanupOnlyHistoricalCampaign: false);

        public CancellationToken Revocation => CancellationToken.None;

        public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                Live
                    ? Result.Success()
                    : Result.Failure(new Error(ErrorCodes.Covenant.StaleSnapshot, "This lease was revoked.")));

        public ValueTask<Result> CompleteAsync(
            CovenantExclusiveLeaseDisposition disposition,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    }


    /// <summary>A fixed clock, so envelope timestamps and expiry are exact rather than approximate.</summary>
    private static FakeTimeProvider FakeClock(DateTimeOffset now)
    {

        FakeTimeProvider provider = new();

        provider.SetUtcNow(now);

        return provider;

    }

}
