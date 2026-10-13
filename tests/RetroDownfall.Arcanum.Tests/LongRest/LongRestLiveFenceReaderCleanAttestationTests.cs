using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Tests.Support;

using Attestation = RetroDownfall.Arcanum.Tests.Support.RecordingGrimoireWorkAdmissionGate.DrainedAttestation;

namespace RetroDownfall.Arcanum.Tests.LongRest;

/// <summary>
/// Clean adoption is Long Rest policy: the fence reader requires a Long Rest work kind before it consults the
/// gate, and the generic gate predicate carries no kind clause at all.
/// </summary>
public sealed class LongRestLiveFenceReaderCleanAttestationTests
{
    private static readonly TimeSpan DrainCheckpoint = TimeSpan.FromMilliseconds(100);

    public static TheoryData<object> NonLongRestKinds
    {
        get
        {
            TheoryData<object> kinds = new();

            foreach (GrimoireWorkKind kind in Enum.GetValues<GrimoireWorkKind>())
            {
                if (kind is not (GrimoireWorkKind.LongRestSweep or GrimoireWorkKind.LongRunningOperationRecovery)) kinds.Add(kind);
            }

            return kinds;
        }
    }

    [Theory]
    [MemberData(nameof(NonLongRestKinds))]
    public async Task A_lease_of_another_work_kind_is_refused_before_the_gate_is_consulted(object value)
    {
        GrimoireWorkKind kind = Assert.IsType<GrimoireWorkKind>(value);

        (RecordingGrimoireWorkAdmissionGate gate, LongRestLiveFenceReader reader) = await DrainedAsync();

        await using IGrimoireWorkLease lease = Acquire(gate, kind);

        Assert.False(reader.AttestCleanAdmission(lease, 1, 2));

        Assert.Empty(gate.DrainedAttestations);

        // The refusal is the reader's: the gate itself attests the same live lease of any kind.
        Assert.True(gate.AttestDrainedMaintenance(lease, 1, 2));

        Assert.Single(gate.DrainedAttestations);
    }

    [Fact]
    public async Task A_Long_Rest_sweep_lease_is_forwarded_to_the_gate_with_the_presented_interval()
    {
        (RecordingGrimoireWorkAdmissionGate gate, LongRestLiveFenceReader reader) = await DrainedAsync();

        await using IGrimoireWorkLease lease = Acquire(gate, GrimoireWorkKind.LongRestSweep);

        Assert.True(reader.AttestCleanAdmission(lease, 1, 2));

        Attestation forwarded = Assert.Single(gate.DrainedAttestations);

        Assert.Equal(new Attestation(GrimoireWorkKind.LongRestSweep, 1, 2, true), forwarded);

        // The gate still decides: a stale interval is refused after the kind check passed.
        Assert.False(reader.AttestCleanAdmission(lease, 0, 2));

        Assert.False(reader.AttestCleanAdmission(lease, 1, 3));

        Assert.Equal(3, gate.DrainedAttestations.Count);
    }

    [Fact]
    public async Task The_recovery_kind_is_forwarded_until_recovery_stops_running_the_sweep()
    {
        // Transitional: the startup recovery path still runs a clean sweep under its kind-14 recovery lease.
        // Park-and-Readmit Recovery (WS3) removes that path, drops this arm of the reader's kind check and
        // turns this test into a refusal-before-the-gate case.
        (RecordingGrimoireWorkAdmissionGate gate, LongRestLiveFenceReader reader) = await DrainedAsync();

        await using IGrimoireWorkLease lease = Acquire(gate, GrimoireWorkKind.LongRunningOperationRecovery);

        Assert.True(reader.AttestCleanAdmission(lease, 1, 2));

        Assert.Equal(new Attestation(GrimoireWorkKind.LongRunningOperationRecovery, 1, 2, true), Assert.Single(gate.DrainedAttestations));
    }

    [Fact]
    public async Task A_lease_minted_by_a_different_recording_gate_is_refused_by_the_forwarding_gate()
    {
        (RecordingGrimoireWorkAdmissionGate gate, LongRestLiveFenceReader reader) = await DrainedAsync();

        (RecordingGrimoireWorkAdmissionGate foreign, _) = await DrainedAsync();

        await using IGrimoireWorkLease other = Acquire(foreign, GrimoireWorkKind.LongRestSweep);

        Assert.False(reader.AttestCleanAdmission(other, 1, 2));

        Assert.False(Assert.Single(gate.DrainedAttestations).Attested);
    }

    private static async Task<(RecordingGrimoireWorkAdmissionGate Gate, LongRestLiveFenceReader Reader)> DrainedAsync()
    {
        GrimoireConnectionAdmissionGate real = new(TimeProvider.System, DrainCheckpoint);

        await GrimoireGateTestEdges.DrainedAbortAsync(real);

        RecordingGrimoireWorkAdmissionGate gate = new(real);

        // AttestCleanAdmission reads only the gate; no database, runtime or configuration is touched.
        LongRestLiveFenceReader reader = new(null!, null!, gate, null!, null!, null!);

        return (gate, reader);
    }

    private static IGrimoireWorkLease Acquire(IGrimoireConnectionAdmissionGate gate, GrimoireWorkKind kind)
    {
        Assert.True(gate.TryAcquireWorkLease(kind, out IGrimoireWorkLease? lease));

        return lease!;
    }
}
