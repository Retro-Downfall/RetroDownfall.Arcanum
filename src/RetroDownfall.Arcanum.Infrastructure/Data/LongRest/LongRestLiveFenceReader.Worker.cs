using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

internal sealed partial class LongRestLiveFenceReader
{
    internal async Task<Result<string>> ReadWorkerAsync(LongRestDiscoveryGeneration generation, string owner,
        DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(owner)) return Unavailable();

            SqliteConnection connection = await OpenAsync(ct).ConfigureAwait(false);

            return await ReadWorkerWithinAsync(connection, null, generation, owner, now, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or SqliteException or ArgumentException)
        {
            return Unavailable();
        }
    }

    internal static async Task<Result<string>> ReadWorkerWithinAsync(SqliteConnection connection, SqliteTransaction? transaction,
        LongRestDiscoveryGeneration generation, string owner, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(owner)) return Unavailable();

            await using SqliteCommand command = connection.CreateCommand();

            command.Transaction = transaction;

            command.CommandText = """
                SELECT operation.LeaseOwner, operation.LeaseExpiresAt, operation.CheckpointPayload,
                    sweep.GenerationId, sweep.PhaseCode, sweep.Revision, sweep.CommittedItemCursor, sweep.CommittedWorkDigest
                FROM LongRunningOperations operation JOIN long_rest_sweeps sweep ON sweep.OperationId = operation.Id
                WHERE operation.Id = $operation AND operation.Kind = 'long-rest-sweep' AND operation.State = 1
                  AND operation.LeaseOwner = $owner AND operation.LeaseExpiresAt IS NOT NULL
                  AND operation.CheckpointVersion = 1 AND length(operation.CheckpointPayload) BETWEEN 1 AND $maximum;
                """;

            command.Parameters.AddWithValue("$operation", generation.OperationId.ToString("N"));

            command.Parameters.AddWithValue("$owner", owner);

            command.Parameters.AddWithValue("$maximum", LongRestSweepCheckpointCodec.MaxCheckpointBytes);

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false)
                || owner != reader.GetString(0)
                || !DateTimeOffset.TryParse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset expiry)
                || expiry <= now)
            {
                return Unavailable();
            }

            Result<LongRestSweepCheckpoint> decoded = LongRestSweepCheckpointCodec.Decode(generation.OperationId, (byte[])reader[2]);

            if (decoded.IsFailure || decoded.Value.Scope != generation.Scope.Kind || decoded.Value.CampaignId != generation.Scope.CampaignId
                || decoded.Value.ProtectedPartitionDigest != generation.Scope.ProtectedPartitionDigest
                || decoded.Value.GenerationId != generation.GenerationId || reader.IsDBNull(3)
                || reader.GetString(3) != generation.GenerationId.ToString("D").ToUpperInvariant()
                || decoded.Value.Revision > reader.GetInt64(5))
            {
                return Unavailable();
            }

            LongRestSweepCheckpoint checkpoint = decoded.Value;

            long revision = reader.GetInt64(5);

            LongRestSweepPhase phase = (LongRestSweepPhase)reader.GetInt32(4);

            string? cursor = reader.IsDBNull(6) ? null : reader.GetString(6);

            CovenantDigest? digest = reader.IsDBNull(7) ? null : new CovenantDigest((byte[])reader[7]);

            if (checkpoint.Revision == revision && (checkpoint.Phase != phase
                || checkpoint.CommittedItemCursor != cursor || checkpoint.CommittedWorkDigest != digest)
                || checkpoint.Revision < revision && checkpoint.Phase > phase)
            {
                return Unavailable();
            }

            // The exact latest coordinate pair was proved above. Its digest can name a partial
            // group for the next anchor while the cursor still names the preceding completed
            // anchor. A lagging checkpoint must name its exact completed unit plus that anchor
            // or the immediately preceding completed anchor, with no pending intervening work.
            if (checkpoint.Revision < revision && (checkpoint.CommittedItemCursor is not null || checkpoint.CommittedWorkDigest is not null))
            {
                await using SqliteCommand committed = connection.CreateCommand();

                committed.Transaction = transaction;

                committed.CommandText = """
                    WITH units AS (
                        SELECT work.WorkDigest, work.DispositionCode, input.MemoryId AS Anchor
                        FROM long_rest_work_items work JOIN long_rest_work_inputs input
                          ON input.WorkItemId = work.WorkItemId AND input.Ordinal = 1
                        WHERE work.OperationId = $operation AND work.GenerationId = $generation AND work.KindCode = 3
                    )
                    SELECT count(*) FROM units completed
                    WHERE completed.WorkDigest = $digest AND completed.DispositionCode = 2
                      AND NOT EXISTS (SELECT 1 FROM units prior
                        WHERE prior.Anchor < completed.Anchor AND prior.DispositionCode <> 2)
                      AND (($cursor = completed.Anchor AND NOT EXISTS (SELECT 1 FROM units pending
                            WHERE pending.Anchor = completed.Anchor AND pending.DispositionCode <> 2))
                        OR ($cursor IS (SELECT max(prior.Anchor) FROM units prior WHERE prior.Anchor < completed.Anchor)
                          AND NOT EXISTS (SELECT 1 FROM units pending WHERE pending.Anchor = $cursor AND pending.DispositionCode <> 2)));
                    """;

                committed.Parameters.AddWithValue("$operation", generation.OperationId.ToString("N"));

                committed.Parameters.AddWithValue("$generation", generation.GenerationId.ToString("D").ToUpperInvariant());

                committed.Parameters.AddWithValue("$digest", (object?)checkpoint.CommittedWorkDigest?.Bytes ?? DBNull.Value);

                committed.Parameters.AddWithValue("$cursor", (object?)checkpoint.CommittedItemCursor ?? DBNull.Value);

                if ((long)(await committed.ExecuteScalarAsync(ct).ConfigureAwait(false))! == 0) return Unavailable();
            }

            return owner;
        }
        catch (Exception exception) when (exception is InvalidOperationException or SqliteException or ArgumentException)
        {
            return Unavailable();
        }
    }

    internal async Task<Result<CanonicalCampaignContext>> ResolveCanonicalAsync(LongRestSweepScope scope, CancellationToken ct)
    {
        if (!scope.IsInitialized) return Unavailable();

        if (scope.CampaignId is not { } campaign) return CanonicalCampaignContext.GlobalOnly;

        Result<long?> available = await campaigns.FindAvailabilityGenerationAsync(campaign, ct).ConfigureAwait(false);

        Result<RegisteredCampaignIdentity?> root = await paths.FindByCampaignAsync(campaign, ct).ConfigureAwait(false);

        if (available.IsFailure || available.Value is null || root.IsFailure || root.Value is { } identity && identity.CampaignId != campaign)
        {
            return Unavailable();
        }

        try
        {
            return CanonicalCampaignContext.Create(SessionCampaignBinding.ForCampaign(campaign), available.Value,
                root.Value?.PolicyVersion ?? CampaignPathIdentityPolicy.Version, root.Value?.Revision, root.Value?.PhysicalIdentityDigest);
        }
        catch (ArgumentException)
        {
            return Unavailable();
        }
    }
}
