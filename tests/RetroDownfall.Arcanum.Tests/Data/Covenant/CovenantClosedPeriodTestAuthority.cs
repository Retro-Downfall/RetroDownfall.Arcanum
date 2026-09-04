using System.Data.Common;
using System.Runtime.CompilerServices;

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

    private readonly IGrimoireClosingOwner _closing;

    private readonly IGrimoireExclusiveClosedLease _closed;

    private readonly IGrimoireMaintenanceIoLane _lane;

    private CovenantClosedPeriodTestAuthority(
        IGrimoireClosingOwner closing,
        IGrimoireExclusiveClosedLease closed,
        IGrimoireMaintenanceIoLane lane,
        CovenantClosedPeriodAuthority authority,
        IGrimoireMaintenancePathAuthority paths,
        IGrimoireDbPassphraseSource passphrase)
    {

        _closing = closing;

        _closed = closed;

        _lane = lane;

        Authority = authority;

        Paths = paths;

        Passphrase = passphrase;

    }

    /// <summary>The authority every kernel under test performs its opens through.</summary>
    internal CovenantClosedPeriodAuthority Authority { get; }

    /// <summary>
    /// The very paths and key this closed period was built over, for a kernel that needs its own.
    /// </summary>
    /// <remarks>
    /// A storage-health kernel takes a path authority and a passphrase source of its own, on top of
    /// the authority it opens through, because some of what it proves is about the file rather than
    /// about a connection: it measures the database's length, enumerates the directory beside it for
    /// residual sidecars, and replaces one file with another. Exposing the gate's own instances here
    /// rather than letting a suite construct a second pair is the point. Two separately-built
    /// authorities agreeing today is a coincidence a later edit gets to break silently, and the
    /// failure it breaks into is a kernel proving a scratch file erased while the gate authorized
    /// opens against a different one — which is to say, a green suite proving nothing.
    /// </remarks>
    internal IGrimoireMaintenancePathAuthority Paths { get; }

    /// <inheritdoc cref="Paths"/>
    internal IGrimoireDbPassphraseSource Passphrase { get; }

    /// <summary>Closes a fresh gate over the given database and holds the closed period open.</summary>
    /// <remarks>
    /// The drain is a parameter rather than a fresh instance because closing admission is what closes
    /// the handles that are already open, and a gate given an empty drain closes nothing. A suite
    /// holding a seeded connection onto the same file has to enrol it in the drain it passes here, or
    /// the first exclusive maintenance open of the closed period contends with that suite's own
    /// handle — and SQLite reports that as a busy database, which reads like a flaw in the erasure
    /// rather than the fixture forgetting to let go.
    /// </remarks>
    /// <param name="decorate">
    /// Wraps the production maintenance factory before the authority is built over it, for a suite
    /// that has to reach a connection the closed period opens. The wrapper sits outside the real
    /// factory rather than replacing it, so the capability is still consumed, the native runtime
    /// still initialized and the connection still policed by production code before the suite sees
    /// it — a substitution at this seam would leave the test asserting against its own double.
    /// </param>
    internal static async Task<CovenantClosedPeriodTestAuthority> CloseAsync(
        string databasePath,
        string passphrase,
        ICovenantSqliteConnectionInitializer initializer,
        ICovenantConnectionDrain? drain = null,
        Func<IGrimoireMaintenanceConnectionFactory, IGrimoireMaintenanceConnectionFactory>? decorate = null)
    {

        ScratchPaths paths = new(databasePath);

        FixedPassphrase key = new(passphrase);

        GrimoireConnectionAdmissionGate gate = new(
            TimeProvider.System,
            drain ?? new CovenantConnectionDrain(),
            paths);

        IGrimoireClosingOwner closing = gate.BeginOrResumeExclusive(Owner).Value;

        _ = await gate.DrainRequestAndWorkAsync(closing, CancellationToken.None);

        IGrimoireExclusiveClosedLease closed =
            (await gate.CloseConnectionAdmissionAsync(closing, CancellationToken.None)).Value;

        IGrimoireMaintenanceIoLane lane = (await closed.AcquireMaintenanceIoLaneAsync(
            static (_, _, _) => ValueTask.FromResult(true),
            CancellationToken.None)).Value;

        IGrimoireMaintenanceConnectionFactory factory = new GrimoireMaintenanceConnectionFactory(
            key,
            initializer,
            SqliteNativeRuntime.Instance);

        return new CovenantClosedPeriodTestAuthority(
            closing,
            closed,
            lane,
            new CovenantClosedPeriodAuthority(
                closed,
                lane,
                decorate is null ? factory : decorate(factory)),
            paths,
            key);

    }

    public async ValueTask DisposeAsync()
    {

        await _lane.DisposeAsync();

        _ = await _closed.CompleteAsync(
            CovenantExclusiveLeaseDisposition.CommitAndReopen,
            CancellationToken.None);

        await _closed.DisposeAsync();

        await _closing.DisposeAsync();

    }

    private static CovenantExclusiveRecoveryOwner Owner { get; } = new(
        Guid.Parse("77777777-1111-4222-8333-444444444444"),
        CovenantExclusiveOperation.CovenantReset,
        new CovenantDigest([.. Enumerable.Repeat((byte)0x41, CovenantLimits.DigestBytes)]));

    /// <summary>
    /// Attempts the closure and reports the gate's refusal instead of throwing it.
    /// </summary>
    /// <remarks>
    /// <see cref="CloseAsync"/> takes the closed lease's value because every suite that calls it
    /// needs one, and a refusal there is a broken fixture rather than a result. The refusals
    /// themselves are worth asserting though — a drain that cannot close a handle is exactly the case
    /// an erasure must not proceed through — and after the drain moved into the gate's stage-two
    /// close there is no longer any way to observe one by calling a kernel. This is that way.
    /// </remarks>
    internal static async Task<Result> TryCloseAsync(
        string databasePath,
        ICovenantConnectionDrain drain)
    {

        GrimoireConnectionAdmissionGate gate = new(
            TimeProvider.System,
            drain,
            new ScratchPaths(databasePath));

        IGrimoireClosingOwner closing = gate.BeginOrResumeExclusive(Owner).Value;

        await using (closing.ConfigureAwait(false))
        {

            Result drained = await gate.DrainRequestAndWorkAsync(closing, CancellationToken.None);

            if (drained.IsFailure)
            {

                return drained;

            }

            Result<IGrimoireExclusiveClosedLease> closed =
                await gate.CloseConnectionAdmissionAsync(closing, CancellationToken.None);

            if (closed.IsFailure)
            {

                return Result.Failure(closed.Error);

            }

            _ = await closed.Value.CompleteAsync(
                CovenantExclusiveLeaseDisposition.CommitAndReopen,
                CancellationToken.None);

            await closed.Value.DisposeAsync();

            return Result.Success();

        }

    }

    /// <summary>
    /// An authority that refuses every open, for a suite proving a caller only ever forwards one.
    /// </summary>
    /// <remarks>
    /// Some collaborators — the erasure transition and the coordinator among them — never open a
    /// database themselves. They take an authority and hand it onwards, and the whole of what a test
    /// can say about them is which collaborator got it and that it arrived unchanged. Handing those
    /// suites something that throws on contact states that expectation in the strongest available
    /// form: a forwarder that started reaching through its authority would fail here rather than
    /// quietly acquire a second way to touch the database.
    ///
    /// <para>Distinct instances, deliberately, so a test can assert the exact object it passed came
    /// out the far side. A shared singleton would make <c>Assert.Same</c> pass for a collaborator
    /// that dropped its argument and substituted one of its own.</para>
    /// </remarks>
    internal static CovenantClosedPeriodAuthority Inert() =>
        new(new InertClosedLease(), new InertLane(), new UnreachableMaintenanceFactory());

    /// <summary>The scratch file every maintenance purpose in this suite resolves to.</summary>
    private sealed class ScratchPaths(string databasePath) : IGrimoireMaintenancePathAuthority
    {

        public string CanonicalDatabasePath { get; } = databasePath;

        public string ExportStagingDatabasePath { get; } =
            CovenantResidualArtifacts.ExportStagingPath(databasePath);

    }

    private static Exception Unreachable([CallerMemberName] string member = "") =>
        new InvalidOperationException(
            $"An inert closed-period authority was used to {member}, but the caller under test is "
            + "supposed to forward its authority rather than open anything through it.");

    private sealed class InertClosedLease : IGrimoireExclusiveClosedLease
    {

        public CovenantExclusiveRecoveryOwner Owner => throw Unreachable();

        public long Generation => throw Unreachable();

        public Result<IGrimoireScopedConnectionPermit> AcquireScopedConnectionPermit(
            DbConnection connection) =>
            throw Unreachable();

        public Result<IGrimoireMaintenanceRenewalTicket> IssueMaintenanceRenewalTicket(
            IGrimoireMaintenanceIoLane lane) =>
            throw Unreachable();

        public Result<IGrimoireMaintenanceConnectionCapability> IssueMaintenanceConnectionCapability(
            CovenantMaintenanceConnectionPurpose purpose,
            IGrimoireMaintenanceIoLane lane) =>
            throw Unreachable();

        public ValueTask<Result<IGrimoireMaintenanceIoLane>> AcquireMaintenanceIoLaneAsync(
            Func<CovenantExclusiveRecoveryOwner, long, CancellationToken, ValueTask<bool>>
                revalidateDurableOwnerAsync,
            CancellationToken cancellationToken) =>
            throw Unreachable();

        public ValueTask<Result> CompleteAsync(
            CovenantExclusiveLeaseDisposition disposition,
            CancellationToken cancellationToken) =>
            throw Unreachable();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    }

    private sealed class InertLane : IGrimoireMaintenanceIoLane
    {

        public CovenantExclusiveRecoveryOwner Owner => throw Unreachable();

        public long Generation => throw Unreachable();

        public ValueTask<Result> RevalidateDurableOwnerAsync(
            Func<CovenantExclusiveRecoveryOwner, long, CancellationToken, ValueTask<bool>>
                revalidateDurableOwnerAsync,
            CancellationToken cancellationToken) =>
            throw Unreachable();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    }


    private sealed class FixedPassphrase(string passphrase) : IGrimoireDbPassphraseSource
    {

        public string Passphrase { get; private set; } = passphrase;

        public void SetPassphrase(string value) => Passphrase = value;

    }

}

