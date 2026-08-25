using System.Collections.Immutable;
using System.Data;
using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Covenant;

/// <summary>
/// Applies one bounded batch of core owner deletions to the encrypted canonical tier.
/// </summary>
/// <remarks>
/// The coordinator owns the lease and the transaction; <see cref="CovenantCleanupWorker"/> owns the
/// algorithm, on the same division <see cref="CovenantIndexRebuildCoordinator"/> keeps with the index
/// rebuilder. A coordinator that also decided what a batch means would be a second implementation of
/// the sweep with its own idea of when the tier has caught up.
///
/// <para>The gate lease is what makes the sweep yield: an installation reset, an erasure, or any other
/// exclusive owner drains it rather than racing it. That is why the batch runs under a lease it does
/// not strictly need to read rows — the lease is the yielding, not the authorization.</para>
/// </remarks>
internal sealed class CovenantOwnerCleanupCoordinator(
    ICovenantOperationGate gate,
    ICovenantConnectionSource connections,
    CovenantCleanupWorker worker)
{

    internal async ValueTask<Result<CovenantCleanupOutcome>> RunBatchAsync(
        int maxEvents,
        CancellationToken cancellationToken)
    {

        Result<CovenantCleanupLease> acquired = await gate
            .AcquireCleanupAsync(CovenantOperationScope.Global, cancellationToken)
            .ConfigureAwait(false);

        if (acquired.IsFailure)
        {

            return acquired.Error;

        }

        await using CovenantCleanupLease lease = acquired.Value;

        SqliteConnection connection = await connections
            .GetOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);

        Result<CovenantCleanupOutcome> applied = await worker
            .RunBatchAsync(lease, new CovenantMutationTransaction(connection, transaction), cancellationToken, maxEvents)
            .ConfigureAwait(false);

        if (applied.IsFailure)
        {

            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return applied.Error;

        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return applied.Value;

    }

}

/// <summary>
/// Applies one bounded batch of canonical outbox deltas to the accelerator projection.
/// </summary>
/// <remarks>
/// Same division and the same reason as the cleanup coordinator, under the accelerator lease rather
/// than the cleanup one because the projection is what it writes. Without a driver the outbox only
/// ever grew: every canonical commit appended to it and nothing under <c>src</c> drained it, so the
/// projection stayed at whatever sequence the last test left and the pending-row ceiling was the only
/// thing standing between an installation and a write refusal it could not act on.
/// </remarks>
internal sealed class CovenantSearchOutboxCoordinator(
    ICovenantOperationGate gate,
    ICovenantConnectionSource connections,
    CovenantSearchOutboxWorker worker)
{

    internal async ValueTask<Result<CovenantOutboxSyncOutcome>> SynchronizeAsync(
        int maxRows,
        CancellationToken cancellationToken)
    {

        Result<CovenantAcceleratorLease> acquired = await gate
            .AcquireAcceleratorAsync(cancellationToken)
            .ConfigureAwait(false);

        if (acquired.IsFailure)
        {

            return acquired.Error;

        }

        await using CovenantAcceleratorLease lease = acquired.Value;

        SqliteConnection connection = await connections
            .GetOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);

        Result<CovenantOutboxSyncOutcome> applied = await worker
            .SynchronizeAsync(lease, new CovenantMutationTransaction(connection, transaction), cancellationToken, maxRows)
            .ConfigureAwait(false);

        if (applied.IsFailure)
        {

            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return applied.Error;

        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return applied.Value;

    }

}

/// <summary>What one compaction pass folded, and for how many Sessions.</summary>
public sealed record CovenantReceiptCompactionOutcome(int SessionsFolded, int ReceiptsFolded);

/// <summary>
/// Folds the oldest turn receipts of the Sessions that have exceeded their live tail.
/// </summary>
/// <remarks>
/// The compactor folds one Session per call and cannot find its own work, so the coordinator supplies
/// the discovery: a bounded read of the Sessions currently over the per-Session receipt ceiling. That
/// read is the reason this is a sweep rather than a step on the write path — the compactor's own
/// remark is that an ordinary turn commit must never pay for a tail somebody else accumulated.
///
/// <para>Each Session folds in its own transaction. One transaction spanning the whole pass would make
/// a single Session's failure discard the folds that already succeeded, and every fold is independently
/// idempotent, so there is nothing to gain by binding them together.</para>
/// </remarks>
internal sealed class CovenantTurnReceiptCompactionCoordinator(
    ICovenantOperationGate gate,
    ICovenantConnectionSource connections,
    CovenantTurnReceiptCompactor compactor)
{

    internal const int DefaultSessionsPerPass = 16;

    internal async ValueTask<Result<CovenantReceiptCompactionOutcome>> CompactAsync(
        int maxSessions,
        CancellationToken cancellationToken)
    {

        Result<CovenantCleanupLease> acquired = await gate
            .AcquireCleanupAsync(CovenantOperationScope.Global, cancellationToken)
            .ConfigureAwait(false);

        if (acquired.IsFailure)
        {

            return acquired.Error;

        }

        await using CovenantCleanupLease lease = acquired.Value;

        SqliteConnection connection = await connections
            .GetOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        ImmutableArray<Guid> backlog = await ReadBacklogAsync(connection, maxSessions, cancellationToken)
            .ConfigureAwait(false);

        int sessions = 0;

        int receipts = 0;

        foreach (Guid sessionId in backlog)
        {

            cancellationToken.ThrowIfCancellationRequested();

            await using SqliteTransaction transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false);

            Result<int> folded = await compactor
                .FoldAsync(sessionId, new CovenantMutationTransaction(connection, transaction), cancellationToken)
                .ConfigureAwait(false);

            if (folded.IsFailure)
            {

                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return folded.Error;

            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            if (folded.Value > 0)
            {

                sessions++;

                receipts += folded.Value;

            }

        }

        return new CovenantReceiptCompactionOutcome(sessions, receipts);

    }

    private static async ValueTask<ImmutableArray<Guid>> ReadBacklogAsync(
        SqliteConnection connection,
        int maxSessions,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = CovenantStoreSql.TurnReceiptBacklog();

        _ = command.Parameters.AddWithValue("$ceiling", CovenantLimits.MaxTurnReceiptsPerSession);

        _ = command.Parameters.AddWithValue("$take", maxSessions);

        ImmutableArray<Guid>.Builder sessions = ImmutableArray.CreateBuilder<Guid>();

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            // Stored as the canonical text form everywhere in this family, so a row that is not one is
            // a corrupt identity rather than a Session to fold, and skipping it leaves it for the
            // integrity checks that are allowed to say so.
            if (Guid.TryParse(reader.GetString(0), CultureInfo.InvariantCulture, out Guid sessionId))
            {

                sessions.Add(sessionId);

            }

        }

        return sessions.ToImmutable();

    }

}
