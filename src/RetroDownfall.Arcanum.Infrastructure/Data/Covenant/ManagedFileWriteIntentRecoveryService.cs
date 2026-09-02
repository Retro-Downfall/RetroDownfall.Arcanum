using System.Collections.Immutable;

using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// One managed-file write intent, as the recovery reader reads it back.
/// </summary>
/// <remarks>
/// The pending sensitivity label projection is deliberately absent from this projection. Recovery
/// decides what to do from the phase, the recorded location, and the created-child identity; reading
/// the encrypted label into memory would load the sensitive part of a file it is about to remove, for
/// a decision that never consults it.
/// </remarks>
internal sealed record ManagedFileWriteIntentRow(
    Guid WriteOperationId,
    Guid ArtifactId,
    Guid SensitivityLabelId,
    ManagedFileWriteDurableLocationEvidence Location,
    CovenantDigest? CreatedChildPhysicalIdentityDigest,
    ManagedFileWriteIntentPhase Phase,
    long Revision);

/// <summary>
/// The reader and compare-and-swap writer for the managed-file write-intent journal.
/// </summary>
/// <remarks>
/// Separate from <see cref="LocalErasureWorkItemStore"/> because the two tables answer different
/// questions: a write intent records a file this installation was creating, and a work item records a
/// file it is removing. Folding them into one accessor would make it possible to read one and update
/// the other by mistake, and their guard triggers demand different authorization scopes.
/// </remarks>
internal static class ManagedFileWriteIntentStore
{

    private const string SelectColumns = """
        SELECT WriteOperationId, ArtifactId, SensitivityLabelId, DurableLocationEvidence,
            CreatedChildPhysicalIdentityDigest, PhaseCode, Revision
        FROM managed_file_write_intents
        """;

    /// <summary>
    /// Reads the complete write-intent inventory in canonical identity order.
    /// </summary>
    /// <remarks>
    /// Every row, not just the unfinished ones. A full installation reset has to account for each
    /// source it will later claim is terminal, and an inventory that silently omitted the rows that
    /// were already done could not tell "finished earlier" from "never seen".
    ///
    /// <para>Rows are stored as uppercase RFC-4122 text, whose lexicographic order is exactly the
    /// network-byte order the inventory vector commits to, so <c>ORDER BY WriteOperationId</c> is the
    /// canonical order rather than an approximation of it. One row past the ceiling is read on
    /// purpose, so an inventory too large to authenticate is detected rather than truncated.</para>
    /// </remarks>
    internal static async Task<Result<IReadOnlyList<ManagedFileWriteIntentRow>>> ListInventoryAsync(
        SqliteConnection connection,
        int ceiling,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ceiling);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            $"{SelectColumns} ORDER BY WriteOperationId LIMIT {ceiling + 1};";

        List<ManagedFileWriteIntentRow> rows = [];

        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            Result<ManagedFileWriteIntentRow> row = Read(reader);

            if (row.IsFailure)
            {

                return Result<IReadOnlyList<ManagedFileWriteIntentRow>>.Failure(row.Error);

            }

