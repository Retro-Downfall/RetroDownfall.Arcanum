using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// What one checked <c>wal_checkpoint(TRUNCATE)</c> reported, read rather than assumed.
/// </summary>
/// <remarks>
/// The pragma answers with a single row of three integers and the shutdown checkpointer throws all
/// three away. That is correct for a shutdown, which only wants the log small, and useless as the
/// proof an erasure needs: a checkpoint that was refused as busy, and a checkpoint that moved every
/// frame, both return without raising anything.
///
/// <para>A negative <see cref="RemainingFrames"/> is the engine saying there is no write-ahead log at
/// all — the state a database left in delete journalling reports. That is a clean answer rather than
/// a failure: a log that does not exist has no frames left in it.</para>
/// </remarks>
internal readonly record struct CovenantWalCheckpointOutcome(
    long Busy,
    long RemainingFrames,
    long CheckpointedFrames)
{

    /// <summary>
    /// Projects the pragma's one row positionally.
    /// </summary>
    /// <remarks>
    /// By ordinal because the pragma's column names are the engine's rather than a contract, and by
    /// its own method so a suite can prove the three ordinals are not transposed against a row whose
    /// three values differ.
    /// </remarks>
    internal static CovenantWalCheckpointOutcome Project(SqliteDataReader reader)
    {

        ArgumentNullException.ThrowIfNull(reader);

        return new CovenantWalCheckpointOutcome(
            Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
            Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
            Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture));

    }

    /// <summary>
    /// Refuses a checkpoint that was blocked, or that left a frame behind.
    /// </summary>
    internal Result RequireTruncated()
    {

        if (Busy != 0)
        {

            return new Error(
                ErrorCodes.Covenant.ErasureIncomplete,
                "A Covenant write-ahead-log checkpoint was refused as busy, so the erased pages "
                + "cannot be proven gone and local erasure is incomplete.");

        }

        if (RemainingFrames > 0)
        {

            return new Error(
                ErrorCodes.Covenant.ErasureIncomplete,
                $"A Covenant write-ahead-log checkpoint moved {CheckpointedFrames.ToString(CultureInfo.InvariantCulture)} "
                + $"frames and left {RemainingFrames.ToString(CultureInfo.InvariantCulture)} in the log, "
                + "so local erasure is incomplete.");

        }

        return Result.Success();

    }

}

/// <summary>
/// What one compaction left behind, measured rather than inferred from the fact that it ran.
/// </summary>
/// <remarks>
/// Two independent questions, because compaction can fail either of them alone. A non-empty free list
/// means the engine is still holding pages it has stopped accounting for; a file longer than the
/// pages it declares means the file holds bytes the engine will never read again and will never
/// overwrite. Either one is a page an erasure cannot say anything about.
/// </remarks>
internal readonly record struct CovenantCompactionMeasurement(
    long FreelistPages,
    long PageCount,
    long PageSize,
    long FileLength)
{

    /// <summary>Whether the file is exactly the pages it accounts for, and nothing else.</summary>
    internal bool IsProven =>
        FreelistPages == 0
        && PageCount > 0
        && PageSize > 0
        && PageCount * PageSize == FileLength;

}

/// <summary>
/// A candidate export that has been opened under this installation's key and proven intact, whole,
/// and compact.
/// </summary>
/// <remarks>
/// A value the verification produces rather than a boolean it returns, because installing a candidate
/// then takes an argument that can only come from having verified one. "Verify before you replace"
/// was otherwise the ordering of two statements at a single call site, and an ordering is exactly the
/// kind of rule that survives a refactor by quietly disappearing from it.
/// </remarks>
internal sealed class CovenantVerifiedExport(string stagingPath)
{

    /// <summary>The file this proof was made about, and the only one it authorizes installing.</summary>
    internal string StagingPath { get; } = stagingPath;

}

/// <summary>The candidate dataset identity and envelope master this erasure created.</summary>
internal sealed record CovenantCandidateDatasetState(
    Guid DatasetGeneration,
    long EnvelopeMasterKeyVersion,
    byte[] EnvelopeMasterKeyFingerprint,
    long EnvelopeKeyEpoch,
    long AppliedCampaignDeletionSequence,
    long AppliedSessionDeletionSequence);

/// <summary>The installation authority the candidate dataset has to agree with.</summary>
internal sealed record CovenantCandidateAuthorityState(
    string InstallationIdentity,
    long AuthorityEpoch,
    long CurrentMasterKeyVersion,
    byte[] CurrentMasterKeyFingerprint,
    long RecoveryEnvelopeEpoch);

/// <summary>The Covenant family's own row in the shared per-capability cleanup cursor.</summary>
internal sealed record CovenantCandidateCapabilityState(
    long AppliedCampaignSequence,
    long AppliedSessionSequence,
    bool FullSweepRequired);

