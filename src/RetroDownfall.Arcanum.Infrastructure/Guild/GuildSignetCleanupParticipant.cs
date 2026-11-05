using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Seals owned authority before payload purge; denial lineage is not an age-pruned cache.</summary>
internal sealed class GuildSignetCleanupParticipant(IGrimoireConnectionAdmissionGate admission) : IGuildOwnerCleanupParticipant
{
    public async ValueTask<Result> PrepareSealAsync(GuildCleanupOwner owner, SqliteConnection connection, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Guid root;

            // Materialize one root, dispose its command, then wait without a SQLite transaction.
            // Sealing it removes that root from the next query without imposing a work ceiling.
            await using (SqliteCommand command = OwnerCommand(owner, connection, null))
            {
                command.CommandText = $"SELECT CASE WHEN typeof(RootCommissionId)='text' AND length(CAST(RootCommissionId AS BLOB))=36 THEN RootCommissionId ELSE '' END FROM guild_signets WHERE {OwnerPredicate(owner)} AND (Revoked=0 OR CancellationRequested=0) ORDER BY RootCommissionId LIMIT 1;";

                object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

                if (value is null)
                {
                    return Result.Success();
                }

                if (value is not string text || !Guid.TryParseExact(text, "D", out root) || root == Guid.Empty
                    || !string.Equals(text, root.ToString("D").ToUpperInvariant(), StringComparison.Ordinal))
                {
                    return Deferred;
                }
            }

            Result<int> sealedRoot = await GuildSignetRootGate.RunAsync<int>(admission, root, async token =>
            {
                await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

                Result available = await GuildStorageAvailability.CheckAsync(connection, transaction, token).ConfigureAwait(false);

                if (available.IsFailure)
                {
                    return available.Error;
                }

                await using SqliteCommand command = OwnerCommand(owner, connection, transaction);

                command.CommandText = $"UPDATE guild_signets SET Revoked=1,CancellationRequested=1,RevocationGeneration=RevocationGeneration+1,Revision=Revision+1 WHERE {OwnerPredicate(owner)} AND RootCommissionId=$root COLLATE NOCASE AND (Revoked=0 OR CancellationRequested=0);";

                command.Parameters.AddWithValue("$root", root.ToString("D").ToUpperInvariant());

                int changed = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                await transaction.CommitAsync(token).ConfigureAwait(false);

                return Result<int>.Success(changed);
            }, cancellationToken).ConfigureAwait(false);

            if (sealedRoot.IsFailure)
            {
                return sealedRoot.Error;
            }
        }
    }

    public async ValueTask<Result> SealAsync(GuildCleanupOwner owner, SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = OwnerCommand(owner, connection, transaction);

        command.CommandText = $"SELECT COUNT(*) FROM guild_signets WHERE {OwnerPredicate(owner)} AND (Revoked=0 OR CancellationRequested=0);";

        // The family transaction never acquires a root gate or revokes authority. A new unsealed
        // row appearing after preparation requires another admission pass before cursor completion.
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is 0L
            ? Result.Success() : Result.Failure(Deferred);
    }

    public ValueTask<Result> ReconcileAsync(GuildCleanupOwner owner, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // No execution is enabled in this slice. The Commission cleanup participant must reconcile
        // actual work before this coordinator reaches its final deletion transaction.
        return ValueTask.FromResult(Result.Success());
    }

    public ValueTask<Result> DeleteAsync(GuildCleanupOwner owner, SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Until the Commission slice can prove terminal/replay/settlement closure, retained
        // denial evidence outlives its deleted owner. Physical removal belongs to full reset.
        return ValueTask.FromResult(Result.Success());
    }

    private static Error Deferred => new(ErrorCodes.Guild.Unavailable,
        "Guild owner authority sealing is deferred; its family cursor has not advanced.");

    private static string OwnerPredicate(GuildCleanupOwner owner) => owner.Kind == 1
        ? "(CampaignId=$owner COLLATE NOCASE OR WorkspaceId=$owner COLLATE NOCASE)" : "SessionId=$owner COLLATE NOCASE";

    private static SqliteCommand OwnerCommand(GuildCleanupOwner owner, SqliteConnection connection, SqliteTransaction? transaction)
    {
        SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.Parameters.AddWithValue("$owner", owner.Id.ToString("D").ToUpperInvariant());

        return command;
    }
}
