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
        var opened = await connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, cancellationToken).ConfigureAwait(false);

        if (opened.IsFailure) return opened.Error;

        await using IGrimoireOrdinaryConnectionLease lease = opened.Value;

        Result available = await GuildStorageAvailability.CheckAsync(lease.Connection, null, cancellationToken).ConfigureAwait(false);

        if (available.IsFailure) return available;

        string? after = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using SqliteTransaction transaction = lease.Connection.BeginTransaction(deferred: false);

            List<(Guid Root, Guid Quest, int State)> page = [];

            await using (SqliteCommand candidates = Command(owner, lease.Connection, transaction))
            {
                candidates.CommandText = Affected(owner) + """
                    SELECT c.RootQuestId,c.Id,c.AccountingStateCode FROM guild_quests c
                    WHERE c.Id IN(SELECT Id FROM affected) AND c.Id>$after ORDER BY c.Id LIMIT 100
                    """;

                candidates.Parameters.AddWithValue("$after", after ?? string.Empty);

                await using SqliteDataReader reader = await candidates.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (!Guid.TryParse(reader.GetString(0), out Guid root) || root == Guid.Empty
                        || !Guid.TryParse(reader.GetString(1), out Guid quest) || quest == Guid.Empty
                        || reader.GetValue(2) is not long state || state is < 0 or > 2)
                        return new Error(ErrorCodes.Guild.ReconciliationRequired, "Guild accounting evidence is inconsistent.");

                    page.Add((root, quest, (int)state));
                }
            }

            foreach (var candidate in page)
            {
                if (candidate.State == 0) continue;

                Result<bool> refreshed = await GuildQuestStore.RefreshAccountingProjectionWithinAsync(lease.Connection,
                    transaction, candidate.Root, candidate.Quest, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);

                if (refreshed.IsFailure) return refreshed.Error;
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            if (page.Count < 100) break;

            after = page[^1].Quest.ToString("D").ToUpperInvariant();
        }

        await using SqliteTransaction finalTransaction = lease.Connection.BeginTransaction(deferred: false);

        Result settled = await SettledAsync(owner, lease.Connection, finalTransaction, cancellationToken).ConfigureAwait(false);

        await finalTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return settled;
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
        if (transaction is null) return new Error(ErrorCodes.Guild.ReconciliationRequired, "Guild owner cleanup requires a transaction.");

        await using (SqliteCommand command = Command(owner, connection, transaction))
        {
            command.CommandText = Affected(owner)
                + $"SELECT COUNT(*) FROM guild_quests c WHERE c.Id IN(SELECT Id FROM affected) AND (c.AccountingStateCode<>0 OR {GuildQuestRetentionPins.NonAccountingUnresolved});";

            if (!Equals(0L, await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)))
                return new Error(ErrorCodes.Guild.ReconciliationRequired, "Guild owner cleanup retains unresolved Quest execution, delivery or accounting evidence.");
        }

        await using SqliteCommand roots = Command(owner, connection, transaction);

        roots.CommandText = Affected(owner) + """
            SELECT c.RootQuestId,c.Id FROM guild_quests c WHERE c.Id IN(SELECT Id FROM affected)
              AND (c.ParentQuestId IS NULL OR c.ParentQuestId NOT IN(SELECT Id FROM affected)) ORDER BY c.Id
            """;

        await using SqliteDataReader reader = await roots.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!Guid.TryParse(reader.GetString(0), out Guid root) || !Guid.TryParse(reader.GetString(1), out Guid quest)
                || !await GuildQuestStore.AccountingClearWithinAsync(connection, transaction, root, quest, cancellationToken).ConfigureAwait(false))
                return new Error(ErrorCodes.Guild.ReconciliationRequired, "Guild owner cleanup retains unresolved Quest execution, delivery or accounting evidence.");
        }

        return Result.Success();
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