/// <summary>
/// Everything one read-only verified reopen read, on the handle that read it.
/// </summary>
/// <remarks>
/// Returned rather than discarded because the reopen is the last read this erasure is allowed to make
/// through a handle that cannot create a sidecar. Anything a later step needs from the candidate
/// database has to come from here, or that step reopens the Grimoire the ordinary way and undoes the
/// absence proof standing beside it (§10.20.6).
/// </remarks>
internal sealed record CovenantVerifiedCandidateState(
    CovenantCandidateDatasetState Dataset,
    CovenantCandidateAuthorityState Authority,
    CovenantCandidateCapabilityState Capability);

/// <summary>
/// The second half of the <c>ICovenantErasureTransition</c> seam: everything between a committed
/// canonical erasure and the moment anything about it may be published.
/// </summary>
/// <remarks>
/// It publishes nothing and decides nothing about admission. Its whole responsibility is to be able
/// to say, from evidence it read rather than from steps it ran, that the bytes the canonical
/// transaction stopped referencing are gone (§10.20.6).
/// </remarks>
internal interface ICovenantLocalErasureStorageHealth
{

    /// <summary>Clears every pool, drains direct handles, and proves no live sidecar survived.</summary>
    Task<Result> CloseHandlesAsync(CancellationToken cancellationToken);

    /// <summary>Runs a checked <c>wal_checkpoint(TRUNCATE)</c>, refusing on busy or a leftover frame.</summary>
    Task<Result> TruncateWalAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Inventories residual artifacts, then compacts, falling back to a verified SQLCipher
    /// export-and-atomic-replace when compaction alone cannot prove the freed pages are gone.
    /// </summary>
    Task<Result> CompactAsync(CancellationToken cancellationToken);

    /// <summary>Installs the empty accelerator's own configuration and runs rank-1 integrity over it.</summary>
    Task<Result> InitializeAcceleratorAsync(CancellationToken cancellationToken);

    /// <summary>Drains again and proves every residual artifact class absent.</summary>
    Task<Result> VerifySidecarAbsenceAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reopens the unpublished candidate read-only on a handle that cannot create a write-ahead log
    /// or a wal-index, verifies its dataset, master, authority, and capability state, and closes it.
    /// </summary>
    Task<Result<CovenantVerifiedCandidateState>> VerifyReopenAsync(CancellationToken cancellationToken);

}

/// <summary>
/// The storage-health proof a Covenant erasure has to pass before anything about it is published.
/// </summary>
/// <remarks>
/// Every step here is a measurement, and the ones that look like actions are measurements too. The
/// checkpoint is checked rather than best effort, the compaction is proven against the file's own
/// length rather than assumed from the fact that <c>VACUUM</c> returned, and the absence of every
/// sidecar is asserted by enumerating what is there rather than by deleting a list of names. A helper
/// that only deleted could not tell "there was nothing to remove" from "the removal failed and nobody
/// looked", and those are opposite answers to the question this type exists to settle.
///
/// <para>Every step drains first. The steps run as separate durable phases, so a resumed erasure
/// re-enters one of them in a process that has since opened the Grimoire the ordinary way, and a
/// proof made beside a live handle is a proof about a file somebody else is still writing to.</para>
///
/// <para>Nothing here takes, completes, or disposes a lease, and nothing publishes. The coordinator
/// holds exactly one exclusive lease for the whole operation, and a storage owner that could acquire
/// a second would be able to reopen admission underneath the operation that closed it.</para>
/// </remarks>
internal sealed class CovenantLocalErasureStorageHealth : ICovenantLocalErasureStorageHealth
{

    /// <summary>The attachment name an export writes through. A fixed identifier, never input.</summary>
    private const string ExportAlias = "covenant_erasure_export";

    /// <summary>The Covenant family's row in the shared per-capability cleanup cursor.</summary>
    private const long CovenantFamilyCode = (long)GrimoireSchemaFamily.Covenant;

    private readonly ICovenantMaintenanceConnectionFactory _connections;

    private readonly ICovenantSqliteConnectionInitializer _initializer;

    private readonly ICovenantConnectionDrain _drain;

    private readonly TimeProvider _timeProvider;

    internal CovenantLocalErasureStorageHealth(
        ICovenantMaintenanceConnectionFactory connections,
        ICovenantSqliteConnectionInitializer initializer,
        ICovenantConnectionDrain drain,
        TimeProvider timeProvider)
    {

        _connections = connections ?? throw new ArgumentNullException(nameof(connections));

        _initializer = initializer ?? throw new ArgumentNullException(nameof(initializer));

        _drain = drain ?? throw new ArgumentNullException(nameof(drain));

        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    }

    public async Task<Result> CloseHandlesAsync(CancellationToken cancellationToken)
    {

        Result drained = await DrainAsync(cancellationToken).ConfigureAwait(false);

        return drained.IsFailure
            ? drained
            : RequireAbsent(CovenantResidualArtifacts.LiveHandleClasses);

    }

