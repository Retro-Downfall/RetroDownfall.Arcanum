using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Infrastructure.Covenant;

/// <summary>
/// One durable schema-repair intent, as the journal reads it back.
/// </summary>
/// <remarks>
/// The owner is reconstructed from immutable journal fields rather than stored as an object, because
/// a <see cref="CovenantExclusiveRecoveryOwner"/> is a validated value and a row that could carry a
/// prebuilt one would be a way to persist a capability.
/// </remarks>
internal sealed record CovenantSchemaRepairIntent(
    Guid OperationId,
    CovenantDigest EffectDigest,
    CovenantDigest InspectedCatalogDigest,
    CovenantSchemaRepairAction Action,
    GrimoireSchemaTransactionTier Tier,
    Guid? CapturedDatasetGeneration,
    long AuthorityEpoch,
    CovenantSchemaRepairPhase Phase,
    long Revision)
{

    /// <summary>The exact gate owner this intent belongs to.</summary>
    public CovenantExclusiveRecoveryOwner Owner =>
        new(OperationId, CovenantExclusiveOperation.SchemaRepair, EffectDigest);

    /// <summary>Whether this intent still holds admission closed.</summary>
    public bool IsActive => Phase
        is CovenantSchemaRepairPhase.Prepared
        or CovenantSchemaRepairPhase.CatalogCommitted
        or CovenantSchemaRepairPhase.HealthVerified
        or CovenantSchemaRepairPhase.ReopenPending;

}

/// <summary>
/// The only reader and writer of <c>covenant_schema_repair_intents</c>.
/// </summary>
/// <remarks>
/// Repair mutates the catalog itself, so recovery cannot work out who owned an operation by
/// inspecting the result: a half-installed family looks the same whether this process created it or
/// found it. This journal is the whole answer, which is why every one of its identity fields is
/// immutable and why only the one-shot post-disposition finalizer may reach a terminal phase
/// (§10.17).
/// </remarks>
internal static class CovenantSchemaRepairJournal
{

