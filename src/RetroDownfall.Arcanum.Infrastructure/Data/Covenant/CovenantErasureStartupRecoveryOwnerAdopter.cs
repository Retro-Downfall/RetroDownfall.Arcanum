using System.Data;

using System.Data.Common;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// Reconstructs the one durable Covenant-erasure owner before readiness is published.
/// </summary>
/// <remarks>
/// The caller supplies the already-open, initialized installation connection. Payload projection is
/// deliberately guarded in SQL so a legacy row with an arbitrarily large blob is classified by its
/// version without ever materializing those bytes into managed memory.
/// </remarks>
internal sealed class CovenantErasureStartupRecoveryOwnerAdopter(CovenantOperationGate gate)
{

    private const int MaximumPayloadBytes = 4096;

    /// <summary>
    /// The highest checkpoint version an ordinary retention mutation writes.
    /// </summary>
    /// <remarks>
    /// An ordinary mutation closed no admission, so a row at or below this version is left to
    /// ordinary reconciliation rather than adopted as an erasure owner. The bound is stated as a
    /// literal rather than as "one less than the launch version" because the two are not adjacent:
    /// the version between them belonged to the retired same-database reset checkpoint, and a row
    /// still carrying it is an erasure this build cannot read — which has to refuse, not be waved
    /// through as ordinary work.
    /// </remarks>
    private const int LastOrdinaryMutationCheckpointVersion = 2;

    private readonly CovenantOperationGate _gate =
        gate ?? throw new ArgumentNullException(nameof(gate));

    internal async Task<Result<CovenantExclusiveRecoveryOwner?>> AdoptBeforeReadinessAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        cancellationToken.ThrowIfCancellationRequested();

        if (connection.State != ConnectionState.Open)
        {

            throw new ArgumentException(
                "Startup recovery adoption requires the already-open installation connection.",
                nameof(connection));

        }

        CovenantExclusiveRecoveryOwner? retained = null;

