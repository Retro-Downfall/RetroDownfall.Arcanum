using System.Globalization;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>Durable claim fencing and physical provider-attempt allocation.</summary>
internal sealed class CampaignMaintenanceCheckpointStore(ICovenantConnectionSource connections) : ICampaignMaintenanceCheckpointStore
{
    private static readonly TimeSpan ClaimLeaseDuration = TimeSpan.FromMinutes(5);

    public async Task<Result> ValidateClaimAsync(SessionTurnClaimLease claimLease, CanonicalCampaignContext campaign, CancellationToken cancellationToken)
    {
        Result<long> result = await WithinAsync(tx => ValidateClaimWithinAsync(tx, claimLease, campaign, cancellationToken), cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? Result.Success() : result.Error;
    }

    public Task<Result<SessionTurnClaimLease>> RenewClaimAsync(SessionTurnClaimLease claimLease, CanonicalCampaignContext campaign, CancellationToken cancellationToken) =>
        WithinAsync<SessionTurnClaimLease>(async tx =>
        {
            Result<long> valid = await ValidateClaimWithinAsync(tx, claimLease, campaign, cancellationToken).ConfigureAwait(false);

            if (valid.IsFailure)
            {
                return valid.Error;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;

            DateTimeOffset deadline = now + ClaimLeaseDuration;

            await using SqliteCommand command = tx.CreateCommand();

            command.CommandText = """
                UPDATE session_turn_claims SET LeaseDeadlineUtc = $deadline, HeartbeatAtUtc = $now
                WHERE ClaimId = $claim AND StateCode = 1 AND ExecutorId = $executor
                  AND OwnerBootId = $boot AND LeaseDeadlineUtc > $now;
                """;

            BindClaim(command, claimLease);

            Bind(command, "$now", Iso(now));

            Bind(command, "$deadline", Iso(deadline));

            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                return Stale();
            }

            return Result<SessionTurnClaimLease>.Success(claimLease with
            {
                Claim = claimLease.Claim with { ExpectedCurrentSensitivityRevision = valid.Value },
                LeaseDeadlineUtc = deadline,
            });
        }, cancellationToken);

    public Task<Result<CampaignMaintenanceAttempt>> PrepareAttemptAsync(CampaignMaintenanceIdentity identity, CovenantDigest providerCallDigest, CancellationToken cancellationToken) =>
        WithinAsync<CampaignMaintenanceAttempt>(async tx =>
        {
            if (!ValidIdentity(identity))
            {
                return Stale();
            }

            Result<long> claim = await ValidateClaimWithinAsync(tx, identity.ClaimLease, identity.Campaign, cancellationToken).ConfigureAwait(false);

            if (claim.IsFailure)
            {
                return claim.Error;
            }

            if (identity.SourceSessionId is { } source && !await IsBoundAsync(tx, source, identity.Campaign.CampaignId!.Value, cancellationToken).ConfigureAwait(false))
            {
                return Stale();
            }

            CheckpointRow? row = await ReadAsync(tx, identity, cancellationToken).ConfigureAwait(false);

            if (row is not null && !MatchesIdentity(row, identity))
            {
                return Stale();
            }

            if (row?.State is CovenantMaintenanceCheckpoint.Committed)
            {
                return Result<CampaignMaintenanceAttempt>.Success(ToAttempt(row, identity, claim.Value));
            }

            if (row?.State is CovenantMaintenanceCheckpoint.Failed
                || (row is not null && row.ExecutorId != identity.ClaimLease.ExecutorId
                    && identity.ClaimLease.Disposition is not SessionTurnClaimDisposition.Adopted))
            {
                return Stale();
            }

            long ordinal;

            await using (SqliteCommand next = tx.CreateCommand())
            {
                next.CommandText = "SELECT COALESCE(MAX(PhysicalProviderAttemptOrdinal), 0) FROM campaign_maintenance_checkpoints WHERE ClaimId = $claim AND StepCode = $step;";

                BindIdentity(next, identity);

                long last = Convert.ToInt64(await next.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);

                if (last == long.MaxValue)
                {
                    return Stale();
                }

                ordinal = last + 1;
            }

            long revision = row is null ? 0 : checked(row.Revision + 1);

            await using SqliteCommand command = tx.CreateCommand();

            command.CommandText = row is null ? """
                INSERT INTO campaign_maintenance_checkpoints
                    (ClaimId, StepCode, CampaignId, SessionId, SourceSessionId, ExecutorId, ExpectedOutputRevision,
                     PhysicalProviderAttemptOrdinal, CheckpointStateCode, InputSourceGeneration, InputManifestDigest,
                     ProviderCallDigest, CheckpointRevision, UpdatedAtUtc)
                VALUES($claim, $step, $campaign, $session, $source, $executor, $expected, $ordinal, 1,
                       $generation, $manifest, $provider, $revision, $now);
                """ : """
                UPDATE campaign_maintenance_checkpoints
                SET ExecutorId = $executor, PhysicalProviderAttemptOrdinal = $ordinal, ProviderCallDigest = $provider,
                    DisclosureReceiptDigest = NULL, CheckpointRevision = $revision, UpdatedAtUtc = $now
                WHERE ClaimId = $claim AND StepCode = $step AND InputManifestDigest = $manifest
                  AND CheckpointStateCode = 1 AND CheckpointRevision = $oldRevision;
                """;

            BindIdentity(command, identity);

            Bind(command, "$ordinal", ordinal);

            Bind(command, "$provider", providerCallDigest.Bytes);

            Bind(command, "$revision", revision);

            Bind(command, "$oldRevision", row?.Revision ?? 0);

            Bind(command, "$now", Iso(DateTimeOffset.UtcNow));

            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                return Stale();
            }

            return Result<CampaignMaintenanceAttempt>.Success(new(identity, revision, (ulong)ordinal, providerCallDigest,
                CovenantMaintenanceCheckpoint.Prepared, null, null, claim.Value));
        }, cancellationToken);

