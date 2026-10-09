using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Repositories;

/// <summary>Proves a native claimed reply clean before reading its body in the same SQL snapshot.</summary>
public sealed partial class GrimoireRepository : ISessionTurnClaimReplayStore
{
    public async ValueTask<Result<GrimoireEntryDto>> ReadCleanCommittedReplyAsync(
        SessionTurnClaimLease claimLease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claimLease);

        if (claimLease.Disposition is not SessionTurnClaimDisposition.Replayed
            || claimLease.Claim.State is not SessionTurnClaimState.Committed
            || claimLease.Claim.AssistantEntryId != claimLease.FutureAssistantEntryId
            || claimLease.ExecutorId is not null || claimLease.LeaseDeadlineUtc is not null)
        {
            return ClaimedReplyUnavailable();
        }

        try
        {
            return await ReadCleanClaimedReplyWithinAsync(claimLease, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Clean claimed reply proof failed for Session {SessionId}.", claimLease.Claim.SessionId);

            return ClaimedReplyUnavailable();
        }
    }

    private async Task<Result<GrimoireEntryDto>> ReadCleanClaimedReplyWithinAsync(
        SessionTurnClaimLease claimLease,
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

        await using SqliteTransaction sqliteTransaction = lease.Connection.BeginTransaction(deferred: true);

        await using IDbContextTransaction transaction = await _db.Database.UseTransactionAsync(sqliteTransaction, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("EF could not attach the claimed replay snapshot.");

        // Covenant labels are outside the governed identity family. Normalize their identity to
        // refuse every spelling of a protected label; the native Entry/claim keys remain exact seeks.
        await using (SqliteCommand proof = await GrimoireSqlCommandFactory.CreateAsync(_db,
            $"""
            SELECT final.ContentSensitivityCode, final.ContentSensitivityDigest,
                EXISTS(SELECT 1 FROM artifact_sensitivity label
                    WHERE label.ArtifactKindCode = $kind AND {CovenantIdentitySql.Keyed("label.ArtifactId", "$artifactKey")})
            FROM session_turn_claims claim
            JOIN assistant_finalization_capacity_reservations reservation ON reservation.ReservationId = claim.FinalizationReservationId
            JOIN assistant_entry_finalizations final ON final.AssistantEntryId = claim.AssistantEntryId AND final.SessionId = claim.SessionId
            JOIN Entries entry ON entry.Id = claim.AssistantEntryId AND entry.SessionId = claim.SessionId AND entry.Role = 2
            WHERE claim.ClaimId = $claim AND claim.SessionId = $session AND claim.StateCode = 3
                AND claim.OriginInstallationId = $origin AND claim.OriginRestoreEpoch = $epoch
                AND claim.ClientTurnId = $client AND claim.SurfaceCode = $surface
                AND claim.RequestDigest = $request AND claim.DependencyDigest = $dependency
                AND claim.PreRequestHistoryRevision = $history AND claim.PreRequestHistoryWatermarkUtc IS $watermark
                AND claim.InputSensitivityRevision = $inputSensitivity AND claim.FinalizationReservationId = $reservation
                AND claim.UserEntryId = $user AND claim.AssistantEntryId = $assistant
                AND claim.ExecutorId IS NULL AND claim.LeaseDeadlineUtc IS NULL
                AND reservation.SessionId = claim.SessionId AND reservation.ClaimId = claim.ClaimId
                AND reservation.OriginCode = 1 AND reservation.StateCode = 2 AND reservation.AssistantEntryId = claim.AssistantEntryId
                AND final.OutcomeCode = 1 AND final.RequestDigest = claim.RequestDigest AND final.SourceEvidenceDigest IS NULL;
            """, cancellationToken).ConfigureAwait(false))
        {
            BindClaimedReplayProof(proof, claimLease);

            await using SqliteDataReader row = await proof.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (!await row.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return ClaimedReplyUnavailable();
            }

            if (row.GetBoolean(2) || row.GetInt32(0) != (int)ContentSensitivity.None)
            {
                return new Error(ErrorCodes.Covenant.OperatorAuthorityUnavailable, "This retained reply requires Covenant authority.");
            }

            CovenantDigest cleanDigest = CovenantDigests.Sensitivity(new SensitivityDigestInput(
                ContentSensitivity.None, GenerationProvenanceMode.Exact, [], []));

            if (!row.GetFieldValue<byte[]>(1).AsSpan().SequenceEqual(cleanDigest.Bytes))
            {
                return ClaimedReplyUnavailable();
            }
        }

        // Body length is evaluated only after clean proof, then bounded before text allocation.
        await using (SqliteCommand length = await GrimoireSqlCommandFactory.CreateAsync(_db,
            "SELECT length(CAST(Content AS BLOB)) FROM Entries WHERE Id = $assistant AND SessionId = $session;",
            cancellationToken).ConfigureAwait(false))
        {
            _ = length.Parameters.AddWithValue("$assistant", ClaimGuid(claimLease.FutureAssistantEntryId));

            _ = length.Parameters.AddWithValue("$session", ClaimGuid(claimLease.Claim.SessionId));

            object? bytes = await length.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            if (bytes is null or DBNull || Convert.ToInt64(bytes, System.Globalization.CultureInfo.InvariantCulture) > GetSessionSettings().MaxEntryContentBytes)
            {
                return ClaimedReplyUnavailable();
            }
        }

        await using SqliteCommand read = await GrimoireSqlCommandFactory.CreateAsync(_db,
            "SELECT Content,ModelUsed,CreatedAt,IsPinned FROM Entries WHERE Id = $assistant AND SessionId = $session;",
            cancellationToken).ConfigureAwait(false);

        _ = read.Parameters.AddWithValue("$assistant", ClaimGuid(claimLease.FutureAssistantEntryId));

        _ = read.Parameters.AddWithValue("$session", ClaimGuid(claimLease.Claim.SessionId));

        await using SqliteDataReader entry = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await entry.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new GrimoireEntryDto(claimLease.FutureAssistantEntryId, MessageRole.Assistant, entry.GetString(0), entry.GetString(1),
                UtcInstantText.Parse(entry.GetString(2)), entry.GetBoolean(3))
            : ClaimedReplyUnavailable();
    }

    private static void BindClaimedReplayProof(SqliteCommand command, SessionTurnClaimLease lease)
    {
        _ = command.Parameters.AddWithValue("$claim", ClaimGuid(lease.Claim.ClaimId));

        _ = command.Parameters.AddWithValue("$session", ClaimGuid(lease.Claim.SessionId));

        _ = command.Parameters.AddWithValue("$origin", ClaimGuid(lease.Claim.OriginInstallationId));

        _ = command.Parameters.AddWithValue("$epoch", lease.Claim.OriginRestoreEpoch);

        _ = command.Parameters.AddWithValue("$client", ClaimGuid(lease.Claim.ClientTurnId));

        _ = command.Parameters.AddWithValue("$surface", (int)lease.Claim.Surface);

        _ = command.Parameters.AddWithValue("$request", lease.Claim.RequestDigest.Bytes);

        _ = command.Parameters.AddWithValue("$dependency", lease.Claim.DependencyDigest.Bytes);

        _ = command.Parameters.AddWithValue("$history", lease.Claim.PreRequestHistoryRevision);

        _ = command.Parameters.AddWithValue("$watermark", lease.Claim.PreRequestHistoryWatermarkUtc is { } watermark ? UtcInstantText.Format(watermark) : DBNull.Value);

        _ = command.Parameters.AddWithValue("$inputSensitivity", lease.Claim.InputSensitivityRevision);

        _ = command.Parameters.AddWithValue("$reservation", ClaimGuid(lease.Claim.FinalizationReservationId));

        _ = command.Parameters.AddWithValue("$user", lease.Claim.UserEntryId is { } user ? ClaimGuid(user) : DBNull.Value);

        _ = command.Parameters.AddWithValue("$assistant", ClaimGuid(lease.FutureAssistantEntryId));

        _ = command.Parameters.AddWithValue("$kind", (int)SensitiveArtifactKind.AssistantEntry);

        _ = command.Parameters.AddWithValue("$artifactKey", CovenantIdentitySql.Key(lease.FutureAssistantEntryId));
    }

    private static Error ClaimedReplyUnavailable() => new(ErrorCodes.Covenant.Unavailable,
        "The authoritative reply is unavailable for this terminal Session turn.");
}