/// <summary>
/// A maintenance factory whose every purpose throws, for a suite that must never open a database.
/// </summary>
/// <remarks>
/// Some collaborators need a factory to exist without ever using one: a coordinator driving doubles
/// still builds a real closed-period authority, and the authority is built over a factory. A stub
/// returning connections would let a phase quietly acquire one and the suite would still pass; this
/// makes the same situation a failure, which is the only version of it worth having.
/// </remarks>
internal sealed class UnreachableMaintenanceFactory : IGrimoireMaintenanceConnectionFactory
{

    private static Exception Unreachable([CallerMemberName] string member = "") =>
        new InvalidOperationException(
            $"An unreachable maintenance factory was asked to {member}, but the caller under test is "
            + "not supposed to open a database at all.");

    public Task<Result<IGrimoireMaintenanceConnectionLease>> OpenJournalCanonicalErasureAsync(
        IGrimoireMaintenanceConnectionCapability capability,
        IGrimoireMaintenanceIoLane lane,
        CancellationToken cancellationToken) =>
        throw Unreachable();

    public Task<Result<IGrimoireMaintenanceConnectionLease>> OpenJournalWalTruncationAsync(
        IGrimoireMaintenanceConnectionCapability capability,
        IGrimoireMaintenanceIoLane lane,
        CancellationToken cancellationToken) =>
        throw Unreachable();