    public async Task<Result> ValidateAttemptAsync(CampaignMaintenanceAttempt attempt, CancellationToken cancellationToken)
    {
        Result<long> result = await WithinAsync(tx => ValidateAttemptWithinAsync(tx, attempt, cancellationToken), cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? Result.Success() : result.Error;
    }

    public Task<Result<CampaignMaintenanceAttempt>> RecordDisclosureAsync(CampaignMaintenanceAttempt attempt, CovenantDigest disclosureReceiptDigest, CancellationToken cancellationToken) =>
        WithinAsync<CampaignMaintenanceAttempt>(async tx =>
        {
            Result<long> valid = await ValidateAttemptWithinAsync(tx, attempt, cancellationToken).ConfigureAwait(false);

            if (valid.IsFailure)
            {
                return valid.Error;
            }

            await using SqliteCommand command = tx.CreateCommand();

            command.CommandText = """
                UPDATE campaign_maintenance_checkpoints
                SET DisclosureReceiptDigest = $disclosure, CheckpointRevision = CheckpointRevision + 1, UpdatedAtUtc = $now
                WHERE ClaimId = $claim AND StepCode = $step AND InputManifestDigest = $manifest
                  AND CheckpointStateCode = 1 AND CheckpointRevision = $revision;
                """;

            BindIdentity(command, attempt.Identity);

            Bind(command, "$disclosure", disclosureReceiptDigest.Bytes);

            Bind(command, "$revision", attempt.CheckpointRevision);

            Bind(command, "$now", Iso(DateTimeOffset.UtcNow));

            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1
                ? Result<CampaignMaintenanceAttempt>.Success(attempt with { CheckpointRevision = checked(attempt.CheckpointRevision + 1) })
                : Stale();
        }, cancellationToken);

    internal static async Task<Result> ValidatePublicationAsync(
        CovenantMutationTransaction transaction,
        CampaignMaintenancePublication publication,
        CampaignMaintenanceIdentity expectedIdentity,
        CancellationToken cancellationToken)
    {
        if (publication.Attempt.Identity != expectedIdentity)
        {
            return Stale();
        }

        Result<long> valid = await ValidateAttemptWithinAsync(transaction, publication.Attempt, cancellationToken).ConfigureAwait(false);

        if (valid.IsFailure)
        {
            return valid.Error;
        }

        CheckpointRow? row = await ReadAsync(transaction, expectedIdentity, cancellationToken).ConfigureAwait(false);

        return row?.Disclosure == publication.DisclosureReceiptDigest ? Result.Success() : Stale();
    }

    internal static async Task<Result> CommitPublicationAsync(
        CovenantMutationTransaction transaction,
        CampaignMaintenancePublication publication,
        CampaignRollupArtifact output,
        CancellationToken cancellationToken)
    {
        CampaignMaintenanceAttempt attempt = publication.Attempt;

        if (output.CampaignId != attempt.Identity.Campaign.CampaignId
            || output.SessionId != attempt.Identity.SourceSessionId
            || output.Revision != attempt.Identity.ExpectedOutputRevision + 1
            || output.SourceGeneration != attempt.Identity.SourceGeneration
            || (attempt.Identity.Step is CovenantMaintenanceStep.CampaignContribution
                && output.SourceManifestDigest != attempt.Identity.SourceManifestDigest))
        {
            return Stale();
        }

        await using (SqliteCommand command = transaction.CreateCommand())
        {
            command.CommandText = """
                UPDATE campaign_maintenance_checkpoints
                SET CheckpointStateCode = 2, OutputArtifactKindCode = $kind, OutputArtifactId = $artifact,
                    OutputRevision = $outputRevision, OutputContentDigest = $content, OutputSensitivityDigest = $sensitivity,
                    CheckpointRevision = CheckpointRevision + 1, UpdatedAtUtc = $now
                WHERE ClaimId = $claim AND StepCode = $step AND InputManifestDigest = $manifest
                  AND CheckpointStateCode = 1 AND CheckpointRevision = $revision AND ExecutorId = $executor
                  AND PhysicalProviderAttemptOrdinal = $ordinal AND ProviderCallDigest = $provider
                  AND DisclosureReceiptDigest = $disclosure;
                """;

            BindIdentity(command, attempt.Identity);

            Bind(command, "$kind", output.SessionId is null ? 14 : 15);

            Bind(command, "$artifact", output.ArtifactId);

            Bind(command, "$outputRevision", output.Revision);

            Bind(command, "$content", output.ContentDigest.Bytes);

            Bind(command, "$sensitivity", output.SensitivityDigest.Bytes);

            Bind(command, "$revision", attempt.CheckpointRevision);

            Bind(command, "$ordinal", checked((long)attempt.PhysicalProviderAttemptOrdinal));

            Bind(command, "$provider", attempt.ProviderCallDigest.Bytes);

            Bind(command, "$disclosure", publication.DisclosureReceiptDigest.Bytes);

            Bind(command, "$now", Iso(DateTimeOffset.UtcNow));

            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                return Stale();
            }
        }

        await using SqliteCommand advance = transaction.CreateCommand();

        advance.CommandText = output.SessionId == attempt.Identity.ClaimLease.Claim.SessionId ? """
            UPDATE session_turn_claims
            SET ExpectedCurrentSensitivityRevision = COALESCE((SELECT Revision FROM session_sensitivity_state WHERE SessionId = $session), 0)
            WHERE ClaimId = $claim AND StateCode = 1 AND ExecutorId = $executor AND OwnerBootId = $boot
              AND LeaseDeadlineUtc > $now AND ExpectedCurrentSensitivityRevision = $expectedSensitivity;
            """ : """
            SELECT COUNT(*) FROM session_turn_claims
            WHERE ClaimId = $claim AND StateCode = 1 AND ExecutorId = $executor AND OwnerBootId = $boot
              AND LeaseDeadlineUtc > $now AND ExpectedCurrentSensitivityRevision = $expectedSensitivity;
            """;

        BindClaim(advance, attempt.Identity.ClaimLease);

        Bind(advance, "$expectedSensitivity", attempt.ExpectedClaimSensitivityRevision);

        Bind(advance, "$now", Iso(DateTimeOffset.UtcNow));

        long changed = output.SessionId == attempt.Identity.ClaimLease.Claim.SessionId
            ? await advance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false)
            : Convert.ToInt64(await advance.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);

