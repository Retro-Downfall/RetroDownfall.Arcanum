using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Repositories;

/// <summary>Consumes a durable claim reservation in the transaction that writes its two Entries.</summary>
public sealed partial class GrimoireRepository : ISessionTurnClaimBeginStore
{
    public async ValueTask<Result<SessionTurnClaimInputSnapshot>> ReadClaimInputAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        if (_db.Database.GetDbConnection() is not SqliteConnection connection)
        {
            throw new InvalidOperationException("The Grimoire requires a SQLCipher connection.");
        }

        Result<IGrimoireOrdinaryConnectionLease> acquired = await _connections.AcquireScopedAsync(
            connection, CovenantSqliteConnectionMode.ReadOnly, cancellationToken).ConfigureAwait(false);

        if (acquired.IsFailure)
        {
            return acquired.Error;
        }

        await using IGrimoireOrdinaryConnectionLease lease = acquired.Value;

        ClaimInputRow? input = await ReadClaimInputWithinAsync(sessionId, cancellationToken).ConfigureAwait(false);

        return input is null
            ? new Error(ErrorCodes.Session.NotFound, "Session not found.")
            : new SessionTurnClaimInputSnapshot(sessionId, input.HistoryRevision, input.HistoryWatermarkUtc, input.SensitivityRevision);
    }

    public async ValueTask<Result<AssistantReplyBeginReceipt>> BeginClaimedAssistantReplyAsync(
        SessionTurnClaimLease claimLease,
        CanonicalCampaignContext campaign,
        string prompt,
        string model,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claimLease);

        ArgumentNullException.ThrowIfNull(prompt);

        ArgumentNullException.ThrowIfNull(model);

        if (!claimLease.IsExecutable || claimLease.ExecutorId is null || claimLease.OwnerBootId is null)
        {
            return StaleClaimBegin();
        }

        using IDisposable writeLock = await SessionEntryPersistence.AcquireWriteLockAsync(
            claimLease.Claim.SessionId, cancellationToken).ConfigureAwait(false);

        try
        {
            return await SqliteBusyRetry.ExecuteAsync(
                () => BeginClaimedWithinAsync(claimLease, campaign, prompt, model, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Claimed assistant begin failed for Session {SessionId}; no Entries were written.", claimLease.Claim.SessionId);

            return new Error(ErrorCodes.Grimoire.WriteFailed, "The conversation turn could not be started.");
        }
    }

    private async Task<Result<AssistantReplyBeginReceipt>> BeginClaimedWithinAsync(
        SessionTurnClaimLease claimLease,
        CanonicalCampaignContext campaign,
        string prompt,
        string model,
        CancellationToken cancellationToken)
    {
        if (_db.Database.GetDbConnection() is not SqliteConnection connection)
        {
            throw new InvalidOperationException("The Grimoire requires a SQLCipher connection.");
        }

        Result<IGrimoireOrdinaryConnectionLease> acquired = await _connections.AcquireScopedAsync(
            connection, CovenantSqliteConnectionMode.ReadWrite, cancellationToken).ConfigureAwait(false);

        if (acquired.IsFailure)
        {
            return acquired.Error;
        }

        await using IGrimoireOrdinaryConnectionLease lease = acquired.Value;

        await using SqliteTransaction sqliteTransaction = lease.Connection.BeginTransaction(deferred: false);

        await using IDbContextTransaction transaction = await _db.Database.UseTransactionAsync(sqliteTransaction, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("EF could not attach the claimed begin transaction.");

        Result<SessionCampaignBinding> binding = await RequireMatchingBindingAsync(
            claimLease.Claim.SessionId, campaign, cancellationToken).ConfigureAwait(false);

        if (binding.IsFailure)
        {
            return binding.Error;
        }

        long? expectedSensitivity = await ReadLiveClaimExpectationAsync(claimLease, cancellationToken).ConfigureAwait(false);

        ClaimInputRow? input = await ReadClaimInputWithinAsync(claimLease.Claim.SessionId, cancellationToken).ConfigureAwait(false);

        if (expectedSensitivity is null || input is null
            || input.HistoryRevision != claimLease.Claim.PreRequestHistoryRevision
            || input.HistoryWatermarkUtc != claimLease.Claim.PreRequestHistoryWatermarkUtc
            || input.SensitivityRevision != expectedSensitivity.Value)
        {
            return StaleClaimBegin();
        }

        int entryCount = await _entryPersistence.GetEntryCountAsync(claimLease.Claim.SessionId, cancellationToken).ConfigureAwait(false);

        Error? limit = SessionEntryPersistence.CheckEntryLimits(entryCount, 2, GetSessionSettings(), prompt, string.Empty);

        if (limit is { } exceeded)
        {
            return exceeded;
        }

        Result<AssistantFinalizationCapacityReservation> consumed = await _finalizationCapacity.ConsumeReservedFinalizationAsync(
            new AssistantFinalizationCapacityIdentity(claimLease.Claim.FinalizationReservationId, claimLease.Claim.SessionId, claimLease.FutureAssistantEntryId),
            new CovenantMutationTransaction(lease.Connection, sqliteTransaction), cancellationToken).ConfigureAwait(false);

        if (consumed.IsFailure)
        {
            return consumed.Error;
        }

        Guid userEntryId = Guid.NewGuid();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        long sequence = await _entryPersistence.ReserveSequenceRangeAsync(claimLease.Claim.SessionId, 2, cancellationToken).ConfigureAwait(false);

        await _entryPersistence.InsertEntriesAsync(
            [
                new Entry
                {
                    Id = userEntryId,
                    SessionId = claimLease.Claim.SessionId,
                    Role = MessageRole.User,
                    Content = prompt,
                    ModelUsed = model,
                    CreatedAt = now,
                    Sequence = sequence,
                },
                new Entry
                {
                    Id = claimLease.FutureAssistantEntryId,
                    SessionId = claimLease.Claim.SessionId,
                    Role = MessageRole.Assistant,
                    Content = string.Empty,
                    ModelUsed = model,
                    CreatedAt = now,
                    Sequence = sequence + 1,
                },
            ], cancellationToken).ConfigureAwait(false);

        await _entryPersistence.BumpSessionUpdatedAtAsync(claimLease.Claim.SessionId, now, cancellationToken).ConfigureAwait(false);

        await _entryPersistence.IncrementUnsummarizedEntryCountIfKnownAsync(claimLease.Claim.SessionId, 2, cancellationToken).ConfigureAwait(false);

        await using (SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(_db,
            """
            UPDATE session_turn_claims
            SET StateCode = 2, UserEntryId = $user, AssistantEntryId = $assistant, HeartbeatAtUtc = $now
            WHERE ClaimId = $claim AND StateCode = 1 AND ExecutorId = $executor AND OwnerBootId = $boot
                AND LeaseDeadlineUtc > $now AND ExpectedCurrentSensitivityRevision = $sensitivity;
            """, cancellationToken).ConfigureAwait(false))
        {
            _ = command.Parameters.AddWithValue("$claim", ClaimGuid(claimLease.Claim.ClaimId));

            _ = command.Parameters.AddWithValue("$user", ClaimGuid(userEntryId));

            _ = command.Parameters.AddWithValue("$assistant", ClaimGuid(claimLease.FutureAssistantEntryId));

            _ = command.Parameters.AddWithValue("$executor", ClaimGuid(claimLease.ExecutorId!.Value));

            _ = command.Parameters.AddWithValue("$boot", ClaimGuid(claimLease.OwnerBootId!.Value));

            _ = command.Parameters.AddWithValue("$now", UtcInstantText.Format(now));

            _ = command.Parameters.AddWithValue("$sensitivity", expectedSensitivity.Value);

            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                return StaleClaimBegin();
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new AssistantReplyBeginReceipt(claimLease.Claim.SessionId, userEntryId, claimLease.FutureAssistantEntryId,
            new SessionTurnInputPreflight(claimLease.Claim.SessionId, binding.Value, input.HistoryRevision, input.TaintedArtifactCount));
    }

    private async Task<long?> ReadLiveClaimExpectationAsync(SessionTurnClaimLease lease, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(_db,
            """
            SELECT claim.ExpectedCurrentSensitivityRevision
            FROM session_turn_claims claim
            JOIN assistant_finalization_capacity_reservations reservation ON reservation.ReservationId = claim.FinalizationReservationId
            WHERE claim.ClaimId = $claim AND claim.SessionId = $session AND claim.StateCode = 1
                AND claim.OriginInstallationId = $origin AND claim.OriginRestoreEpoch = $epoch
                AND claim.ClientTurnId = $client AND claim.SurfaceCode = $surface
                AND claim.RequestDigest = $request AND claim.DependencyDigest = $dependency
                AND claim.PreRequestHistoryRevision = $history AND claim.PreRequestHistoryWatermarkUtc IS $watermark
                AND claim.InputSensitivityRevision = $inputSensitivity AND claim.FinalizationReservationId = $reservation
                AND claim.OwnerBootId = $boot AND claim.ExecutorId = $executor AND claim.LeaseDeadlineUtc > $now
                AND reservation.SessionId = claim.SessionId AND reservation.ClaimId = claim.ClaimId
                AND reservation.OriginCode = 1 AND reservation.StateCode = 1 AND reservation.AssistantEntryId = $assistant;
            """, cancellationToken).ConfigureAwait(false);

        _ = command.Parameters.AddWithValue("$claim", ClaimGuid(lease.Claim.ClaimId));

        _ = command.Parameters.AddWithValue("$session", ClaimGuid(lease.Claim.SessionId));

        _ = command.Parameters.AddWithValue("$origin", ClaimGuid(lease.Claim.OriginInstallationId));

        _ = command.Parameters.AddWithValue("$epoch", lease.Claim.OriginRestoreEpoch);

        _ = command.Parameters.AddWithValue("$client", ClaimGuid(lease.Claim.ClientTurnId));

        _ = command.Parameters.AddWithValue("$surface", (int)lease.Claim.Surface);

        _ = command.Parameters.AddWithValue("$request", lease.Claim.RequestDigest.Bytes.ToArray());

        _ = command.Parameters.AddWithValue("$dependency", lease.Claim.DependencyDigest.Bytes.ToArray());

        _ = command.Parameters.AddWithValue("$history", lease.Claim.PreRequestHistoryRevision);

        _ = command.Parameters.AddWithValue("$watermark", lease.Claim.PreRequestHistoryWatermarkUtc is { } watermark ? UtcInstantText.Format(watermark) : DBNull.Value);

        _ = command.Parameters.AddWithValue("$inputSensitivity", lease.Claim.InputSensitivityRevision);

        _ = command.Parameters.AddWithValue("$reservation", ClaimGuid(lease.Claim.FinalizationReservationId));

        _ = command.Parameters.AddWithValue("$boot", ClaimGuid(lease.OwnerBootId!.Value));

        _ = command.Parameters.AddWithValue("$executor", ClaimGuid(lease.ExecutorId!.Value));

        _ = command.Parameters.AddWithValue("$assistant", ClaimGuid(lease.FutureAssistantEntryId));

        _ = command.Parameters.AddWithValue("$now", UtcInstantText.Format(DateTimeOffset.UtcNow));

        object? expected = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return expected is null or DBNull ? null : Convert.ToInt64(expected, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<ClaimInputRow?> ReadClaimInputWithinAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(_db,
            """
            SELECT COALESCE((SELECT MAX(e.Sequence) FROM "Entries" e WHERE e.SessionId = s.Id), 0),
                (SELECT e.CreatedAt FROM "Entries" e WHERE e.SessionId = s.Id ORDER BY e.Sequence DESC LIMIT 1),
                COALESCE(sensitivity.Revision, 0), COALESCE(sensitivity.TaintedArtifactCount, 0)
            FROM "Sessions" s LEFT JOIN session_sensitivity_state sensitivity ON sensitivity.SessionId = s.Id
            WHERE s.Id = $session;
            """, cancellationToken).ConfigureAwait(false);

        _ = command.Parameters.AddWithValue("$session", ClaimGuid(sessionId));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ClaimInputRow(reader.GetInt64(0), reader.IsDBNull(1) ? null : UtcInstantText.Parse(reader.GetString(1)), reader.GetInt64(2), reader.GetInt32(3))
            : null;
    }

    private static string ClaimGuid(Guid id) => id.ToString("D").ToUpperInvariant();

    private static Error StaleClaimBegin() => new(ErrorCodes.Covenant.StaleSnapshot,
        "This Session turn claim is stale or its frozen history and sensitivity no longer match.");

    private sealed record ClaimInputRow(long HistoryRevision, DateTimeOffset? HistoryWatermarkUtc, long SensitivityRevision, int TaintedArtifactCount);
}
