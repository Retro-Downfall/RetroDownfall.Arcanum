using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

internal static class CovenantV3MaintenanceTestAuthority
{
    private static readonly ICovenantExclusiveOperationLease Lease = new TestExclusiveLease();

    internal static CovenantV3MaintenanceCapability Mint(CovenantV3MaintenancePurpose purpose) =>
        CovenantV3MaintenanceCapability.MintAsync(Lease, purpose, CancellationToken.None)
            .AsTask().GetAwaiter().GetResult().Value;

    internal static CovenantV3CompactionCapabilities Compaction() => new(
        Mint(CovenantV3MaintenancePurpose.CompactionVacuum),
        Mint(CovenantV3MaintenancePurpose.CompactionExport),
        Mint(CovenantV3MaintenancePurpose.CompactionExportVerification),
        Mint(CovenantV3MaintenancePurpose.CompactionPostReplaceJournalRestore));

    private sealed class TestExclusiveLease : ICovenantExclusiveOperationLease
    {
        public CovenantOperationLeaseSnapshot Snapshot { get; } = new(
            Guid.Parse("55555555-1111-4222-8333-444444444444"),
            1,
            CovenantLeaseKind.Exclusive,
            CovenantLeaseCoverage.Installation,
            null,
            Guid.Parse("66666666-1111-4222-8333-444444444444"),
            1,
            1,
            0,
            null,
            null,
            null,
            null,
            new CovenantExclusiveRecoveryOwner(
                Guid.Parse("77777777-1111-4222-8333-444444444444"),
                CovenantExclusiveOperation.CovenantReset,
                new CovenantDigest([.. Enumerable.Repeat((byte)0x41, CovenantLimits.DigestBytes)])),
            false);

        public CancellationToken Revocation => CancellationToken.None;

        public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());

        public Result ExecuteWhileHeld(Func<Result> callback) => callback();

