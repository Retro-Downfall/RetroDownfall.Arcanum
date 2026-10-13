using System.Text.Json;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

/// <summary>A parked sweep that the worker (or a fresh Session request) has just moved straight back to Running.</summary>
/// <param name="Operation">The operation as it stands after readmission: Running, leased to the readmitting owner.</param>
/// <param name="ReadmitCount">The Sweep Failure Budget the readmission left behind; the next park reads it.</param>
internal sealed record LongRestReadmission(LongRunningOperation Operation, int ReadmitCount);

/// <summary>The parked-sweep half of the store: Atomic Readmission and the park stamp that starts its cooldown.</summary>
internal interface ILongRestParkedReadmission
{
    /// <summary>
    /// Atomic Readmission. In one immediate transaction it re-proves that the operation is still the parked sweep the
    /// caller read, that its fault class and Sweep Failure Budget allow readmission now, and then moves it
    /// <c>ReconciliationRequired</c> to <c>Running</c> under <paramref name="workerOwner"/>. The row never passes through
    /// <c>Pending</c>, which the reconciler's discovery would adopt. Null means the sweep is not readmittable now.
    /// </summary>
    Task<Result<LongRestReadmission?>> TryReadmitParkedAsync(Guid operationId, long expectedRevision, LongRestSweepScope scope,
        LongRestConfigurationStamp configuration, string workerOwner, DateTimeOffset now, DateTimeOffset leaseExpiresAt, CancellationToken ct);

    /// <summary>Stamps when the owning worker parked the sweep, which starts the cooldown the next readmission must wait out.</summary>
    Task<Result> MarkParkedAsync(Guid operationId, DateTimeOffset parkedAt, CancellationToken ct);
}