    public Task<Result<IGrimoireMaintenanceConnectionLease>> OpenJournalCompactionAsync(
        IGrimoireMaintenanceConnectionCapability capability,
        IGrimoireMaintenanceIoLane lane,
        CancellationToken cancellationToken) =>
        throw Unreachable();

    public Task<Result<IGrimoireMaintenanceConnectionLease>> OpenJournalExportVerificationAsync(
        IGrimoireMaintenanceConnectionCapability capability,
        IGrimoireMaintenanceIoLane lane,
        CancellationToken cancellationToken) =>
        throw Unreachable();

    public Task<Result<IGrimoireMaintenanceConnectionLease>> OpenJournalAcceleratorInitializationAsync(
        IGrimoireMaintenanceConnectionCapability capability,
        IGrimoireMaintenanceIoLane lane,
        CancellationToken cancellationToken) =>
        throw Unreachable();

    public Task<Result<IGrimoireMaintenanceConnectionLease>> OpenJournalCandidateReopenAsync(
        IGrimoireMaintenanceConnectionCapability capability,
        IGrimoireMaintenanceIoLane lane,
        CancellationToken cancellationToken) =>
        throw Unreachable();

    public Task<Result<IGrimoireMaintenanceConnectionLease>> OpenJournalInventorySnapshotAsync(
        IGrimoireMaintenanceConnectionCapability capability,
        IGrimoireMaintenanceIoLane lane,
        CancellationToken cancellationToken) =>
        throw Unreachable();

}

