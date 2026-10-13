using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

/// <summary>
/// The gate attests a presented live lease and a previous admission interval under its own lock, at every use.
/// No proof object exists; stored coordinates alone, a foreign gate's lease or a wrapper can never mint authority.
/// </summary>
public sealed class GrimoireConnectionAdmissionGateDrainedAttestationTests
{
    // Stage one of an exclusive transition waits this long for live leases before it times out.
    private static readonly TimeSpan DrainCheckpoint = TimeSpan.FromMilliseconds(100);

    public static TheoryData<object> AllWorkKinds
    {
        get
        {
            TheoryData<object> kinds = new();

            foreach (GrimoireWorkKind kind in Enum.GetValues<GrimoireWorkKind>()) kinds.Add(kind);

            return kinds;
        }
    }

    [Fact]
    public async Task Own_live_lease_after_a_drained_abort_attests_the_contiguous_interval()
    {
        GrimoireConnectionAdmissionGate gate = CreateGate();

        await GrimoireGateTestEdges.DrainedAbortAsync(gate);

        await using IGrimoireWorkLease lease = Acquire(gate, GrimoireWorkKind.LongRestSweep);

        Assert.Equal(2, lease.Generation);

        Assert.True(gate.AttestDrainedMaintenance(lease, 1, 2));

        // Attestation is a read: it can be repeated at every use and never consumes anything.
        Assert.True(gate.AttestDrainedMaintenance(lease, 1, 2));
    }

    [Fact]
    public async Task Several_contiguous_drained_aborts_attest_from_the_original_previous_generation()
    {
        GrimoireConnectionAdmissionGate gate = CreateGate();

        await GrimoireGateTestEdges.DrainedAbortAsync(gate, seed: 1);

        await GrimoireGateTestEdges.DrainedAbortAsync(gate, seed: 2);

        await using IGrimoireWorkLease lease = Acquire(gate, GrimoireWorkKind.LongRestSweep);

        Assert.Equal(3, lease.Generation);

        Assert.True(gate.AttestDrainedMaintenance(lease, 1, 3));

        Assert.True(gate.AttestDrainedMaintenance(lease, 2, 3));
    }

    [Theory]
    [MemberData(nameof(AllWorkKinds))]
    public async Task The_gate_predicate_carries_no_work_kind_clause(object value)
    {
        GrimoireWorkKind kind = Assert.IsType<GrimoireWorkKind>(value);

        // Long Rest policy about which kinds may adopt lives in LongRestLiveFenceReader, never in the gate.
        GrimoireConnectionAdmissionGate gate = CreateGate();

        await GrimoireGateTestEdges.DrainedAbortAsync(gate);

        await using IGrimoireWorkLease lease = Acquire(gate, kind);

        Assert.True(gate.AttestDrainedMaintenance(lease, 1, 2));
    }

    [Fact]
    public async Task A_foreign_real_gate_with_equal_counters_cannot_attest_through_this_gate()
    {
        GrimoireConnectionAdmissionGate gate = CreateGate();

        GrimoireConnectionAdmissionGate foreign = CreateGate();

        await GrimoireGateTestEdges.DrainedAbortAsync(gate);

        await GrimoireGateTestEdges.DrainedAbortAsync(foreign);

        Assert.Equal(gate.CurrentGeneration, foreign.CurrentGeneration);

        await using IGrimoireWorkLease own = Acquire(gate, GrimoireWorkKind.LongRestSweep);

        await using IGrimoireWorkLease other = Acquire(foreign, GrimoireWorkKind.LongRestSweep);

        Assert.Equal(own.Generation, other.Generation);

        Assert.True(gate.AttestDrainedMaintenance(own, 1, 2));

        Assert.True(foreign.AttestDrainedMaintenance(other, 1, 2));

        Assert.False(gate.AttestDrainedMaintenance(other, 1, 2));

        Assert.False(foreign.AttestDrainedMaintenance(own, 1, 2));
    }

    [Fact]
    public async Task A_wrapper_lease_is_refused_even_when_it_forwards_to_a_live_own_lease()
    {
        GrimoireConnectionAdmissionGate gate = CreateGate();

        await GrimoireGateTestEdges.DrainedAbortAsync(gate);

        await using IGrimoireWorkLease inner = Acquire(gate, GrimoireWorkKind.LongRestSweep);

        WrapperLease wrapper = new(inner);

        Assert.Equal(inner.Generation, wrapper.Generation);

        Assert.True(gate.AttestDrainedMaintenance(inner, 1, 2));

        Assert.False(gate.AttestDrainedMaintenance(wrapper, 1, 2));
    }