        return changed == 1 ? Result.Success() : Stale();
    }

    private async Task<Result<T>> WithinAsync<T>(Func<CovenantMutationTransaction, Task<Result<T>>> action, CancellationToken cancellationToken)
    {
        SqliteConnection connection = await connections.GetOpenCoreConnectionAsync(cancellationToken).ConfigureAwait(false);

        return await SqliteBusyRetry.ExecuteAsync(async () =>
        {
            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

            Result<T> result = await action(new(connection, transaction)).ConfigureAwait(false);

            if (result.IsSuccess)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return result;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Result<long>> ValidateClaimWithinAsync(CovenantMutationTransaction transaction, SessionTurnClaimLease lease, CanonicalCampaignContext campaign, CancellationToken cancellationToken)
    {
        if (!lease.IsExecutable || lease.ExecutorId is null || lease.OwnerBootId is null || lease.Claim.State is not SessionTurnClaimState.PendingMaintenance || !campaign.IsCampaignBound)
        {
            return Stale();
        }

        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = """
            SELECT c.ExpectedCurrentSensitivityRevision
            FROM session_turn_claims c
            JOIN "Sessions" s ON s.Id = c.SessionId
            JOIN session_campaign_bindings b ON b.SessionId = c.SessionId AND b.BindingKindCode = 2 AND b.CampaignId = $campaign
            JOIN "Campaigns" p ON p.Id = b.CampaignId
            WHERE c.ClaimId = $claim AND c.SessionId = $session AND c.StateCode = 1
              AND c.ExecutorId = $executor AND c.OwnerBootId = $boot AND c.LeaseDeadlineUtc > $now
              AND c.RequestDigest = $request AND c.DependencyDigest = $dependency
              AND c.OriginInstallationId = $origin AND c.OriginRestoreEpoch = $restore
              AND c.PreRequestHistoryRevision = $history AND c.InputSensitivityRevision = $inputSensitivity
              AND c.PreRequestHistoryRevision = COALESCE((SELECT MAX(Sequence) FROM "Entries" WHERE SessionId = c.SessionId), 0)
              AND c.ExpectedCurrentSensitivityRevision = COALESCE((SELECT Revision FROM session_sensitivity_state WHERE SessionId = c.SessionId), 0)
              AND (SELECT RegistryEpoch FROM campaign_registry_state WHERE StateKey = 1) = $availability
              AND (($pathRevision IS NULL AND NOT EXISTS(SELECT 1 FROM campaign_path_identities WHERE CampaignId = $campaign))
                  OR EXISTS(SELECT 1 FROM campaign_path_identities WHERE CampaignId = $campaign AND PolicyVersion = $policy
                      AND Revision = $pathRevision AND PhysicalIdentityDigest = $root));
            """;

        BindClaim(command, lease);

        Bind(command, "$campaign", campaign.CampaignId);

        Bind(command, "$now", Iso(DateTimeOffset.UtcNow));

        Bind(command, "$request", lease.Claim.RequestDigest.Bytes);

        Bind(command, "$dependency", lease.Claim.DependencyDigest.Bytes);

        Bind(command, "$origin", lease.Claim.OriginInstallationId);

        Bind(command, "$restore", lease.Claim.OriginRestoreEpoch);

        Bind(command, "$history", lease.Claim.PreRequestHistoryRevision);

        Bind(command, "$inputSensitivity", lease.Claim.InputSensitivityRevision);

        Bind(command, "$availability", campaign.CampaignAvailabilityGeneration);

        Bind(command, "$policy", campaign.PathIdentityPolicyVersion);

        Bind(command, "$pathRevision", campaign.PathIdentityRevision);

        Bind(command, "$root", campaign.RootIdentityDigest?.Bytes);

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull ? Stale() : Result<long>.Success(Convert.ToInt64(value, CultureInfo.InvariantCulture));
    }

    private static async Task<Result<long>> ValidateAttemptWithinAsync(CovenantMutationTransaction transaction, CampaignMaintenanceAttempt attempt, CancellationToken cancellationToken)
    {
        Result<long> valid = await ValidateClaimWithinAsync(transaction, attempt.Identity.ClaimLease, attempt.Identity.Campaign, cancellationToken).ConfigureAwait(false);

        if (valid.IsFailure)
        {
            return valid.Error;
        }

        CheckpointRow? row = await ReadAsync(transaction, attempt.Identity, cancellationToken).ConfigureAwait(false);

        return row is not null && MatchesIdentity(row, attempt.Identity)
            && row.State is CovenantMaintenanceCheckpoint.Prepared
            && row.ExecutorId == attempt.Identity.ClaimLease.ExecutorId
            && row.Revision == attempt.CheckpointRevision && row.Ordinal == attempt.PhysicalProviderAttemptOrdinal
            && row.Provider == attempt.ProviderCallDigest && valid.Value == attempt.ExpectedClaimSensitivityRevision
                ? valid : Stale();
    }

    private static async Task<CheckpointRow?> ReadAsync(CovenantMutationTransaction transaction, CampaignMaintenanceIdentity identity, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = """
            SELECT CampaignId, SessionId, SourceSessionId, ExecutorId, ExpectedOutputRevision, InputSourceGeneration,
                CheckpointStateCode, CheckpointRevision, PhysicalProviderAttemptOrdinal, ProviderCallDigest,
                DisclosureReceiptDigest, OutputArtifactId, OutputRevision
            FROM campaign_maintenance_checkpoints WHERE ClaimId = $claim AND StepCode = $step AND InputManifestDigest = $manifest;
            """;

        BindIdentity(command, identity);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? new(
            Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
            Guid.Parse(reader.GetString(3)), reader.GetInt64(4), reader.GetInt64(5), (CovenantMaintenanceCheckpoint)reader.GetInt32(6),
            reader.GetInt64(7), checked((ulong)reader.GetInt64(8)), new((byte[])reader[9]), reader.IsDBNull(10) ? null : new CovenantDigest((byte[])reader[10]),
            reader.IsDBNull(11) ? null : Guid.Parse(reader.GetString(11)), reader.IsDBNull(12) ? null : reader.GetInt64(12)) : null;
    }

    private static async Task<bool> IsBoundAsync(CovenantMutationTransaction transaction, Guid session, Guid campaign, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = transaction.CreateCommand();

        command.CommandText = "SELECT COUNT(*) FROM session_campaign_bindings WHERE SessionId = $source AND BindingKindCode = 2 AND CampaignId = $campaign;";

        Bind(command, "$source", session);

        Bind(command, "$campaign", campaign);

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) == 1;
    }

    private static bool ValidIdentity(CampaignMaintenanceIdentity identity) => identity.SourceGeneration >= 0 && identity.ExpectedOutputRevision >= 0
        && ((identity.Step is CovenantMaintenanceStep.CampaignRollup && identity.SourceSessionId is null)
            || (identity.Step is CovenantMaintenanceStep.CampaignContribution && identity.SourceSessionId is { } session && session != Guid.Empty));

    private static bool MatchesIdentity(CheckpointRow row, CampaignMaintenanceIdentity identity) => row.CampaignId == identity.Campaign.CampaignId
        && row.SessionId == identity.ClaimLease.Claim.SessionId && row.SourceSessionId == identity.SourceSessionId
        && row.ExpectedOutputRevision == identity.ExpectedOutputRevision && row.SourceGeneration == identity.SourceGeneration;

    private static CampaignMaintenanceAttempt ToAttempt(CheckpointRow row, CampaignMaintenanceIdentity identity, long sensitivityRevision) =>
        new(identity, row.Revision, row.Ordinal, row.Provider, row.State, row.OutputArtifactId, row.OutputRevision, sensitivityRevision);

    private static void BindIdentity(SqliteCommand command, CampaignMaintenanceIdentity identity)
    {
        BindClaim(command, identity.ClaimLease);

        Bind(command, "$step", (int)identity.Step);

        Bind(command, "$campaign", identity.Campaign.CampaignId);

        Bind(command, "$source", identity.SourceSessionId);

        Bind(command, "$expected", identity.ExpectedOutputRevision);

        Bind(command, "$generation", identity.SourceGeneration);

        Bind(command, "$manifest", identity.SourceManifestDigest.Bytes);
    }

    private static void BindClaim(SqliteCommand command, SessionTurnClaimLease lease)
    {
        Bind(command, "$claim", lease.Claim.ClaimId);

        Bind(command, "$session", lease.Claim.SessionId);

        Bind(command, "$executor", lease.ExecutorId);

        Bind(command, "$boot", lease.OwnerBootId);
    }

    private static void Bind(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string Iso(DateTimeOffset value) => UtcInstantText.Format(value);

    private static Error Stale() => new(ErrorCodes.Covenant.StaleSnapshot, "The Campaign maintenance claim, source vector, or checkpoint changed.");

    private sealed record CheckpointRow(Guid CampaignId, Guid SessionId, Guid? SourceSessionId, Guid ExecutorId, long ExpectedOutputRevision,
        long SourceGeneration, CovenantMaintenanceCheckpoint State, long Revision, ulong Ordinal, CovenantDigest Provider,
        CovenantDigest? Disclosure, Guid? OutputArtifactId, long? OutputRevision);
}
