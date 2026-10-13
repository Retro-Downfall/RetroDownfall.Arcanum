using System.Data;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

/// <summary>Owns durable adjudication staging and atomic canonical/item progress.</summary>
internal sealed partial class LongRestSweepStore(ArcanumDbContext db, TimeProvider timeProvider) : ILongRestSweepStore
{
    public Task<Result<LongRestSweep>> BeginOrResumeAsync(Guid operationId, LongRestSweepScope scope,
        LongRestConfigurationStamp configuration, CancellationToken ct) => WithinAsync(async (connection, transaction) =>
    {
        if (operationId == Guid.Empty || !scope.IsInitialized || configuration is not { IsValid: true })
        {
            throw new InvalidDataException();
        }

        LongRestDiscoveryGeneration? generation = await LongRestDiscoveryStore.ReadSweepGenerationAsync(connection, transaction, operationId, ct).ConfigureAwait(false);

        if (generation is not null && (generation.Scope != scope || generation.Fence.Configuration != configuration
            || generation.Phase == LongRestSweepPhase.Retired))
        {
            throw new InvalidDataException();
        }

        LongRestSweep? prior = await ReadAsync(connection, transaction, operationId, ct).ConfigureAwait(false);

        if (prior is not null)
        {
            if (prior.Scope != scope || prior.Phase == LongRestSweepPhase.Retired
                || (generation is not null && prior.GenerationId != generation.GenerationId))
            {
                throw new InvalidDataException();
            }

            await using SqliteCommand stamp = Command(connection, transaction, "SELECT ConfigurationJson FROM long_rest_sweeps WHERE OperationId = $operation;", ("$operation", operationId.ToString("N")));

            LongRestConfigurationStamp stored = JsonSerializer.Deserialize((string)(await stamp.ExecuteScalarAsync(ct).ConfigureAwait(false))!, LongRestJsonContext.Default.LongRestConfigurationStamp) ?? throw new InvalidDataException();

            if (stored != configuration && (generation is null || stored.BootId == configuration.BootId || stored.ValueDigest != configuration.ValueDigest
                || prior.Phase is LongRestSweepPhase.Completed or LongRestSweepPhase.Invalidated))
            {
                throw new InvalidDataException();
            }

            LongRestSweepPhase phase = prior.Phase is LongRestSweepPhase.Completed or LongRestSweepPhase.Invalidated ? prior.Phase : generation?.Phase ?? prior.Phase;

            if (stored != configuration || phase != prior.Phase)
            {
                await ExecuteAsync(connection, transaction, "UPDATE long_rest_sweeps SET ConfigurationJson = $stamp, PhaseCode = $phase, Revision = Revision + 1, UpdatedAtUtc = $at WHERE OperationId = $operation AND Revision = $revision;", ct,
                    ("$stamp", JsonSerializer.Serialize(configuration, LongRestJsonContext.Default.LongRestConfigurationStamp)), ("$phase", (int)phase),
                    ("$at", UtcInstantText.Format(timeProvider.GetUtcNow())), ("$operation", operationId.ToString("N")), ("$revision", prior.Revision)).ConfigureAwait(false);
            }

            return (await ReadAsync(connection, transaction, operationId, ct).ConfigureAwait(false))!;
        }

        await using SqliteCommand insert = Command(connection, transaction, """
            INSERT INTO long_rest_sweeps (OperationId, GenerationId, ScopeKindCode, CampaignId, PartitionDigest, ConfigurationJson, PhaseCode, Revision, UpdatedAtUtc)
            SELECT Id, $generation, $scope, $campaign, $partition, $stamp, $phase, 0, $at FROM LongRunningOperations
            WHERE Id = $operation AND Kind = 'long-rest-sweep';
            """, ("$operation", operationId.ToString("N")), ("$generation", generation is null ? null : Format(generation.GenerationId)),
            ("$scope", (int)scope.Kind), ("$campaign", scope.CampaignId is { } campaign ? Format(campaign) : null), ("$partition", scope.ProtectedPartitionDigest?.Bytes),
            ("$stamp", JsonSerializer.Serialize(configuration, LongRestJsonContext.Default.LongRestConfigurationStamp)), ("$phase", (int)(generation?.Phase ?? LongRestSweepPhase.Capture)),
            ("$at", UtcInstantText.Format(timeProvider.GetUtcNow())));

        if (await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
        {
            throw new InvalidDataException();
        }

        return (await ReadAsync(connection, transaction, operationId, ct).ConfigureAwait(false))!;
    }, ct);

    private async Task<Result<T>> WithinAsync<T>(Func<SqliteConnection, SqliteTransaction, Task<T>> action, CancellationToken ct)
    {
        try
        {
            return await SqliteBusyRetry.ExecuteAsync(async () =>
            {
                SqliteConnection connection = (SqliteConnection)db.Database.GetDbConnection();

                if (connection.State != ConnectionState.Open)
                {
                    await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
                }

                await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

                T value = await action(connection, transaction).ConfigureAwait(false);

                await transaction.CommitAsync(ct).ConfigureAwait(false);

                return Result<T>.Success(value);
            }, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SqliteException or InvalidDataException or ArgumentException or FormatException or OverflowException or JsonException or InvalidOperationException)
        {
            return Result<T>.Failure(new Error(ErrorCodes.LongRest.StaleInput, "The sweep could not prove its exact current work and immutable accepted evidence."));
        }
    }

    internal static async Task<LongRestSweep?> ReadAsync(SqliteConnection connection, SqliteTransaction transaction, Guid operation, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, "SELECT GenerationId, ScopeKindCode, CampaignId, PartitionDigest, PhaseCode, Revision, CommittedItemCursor, CommittedWorkDigest FROM long_rest_sweeps WHERE OperationId = $operation;", ("$operation", operation.ToString("N")));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        LongRestSweepScope scope = LongRestSweepScope.Create(new((LongRestSweepScopeKind)reader.GetInt32(1), reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2))), reader.IsDBNull(3) ? null : new CovenantDigest((byte[])reader[3])).Value;

