using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// A real closed period over a scratch database, so a suite can drive real maintenance opens.
/// </summary>
/// <remarks>
/// Deliberately the production admission gate, the production maintenance factory and the production
/// capability semantics. What a Covenant erasure needs proved is that each open is authorized by a
/// closed generation and spent once on the purpose it was issued for, and only the real gate can say
/// so — a double would hand back whatever it was asked for and the first unauthorized open would
/// reach an operator instead of a test.
///
/// <para>The one substitution is where the paths point, and it is the reason this type exists rather
/// than a literal in the gate. Every path a purpose resolves to comes from an authority the composition
/// root supplies; here that authority names the fixture's scratch file. Without it these tests would
/// open, vacuum, replace and prove-erased the developer's own installation, which is a mistake a suite
/// gets to make exactly once.</para>
/// </remarks>
internal sealed class CovenantClosedPeriodTestAuthority : IAsyncDisposable
{

    private readonly GrimoireConnectionAdmissionGate _gate;

    private readonly IGrimoireClosingOwner _closing;

    private readonly IGrimoireExclusiveClosedLease _closed;

    private readonly IGrimoireMaintenanceIoLane _lane;

    private CovenantClosedPeriodTestAuthority(
        GrimoireConnectionAdmissionGate gate,
        IGrimoireClosingOwner closing,
        IGrimoireExclusiveClosedLease closed,
        IGrimoireMaintenanceIoLane lane,
        CovenantClosedPeriodAuthority authority)
    {

        _gate = gate;

        _closing = closing;

        _closed = closed;

        _lane = lane;

        Authority = authority;

    }

    /// <summary>The authority every kernel under test performs its opens through.</summary>
    internal CovenantClosedPeriodAuthority Authority { get; }

    /// <summary>Closes a fresh gate over the given database and holds the closed period open.</summary>
    internal static async Task<CovenantClosedPeriodTestAuthority> CloseAsync(
        string databasePath,
        string passphrase,
        ICovenantSqliteConnectionInitializer initializer)
    {

        ScratchPaths paths = new(databasePath);

        GrimoireConnectionAdmissionGate gate = new(
            TimeProvider.System,
            new CovenantConnectionDrain(),
            paths);

        IGrimoireClosingOwner closing = gate.BeginOrResumeExclusive(Owner).Value;

        _ = await gate.DrainRequestAndWorkAsync(closing, CancellationToken.None);

        IGrimoireExclusiveClosedLease closed =
            (await gate.CloseConnectionAdmissionAsync(closing, CancellationToken.None)).Value;

        IGrimoireMaintenanceIoLane lane = (await closed.AcquireMaintenanceIoLaneAsync(
            static (_, _, _) => ValueTask.FromResult(true),
            CancellationToken.None)).Value;

        return new CovenantClosedPeriodTestAuthority(
            gate,
            closing,
            closed,
            lane,
            new CovenantClosedPeriodAuthority(
                closed,
                lane,
                new GrimoireMaintenanceConnectionFactory(
                    new FixedPassphrase(passphrase),
                    initializer,
                    SqliteNativeRuntime.Instance)));

    }

    public async ValueTask DisposeAsync()
    {

        await _lane.DisposeAsync();

        _ = await _closed.CompleteAsync(
            CovenantExclusiveLeaseDisposition.CommitAndReopen,
            CancellationToken.None);

        await _closed.DisposeAsync();

        await _closing.DisposeAsync();

        _gate.Dispose();

    }

    private static CovenantExclusiveRecoveryOwner Owner { get; } = new(
        Guid.Parse("77777777-1111-4222-8333-444444444444"),
        CovenantExclusiveOperation.CovenantReset,
        new CovenantDigest([.. Enumerable.Repeat((byte)0x41, CovenantLimits.DigestBytes)]));

    /// <summary>The scratch file every maintenance purpose in this suite resolves to.</summary>
    private sealed class ScratchPaths(string databasePath) : IGrimoireMaintenancePathAuthority
    {

        public string CanonicalDatabasePath { get; } = databasePath;

        public string ExportStagingDatabasePath { get; } =
            CovenantResidualArtifacts.ExportStagingPath(databasePath);

    }

    private sealed class FixedPassphrase(string passphrase) : IGrimoireDbPassphraseSource
    {

        public string Passphrase { get; private set; } = passphrase;

        public void SetPassphrase(string value) => Passphrase = value;

    }

}
