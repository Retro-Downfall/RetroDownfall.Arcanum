using System.Data;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

/// <summary>Reads independent live process and canonical facts; no retained claim supplies lineage.</summary>
internal sealed partial class LongRestLiveFenceReader(ArcanumDbContext db, ICovenantRuntimeGenerationProvider runtime,
    IGrimoireConnectionAdmissionGate admission, ILongRestConfigurationProvider configuration,
    ICampaignAvailabilityReader campaigns, ICampaignPathIdentityReader paths)
{
    internal async Task<Result<LongRestSweepFence>> CaptureAsync(CanonicalCampaignContext scope,
        LongRestConfigurationStamp stamp, long? retainedHighwater, CancellationToken ct)
    {
        try
        {
            LongRestSweepConfiguration options = configuration.Capture();

            CovenantRuntimeGenerationState live = runtime.Current;

            long admitted = admission.CurrentGeneration;

            if (!options.Enabled || options.Recipe.IsFailure || options.Stamp != stamp || !configuration.IsCurrent(stamp)
                || live.AuthorityRetired || live.Availability.Canonical != CovenantCapabilityState.Healthy
                || live.Availability.DatasetGeneration is not { } dataset || dataset == Guid.Empty || admitted <= 0)
            {
                return Unavailable();
            }

            if (scope.IsCampaignBound)
            {
                Guid campaign = scope.CampaignId!.Value;

                Result<long?> available = await campaigns.FindAvailabilityGenerationAsync(campaign, ct).ConfigureAwait(false);

                Result<RegisteredCampaignIdentity?> root = await paths.FindByCampaignAsync(campaign, ct).ConfigureAwait(false);

                if (available.IsFailure || available.Value is null || root.IsFailure
                    || available.Value != scope.CampaignAvailabilityGeneration
                    || (root.Value?.PolicyVersion ?? CampaignPathIdentityPolicy.Version) != scope.PathIdentityPolicyVersion
                    || root.Value?.Revision != scope.PathIdentityRevision || root.Value?.PhysicalIdentityDigest != scope.RootIdentityDigest
                    || root.Value is { } identity && identity.CampaignId != campaign)
                {
                    return Unavailable();
                }
            }

            SqliteConnection connection = (SqliteConnection)db.Database.GetDbConnection();

            if (connection.State != ConnectionState.Open)
            {
                await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
            }

            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = "SELECT COALESCE(max(InsertionSequence), 0) FROM long_rest_sources;";

            long currentHighwater = (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!;

            long highwater = retainedHighwater ?? currentHighwater;

            if (highwater < 0 || highwater > currentHighwater || !ReferenceEquals(live, runtime.Current)
                || admitted != admission.CurrentGeneration || !configuration.IsCurrent(stamp))
            {
                return Unavailable();
            }

            return new LongRestSweepFence(scope, highwater, dataset, admitted, live.RuntimeAuthorityGeneration,
                live.Availability.Generation, stamp);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or SqliteException or OverflowException)
        {
            return Unavailable();
        }
    }

    private static Error Unavailable() => new(ErrorCodes.LongRest.Unavailable, "The current Long Rest dataset, scope or configuration could not be proved.");
}
