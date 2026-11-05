using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildRosterCursorState(int Stage, string? After, long? SnapshotSequence,
    Guid? GlobalDiscoveryId = null, Guid? WorkspaceDiscoveryId = null);

/// <summary>Content-free accepted discovery receipts; raw Card bytes remain in guild_content.</summary>
internal sealed class GuildRosterRefreshStore(IGrimoireOrdinaryConnectionFactory connections,
    IGrimoireConnectionAdmissionGate gate, GuildRosterRefreshCoordinator coordination)
{
    internal SemaphoreSlim RefreshGate => coordination.Gate;

    internal Task<Result<GuildRefreshReceipt?>> FindAsync(Guid installationGeneration, Guid principalId,
        GuildMutationIdentity mutation, CancellationToken cancellationToken) => ExecuteAsync(false,
            (connection, token) => FindWithinAsync(connection, null, installationGeneration, principalId, mutation, token), cancellationToken);

    internal Task<Result<GuildRefreshReceipt>> AcceptAsync(Guid installationGeneration, Guid principalId,
        GuildMutationIdentity mutation, GuildRefreshReceipt receipt, CancellationToken cancellationToken) => ExecuteAsync(true,
            async (connection, token) =>
            {
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, GuildJsonContext.Default.GuildRefreshReceipt);

                if (bytes.Length > 65536 || installationGeneration == Guid.Empty || principalId == Guid.Empty
                    || mutation.IdempotencyKey == Guid.Empty || mutation.CanonicalRequestDigest.Length != 64
                    || receipt.Receipt.IdempotencyKey != mutation.IdempotencyKey)
                {
                    return Result<GuildRefreshReceipt>.Failure(new Error(ErrorCodes.Guild.InvalidRequest, "The refresh receipt is invalid."));
                }

                await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

                Result<GuildRefreshReceipt?> prior = await FindWithinAsync(connection, transaction,
                    installationGeneration, principalId, mutation, token).ConfigureAwait(false);

                if (prior.IsFailure)
                {
                    return Result<GuildRefreshReceipt>.Failure(prior.Error);
                }

                if (prior.Value is { } existing)
                {
                    return Result<GuildRefreshReceipt>.Success(existing);
                }

                await using SqliteCommand command = connection.CreateCommand();

                command.Transaction = transaction;

                command.CommandText = """
                    INSERT INTO guild_roster_refreshes(InstallationGeneration,PrincipalId,IdempotencyKey,CanonicalRequestDigest,NextCursor,ResultJson)
                    VALUES(@generation,@principal,@key,@digest,@next,@result);
                    """;

                BindIdentity(command, installationGeneration, principalId, mutation);

                command.Parameters.AddWithValue("@digest", mutation.CanonicalRequestDigest);

                command.Parameters.AddWithValue("@result", bytes);

                command.Parameters.AddWithValue("@next", (object?)receipt.Diagnostics.NextCursor ?? DBNull.Value);

                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                await transaction.CommitAsync(token).ConfigureAwait(false);

                return Result<GuildRefreshReceipt>.Success(receipt);
            }, cancellationToken);

    internal Task<Result<string>> SaveCursorAsync(Guid generation, Guid principal, string binding, string kind,
        GuildRosterCursorState state, CancellationToken cancellationToken) => ExecuteAsync(true, async (connection, token) =>
        {
            if (generation == Guid.Empty || principal == Guid.Empty || binding.Length != 64 || kind is not ("list" or "refresh")
                || state.Stage is < 0 or > 4 || state.SnapshotSequence < 0
                || state.GlobalDiscoveryId == Guid.Empty || state.WorkspaceDiscoveryId == Guid.Empty
                || state.After is not null && Encoding.UTF8.GetByteCount(state.After) > 1024)
            {
                return Result<string>.Failure(new Error(ErrorCodes.Guild.CursorInvalid, "The continuation binding is invalid."));
            }

            string cursor = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = """
                INSERT INTO guild_roster_cursors(Cursor,InstallationGeneration,PrincipalId,BindingDigest,Kind,Stage,AfterKey,SnapshotSequence,GlobalDiscoveryId,WorkspaceDiscoveryId)
                VALUES(@cursor,@generation,@principal,@binding,@kind,@stage,@after,@snapshot,@globalScan,@workspaceScan);
                """;

            command.Parameters.AddWithValue("@cursor", cursor);

            command.Parameters.AddWithValue("@generation", generation.ToString("D"));

            command.Parameters.AddWithValue("@principal", principal.ToString("D"));

            command.Parameters.AddWithValue("@binding", binding);

            command.Parameters.AddWithValue("@kind", kind);

            command.Parameters.AddWithValue("@stage", state.Stage);

            command.Parameters.AddWithValue("@after", (object?)state.After ?? DBNull.Value);

            command.Parameters.AddWithValue("@snapshot", (object?)state.SnapshotSequence ?? DBNull.Value);

            command.Parameters.AddWithValue("@globalScan", (object?)state.GlobalDiscoveryId?.ToString("D") ?? DBNull.Value);

            command.Parameters.AddWithValue("@workspaceScan", (object?)state.WorkspaceDiscoveryId?.ToString("D") ?? DBNull.Value);

            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);

            return Result<string>.Success(cursor);
        }, cancellationToken);

    internal Task<Result<GuildRosterCursorState>> ReadCursorAsync(Guid generation, Guid principal, string binding, string kind,
        string cursor, CancellationToken cancellationToken)
    {
        if (cursor.Length != 64 || cursor.Any(value => !Uri.IsHexDigit(value)))
        {
            return Task.FromResult(Result<GuildRosterCursorState>.Failure(
                new Error(ErrorCodes.Guild.CursorInvalid, "The continuation is invalid.")));
        }

        return ExecuteAsync(false, async (connection, token) =>
        {
            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = """
                SELECT InstallationGeneration,PrincipalId,BindingDigest,Kind,Stage,AfterKey,SnapshotSequence,GlobalDiscoveryId,WorkspaceDiscoveryId,
                    CASE WHEN typeof(InstallationGeneration)='text' AND length(CAST(InstallationGeneration AS BLOB))=36
                        AND typeof(PrincipalId)='text' AND length(CAST(PrincipalId AS BLOB))=36
                        AND typeof(BindingDigest)='text' AND length(BindingDigest)=64 AND length(CAST(BindingDigest AS BLOB))<=256
                        AND Kind IN('list','refresh')
                        AND typeof(Stage)='integer' AND Stage BETWEEN 0 AND 4
                        AND (AfterKey IS NULL OR (typeof(AfterKey)='text' AND length(CAST(AfterKey AS BLOB))<=1024))
                        AND (SnapshotSequence IS NULL OR (typeof(SnapshotSequence)='integer' AND SnapshotSequence>=0))
                        AND (GlobalDiscoveryId IS NULL OR (typeof(GlobalDiscoveryId)='text' AND length(CAST(GlobalDiscoveryId AS BLOB))=36))
                        AND (WorkspaceDiscoveryId IS NULL OR (typeof(WorkspaceDiscoveryId)='text' AND length(CAST(WorkspaceDiscoveryId AS BLOB))=36))
                    THEN 1 ELSE 0 END
                FROM guild_roster_cursors WHERE Cursor=@cursor;
                """;

            command.Parameters.AddWithValue("@cursor", cursor);

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);

            if (!await reader.ReadAsync(token).ConfigureAwait(false))
            {
                return Result<GuildRosterCursorState>.Failure(new Error(ErrorCodes.Guild.CursorGone,
                    "The retained continuation is no longer available."));
            }

            // Inspect SQLite's byte counts before materializing any retained variable text. The
            // digest envelope allows every UTF-8 encoding of the existing 64-character contract.
            if (reader.GetInt64(9) != 1)
            {
                return Result<GuildRosterCursorState>.Failure(new Error(ErrorCodes.Guild.Unavailable,
                    "The retained continuation is unavailable."));
            }

            if (reader.GetString(0) != generation.ToString("D"))
            {
                return Result<GuildRosterCursorState>.Failure(new Error(ErrorCodes.Guild.CursorGone,
                    "The retained continuation is no longer available."));
            }

            if (reader.GetString(1) != principal.ToString("D") || reader.GetString(2) != binding || reader.GetString(3) != kind)
            {
                return Result<GuildRosterCursorState>.Failure(new Error(ErrorCodes.Guild.CursorInvalid,
                    "The continuation does not match this caller and query."));
            }

            if (!TryReadDiscoveryId(reader, 7, out Guid? globalDiscovery)
                || !TryReadDiscoveryId(reader, 8, out Guid? workspaceDiscovery))
            {
                return Result<GuildRosterCursorState>.Failure(new Error(ErrorCodes.Guild.Unavailable,
                    "The retained continuation is unavailable."));
            }

            return Result<GuildRosterCursorState>.Success(new(reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetInt64(6),
                globalDiscovery, workspaceDiscovery));
        }, cancellationToken);
    }

    private static bool TryReadDiscoveryId(SqliteDataReader reader, int ordinal, out Guid? value)
    {
        value = null;

        if (reader.IsDBNull(ordinal))
        {
            return true;
        }

        if (!Guid.TryParseExact(reader.GetString(ordinal), "D", out Guid parsed) || parsed == Guid.Empty)
        {
            return false;
        }

        value = parsed;

        return true;
    }

    private static async Task<Result<GuildRefreshReceipt?>> FindWithinAsync(SqliteConnection connection,
        SqliteTransaction? transaction, Guid generation, Guid principal, GuildMutationIdentity mutation, CancellationToken token)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT CanonicalRequestDigest,ResultJson,
                CASE WHEN typeof(CanonicalRequestDigest)='text' AND length(CanonicalRequestDigest)=64
                    AND length(CAST(CanonicalRequestDigest AS BLOB))<=256
                    AND typeof(ResultJson)='blob' AND length(ResultJson)<=65536
                THEN 1 ELSE 0 END
            FROM guild_roster_refreshes
            WHERE InstallationGeneration=@generation AND PrincipalId=@principal AND IdempotencyKey=@key;
            """;

        BindIdentity(command, generation, principal, mutation);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);

        if (!await reader.ReadAsync(token).ConfigureAwait(false))
        {
            return Result<GuildRefreshReceipt?>.Success(null);
        }

        if (reader.GetInt64(2) != 1)
        {
            return Result<GuildRefreshReceipt?>.Failure(new Error(ErrorCodes.Guild.Unavailable,
                "The retained refresh receipt is unavailable."));
        }

        if (!string.Equals(reader.GetString(0), mutation.CanonicalRequestDigest, StringComparison.Ordinal))
        {
            return Result<GuildRefreshReceipt?>.Failure(new Error(ErrorCodes.Guild.IdempotencyConflict,
                "The refresh key was already used for different intent."));
        }

        GuildRefreshReceipt? receipt = JsonSerializer.Deserialize((byte[])reader.GetValue(1), GuildJsonContext.Default.GuildRefreshReceipt);

        return receipt is null
            ? Result<GuildRefreshReceipt?>.Failure(new Error(ErrorCodes.Guild.Unavailable, "The retained refresh receipt is unavailable."))
            : Result<GuildRefreshReceipt?>.Success(receipt with { Receipt = receipt.Receipt with { Replayed = true } });
    }

    private static void BindIdentity(SqliteCommand command, Guid generation, Guid principal, GuildMutationIdentity mutation)
    {
        command.Parameters.AddWithValue("@generation", generation.ToString("D"));

        command.Parameters.AddWithValue("@principal", principal.ToString("D"));

        command.Parameters.AddWithValue("@key", mutation.IdempotencyKey.ToString("D"));
    }

    private async Task<Result<T>> ExecuteAsync<T>(bool write, Func<SqliteConnection, CancellationToken, Task<Result<T>>> action,
        CancellationToken cancellationToken)
    {
        if (!gate.TryAcquireWorkLease(GrimoireWorkKind.GuildStorage, out IGrimoireWorkLease? work) || work is null)
        {
            return Result<T>.Failure(new Error(ErrorCodes.Guild.MaintenanceDeferred, "Guild storage is paused for maintenance."));
        }

        await using (work)
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, work.MaintenanceRevocation);

            if (!work.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? effect) || effect is null)
            {
                return Result<T>.Failure(new Error(ErrorCodes.Guild.MaintenanceDeferred, "Guild storage is paused for maintenance."));
            }

            await using (effect)
            {
                try
                {
                    Result<IGrimoireOrdinaryConnectionLease> acquired = await connections.OpenFreshAsync(write
                        ? GrimoireOrdinaryFreshConnectionKind.ReadWrite : GrimoireOrdinaryFreshConnectionKind.ReadOnly,
                        linked.Token).ConfigureAwait(false);

                    if (acquired.IsFailure)
                    {
                        return Result<T>.Failure(acquired.Error);
                    }

                    await using IGrimoireOrdinaryConnectionLease lease = acquired.Value;

                    Result available = await GuildStorageAvailability.CheckAsync(lease.Connection, null, linked.Token).ConfigureAwait(false);

                    if (available.IsFailure)
                    {
                        return Result<T>.Failure(available.Error);
                    }

                    return await action(lease.Connection, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return Result<T>.Failure(new Error(ErrorCodes.Guild.MaintenanceDeferred, "Guild storage is paused for maintenance."));
                }
                catch (Exception error) when (error is SqliteException or JsonException)
                {
                    return Result<T>.Failure(new Error(ErrorCodes.Guild.Unavailable, "The Guild refresh receipt store is unavailable."));
                }
            }
        }
    }
}
