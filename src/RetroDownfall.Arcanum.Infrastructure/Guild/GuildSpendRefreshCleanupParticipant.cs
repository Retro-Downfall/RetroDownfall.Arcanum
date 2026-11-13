using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Closes live refresh roots around owner sealing without holding a database lock while joining.</summary>
internal sealed class GuildSpendRefreshCleanupParticipant(GuildSpendRefreshTracker tracker)
{
    internal async Task<Result<Scope>> PrepareAsync(GuildCleanupOwner owner, SqliteConnection connection, CancellationToken ct)
    {
        Result evidence = await ValidateEvidenceAsync(owner, connection, null, ct).ConfigureAwait(false);

        if (evidence.IsFailure) return evidence.Error;

        List<Guid> affected = [];

        foreach (Guid root in tracker.ActiveRoots())
        {
            if (await IsAffectedAsync(owner, root, connection, null, ct).ConfigureAwait(false)) affected.Add(root);
        }

        ct.ThrowIfCancellationRequested();

        // Once closure starts it must physically join even if the outer cleanup is cancelled.
        IDisposable closure = await tracker.CloseRootsScopedAsync(affected).ConfigureAwait(false);

        return new Scope(closure, affected);
    }

    internal async Task<Result> ValidateWithinAsync(GuildCleanupOwner owner, Scope scope, SqliteConnection connection,
        SqliteTransaction transaction, CancellationToken ct)
    {
        Result evidence = await ValidateEvidenceAsync(owner, connection, transaction, ct).ConfigureAwait(false);

        if (evidence.IsFailure) return evidence;

        // The writer prevents a newly entered attempt from verifying its source until sealing commits.
        // An affected root that entered before this snapshot was not joined: defer and retry.
        foreach (Guid root in tracker.ActiveRoots())
        {
            if (!scope.Contains(root) && await IsAffectedAsync(owner, root, connection, transaction, ct).ConfigureAwait(false))
                return Deferred;
        }

        return Result.Success();
    }

    private static async Task<Result> ValidateEvidenceAsync(GuildCleanupOwner owner, SqliteConnection connection,
        SqliteTransaction? transaction, CancellationToken ct)
    {
        if (owner.Kind is not (1 or 2) || owner.Id == Guid.Empty) return Deferred;

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = owner.Kind == 1 ? """
            SELECT EventSequence FROM guild_owner_cleanup WHERE OwnerKindCode=$kind AND OwnerId=$owner AND PhaseCode BETWEEN 0 AND 5
              AND NOT EXISTS(SELECT 1 FROM Campaigns WHERE Id=$owner);
            """ : """
            SELECT EventSequence FROM guild_owner_cleanup WHERE OwnerKindCode=$kind AND OwnerId=$owner AND PhaseCode BETWEEN 0 AND 5
              AND NOT EXISTS(SELECT 1 FROM Sessions WHERE Id=$owner);
            """;

        command.Parameters.AddWithValue("$kind", owner.Kind);

        command.Parameters.AddWithValue("$owner", Format(owner.Id));

        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return Deferred;

            object sequence = reader.GetValue(0);

            if (owner.EventSequence is { } expected)
            {
                if (sequence is not long saved || saved != expected || expected <= 0) return Deferred;
            }
            else if (sequence is not DBNull) return Deferred;
        }

        if (owner.EventSequence is { } eventSequence)
        {
            command.CommandText = "SELECT COUNT(*) FROM owner_deletion_events WHERE Sequence=$sequence AND OwnerKindCode=$kind AND OwnerId=$owner";

            command.Parameters.AddWithValue("$sequence", eventSequence);

            if (!Equals(1L, await command.ExecuteScalarAsync(ct).ConfigureAwait(false))) return Deferred;
        }

        return Result.Success();
    }

    private static async Task<bool> IsAffectedAsync(GuildCleanupOwner owner, Guid root, SqliteConnection connection,
        SqliteTransaction? transaction, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = owner.Kind == 1 ? """
            SELECT EXISTS(SELECT 1 FROM guild_quests INDEXED BY idx_guild_quests_root WHERE RootQuestId=$root
                AND (CampaignId=$owner OR WorkspaceId=$owner) LIMIT 1);
            """ : """
            SELECT EXISTS(SELECT 1 FROM guild_quests INDEXED BY idx_guild_quests_root WHERE RootQuestId=$root
                AND SessionId=$owner LIMIT 1);
            """;

        command.Parameters.AddWithValue("$root", Format(root));

        command.Parameters.AddWithValue("$owner", Format(owner.Id));

        return Equals(1L, await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    private static string Format(Guid id) => id.ToString("D").ToUpperInvariant();

    private static Error Deferred => new(ErrorCodes.Guild.Unavailable, "Guild owner cleanup cannot prove refresh closure.");

    internal sealed class Scope(IDisposable closure, IEnumerable<Guid> roots) : IDisposable
    {
        private readonly HashSet<Guid> _roots = [.. roots];

        internal bool Contains(Guid root) => _roots.Contains(root);

        public void Dispose() => closure.Dispose();
    }
}