    public async Task<Result> TruncateWalAsync(CancellationToken cancellationToken)
    {

        Result drained = await DrainAsync(cancellationToken).ConfigureAwait(false);

        if (drained.IsFailure)
        {

            return drained;

        }

        return await WithMaintenanceConnectionAsync(
            "truncate the write-ahead log",
            async (connection, token) =>
            {

                Result<CovenantWalCheckpointOutcome> outcome =
                    await CheckpointAsync(connection, token).ConfigureAwait(false);

                return outcome.IsFailure ? Result.Failure(outcome.Error) : outcome.Value.RequireTruncated();

            },
            cancellationToken).ConfigureAwait(false);

    }

    public async Task<Result> CompactAsync(CancellationToken cancellationToken)
    {

        Result drained = await DrainAsync(cancellationToken).ConfigureAwait(false);

        if (drained.IsFailure)
        {

            return drained;

        }

        // An interrupted pass of this same step is entitled to have left a staging file behind, and
        // it is this step's own litter rather than a reason to stop: a staging file is a complete
        // encrypted copy of a database the erasure is in the middle of replacing, so the one thing
        // that must not happen is for it to survive the proof.
        Result cleared = CovenantResidualArtifacts.RemoveOwnStaging(_connections.DatabasePath);

        if (cleared.IsFailure)
        {

            return cleared;

        }

        // Everything else is refused before a single byte is rewritten. A surviving sidecar names a
        // handle still holding the database; a surviving replaced original names a previous pass that
        // installed a candidate it could not verify, and rewriting the file underneath that is
        // rewriting a destination whose identity nobody has established.
        Result residue = RequireAbsent(CovenantResidualArtifacts.Declared);

        if (residue.IsFailure)
        {

            return residue;

        }

        Result<CovenantCompactionMeasurement?> compacted =
            await VacuumAsync(cancellationToken).ConfigureAwait(false);

        if (compacted.IsFailure)
        {

            return Result.Failure(compacted.Error);

        }

        // The cheaper arm proved itself. Rewriting the file anyway would be a second irreversible
        // step taken for no reason, on a database an operator is already waiting on.
        return compacted.Value is { IsProven: true }
            ? Result.Success()
            : await ExportAndReplaceAsync(cancellationToken).ConfigureAwait(false);

    }

    public async Task<Result> InitializeAcceleratorAsync(CancellationToken cancellationToken)
    {

        Result drained = await DrainAsync(cancellationToken).ConfigureAwait(false);

        if (drained.IsFailure)
        {

            return drained;

        }

        return await WithMaintenanceConnectionAsync(
            "initialize the empty Covenant accelerator",
            InitializeAcceleratorOnConnectionAsync,
            cancellationToken).ConfigureAwait(false);

    }

    public async Task<Result> VerifySidecarAbsenceAsync(CancellationToken cancellationToken)
    {

        Result drained = await DrainAsync(cancellationToken).ConfigureAwait(false);

        // Every class, not only the ones a live handle produces. A staging or replaced file that
        // survived this far is a copy of protected state the erasure has already reported compacting.
        return drained.IsFailure ? drained : RequireAbsent(CovenantResidualArtifacts.Declared);

    }

    public async Task<Result<CovenantVerifiedCandidateState>> VerifyReopenAsync(
        CancellationToken cancellationToken)
    {

        Result drained = await DrainAsync(cancellationToken).ConfigureAwait(false);

        if (drained.IsFailure)
        {

            return Result<CovenantVerifiedCandidateState>.Failure(drained.Error);

        }

        // Before the handle, not only after it. The sidecar-free handle is opened immutable, which
        // tells the engine the file cannot change underneath it — so a write-ahead log that did exist
        // would be ignored and this verification would answer from superseded pages.
        Result before = RequireAbsent(CovenantResidualArtifacts.Declared);

        if (before.IsFailure)
        {

            return Result<CovenantVerifiedCandidateState>.Failure(before.Error);

        }

        Result<CovenantVerifiedCandidateState> verified =
            await ReadAndVerifyCandidateAsync(cancellationToken).ConfigureAwait(false);

        if (verified.IsFailure)
        {

            return verified;

        }

        Result after = RequireAbsent(CovenantResidualArtifacts.Declared);

        return after.IsFailure ? Result<CovenantVerifiedCandidateState>.Failure(after.Error) : verified;

    }

