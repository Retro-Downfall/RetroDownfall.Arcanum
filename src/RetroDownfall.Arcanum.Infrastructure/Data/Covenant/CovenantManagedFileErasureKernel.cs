using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// The producer row one erasure is authorized by, as the kernel reads it inside its own transaction.
/// </summary>
internal sealed record ManagedFileProducerRow(
    Guid WriteOperationId,
    Guid ArtifactId,
    Guid SensitivityLabelId,
    long Revision,
    ManagedFileDurableLocationEvidence Location,
    ManagedFileOwnershipEvidence Ownership,
    CovenantOperationScope OwnerScope);

/// <summary>
/// The shared open, verify, compare-delete, evidence, and completion machine.
/// </summary>
/// <remarks>
/// One implementation, two entry points. The live kernel reaches it holding a caller-owned lease, and
/// pre-readiness recovery reaches it holding the installation lock and a durable work item. Both walk
/// exactly the same states in exactly the same order, because a recovery pass that had its own
/// deletion algorithm would be a second answer to "is this file Arcanum's" — and the whole reason the
/// work item exists is that the first answer was already committed (§10.17).
/// </remarks>
internal sealed class ManagedFileErasureStateMachine(
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

    private readonly TimeProvider _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <summary>
    /// Drives one durable work item from wherever it is to a terminal state.
    /// </summary>
    /// <remarks>
    /// Every filesystem effect happens between transactions, never inside one. A SQLite transaction
    /// held open across an unlink would either block every other writer for the duration or, worse,
    /// roll back after the unlink already happened.
    /// </remarks>
    internal async Task<Result<CovenantArtifactErasureProgress>> ResolveAsync(
        SqliteConnection connection,
        LocalErasureWorkItemRow item,
        CovenantSqliteAuthorizationKind authorization,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(item);

        LocalErasureWorkItemRow current = item;

        if (current.State == LocalErasureWorkItemState.Prepared)
        {

            Result<LocalErasureWorkItemRow> verified = await ProveAbsenceAsync(
                connection,
                current,
                authorization,
                cancellationToken).ConfigureAwait(false);

            if (verified.IsFailure)
            {

                return Result<CovenantArtifactErasureProgress>.Failure(verified.Error);

            }

            current = verified.Value;

        }

        if (current.State == LocalErasureWorkItemState.ManualBlocker)
        {

            // The file, its producer row, and its label are all deliberately untouched. Reporting one
            // preserved evidence row rather than one erased artifact is the honest count: nothing was
            // removed, and an operator has to look at the file itself.
            return Result<CovenantArtifactErasureProgress>.Success(
                new CovenantArtifactErasureProgress(1, 0, 1, CovenantErasureBlocker.ManualOwnershipMismatch));

        }

        if (current.State == LocalErasureWorkItemState.Completed)
        {

            return Result<CovenantArtifactErasureProgress>.Success(
                new CovenantArtifactErasureProgress(1, 1, 0, CovenantErasureBlocker.None));

        }

        return await CompleteAsync(connection, current, authorization, cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// Opens the exact recorded location, proves the file is gone, and records which proof it has.
    /// </summary>
    private async Task<Result<LocalErasureWorkItemRow>> ProveAbsenceAsync(
        SqliteConnection connection,
        LocalErasureWorkItemRow item,
        CovenantSqliteAuthorizationKind authorization,
        CancellationToken cancellationToken)
    {

        Result<ManagedFileResolvedRoot?> root = await ManagedFileRootResolver
            .ResolveAsync(connection, transaction: null, item.Location, cancellationToken)
            .ConfigureAwait(false);

        if (root.IsFailure)
        {

            return Result<LocalErasureWorkItemRow>.Failure(root.Error);

        }

        if (root.Value is not { } resolved)
        {

            return await TerminalizeManualAsync(connection, item, authorization, cancellationToken)
                .ConfigureAwait(false);

        }

        Result<ManagedFileOpenOutcome> opened = await _opener
            .OpenNoFollowAsync(resolved, item.Location, cancellationToken)
            .ConfigureAwait(false);

        if (opened.IsFailure)
        {

            return Result<LocalErasureWorkItemRow>.Failure(opened.Error);

        }

        ManagedFileOpenHandle? handle = opened.Value.Handle;

        try
        {

            switch (opened.Value.Kind)
            {

                case ManagedFileOpenKind.Absent:

                    return await AdvanceAsync(
                        connection,
                        item,
                        authorization,
                        LocalErasureWorkItemState.DeletionVerified,
                        LocalErasureDeletionEvidenceCode.AlreadyAbsent,
                        cancellationToken).ConfigureAwait(false);

                case ManagedFileOpenKind.Mismatch:

                    return await TerminalizeManualAsync(connection, item, authorization, cancellationToken)
                        .ConfigureAwait(false);

                default:

                    break;

            }

            Result<ManagedFileCompareDeleteResult> deleted = await _verifier
                .CompareDeleteAsync(handle!, item.Ownership, cancellationToken)
                .ConfigureAwait(false);

            if (deleted.IsFailure)
            {

                return Result<LocalErasureWorkItemRow>.Failure(deleted.Error);

            }

            return deleted.Value == ManagedFileCompareDeleteResult.Deleted
                ? await AdvanceAsync(
                    connection,
                    item,
                    authorization,
                    LocalErasureWorkItemState.DeletionVerified,
                    LocalErasureDeletionEvidenceCode.SameHandleDeletedAndParentFsynced,
                    cancellationToken).ConfigureAwait(false)
                : await TerminalizeManualAsync(connection, item, authorization, cancellationToken)
                    .ConfigureAwait(false);

        }
        finally
        {

            // Disposed on every terminal and failure path, including the ones that never opened a
            // handle at all. A leaked descriptor here would keep the file's inode alive after the
            // unlink and make "the bytes are gone" untrue for as long as the process lives.
            handle?.Dispose();

        }

    }

    /// <summary>
    /// Removes the exact label, terminalizes the producer, and completes the work item, in that order.
    /// </summary>
    /// <remarks>
    /// The order is not a preference. The producer's update guard refuses the adoption-to-erasure edge
    /// while the label still exists, and the work item's completion guard refuses to advance until the
    /// producer is already <c>Erased</c>, so the database enforces the sequence the comment describes.
    /// </remarks>
    private async Task<Result<CovenantArtifactErasureProgress>> CompleteAsync(
        SqliteConnection connection,
        LocalErasureWorkItemRow item,
        CovenantSqliteAuthorizationKind authorization,
        CancellationToken cancellationToken)
    {

        try
        {

            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

            using (_initializer.Authorize(connection, authorization))
            {

                await ExecuteAsync(
                    connection,
                    transaction,
                    "DELETE FROM artifact_sensitivity WHERE LabelId = $label;",
                    command => command.Parameters.AddWithValue("$label", Format(item.SourceSensitivityLabelId)),
                    cancellationToken).ConfigureAwait(false);

                int producer = await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    UPDATE managed_file_write_intents
                    SET PhaseCode = 10, Revision = Revision + 1, UpdatedAtUtc = $now
                    WHERE WriteOperationId = $source AND PhaseCode = 7 AND Revision = $revision;
                    """,
                    command =>
                    {

                        _ = command.Parameters.AddWithValue("$now", Iso(_time.GetUtcNow()));

                        _ = command.Parameters.AddWithValue("$source", Format(item.SourceWriteOperationId));

                        return command.Parameters.AddWithValue("$revision", item.ExpectedSourceRevision);

                    },
                    cancellationToken).ConfigureAwait(false);

                if (producer != 1)
                {

                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                    return Blocked(CovenantErasureBlocker.IntegrityFailure);

                }

                bool completed = await LocalErasureWorkItemStore.TryAdvanceAsync(
                    connection,
                    transaction,
                    item.WorkItemId,
                    LocalErasureWorkItemState.DeletionVerified,
                    item.CheckpointRevision,
                    LocalErasureWorkItemState.Completed,
                    item.DeletionEvidence,
                    _time.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);

                if (!completed)
                {

                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                    return Blocked(CovenantErasureBlocker.IntegrityFailure);

                }

            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return Result<CovenantArtifactErasureProgress>.Success(
                new CovenantArtifactErasureProgress(1, 1, 0, CovenantErasureBlocker.None));

        }
        catch (SqliteException)
        {

            return Blocked(CovenantErasureBlocker.IntegrityFailure);

        }

    }

    private async Task<Result<LocalErasureWorkItemRow>> AdvanceAsync(
        SqliteConnection connection,
        LocalErasureWorkItemRow item,
        CovenantSqliteAuthorizationKind authorization,
        LocalErasureWorkItemState nextState,
        LocalErasureDeletionEvidenceCode? evidence,
        CancellationToken cancellationToken)
    {

        try
        {

            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

            bool advanced;

            using (_initializer.Authorize(connection, authorization))
            {

                advanced = await LocalErasureWorkItemStore.TryAdvanceAsync(
                    connection,
                    transaction,
                    item.WorkItemId,
                    item.State,
                    item.CheckpointRevision,
                    nextState,
                    evidence,
                    _time.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);

            }

            if (!advanced)
            {

                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return Result<LocalErasureWorkItemRow>.Failure(
                    new Error(
                        ErrorCodes.Covenant.RevisionConflict,
                        "A local erasure work item moved underneath this attempt."));

            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return Result<LocalErasureWorkItemRow>.Success(
                item with
                {
                    State = nextState,
                    DeletionEvidence = evidence,
                    CheckpointRevision = item.CheckpointRevision + 1,
                });

        }
        catch (SqliteException exception)
        {

            return Result<LocalErasureWorkItemRow>.Failure(
                new Error(ErrorCodes.Covenant.ManualArtifactErasureRequired, exception.Message));

        }

    }

    private Task<Result<LocalErasureWorkItemRow>> TerminalizeManualAsync(
        SqliteConnection connection,
        LocalErasureWorkItemRow item,
        CovenantSqliteAuthorizationKind authorization,
        CancellationToken cancellationToken) =>
        AdvanceAsync(
            connection,
            item,
            authorization,
            LocalErasureWorkItemState.ManualBlocker,
            evidence: null,
            cancellationToken);

    private static async Task<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        Func<SqliteCommand, object> bind,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        _ = bind(command);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private static Result<CovenantArtifactErasureProgress> Blocked(CovenantErasureBlocker blocker) =>
        Result<CovenantArtifactErasureProgress>.Success(
            new CovenantArtifactErasureProgress(1, 0, 1, blocker));

    private static string Iso(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    private static string Format(Guid value) => value.ToString("D").ToUpperInvariant();

}

/// <summary>
/// Resolves the Campaign root a durable location was recorded against, from the database alone.
/// </summary>
/// <remarks>
/// The lookup is by physical identity, not by Campaign: the identity is the fact the producer
/// recorded, and looking the Campaign up first would let a Campaign whose root has since been
/// re-registered supply a different directory under the same name.
/// </remarks>
internal static class ManagedFileRootResolver
{

    internal static async Task<Result<ManagedFileResolvedRoot?>> ResolveAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ManagedFileDurableLocationEvidence location,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(location);

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT DisplayPath, Revision
            FROM campaign_path_identities
            WHERE PhysicalIdentityDigest = $identity;
            """;

        _ = command.Parameters.AddWithValue("$identity", location.CampaignRootIdentityDigest.Bytes.ToArray());

        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            return Result<ManagedFileResolvedRoot?>.Success(null);

        }

        long revision = reader.GetInt64(1);

        // A root that has been re-registered since the producer recorded this location is not the same
        // root. Following the same relative segments under it would delete a file at a path that
        // merely looks familiar.
        return Result<ManagedFileResolvedRoot?>.Success(
            revision == location.PathRevision
                ? new ManagedFileResolvedRoot(reader.GetString(0), revision)
                : null);

    }

}

/// <summary>
/// The one kernel that erases an Arcanum-owned file living outside SQLite.
/// </summary>
/// <remarks>
/// It is handed identities and nothing else. Location, ownership, and owner scope are all reread from
/// the producer's own durable row inside one immediate transaction, and the work item that authorizes
/// the deletion is created from those reread values before the first syscall (§10.17).
/// </remarks>
internal sealed class CovenantManagedFileErasureKernel(
    ICovenantConnectionSource connections,
    ICovenantSqliteConnectionInitializer initializer,
    ManagedFileErasureStateMachine stateMachine,
    TimeProvider timeProvider) : ICovenantManagedFileErasureKernel
{

    private readonly ICovenantConnectionSource _connections =
        connections ?? throw new ArgumentNullException(nameof(connections));

    private readonly ICovenantSqliteConnectionInitializer _initializer =
        initializer ?? throw new ArgumentNullException(nameof(initializer));

    private readonly ManagedFileErasureStateMachine _stateMachine =
        stateMachine ?? throw new ArgumentNullException(nameof(stateMachine));

    private readonly TimeProvider _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async ValueTask<Result<CovenantArtifactErasureProgress>> EraseAsync(
        CovenantManagedFileErasureRequest request,
        CovenantArtifactErasureAuthority authority,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(request);

        ArgumentNullException.ThrowIfNull(authority);

        Result current = await authority.RevalidateAsync(cancellationToken).ConfigureAwait(false);

        if (current.IsFailure)
        {

            return Result<CovenantArtifactErasureProgress>.Success(
                new CovenantArtifactErasureProgress(0, 0, 0, CovenantErasureBlocker.AuthorityStale));

        }

        CovenantSqliteAuthorizationKind authorization = Authorization(authority);

        SqliteConnection connection = await _connections
            .GetOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        Result<LocalErasureWorkItemRow> prepared = await PrepareAsync(
            connection,
            request,
            authority,
            authorization,
            cancellationToken).ConfigureAwait(false);

        if (prepared.IsFailure)
        {

            return Result<CovenantArtifactErasureProgress>.Success(
                new CovenantArtifactErasureProgress(1, 0, 1, MapBlocker(prepared.Error)));

        }

        return await _stateMachine
            .ResolveAsync(connection, prepared.Value, authorization, cancellationToken)
            .ConfigureAwait(false);

    }

    /// <summary>
    /// Rereads the producer, derives the owner scope from it, and creates the durable work item.
    /// </summary>
    private async Task<Result<LocalErasureWorkItemRow>> PrepareAsync(
        SqliteConnection connection,
        CovenantManagedFileErasureRequest request,
        CovenantArtifactErasureAuthority authority,
        CovenantSqliteAuthorizationKind authorization,
        CancellationToken cancellationToken)
    {

        Result<LocalErasureWorkItemRow?> existing = await LocalErasureWorkItemStore
            .TryReadAsync(connection, transaction: null, request.WorkItemId, cancellationToken)
            .ConfigureAwait(false);

        if (existing.IsFailure)
        {

            return Result<LocalErasureWorkItemRow>.Failure(existing.Error);

        }

        if (existing.Value is { } resumed)
        {

            // A resumed attempt never re-derives authority from the producer row: the work item is
            // itself the durable authorization, and re-deriving it would let a producer that has since
            // moved retarget work the database already authorized.
            return resumed.SourceWriteOperationId == request.SourceManagedWriteOperationId
                && resumed.ArtifactId == request.ArtifactId
                && resumed.SourceSensitivityLabelId == request.SensitivityLabelId
                    ? Result<LocalErasureWorkItemRow>.Success(resumed)
                    : Result<LocalErasureWorkItemRow>.Failure(
                        new Error(
                            ErrorCodes.Covenant.ManualArtifactErasureRequired,
                            "The work item identity names a different managed file than the request."));

        }

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        Result<ManagedFileProducerRow?> producer = await ReadProducerAsync(
            connection,
            transaction,
            request,
            cancellationToken).ConfigureAwait(false);

        if (producer.IsFailure)
        {

            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return Result<LocalErasureWorkItemRow>.Failure(producer.Error);

        }

        if (producer.Value is not { } row)
        {

            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return Result<LocalErasureWorkItemRow>.Failure(
                new Error(
                    ErrorCodes.Covenant.ManualArtifactErasureRequired,
                    "No adopted managed-file producer matches this erasure request."));

        }

        if (!authority.Covers(row.OwnerScope))
        {

            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return Result<LocalErasureWorkItemRow>.Failure(
                new Error(
                    ErrorCodes.Covenant.ForbiddenAuthority,
                    "The managed file's current owner is outside this erasure authority's coverage."));

        }

        try
        {

            using (_initializer.Authorize(connection, authorization))
            {

                await LocalErasureWorkItemStore.InsertPreparedAsync(
                    connection,
                    transaction,
                    request.WorkItemId,
                    request.OperationId,
                    row.WriteOperationId,
                    row.Revision,
                    row.ArtifactId,
                    row.SensitivityLabelId,
                    row.Location,
                    row.Ownership,
                    _time.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);

            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        }
        catch (SqliteException exception)
        {

            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return Result<LocalErasureWorkItemRow>.Failure(
                new Error(ErrorCodes.Covenant.ManualArtifactErasureRequired, exception.Message));

        }

        return Result<LocalErasureWorkItemRow>.Success(
            new LocalErasureWorkItemRow(
                request.WorkItemId,
                request.OperationId,
                row.WriteOperationId,
                row.Revision,
                row.ArtifactId,
                row.SensitivityLabelId,
                row.Location,
                row.Ownership,
                LocalErasureWorkItemState.Prepared,
                DeletionEvidence: null,
                CheckpointRevision: 0));

    }

    private static async Task<Result<ManagedFileProducerRow?>> ReadProducerAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CovenantManagedFileErasureRequest request,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT intents.WriteOperationId, intents.ArtifactId, intents.SensitivityLabelId, intents.Revision,
                intents.DurableLocationEvidence, intents.FinalOwnershipEvidence, labels.CampaignId
            FROM managed_file_write_intents AS intents
            JOIN artifact_sensitivity AS labels
                ON labels.LabelId = intents.SensitivityLabelId AND labels.ArtifactId = intents.ArtifactId
            WHERE intents.WriteOperationId = $source
                AND intents.PhaseCode = 7
                AND intents.Revision = $revision
                AND intents.ArtifactId = $artifact
                AND intents.SensitivityLabelId = $label
                AND intents.PendingArtifactSensitivityLabel IS NULL
                AND intents.FinalOwnershipEvidence IS NOT NULL;
            """;

        _ = command.Parameters.AddWithValue("$source", Format(request.SourceManagedWriteOperationId));

        _ = command.Parameters.AddWithValue("$revision", (long)request.ExpectedSourceWriteRevision);

        _ = command.Parameters.AddWithValue("$artifact", Format(request.ArtifactId));

        _ = command.Parameters.AddWithValue("$label", Format(request.SensitivityLabelId));

        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            return Result<ManagedFileProducerRow?>.Success(null);

        }

        Result<ManagedFileWriteDurableLocationEvidence> location =
            ManagedFileEvidenceCodec.DecodeWriteLocation((byte[])reader.GetValue(4));

        if (location.IsFailure)
        {

            return Result<ManagedFileProducerRow?>.Failure(location.Error);

        }

        Result<ManagedFileOwnershipEvidence> ownership =
            ManagedFileEvidenceCodec.DecodeOwnership((byte[])reader.GetValue(5));

        if (ownership.IsFailure)
        {

            return Result<ManagedFileProducerRow?>.Failure(ownership.Error);

        }

        Guid? campaignId = reader.IsDBNull(6)
            ? null
            : Guid.Parse(reader.GetString(6), CultureInfo.InvariantCulture);

        return Result<ManagedFileProducerRow?>.Success(
            new ManagedFileProducerRow(
                Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                Guid.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                Guid.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                reader.GetInt64(3),
                location.Value.Target,
                ownership.Value,
                campaignId is { } present
                    ? CovenantOperationScope.ForCampaign(present)
                    : CovenantOperationScope.Global));

    }

    /// <summary>
    /// The connection-local authorization the live authority arm selects.
    /// </summary>
    /// <remarks>
    /// Selected from the arm rather than passed in, and never
    /// <see cref="CovenantSqliteAuthorizationKind.ManagedFileIntentMutation"/>. The producer's own
    /// update guard refuses that scope on the adoption-to-erasure edge precisely so one borrowed
    /// capability cannot both create a managed file and destroy it.
    /// </remarks>
    internal static CovenantSqliteAuthorizationKind Authorization(CovenantArtifactErasureAuthority authority) =>
        authority.Kind == CovenantArtifactErasureAuthorityKind.Exclusive
            ? CovenantSqliteAuthorizationKind.CovenantFamilyMaintenance
            : CovenantSqliteAuthorizationKind.SensitivityRetentionPurge;

    /// <summary>
    /// Maps a preparation refusal onto the closed, content-free blocker vocabulary.
    /// </summary>
    /// <remarks>
    /// A coverage refusal is an ownership mismatch: the file belongs to somebody this authority does
    /// not speak for. Everything else at this stage is durable state disagreeing with itself, which an
    /// operator has to look at rather than retry.
    /// </remarks>
    private static CovenantErasureBlocker MapBlocker(Error error) =>
        error.Code == ErrorCodes.Covenant.ForbiddenAuthority
            ? CovenantErasureBlocker.ManualOwnershipMismatch
            : CovenantErasureBlocker.IntegrityFailure;

    private static string Format(Guid value) => value.ToString("D").ToUpperInvariant();

}
