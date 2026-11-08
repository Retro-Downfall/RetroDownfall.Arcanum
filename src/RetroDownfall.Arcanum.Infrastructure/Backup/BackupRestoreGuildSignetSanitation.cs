using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

/// <summary>Imported authority is historical evidence; restore never revives its execution rights.</summary>
internal static class BackupRestoreGuildSignetSanitation
{
    internal static async Task<Result> SanitizeStagedAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        Result available = await GuildStorageAvailability.CheckAsync(connection, transaction, ct).ConfigureAwait(false);

        if (available.IsFailure)
        {
            return available;
        }

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "UPDATE guild_signet_authority_state SET Epoch=$epoch WHERE StateKey=1;";

        command.Parameters.AddWithValue("$epoch", Guid.NewGuid().ToString("D").ToUpperInvariant());

        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
        {
            return Unknown();
        }

        command.CommandText = "UPDATE guild_signets SET RestoredInert=1,Revision=Revision+1 WHERE RestoredInert=0;";

        _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        command.CommandText = "UPDATE guild_workspace_leases SET StateCode=4,ReasonCode='restored-ownership',Revision=Revision+1 WHERE StateCode<>3;";

        _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        Result verified = await VerifyWithinAsync(connection, transaction, ct).ConfigureAwait(false);

        if (verified.IsFailure)
        {
            return verified;
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return Result.Success();
    }

    /// <summary>Select-only proof against the closed adopted database, including resumed recovery.</summary>
    internal static async Task<Result> VerifyCommittedAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);

        return await VerifyWithinAsync(connection, transaction, ct).ConfigureAwait(false);
    }

    private static async Task<Result> VerifyWithinAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "SELECT SchemaVersion FROM grimoire_feature_schemas WHERE FamilyCode=2 AND TransactionTierCode=3";

        object? version = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);

        if (version is null or DBNull || version is long value && value < 2)
        {
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name COLLATE NOCASE IN('guild_signets','guild_signet_revocations','guild_signet_authority_state')";

            return Equals(0L, await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) ? Result.Success() : Unknown();
        }

        Result available = await GuildStorageAvailability.CheckAsync(connection, transaction, ct).ConfigureAwait(false);

        if (available.IsFailure)
        {
            return available;
        }

        command.CommandText = "SELECT Epoch FROM guild_signet_authority_state WHERE StateKey=1";

        object? epoch = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);

        if (epoch is not string text || !Guid.TryParseExact(text, "D", out Guid parsed) || parsed == Guid.Empty
            || text != parsed.ToString("D").ToUpperInvariant())
        {
            return Unknown();
        }

        command.CommandText = "SELECT EXISTS(SELECT 1 FROM guild_signets WHERE RestoredInert<>1 OR AuthorityEpoch=$epoch)";

        command.Parameters.AddWithValue("$epoch", text);

        if (!Equals(0L, await command.ExecuteScalarAsync(ct).ConfigureAwait(false))) return Unknown();

        command.CommandText = "SELECT EXISTS(SELECT 1 FROM guild_workspace_leases WHERE StateCode IN(1,2))";

        return Equals(0L, await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) ? Result.Success() : Unknown();
    }

    private static Error Unknown() => new(ErrorCodes.Guild.AuthorityUnavailable, "Restored Guild Signet execution inactivity could not be proved.");
}
