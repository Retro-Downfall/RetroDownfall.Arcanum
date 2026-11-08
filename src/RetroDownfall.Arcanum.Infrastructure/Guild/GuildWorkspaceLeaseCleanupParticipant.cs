using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Owner erasure seals admission but retains every unresolved physical writer and its evidence.</summary>
internal sealed class GuildWorkspaceLeaseCleanupParticipant(IGrimoireOrdinaryConnectionFactory connections) : IGuildOwnerCleanupParticipant
{
    public async ValueTask<Result> SealAsync(GuildCleanupOwner owner, SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = Command(owner, connection, transaction);

        command.CommandText = $"""
            UPDATE guild_workspace_leases SET StateCode=CASE WHEN InFlightMutationCount=0 THEN 3 ELSE 2 END,
                Revision=Revision+1,ReasonCode='owner-erased'
            WHERE {Predicate(owner)} AND StateCode=1;
            """;

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    public async ValueTask<Result> ReconcileAsync(GuildCleanupOwner owner, CancellationToken cancellationToken)
    {
        var opened = await connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly, cancellationToken).ConfigureAwait(false);

        if (opened.IsFailure) return opened.Error;

        await using IGrimoireOrdinaryConnectionLease held = opened.Value;

        Result available = await GuildStorageAvailability.CheckAsync(held.Connection, null, cancellationToken).ConfigureAwait(false);

        return available.IsFailure ? available : await SettledAsync(owner, held.Connection, null, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Result> DeleteAsync(GuildCleanupOwner owner, SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken) =>
        await SettledAsync(owner, connection, transaction, cancellationToken).ConfigureAwait(false);

    private static async Task<Result> SettledAsync(GuildCleanupOwner owner, SqliteConnection connection, SqliteTransaction? transaction, CancellationToken ct)
    {
        await using SqliteCommand command = Command(owner, connection, transaction);

        command.CommandText = $"SELECT COUNT(*) FROM guild_workspace_leases WHERE {Predicate(owner)} AND (StateCode IS NOT 3 OR InFlightMutationCount IS NOT 0)";

        // Released rows are immutable replay/denial evidence; owner erasure does not prune them.
        return Equals(0L, await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) ? Result.Success()
            : new Error(ErrorCodes.Guild.ReconciliationRequired, "Guild owner cleanup retains unresolved workspace mutation evidence.");
    }

    private static string Predicate(GuildCleanupOwner owner) => owner.Kind == 1
        ? "(CampaignId=$owner COLLATE NOCASE OR WorkspaceId=$owner COLLATE NOCASE)" : "SessionId=$owner COLLATE NOCASE";

    private static SqliteCommand Command(GuildCleanupOwner owner, SqliteConnection connection, SqliteTransaction? transaction)
    {
        SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.Parameters.AddWithValue("$owner", owner.Id.ToString("D").ToUpperInvariant());

        return command;
    }
}