    [Fact]
    public async Task A_null_lease_is_refused()
    {
        GrimoireConnectionAdmissionGate gate = CreateGate();

        await GrimoireGateTestEdges.DrainedAbortAsync(gate);

        Assert.False(gate.AttestDrainedMaintenance(null!, 1, 2));
    }

    [Fact]
    public async Task A_disposed_lease_is_refused()
    {
        GrimoireConnectionAdmissionGate gate = CreateGate();

        await GrimoireGateTestEdges.DrainedAbortAsync(gate);

        IGrimoireWorkLease lease = Acquire(gate, GrimoireWorkKind.LongRestSweep);

        Assert.True(gate.AttestDrainedMaintenance(lease, 1, 2));

        await lease.DisposeAsync();

        Assert.False(gate.AttestDrainedMaintenance(lease, 1, 2));

        await lease.DisposeAsync();

        Assert.False(gate.AttestDrainedMaintenance(lease, 1, 2));
    }

    [Fact]
    public async Task A_lease_whose_scope_is_disposed_while_an_effect_group_is_active_is_refused()
    {
        GrimoireConnectionAdmissionGate gate = CreateGate();

        await GrimoireGateTestEdges.DrainedAbortAsync(gate);

        IGrimoireWorkLease lease = Acquire(gate, GrimoireWorkKind.LongRestSweep);

        Assert.True(lease.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? group));

        Assert.True(gate.AttestDrainedMaintenance(lease, 1, 2));

        // The lease stays registered with the gate until the group finishes, but its scope is gone.
        await lease.DisposeAsync();

        Assert.False(gate.AttestDrainedMaintenance(lease, 1, 2));

        await group!.DisposeAsync();

