using System.Reflection;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

public sealed class CovenantV3MaintenanceCapabilityTests
{
    [Fact]
    public async Task MintAsync_RejectsMissingRecoveryOwner()
    {
        StubExclusiveLease lease = new(CovenantExclusiveOperation.CovenantReset, hasOwner: false);

        Assert.True((await CovenantV3MaintenanceCapability.MintAsync(
            lease,
            CovenantV3MaintenancePurpose.CanonicalErasure,
            CancellationToken.None)).IsFailure);
    }

    [Fact]
    public async Task MintAsync_RejectsWrongOperation()
    {
        StubExclusiveLease lease = new(CovenantExclusiveOperation.BackupRestore);

        Assert.True((await CovenantV3MaintenanceCapability.MintAsync(
            lease,
            CovenantV3MaintenancePurpose.CanonicalErasure,
            CancellationToken.None)).IsFailure);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task MintAsync_RejectsStaleRevokedOrDisposedLease(bool revalidates, bool held)
    {
        StubExclusiveLease lease = new(CovenantExclusiveOperation.CovenantReset, revalidates: revalidates, held: held);

        Assert.True((await CovenantV3MaintenanceCapability.MintAsync(
            lease,
            CovenantV3MaintenancePurpose.CanonicalErasure,
            CancellationToken.None)).IsFailure);
    }

    [Fact]
    public async Task ConsumeAsync_RejectsADifferentPurpose()
    {
        StubExclusiveLease lease = new(CovenantExclusiveOperation.CovenantReset);

        Result<CovenantV3MaintenanceCapability> minted = await CovenantV3MaintenanceCapability.MintAsync(
            lease,
            CovenantV3MaintenancePurpose.CanonicalErasure,
            CancellationToken.None);

        Assert.True(minted.IsSuccess);

        Result consumed = await minted.Value.ConsumeAsync(
            CovenantV3MaintenancePurpose.WalTruncation,
            CancellationToken.None);

        Assert.True(consumed.IsFailure);

        Assert.True((await minted.Value.ConsumeAsync(
            CovenantV3MaintenancePurpose.CanonicalErasure,
            CancellationToken.None)).IsFailure);
    }

    [Fact]
    public async Task ConsumeAsync_IsOneShot()
    {
        CovenantV3MaintenanceCapability capability = (await CovenantV3MaintenanceCapability.MintAsync(
            new StubExclusiveLease(CovenantExclusiveOperation.CovenantReset),
            CovenantV3MaintenancePurpose.CanonicalErasure,
            CancellationToken.None)).Value;

        Assert.True((await capability.ConsumeAsync(CovenantV3MaintenancePurpose.CanonicalErasure, CancellationToken.None)).IsSuccess);
        Assert.True((await capability.ConsumeAsync(CovenantV3MaintenancePurpose.CanonicalErasure, CancellationToken.None)).IsFailure);
    }

    [Fact]
    public async Task DisposeAsync_RevokesAnUnusedCapability()
    {
        CovenantV3MaintenanceCapability capability = (await CovenantV3MaintenanceCapability.MintAsync(
            new StubExclusiveLease(CovenantExclusiveOperation.HealthyCatalogFactoryErasure),
            CovenantV3MaintenancePurpose.CanonicalErasure,
            CancellationToken.None)).Value;

        await capability.DisposeAsync();

        Assert.True((await capability.ConsumeAsync(CovenantV3MaintenancePurpose.CanonicalErasure, CancellationToken.None)).IsFailure);
    }

    [Fact]
    public async Task Factory_ConsumesAuthorityBeforeNativeInitializationOrPassphraseResolution()
    {
        CountingPassphrase passphrase = new();
        ThrowingNativeRuntime runtime = new();
        CovenantV3MaintenanceConnectionFactory factory = new(
            passphrase,
            runtime,
            CovenantSqliteConnectionInitializer.Instance);
        CovenantV3MaintenanceCapability capability = (await CovenantV3MaintenanceCapability.MintAsync(
            new StubExclusiveLease(CovenantExclusiveOperation.CovenantReset),
            CovenantV3MaintenancePurpose.CanonicalErasure,
            CancellationToken.None)).Value;

        Result<ICovenantV3MaintenanceConnectionLease> first =
            await factory.OpenV3CanonicalErasureAsync(capability, CancellationToken.None);
        Result<ICovenantV3MaintenanceConnectionLease> replay =
            await factory.OpenV3CanonicalErasureAsync(capability, CancellationToken.None);

        Assert.True(first.IsFailure);
        Assert.True(replay.IsFailure);
        Assert.Equal(1, runtime.InitializeCalls);
        Assert.Equal(0, passphrase.Reads);
    }

    [Fact]
    public void FactoryInterface_IsClosedAndPurposeSpecific()
    {
        Assert.Equal(
            [
                "AttachV3ExportStagingAsync",
                "OpenV3AcceleratorInitializationAsync",
                "OpenV3CandidateReopenVerificationAsync",
                "OpenV3CanonicalErasureAsync",
                "OpenV3ExportSourceAsync",
                "OpenV3ExportVerificationAsync",
                "OpenV3PostReplaceJournalRestoreAsync",
                "OpenV3VacuumAsync",
                "OpenV3WalTruncationAsync",
            ],
            typeof(ICovenantV3MaintenanceConnectionFactory).GetMethods()
                .Select(static method => method.Name)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Factory_FixesCanonicalStagingImmutableAndUnpooledPolicies()
    {
        CovenantV3MaintenanceConnectionFactory factory = new(
            new FixedPassphrase(),
            new ThrowingNativeRuntime(),
            CovenantSqliteConnectionInitializer.Instance);

        SqliteConnectionStringBuilder canonical = Builder(factory, "DatabaseBuilder");
        SqliteConnectionStringBuilder staging = Builder(factory, "StagingBuilder");
        SqliteConnectionStringBuilder immutable = Builder(factory, "ImmutableReadOnlyBuilder");

        Assert.Equal(ArcanumPaths.GrimoireDatabaseFile, canonical.DataSource);
        Assert.Equal(CovenantResidualArtifacts.ExportStagingPath(ArcanumPaths.GrimoireDatabaseFile), staging.DataSource);
        Assert.Contains("immutable=1", immutable.DataSource, StringComparison.Ordinal);
        Assert.Equal(SqliteOpenMode.ReadOnly, staging.Mode);
        Assert.Equal(SqliteOpenMode.ReadOnly, immutable.Mode);
        Assert.False(canonical.Pooling);
        Assert.False(staging.Pooling);
        Assert.False(immutable.Pooling);
    }

    [Fact]
    public async Task Attach_rejectsAnotherLeaseKind_andProductionLeaseOwnsPhysicalDisposal()
    {
        CovenantV3MaintenanceConnectionFactory factory = new(
            new FixedPassphrase(),
            new ThrowingNativeRuntime(),
            CovenantSqliteConnectionInitializer.Instance);
        await using FakeV3Lease wrong = new();

        Assert.True((await factory.AttachV3ExportStagingAsync(wrong, CancellationToken.None)).IsFailure);

        SqliteConnection connection = new("Data Source=:memory:");
        connection.Open();
        Type leaseType = typeof(CovenantV3MaintenanceConnectionFactory).GetNestedType(
            "CovenantV3MaintenanceConnectionLease",
            BindingFlags.NonPublic)!;
        ICovenantV3MaintenanceConnectionLease owned = (ICovenantV3MaintenanceConnectionLease)
            Activator.CreateInstance(
                leaseType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                [connection, CovenantV3MaintenancePurpose.CompactionExport],
                culture: null)!;

        await owned.DisposeAsync();

        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
    }

    private static SqliteConnectionStringBuilder Builder(
        CovenantV3MaintenanceConnectionFactory factory,
        string name) =>
        (SqliteConnectionStringBuilder)typeof(CovenantV3MaintenanceConnectionFactory)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(factory, null)!;

    private sealed class StubExclusiveLease(
        CovenantExclusiveOperation operation,
        bool hasOwner = true,
        bool revalidates = true,
        bool held = true) : ICovenantExclusiveOperationLease
    {
        public CovenantOperationLeaseSnapshot Snapshot { get; } = new(
            Guid.Parse("5F6E7D8C-9B0A-4132-8455-667788990011"),
            1,
            CovenantLeaseKind.Exclusive,
            CovenantLeaseCoverage.Installation,
            null,
            Guid.Parse("11111111-2222-4333-8444-555555555555"),
            1,
            1,
            0,
            null,
            null,
            null,
            null,
            hasOwner
                ? new CovenantExclusiveRecoveryOwner(
                    Guid.Parse("77777777-8888-4999-8AAA-BBBBBBBBBBBB"),
                    operation,
                    new CovenantDigest([.. Enumerable.Repeat((byte)0x44, CovenantLimits.DigestBytes)]))
                : null,
            false);

        public CancellationToken Revocation => CancellationToken.None;

        public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken) => ValueTask.FromResult(
            revalidates
                ? Result.Success()
                : Result.Failure(new Error(ErrorCodes.Covenant.InvalidScope, "stale")));

        public Result ExecuteWhileHeld(Func<Result> callback) => held
            ? callback()
            : Result.Failure(new Error(ErrorCodes.Covenant.InvalidScope, "disposed"));

        public ValueTask<Result> CompleteAsync(CovenantExclusiveLeaseDisposition disposition, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CountingPassphrase : IGrimoireDbPassphraseSource
    {
        public int Reads { get; private set; }

        public string Passphrase
        {
            get
            {
                Reads++;
                return "unused";
            }
        }

        public void SetPassphrase(string passphrase) => throw new NotSupportedException();
    }

    private sealed class FixedPassphrase : IGrimoireDbPassphraseSource
    {
        public string Passphrase => "test-passphrase";

        public void SetPassphrase(string passphrase) => throw new NotSupportedException();
    }

    private sealed class FakeV3Lease : ICovenantV3MaintenanceConnectionLease
    {
        public SqliteConnection Connection { get; } = new("Data Source=:memory:");

        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }

    private sealed class ThrowingNativeRuntime : ISqliteNativeRuntime
    {
        public int InitializeCalls { get; private set; }

        public void Initialize()
        {
            InitializeCalls++;
            throw new InvalidOperationException("stop before provider construction");
        }
    }
}