    /// <summary>
    /// Writes a fresh database with <c>sqlcipher_export</c>, verifies it, and installs it through the
    /// shared atomic-replace primitive.
    /// </summary>
    /// <remarks>
    /// Internal rather than private so a suite can drive the arm end to end against a real database.
    /// The condition that selects it is a measurement of a healthy engine's own accounting, and a
    /// healthy engine does not normally produce a state that fails it — which is exactly why the
    /// selection is a measurement rather than a prediction, and why the arm it selects has to be
    /// exercised directly rather than waited for.
    ///
    /// <para>The export is verified before the destination is touched, so a candidate that cannot be
    /// proven leaves the original database exactly where it was. The one outcome that may not be
    /// reported that way is <see cref="AtomicReplaceStatus.ReplacedButUnverified"/>: the move
    /// completed and the recovery did not, so the destination may already hold the new file and
    /// nobody may claim otherwise.</para>
    /// </remarks>
    internal async Task<Result> ExportAndReplaceAsync(CancellationToken cancellationToken)
    {

        string databasePath = _connections.DatabasePath;

        string stagingPath = CovenantResidualArtifacts.ExportStagingPath(databasePath);

        try
        {

            Result exported = await ExportAsync(stagingPath, cancellationToken).ConfigureAwait(false);

            if (exported.IsFailure)
            {

                return exported;

            }

            Result<CovenantVerifiedExport> verified =
                await VerifyExportAsync(stagingPath, cancellationToken).ConfigureAwait(false);

            return verified.IsFailure
                ? Result.Failure(verified.Error)
                : await ReplaceAsync(verified.Value, cancellationToken).ConfigureAwait(false);

        }
        finally
        {

            _ = CovenantResidualArtifacts.RemoveOwnStaging(databasePath);

        }

    }