        Assert.False(gate.AttestDrainedMaintenance(lease, 1, 2));
    }

    [Fact]
    public async Task A_pending_maintenance_revocation_refuses_while_closing_and_after_the_proven_abort()
    {
        // RevocationPending is only ever raised while the gate is Closing, and every abort advances the
        // generation. A pending lease therefore never matches the gate generation while Ordinary; the
        // term is still evaluated, and this pins that the obligation refuses at every reachable point.
        GrimoireConnectionAdmissionGate gate = CreateGate();

        await GrimoireGateTestEdges.DrainedAbortAsync(gate);

        await using IGrimoireWorkLease lease = Acquire(gate, GrimoireWorkKind.LongRestSweep);

        Assert.True(lease.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? group));

        await using IGrimoireExternalEffectGroup _ = group!;

        Assert.True(gate.AttestDrainedMaintenance(lease, 1, 2));

        Result<IGrimoireClosingOwner> begun = gate.BeginOrResumeExclusive(GrimoireGateTestEdges.Owner(3));

        Assert.True(begun.IsSuccess);

        await using IGrimoireClosingOwner closing = begun.Value;

        Assert.True(lease.MaintenanceRevocationPending);

        // An active effect group defers the token: only the obligation is visible.
        Assert.False(lease.MaintenanceRevocation.IsCancellationRequested);

        Assert.False(gate.AttestDrainedMaintenance(lease, 1, 2));

        Result drain = await gate.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(GrimoireGateTestEdges.WorkDrainTimeoutCode, drain.Error.Code);

        Assert.True((await gate.AbortClosingAsync(closing, static _ => ValueTask.FromResult(true), CancellationToken.None)).IsSuccess);

        Assert.Equal(3, gate.CurrentGeneration);

        Assert.True(lease.MaintenanceRevocationPending);

        Assert.False(lease.MaintenanceRevocation.IsCancellationRequested);

        Assert.False(gate.AttestDrainedMaintenance(lease, 1, 2));

        Assert.False(gate.AttestDrainedMaintenance(lease, 2, 3));

        Assert.False(gate.AttestDrainedMaintenance(lease, 1, 3));
    }

    [Fact]
    public async Task A_cancelled_revocation_token_refuses_and_a_fresh_lease_attests_after_the_drained_abort()
    {
        GrimoireConnectionAdmissionGate gate = CreateGate();

        await GrimoireGateTestEdges.DrainedAbortAsync(gate);

        IGrimoireWorkLease lease = Acquire(gate, GrimoireWorkKind.LongRestSweep);

        Assert.True(gate.AttestDrainedMaintenance(lease, 1, 2));

        Result<IGrimoireClosingOwner> begun = gate.BeginOrResumeExclusive(GrimoireGateTestEdges.Owner(3));

        Assert.True(begun.IsSuccess);

        await using IGrimoireClosingOwner closing = begun.Value;

        // With no effect group the token is cancelled immediately.
        Assert.True(lease.MaintenanceRevocation.IsCancellationRequested);

        Assert.True(lease.MaintenanceRevocationPending);

        Assert.False(gate.AttestDrainedMaintenance(lease, 1, 2));

        await lease.DisposeAsync();

        Assert.True((await gate.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);

        Assert.True((await gate.AbortClosingAsync(closing, static _ => ValueTask.FromResult(true), CancellationToken.None)).IsSuccess);

        Assert.Equal(3, gate.CurrentGeneration);

        Assert.False(gate.AttestDrainedMaintenance(lease, 1, 2));

        Assert.False(gate.AttestDrainedMaintenance(lease, 2, 3));

        // The abort was fully drained, so the original previous generation still attests for a fresh lease.
        await using IGrimoireWorkLease fresh = Acquire(gate, GrimoireWorkKind.LongRestSweep);

        Assert.True(gate.AttestDrainedMaintenance(fresh, 1, 3));
    }

    [Fact]
    public async Task A_closing_gate_and_a_closed_gate_refuse_and_a_committed_close_raises_the_floor()
    {
        GrimoireConnectionAdmissionGate gate = CreateGate();

        await GrimoireGateTestEdges.DrainedAbortAsync(gate);

        IGrimoireWorkLease lease = Acquire(gate, GrimoireWorkKind.LongRestSweep);

        Assert.True(gate.AttestDrainedMaintenance(lease, 1, 2));

        Result<IGrimoireClosingOwner> begun = gate.BeginOrResumeExclusive(GrimoireGateTestEdges.Owner(3));

        Assert.True(begun.IsSuccess);

        await using IGrimoireClosingOwner closing = begun.Value;

        Assert.False(gate.AttestDrainedMaintenance(lease, 1, 2));

        await lease.DisposeAsync();

        Assert.True((await gate.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);

        Result<IGrimoireExclusiveClosedLease> closed = await gate.CloseConnectionAdmissionAsync(closing, CancellationToken.None);

        Assert.True(closed.IsSuccess, closed.IsFailure ? closed.Error.Message : null);

        await using IGrimoireExclusiveClosedLease closedLease = closed.Value;

        Assert.Equal(3, gate.CurrentGeneration);

        Assert.False(gate.AttestDrainedMaintenance(lease, 1, 2));

        Assert.False(gate.AttestDrainedMaintenance(lease, 2, 3));

        Assert.True((await closedLease.CompleteAsync(CovenantExclusiveLeaseDisposition.CommitAndReopen, CancellationToken.None)).IsSuccess);

        // A committed Closed transition is destructive: no interval that starts before it can ever attest.
        await using (IGrimoireWorkLease reopened = Acquire(gate, GrimoireWorkKind.LongRestSweep))
        {
            Assert.Equal(3, reopened.Generation);

            Assert.False(gate.AttestDrainedMaintenance(reopened, 1, 3));

            Assert.False(gate.AttestDrainedMaintenance(reopened, 2, 3));

            Assert.False(gate.AttestDrainedMaintenance(reopened, 3, 3));
        }

        // Only aborts drained after the commit start a new attestable interval.
        await GrimoireGateTestEdges.DrainedAbortAsync(gate, seed: 4);

        await using IGrimoireWorkLease after = Acquire(gate, GrimoireWorkKind.LongRestSweep);

        Assert.Equal(4, after.Generation);

        Assert.True(gate.AttestDrainedMaintenance(after, 3, 4));

        Assert.False(gate.AttestDrainedMaintenance(after, 2, 4));

        Assert.False(gate.AttestDrainedMaintenance(after, 1, 4));
    }

    [Fact]
    public async Task A_current_generation_that_is_not_the_gate_generation_is_refused()
    {
        GrimoireConnectionAdmissionGate gate = CreateGate();

        await GrimoireGateTestEdges.DrainedAbortAsync(gate);

        await using IGrimoireWorkLease lease = Acquire(gate, GrimoireWorkKind.LongRestSweep);

        Assert.True(gate.AttestDrainedMaintenance(lease, 1, 2));

        Assert.False(gate.AttestDrainedMaintenance(lease, 1, 3));

        Assert.False(gate.AttestDrainedMaintenance(lease, 2, 3));

        Assert.False(gate.AttestDrainedMaintenance(lease, 0, 1));

        Assert.False(gate.AttestDrainedMaintenance(lease, long.MinValue, long.MaxValue));
    }

    [Fact]
    public async Task A_lease_from_an_earlier_generation_is_refused_after_an_undrained_abort()
    {
        GrimoireConnectionAdmissionGate gate = CreateGate();

        IGrimoireWorkLease earlier = Acquire(gate, GrimoireWorkKind.LongRestSweep);

        Assert.Equal(1, earlier.Generation);

        await GrimoireGateTestEdges.UndrainedAbortAsync(gate);

        Assert.Equal(2, gate.CurrentGeneration);

        Assert.False(gate.AttestDrainedMaintenance(earlier, 0, 1));

        Assert.False(gate.AttestDrainedMaintenance(earlier, 1, 2));

        Assert.False(gate.AttestDrainedMaintenance(earlier, 2, 2));

        await earlier.DisposeAsync();
    }

    [Fact]
    public async Task A_previous_generation_below_the_drained_abort_floor_is_refused()
    {
        GrimoireConnectionAdmissionGate gate = CreateGate();

        IGrimoireWorkLease held = Acquire(gate, GrimoireWorkKind.LongRestSweep);

        // The lease is still held, so stage one times out: this abort is not drained and raises the floor.
        await GrimoireGateTestEdges.UndrainedAbortAsync(gate);

        await using (IGrimoireWorkLease belowFloor = Acquire(gate, GrimoireWorkKind.LongRestSweep))
        {
            Assert.Equal(2, belowFloor.Generation);

            Assert.False(gate.AttestDrainedMaintenance(belowFloor, 1, 2));
        }

        await held.DisposeAsync();

        // A later fully drained abort keeps the raised floor: only intervals that start at or above it attest.
        await GrimoireGateTestEdges.DrainedAbortAsync(gate, seed: 3);

        await using IGrimoireWorkLease fresh = Acquire(gate, GrimoireWorkKind.LongRestSweep);

        Assert.Equal(3, fresh.Generation);

        Assert.True(gate.AttestDrainedMaintenance(fresh, 2, 3));

        Assert.False(gate.AttestDrainedMaintenance(fresh, 1, 3));

        Assert.False(gate.AttestDrainedMaintenance(fresh, 0, 3));
    }

    [Theory]
    [InlineData(2L, 2L)]
    [InlineData(3L, 2L)]
    [InlineData(long.MaxValue, 2L)]
    public async Task A_previous_generation_at_or_above_the_current_generation_is_refused(long previous, long current)
    {
        GrimoireConnectionAdmissionGate gate = CreateGate();

        await GrimoireGateTestEdges.DrainedAbortAsync(gate);

        await using IGrimoireWorkLease lease = Acquire(gate, GrimoireWorkKind.LongRestSweep);

        Assert.True(gate.AttestDrainedMaintenance(lease, 1, 2));

        Assert.False(gate.AttestDrainedMaintenance(lease, previous, current));
    }

    private static GrimoireConnectionAdmissionGate CreateGate() => new(TimeProvider.System, DrainCheckpoint);

    private static IGrimoireWorkLease Acquire(IGrimoireConnectionAdmissionGate gate, GrimoireWorkKind kind)
    {
        Assert.True(gate.TryAcquireWorkLease(kind, out IGrimoireWorkLease? lease));

        return lease!;
    }

    private sealed class WrapperLease(IGrimoireWorkLease inner) : IGrimoireWorkLease
    {
        public GrimoireWorkKind Kind => inner.Kind;

        public long Generation => inner.Generation;

        public CancellationToken MaintenanceRevocation => inner.MaintenanceRevocation;

        public bool MaintenanceRevocationPending => inner.MaintenanceRevocationPending;

        public bool TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? effectGroup) =>
            inner.TryBeginExternalEffectGroup(out effectGroup);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