        return new(operation, reader.IsDBNull(0) ? Guid.Empty : Guid.Parse(reader.GetString(0)), scope, (LongRestSweepPhase)reader.GetInt32(4), reader.GetInt64(5),
            reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : new CovenantDigest((byte[])reader[7]));
    }

    private async Task<LongRestDiscoveryGeneration> RequireGenerationAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestSweep expected, LongRestMaintenanceCapability? capability, CancellationToken ct)
    {
        if (expected != await ReadAsync(connection, transaction, expected.OperationId, ct).ConfigureAwait(false))
        {
            throw new InvalidDataException();
        }

        LongRestDiscoveryGeneration generation = await LongRestDiscoveryStore.ReadSweepGenerationAsync(connection, transaction, expected.OperationId, ct).ConfigureAwait(false) ?? throw new InvalidDataException();

        if (generation.GenerationId != expected.GenerationId || generation.Scope != expected.Scope || !generation.Published
            || generation.Phase is not (LongRestSweepPhase.Discover or LongRestSweepPhase.Adjudicate or LongRestSweepPhase.Completed))
        {
            throw new InvalidDataException();
        }

        await ValidateClaimAsync(connection, transaction, generation, capability, ct).ConfigureAwait(false);

        await LongRestDiscoveryStore.ValidateSweepGenerationAsync(connection, transaction, generation, capability, ct).ConfigureAwait(false);

        return generation;
    }

    private async Task ValidateClaimAsync(SqliteConnection connection, SqliteTransaction transaction, LongRestDiscoveryGeneration generation,
        LongRestMaintenanceCapability? capability, CancellationToken ct)
    {
        if (generation.Scope.ProtectedPartitionDigest is not null)
        {
            if (capability is null || await LongRestAttemptStore.ValidatePendingClaimWithinTransactionAsync(connection, transaction,
                capability.Claim, generation.Fence.CanonicalScope, timeProvider.GetUtcNow(), ct).ConfigureAwait(false) != capability.Claim.Claim.ExpectedCurrentSensitivityRevision)
            {
                throw new InvalidDataException();
            }
        }
    }

    private static string Format(Guid value) => value.ToString("D").ToUpperInvariant();

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] values)
    {
        SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        foreach ((string name, object? value) in values)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken ct, params (string Name, object? Value)[] values)
    {
        await using SqliteCommand command = Command(connection, transaction, sql, values);

        _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