        try
        {

            await using (DbCommand command = connection.CreateCommand())
            {

                command.CommandText =
                    """
                    SELECT
                        "Id",
                        "Kind",
                        "State",
                        "RecoveryPolicy",
                        "CheckpointVersion",
                        "CheckpointReference",
                        CASE
                            WHEN typeof("Kind") = 'text'
                             AND typeof("CheckpointVersion") = 'integer'
                             AND (("Kind" = @mutation AND "CheckpointVersion" = @mutationVersion)
                               OR ("Kind" = @factory AND "CheckpointVersion" = @factoryVersion))
                             AND typeof("CheckpointPayload") = 'blob'
                             AND length("CheckpointPayload") BETWEEN 1 AND @maximumPayload
                            THEN "CheckpointPayload"
                            ELSE NULL
                        END,
                        typeof("CheckpointPayload"),
                        length("CheckpointPayload")
                    FROM "LongRunningOperations"
                    WHERE "Kind" IN (@mutation, @factory)
                      AND "State" NOT IN (@completed, @failed, @abandoned)
                    ORDER BY "CreatedAt", "Id"
                    """;

                Add(command, "@mutation", LongRunningOperationKinds.DataRetentionMutation);

                Add(command, "@factory", LongRunningOperationKinds.DataRetentionFactoryReset);

                Add(command, "@mutationVersion", CovenantOfflineTransitionLaunchV4.CurrentVersion);

                Add(command, "@factoryVersion", DataRetentionFactoryTransitionLaunchV2.CurrentVersion);

                Add(command, "@maximumPayload", MaximumPayloadBytes);

                Add(command, "@completed", (int)LongRunningOperationState.Completed);

                Add(command, "@failed", (int)LongRunningOperationState.Failed);

                Add(command, "@abandoned", (int)LongRunningOperationState.Abandoned);

                await using DbDataReader reader = await command
                    .ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {

                    Result<CovenantExclusiveRecoveryOwner?> parsed = Parse(reader);

                    if (parsed.IsFailure)
                    {

                        return parsed;

                    }

                    if (parsed.Value is not { } owner)
                    {

                        continue;

                    }

                    if (retained is not null)
                    {

                        return Refusal();

                    }

                    retained = owner;

                }

            }

            if (retained is { } adopted)
            {

                try
                {

                    _gate.AdoptDurableRecoveryOwner(
                        adopted,
                        scope: null,
                        cleanupOnlyHistoricalCampaign: false);

                }
                catch (ArgumentException)
                {

                    return Refusal();

                }
                catch (InvalidOperationException)
                {

                    return Refusal();

                }

            }

            return Result<CovenantExclusiveRecoveryOwner?>.Success(retained);

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            throw;

        }
        catch (Exception)
        {

            return Refusal();

        }

    }

    private static Result<CovenantExclusiveRecoveryOwner?> Parse(DbDataReader reader)
    {

        if (reader.GetValue(0) is not string rawId
            || !Guid.TryParseExact(rawId, "N", out Guid operationId)
            || !string.Equals(rawId, operationId.ToString("N"), StringComparison.Ordinal)
            || reader.GetValue(1) is not string kind
            || reader.GetValue(2) is not long rawState
            || rawState is < int.MinValue or > int.MaxValue
            || !Enum.IsDefined((LongRunningOperationState)(int)rawState)
            || reader.GetValue(3) is not long rawPolicy
            || rawPolicy is < int.MinValue or > int.MaxValue
            || !Enum.IsDefined((LongRunningOperationRecoveryPolicy)(int)rawPolicy)
            || reader.GetValue(4) is not long rawVersion
            || rawVersion is < int.MinValue or > int.MaxValue)
        {

            return Refusal();

        }

        LongRunningOperationState state = (LongRunningOperationState)(int)rawState;

        LongRunningOperationRecoveryPolicy policy =
            (LongRunningOperationRecoveryPolicy)(int)rawPolicy;

        int version = (int)rawVersion;

        bool mutation = string.Equals(
            kind,
            LongRunningOperationKinds.DataRetentionMutation,
            StringComparison.Ordinal);

        bool factory = string.Equals(
            kind,
            LongRunningOperationKinds.DataRetentionFactoryReset,
            StringComparison.Ordinal);

        if (!mutation && !factory
            || mutation && policy != LongRunningOperationRecoveryPolicy.ReconcileAndComplete
            || factory && policy != LongRunningOperationRecoveryPolicy.RestartIdempotently)
        {

            return Refusal();

        }

        if (mutation && version is >= 0 and <= LastOrdinaryMutationCheckpointVersion
            || factory && version == 0)
        {

            return Result<CovenantExclusiveRecoveryOwner?>.Success(null);

        }

        if (state is not LongRunningOperationState.Running
            and not LongRunningOperationState.Waiting
            and not LongRunningOperationState.Cancelling
            and not LongRunningOperationState.ReconciliationRequired)
        {

            return Refusal();

        }

        int expectedVersion = mutation
            ? CovenantOfflineTransitionLaunchV4.CurrentVersion
            : DataRetentionFactoryTransitionLaunchV2.CurrentVersion;

        if (version != expectedVersion
            || reader.GetValue(5) is not string reference
            || !string.Equals(
                reference,
                CovenantResetCheckpointInitiator.CheckpointReference(kind, operationId),
                StringComparison.Ordinal)
            || reader.GetValue(6) is not byte[] payload
            || reader.GetValue(7) is not string payloadType
            || !string.Equals(payloadType, "blob", StringComparison.Ordinal)
            || reader.GetValue(8) is not long payloadLength
            || payloadLength is < 1 or > MaximumPayloadBytes
            || payload.Length != payloadLength)
        {

            return Refusal();

        }

        Result<CovenantErasureCheckpointState> checkpoint;

        if (mutation)
        {

            checkpoint = CovenantErasureCheckpointState.FromMutationCheckpoint(
                operationId,
                version,
                payload,
                out bool describesCovenantErasure);

            if (!describesCovenantErasure)
            {

                return Result<CovenantExclusiveRecoveryOwner?>.Success(null);

            }

        }
        else
        {

            checkpoint = CovenantErasureCheckpointState.FromFactoryResetCheckpoint(
                operationId,
                version,
                payload);

        }

        CovenantExclusiveOperation expectedOperation = mutation
            ? CovenantExclusiveOperation.CovenantReset
            : CovenantExclusiveOperation.HealthyCatalogFactoryErasure;

        return checkpoint.IsSuccess
            && checkpoint.Value.Operation == expectedOperation
            && CovenantResetPhaseMachine.IsDeclared(checkpoint.Value.Phase)
                ? Result<CovenantExclusiveRecoveryOwner?>.Success(checkpoint.Value.Owner)
                : Refusal();

    }

    private static Result<CovenantExclusiveRecoveryOwner?> Refusal() =>
        Result<CovenantExclusiveRecoveryOwner?>.Failure(
            new Error(
                ErrorCodes.Covenant.ManualRecoveryRequired,
                "Durable Covenant erasure ownership could not be reconstructed safely."));

    private static void Add(DbCommand command, string name, object value)
    {

        DbParameter parameter = command.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value;

        _ = command.Parameters.Add(parameter);

    }

}
