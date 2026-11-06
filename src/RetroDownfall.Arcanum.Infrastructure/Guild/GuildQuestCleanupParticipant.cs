using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Seals the ordinary Quest lineage; uncertainty retains its existing family work.</summary>
internal sealed class GuildQuestCleanupParticipant(IGrimoireOrdinaryConnectionFactory connections, TimeProvider clock) : IGuildOwnerCleanupParticipant
{
    public async ValueTask<Result> SealAsync(GuildCleanupOwner owner, SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = Command(owner, connection, transaction);

        command.CommandText = Affected(owner) + $"""
            UPDATE guild_quests AS c SET CancellationRequested=1,
                Retired=CASE WHEN {Owned(owner)} THEN 1 ELSE Retired END,Revision=Revision+1,UpdatedAtUtc=$now
            WHERE Id IN(SELECT Id FROM affected) AND (CancellationRequested=0 OR ({Owned(owner)} AND Retired=0));
            """;

        command.Parameters.AddWithValue("$now", UtcInstantText.Format(clock.GetUtcNow()));

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    public async ValueTask<Result> ReconcileAsync(GuildCleanupOwner owner, CancellationToken cancellationToken)
    {
        var opened = await connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly, cancellationToken).ConfigureAwait(false);

        if (opened.IsFailure)
        {
            return opened.Error;
        }

        await using IGrimoireOrdinaryConnectionLease lease = opened.Value;

        Result available = await GuildStorageAvailability.CheckAsync(lease.Connection, null, cancellationToken).ConfigureAwait(false);

        if (available.IsFailure)
        {
            return available;
        }

        return await SettledAsync(owner, lease.Connection, null, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Result> DeleteAsync(GuildCleanupOwner owner, SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        Result settled = await SettledAsync(owner, connection, transaction, cancellationToken).ConfigureAwait(false);

        if (settled.IsFailure)
        {
            return settled;
        }

        await using SqliteCommand command = Command(owner, connection, transaction);

        command.CommandText = Affected(owner) + """
            UPDATE guild_quests SET CancellationConverged=1,Revision=Revision+1,UpdatedAtUtc=$now
            WHERE Id IN(SELECT Id FROM affected) AND CancellationConverged=0;
            """;

        command.Parameters.AddWithValue("$now", UtcInstantText.Format(clock.GetUtcNow()));

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        // Immutable issue/dispatch/result/answer/application evidence and authority-denial lineage
        // remain after owner deletion. Only a qualified full reset removes that namespace.
        return Result.Success();
    }

    private static async Task<Result> SettledAsync(GuildCleanupOwner owner, SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = Command(owner, connection, transaction);

        command.CommandText = Affected(owner)
            + $"SELECT COUNT(*) FROM guild_quests c WHERE c.Id IN(SELECT Id FROM affected) AND {GuildQuestRetentionPins.Unresolved};";

        return Equals(0L, await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))
            ? Result.Success()
            : new Error(ErrorCodes.Guild.ReconciliationRequired, "Guild owner cleanup retains unresolved Quest execution, delivery or accounting evidence.");
    }

    private static string Affected(GuildCleanupOwner owner) => $"""
        WITH RECURSIVE affected(Id) AS (
            SELECT c.Id FROM guild_quests c WHERE {Owned(owner)}
            UNION
            SELECT child.Id FROM guild_quests child JOIN affected parent ON child.ParentQuestId=parent.Id
        )
        """;

    private static string Owned(GuildCleanupOwner owner) => owner.Kind == 1
        ? "(c.CampaignId=$owner COLLATE NOCASE OR c.WorkspaceId=$owner COLLATE NOCASE)" : "c.SessionId=$owner COLLATE NOCASE";

    private static SqliteCommand Command(GuildCleanupOwner owner, SqliteConnection connection, SqliteTransaction? transaction)
    {
        SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.Parameters.AddWithValue("$owner", owner.Id.ToString("D").ToUpperInvariant());

        return command;
    }
}