        public ValueTask<Result> CompleteAsync(
            CovenantExclusiveLeaseDisposition disposition,
            CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class CovenantV3MaintenanceTestConnectionFactory(
    ICovenantMaintenanceConnectionFactory inner,
    ICovenantSqliteConnectionInitializer initializer)
    : ICovenantV3MaintenanceConnectionFactory, ICovenantV3MaintenancePathAuthority
{
    public string CanonicalDatabasePath => inner.DatabasePath;

    public string ExportStagingDatabasePath => CovenantResidualArtifacts.ExportStagingPath(inner.DatabasePath);

    public Task<Result<ICovenantV3MaintenanceConnectionLease>> OpenV3CanonicalErasureAsync(
        CovenantV3MaintenanceCapability capability,
        CancellationToken cancellationToken) => OpenAsync(
            capability,
            CovenantV3MaintenancePurpose.CanonicalErasure,
            inner.OpenAsync,
            CovenantSqliteConnectionMode.ExclusiveMaintenance,
            cancellationToken);

    public Task<Result<ICovenantV3MaintenanceConnectionLease>> OpenV3WalTruncationAsync(CovenantV3MaintenanceCapability capability, CancellationToken cancellationToken) =>
        OpenAsync(capability, CovenantV3MaintenancePurpose.WalTruncation, inner.OpenAsync, CovenantSqliteConnectionMode.ExclusiveMaintenance, cancellationToken);

    public Task<Result<ICovenantV3MaintenanceConnectionLease>> OpenV3VacuumAsync(CovenantV3MaintenanceCapability capability, CancellationToken cancellationToken) =>
        OpenAsync(capability, CovenantV3MaintenancePurpose.CompactionVacuum, inner.OpenAsync, CovenantSqliteConnectionMode.ExclusiveMaintenance, cancellationToken);

    public Task<Result<ICovenantV3MaintenanceConnectionLease>> OpenV3ExportSourceAsync(CovenantV3MaintenanceCapability capability, CancellationToken cancellationToken) =>
        OpenAsync(capability, CovenantV3MaintenancePurpose.CompactionExport, inner.OpenAsync, CovenantSqliteConnectionMode.ExclusiveMaintenance, cancellationToken);

    public Task<Result<ICovenantV3MaintenanceConnectionLease>> OpenV3ExportVerificationAsync(CovenantV3MaintenanceCapability capability, CancellationToken cancellationToken) =>
        OpenAsync(capability, CovenantV3MaintenancePurpose.CompactionExportVerification, token => inner.OpenSideFileAsync(ExportStagingDatabasePath, token), CovenantSqliteConnectionMode.ReadOnly, cancellationToken);

    public Task<Result<ICovenantV3MaintenanceConnectionLease>> OpenV3PostReplaceJournalRestoreAsync(CovenantV3MaintenanceCapability capability, CancellationToken cancellationToken) =>
        OpenAsync(capability, CovenantV3MaintenancePurpose.CompactionPostReplaceJournalRestore, inner.OpenAsync, CovenantSqliteConnectionMode.ReadWrite, cancellationToken);

    public Task<Result<ICovenantV3MaintenanceConnectionLease>> OpenV3AcceleratorInitializationAsync(CovenantV3MaintenanceCapability capability, CancellationToken cancellationToken) =>
        OpenAsync(capability, CovenantV3MaintenancePurpose.AcceleratorInitialization, inner.OpenAsync, CovenantSqliteConnectionMode.ExclusiveMaintenance, cancellationToken);

    public Task<Result<ICovenantV3MaintenanceConnectionLease>> OpenV3CandidateReopenVerificationAsync(CovenantV3MaintenanceCapability capability, CancellationToken cancellationToken) =>
        OpenAsync(capability, CovenantV3MaintenancePurpose.CandidateReopenVerification, inner.OpenSidecarFreeReadOnlyAsync, CovenantSqliteConnectionMode.ReadOnly, cancellationToken);

    public Task<Result> AttachV3ExportStagingAsync(ICovenantV3MaintenanceConnectionLease exportLease, CancellationToken cancellationToken) =>
        exportLease is TestLease { Purpose: CovenantV3MaintenancePurpose.CompactionExport }
            ? AttachAsync(exportLease.Connection, cancellationToken)
            : Task.FromResult(Result.Failure(new Error(ErrorCodes.Covenant.InvalidScope, "The test lease is not an export source.")));

    private async Task<Result> AttachAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await inner.AttachSideFileAsync(connection, "covenant_erasure_export", ExportStagingDatabasePath, cancellationToken);
        return Result.Success();
    }

    private async Task<Result<ICovenantV3MaintenanceConnectionLease>> OpenAsync(
        CovenantV3MaintenanceCapability capability,
        CovenantV3MaintenancePurpose purpose,
        Func<CancellationToken, Task<SqliteConnection>> open,
        CovenantSqliteConnectionMode mode,
        CancellationToken cancellationToken)
    {
        Result consumed = await capability.ConsumeAsync(purpose, cancellationToken);
        if (consumed.IsFailure)
        {
            return Result<ICovenantV3MaintenanceConnectionLease>.Failure(consumed.Error);
        }

        SqliteConnection? connection = null;
        try
        {
            connection = await open(cancellationToken);
            await initializer.InitializeAsync(connection, mode, cancellationToken);
            return Result<ICovenantV3MaintenanceConnectionLease>.Success(new TestLease(connection, purpose));
        }
        catch (Exception failed)
        {
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }

            return Result<ICovenantV3MaintenanceConnectionLease>.Failure(
                new Error(ErrorCodes.Covenant.MaintenanceFailed, failed.Message));
        }
    }

    private sealed class TestLease(SqliteConnection connection, CovenantV3MaintenancePurpose purpose)
        : ICovenantV3MaintenanceConnectionLease
    {
        public SqliteConnection Connection { get; } = connection;

        internal CovenantV3MaintenancePurpose Purpose { get; } = purpose;

        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }
}
