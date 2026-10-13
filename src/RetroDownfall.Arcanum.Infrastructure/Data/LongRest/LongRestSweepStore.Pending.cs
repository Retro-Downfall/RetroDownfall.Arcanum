using System.Data;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

internal sealed partial class LongRestSweepStore
{
    public async Task<Result<Guid?>> FindNextPendingCleanAsync(LongRestConfigurationStamp configuration,
        Guid? afterOperationId, CancellationToken ct)
    {
        try
        {
            if (configuration is not { IsValid: true } || afterOperationId == Guid.Empty)
            {
                throw new InvalidDataException();
            }

            string stamp = JsonSerializer.Serialize(configuration, LongRestJsonContext.Default.LongRestConfigurationStamp);

            Guid? candidate = await SqliteBusyRetry.ExecuteAsync<Guid?>(async () =>
            {
                SqliteConnection connection = (SqliteConnection)db.Database.GetDbConnection();

                if (connection.State != ConnectionState.Open)
                {
                    await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
                }

                await using SqliteCommand command = connection.CreateCommand();

                command.CommandText = """
                    SELECT operation.Id
                    FROM LongRunningOperations AS operation
                    JOIN long_rest_sweeps AS sweep ON sweep.OperationId = operation.Id
                    WHERE operation.Kind = $kind
                      AND operation.State = $pending
                      AND operation.AttemptCount = 0
                      AND operation.CheckpointVersion = 0
                      AND operation.CheckpointPayload IS NULL
                      AND operation.CheckpointReference IS NULL
                      AND operation.LeaseOwner IS NULL
                      AND operation.LeaseExpiresAt IS NULL
                      AND sweep.PartitionDigest IS NULL
                      AND sweep.GenerationId IS NULL
                      AND sweep.PhaseCode = $capture
                      AND sweep.ConfigurationJson = $configuration
                      AND ($after IS NULL OR operation.Id > $after)
                    ORDER BY operation.Id
                    LIMIT 1;
                    """;

                command.Parameters.AddWithValue("$kind", LongRunningOperationKinds.LongRestSweep);

                command.Parameters.AddWithValue("$pending", (int)LongRunningOperationState.Pending);

                command.Parameters.AddWithValue("$capture", (int)LongRestSweepPhase.Capture);

                command.Parameters.AddWithValue("$configuration", stamp);

                command.Parameters.AddWithValue("$after", (object?)afterOperationId?.ToString("N") ?? DBNull.Value);

                object? value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);

                return value is null or DBNull ? null : Guid.Parse((string)value);
            }, ct).ConfigureAwait(false);

            return Result<Guid?>.Success(candidate);
        }
        catch (Exception exception) when (exception is SqliteException or InvalidDataException or ArgumentException
            or FormatException or JsonException or InvalidOperationException)
        {
            return Result<Guid?>.Failure(new Error(ErrorCodes.LongRest.StaleInput, "The pending sweep could not prove its exact current clean request."));
        }
    }
}