/// <summary>Scratch paths for a gate whose purposes this suite never actually opens.</summary>
internal sealed class ScratchMaintenancePaths : IGrimoireMaintenancePathAuthority
{

    /// <summary>
    /// Names a real database when the suite has one, and an unused temporary path when it does not.
    /// </summary>
    /// <remarks>
    /// A gate cannot be built without somewhere for its purposes to resolve to, and most suites that
    /// need a gate never open anything through it. Those get a path under the temporary root that
    /// nothing creates. The ones that do perform a real read pass their own scratch file, and the
    /// point of the parameter is that it is the only way to get one: no overload of this names the
    /// installation's own Grimoire, so a suite cannot reach it by forgetting to say where to look.
    /// </remarks>
    internal ScratchMaintenancePaths(string? databasePath = null)
    {

        CanonicalDatabasePath = databasePath ?? Path.Combine(
            Path.GetTempPath(),
            $"covenant-unused-{Guid.NewGuid():N}",
            "arcanum.db");

        ExportStagingDatabasePath =
            CovenantResidualArtifacts.ExportStagingPath(CanonicalDatabasePath);

    }

    public string CanonicalDatabasePath { get; }

    public string ExportStagingDatabasePath { get; }

}

/// <summary>The one connection object the gate admits for this harness's ledger step.</summary>
internal sealed class ScratchLedgerConnection : ICovenantClosedPeriodLedgerConnection, IDisposable
{

    private readonly string _directory = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"covenant-ledger-{Guid.NewGuid():N}")).FullName;

    private readonly SqliteConnection _connection;

    public ScratchLedgerConnection() =>
        _connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_directory, "ledger.db"),
            }.ToString());

    public DbConnection Connection => _connection;

    public void Dispose()
    {

        _connection.Dispose();

        try
        {

            Directory.Delete(_directory, recursive: true);

        }
        catch (IOException)
        {
        }

    }

}

/// <summary>The scratch databases' fixed key, for a suite that opens one through the real factory.</summary>
internal sealed class ScratchPassphraseSource : IGrimoireDbPassphraseSource
{

    public string Passphrase { get; private set; } = CovenantSchemaScratchDatabase.ScratchPassphrase;

    public void SetPassphrase(string value) => Passphrase = value;

}
