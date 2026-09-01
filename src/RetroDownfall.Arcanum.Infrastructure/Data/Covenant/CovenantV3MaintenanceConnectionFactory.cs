using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>Purpose-specific unpooled V3 maintenance handles; no caller supplies a path or key.</summary>
internal interface ICovenantV3MaintenanceConnectionFactory
{
    Task<Result<ICovenantV3MaintenanceConnectionLease>> OpenV3CanonicalErasureAsync(CovenantV3MaintenanceCapability capability, CancellationToken cancellationToken);

    Task<Result<ICovenantV3MaintenanceConnectionLease>> OpenV3WalTruncationAsync(CovenantV3MaintenanceCapability capability, CancellationToken cancellationToken);

    Task<Result<ICovenantV3MaintenanceConnectionLease>> OpenV3VacuumAsync(CovenantV3MaintenanceCapability capability, CancellationToken cancellationToken);

    Task<Result<ICovenantV3MaintenanceConnectionLease>> OpenV3ExportSourceAsync(CovenantV3MaintenanceCapability capability, CancellationToken cancellationToken);

    Task<Result<ICovenantV3MaintenanceConnectionLease>> OpenV3ExportVerificationAsync(CovenantV3MaintenanceCapability capability, CancellationToken cancellationToken);

    Task<Result<ICovenantV3MaintenanceConnectionLease>> OpenV3PostReplaceJournalRestoreAsync(CovenantV3MaintenanceCapability capability, CancellationToken cancellationToken);

    Task<Result<ICovenantV3MaintenanceConnectionLease>> OpenV3AcceleratorInitializationAsync(CovenantV3MaintenanceCapability capability, CancellationToken cancellationToken);

    Task<Result<ICovenantV3MaintenanceConnectionLease>> OpenV3CandidateReopenVerificationAsync(CovenantV3MaintenanceCapability capability, CancellationToken cancellationToken);

    Task<Result> AttachV3ExportStagingAsync(ICovenantV3MaintenanceConnectionLease exportLease, CancellationToken cancellationToken);
}

internal interface ICovenantV3MaintenanceConnectionLease : IAsyncDisposable
{
    SqliteConnection Connection { get; }
}