    /// <summary>
    /// Writes the whole database out through <c>sqlcipher_export</c> under this installation's key.
    /// </summary>
    /// <remarks>
    /// A fresh file rather than a rewrite in place, because that is the only compaction whose result
    /// does not depend on the engine's accounting of the file it started from: every page in the
    /// staging file was written by this export, so there is no page in it that an erasure has to take
    /// somebody's word for.
    /// </remarks>
    internal async Task<Result> ExportAsync(string stagingPath, CancellationToken cancellationToken)
    {

        ArgumentException.ThrowIfNullOrEmpty(stagingPath);

        return await WithMaintenanceConnectionAsync(
            "export the Covenant database",
            async (connection, token) =>
            {

                await _connections
                    .AttachSideFileAsync(connection, ExportAlias, stagingPath, token)
                    .ConfigureAwait(false);

                try
                {

                    await using SqliteCommand command = connection.CreateCommand();

                    command.CommandText = $"SELECT sqlcipher_export('{ExportAlias}');";

                    _ = await command.ExecuteScalarAsync(token).ConfigureAwait(false);

                }
                finally
                {

                    await using SqliteCommand detach = connection.CreateCommand();

                    detach.CommandText = $"DETACH DATABASE {ExportAlias};";

                    _ = await detach.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                }

                return Result.Success();

            },
            cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// Proves the exported candidate opens under this installation's key, is intact, and is compact,
    /// before anything replaces the database that is still working.
    /// </summary>
    internal async Task<Result<CovenantVerifiedExport>> VerifyExportAsync(
        string stagingPath,
        CancellationToken cancellationToken)
    {

        ArgumentException.ThrowIfNullOrEmpty(stagingPath);

        if (!File.Exists(stagingPath))
        {

            return Unverified("the export wrote no candidate database");

        }

        SqliteConnection connection;

        try
        {

            connection = await _connections.OpenSideFileAsync(stagingPath, cancellationToken)
                .ConfigureAwait(false);

        }
        catch (SqliteException failed)
        {

            return Unverified(failed.Message);

        }

        await using (connection.ConfigureAwait(false))
        {

            try
            {

                await _initializer
                    .InitializeAsync(connection, CovenantSqliteConnectionMode.ReadOnly, cancellationToken)
                    .ConfigureAwait(false);

                Result intact = await RequireCipherIntegrityAsync(connection, cancellationToken)
                    .ConfigureAwait(false);

                if (intact.IsFailure)
                {

                    return Result<CovenantVerifiedExport>.Failure(intact.Error);

                }

                string? structural = await ScalarStringAsync(
                    connection,
                    "PRAGMA integrity_check;",
                    cancellationToken).ConfigureAwait(false);

                if (!string.Equals(structural, "ok", StringComparison.Ordinal))
                {

                    return Unverified("the exported database did not report structural integrity");

                }

                CovenantCompactionMeasurement measured = await MeasureAsync(
                    connection,
                    stagingPath,
                    cancellationToken).ConfigureAwait(false);

                if (!measured.IsProven)
                {

                    return Unverified("the exported database is not exactly the pages it accounts for");

                }

                // A database with no canonical singleton is one nothing in this installation can
                // resume from, and installing it would replace a working file with a candidate whose
                // identity nobody checked.
                long singletons = await ScalarLongAsync(
                    connection,
                    "SELECT COUNT(*) FROM covenant_state WHERE StateKey = 1;",
                    cancellationToken).ConfigureAwait(false);

                return singletons == 1
                    ? Result<CovenantVerifiedExport>.Success(new CovenantVerifiedExport(stagingPath))
                    : Unverified("the exported database carries no Covenant canonical singleton");

            }
            catch (SqliteException failed)
            {

                return Unverified(failed.Message);

            }
            catch (InvalidOperationException failed)
            {

                return Unverified(failed.Message);

            }

        }

    }

    /// <summary>
    /// Installs a verified candidate through the one atomic-replace primitive, and re-establishes the
    /// journalling mode an exported database does not carry.
    /// </summary>
    /// <remarks>
    /// The primitive's own outcome is the answer rather than an exception, because it distinguishes
    /// the three cases that matter to an erasure: nothing moved, something moved and was put back,
    /// and something moved that could not be put back. Only the third leaves a database an operator
    /// has to be told about by name.
    ///
    /// <para><c>sqlcipher_export</c> writes a rollback-journalled database, so the installed file
    /// would otherwise carry a journalling mode this installation never chose. It is reopened here in
    /// exactly the mode the installation opens its Grimoire in, through the one component that owns
    /// connection policy and reads it back, rather than left for the first ordinary connection to
    /// change the mode underneath a proof that has already been made.</para>
    /// </remarks>
    internal async Task<Result> ReplaceAsync(
        CovenantVerifiedExport verified,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(verified);

        string stagingPath = verified.StagingPath;

        string databasePath = _connections.DatabasePath;

        AtomicReplaceStatus status;

        try
        {

            status = await AtomicFile.ReplaceAsync(
                databasePath,
                CovenantResidualArtifacts.ReplacementStagingPath(databasePath),
                async (destination, token) =>
                {

                    await using FileStream source = new(
                        stagingPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        bufferSize: 4096,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);

                    await source.CopyToAsync(destination, token).ConfigureAwait(false);

                },
                cancellationToken).ConfigureAwait(false);

        }
        catch (Exception failed) when (failed is IOException or UnauthorizedAccessException)
        {

            return Unverifiable(failed.Message);

        }

        switch (status)
        {

            case AtomicReplaceStatus.Succeeded:

                break;

            case AtomicReplaceStatus.ReplacedButUnverified:

                return new Error(
                    ErrorCodes.Covenant.ManualRecoveryRequired,
                    "A Covenant export replaced the Grimoire and could not verify the result or "
                    + "recover the original, so an operator has to establish which database is in "
                    + "place before anything else runs.");

            default:

                return Unverifiable(
                    $"the atomic replace reported {status.ToString()} and the original is left in place");

        }

        // Opened in exactly the mode this installation opens its Grimoire in, so the one component
        // that owns connection policy is the one that puts the journalling mode back and proves it
        // took. Restating the pragma here would be a second opinion about a setting that already has
        // an owner, and the read-back is the whole value of having one.
        return await WithMaintenanceConnectionAsync(
            "restore write-ahead logging on the replaced Covenant database",
            static (_, _) => Task.FromResult(Result.Success()),
            cancellationToken,
            CovenantSqliteConnectionMode.ReadWrite).ConfigureAwait(false);

    }

    /// <summary>
    /// Compacts in place and measures the result, or reports that the compaction itself did not run.
    /// </summary>
    /// <remarks>
    /// A null measurement means <c>VACUUM</c> did not complete, which is not on its own a failure:
    /// it is the first of the two ways compaction can fail to prove itself, and both select the same
    /// remedy.
    ///
    /// <para>Internal rather than private so a suite can assert which of the two arms a healthy
    /// database takes. The fresh-file arm produces a correct result either way, so a proof that only
    /// looked at the finished file would pass whether or not the cheaper arm ever ran.</para>
    /// </remarks>
    internal async Task<Result<CovenantCompactionMeasurement?>> VacuumAsync(CancellationToken cancellationToken)
    {

        CovenantCompactionMeasurement? measurement = null;

        Result compacted = await WithMaintenanceConnectionAsync(
            "compact the Covenant database",
            async (connection, token) =>
            {

                try
                {

                    await using SqliteCommand command = connection.CreateCommand();

                    command.CommandText = "VACUUM;";

                    _ = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                }
                catch (SqliteException)
                {

                    // The engine could not rewrite the file in place. The fresh-file arm does not
                    // depend on it being able to, so this is a fall-through rather than a refusal.
                    return Result.Success();

                }

                Result<CovenantWalCheckpointOutcome> outcome =
                    await CheckpointAsync(connection, token).ConfigureAwait(false);

                if (outcome.IsFailure)
                {

                    return Result.Failure(outcome.Error);

                }

                Result truncated = outcome.Value.RequireTruncated();

                if (truncated.IsFailure)
                {

                    return truncated;

                }

                measurement = await MeasureAsync(connection, _connections.DatabasePath, token)
                    .ConfigureAwait(false);

                return Result.Success();

            },
            cancellationToken).ConfigureAwait(false);

        return compacted.IsFailure
            ? Result<CovenantCompactionMeasurement?>.Failure(compacted.Error)
            : Result<CovenantCompactionMeasurement?>.Success(measurement);

    }

    /// <summary>
    /// Prepares the empty accelerator with the same initializer a fresh install runs.
    /// </summary>
    /// <remarks>
    /// The same initializer rather than a third copy of its statements. FTS5 secure delete and rank-1
    /// integrity are properties of the index rather than of the database, and an index only this path
    /// knew how to prepare would be an index only this path could prove.
    ///
    /// <para>An installation whose optional accelerator tier is not installed has no index to prepare
    /// and is not a failure. The canonical tier commits without one by design, and refusing here
    /// would make a degraded installation impossible to reset.</para>
    /// </remarks>
    private async Task<Result> InitializeAcceleratorOnConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        if (!await ObjectExistsAsync(connection, "covenant_fts", cancellationToken).ConfigureAwait(false))
        {

            return Result.Success();

        }

        Result<CovenantCandidateAuthorityState> authority =
            await ReadAuthorityAsync(connection, cancellationToken).ConfigureAwait(false);

        if (authority.IsFailure)
        {

            return Result.Failure(authority.Error);

        }

        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {

            await new CovenantAcceleratorSchemaDataInitializer()
                .InitializeAsync(
                    connection,
                    transaction,
                    new GrimoireSchemaInitializationContext(
                        authority.Value.InstallationIdentity,
                        authority.Value.AuthorityEpoch,
                        checked((uint)authority.Value.CurrentMasterKeyVersion),
                        authority.Value.CurrentMasterKeyFingerprint,
                        authority.Value.RecoveryEnvelopeEpoch,
                        _timeProvider.GetUtcNow()),
                    cancellationToken)
                .ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return Result.Success();

        }
        catch (Exception failed) when (failed is SqliteException or InvalidOperationException)
        {

            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                $"The empty Covenant accelerator did not pass rank-1 integrity: {failed.Message}");

        }

    }

