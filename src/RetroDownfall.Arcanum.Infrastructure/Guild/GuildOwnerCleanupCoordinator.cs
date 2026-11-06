using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildCleanupOwner(int Kind, Guid Id, long? EventSequence);

/// <summary>Every Guild table participates in one family completion transaction.</summary>
internal interface IGuildOwnerCleanupParticipant
{
    ValueTask<Result> PrepareSealAsync(GuildCleanupOwner owner, SqliteConnection connection, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(Result.Success());
    }

    ValueTask<Result> SealAsync(GuildCleanupOwner owner, SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken);

    ValueTask<Result> ReconcileAsync(GuildCleanupOwner owner, CancellationToken cancellationToken);

    ValueTask<Result> DeleteAsync(GuildCleanupOwner owner, SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken);
}

internal sealed record GuildCleanupOutcome(int OwnersCleaned, long AppliedCampaignSequence, long AppliedSessionSequence);

/// <summary>
/// The sole Guild owner-deletion consumer. Protected erasure owns separate transactions; every
/// ordinary participant and the family cursor commit together only after erasure has cleared.
/// </summary>
internal sealed class GuildOwnerCleanupCoordinator(
    IGrimoireOrdinaryConnectionFactory connections,
    IGrimoireConnectionAdmissionGate admission,
    ICovenantSensitiveArtifactPurger purger,
    ICovenantLabeledArtifactTransactionGuard labels,
    IReadOnlyList<IGuildOwnerCleanupParticipant> participants,
    TimeProvider clock)
{
    internal async Task<Result<GuildCleanupOutcome>> RunBatchAsync(int maxEvents, int maxOrphans, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEvents, 1);

        ArgumentOutOfRangeException.ThrowIfLessThan(maxOrphans, 1);

        if (!admission.TryAcquireWorkLease(GrimoireWorkKind.GuildMaintenance, out IGrimoireWorkLease? acquired) || acquired is null)
        {
            return MaintenanceDeferred();
        }

        await using IGrimoireWorkLease work = acquired;

        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, work.MaintenanceRevocation);

        if (!work.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? group) || group is null)
        {
            return MaintenanceDeferred();
        }

        await using IGrimoireExternalEffectGroup effects = group;

        Result<IGrimoireOrdinaryConnectionLease> opened = await connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, lifetime.Token).ConfigureAwait(false);

        if (opened.IsFailure)
        {
            return opened.Error;
        }

        await using IGrimoireOrdinaryConnectionLease held = opened.Value;

        SqliteConnection connection = held.Connection;

        try
        {
            Result available = await GuildStorageAvailability.CheckAsync(connection, null, lifetime.Token).ConfigureAwait(false);

            if (available.IsFailure)
            {
                return available.Error;
            }

            Result materialized = await MaterializeAsync(connection, maxEvents, maxOrphans, lifetime.Token).ConfigureAwait(false);

            if (materialized.IsFailure)
            {
                return materialized.Error;
            }

            List<GuildCleanupOwner> owners = await ReadWorkAsync(connection, maxEvents + maxOrphans, lifetime.Token).ConfigureAwait(false);

            int cleaned = 0;

            foreach (GuildCleanupOwner owner in owners)
            {
                lifetime.Token.ThrowIfCancellationRequested();

                foreach (IGuildOwnerCleanupParticipant participant in participants)
                {
                    Result prepared = await participant.PrepareSealAsync(owner, connection, lifetime.Token).ConfigureAwait(false);

                    if (prepared.IsFailure)
                    {
                        return prepared.Error;
                    }
                }

                Result sealedOwner = await SealAsync(connection, owner, lifetime.Token).ConfigureAwait(false);

                if (sealedOwner.IsFailure)
                {
                    return sealedOwner.Error;
                }

                foreach (IGuildOwnerCleanupParticipant participant in participants)
                {
                    Result reconciled = await participant.ReconcileAsync(owner, lifetime.Token).ConfigureAwait(false);

                    if (reconciled.IsFailure)
                    {
                        return reconciled.Error;
                    }
                }

                Result purged = await PurgeAsync(connection, owner, lifetime.Token).ConfigureAwait(false);

                if (purged.IsFailure)
                {
                    return purged.Error;
                }

                Result finalized = await FinalizeAsync(connection, owner, lifetime.Token).ConfigureAwait(false);

                if (finalized.IsFailure)
                {
                    return finalized.Error;
                }

                cleaned++;
            }

            (long campaign, long session) = await ReadCursorAsync(connection, null, lifetime.Token).ConfigureAwait(false);

            return new GuildCleanupOutcome(cleaned, campaign, session);
        }
        catch (SqliteException)
        {
            return Deferred();
        }
    }

    private async Task<Result> MaterializeAsync(SqliteConnection connection, int maxEvents, int maxOrphans, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        (long campaign, long session) = await ReadCursorAsync(connection, transaction, ct).ConfigureAwait(false);

        if (campaign < 0 || session < 0)
        {
            return Deferred();
        }

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO guild_owner_cleanup(OwnerKindCode,OwnerId,EventSequence,PhaseCode,CreatedAtUtc,UpdatedAtUtc)
            SELECT OwnerKindCode,OwnerId,Sequence,0,$now,$now FROM owner_deletion_events
            WHERE (OwnerKindCode=1 AND Sequence>$campaign) OR (OwnerKindCode=2 AND Sequence>$session)
            ORDER BY Sequence LIMIT $limit
            ON CONFLICT(OwnerKindCode,OwnerId) DO UPDATE SET
                EventSequence=MAX(COALESCE(guild_owner_cleanup.EventSequence,0),excluded.EventSequence),UpdatedAtUtc=excluded.UpdatedAtUtc;
            """;

        command.Parameters.AddWithValue("$now", UtcInstantText.Format(clock.GetUtcNow()));

        command.Parameters.AddWithValue("$campaign", campaign);

        command.Parameters.AddWithValue("$session", session);

        command.Parameters.AddWithValue("$limit", maxEvents);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await using SqliteCommand orphans = connection.CreateCommand();

        orphans.Transaction = transaction;

        orphans.CommandText = """
            INSERT INTO guild_owner_cleanup(OwnerKindCode,OwnerId,EventSequence,PhaseCode,CreatedAtUtc,UpdatedAtUtc)
            SELECT orphan.Kind,orphan.Id,NULL,0,$now,$now FROM (
                SELECT 1 Kind,CampaignId Id FROM guild_content WHERE CampaignId IS NOT NULL
                UNION SELECT 2,SessionId FROM guild_content WHERE SessionId IS NOT NULL
                UNION SELECT 1,CampaignId FROM guild_agent_cards WHERE CampaignId IS NOT NULL
                UNION SELECT 2,SessionId FROM guild_agent_cards WHERE SessionId IS NOT NULL
                UNION SELECT 1,CampaignId FROM guild_content_erasure_receipts WHERE CampaignId IS NOT NULL
                UNION SELECT 2,SessionId FROM guild_content_erasure_receipts WHERE SessionId IS NOT NULL
                UNION SELECT 1,WorkspaceId FROM guild_declared_roots WHERE WorkspaceId IS NOT NULL
                UNION SELECT 1,CampaignId FROM guild_signets WHERE CampaignId IS NOT NULL AND (Revoked=0 OR CancellationRequested=0)
                UNION SELECT 2,SessionId FROM guild_signets WHERE SessionId IS NOT NULL AND (Revoked=0 OR CancellationRequested=0)
                UNION SELECT 1,WorkspaceId FROM guild_signets WHERE WorkspaceId IS NOT NULL AND (Revoked=0 OR CancellationRequested=0)
                UNION SELECT 1,CampaignId FROM guild_quests WHERE CampaignId IS NOT NULL AND (Retired=0 OR CancellationRequested=0)
                UNION SELECT 2,SessionId FROM guild_quests WHERE SessionId IS NOT NULL AND (Retired=0 OR CancellationRequested=0)
                UNION SELECT 1,WorkspaceId FROM guild_quests WHERE WorkspaceId IS NOT NULL AND (Retired=0 OR CancellationRequested=0)
            ) orphan
            WHERE ((Kind=1 AND NOT EXISTS(SELECT 1 FROM Campaigns owner WHERE owner.Id=orphan.Id COLLATE NOCASE))
                OR (Kind=2 AND NOT EXISTS(SELECT 1 FROM Sessions owner WHERE owner.Id=orphan.Id COLLATE NOCASE)))
                AND NOT EXISTS(SELECT 1 FROM guild_owner_cleanup work WHERE work.OwnerKindCode=orphan.Kind AND work.OwnerId=orphan.Id COLLATE NOCASE)
            ORDER BY orphan.Kind,orphan.Id LIMIT $limit
            ON CONFLICT(OwnerKindCode,OwnerId) DO NOTHING;
            """;

        orphans.Parameters.AddWithValue("$now", UtcInstantText.Format(clock.GetUtcNow()));

        orphans.Parameters.AddWithValue("$limit", maxOrphans);

        int found = await orphans.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await using SqliteCommand sweep = connection.CreateCommand();

        sweep.Transaction = transaction;

        sweep.CommandText = "UPDATE capability_cleanup_state SET FullSweepRequired=$more,UpdatedAtUtc=$now WHERE CapabilityFamilyCode=2;";

        sweep.Parameters.AddWithValue("$more", found == maxOrphans ? 1 : 0);

        sweep.Parameters.AddWithValue("$now", UtcInstantText.Format(clock.GetUtcNow()));

        await sweep.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return Result.Success();
    }

    private static async Task<List<GuildCleanupOwner>> ReadWorkAsync(SqliteConnection connection, int limit, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT OwnerKindCode,OwnerId,EventSequence FROM guild_owner_cleanup WHERE PhaseCode<6 ORDER BY EventSequence IS NULL,EventSequence,OwnerKindCode,OwnerId LIMIT $limit;";

        command.Parameters.AddWithValue("$limit", limit);

        List<GuildCleanupOwner> owners = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            owners.Add(new(reader.GetInt32(0), Guid.Parse(reader.GetString(1)), reader.IsDBNull(2) ? null : reader.GetInt64(2)));
        }

        return owners;
    }

    private async Task<Result> SealAsync(SqliteConnection connection, GuildCleanupOwner owner, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        Result available = await GuildStorageAvailability.CheckAsync(connection, transaction, ct).ConfigureAwait(false);

        if (available.IsFailure)
        {
            return available.Error;
        }

        foreach (IGuildOwnerCleanupParticipant participant in participants)
        {
            Result sealedOwner = await participant.SealAsync(owner, connection, transaction, ct).ConfigureAwait(false);

            if (sealedOwner.IsFailure)
            {
                return sealedOwner.Error;
            }
        }

        await using SqliteCommand command = OwnerCommand(connection, transaction, owner);

        command.CommandText = "UPDATE guild_owner_cleanup SET PhaseCode=MAX(PhaseCode,1),UpdatedAtUtc=$now WHERE OwnerKindCode=$kind AND OwnerId=$owner COLLATE NOCASE;";

        command.Parameters.AddWithValue("$now", UtcInstantText.Format(clock.GetUtcNow()));

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return Result.Success();
    }

    private async Task<Result> PurgeAsync(SqliteConnection connection, GuildCleanupOwner owner, CancellationToken ct)
    {
        string after = string.Empty;

        while (true)
        {
            Guid[] ids = await ReadContentAsync(connection, null, owner, after, ct).ConfigureAwait(false);

            if (ids.Length == 0)
            {
                return Result.Success();
            }

            CovenantSensitivePurgeTarget[] targets = [.. ids.Select(id => new CovenantSensitivePurgeTarget(SensitiveArtifactKind.GuildContent, id))];

            Result<CovenantSensitivePurgeOutcome> purged = await purger.PurgeAsync(targets, ct).ConfigureAwait(false);

            if (purged.IsFailure)
            {
                return purged.Error;
            }

            if (purged.Value.Results.Count != ids.Length
                || purged.Value.Results.Select(result => result.ArtifactId).Distinct().Count() != ids.Length
                || purged.Value.Results.Any(result => result.Kind != SensitiveArtifactKind.GuildContent || !ids.Contains(result.ArtifactId)
                    || result.Disposition is not CovenantSensitivePurgeDisposition.Unlabeled and not CovenantSensitivePurgeDisposition.Purged))
            {
                return Deferred();
            }

            foreach (CovenantSensitivePurgeResult result in purged.Value.Results.Where(result => result.Disposition == CovenantSensitivePurgeDisposition.Purged))
            {
                await using SqliteCommand check = connection.CreateCommand();

                check.CommandText = "SELECT COUNT(*) FROM guild_content WHERE ContentId=$id COLLATE NOCASE;";

                check.Parameters.AddWithValue("$id", result.ArtifactId.ToString("D").ToUpperInvariant());

                if (await check.ExecuteScalarAsync(ct).ConfigureAwait(false) is not 0L)
                {
                    return Deferred();
                }
            }

            after = ids[^1].ToString("D").ToUpperInvariant();
        }
    }

    private async Task<Result> FinalizeAsync(SqliteConnection connection, GuildCleanupOwner owner, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        Result available = await GuildStorageAvailability.CheckAsync(connection, transaction, ct).ConfigureAwait(false);

        if (available.IsFailure)
        {
            return available.Error;
        }

        // Recheck labels after the purger, under the writer lock that will delete the rows. A newly
        // labelled revision never inherits an earlier Unlabeled disposition.
        string after = string.Empty;

        while (true)
        {
            Guid[] ids = await ReadContentAsync(connection, transaction, owner, after, ct).ConfigureAwait(false);

            if (ids.Length == 0)
            {
                break;
            }

            Result unlabelled = await labels.EnsureAllUnlabeledAsync(SensitiveArtifactKind.GuildContent, ids,
                connection, transaction, ct).ConfigureAwait(false);

            if (unlabelled.IsFailure)
            {
                return unlabelled.Error;
            }

            foreach (Guid id in ids)
            {
                await GuildContentLifecycle.PrepareOrdinaryPurgeAsync(connection, transaction, id.ToString("N"), ct).ConfigureAwait(false);
            }

            after = ids[^1].ToString("D").ToUpperInvariant();
        }

        await using (SqliteCommand protectedBody = OwnerCommand(connection, transaction, owner))
        {
            protectedBody.CommandText = $"SELECT COUNT(*) FROM guild_content WHERE {(owner.Kind == 1 ? "CampaignId" : "SessionId")}=$owner COLLATE NOCASE AND SensitivityCode<>0;";

            if (await protectedBody.ExecuteScalarAsync(ct).ConfigureAwait(false) is not 0L)
            {
                return Deferred();
            }
        }

        foreach (IGuildOwnerCleanupParticipant participant in participants)
        {
            Result deleted = await participant.DeleteAsync(owner, connection, transaction, ct).ConfigureAwait(false);

            if (deleted.IsFailure)
            {
                return deleted.Error;
            }
        }

        await using SqliteCommand command = OwnerCommand(connection, transaction, owner);

        string column = owner.Kind == 1 ? "CampaignId" : "SessionId";

        command.CommandText = $"""
            DELETE FROM guild_content WHERE {column}=$owner COLLATE NOCASE;
            DELETE FROM guild_content_erasure_receipts WHERE {column}=$owner COLLATE NOCASE
                AND NOT ({GuildQuestRetentionPins.ContentReferences});
            UPDATE guild_owner_cleanup SET PhaseCode=6,UpdatedAtUtc=$now WHERE OwnerKindCode=$kind AND OwnerId=$owner COLLATE NOCASE;
            """;

        command.Parameters.AddWithValue("$now", UtcInstantText.Format(clock.GetUtcNow()));

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        if (owner.EventSequence is { } sequence)
        {
            await using SqliteCommand evidence = OwnerCommand(connection, transaction, owner);

            evidence.CommandText = "SELECT COUNT(*) FROM owner_deletion_events WHERE Sequence=$sequence AND OwnerKindCode=$kind AND OwnerId=$owner COLLATE NOCASE;";

            evidence.Parameters.AddWithValue("$sequence", sequence);

            if (await evidence.ExecuteScalarAsync(ct).ConfigureAwait(false) is not 1L)
            {
                return Deferred();
            }

            await using SqliteCommand cursor = connection.CreateCommand();

            cursor.Transaction = transaction;

            cursor.CommandText = $"UPDATE capability_cleanup_state SET {(owner.Kind == 1 ? "AppliedCampaignSequence" : "AppliedSessionSequence")}=MAX({(owner.Kind == 1 ? "AppliedCampaignSequence" : "AppliedSessionSequence")},$sequence),UpdatedAtUtc=$now WHERE CapabilityFamilyCode=2;";

            cursor.Parameters.AddWithValue("$sequence", sequence);

            cursor.Parameters.AddWithValue("$now", UtcInstantText.Format(clock.GetUtcNow()));

            if (await cursor.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                return Deferred();
            }
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return Result.Success();
    }

    private static async Task<Guid[]> ReadContentAsync(SqliteConnection connection, SqliteTransaction? transaction,
        GuildCleanupOwner owner, string after, CancellationToken ct)
    {
        await using SqliteCommand command = OwnerCommand(connection, transaction, owner);

        string column = owner.Kind == 1 ? "CampaignId" : "SessionId";

        command.CommandText = $"""
            SELECT Id FROM (
                SELECT ContentId Id FROM guild_content WHERE {column}=$owner COLLATE NOCASE
                UNION SELECT ArtifactId FROM artifact_sensitivity WHERE ArtifactKindCode=17 AND {column}=$owner COLLATE NOCASE
            ) WHERE Id>$after ORDER BY Id LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$after", after);

        command.Parameters.AddWithValue("$limit", ICovenantSensitiveArtifactPurger.MaxTargets);

        List<Guid> ids = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            ids.Add(Guid.Parse(reader.GetString(0)));
        }

        return [.. ids];
    }

    private static async Task<(long Campaign, long Session)> ReadCursorAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "SELECT AppliedCampaignSequence,AppliedSessionSequence FROM capability_cleanup_state WHERE CapabilityFamilyCode=2;";

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return (-1, -1);
        }

        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static SqliteCommand OwnerCommand(SqliteConnection connection, SqliteTransaction? transaction, GuildCleanupOwner owner)
    {
        SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.Parameters.AddWithValue("$owner", owner.Id.ToString("D").ToUpperInvariant());

        command.Parameters.AddWithValue("$kind", owner.Kind);

        return command;
    }

    private static Error MaintenanceDeferred() => new(ErrorCodes.Guild.MaintenanceDeferred, "Guild owner cleanup is deferred while ordinary work or effect admission is closed.");

    private static Error Deferred() => new(ErrorCodes.Guild.Unavailable, "Guild owner cleanup is deferred; its family cursor has not advanced past unfinished work.");
}
