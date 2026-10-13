using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

/// <summary>Retires imported derived execution authority before a full restored generation is adopted.</summary>
internal static class BackupRestoreLongRestSanitation
{
    private const string AllGenerations = "SELECT GenerationId FROM long_rest_discovery_generations";

    internal static async Task<Result> SanitizeStagedAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        try
        {
            if (await GrimoireCoreSchemaVersion.ReadAsync(connection, transaction, ct).ConfigureAwait(false) < 18)
            {
                Result legacy = await VerifyLegacyAbsenceAsync(connection, transaction, ct).ConfigureAwait(false);

                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

                return legacy;
            }

            // The inventory is shared with native owner deletion and Annals. Full adoption owns
            // the whole staged derived graph, including empty roots and FK-off orphan residue.
            foreach (var step in LongRestLifecycle.GenerationSteps(AllGenerations))
            {
                await using SqliteCommand command = connection.CreateCommand();

                command.Transaction = transaction;

                command.CommandText = $"DELETE FROM {step.Table};";

                _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using (SqliteCommand labels = connection.CreateCommand())
            {
                labels.Transaction = transaction;

                labels.CommandText = "DELETE FROM artifact_sensitivity WHERE ArtifactKindCode = $kind;";

                _ = labels.Parameters.AddWithValue("$kind", (int)SensitiveArtifactKind.LongRestDiscovery);

                _ = await labels.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            Result verified = await VerifyInSnapshotAsync(connection, transaction, ct).ConfigureAwait(false);

            if (verified.IsFailure)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

                return verified;
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);

            return Result.Success();
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

            throw;
        }
    }

    /// <summary>Select-only proof on the exact adopted database; it never repairs a live installation.</summary>
    internal static async Task<Result> VerifyCommittedAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);

        Result verified = await VerifyInSnapshotAsync(connection, transaction, ct).ConfigureAwait(false);

        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

        return verified;
    }

    private static async Task<Result> VerifyInSnapshotAsync(SqliteConnection connection, SqliteTransaction transaction,
        CancellationToken ct)
    {
        // A pre-v18 database may prove the whole new family absent. At v18 every shared
        // inventory target is mandatory; a missing or malformed target is an error, not zero.
        if (await GrimoireCoreSchemaVersion.ReadAsync(connection, transaction, ct).ConfigureAwait(false) < 18)
        {
            return await VerifyLegacyAbsenceAsync(connection, transaction, ct).ConfigureAwait(false);
        }

        foreach (var step in LongRestLifecycle.GenerationSteps(AllGenerations))
        {
            await using SqliteCommand command = connection.CreateCommand();

            command.Transaction = transaction;

            command.CommandText = $"SELECT EXISTS(SELECT 1 FROM {step.Table});";

            if ((long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))! != 0)
            {
                return Unknown();
            }
        }

        await using SqliteCommand labels = connection.CreateCommand();

        labels.Transaction = transaction;

        labels.CommandText = "SELECT EXISTS(SELECT 1 FROM artifact_sensitivity WHERE ArtifactKindCode = $kind);";

        _ = labels.Parameters.AddWithValue("$kind", (int)SensitiveArtifactKind.LongRestDiscovery);

        return (long)(await labels.ExecuteScalarAsync(ct).ConfigureAwait(false))! == 0
            ? Result.Success() : Unknown();
    }

    private static async Task<Result> VerifyLegacyAbsenceAsync(SqliteConnection connection,
        SqliteTransaction transaction, CancellationToken ct)
    {
        foreach (var step in LongRestLifecycle.GenerationSteps(AllGenerations))
        {
            if (await ExistsAsync(connection, transaction, step.Table, ct).ConfigureAwait(false))
            {
                return Unknown();
            }
        }

        if (!await ExistsAsync(connection, transaction, "artifact_sensitivity", ct).ConfigureAwait(false))
        {
            return Result.Success();
        }

        await using SqliteCommand labels = connection.CreateCommand();

        labels.Transaction = transaction;

        labels.CommandText = "SELECT EXISTS(SELECT 1 FROM main.artifact_sensitivity WHERE ArtifactKindCode = $kind);";

        _ = labels.Parameters.AddWithValue("$kind", (int)SensitiveArtifactKind.LongRestDiscovery);

        return (long)(await labels.ExecuteScalarAsync(ct).ConfigureAwait(false))! == 0
            ? Result.Success() : Unknown();
    }

    private static async Task<bool> ExistsAsync(SqliteConnection connection, SqliteTransaction transaction,
        string table, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "SELECT EXISTS(SELECT 1 FROM main.sqlite_master WHERE name COLLATE NOCASE = $table);";

        _ = command.Parameters.AddWithValue("$table", table);

        return (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))! != 0;
    }

    internal static Result Unknown() => new Error(ErrorCodes.Covenant.ManualRecoveryRequired,
        "The restored database has no proven absence of imported Long Rest execution authority; its recovery evidence is retained.");
}