    /// <summary>
    /// Opens the sidecar-free handle, reads the candidate's four states, and refuses any disagreement.
    /// </summary>
    private async Task<Result<CovenantVerifiedCandidateState>> ReadAndVerifyCandidateAsync(
        CancellationToken cancellationToken)
    {

        SqliteConnection connection;

        try
        {

            connection = await _connections.OpenSidecarFreeReadOnlyAsync(cancellationToken)
                .ConfigureAwait(false);

        }
        catch (SqliteException failed)
        {

            return Result<CovenantVerifiedCandidateState>.Failure(
                new Error(
                    ErrorCodes.Covenant.ErasureIncomplete,
                    $"A Covenant erasure could not reopen its candidate database read-only: {failed.Message}"));

        }

        await using (connection.ConfigureAwait(false))
        {

            try
            {

                await _initializer
                    .InitializeAsync(connection, CovenantSqliteConnectionMode.ReadOnly, cancellationToken)
                    .ConfigureAwait(false);

                Result<CovenantVerifiedCandidateState> verified =
                    await VerifyCandidateAsync(connection, cancellationToken).ConfigureAwait(false);

                // While the handle is still open, not only once it has closed. An ordinary read-only
                // connection creates a wal-index to read a write-ahead-logged database and removes it
                // again on close, so a proof made after the close would report the absence of a file
                // that existed for the whole of the read it was proving.
                Result open = RequireAbsent(CovenantResidualArtifacts.Declared);

                return open.IsFailure
                    ? Result<CovenantVerifiedCandidateState>.Failure(open.Error)
                    : verified;

            }
            catch (Exception failed) when (failed is SqliteException or InvalidOperationException)
            {

                return Result<CovenantVerifiedCandidateState>.Failure(
                    new Error(
                        ErrorCodes.Covenant.IntegrityFailure,
                        $"A Covenant erasure could not verify its candidate database: {failed.Message}"));

            }

        }

    }

    private async Task<Result<CovenantVerifiedCandidateState>> VerifyCandidateAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        Result<CovenantCandidateDatasetState> dataset =
            await ReadDatasetAsync(connection, cancellationToken).ConfigureAwait(false);

        if (dataset.IsFailure)
        {

            return Result<CovenantVerifiedCandidateState>.Failure(dataset.Error);

        }

        Result<CovenantCandidateAuthorityState> authority =
            await ReadAuthorityAsync(connection, cancellationToken).ConfigureAwait(false);

        if (authority.IsFailure)
        {

            return Result<CovenantVerifiedCandidateState>.Failure(authority.Error);

        }

        Result<CovenantCandidateCapabilityState> capability =
            await ReadCapabilityAsync(connection, cancellationToken).ConfigureAwait(false);

        if (capability.IsFailure)
        {

            return Result<CovenantVerifiedCandidateState>.Failure(capability.Error);

        }

        // The candidate's envelope master may lag the installation authority and may not lead it.
        // Lagging is ordinary: the canonical singleton is reconciled to a rotated master by the next
        // mutation that needs one, and an erased family has no next mutation. Leading is not
        // reachable by any writer, so a candidate that leads names a master key the authority row
        // cannot resolve, and publishing it would authorize envelopes nothing can open.
        if (dataset.Value.EnvelopeMasterKeyVersion > authority.Value.CurrentMasterKeyVersion)
        {

            return Mismatch("its envelope master version is ahead of the installation authority");

        }