    /// <summary>
    /// Reads the single nonterminal intent, if one exists.
    /// </summary>
    internal static async Task<Result<CovenantSchemaRepairIntent?>> TryReadActiveAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT OperationId, EffectDigest, InspectedCatalogDigest, RepairActionCode, TargetTierCode,
                CapturedDatasetGeneration, AuthorityEpoch, PhaseCode, Revision
            FROM covenant_schema_repair_intents
            WHERE PhaseCode IN (1, 2, 3, 4)
            ORDER BY CreatedAtUtc
            LIMIT 2;
            """;

        List<CovenantSchemaRepairIntent> intents = [];

        await using (SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false))
        {

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                intents.Add(
                    new CovenantSchemaRepairIntent(
                        Guid.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                        new CovenantDigest((byte[])reader.GetValue(1)),
                        new CovenantDigest((byte[])reader.GetValue(2)),
                        (CovenantSchemaRepairAction)reader.GetInt32(3),
                        (GrimoireSchemaTransactionTier)reader.GetInt32(4),
                        reader.IsDBNull(5) ? null : new Guid((byte[])reader.GetValue(5)),
                        reader.GetInt64(6),
                        (CovenantSchemaRepairPhase)reader.GetInt32(7),
                        reader.GetInt64(8)));

            }

        }

        // Two live intents mean two operations each believe they closed admission, and neither can
        // safely finish the other's work. That is an operator's problem, not a recovery pass's.
        return intents.Count > 1
            ? Result<CovenantSchemaRepairIntent?>.Failure(
                new Error(
                    ErrorCodes.Covenant.ManualRecoveryRequired,
                    "More than one Covenant schema repair intent is active."))
            : Result<CovenantSchemaRepairIntent?>.Success(intents.FirstOrDefault());

    }

    /// <summary>
    /// Commits the <c>Prepared</c> intent that authorizes the first repair statement.
    /// </summary>
    /// <remarks>
    /// Written before any DDL, in its own transaction. A journal committed after the catalog change it
    /// describes would be a record of an operation that already happened, which is exactly the state
    /// recovery cannot tell apart from one that never started.
    /// </remarks>
    internal static async Task<Result> CommitPreparedAsync(
        SqliteConnection connection,
        ICovenantSqliteConnectionInitializer initializer,
        CovenantSchemaRepairIntent intent,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(initializer);

        ArgumentNullException.ThrowIfNull(intent);

        try
        {

            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

            await using (SqliteCommand command = connection.CreateCommand())
            {

                command.Transaction = transaction;

                command.CommandText = """
                    INSERT INTO covenant_schema_repair_intents (
                        OperationId, EffectDigest, InspectedCatalogDigest, RepairActionCode, TargetTierCode,
                        CapturedDatasetGeneration, AuthorityEpoch, PhaseCode, Revision, LastDurableErrorCode,
                        CreatedAtUtc, UpdatedAtUtc)
                    VALUES ($operation, $effect, $catalog, $action, $tier, $generation, $epoch, 1, 0, NULL,
                        $now, $now);
                    """;

                _ = command.Parameters.AddWithValue("$operation", Format(intent.OperationId));

                _ = command.Parameters.AddWithValue("$effect", intent.EffectDigest.Bytes.ToArray());

                _ = command.Parameters.AddWithValue("$catalog", intent.InspectedCatalogDigest.Bytes.ToArray());

                _ = command.Parameters.AddWithValue("$action", (int)intent.Action);

                _ = command.Parameters.AddWithValue("$tier", (int)intent.Tier);

                _ = command.Parameters.AddWithValue(
                    "$generation",
                    intent.CapturedDatasetGeneration is { } generation
                        ? generation.ToByteArray()
                        : DBNull.Value);

                _ = command.Parameters.AddWithValue("$epoch", intent.AuthorityEpoch);

                _ = command.Parameters.AddWithValue("$now", Iso(utcNow));

                _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return Result.Success();

        }
        catch (SqliteException exception)
        {

            return Result.Failure(new Error(ErrorCodes.Covenant.MaintenanceFailed, exception.Message));

        }

    }

    /// <summary>
    /// Advances one intent by exactly one edge, compare-and-swapping on its phase and revision.
    /// </summary>
    internal static async Task<Result<bool>> TryAdvanceAsync(
        SqliteConnection connection,
        ICovenantSqliteConnectionInitializer initializer,
        CovenantSchemaRepairIntent intent,
        CovenantSchemaRepairPhase nextPhase,
        string? lastDurableErrorCode,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(initializer);

        ArgumentNullException.ThrowIfNull(intent);

        try
        {

            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

            int changed;

            using (initializer.Authorize(connection, CovenantSqliteAuthorizationKind.CovenantFamilyMaintenance))
            {

                await using SqliteCommand command = connection.CreateCommand();

                command.Transaction = transaction;

                command.CommandText = """
                    UPDATE covenant_schema_repair_intents
                    SET PhaseCode = $next,
                        Revision = Revision + 1,
                        LastDurableErrorCode = $error,
                        UpdatedAtUtc = $now
                    WHERE OperationId = $operation AND PhaseCode = $expectedPhase AND Revision = $expectedRevision;
                    """;

                _ = command.Parameters.AddWithValue("$next", (int)nextPhase);

                _ = command.Parameters.AddWithValue(
                    "$error",
                    lastDurableErrorCode is { } code ? code : DBNull.Value);

                _ = command.Parameters.AddWithValue("$now", Iso(utcNow));

                _ = command.Parameters.AddWithValue("$operation", Format(intent.OperationId));

                _ = command.Parameters.AddWithValue("$expectedPhase", (int)intent.Phase);

                _ = command.Parameters.AddWithValue("$expectedRevision", intent.Revision);

                changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            }

            if (changed != 1)
            {

                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return Result<bool>.Success(false);

            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return Result<bool>.Success(true);

        }
        catch (SqliteException exception)
        {

            return Result<bool>.Failure(new Error(ErrorCodes.Covenant.MaintenanceFailed, exception.Message));

        }

    }

    private static string Iso(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    private static string Format(Guid value) => value.ToString("D").ToUpperInvariant();

}
