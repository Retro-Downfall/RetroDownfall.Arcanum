using System.Data;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

internal sealed partial class LongRestLiveFenceReader
{
    internal async Task<Result<LongRestOperationScope>> ReadScopeAsync(Guid operationId, CancellationToken ct)
    {
        try
        {
            SqliteConnection connection = await OpenAsync(ct).ConfigureAwait(false);

            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = """
                SELECT sweep.ScopeKindCode, sweep.CampaignId, sweep.PartitionDigest, sweep.ConfigurationJson
                FROM long_rest_sweeps sweep JOIN LongRunningOperations operation ON operation.Id = sweep.OperationId
                WHERE sweep.OperationId = $operation AND operation.Kind = 'long-rest-sweep';
                """;

            command.Parameters.AddWithValue("$operation", operationId.ToString("N"));

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return Result<LongRestOperationScope>.Failure(Unavailable());
            }

            Result<LongRestSweepScope> scope = LongRestSweepScope.Create(new((LongRestSweepScopeKind)reader.GetInt32(0),
                reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1))), reader.IsDBNull(2) ? null : new CovenantDigest((byte[])reader[2]));

            LongRestConfigurationStamp? stamp = JsonSerializer.Deserialize(reader.GetString(3), LongRestJsonContext.Default.LongRestConfigurationStamp);

            return scope.IsSuccess && stamp is { IsValid: true }
                ? Result<LongRestOperationScope>.Success(new LongRestOperationScope(scope.Value, stamp))
                : Result<LongRestOperationScope>.Failure(Unavailable());
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or SqliteException or JsonException or FormatException)
        {
            return Result<LongRestOperationScope>.Failure(Unavailable());
        }
    }

    internal async Task<Result> ValidateClaimAsync(SessionTurnClaimLease claim, CanonicalCampaignContext scope,
        DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            SqliteConnection connection = await OpenAsync(ct).ConfigureAwait(false);

            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);

            LongRestVerificationCounters.Transaction(deferred: true);

            long sensitivity = await LongRestAttemptStore.ValidatePendingClaimWithinTransactionAsync(connection, transaction, claim, scope, now, ct).ConfigureAwait(false);

            return sensitivity == claim.Claim.ExpectedCurrentSensitivityRevision ? Result.Success() : Unavailable();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or SqliteException or InvalidDataException)
        {
            return Unavailable();
        }
    }

    /// <summary>
    /// Proves that the generation still holds, to the depth the caller names. <paramref name="inputs"/> are the memories the calling step
    /// is about to act on: each is re-proved in full, whatever the depth, so the leaves a send is built from are never taken on trust.
    /// </summary>
    internal async Task<Result> ValidateGenerationAsync(LongRestDiscoveryGeneration expected, LongRestMaintenanceCapability? capability,
        DateTimeOffset now, LongRestVerificationDepth depth, IReadOnlyCollection<string>? inputs, CancellationToken ct)
    {
        Result<LongRestSweepFence> before = await CaptureAsync(expected.Fence.CanonicalScope, expected.Fence.Configuration,
            expected.Fence.InsertionHighWatermark, ct).ConfigureAwait(false);

        if (before.IsFailure || before.Value != expected.Fence)
        {
            return Unavailable();
        }

        try
        {
            SqliteConnection connection = await OpenAsync(ct).ConfigureAwait(false);

            await using (SqliteTransaction transaction = connection.BeginTransaction(deferred: true))
            {
                LongRestVerificationCounters.Transaction(deferred: true);

                LongRestDiscoveryGeneration? current = await LongRestDiscoveryStore.ReadSweepGenerationAsync(connection, transaction, expected.OperationId, ct).ConfigureAwait(false);

                if (current is null || current.GenerationId != expected.GenerationId || current.Scope != expected.Scope
                    || current.Fence != expected.Fence || current.Recipe != expected.Recipe)
                {
                    return Unavailable();
                }

                if (current.Scope.ProtectedPartitionDigest is not null)
                {
                    if (capability is null)
                    {
                        return Unavailable();
                    }

                    SessionTurnClaimLease claim = capability.Claim;

                    long sensitivity = await LongRestAttemptStore.ValidatePendingClaimWithinTransactionAsync(connection, transaction,
                        claim, current.Fence.CanonicalScope, now, ct).ConfigureAwait(false);

                    if (sensitivity != claim.Claim.ExpectedCurrentSensitivityRevision)
                    {
                        return Unavailable();
                    }
                }

                await LongRestDiscoveryStore.ValidateSweepGenerationAsync(connection, transaction, current, capability, depth, ct).ConfigureAwait(false);

                await LongRestDiscoveryStore.ReadLeavesAsync(connection, transaction, current, inputs ?? [], capability, ct).ConfigureAwait(false);
            }

            Result<LongRestSweepFence> after = await CaptureAsync(expected.Fence.CanonicalScope, expected.Fence.Configuration,
                expected.Fence.InsertionHighWatermark, ct).ConfigureAwait(false);

            return after.IsSuccess && after.Value == expected.Fence ? Result.Success() : Unavailable();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or SqliteException or InvalidDataException or JsonException or FormatException or OverflowException)
        {
            return Unavailable();
        }
    }

    /// <summary>Exact retained-source metadata only, usable before root consumption or retry reset.</summary>
    internal async Task<Result> ValidateRetainedSourceMetadataAsync(LongRestDiscoveryGeneration expected, CancellationToken ct)
    {
        try
        {
            SqliteConnection connection = await OpenAsync(ct).ConfigureAwait(false);

            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);

            LongRestVerificationCounters.Transaction(deferred: true);

            LongRestDiscoveryGeneration? current = await LongRestDiscoveryStore.ReadSweepGenerationAsync(connection, transaction, expected.OperationId, ct).ConfigureAwait(false);

            if (current != expected || current.Phase is LongRestSweepPhase.Invalidated or LongRestSweepPhase.Deferred or LongRestSweepPhase.Retired) return Unavailable();

            // Issuing a capability over a retained generation is a seam.
            await LongRestDiscoveryStore.VerifyFrozenMetadataAsync(connection, transaction, current, LongRestVerificationDepth.Seam, ct).ConfigureAwait(false);

            return Result.Success();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or SqliteException or InvalidDataException or JsonException or FormatException or OverflowException)
        {
            return Unavailable();
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        SqliteConnection connection = (SqliteConnection)db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        }

        return connection;
    }
}

internal sealed record LongRestOperationScope(LongRestSweepScope Scope, LongRestConfigurationStamp Configuration);
