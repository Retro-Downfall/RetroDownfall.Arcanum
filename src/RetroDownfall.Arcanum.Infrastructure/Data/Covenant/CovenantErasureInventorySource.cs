using System.Data;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// The exact canonical state an offline transition binds its launch to.
/// </summary>
/// <remarks>
/// Read as one tuple rather than four reads because the four move together: the canonical transaction
/// stamps a generation and advances all three epochs in the same statement, so a reader that took them
/// separately could observe a generation from before a commit beside epochs from after one and hand a
/// launch a source state that never existed.
/// </remarks>
internal readonly record struct CovenantOfflineTransitionSourceState(
    Guid DatasetGeneration,
    ulong AcceleratorEpoch,
    ulong KeyReclamationEpoch,
    ulong EnvelopeKeyEpoch);

/// <summary>
/// Builds the complete effect-free Covenant erasure proof and replays its two executor arms through
/// bounded keyset pages, owning and releasing one maintenance snapshot per call.
/// </summary>
internal sealed class CovenantErasureInventorySource(
    IGrimoireOrdinaryConnectionFactory connections,
    CovenantHealthyCatalogErasureGuard healthyCatalog,
    CovenantManagedFileErasureRequestReader managedFiles,
    CovenantDisclosureExposureReader disclosures) : ICovenantErasureInventorySource
{

    private const int PageSize = CovenantProtectedArtifactErasurePage.MaxItems;

    private static readonly Error UnsafeInventory = new(
        ErrorCodes.Covenant.IntegrityFailure,
        "The Covenant erasure inventory could not be proved from one bounded storage snapshot.");

    private static readonly Error UnadvanceableCanonicalState = new(
        ErrorCodes.Covenant.IntegrityFailure,
        "The Covenant canonical state is not one an offline transition can preselect a target against.");

    private static readonly Error ManualManagedErasure = new(
        ErrorCodes.Covenant.ManualArtifactErasureRequired,
        "A managed artifact does not have one exact adopted ownership record and requires manual erasure.");

    private readonly IGrimoireOrdinaryConnectionFactory _connections =
        connections ?? throw new ArgumentNullException(nameof(connections));

    private readonly CovenantHealthyCatalogErasureGuard _healthyCatalog =
        healthyCatalog ?? throw new ArgumentNullException(nameof(healthyCatalog));

    private readonly CovenantManagedFileErasureRequestReader _managedFiles =
        managedFiles ?? throw new ArgumentNullException(nameof(managedFiles));

    private readonly CovenantDisclosureExposureReader _disclosures =
        disclosures ?? throw new ArgumentNullException(nameof(disclosures));

    /// <summary>
    /// Reads the exact source generation and epoch tuple a launch preselects its target against.
    /// </summary>
    /// <remarks>
    /// This runs before the launch row commits and therefore before admission closes, which is the
    /// only place the refusals below are cheap. An epoch already at the ceiling its update trigger
    /// refuses to move past cannot have a successor preselected for it, and a launch that preselected
    /// one anyway would be refused by the database after the transition had already stopped ordinary
    /// access — with no phase left that can safely go either forward or back.
    /// </remarks>
    public Task<Result<CovenantOfflineTransitionSourceState>> ReadOfflineTransitionSourceStateAsync(
        CancellationToken cancellationToken) =>
        WithOwnedSnapshotAsync(
            authority: null,
            ReadOfflineTransitionSourceStateAsync,
            cancellationToken);

    public Task<Result<CovenantErasureInventorySummary>> PreflightBeforeCanonicalAsync(
        CovenantExclusiveOperation operation,
        Guid datasetGeneration,
        CovenantClosedPeriodAuthority authority,
        CancellationToken cancellationToken)
    {

        if (operation is not CovenantExclusiveOperation.CovenantReset
            and not CovenantExclusiveOperation.HealthyCatalogFactoryErasure
            || datasetGeneration == Guid.Empty)
        {

            return Task.FromResult(Result<CovenantErasureInventorySummary>.Failure(UnsafeInventory));

        }

        return WithOwnedSnapshotAsync(
            authority,
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

    public Task<Result> PreflightRemainingManagedAsync(
        CovenantClosedPeriodAuthority authority,
        CancellationToken cancellationToken) =>
        WithOwnedSnapshotAsync(
            authority,
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
        CovenantClosedPeriodAuthority authority,
        CancellationToken cancellationToken)
    {

        if (datasetGeneration == Guid.Empty || afterLabelId == Guid.Empty)
        {

            return Task.FromResult(Result<CovenantDatabaseErasureBatch>.Failure(UnsafeInventory));

        }

        return WithOwnedSnapshotAsync(
            authority,
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
        CovenantClosedPeriodAuthority authority,
        CancellationToken cancellationToken)
    {

        if (operationId == Guid.Empty || afterLabelId == Guid.Empty)
        {

            return Task.FromResult(Result<CovenantManagedFileErasureBatch>.Failure(UnsafeInventory));

        }

        return WithOwnedSnapshotAsync(
            authority,
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
        CovenantClosedPeriodAuthority authority,
        CancellationToken cancellationToken) =>
        WithOwnedSnapshotAsync(
            authority,
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

    /// <summary>
    /// The current source tuple, read as one statement over a caller-supplied handle.
    /// </summary>
    /// <remarks>
    /// Shared with pre-readiness recovery rather than copied there. The rule this encodes — which four
    /// columns are the tuple and which values are advanceable — decides whether a transition may be
    /// resumed, and a second reader would be right on the day it was written.
    ///
    /// <para>The transaction is optional because the two callers differ in what they hold. The
    /// inventory reads inside its own maintenance snapshot; recovery reads over an unpooled connection
    /// that is the only handle on the catalog, and enrolling it in a transaction would add a writer to
    /// a pass whose whole property is that it does not write.</para>
    /// </remarks>
    internal static async Task<Result<CovenantOfflineTransitionSourceState>>
        ReadOfflineTransitionSourceStateAsync(
            SqliteConnection connection,
            SqliteTransaction? transaction,
            CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT DatasetGeneration, AcceleratorEpoch, KeyReclamationEpoch, EnvelopeKeyEpoch
            FROM covenant_state
            WHERE StateKey = 1
              AND typeof(DatasetGeneration) = 'blob'
              AND length(DatasetGeneration) = 16;
            """;

        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            return Result<CovenantOfflineTransitionSourceState>.Failure(UnadvanceableCanonicalState);

        }

        Guid generation = new(reader.GetFieldValue<byte[]>(0));

        if (generation == Guid.Empty
            || !TryReadAdvanceableEpoch(reader, 1, out ulong accelerator)
            || !TryReadAdvanceableEpoch(reader, 2, out ulong keyReclamation)
            || !TryReadAdvanceableEpoch(reader, 3, out ulong envelopeKey))
        {

            return Result<CovenantOfflineTransitionSourceState>.Failure(UnadvanceableCanonicalState);

        }

        return Result<CovenantOfflineTransitionSourceState>.Success(
            new CovenantOfflineTransitionSourceState(
                generation,
                accelerator,
                keyReclamation,
                envelopeKey));

    }

    /// <summary>
    /// One epoch, when it is one a successor can still be preselected for.
    /// </summary>
    /// <remarks>
    /// The column is a signed integer whose check constraint already refuses zero and whose update
    /// trigger already refuses a decrease, so both bounds below describe a row this product does not
    /// write. They are checked anyway because the cost of being wrong is asymmetric: a refused read
    /// is a message, and an accepted one is a launch committed to a target the database will reject
    /// once there is no ordinary access left to fall back to.
    /// </remarks>
    private static bool TryReadAdvanceableEpoch(SqliteDataReader reader, int ordinal, out ulong epoch)
    {

        epoch = 0;

        if (reader.IsDBNull(ordinal))
        {

            return false;

        }

        long value = reader.GetInt64(ordinal);

        if (value is <= 0 or long.MaxValue)
        {

            return false;

        }

        epoch = (ulong)value;

        return true;

    }

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

    /// <summary>
    /// Reads one bounded snapshot on the closed period's own maintenance connection.
    /// </summary>
    /// <remarks>
    /// The same bounded shape as the ordinary route — one connection, one transaction, nothing left
    /// open — over a handle the admission gate authorized for exactly this purpose. It is read-only
    /// and it is not the exclusive maintenance handle the destructive phases take, because an
    /// inventory page has no business being able to write and taking an exclusive lock to count rows
    /// would contend with the erasure standing beside it.
    /// </remarks>
    private static async Task<Result<T>> WithClosedSnapshotAsync<T>(
        CovenantClosedPeriodAuthority authority,
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<Result<T>>> work,
        CancellationToken cancellationToken)
    {

        Result<IGrimoireMaintenanceConnectionLease> opened =
            await authority.OpenInventorySnapshotAsync(cancellationToken).ConfigureAwait(false);

        if (opened.IsFailure)
        {

            return Result<T>.Failure(UnsafeInventory);

        }

        await using (opened.Value.ConfigureAwait(false))
        {

            SqliteTransaction? transaction = null;

            try
            {

                transaction = (SqliteTransaction)await opened.Value.Connection
                    .BeginTransactionAsync(cancellationToken)
                    .ConfigureAwait(false);

                return await work(opened.Value.Connection, transaction, cancellationToken)
                    .ConfigureAwait(false);

            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {

                throw;

            }
            catch
            {

                return Result<T>.Failure(UnsafeInventory);

            }
            finally
            {

                if (transaction is not null)
                {

                    await transaction.DisposeAsync().ConfigureAwait(false);

                }

            }

        }

    }

    /// <summary>
    /// Reads one bounded snapshot, through the closed period's own route when there is one.
    /// </summary>
    /// <remarks>
    /// Two routes because there are two moments. The launch-time read happens before anything is
    /// closed and takes the ordinary one; every read inside a closed period has to take the
    /// maintenance one, because ordinary admission is shut for the exact generation this erasure is
    /// erasing and the gate refuses an ordinary open outright. A single route would mean either
    /// refusing the launch read or leaving a way around the closed period, and the second is
    /// indistinguishable from having no closed period at all.
    /// </remarks>
    private async Task<Result<T>> WithOwnedSnapshotAsync<T>(
        CovenantClosedPeriodAuthority? authority,
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<Result<T>>> work,
        CancellationToken cancellationToken)
    {

        if (authority is not null)
        {

            return await WithClosedSnapshotAsync(authority, work, cancellationToken)
                .ConfigureAwait(false);

        }

        IGrimoireOrdinaryConnectionLease? lease = null;

        SqliteTransaction? transaction = null;

        Result<T> result = Result<T>.Failure(UnsafeInventory);

        bool callerCancelled = false;

        try
        {

            Result<IGrimoireOrdinaryConnectionLease> acquired = await _connections
                .OpenFreshAsync(
                    GrimoireOrdinaryFreshConnectionKind.ReadOnly,
                    cancellationToken)
                .ConfigureAwait(false);

            if (acquired.IsFailure)
            {

                return Result<T>.Failure(UnsafeInventory);

            }

            lease = acquired.Value;

            SqliteConnection connection = lease.Connection;

            transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            result = await work(connection, transaction, cancellationToken).ConfigureAwait(false);

            await GrimoireScopedConsumerTestSeam.PauseAsync(
                "CovenantErasureInventorySource.WithOwnedSnapshotAsync",
                GrimoireScopedConsumerFinalUseKind.ReaderMaterialized,
                result.IsSuccess ? 1 : 0,
                cancellationToken).ConfigureAwait(false);

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            callerCancelled = true;

        }
        catch
        {

            result = Result<T>.Failure(UnsafeInventory);

        }

        bool cleaned = await CleanupAsync(transaction, lease).ConfigureAwait(false);

        if (callerCancelled)
        {

            cancellationToken.ThrowIfCancellationRequested();

        }

        return cleaned ? result : Result<T>.Failure(UnsafeInventory);

    }

    private async Task<Result> WithOwnedSnapshotAsync(
        CovenantClosedPeriodAuthority? authority,
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<Result>> work,
        CancellationToken cancellationToken)
    {

        Result<Unit> result = await WithOwnedSnapshotAsync(
            authority,
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
        IGrimoireOrdinaryConnectionLease? lease)
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

        if (lease is not null)
        {

            try
            {

                await lease.DisposeAsync().ConfigureAwait(false);

            }
            catch
            {

                cleaned = false;

            }

        }

        return cleaned;

    }

    private readonly record struct Unit
    {

        internal static Unit Value { get; } = new();

    }

}
