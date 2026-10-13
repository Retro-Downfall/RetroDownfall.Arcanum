using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

internal sealed partial class LongRestAttemptStore
{
    public Task<Result<SessionTurnClaimLease>> RenewClaimAsync(SessionTurnClaimLease lease, CanonicalCampaignContext scope, CancellationToken ct) =>
        WithinAsync(async (connection, transaction) =>
        {
            DateTimeOffset now = timeProvider.GetUtcNow();

            long sensitivity = await ValidatePendingClaimWithinTransactionAsync(connection, transaction, lease, scope, now, ct).ConfigureAwait(false);

            DateTimeOffset deadline = now.AddMinutes(5);

            await using SqliteCommand command = Command(connection, transaction, """
                UPDATE session_turn_claims SET LeaseDeadlineUtc = $deadline, HeartbeatAtUtc = $now
                WHERE ClaimId = $claim AND StateCode = 1 AND ExecutorId = $executor AND OwnerBootId = $boot AND LeaseDeadlineUtc > $now;
                """, ("$claim", Format(lease.Claim.ClaimId)), ("$executor", Format(lease.ExecutorId!.Value)), ("$boot", Format(lease.OwnerBootId!.Value)),
                ("$now", UtcInstantText.Format(now)), ("$deadline", UtcInstantText.Format(deadline)));

            if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                throw new InvalidDataException();
            }

            return lease with { Claim = lease.Claim with { ExpectedCurrentSensitivityRevision = sensitivity }, LeaseDeadlineUtc = deadline };
        }, ct);

    internal static async Task<long> ValidatePendingClaimWithinTransactionAsync(SqliteConnection connection, SqliteTransaction transaction,
        SessionTurnClaimLease lease, CanonicalCampaignContext scope, DateTimeOffset now, CancellationToken ct)
    {
        if (lease is null || lease.Claim is null || !lease.IsExecutable || lease.ExecutorId is null || lease.OwnerBootId is null
            || lease.LeaseDeadlineUtc is null || lease.LeaseDeadlineUtc <= now || lease.Claim.State != SessionTurnClaimState.PendingMaintenance
            || lease.Claim.OwnerBootId != lease.OwnerBootId || lease.Claim.OriginRestoreEpoch < 0)
        {
            throw new InvalidDataException();
        }

        bool campaign = scope.IsCampaignBound;

        SessionTurnClaim claim = lease.Claim;

        await using SqliteCommand command = Command(connection, transaction, """
            SELECT c.ExpectedCurrentSensitivityRevision FROM session_turn_claims c
            JOIN Sessions session ON session.Id = c.SessionId
            JOIN session_campaign_bindings binding ON binding.SessionId = session.Id
            JOIN assistant_finalization_capacity_reservations reservation ON reservation.ReservationId = c.FinalizationReservationId
                AND reservation.SessionId = c.SessionId AND reservation.ClaimId = c.ClaimId AND reservation.StateCode = 1 AND reservation.OriginCode = 1
            WHERE c.ClaimId = $claim AND c.SessionId = $session AND c.StateCode = 1 AND c.ExecutorId = $executor AND c.OwnerBootId = $boot
                AND c.LeaseDeadlineUtc > $now AND c.RequestDigest = $request AND c.DependencyDigest = $dependency
                AND c.OriginInstallationId = $origin AND c.OriginRestoreEpoch = $restore AND c.ClientTurnId = $client AND c.SurfaceCode = $surface
                AND c.FinalizationReservationId = $reservation AND reservation.AssistantEntryId = $future
                AND c.CheckpointRevision = $checkpoint AND c.CompletedStepMask = $mask
                AND c.PreRequestHistoryRevision = $history AND c.InputSensitivityRevision = $inputSensitivity
                AND c.PreRequestHistoryRevision = COALESCE((SELECT MAX(Sequence) FROM Entries WHERE SessionId = c.SessionId), 0)
                AND c.ExpectedCurrentSensitivityRevision = COALESCE((SELECT Revision FROM session_sensitivity_state WHERE SessionId = c.SessionId), 0)
                AND (($campaign IS NULL AND binding.BindingKindCode = 1 AND binding.CampaignId IS NULL)
                    OR ($campaign IS NOT NULL AND binding.BindingKindCode = 2 AND binding.CampaignId = $campaign
                        AND EXISTS(SELECT 1 FROM Campaigns WHERE Id = $campaign)
                        AND (SELECT RegistryEpoch FROM campaign_registry_state WHERE StateKey = 1) = $availability
                        AND (($pathRevision IS NULL AND NOT EXISTS(SELECT 1 FROM campaign_path_identities WHERE CampaignId = $campaign))
                            OR EXISTS(SELECT 1 FROM campaign_path_identities WHERE CampaignId = $campaign AND PolicyVersion = $policy
                                AND Revision = $pathRevision AND PhysicalIdentityDigest = $root))));
            """, ("$claim", Format(claim.ClaimId)), ("$session", Format(claim.SessionId)), ("$executor", Format(lease.ExecutorId.Value)),
            ("$boot", Format(lease.OwnerBootId.Value)), ("$now", UtcInstantText.Format(now)), ("$request", claim.RequestDigest.Bytes),
            ("$dependency", claim.DependencyDigest.Bytes), ("$origin", Format(claim.OriginInstallationId)), ("$restore", claim.OriginRestoreEpoch),
            ("$client", Format(claim.ClientTurnId)), ("$surface", (int)claim.Surface), ("$reservation", Format(claim.FinalizationReservationId)),
            ("$future", Format(lease.FutureAssistantEntryId)), ("$checkpoint", claim.CheckpointRevision), ("$mask", claim.CompletedStepMask),
            ("$history", claim.PreRequestHistoryRevision), ("$inputSensitivity", claim.InputSensitivityRevision),
            ("$campaign", campaign ? Format(scope.CampaignId!.Value) : null), ("$availability", scope.CampaignAvailabilityGeneration),
            ("$pathRevision", scope.PathIdentityRevision), ("$policy", scope.PathIdentityPolicyVersion), ("$root", scope.RootIdentityDigest?.Bytes));

        if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not long sensitivity)
        {
            throw new InvalidDataException();
        }

        return sensitivity;
    }
}
