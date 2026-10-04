using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// The one checked <c>wal_checkpoint(TRUNCATE)</c> a proof may rest on: it reads the pragma's answer
/// rather than assuming it.
/// </summary>
/// <remarks>
/// <para>The pragma answers with exactly one row of three integers. No row, a row of another shape, or a
/// second row is not an answer, and each is refused rather than read as a clean checkpoint. A busy or
/// partial checkpoint is not refused here: it is an answer, and the caller decides what it proves
/// through <see cref="CovenantWalCheckpointOutcome.IsTruncated"/>.</para>
///
/// <para>It runs on the caller's connection with whatever wait that connection has, so a caller that
/// must not wait long sets its own <c>busy_timeout</c> first. The graceful-shutdown checkpoint and the
/// native runtime probe stay outside this helper on purpose: neither proves anything from the answer.</para>
/// </remarks>
internal static class GrimoireWalCheckpoint
{
    private static readonly Error NoAnswer = new(
        ErrorCodes.Covenant.ErasureIncomplete,
        "A write-ahead-log checkpoint reported nothing, so it cannot be taken as proof that no frame remains.");

    private static readonly Error UnexpectedRow = new(
        ErrorCodes.Covenant.ErasureIncomplete,
        "A write-ahead-log checkpoint reported more than one row, so its answer cannot be read.");

    internal static async Task<Result<CovenantWalCheckpointOutcome>> TruncateAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.FieldCount != 3)
        {
            return Result<CovenantWalCheckpointOutcome>.Failure(NoAnswer);
        }

        CovenantWalCheckpointOutcome outcome = CovenantWalCheckpointOutcome.Project(reader);

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return Result<CovenantWalCheckpointOutcome>.Failure(UnexpectedRow);
        }

        return Result<CovenantWalCheckpointOutcome>.Success(outcome);
    }
}