        // Both cleanup cursors move to the same journal position. A reset that moved only one leaves
        // the two disagreeing about the same journal, and the next sweep replays deletions against a
        // dataset with no rows to delete.
        if (dataset.Value.AppliedCampaignDeletionSequence != capability.Value.AppliedCampaignSequence
            || dataset.Value.AppliedSessionDeletionSequence != capability.Value.AppliedSessionSequence)
        {

            return Mismatch("its two owner-deletion cursors disagree");

        }

        Result empty = await RequireFamilyEmptyAsync(connection, cancellationToken).ConfigureAwait(false);

        return empty.IsFailure
            ? Result<CovenantVerifiedCandidateState>.Failure(empty.Error)
            : Result<CovenantVerifiedCandidateState>.Success(
                new CovenantVerifiedCandidateState(dataset.Value, authority.Value, capability.Value));

    }

    /// <summary>
    /// Re-reads the family the canonical transaction emptied, through a handle that cannot have
    /// cached its result.
    /// </summary>
    /// <remarks>
    /// The same list the transaction deleted through, rather than a second one. Two lists could only
    /// ever differ in the case that matters: a table the erasure stopped naming would also be a table
    /// the proof stopped counting.
    /// </remarks>
    private static async Task<Result> RequireFamilyEmptyAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        foreach (string table in CovenantCanonicalErasureTransaction.FamilyTables)
        {

            if (!await ObjectExistsAsync(connection, table, cancellationToken).ConfigureAwait(false))
            {

                continue;

            }

            long rows = await ScalarLongAsync(
                connection,
                $"SELECT COUNT(*) FROM \"{table}\";",
                cancellationToken).ConfigureAwait(false);

            if (rows != 0)
            {

                return new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    $"The reopened Covenant candidate still holds rows in {table}, so the erasure it "
                    + "reports is not the one on disk.");

            }

        }

        return Result.Success();

    }

    private static async Task<Result<CovenantCandidateDatasetState>> ReadDatasetAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT DatasetGeneration,
                   EnvelopeMasterKeyVersion,
                   EnvelopeMasterKeyFingerprint,
                   EnvelopeKeyEpoch,
                   AppliedCampaignDeletionSequence,
                   AppliedSessionDeletionSequence
            FROM covenant_state
            WHERE StateKey = 1;
            """;

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            return Result<CovenantCandidateDatasetState>.Failure(
                new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    "The reopened Covenant candidate carries no canonical singleton."));

        }

        byte[] generation = (byte[])reader.GetValue(0);

        byte[] fingerprint = (byte[])reader.GetValue(2);

        if (generation.Length != 16 || generation.AsSpan().SequenceEqual(new byte[16]))
        {

            return Result<CovenantCandidateDatasetState>.Failure(
                new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    "The reopened Covenant candidate carries no dataset generation."));

        }

        return Result<CovenantCandidateDatasetState>.Success(
            new CovenantCandidateDatasetState(
                new Guid(generation),
                Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                fingerprint,
                Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture),
                Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture),
                Convert.ToInt64(reader.GetValue(5), CultureInfo.InvariantCulture)));

    }

    private static async Task<Result<CovenantCandidateAuthorityState>> ReadAuthorityAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT InstallationIdentity,
                   AuthorityEpoch,
                   CurrentMasterKeyVersion,
                   CurrentMasterKeyFingerprint,
                   RecoveryEnvelopeEpoch
            FROM covenant_authority_state
            WHERE StateKey = 1;
            """;

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            return Result<CovenantCandidateAuthorityState>.Failure(
                new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    "The reopened Covenant candidate carries no installation authority row."));

        }

        return Result<CovenantCandidateAuthorityState>.Success(
            new CovenantCandidateAuthorityState(
                reader.GetString(0),
                Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
                (byte[])reader.GetValue(3),
                Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture)));

    }

    private static async Task<Result<CovenantCandidateCapabilityState>> ReadCapabilityAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT AppliedCampaignSequence, AppliedSessionSequence, FullSweepRequired
            FROM capability_cleanup_state
            WHERE CapabilityFamilyCode = $family;
            """;

        _ = command.Parameters.AddWithValue("$family", CovenantFamilyCode);

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            return Result<CovenantCandidateCapabilityState>.Failure(
                new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    "The reopened Covenant candidate carries no capability cleanup cursor."));

        }

        return Result<CovenantCandidateCapabilityState>.Success(
            new CovenantCandidateCapabilityState(
                Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture) == 1));

    }

    private static async Task<Result<CovenantWalCheckpointOutcome>> CheckpointAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.FieldCount != 3)
        {

            return Result<CovenantWalCheckpointOutcome>.Failure(
                new Error(
                    ErrorCodes.Covenant.ErasureIncomplete,
                    "A Covenant write-ahead-log checkpoint reported nothing, so it cannot be taken as "
                    + "proof that no frame remains."));

        }

        return Result<CovenantWalCheckpointOutcome>.Success(CovenantWalCheckpointOutcome.Project(reader));

    }

    private static async Task<CovenantCompactionMeasurement> MeasureAsync(
        SqliteConnection connection,
        string path,
        CancellationToken cancellationToken)
    {

        long freelist = await ScalarLongAsync(connection, "PRAGMA freelist_count;", cancellationToken)
            .ConfigureAwait(false);

        long pages = await ScalarLongAsync(connection, "PRAGMA page_count;", cancellationToken)
            .ConfigureAwait(false);

        long pageSize = await ScalarLongAsync(connection, "PRAGMA page_size;", cancellationToken)
            .ConfigureAwait(false);

        long length = File.Exists(path) ? new FileInfo(path).Length : 0;

        return new CovenantCompactionMeasurement(freelist, pages, pageSize, length);

    }

    private static async Task<Result> RequireCipherIntegrityAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "PRAGMA cipher_integrity_check;";

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        // SQLCipher reports problems as rows and returns nothing when the database is intact, so an
        // empty result is the pass condition and any row must say ok.
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            if (!string.Equals(reader.GetString(0), "ok", StringComparison.OrdinalIgnoreCase))
            {

                return Unverifiable("the exported database failed its cipher integrity check");

            }

        }

        return Result.Success();

    }

    private static async Task<bool> ObjectExistsAsync(
        SqliteConnection connection,
        string name,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT 1 FROM sqlite_master WHERE \"name\" = $name LIMIT 1;";

        _ = command.Parameters.AddWithValue("$name", name);

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is not null and not DBNull;

    }

    private static async Task<long> ScalarLongAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull ? 0L : Convert.ToInt64(value, CultureInfo.InvariantCulture);

    }

    private static async Task<string?> ScalarStringAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);

    }

    private static Result<CovenantVerifiedCandidateState> Mismatch(string detail) =>
        Result<CovenantVerifiedCandidateState>.Failure(
            new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                $"The reopened Covenant candidate cannot be published because {detail}."));

    private static Result<CovenantVerifiedExport> Unverified(string detail) =>
        Result<CovenantVerifiedExport>.Failure(Unverifiable(detail));

    private static Error Unverifiable(string detail) =>
        new(
            ErrorCodes.Covenant.ErasureIncomplete,
            $"A Covenant export could not be verified: {detail}. The original database is left in place.");

    /// <summary>
    /// Reports which classes of residual artifact exist, and refuses without naming a file.
    /// </summary>
    private Result RequireAbsent(IReadOnlyList<CovenantResidualArtifactClass> classes)
    {

        List<CovenantResidualArtifactClass> survivors =
        [
            .. CovenantResidualArtifacts.Survivors(_connections.DatabasePath).Where(classes.Contains),
        ];

        return survivors.Count == 0
            ? Result.Success()
            : new Error(
                ErrorCodes.Covenant.ErasureIncomplete,
                $"A Covenant erasure left {CovenantResidualArtifacts.Describe(survivors)} beside the "
                + "Grimoire, so local erasure is incomplete.");

    }

    /// <summary>
    /// Drains, and reports a drain that could not prove itself as the incomplete erasure it is.
    /// </summary>
    /// <remarks>
    /// The drain's own code says a maintenance step failed, which is true and is not what an operator
    /// needs here: a handle this process could not close is a handle still holding the database, and
    /// the erasure standing behind it is incomplete whatever the drain calls it internally.
    /// </remarks>
    private async Task<Result> DrainAsync(CancellationToken cancellationToken)
    {

        Result drained = await _drain.DrainAsync(cancellationToken).ConfigureAwait(false);

        return drained.IsSuccess
            ? drained
            : new Error(ErrorCodes.Covenant.ErasureIncomplete, drained.Error.Message);

    }

    private async Task<Result> WithMaintenanceConnectionAsync(
        string step,
        Func<SqliteConnection, CancellationToken, Task<Result>> work,
        CancellationToken cancellationToken,
        CovenantSqliteConnectionMode mode = CovenantSqliteConnectionMode.ExclusiveMaintenance)
    {

        SqliteConnection connection;

        try
        {

            connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        }
        catch (SqliteException failed)
        {

            return Failure(step, failed.Message);

        }

        await using (connection.ConfigureAwait(false))
        {

            try
            {

                await _initializer
                    .InitializeAsync(connection, mode, cancellationToken)
                    .ConfigureAwait(false);

                return await work(connection, cancellationToken).ConfigureAwait(false);

            }
            catch (Exception failed) when (failed is SqliteException or InvalidOperationException)
            {

                return Failure(step, failed.Message);

            }

        }

    }

    private static Error Failure(string step, string detail) =>
        new(
            ErrorCodes.Covenant.MaintenanceFailed,
            $"A Covenant erasure could not {step}: {detail}");

}
