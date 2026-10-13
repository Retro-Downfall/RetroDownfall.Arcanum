using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Tests.Covenant;

public sealed class CovenantBorrowedTurnLeaseTests
{
    [Fact]
    public async Task Only_the_exact_live_wrapper_issued_by_this_gate_proves_borrowed_authority()
    {
        ICovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantTurnLease real = (await gate.AcquireTurnAsync(CanonicalCampaignContext.GlobalOnly, CancellationToken.None)).Value;

        Assert.True((await gate.ValidateTurnLeaseAsync(real, CancellationToken.None)).IsSuccess);

        await using CovenantTurnLease forged = new(new ForgedRegistration(real.Snapshot));

        Assert.True((await forged.RevalidateAsync(CancellationToken.None)).IsSuccess);

        Assert.True((await gate.ValidateTurnLeaseAsync(forged, CancellationToken.None)).IsFailure);

        ICovenantOperationGate other = CovenantOperationGateFixture.CreateGate();

        Assert.True((await other.ValidateTurnLeaseAsync(real, CancellationToken.None)).IsFailure);

        await real.DisposeAsync();

        Assert.True((await gate.ValidateTurnLeaseAsync(real, CancellationToken.None)).IsFailure);
    }

    [Fact]
    public async Task Generation_revocation_invalidates_the_exact_borrowed_wrapper()
    {
        FakeCovenantAuthorityProvider authority = new();

        ICovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(authority: authority);

        await using CovenantTurnLease real = (await gate.AcquireTurnAsync(CanonicalCampaignContext.GlobalOnly, CancellationToken.None)).Value;

        Assert.True((await gate.ValidateTurnLeaseAsync(real, CancellationToken.None)).IsSuccess);

        authority.Advance();

        Assert.True((await gate.ValidateTurnLeaseAsync(real, CancellationToken.None)).IsFailure);
    }

    private sealed class ForgedRegistration(CovenantOperationLeaseSnapshot snapshot) : ICovenantLeaseRegistration
    {
        public CovenantOperationLeaseSnapshot Snapshot => snapshot;

        public CancellationToken Revocation => CancellationToken.None;

        public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

        public ValueTask ReleaseAsync() => ValueTask.CompletedTask;
    }
}