            rows.Add(row.Value);

        }

        return Result<IReadOnlyList<ManagedFileWriteIntentRow>>.Success(rows);

    }

    /// <summary>
    /// Terminalizes one write intent, compare-and-swapping on its exact phase and revision.
    /// </summary>
    /// <remarks>
    /// The pending label projection is cleared in the same statement, because the table refuses a
    /// terminal row that still holds one. That is the point of clearing it here: adoption is what the
    /// projection existed for, and a row that will never be adopted must not keep carrying it.
    /// </remarks>
    internal static async Task<bool> TryTerminalizeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid writeOperationId,
        ManagedFileWriteIntentPhase expectedPhase,
        long expectedRevision,
        ManagedFileWriteIntentPhase terminalPhase,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            UPDATE managed_file_write_intents
            SET PhaseCode = $terminal,
                PendingArtifactSensitivityLabel = NULL,
                Revision = Revision + 1,
                UpdatedAtUtc = $now
            WHERE WriteOperationId = $write
                AND PhaseCode = $expectedPhase
                AND Revision = $expectedRevision;
            """;

        _ = command.Parameters.AddWithValue("$terminal", (int)terminalPhase);

        _ = command.Parameters.AddWithValue("$now", Iso(utcNow));

        _ = command.Parameters.AddWithValue("$write", Format(writeOperationId));

        _ = command.Parameters.AddWithValue("$expectedPhase", (int)expectedPhase);

        _ = command.Parameters.AddWithValue("$expectedRevision", expectedRevision);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;

    }

    private static Result<ManagedFileWriteIntentRow> Read(SqliteDataReader reader)
    {

        Result<ManagedFileWriteDurableLocationEvidence> location =
            ManagedFileEvidenceCodec.DecodeWriteLocation((byte[])reader.GetValue(3));

        if (location.IsFailure)
        {

            return Result<ManagedFileWriteIntentRow>.Failure(location.Error);

        }

        CovenantDigest? createdChild = reader.IsDBNull(4)
            ? null
            : new CovenantDigest((byte[])reader.GetValue(4));

        return Result<ManagedFileWriteIntentRow>.Success(
            new ManagedFileWriteIntentRow(
                Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                Guid.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                Guid.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                location.Value,
                createdChild,
                (ManagedFileWriteIntentPhase)reader.GetInt32(5),
                reader.GetInt64(6)));

    }

    private static string Iso(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    private static string Format(Guid value) => value.ToString("D").ToUpperInvariant();

}

/// <summary>
/// Which of the two terminal outcomes recovery reached for one unfinished managed-file write.
/// </summary>
internal enum ManagedFileWriteIntentRecoveryOutcome : byte
{

    Cleaned = 1,

    ManualNonrevocable = 2,

}

/// <summary>
/// The sole terminalizer of managed-file write intents that never finished being adopted.
/// </summary>
/// <remarks>
/// A write intent between <c>Prepared</c> and <c>ParentFsynced</c> describes a file this installation
/// was in the middle of creating: a temporary child under the recorded parent, or the same child
/// already renamed onto its target leaf, with no sensitivity label created for it yet. A full
/// installation reset cannot leave either behind, and cannot adopt them either — adoption is what the
/// crashed operation was doing, and a reset has no business finishing somebody else's write.
///
/// <para>So there are exactly two outcomes, and they are the two the schema legislates. <c>Cleaned</c>
/// means both candidate children are provably gone: each one that existed carried the exact
/// created-child physical identity the producer recorded and was compare-deleted through it, and each
/// one that did not exist was proved absent through the same no-follow walk. <c>ManualNonrevocable</c>
/// means something is there that this operation may not touch — a child whose identity is not the one
/// recorded, a walk that no longer resolves, or a <c>Prepared</c> row that never created a child yet
/// has one of its leaves occupied. In that arm the file, the row's location evidence, and the parent
/// directory are all left exactly as found.</para>
///
/// <para>Content is never compared. The producer's expected content hash describes the file it
/// intended to finish writing, and a crash at <c>TempCreated</c> or <c>TempWritten</c> leaves an empty
/// or partial child that would fail that comparison while still being unambiguously ours. Physical
/// identity is what the producer recorded precisely so that this decision does not depend on how far
/// the write got.</para>
/// </remarks>
internal sealed class ManagedFileWriteIntentRecoveryService(
    ICovenantSqliteConnectionInitializer initializer,
    IManagedFileCapabilityOpener opener,
    IManagedFileOwnershipVerifier verifier,
    TimeProvider timeProvider)
{

    private readonly ICovenantSqliteConnectionInitializer _initializer =
        initializer ?? throw new ArgumentNullException(nameof(initializer));

    private readonly IManagedFileCapabilityOpener _opener =
        opener ?? throw new ArgumentNullException(nameof(opener));

    private readonly IManagedFileOwnershipVerifier _verifier =
        verifier ?? throw new ArgumentNullException(nameof(verifier));

    private readonly TimeProvider _time =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <summary>
    /// Drives one nonterminal write intent to <c>Cleaned</c> or <c>ManualNonrevocable</c>.
    /// </summary>
    /// <remarks>
    /// The caller owns the connection, holds the installation lock, and has already stopped the host.
    /// This method opens no transaction around a filesystem effect: the two children are resolved and
    /// removed first, and only then is the row advanced inside its own immediate transaction, because
    /// a transaction held across an unlink either blocks every other writer or rolls back over an
    /// effect that already happened.
    /// </remarks>
    internal async Task<Result<ManagedFileWriteIntentRecoveryOutcome>> RecoverForFullInstallationResetAsync(
        SqliteConnection connection,
        ManagedFileWriteIntentRow intent,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(intent);

        if (intent.Phase is < ManagedFileWriteIntentPhase.Prepared
            or > ManagedFileWriteIntentPhase.ParentFsynced)
        {

            return Result<ManagedFileWriteIntentRecoveryOutcome>.Failure(
                new Error(
                    ErrorCodes.Covenant.ManualArtifactErasureRequired,
                    "Only an unfinished managed-file write intent can be recovered."));

        }

        Result<ManagedFileResolvedRoot?> root = await ManagedFileRootResolver
            .ResolveAsync(connection, transaction: null, intent.Location.Target, cancellationToken)
            .ConfigureAwait(false);

        if (root.IsFailure)
        {

            return Result<ManagedFileWriteIntentRecoveryOutcome>.Failure(root.Error);

        }

        bool manual = root.Value is null;

        if (root.Value is { } resolved)
        {

            foreach (ManagedFileDurableLocationEvidence candidate in Candidates(intent.Location))
            {

                Result<bool> child = await RemoveCandidateChildAsync(
                    resolved,
                    candidate,
                    intent.CreatedChildPhysicalIdentityDigest,
                    cancellationToken).ConfigureAwait(false);

                if (child.IsFailure)
                {

                    return Result<ManagedFileWriteIntentRecoveryOutcome>.Failure(child.Error);

                }

                manual |= child.Value;

            }

        }

        ManagedFileWriteIntentRecoveryOutcome outcome = manual
            ? ManagedFileWriteIntentRecoveryOutcome.ManualNonrevocable
            : ManagedFileWriteIntentRecoveryOutcome.Cleaned;

        Result terminalized = await TerminalizeAsync(
            connection,
            intent,
            outcome,
            cancellationToken).ConfigureAwait(false);

        return terminalized.IsFailure
            ? Result<ManagedFileWriteIntentRecoveryOutcome>.Failure(terminalized.Error)
            : Result<ManagedFileWriteIntentRecoveryOutcome>.Success(outcome);

    }

    /// <summary>
    /// The two leaves one unfinished write could have left a child on, in the order it would have.
    /// </summary>
    /// <remarks>
    /// Both are always checked regardless of phase. The recorded phase says where the crash was
    /// journaled, not where it actually got to — the rename and the compare-and-swap that records it
    /// are two effects in two systems, so a row reading <c>TempFsynced</c> may already have a child on
    /// its target leaf.
    /// </remarks>
    private static ManagedFileDurableLocationEvidence[] Candidates(
        ManagedFileWriteDurableLocationEvidence location) =>
        [
            new ManagedFileDurableLocationEvidence(
                location.Target.CampaignRootIdentityDigest,
                location.Target.PathRevision,
                location.Target.NormalizedParentSegments,
                location.Target.ParentPhysicalIdentityDigest,
                location.TemporaryLeaf),
            location.Target,
        ];

    /// <summary>
    /// Removes one candidate child if it is provably this operation's, and reports whether it is not.
    /// </summary>
    /// <returns>
    /// <c>true</c> when something is present that this operation may not remove, which forces the
    /// manual arm; <c>false</c> when the leaf is now provably absent.
    /// </returns>
    private async Task<Result<bool>> RemoveCandidateChildAsync(
        ManagedFileResolvedRoot root,
        ManagedFileDurableLocationEvidence candidate,
        CovenantDigest? createdChildPhysicalIdentityDigest,
        CancellationToken cancellationToken)
    {

        Result<ManagedFileOpenOutcome> opened = await _opener
            .OpenNoFollowAsync(root, candidate, cancellationToken)
            .ConfigureAwait(false);

        if (opened.IsFailure)
        {

            return Result<bool>.Failure(opened.Error);

        }

        if (opened.Value.Kind is ManagedFileOpenKind.Absent)
        {

            return Result<bool>.Success(false);

        }

        if (opened.Value.Kind is ManagedFileOpenKind.Mismatch
            || opened.Value.Handle is not { } handle)
        {

            return Result<bool>.Success(true);

        }

        try
        {

            if (createdChildPhysicalIdentityDigest is not { } expected)
            {

                // A Prepared row never created a child, so nothing here can be proved to be ours. The
                // file stays, and an operator has to look at it.
                return Result<bool>.Success(true);

            }

            Result<ManagedFileCompareDeleteResult> deleted = await _verifier
                .CompareDeleteCreatedChildAsync(handle, expected, cancellationToken)
                .ConfigureAwait(false);

            return deleted.IsFailure
                ? Result<bool>.Failure(deleted.Error)
                : Result<bool>.Success(
                    deleted.Value is not ManagedFileCompareDeleteResult.Deleted);

        }
        finally
        {

            handle.Dispose();

        }

    }

    private async Task<Result> TerminalizeAsync(
        SqliteConnection connection,
        ManagedFileWriteIntentRow intent,
        ManagedFileWriteIntentRecoveryOutcome outcome,
        CancellationToken cancellationToken)
    {

        ManagedFileWriteIntentPhase terminal =
            outcome is ManagedFileWriteIntentRecoveryOutcome.Cleaned
                ? ManagedFileWriteIntentPhase.Cleaned
                : ManagedFileWriteIntentPhase.ManualNonrevocable;

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        try
        {

            bool advanced;

            using (_initializer.Authorize(
                connection,
                CovenantSqliteAuthorizationKind.ManagedFileIntentMutation))
            {

                advanced = await ManagedFileWriteIntentStore.TryTerminalizeAsync(
                    connection,
                    transaction,
                    intent.WriteOperationId,
                    intent.Phase,
                    intent.Revision,
                    terminal,
                    _time.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);

            }

            if (!advanced)
            {

                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                // The row moved under us. The filesystem effect above is already durable, so the honest
                // answer is that a human has to reconcile the two rather than that this pass succeeded.
                return Result.Failure(
                    new Error(
                        ErrorCodes.Covenant.ManualArtifactErasureRequired,
                        "A managed-file write intent changed while it was being terminalized."));

            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return Result.Success();

        }
        catch (SqliteException exception)
        {

            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

            return Result.Failure(
                new Error(ErrorCodes.Covenant.ManualArtifactErasureRequired, exception.Message));

        }

    }

}