internal sealed class CovenantV3MaintenanceConnectionFactory(
    IGrimoireDbPassphraseSource passphrase,
    ISqliteNativeRuntime nativeRuntime) : ICovenantV3MaintenanceConnectionFactory
{
    private const string ExportAlias = "covenant_erasure_export";

    private readonly IGrimoireDbPassphraseSource _passphrase = passphrase ?? throw new ArgumentNullException(nameof(passphrase));

    private readonly ISqliteNativeRuntime _nativeRuntime = nativeRuntime ?? throw new ArgumentNullException(nameof(nativeRuntime));

    [GrimoireConnectionAcquisitionRoute]
    public Task<Result<ICovenantV3MaintenanceConnectionLease>> OpenV3CanonicalErasureAsync(CovenantV3MaintenanceCapability capability, CancellationToken cancellationToken) =>
        OpenAsync(capability, CovenantV3MaintenancePurpose.CanonicalErasure, DatabaseBuilder(), cancellationToken);

    [GrimoireConnectionAcquisitionRoute]
    public Task<Result<ICovenantV3MaintenanceConnectionLease>> OpenV3WalTruncationAsync(CovenantV3MaintenanceCapability capability, CancellationToken cancellationToken) =>
        OpenAsync(capability, CovenantV3MaintenancePurpose.WalTruncation, DatabaseBuilder(), cancellationToken);

    [GrimoireConnectionAcquisitionRoute]
    public Task<Result<ICovenantV3MaintenanceConnectionLease>> OpenV3VacuumAsync(CovenantV3MaintenanceCapability capability, CancellationToken cancellationToken) =>
        OpenAsync(capability, CovenantV3MaintenancePurpose.CompactionVacuum, DatabaseBuilder(), cancellationToken);

    [GrimoireConnectionAcquisitionRoute]
    public Task<Result<ICovenantV3MaintenanceConnectionLease>> OpenV3ExportSourceAsync(CovenantV3MaintenanceCapability capability, CancellationToken cancellationToken) =>
        OpenAsync(capability, CovenantV3MaintenancePurpose.CompactionExport, DatabaseBuilder(), cancellationToken);

    [GrimoireConnectionAcquisitionRoute]
    public Task<Result<ICovenantV3MaintenanceConnectionLease>> OpenV3ExportVerificationAsync(CovenantV3MaintenanceCapability capability, CancellationToken cancellationToken) =>
        OpenAsync(capability, CovenantV3MaintenancePurpose.CompactionExportVerification, StagingBuilder(), cancellationToken);

    [GrimoireConnectionAcquisitionRoute]
    public Task<Result<ICovenantV3MaintenanceConnectionLease>> OpenV3PostReplaceJournalRestoreAsync(CovenantV3MaintenanceCapability capability, CancellationToken cancellationToken) =>
        OpenAsync(capability, CovenantV3MaintenancePurpose.CompactionPostReplaceJournalRestore, DatabaseBuilder(), cancellationToken);

    [GrimoireConnectionAcquisitionRoute]
    public Task<Result<ICovenantV3MaintenanceConnectionLease>> OpenV3AcceleratorInitializationAsync(CovenantV3MaintenanceCapability capability, CancellationToken cancellationToken) =>
        OpenAsync(capability, CovenantV3MaintenancePurpose.AcceleratorInitialization, DatabaseBuilder(), cancellationToken);

    [GrimoireConnectionAcquisitionRoute]
    public Task<Result<ICovenantV3MaintenanceConnectionLease>> OpenV3CandidateReopenVerificationAsync(CovenantV3MaintenanceCapability capability, CancellationToken cancellationToken) =>
        OpenAsync(capability, CovenantV3MaintenancePurpose.CandidateReopenVerification, ImmutableReadOnlyBuilder(), cancellationToken);

    public async Task<Result> AttachV3ExportStagingAsync(
        ICovenantV3MaintenanceConnectionLease exportLease,
        CancellationToken cancellationToken)
    {
        if (exportLease is not CovenantV3MaintenanceConnectionLease { Purpose: CovenantV3MaintenancePurpose.CompactionExport })
        {
            return Result.Failure(new Error(ErrorCodes.Covenant.InvalidScope, "Only an export-source V3 lease can attach the export staging database."));
        }

        await using SqliteCommand command = exportLease.Connection.CreateCommand();
        command.CommandText = $"ATTACH DATABASE $path AS {ExportAlias} KEY $key;";
        _ = command.Parameters.AddWithValue("$path", CovenantResidualArtifacts.ExportStagingPath(ArcanumPaths.GrimoireDatabaseFile));
        _ = command.Parameters.AddWithValue("$key", _passphrase.Passphrase);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    private async Task<Result<ICovenantV3MaintenanceConnectionLease>> OpenAsync(
        CovenantV3MaintenanceCapability capability,
        CovenantV3MaintenancePurpose purpose,
        SqliteConnectionStringBuilder builder,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capability);
        Result consumed = await capability.ConsumeAsync(purpose, cancellationToken).ConfigureAwait(false);
        if (consumed.IsFailure)
        {
            return Result<ICovenantV3MaintenanceConnectionLease>.Failure(consumed.Error);
        }

        _nativeRuntime.Initialize();
        SqliteConnection connection = new(builder.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return Result<ICovenantV3MaintenanceConnectionLease>.Success(new CovenantV3MaintenanceConnectionLease(connection, purpose));
        }
        catch (SqliteException failed)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            return Result<ICovenantV3MaintenanceConnectionLease>.Failure(new Error(ErrorCodes.Covenant.MaintenanceFailed, failed.Message));
        }
    }

    private SqliteConnectionStringBuilder DatabaseBuilder() => new()
    {
        DataSource = ArcanumPaths.GrimoireDatabaseFile,
        Password = _passphrase.Passphrase,
        Pooling = false,
    };

    private SqliteConnectionStringBuilder StagingBuilder() => new()
    {
        DataSource = CovenantResidualArtifacts.ExportStagingPath(ArcanumPaths.GrimoireDatabaseFile),
        Password = _passphrase.Passphrase,
        Pooling = false,
    };

    private SqliteConnectionStringBuilder ImmutableReadOnlyBuilder() => new()
    {
        DataSource = "file:" + Path.GetFullPath(ArcanumPaths.GrimoireDatabaseFile) + "?immutable=1",
        Password = _passphrase.Passphrase,
        Pooling = false,
        Mode = SqliteOpenMode.ReadOnly,
    };

    private sealed class CovenantV3MaintenanceConnectionLease(
        SqliteConnection connection,
        CovenantV3MaintenancePurpose purpose) : ICovenantV3MaintenanceConnectionLease
    {
        public SqliteConnection Connection { get; } = connection;

        internal CovenantV3MaintenancePurpose Purpose { get; } = purpose;

        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }
}
