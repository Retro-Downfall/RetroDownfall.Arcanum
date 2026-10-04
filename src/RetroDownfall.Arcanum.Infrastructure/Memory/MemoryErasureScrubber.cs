using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Memory;

/// <summary>
/// The post-commit write-ahead-log scrub every erase attempts once its transaction is disposed, and the
/// receipt read-back an erase makes when its commit's outcome is uncertain.
/// </summary>
/// <remarks>
/// <para>The checkpoint runs on a dedicated, unpooled read-write connection opened for it alone, never
/// on the connection that committed the erase. That connection's wait is cut to a quarter of a second,
/// so a reader holding an older snapshot is reported as <see cref="MemoryErasureWalCheckpointAttempt.Busy"/>
/// rather than waited out: the erase has already committed, and the scrub can be retried later through
/// a replay or the scrub route. The connection is disposed with its lease, so the shortened wait never
/// reaches another caller.</para>
///
/// <para>It never throws for anything the database does. A connection that could not be opened, or a
/// checkpoint that could not be read, is <see cref="MemoryErasureWalCheckpointAttempt.Unavailable"/>,
/// because a committed erase must always be able to report its result.</para>
///
/// <para><b>An uncertain commit is settled by its receipt.</b> A <c>COMMIT</c> that fails with anything
/// but a busy database may still have persisted. The erase reads its receipt back here, on a read-only
/// unpooled connection of its own, after its transaction is disposed: a receipt that is there is an
/// erase that happened, and one that is not is an erase that did not. A read that cannot be made says
/// neither, and the erase reports its failure.</para>
///
/// <para>Its two log lines are content-free: the attempt, and, when the erase protocol abandons a
/// committed erase's scrub or receipt upgrade, the failure's type. Neither names content, a
/// fingerprint, a key, or an exception message. The read-back logs nothing.</para>
/// </remarks>
internal sealed class MemoryErasureScrubber(
    IGrimoireOrdinaryConnectionFactory connections,
    ILogger<MemoryErasureScrubber> logger)
{
    private readonly IGrimoireOrdinaryConnectionFactory _connections = connections;

    private readonly ILogger<MemoryErasureScrubber> _logger = logger;

    private static readonly Error ReceiptUnreadable = new(
        ErrorCodes.MemoryErasure.Unavailable,
        "The erasure receipt could not be read back to settle an uncertain commit.");

    internal MemoryErasureScrubber(IGrimoireOrdinaryConnectionFactory connections)
        : this(connections, NullLogger<MemoryErasureScrubber>.Instance)
    {
    }

    internal async Task<MemoryErasureWalCheckpointAttempt> CheckpointAsync(CancellationToken cancellationToken)
    {
        MemoryErasureWalCheckpointAttempt attempt;

        try
        {
            Result<IGrimoireOrdinaryConnectionLease> opened = await _connections
                .OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, cancellationToken)
                .ConfigureAwait(false);

            if (opened.IsFailure)
            {
                attempt = MemoryErasureWalCheckpointAttempt.Unavailable;
            }
            else
            {
                await using IGrimoireOrdinaryConnectionLease lease = opened.Value;

                attempt = await TruncateAsync(lease.Connection, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception failure) when (IsStorageFault(failure))
        {
            attempt = MemoryErasureWalCheckpointAttempt.Unavailable;
        }

        _logger.LogInformation("Erasure write-ahead-log checkpoint attempt: {WalCheckpointAttempt}.", attempt);

        return attempt;
    }

    /// <summary>
    /// Reads one erase's receipt on a fresh read-only connection, to settle whether a commit that
    /// failed persisted.
    /// </summary>
    /// <returns>The receipt, null when none is recorded, or a failure when the read could not be made.</returns>
    internal async Task<Result<MemoryErasureReceiptRow?>> ReadCommittedReceiptAsync(
        Guid mutationId,
        CancellationToken cancellationToken)
    {
        try
        {
            Result<IGrimoireOrdinaryConnectionLease> opened = await _connections
                .OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly, cancellationToken)
                .ConfigureAwait(false);

            if (opened.IsFailure)
            {
                return Result<MemoryErasureReceiptRow?>.Failure(ReceiptUnreadable);
            }

            await using IGrimoireOrdinaryConnectionLease lease = opened.Value;

            return Result<MemoryErasureReceiptRow?>.Success(await MemoryErasureEvidence
                .ReadReceiptAsync(lease.Connection, null, mutationId, cancellationToken)
                .ConfigureAwait(false));
        }
        catch (Exception failure) when (IsStorageFault(failure) || failure is InvalidDataException)
        {
            return Result<MemoryErasureReceiptRow?>.Failure(ReceiptUnreadable);
        }
    }

    /// <summary>
    /// Records that a committed erase's post-commit scrub or receipt upgrade was abandoned, by the
    /// failure's type alone.
    /// </summary>
    internal void ReportAbandoned(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        _logger.LogWarning(
            "An erase committed, but its post-commit scrub or receipt upgrade failed ({FailureType}); the receipt stays pending until a replay or the scrub route finishes it.",
            failure.GetType().Name);
    }

    /// <summary>Runs the checked checkpoint with the scrub's short wait on the connection opened for it.</summary>
    private static async Task<MemoryErasureWalCheckpointAttempt> TruncateAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using (SqliteCommand wait = connection.CreateCommand())
        {
            // A literal, not a composed value: the scrub's whole wait for a busy log.
            wait.CommandText = "PRAGMA busy_timeout = 250;";

            _ = await wait.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        Result<CovenantWalCheckpointOutcome> outcome =
            await GrimoireWalCheckpoint.TruncateAsync(connection, cancellationToken).ConfigureAwait(false);

        if (outcome.IsFailure)
        {
            return MemoryErasureWalCheckpointAttempt.Unavailable;
        }

        return outcome.Value.IsTruncated
            ? MemoryErasureWalCheckpointAttempt.Truncated
            : MemoryErasureWalCheckpointAttempt.Busy;
    }

    private static bool IsStorageFault(Exception failure) =>
        failure is SqliteException or InvalidOperationException or IOException or UnauthorizedAccessException;
}
