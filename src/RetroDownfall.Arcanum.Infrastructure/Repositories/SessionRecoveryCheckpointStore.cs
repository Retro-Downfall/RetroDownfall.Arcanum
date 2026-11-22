using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Infrastructure.Repositories;

internal sealed class SessionRecoveryCheckpointStore(IGrimoireOrdinaryConnectionFactory connections, TimeProvider time,
    CovenantProcessBootIdentity? bootIdentity = null, GrimoireDatabaseHostedService? databaseHost = null,
    ICovenantAuthoritySnapshotProvider? authority = null)
    : ISessionRecoveryCheckpointStore
{
    private const string Columns = """
        Id,SessionId,AssistantEntryId,ClaimId,KindCode,Provider,Model,ProviderRouteDigest,CampaignId,Workspace,WorkspaceIdentity,
        QuestionEntryId,InvocationId,LastVerifiedInvocationId,ConsumingClaimId
        """;

    public async Task<Result<SessionRecoveryCheckpointRecord>> SaveAsync(SessionTurnClaimLease owner,
        SessionRecoveryCheckpointRecord checkpoint, CancellationToken ct)
    {
        GuildMemoryAccess.DemandOperator();

        if (checkpoint.Id == Guid.Empty || owner.Claim.SessionId != checkpoint.SessionId
            || owner.FutureAssistantEntryId != checkpoint.AssistantEntryId || owner.Claim.ClaimId != checkpoint.ClaimId
            || owner.ExecutorId is not { } executor || owner.OwnerBootId is not { } boot || !owner.IsExecutable)
            return Conflict;

        var opened = await connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, ct).ConfigureAwait(false);

        if (opened.IsFailure) return opened.Error;

        await using var lease = opened.Value;

        try
        {
            return await SqliteBusyRetry.ExecuteAsync(async () =>
            {
                SqliteConnection connection = lease.Connection;

                await using SqliteTransaction tx = connection.BeginTransaction(deferred: false);

                await using SqliteCommand insert = connection.CreateCommand();

                insert.Transaction = tx;

                insert.CommandText = """
                    INSERT INTO session_recovery_checkpoints(Id,SessionId,AssistantEntryId,ClaimId,KindCode,Provider,Model,
                        ProviderRouteDigest,CampaignId,Workspace,WorkspaceIdentity,QuestionEntryId,InvocationId,LastVerifiedInvocationId,CreatedAtUtcTicks)
                    SELECT $id,$session,$assistant,$claim,$kind,$provider,$model,$route,$campaign,$workspace,$identity,
                        $question,$invocation,$verified,$created
                    WHERE EXISTS(SELECT 1 FROM session_turn_claims c WHERE c.ClaimId=$claim AND c.SessionId=$session
                        AND c.AssistantEntryId=$assistant AND c.StateCode=2 AND c.OwnerBootId=$boot AND c.ExecutorId=$executor
                        AND c.LeaseDeadlineUtc > $now)
                    AND ($question IS NULL OR EXISTS(SELECT 1 FROM Entries e WHERE e.SessionId=$session AND e.Id=$question AND e.ToolName='ask_human'))
                    ON CONFLICT(ClaimId) DO NOTHING
                    """;

                Add(insert, "$id", Id(checkpoint.Id));
                Add(insert, "$session", Id(checkpoint.SessionId));
                Add(insert, "$assistant", Id(checkpoint.AssistantEntryId));
                Add(insert, "$claim", Id(checkpoint.ClaimId));
                Add(insert, "$kind", (int)checkpoint.Kind);
                Add(insert, "$provider", checkpoint.Scope.Provider);
                Add(insert, "$model", checkpoint.Scope.Model);
                Add(insert, "$route", checkpoint.Scope.ProviderRouteDigest.Bytes.ToArray());
                Add(insert, "$campaign", Id(checkpoint.Scope.CampaignId));
                Add(insert, "$workspace", checkpoint.Scope.Workspace);
                Add(insert, "$identity", checkpoint.Scope.WorkspaceIdentity is { } identity ? identity.Bytes.ToArray() : null);
                Add(insert, "$question", Id(checkpoint.QuestionEntryId));
                Add(insert, "$invocation", Id(checkpoint.InvocationId));
                Add(insert, "$verified", Id(checkpoint.LastVerifiedInvocationId));
                Add(insert, "$created", time.GetUtcNow().UtcTicks);
                Add(insert, "$boot", Id(boot));
                Add(insert, "$executor", Id(executor));
                Add(insert, "$now", UtcInstantText.Format(time.GetUtcNow()));

                _ = await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                var saved = await FindWithinAsync(connection, tx, checkpoint.SessionId, checkpoint.Id, ct).ConfigureAwait(false);

                if (saved != checkpoint) return Result<SessionRecoveryCheckpointRecord>.Failure(Conflict);

                await tx.CommitAsync(ct).ConfigureAwait(false);

                return Result<SessionRecoveryCheckpointRecord>.Success(saved);
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqliteException or FormatException)
        {
            return Unavailable;
        }
    }

    public Task<Result<SessionRecoveryCheckpointRecord?>> FindAsync(Guid sessionId, Guid? checkpointId, CancellationToken ct) =>
        ReadAsync<SessionRecoveryCheckpointRecord?>(async (connection, token) =>
            await FindWithinAsync(connection, null, sessionId, checkpointId, token).ConfigureAwait(false), ct);

    public Task<Result<SessionRecoveryReadSnapshot>> ReadStatusAsync(Guid sessionId, Guid? after, CancellationToken ct) =>
        ReadAsync<SessionRecoveryReadSnapshot>(async (connection, token) =>
        {
            await using SqliteTransaction tx = connection.BeginTransaction(deferred: true);

            SessionRecoveryCheckpointRecord? checkpoint =
                await FindWithinAsync(connection, tx, sessionId, null, token).ConfigureAwait(false);

            Guid? currentBoot = null;

            Guid? currentInstallation = null;

            if (bootIdentity is { } boot && authority?.Current is { } snapshot
                && Guid.TryParse(snapshot.InstallationIdentity, out Guid installation) && installation != Guid.Empty
                && databaseHost?.OwnsStartedDatabase(connection) == true)
            {
                currentBoot = boot.BootId;

                currentInstallation = installation;
            }

            var effects = await SqliteToolInvocationStore.ReadInterruptedEffectsWithinAsync(connection, tx,
                sessionId, after, currentBoot, currentInstallation, token).ConfigureAwait(false);

            return effects.IsSuccess ? new SessionRecoveryReadSnapshot(checkpoint, effects.Value) : effects.Error;
        }, ct);

    private async Task<Result<T>> ReadAsync<T>(Func<SqliteConnection, CancellationToken, Task<Result<T>>> read, CancellationToken ct)
    {
        GuildMemoryAccess.DemandOperator();

        var opened = await connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly, ct).ConfigureAwait(false);

        if (opened.IsFailure) return opened.Error;

        await using var lease = opened.Value;

        try
        {
            return await read(lease.Connection, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidDataException or FormatException)
        {
            return Unavailable;
        }
    }

    private static async Task<SessionRecoveryCheckpointRecord?> FindWithinAsync(SqliteConnection connection, SqliteTransaction? tx,
        Guid sessionId, Guid? checkpointId, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = tx;

        command.CommandText = "SELECT " + Columns + """
             FROM session_recovery_checkpoints
             WHERE SessionId=$session AND (Id=$id OR ($id IS NULL AND ConsumingClaimId IS NULL))
             ORDER BY CreatedAtUtcTicks DESC,Id LIMIT 1
            """;

        Add(command, "$session", Id(sessionId));
        Add(command, "$id", Id(checkpointId));

        await using var row = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await row.ReadAsync(ct).ConfigureAwait(false)) return null;

        return new(Guid.ParseExact(row.GetString(0), "D"), Guid.ParseExact(row.GetString(1), "D"),
            Guid.ParseExact(row.GetString(2), "D"), Guid.ParseExact(row.GetString(3), "D"), (SessionRecoveryKind)row.GetInt32(4),
            new(row.GetString(5), row.GetString(6), new CovenantDigest((byte[])row.GetValue(7)), ReadId(row, 8),
                row.IsDBNull(9) ? null : row.GetString(9), row.IsDBNull(10) ? null : new CovenantDigest((byte[])row.GetValue(10))),
            ReadId(row, 11), ReadId(row, 12), ReadId(row, 13), ReadId(row, 14));
    }

    internal static async Task<Result> ConsumeWithinAsync(SqliteConnection connection, SqliteTransaction tx,
        SessionTurnClaimLease owner, Guid checkpointId, CancellationToken ct)
    {
        SessionRecoveryCheckpointRecord? checkpoint = await FindWithinAsync(connection, tx, owner.Claim.SessionId, checkpointId, ct)
            .ConfigureAwait(false);

        if (checkpoint is null || checkpoint.ConsumingClaimId is not null) return Conflict;

        if (checkpoint is { Kind: SessionRecoveryKind.ReconciliationRequired, InvocationId: { } receipt })
        {
            var proof = await SqliteToolInvocationStore.ReadWithinAsync(connection, tx, checkpoint.SessionId, receipt, ct)
                .ConfigureAwait(false);

            if (proof.IsFailure) return proof.Error;

            if (proof.Value?.AssistantEntryId != checkpoint.AssistantEntryId
                || proof.Value is not ({ State: ToolInvocationState.Prepared }
                    or { State: ToolInvocationState.Completed, ResultEntryId: not null })) return Conflict;
        }

        var progress = await SqliteToolInvocationStore.ReadTurnProgressWithinAsync(connection, tx, checkpoint.SessionId,
            checkpoint.AssistantEntryId, ct).ConfigureAwait(false);

        if (progress.IsFailure) return progress.Error;

        if (progress.Value.OutstandingInvocationId is not null) return Conflict;

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = tx;

        command.CommandText = """
            UPDATE session_recovery_checkpoints AS p SET ConsumingClaimId=$claim
            WHERE p.Id=$id AND p.SessionId=$session AND p.ConsumingClaimId IS NULL AND p.ClaimId<>$claim
              AND EXISTS(SELECT 1 FROM session_turn_claims c WHERE c.ClaimId=p.ClaimId AND c.SessionId=$session
                AND c.StateCode IN(3,4,6))
              AND (p.QuestionEntryId IS NULL OR EXISTS(SELECT 1 FROM Entries e WHERE e.Id=p.QuestionEntryId AND e.SessionId=$session))
            """;

        Add(command, "$session", Id(owner.Claim.SessionId));
        Add(command, "$id", Id(checkpointId));
        Add(command, "$claim", Id(owner.Claim.ClaimId));

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1 ? Result.Success() : Conflict;
    }

    private static Guid? ReadId(SqliteDataReader row, int index) => row.IsDBNull(index) ? null : Guid.ParseExact(row.GetString(index), "D");

    private static string? Id(Guid? id) => id?.ToString("D").ToUpperInvariant();

    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static Error Conflict => new(ErrorCodes.Hub.SessionRecoveryConflict, "This recovery checkpoint cannot be used for the current Session turn.");

    private static Error Unavailable => new(ErrorCodes.Hub.Error, "The Session recovery checkpoint is unavailable.");
}
