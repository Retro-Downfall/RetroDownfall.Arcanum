using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Memory;

/// <summary>
/// The post-commit write-ahead-log scrub every erase attempts once its transaction is disposed.
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
/// <para>Its one log line carries the attempt and nothing else.</para>
/// </remarks>
internal sealed class MemoryErasureScrubber(
    IGrimoireOrdinaryConnectionFactory connections,
    ILogger<MemoryErasureScrubber> logger)
{
    private readonly IGrimoireOrdinaryConnectionFactory _connections = connections;

    private readonly ILogger<MemoryErasureScrubber> _logger = logger;

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
