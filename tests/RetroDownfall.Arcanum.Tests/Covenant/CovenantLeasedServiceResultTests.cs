using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// Issue #89 — how a service hands a live lease to the response that will release it.
/// </summary>
/// <remarks>
/// The transfer is the whole point. A service that returned a plain <c>Result&lt;T&gt;</c> and
/// released its lease on the way out would leave the response serializing protected content with no
/// coverage: a reset draining at that moment would report erasure complete while the same process
/// finished writing the erased bytes down a socket.
///
/// <para>Ownership is taken exactly once. Two takers would mean two disposals of one registration,
/// and a delayed second release can free a slot a later lease now owns. A value that is never taken
/// is a leak, so the tests pin both the single-take rule and the disposal path that covers an
/// abandoned transfer.</para>
/// </remarks>
public sealed class CovenantLeasedServiceResultTests
{

    [Fact]
    public void Take_yields_the_payload_and_the_lease_exactly_once()
    {

        FakeLease lease = new();

        CovenantLeasedServiceResult<int> transfer = CovenantLeasedServiceResult<int>.Create(
            Result<int>.Success(7),
            lease);

        (Result<int> payload, ICovenantOperationLease taken) = transfer.Take();

        Assert.Equal(7, payload.Value);

        Assert.Same(lease, taken);

        Assert.Throws<InvalidOperationException>(() => transfer.Take());

    }

    [Fact]
    public async Task An_untaken_transfer_releases_its_lease_on_disposal()
    {

        FakeLease lease = new();

        CovenantLeasedServiceResult<int> transfer = CovenantLeasedServiceResult<int>.Create(
            Result<int>.Success(1),
            lease);

        await transfer.DisposeAsync();

        Assert.Equal(1, lease.DisposeCount);

    }

    [Fact]
    public async Task Disposing_a_taken_transfer_does_not_release_the_new_owner_lease()
    {

        FakeLease lease = new();

        CovenantLeasedServiceResult<int> transfer = CovenantLeasedServiceResult<int>.Create(
            Result<int>.Success(1),
            lease);

        _ = transfer.Take();

        await transfer.DisposeAsync();

        Assert.Equal(0, lease.DisposeCount);

    }

    [Fact]
    public void A_failure_still_carries_a_lease()
    {

        FakeLease lease = new();

        CovenantLeasedServiceResult<int> transfer = CovenantLeasedServiceResult<int>.Create(
            Result<int>.Failure(new Error(ErrorCodes.Covenant.NotFound, "no such key")),
            lease);

        (Result<int> payload, ICovenantOperationLease taken) = transfer.Take();

        Assert.True(payload.IsFailure);

        // The error path holds coverage too. The refusal is serialized through the same boundary,
        // and a boundary that only held a lease on success would have none while it wrote the body
        // that says the content is gone.
        Assert.Same(lease, taken);

    }

    [Fact]
    public void An_exclusive_transfer_carries_its_one_disposition_and_finalizer()
    {

        FakeExclusiveLease lease = new();

        CovenantExclusiveLeasedServiceResult<int> transfer =
            CovenantExclusiveLeasedServiceResult<int>.Create(
                Result<int>.Success(3),
                lease,
                CovenantExclusiveLeaseDisposition.CommitAndReopen,
                CovenantNoOpPostDispositionFinalizer.Instance);

        (Result<int> payload,
            ICovenantExclusiveOperationLease taken,
            CovenantExclusiveLeaseDisposition disposition,
            ICovenantExclusivePostDispositionFinalizer finalizer) = transfer.Take();

        Assert.Equal(3, payload.Value);

        Assert.Same(lease, taken);

        Assert.Equal(CovenantExclusiveLeaseDisposition.CommitAndReopen, disposition);

        Assert.Same(CovenantNoOpPostDispositionFinalizer.Instance, finalizer);

    }

    [Fact]
    public void An_exclusive_transfer_refuses_an_unlisted_disposition()
    {

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CovenantExclusiveLeasedServiceResult<int>.Create(
                Result<int>.Success(1),
                new FakeExclusiveLease(),
                (CovenantExclusiveLeaseDisposition)99,
                CovenantNoOpPostDispositionFinalizer.Instance));

    }

    [Fact]
    public void A_transfer_requires_a_lease_and_a_payload()
    {

        Assert.Throws<ArgumentNullException>(() =>
            CovenantLeasedServiceResult<int>.Create(Result<int>.Success(1), lease: null!));

        Assert.Throws<ArgumentNullException>(() =>
            CovenantExclusiveLeasedServiceResult<int>.Create(
                Result<int>.Success(1),
                new FakeExclusiveLease(),
                CovenantExclusiveLeaseDisposition.KeepClosed,
                finalizer: null!));

    }

    private class FakeLease : ICovenantOperationLease
    {

        public int DisposeCount { get; private set; }

        public CovenantOperationLeaseSnapshot Snapshot => throw new NotSupportedException();

        public CancellationToken Revocation => CancellationToken.None;

        public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());

        public ValueTask DisposeAsync()
        {

            DisposeCount++;

            return ValueTask.CompletedTask;

        }

    }

    private sealed class FakeExclusiveLease : FakeLease, ICovenantExclusiveOperationLease
    {

        public ValueTask<Result> CompleteAsync(
            CovenantExclusiveLeaseDisposition disposition,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());

    }

}
