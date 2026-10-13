using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

internal sealed partial class LongRestLiveFenceReader
{
    /// <summary>
    /// Attests that the presented lease is a live Long Rest lease and that every admission generation since
    /// <paramref name="previousGeneration"/> was a fully drained, non-destructive abort. Which work kinds may carry a
    /// clean sweep across such an abort is Long Rest policy, so it is decided here and never inside the generic gate.
    /// </summary>
    /// <remarks>
    /// The startup recovery path still runs a clean sweep under its own recovery lease, so that kind is accepted
    /// until recovery only parks the sweep and the hosted worker alone readmits it.
    /// </remarks>
    internal bool AttestCleanAdmission(IGrimoireWorkLease lease, long previousGeneration, long currentGeneration) =>
        lease.Kind is GrimoireWorkKind.LongRestSweep or GrimoireWorkKind.LongRunningOperationRecovery
        && admission.AttestDrainedMaintenance(lease, previousGeneration, currentGeneration);

    internal bool IsCleanRecipeCurrent(LongRestRecipeIdentity expected)
    {
        LongRestSweepConfiguration options = configuration.Capture();

        return options.Enabled && options.Recipe.IsSuccess && options.Recipe.Value == expected && configuration.IsCurrent(options.Stamp);
    }

    internal bool IsRequestFenceCurrent(LongRestSweepFence expected)
    {
        LongRestSweepConfiguration options = configuration.Capture();

        CovenantRuntimeGenerationState live = runtime.Current;

        return options.Enabled && options.Recipe.IsSuccess && options.Stamp == expected.Configuration
            && configuration.IsCurrent(expected.Configuration) && !live.AuthorityRetired
            && live.Availability.Canonical == CovenantCapabilityState.Healthy
            && live.Availability.DatasetGeneration == expected.DatasetGeneration
            && live.RuntimeAuthorityGeneration == expected.RuntimeAuthorityGeneration
            && live.Availability.Generation == expected.CapabilityGeneration
            && admission.CurrentGeneration == expected.AdmissionGeneration;
    }

    internal async Task<bool> ValidateRequestFenceWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestSweepFence expected, CancellationToken ct)
    {
        if (!IsRequestFenceCurrent(expected)) return false;

        CanonicalCampaignContext scope = expected.CanonicalScope;

        if (scope.IsCampaignBound)
        {
            await using SqliteCommand campaign = connection.CreateCommand();

            campaign.Transaction = transaction;

            campaign.CommandText = """
                SELECT state.RegistryEpoch, identity.PolicyVersion, identity.Revision, identity.PhysicalIdentityDigest
                FROM campaign_registry_state AS state
                LEFT JOIN campaign_path_identities AS identity ON identity.CampaignId = $campaign
                WHERE state.StateKey = 1 AND EXISTS (SELECT 1 FROM Campaigns WHERE Id = $campaign);
                """;

            campaign.Parameters.AddWithValue("$campaign", scope.CampaignId!.Value);

            await using SqliteDataReader reader = await campaign.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false)
                || reader.GetInt64(0) != scope.CampaignAvailabilityGeneration
                || (reader.IsDBNull(1) ? CampaignPathIdentityPolicy.Version : checked((uint)reader.GetInt64(1))) != scope.PathIdentityPolicyVersion
                || (reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2)) != scope.PathIdentityRevision
                || (reader.IsDBNull(3) ? (CovenantDigest?)null : new CovenantDigest((byte[])reader[3])) != scope.RootIdentityDigest) return false;
        }

        await using SqliteCommand highwater = connection.CreateCommand();

        highwater.Transaction = transaction;

        highwater.CommandText = "SELECT COALESCE(max(InsertionSequence), 0) FROM long_rest_sources;";

        long current = (long)(await highwater.ExecuteScalarAsync(ct).ConfigureAwait(false))!;

        return expected.InsertionHighWatermark >= 0 && expected.InsertionHighWatermark <= current && IsRequestFenceCurrent(expected);
    }
}