internal sealed partial class LongRestSweepStore : ILongRestParkedReadmission
{
    public Task<Result<LongRestReadmission?>> TryReadmitParkedAsync(Guid operationId, long expectedRevision, LongRestSweepScope scope,
        LongRestConfigurationStamp configuration, string workerOwner, DateTimeOffset now, DateTimeOffset leaseExpiresAt, CancellationToken ct) =>
        WithinAsync<LongRestReadmission?>(async (connection, transaction) =>
    {
        if (operationId == Guid.Empty || expectedRevision < 0 || !scope.IsInitialized || configuration is not { IsValid: true }
            || string.IsNullOrWhiteSpace(workerOwner) || leaseExpiresAt <= now)
        {
            throw new InvalidDataException();
        }

        LongRunningOperation operation = await LongRunningOperationStore.ReadWithinAsync(connection, transaction, operationId, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException();

        ParkedSweep sweep = await ReadParkedSweepAsync(connection, transaction, operationId, ct).ConfigureAwait(false) ?? throw new InvalidDataException();

        // Identity that a legitimate actor cannot change under a candidate: a mismatch is a failed proof, not a lost race.
        if (operation.Kind != LongRunningOperationKinds.LongRestSweep || operation.RecoveryPolicy != LongRunningOperationRecoveryPolicy.ResumeFromCheckpoint
            || operation.CheckpointVersion != 1 || sweep.Scope != scope || sweep.Phase is LongRestSweepPhase.Invalidated or LongRestSweepPhase.Retired)
        {
            throw new InvalidDataException();
        }

        // State that another actor may have moved since the caller read it: there is nothing left to readmit.
        if (operation.State != LongRunningOperationState.ReconciliationRequired || operation.Revision != expectedRevision
            || operation.LeaseOwner is not null || operation.LeaseExpiresAt is not null)
        {
            return null;
        }

        LongRestConfigurationStamp stored = JsonSerializer.Deserialize(sweep.ConfigurationJson, LongRestJsonContext.Default.LongRestConfigurationStamp)
            ?? throw new InvalidDataException();

        if (!stored.IsValid || stored.ValueDigest != configuration.ValueDigest) return null;

        string code = operation.TerminalErrorCode ?? string.Empty;

        int readmitCount;

        if (scope.ProtectedPartitionDigest is not null)
        {
            // A protected sweep has no failure budget: a fresh, capability-validated Session request is the only way in.
            if (code != LongRestParkCodes.AuthorizedRequestRequired) return null;

            readmitCount = sweep.ReadmitCount;
        }
        else if (LongRestFaultPolicy.Classify(code) != LongRestFaultClass.ResumeLater)
        {
            return null;
        }
        else if (code == LongRestParkCodes.ReadmitRequested)
        {
            // An operator retry is an explicit decision: it ignores the cooldown and starts a fresh budget.
            readmitCount = 0;
        }
        else if (code == LongRestParkCodes.MaintenanceDeferred)
        {
            // Yielding to maintenance is not a failure: it neither waits nor spends budget.
            readmitCount = sweep.ReadmitCount;
        }
        else
        {
            if (!LongRestFaultPolicy.CanReadmit(sweep.ReadmitCount)) return null;

            if (sweep.ParkedAt is { } parkedAt && parkedAt + LongRestFaultPolicy.Cooldown(sweep.ReadmitCount) > now) return null;

            readmitCount = sweep.ReadmitCount + 1;
        }

        string at = UtcInstantText.Format(now);

        await using (SqliteCommand lease = Command(connection, transaction, """
            UPDATE LongRunningOperations
            SET State = $running, StartedAt = COALESCE(StartedAt, $now), HeartbeatAt = $now, LeaseOwner = $owner, LeaseExpiresAt = $lease,
                AttemptCount = AttemptCount + 1, TerminalErrorCode = NULL, Revision = Revision + 1
            WHERE Id = $operation AND Revision = $revision AND Kind = $kind AND State = $attention
                AND LeaseOwner IS NULL AND LeaseExpiresAt IS NULL AND TerminalErrorCode IS $code;
            """, ("$running", (int)LongRunningOperationState.Running), ("$now", at), ("$owner", workerOwner),
            ("$lease", UtcInstantText.Format(leaseExpiresAt)), ("$operation", operationId.ToString("N")), ("$revision", expectedRevision),
            ("$kind", LongRunningOperationKinds.LongRestSweep), ("$attention", (int)LongRunningOperationState.ReconciliationRequired),
            ("$code", operation.TerminalErrorCode)))
        {
            if (await lease.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) throw new InvalidDataException();
        }

        await using (SqliteCommand budget = Command(connection, transaction, """
            UPDATE long_rest_sweeps SET ReadmitCount = $count, ParkedAt = NULL
            WHERE OperationId = $operation AND Revision = $revision AND PartitionDigest IS $partition
                AND PhaseCode NOT IN ($invalidated, $retired);
            """, ("$count", readmitCount), ("$operation", operationId.ToString("N")), ("$revision", sweep.Revision),
            ("$partition", scope.ProtectedPartitionDigest?.Bytes), ("$invalidated", (int)LongRestSweepPhase.Invalidated),
            ("$retired", (int)LongRestSweepPhase.Retired)))
        {
            if (await budget.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) throw new InvalidDataException();
        }

        LongRunningOperation running = await LongRunningOperationStore.ReadWithinAsync(connection, transaction, operationId, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException();

        return new LongRestReadmission(running, readmitCount);
    }, ct);

    public async Task<Result> MarkParkedAsync(Guid operationId, DateTimeOffset parkedAt, CancellationToken ct)
    {
        if (operationId == Guid.Empty) return new Error(ErrorCodes.LongRest.StaleInput, "The sweep could not prove its exact current work and immutable accepted evidence.");

        Result<bool> marked = await WithinAsync(async (connection, transaction) =>
        {
            await ExecuteAsync(connection, transaction, """
                UPDATE long_rest_sweeps SET ParkedAt = $at
                WHERE OperationId = $operation AND PhaseCode NOT IN ($invalidated, $retired);
                """, ct, ("$at", UtcInstantText.Format(parkedAt)), ("$operation", operationId.ToString("N")),
                ("$invalidated", (int)LongRestSweepPhase.Invalidated), ("$retired", (int)LongRestSweepPhase.Retired)).ConfigureAwait(false);

            return true;
        }, ct).ConfigureAwait(false);

        return marked.IsSuccess ? Result.Success() : marked.Error;
    }

    private static async Task<ParkedSweep?> ReadParkedSweepAsync(SqliteConnection connection, SqliteTransaction transaction, Guid operationId,
        CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            SELECT ScopeKindCode, CampaignId, PartitionDigest, PhaseCode, Revision, ConfigurationJson, ReadmitCount, ParkedAt
            FROM long_rest_sweeps WHERE OperationId = $operation;
            """, ("$operation", operationId.ToString("N")));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;

        LongRestSweepScope scope = LongRestSweepScope.Create(new((LongRestSweepScopeKind)reader.GetInt32(0), reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1))),
            reader.IsDBNull(2) ? null : new CovenantDigest((byte[])reader[2])).Value;

        return new(scope, (LongRestSweepPhase)reader.GetInt32(3), reader.GetInt64(4), reader.GetString(5), checked((int)reader.GetInt64(6)),
            reader.IsDBNull(7) ? null : UtcInstantText.Parse(reader.GetString(7)));
    }

    private sealed record ParkedSweep(LongRestSweepScope Scope, LongRestSweepPhase Phase, long Revision, string ConfigurationJson,
        int ReadmitCount, DateTimeOffset? ParkedAt);
}
