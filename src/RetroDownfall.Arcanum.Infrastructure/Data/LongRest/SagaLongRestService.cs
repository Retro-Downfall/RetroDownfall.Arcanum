using System.Data;
using System.Data.Common;
using System.Security.Cryptography;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

/// <summary>The owning Saga transaction for explicit exact-version consolidation declarations.</summary>
internal sealed partial class SagaLongRestService(ArcanumDbContext db, TimeProvider timeProvider) : ILongRestService
{
    public async Task<Result<LongRestReceipt>> ApplyAsync(LongRestRequest request, CancellationToken cancellationToken)
    {
        Result valid = LongRestPolicy.ValidateRequest(request);

        if (valid.IsFailure)
        {
            return Result<LongRestReceipt>.Failure(valid.Error);
        }

        // Own the declaration before awaiting: mutable caller arrays cannot change the exact group
        // after validation while the admitted connection is being acquired.
        request = request with { Targets = request.Targets.ToArray() };

        try
        {
            return await SqliteBusyRetry.ExecuteAsync(async () =>
            {
                DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

                await using SqliteTransaction transaction = ((SqliteConnection)connection).BeginTransaction(deferred: false);

                if (await GrimoireCoreSchemaVersion.ReadAsync(connection, cancellationToken, transaction).ConfigureAwait(false) < 17)
                {
                    return Refuse(ErrorCodes.LongRest.Unavailable, "The Long Rest schema is not ready.");
                }

                List<LoadedSnapshot> loaded = [];

                foreach (LongRestTarget target in request.Targets)
                {
                    Result<LoadedSnapshot> snapshot = await ReadSnapshotAsync(connection, transaction, target, cancellationToken).ConfigureAwait(false);

                    if (snapshot.IsFailure)
                    {
                        return Result<LongRestReceipt>.Failure(snapshot.Error);
                    }

                    loaded.Add(snapshot.Value);
                }

                LongRestSnapshot[] snapshots = loaded.Select(item => item.Snapshot).ToArray();

                // A successor can add new incoming dependency edges to an old exact input.
                // That changes today's context, but cannot invalidate the already applied decision.
                Result<LongRestReceipt>? historical = await ReadHistoricalAppliedReceiptAsync(
                    connection, transaction, request, snapshots, cancellationToken).ConfigureAwait(false);

                if (historical is not null)
                {
                    return historical;
                }

                Result<string> identity = LongRestPolicy.Identify(request, snapshots);

                if (identity.IsFailure)
                {
                    return Result<LongRestReceipt>.Failure(identity.Error);
                }

                // Replay is historical evidence, even after an input's head changes. No receipt,
                // projection or memory write takes place on this path.
                Result<LongRestReceipt>? replay = await ReadReceiptAsync(connection, transaction, identity.Value, cancellationToken).ConfigureAwait(false);

                if (replay is not null)
                {
                    return replay;
                }

                foreach (LoadedSnapshot input in loaded)
                {
                    if (input.Content is { } content
                        && !CryptographicOperations.FixedTimeEquals(
                            AnnalContentDigest.ForSagaMemory(content), Convert.FromHexString(input.Snapshot.ContentHash)))
                    {
                        return Refuse(ErrorCodes.LongRest.StaleInput, "The stored content no longer matches its exact observed version.");
                    }
                }

                Result<LongRestReceipt> decision = LongRestPolicy.Evaluate(request, snapshots);

                if (decision.IsFailure)
                {
                    return decision;
                }

                await WriteReceiptAsync(connection, transaction, request, decision.Value, snapshots, cancellationToken).ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return decision;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqliteException or InvalidDataException or FormatException or ArgumentException)
        {
            // Closed diagnostics: provider/storage errors may contain memory text or identifiers.
            return Refuse(ErrorCodes.LongRest.Unavailable, "The Long Rest could not commit a valid transformation.");
        }
    }

    public async Task<Result<LongRestReceipt>> GetReceiptAsync(string receiptId, CancellationToken cancellationToken)
    {
        if (receiptId is null || receiptId.Length != 64 || !receiptId.All(Uri.IsHexDigit))
        {
            return Refuse(ErrorCodes.LongRest.InvalidRequest, "ReceiptId must be a 32-byte hexadecimal identity.");
        }

        try
        {
            return await SqliteBusyRetry.ExecuteAsync(async () =>
            {
                DbConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

                await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                if (await GrimoireCoreSchemaVersion.ReadAsync(connection, cancellationToken, transaction).ConfigureAwait(false) < 17)
                {
                    return Refuse(ErrorCodes.LongRest.Unavailable, "The Long Rest schema is not ready.");
                }

                Result<LongRestReceipt>? receipt = await ReadReceiptAsync(connection, transaction, receiptId.ToUpperInvariant(), cancellationToken).ConfigureAwait(false);

                return receipt ?? Refuse(ErrorCodes.LongRest.NotFound, "The transformation receipt does not exist.");
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqliteException or InvalidDataException or FormatException or ArgumentException)
        {
            return Refuse(ErrorCodes.LongRest.Unavailable, "The transformation receipt cannot be inspected safely.");
        }
    }

    private async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        DbConnection connection = db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        return connection;
    }

    private static Result<LongRestReceipt> Refuse(string code, string message) =>
        Result<LongRestReceipt>.Failure(new Error(code, message));

    private static async Task ExecuteAsync(DbConnection connection, DbTransaction transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        foreach ((string name, object? value) in parameters)
        {
            DbParameter parameter = command.CreateParameter();

            parameter.ParameterName = name;

            parameter.Value = value ?? DBNull.Value;

            command.Parameters.Add(parameter);
        }

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record LoadedSnapshot(LongRestSnapshot Snapshot, string? Content);
}
