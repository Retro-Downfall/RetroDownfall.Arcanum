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
    // Parked clean sweeps the worker may drive again: the fault policy's resumable codes, passed as parameters.
    private static readonly string[] ResumeLaterCodes = [.. LongRestFaultPolicy.ResumeLaterCodes];

    // Parks that are not failures: neither spends the Sweep Failure Budget nor waits out a cooldown. The policy owns the set.
    private static readonly string[] FreeParkCodes = [.. LongRestFaultPolicy.FreeParkCodes];

    // A bounded read of the sweeps still cooling, to name when the next one is due.
    private const int CoolingSweepLimit = 256;

    public async Task<Result<LongRestRunnableSweep>> FindNextRunnableCleanAsync(LongRestConfigurationStamp configuration,
        Guid? afterOperationId, CancellationToken ct)
    {
        try
        {
            if (configuration is not { IsValid: true } || afterOperationId == Guid.Empty)
            {
                throw new InvalidDataException();
            }

            string stamp = JsonSerializer.Serialize(configuration, LongRestJsonContext.Default.LongRestConfigurationStamp);

            DateTimeOffset now = timeProvider.GetUtcNow();

            LongRestRunnableSweep found = await SqliteBusyRetry.ExecuteAsync<LongRestRunnableSweep>(async () =>
            {
                SqliteConnection connection = (SqliteConnection)db.Database.GetDbConnection();

                if (connection.State != ConnectionState.Open)
                {
                    await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
                }

                Guid? candidate = await ReadRunnableCandidateAsync(connection, stamp, afterOperationId, now, ct).ConfigureAwait(false);

                DateTimeOffset? due = await ReadNextCooldownDueAsync(connection, stamp, now, ct).ConfigureAwait(false);

                return new(candidate, due);
            }, ct).ConfigureAwait(false);

            return Result<LongRestRunnableSweep>.Success(found);
        }
        catch (Exception exception) when (exception is SqliteException or InvalidDataException or ArgumentException
            or FormatException or JsonException or InvalidOperationException)
        {
            return Result<LongRestRunnableSweep>.Failure(new Error(ErrorCodes.LongRest.StaleInput, "The pending sweep could not prove its exact current clean request."));
        }
    }

    private static async Task<Guid?> ReadRunnableCandidateAsync(SqliteConnection connection, string stamp, Guid? afterOperationId,
        DateTimeOffset now, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = $"""
            SELECT operation.Id
            FROM LongRunningOperations AS operation
            JOIN long_rest_sweeps AS sweep ON sweep.OperationId = operation.Id
            WHERE operation.Kind = $kind
              AND sweep.PartitionDigest IS NULL
              AND (
                (
                  operation.State = $pending
                  AND operation.AttemptCount = 0
                  AND operation.CheckpointVersion = 0
                  AND operation.CheckpointPayload IS NULL
                  AND operation.CheckpointReference IS NULL
                  AND operation.LeaseOwner IS NULL
                  AND operation.LeaseExpiresAt IS NULL
                  AND sweep.GenerationId IS NULL
                  AND sweep.PhaseCode = $capture
                  AND sweep.ConfigurationJson = $configuration
                )
                OR
                {ParkedClauseSql()}
              )
              AND ($after IS NULL OR operation.Id > $after)
            ORDER BY operation.Id
            LIMIT 1;
            """;

        BindParkedClause(command, stamp, now);

        command.Parameters.AddWithValue("$kind", LongRunningOperationKinds.LongRestSweep);

        command.Parameters.AddWithValue("$pending", (int)LongRunningOperationState.Pending);

        command.Parameters.AddWithValue("$capture", (int)LongRestSweepPhase.Capture);

        command.Parameters.AddWithValue("$after", (object?)afterOperationId?.ToString("N") ?? DBNull.Value);

        object? value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);

        return value is null or DBNull ? null : Guid.Parse((string)value);
    }

    private static async Task<DateTimeOffset?> ReadNextCooldownDueAsync(SqliteConnection connection, string stamp, DateTimeOffset now,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();

        // Parked, resumable, budgeted, same configuration value, and still inside its cooldown.
        command.CommandText = $"""
            SELECT sweep.ParkedAt, sweep.ReadmitCount
            FROM LongRunningOperations AS operation
            JOIN long_rest_sweeps AS sweep ON sweep.OperationId = operation.Id
            WHERE operation.Kind = $kind
              AND sweep.PartitionDigest IS NULL
              AND {ParkedBaseSql()}
              AND operation.TerminalErrorCode NOT IN ({Placeholders("free", FreeParkCodes.Length)})
              AND sweep.ReadmitCount < $max
              AND sweep.ParkedAt IS NOT NULL
              AND sweep.ParkedAt > {CooldownThresholdSql()}
            ORDER BY sweep.ParkedAt
            LIMIT {CoolingSweepLimit};
            """;

        BindParkedClause(command, stamp, now);

        command.Parameters.AddWithValue("$kind", LongRunningOperationKinds.LongRestSweep);

        DateTimeOffset? earliest = null;

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            int readmitCount = checked((int)reader.GetInt64(1));

            DateTimeOffset due = UtcInstantText.Parse(reader.GetString(0)) + LongRestFaultPolicy.Cooldown(readmitCount);

            if (earliest is null || due < earliest) earliest = due;
        }

        return earliest;
    }

    /// <summary>
    /// A parked clean sweep: resumable code, no live lease, an intact started checkpoint and an unretired sweep whose
    /// configuration value is the current one. BootId and epoch may differ, because a restart is exactly why it parked.
    /// </summary>
    private static string ParkedBaseSql() => $"""
        operation.State = $attention
        AND operation.TerminalErrorCode IN ({Placeholders("code", ResumeLaterCodes.Length)})
        AND operation.LeaseOwner IS NULL
        AND operation.LeaseExpiresAt IS NULL
        AND operation.CheckpointVersion = 1
        AND sweep.PhaseCode NOT IN ($invalidated, $retired)
        AND json_extract(sweep.ConfigurationJson, '$.valueDigest') = json_extract($configuration, '$.valueDigest')
        """;

    private static string ParkedClauseSql() => $"""
        (
          {ParkedBaseSql()}
          AND (
            operation.TerminalErrorCode IN ({Placeholders("free", FreeParkCodes.Length)})
            OR (
              sweep.ReadmitCount < $max
              AND (sweep.ParkedAt IS NULL OR sweep.ParkedAt <= {CooldownThresholdSql()})
            )
          )
        )
        """;

    private static string CooldownThresholdSql() =>
        "CASE sweep.ReadmitCount WHEN 0 THEN $cooldown0 WHEN 1 THEN $cooldown1 WHEN 2 THEN $cooldown2 ELSE $cooldown3 END";

    private static string Placeholders(string prefix, int count) =>
        string.Join(", ", Enumerable.Range(0, count).Select(index => $"${prefix}{index}"));

    private static void BindParkedClause(SqliteCommand command, string stamp, DateTimeOffset now)
    {
        command.Parameters.AddWithValue("$attention", (int)LongRunningOperationState.ReconciliationRequired);

        command.Parameters.AddWithValue("$invalidated", (int)LongRestSweepPhase.Invalidated);

        command.Parameters.AddWithValue("$retired", (int)LongRestSweepPhase.Retired);

        command.Parameters.AddWithValue("$max", LongRestFaultPolicy.MaxReadmissions);

        command.Parameters.AddWithValue("$configuration", stamp);

        for (int index = 0; index < ResumeLaterCodes.Length; index++)
        {
            command.Parameters.AddWithValue($"$code{index}", ResumeLaterCodes[index]);
        }

        for (int index = 0; index < FreeParkCodes.Length; index++)
        {
            command.Parameters.AddWithValue($"$free{index}", FreeParkCodes[index]);
        }

        // A sweep parked at ParkedAt is due once ParkedAt + cooldown(readmits) <= now, i.e. ParkedAt <= now - cooldown.
        for (int readmitCount = 0; readmitCount < LongRestFaultPolicy.MaxReadmissions; readmitCount++)
        {
            command.Parameters.AddWithValue($"$cooldown{readmitCount}", UtcInstantText.Format(now - LongRestFaultPolicy.Cooldown(readmitCount)));
        }
    }
}
