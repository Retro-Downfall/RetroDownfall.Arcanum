using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Owner cleanup seals Cohorts through the existing root-gated recovery and retains content-free evidence.</summary>
internal sealed class GuildCohortCleanupParticipant(IGrimoireOrdinaryConnectionFactory connections,
    GuildCohortStore? cohorts, GuildLocalHostPolicy? policy) : IGuildOwnerCleanupParticipant
{
    public async ValueTask<Result> PrepareSealAsync(GuildCleanupOwner owner, SqliteConnection connection, CancellationToken ct)
    {
        Guid? active;

        await using (SqliteCommand command = Command(owner, connection, null, """
            SELECT c.Id FROM guild_quests q JOIN guild_cohorts c ON c.IssuerQuestId=q.Id
            WHERE {OWNER} AND c.Retired=0 AND c.StateCode IN(1,2) ORDER BY q.Id,c.Id LIMIT 1
            """))
        {
            object? value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);

            if (value is null) return Result.Success();

            if (value is not string text || !Guid.TryParseExact(text, "D", out Guid id) || id == Guid.Empty) return Deferred;

            active = id;
        }

        return await DriveOneAsync(active.Value, ct).ConfigureAwait(false);
    }

    public async ValueTask<Result> SealAsync(GuildCleanupOwner owner, SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        await using SqliteCommand command = Command(owner, connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM guild_quests q JOIN guild_cohorts c ON c.IssuerQuestId=q.Id
              WHERE {OWNER} AND c.Retired=0 AND (c.StateCode IN(1,2) OR
                (c.StateCode=3 AND NOT EXISTS(SELECT 1 FROM guild_cohort_stop_frontiers f WHERE f.CohortId=c.Id))))
            """);

        return Equals(0L, await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) ? Result.Success() : Deferred;
    }

    public async ValueTask<Result> ReconcileAsync(GuildCleanupOwner owner, CancellationToken ct)
    {
        Result<IGrimoireOrdinaryConnectionLease> acquired = await connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, ct).ConfigureAwait(false);

        if (acquired.IsFailure) return acquired.Error;

        Guid? work = null;

        await using (IGrimoireOrdinaryConnectionLease lease = acquired.Value)
        await using (SqliteTransaction transaction = lease.Connection.BeginTransaction(deferred: false))
        {
            Result available = await GuildStorageAvailability.CheckAsync(lease.Connection, transaction, ct).ConfigureAwait(false);

            if (available.IsFailure) return available.Error;

            await using SqliteCommand read = Command(owner, lease.Connection, transaction, """
                SELECT c.Id,c.StateCode,a.Closed,
                    EXISTS(SELECT 1 FROM guild_cohort_stop_frontiers f WHERE f.CohortId=c.Id AND f.Completed=0),
                    q.Retired,q.CancellationRequested
                FROM guild_quests q JOIN guild_cohorts c ON c.IssuerQuestId=q.Id
                LEFT JOIN guild_budget_accounts a ON a.AccountId=c.AllocationAccountId
                WHERE {OWNER} AND c.Retired=0 ORDER BY q.Id,c.Id LIMIT 1
                """);

            await using SqliteDataReader reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                await transaction.CommitAsync(ct).ConfigureAwait(false);

                return Result.Success();
            }

            if (!Guid.TryParseExact(reader.GetString(0), "D", out Guid id) || id == Guid.Empty || reader.IsDBNull(2)) return Deferred;

            bool terminal = reader.GetInt32(1) is 4 or 5 or 6;
            bool closed = reader.GetBoolean(2);
            bool pending = reader.GetBoolean(3);
            bool sealedIssuer = reader.GetBoolean(4) && reader.GetBoolean(5);

            await reader.DisposeAsync().ConfigureAwait(false);

            if (terminal && closed && !pending && sealedIssuer)
            {
                await using SqliteCommand redact = lease.Connection.CreateCommand();

                redact.Transaction = transaction;
                redact.CommandText = "UPDATE guild_cohorts SET Name=NULL,Retired=1,Revision=Revision+1 WHERE Id=$id AND Retired=0 AND StateCode IN(4,5,6) AND Revision<9223372036854775807";
                redact.Parameters.AddWithValue("$id", id.ToString("D").ToUpperInvariant());

                if (await redact.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) return Deferred;

                await transaction.CommitAsync(ct).ConfigureAwait(false);

                return Deferred;
            }

            work = id;
        }

        return await DriveOneAsync(work.Value, ct).ConfigureAwait(false);
    }

    public async ValueTask<Result> DeleteAsync(GuildCleanupOwner owner, SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        await using SqliteCommand command = Command(owner, connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM guild_quests q JOIN guild_cohorts c ON c.IssuerQuestId=q.Id
              WHERE {OWNER} AND (c.Retired=0 OR c.StateCode NOT IN(4,5,6)
                OR EXISTS(SELECT 1 FROM guild_cohort_stop_frontiers f WHERE f.CohortId=c.Id AND f.Completed=0)))
            """);

        return Equals(0L, await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) ? Result.Success() : Deferred;
    }

    private async Task<Result> DriveOneAsync(Guid cohortId, CancellationToken ct)
    {
        if (cohorts is null || policy is null) return Deferred;

        Result<GuildCallerContext> caller = await policy.CreateNativeOperatorAsync(Guid.NewGuid(), ct).ConfigureAwait(false);

        if (caller.IsFailure) return caller.Error;

        Result<CohortConvergenceDto> advanced = await cohorts.ReconcileAsync(caller.Value, cohortId, ct).ConfigureAwait(false);

        if (advanced.IsFailure && advanced.Error.Code is not (ErrorCodes.Guild.MaintenanceDeferred or ErrorCodes.Guild.AuthorityUnavailable))
            return advanced.Error;

        return Deferred;
    }

    private static SqliteCommand Command(GuildCleanupOwner owner, SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql.Replace("{OWNER}", owner.Kind == 1
            ? "(q.CampaignId=$owner OR q.WorkspaceId=$owner)" : "q.SessionId=$owner", StringComparison.Ordinal);
        command.Parameters.AddWithValue("$owner", owner.Id.ToString("D").ToUpperInvariant());
        return command;
    }

    private static Error Deferred => new(ErrorCodes.Guild.ReconciliationRequired,
        "Cohort owner cleanup retains active, unsettled, or not-yet-redacted evidence.");
}
