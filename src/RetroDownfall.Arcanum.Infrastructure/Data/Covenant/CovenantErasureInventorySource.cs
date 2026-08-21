using System.Data;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// Builds the complete effect-free Covenant erasure proof and replays its two executor arms through
/// bounded keyset pages, owning and releasing one maintenance snapshot per call.
/// </summary>
internal sealed class CovenantErasureInventorySource(
    ICovenantMaintenanceConnectionFactory connections,
    ICovenantSqliteConnectionInitializer initializer,
    ICovenantConnectionDrain drain,
    CovenantHealthyCatalogErasureGuard healthyCatalog,
    CovenantManagedFileErasureRequestReader managedFiles,
    CovenantDisclosureExposureReader disclosures) : ICovenantErasureInventorySource
{

    private const int PageSize = CovenantProtectedArtifactErasurePage.MaxItems;

    private static readonly Error UnsafeInventory = new(
        ErrorCodes.Covenant.IntegrityFailure,
        "The Covenant erasure inventory could not be proved from one bounded storage snapshot.");

    private static readonly Error ManualManagedErasure = new(
        ErrorCodes.Covenant.ManualArtifactErasureRequired,
        "A managed artifact does not have one exact adopted ownership record and requires manual erasure.");

    private readonly ICovenantMaintenanceConnectionFactory _connections =
        connections ?? throw new ArgumentNullException(nameof(connections));

    private readonly ICovenantSqliteConnectionInitializer _initializer =
        initializer ?? throw new ArgumentNullException(nameof(initializer));

    private readonly ICovenantConnectionDrain _drain =
        drain ?? throw new ArgumentNullException(nameof(drain));

    private readonly CovenantHealthyCatalogErasureGuard _healthyCatalog =
        healthyCatalog ?? throw new ArgumentNullException(nameof(healthyCatalog));

    private readonly CovenantManagedFileErasureRequestReader _managedFiles =
        managedFiles ?? throw new ArgumentNullException(nameof(managedFiles));

    private readonly CovenantDisclosureExposureReader _disclosures =
        disclosures ?? throw new ArgumentNullException(nameof(disclosures));

    public Task<Result<CovenantErasureInventorySummary>> PreflightBeforeCanonicalAsync(
        CovenantExclusiveOperation operation,
        Guid datasetGeneration,
        CancellationToken cancellationToken)
    {

        if (operation is not CovenantExclusiveOperation.CovenantReset
            and not CovenantExclusiveOperation.HealthyCatalogFactoryErasure
            || datasetGeneration == Guid.Empty)
        {

            return Task.FromResult(Result<CovenantErasureInventorySummary>.Failure(UnsafeInventory));

        }

        return WithOwnedSnapshotAsync(
            async (connection, transaction, token) =>
            {

                Result<Guid> liveDataset = await ReadDatasetGenerationAsync(
                    connection,
                    transaction,
                    token).ConfigureAwait(false);

                if (liveDataset.IsFailure || liveDataset.Value != datasetGeneration)
                {

                    return Result<CovenantErasureInventorySummary>.Failure(UnsafeInventory);

                }

                if (operation == CovenantExclusiveOperation.HealthyCatalogFactoryErasure)
                {

                    Result healthy = await _healthyCatalog
                        .RequireHealthyWithinAsync(connection, transaction, token)
                        .ConfigureAwait(false);

                    if (healthy.IsFailure)
                    {

                        return Result<CovenantErasureInventorySummary>.Failure(healthy.Error);

                    }

                }

                Result<CovenantDisclosureExposure> exposure = await _disclosures
                    .ReadWithinAsync(connection, transaction, token)
                    .ConfigureAwait(false);

                if (exposure.IsFailure)
                {

                    return Result<CovenantErasureInventorySummary>.Failure(exposure.Error);

                }

                long databaseCount = 0;

                long managedCount = 0;

                Guid? cursor = null;

                while (true)
                {

                    Result<IReadOnlyList<ArtifactSensitivityLabel>> page =
                        await ArtifactSensitivityLedger.ReadPageWithinAsync(
                            connection,
                            transaction,
                            cursor,
                            PageSize,
                            token).ConfigureAwait(false);

                    if (page.IsFailure)
                    {

                        return Result<CovenantErasureInventorySummary>.Failure(page.Error);

                    }

                    foreach (ArtifactSensitivityLabel label in page.Value)
                    {

                        Result<CovenantSensitiveArtifactPurgeRule> rule =
                            CovenantSensitiveArtifactPurgePolicy.Resolve(label.ArtifactKind);

                        if (rule.IsFailure)
                        {

                            return Result<CovenantErasureInventorySummary>.Failure(UnsafeInventory);

                        }

                        try
                        {

                            if (rule.Value.Executor == CovenantArtifactPurgeExecutor.DatabaseTransaction)
                            {

                                databaseCount = checked(databaseCount + 1);

                            }
                            else
                            {

                                Result<CovenantManagedFileErasureIdentity?> source = await _managedFiles
                                    .TryReadWithinAsync(connection, transaction, label, token)
                                    .ConfigureAwait(false);

                                if (source.IsFailure || source.Value is null)
                                {

                                    return Result<CovenantErasureInventorySummary>.Failure(
                                        source.IsFailure ? source.Error : ManualManagedErasure);

                                }

                                managedCount = checked(managedCount + 1);

                            }

                        }
                        catch (OverflowException)
                        {

                            return Result<CovenantErasureInventorySummary>.Failure(UnsafeInventory);

                        }

                    }

                    if (page.Value.Count == 0)
                    {

                        break;

                    }

                    cursor = page.Value[^1].LabelId;

                    if (page.Value.Count < PageSize)
                    {

                        break;

                    }

                }

                return Result<CovenantErasureInventorySummary>.Success(
                    new CovenantErasureInventorySummary(databaseCount, managedCount, exposure.Value));

            },
            cancellationToken);

    }

    public Task<Result> PreflightRemainingManagedAsync(CancellationToken cancellationToken) =>
        WithOwnedSnapshotAsync(
            async (connection, transaction, token) =>
            {

                Guid? cursor = null;

                while (true)
                {

                    Result<IReadOnlyList<ArtifactSensitivityLabel>> page =
                        await ArtifactSensitivityLedger.ReadPageWithinAsync(
                            connection,
                            transaction,
                            cursor,
                            PageSize,
                            token).ConfigureAwait(false);

                    if (page.IsFailure)
                    {

                        return Result.Failure(page.Error);

                    }

                    foreach (ArtifactSensitivityLabel label in page.Value)
                    {

                        Result<CovenantSensitiveArtifactPurgeRule> rule =
                            CovenantSensitiveArtifactPurgePolicy.Resolve(label.ArtifactKind);

                        if (rule.IsFailure)
                        {

                            return Result.Failure(UnsafeInventory);

                        }

                        if (rule.Value.Executor != CovenantArtifactPurgeExecutor.ManagedFileKernel)
                        {

                            continue;

                        }

                        Result<CovenantManagedFileErasureIdentity?> source = await _managedFiles
                            .TryReadWithinAsync(connection, transaction, label, token)
                            .ConfigureAwait(false);

                        if (source.IsFailure || source.Value is null)
                        {

                            return Result.Failure(source.IsFailure ? source.Error : ManualManagedErasure);

                        }

                    }

                    if (page.Value.Count == 0)
                    {

                        break;

                    }

                    cursor = page.Value[^1].LabelId;

                    if (page.Value.Count < PageSize)
                    {

                        break;

                    }

                }

                return Result.Success();

            },
            cancellationToken);

    public Task<Result<CovenantDatabaseErasureBatch>> ReadNextDatabaseBatchAsync(
        Guid datasetGeneration,
        Guid? afterLabelId,
        CancellationToken cancellationToken)
    {

        if (datasetGeneration == Guid.Empty || afterLabelId == Guid.Empty)
        {

            return Task.FromResult(Result<CovenantDatabaseErasureBatch>.Failure(UnsafeInventory));

        }

        return WithOwnedSnapshotAsync(
            async (connection, transaction, token) =>
            {

                Result<IReadOnlyList<ArtifactSensitivityLabel>> raw =
                    await ArtifactSensitivityLedger.ReadPageWithinAsync(
                        connection,
                        transaction,
                        afterLabelId,
                        PageSize,
                        token).ConfigureAwait(false);

                if (raw.IsFailure)
                {

                    return Result<CovenantDatabaseErasureBatch>.Failure(raw.Error);

                }

                List<CovenantProtectedArtifactErasureItem> items = [];

                foreach (ArtifactSensitivityLabel label in raw.Value)
                {

                    Result<CovenantSensitiveArtifactPurgeRule> rule =
                        CovenantSensitiveArtifactPurgePolicy.Resolve(label.ArtifactKind);

                    if (rule.IsFailure)
                    {

                        return Result<CovenantDatabaseErasureBatch>.Failure(UnsafeInventory);

                    }

                    if (rule.Value.Executor == CovenantArtifactPurgeExecutor.DatabaseTransaction)
                    {

                        items.Add(ToDatabaseItem(label));

                    }

                }

                Guid? next = raw.Value.Count == 0 ? afterLabelId : raw.Value[^1].LabelId;

                bool complete = raw.Value.Count < PageSize;

                CovenantProtectedArtifactErasurePage? page = items.Count == 0
                    ? null
                    : new CovenantProtectedArtifactErasurePage(datasetGeneration, items);

                return Result<CovenantDatabaseErasureBatch>.Success(
                    new CovenantDatabaseErasureBatch(next, complete, page));

            },
            cancellationToken);

    }

    public Task<Result<CovenantManagedFileErasureBatch>> ReadNextManagedFileBatchAsync(
        Guid operationId,
        Guid? afterLabelId,
        CancellationToken cancellationToken)
    {

        if (operationId == Guid.Empty || afterLabelId == Guid.Empty)
        {

            return Task.FromResult(Result<CovenantManagedFileErasureBatch>.Failure(UnsafeInventory));

        }

        return WithOwnedSnapshotAsync(
            async (connection, transaction, token) =>
            {

                Result<IReadOnlyList<ArtifactSensitivityLabel>> raw =
                    await ArtifactSensitivityLedger.ReadPageWithinAsync(
                        connection,
                        transaction,
                        afterLabelId,
                        PageSize,
                        token).ConfigureAwait(false);

                if (raw.IsFailure)
                {

                    return Result<CovenantManagedFileErasureBatch>.Failure(raw.Error);

                }

                List<CovenantManagedFileErasureRequest> requests = [];

                foreach (ArtifactSensitivityLabel label in raw.Value)
                {

                    Result<CovenantSensitiveArtifactPurgeRule> rule =
                        CovenantSensitiveArtifactPurgePolicy.Resolve(label.ArtifactKind);

                    if (rule.IsFailure)
                    {

                        return Result<CovenantManagedFileErasureBatch>.Failure(UnsafeInventory);

                    }

                    if (rule.Value.Executor != CovenantArtifactPurgeExecutor.ManagedFileKernel)
                    {

                        continue;

                    }

                    Result<CovenantManagedFileErasureIdentity?> source = await _managedFiles
                        .TryReadWithinAsync(connection, transaction, label, token)
                        .ConfigureAwait(false);

                    if (source.IsFailure || source.Value is null)
                    {

                        return Result<CovenantManagedFileErasureBatch>.Failure(
                            source.IsFailure ? source.Error : ManualManagedErasure);

                    }

                    requests.Add(source.Value.ToRequest(operationId));

                }

                Guid? next = raw.Value.Count == 0 ? afterLabelId : raw.Value[^1].LabelId;

                bool complete = raw.Value.Count < PageSize;

                return Result<CovenantManagedFileErasureBatch>.Success(
                    new CovenantManagedFileErasureBatch(next, complete, requests));

            },
            cancellationToken);

    }

    public Task<Result<CovenantDisclosureExposure>> ReadDisclosureExposureAsync(
        CancellationToken cancellationToken) =>
        WithOwnedSnapshotAsync(
            (connection, transaction, token) =>
                _disclosures.ReadWithinAsync(connection, transaction, token),
            cancellationToken);

    private static CovenantProtectedArtifactErasureItem ToDatabaseItem(
        ArtifactSensitivityLabel label) =>
        new(
            label.ArtifactId,
            label.ArtifactKind,
            label.SessionId,
            label.LabelId,
            label,
            label.ArtifactContentDigest,
            label.ArtifactRevision);

    private static async Task<Result<Guid>> ReadDatasetGenerationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT DatasetGeneration
            FROM covenant_state
            WHERE StateKey = 1
              AND typeof(DatasetGeneration) = 'blob'
              AND length(DatasetGeneration) = 16;
            """;

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        if (value is not byte[] { Length: 16 } bytes)
        {

            return Result<Guid>.Failure(UnsafeInventory);

        }

        Guid generation = new(bytes);

        return generation == Guid.Empty
            ? Result<Guid>.Failure(UnsafeInventory)
            : Result<Guid>.Success(generation);

    }

    private async Task<Result<T>> WithOwnedSnapshotAsync<T>(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<Result<T>>> work,
        CancellationToken cancellationToken)
    {

        SqliteConnection? connection = null;

        SqliteTransaction? transaction = null;

        IDisposable? enrollment = null;

        Result<T> result = Result<T>.Failure(UnsafeInventory);

        bool callerCancelled = false;

        try
        {

            connection = await _connections.OpenReadOnlyAsync(cancellationToken).ConfigureAwait(false);

            enrollment = _drain.Register(connection);

            await _initializer.InitializeAsync(
                connection,
                CovenantSqliteConnectionMode.ReadOnly,
                cancellationToken).ConfigureAwait(false);

            transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            result = await work(connection, transaction, cancellationToken).ConfigureAwait(false);

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            callerCancelled = true;

        }
        catch
        {

            result = Result<T>.Failure(UnsafeInventory);

        }

        bool cleaned = await CleanupAsync(transaction, connection, enrollment).ConfigureAwait(false);

        if (callerCancelled)
        {

            cancellationToken.ThrowIfCancellationRequested();

        }

        return cleaned ? result : Result<T>.Failure(UnsafeInventory);

    }

    private async Task<Result> WithOwnedSnapshotAsync(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<Result>> work,
        CancellationToken cancellationToken)
    {

        Result<Unit> result = await WithOwnedSnapshotAsync(
            async (connection, transaction, token) =>
            {

                Result completed = await work(connection, transaction, token).ConfigureAwait(false);

                return completed.IsFailure
                    ? Result<Unit>.Failure(completed.Error)
                    : Result<Unit>.Success(Unit.Value);

            },
            cancellationToken).ConfigureAwait(false);

        return result.IsFailure ? Result.Failure(result.Error) : Result.Success();

    }

    private static async Task<bool> CleanupAsync(
        SqliteTransaction? transaction,
        SqliteConnection? connection,
        IDisposable? enrollment)
    {

        bool cleaned = true;

        if (transaction is not null)
        {

            try
            {

                await transaction.DisposeAsync().ConfigureAwait(false);

            }
            catch
            {

                cleaned = false;

            }

        }

        if (connection is not null)
        {

            try
            {

                if (connection.State != ConnectionState.Closed)
                {

                    await connection.CloseAsync().ConfigureAwait(false);

                }

                await connection.DisposeAsync().ConfigureAwait(false);

            }
            catch
            {

                cleaned = false;

            }

        }

        try
        {

            enrollment?.Dispose();

        }
        catch
        {

            cleaned = false;

        }

        return cleaned;

    }

    private readonly record struct Unit
    {

        internal static Unit Value { get; } = new();

    }

}
