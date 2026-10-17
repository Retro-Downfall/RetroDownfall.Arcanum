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
    /// <summary>
    /// The live insertion boundary: the highest sequence <c>long_rest_sources</c> has ever issued, not the highest one it still holds. The
    /// table is <c>AUTOINCREMENT</c>, which records that sequence in <c>sqlite_sequence</c> and never lowers it, so deleting the newest
    /// tracked memory anywhere (another scope, another partition) cannot put the live boundary below one a generation retained. Reading
    /// <c>max(InsertionSequence)</c> would, and a generation whose leaves were all untouched could no longer be proved against a boundary
    /// that fell. The larger of the two is read so a table whose counter was ever lost still never reads below its own rows.
    /// </summary>
    internal const string InsertionBoundarySql = """
        SELECT max(COALESCE((SELECT seq FROM sqlite_sequence WHERE name = 'long_rest_sources'), 0),
                   COALESCE((SELECT max(InsertionSequence) FROM long_rest_sources), 0));
        """;

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

            command.CommandText = InsertionBoundarySql;

            long currentHighwater = (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!;

            long highwater = retainedHighwater ?? currentHighwater;

            if (highwater < 0 || highwater > currentHighwater || !SameCapabilityFacts(live, runtime.Current)
                || admitted != admission.CurrentGeneration || !configuration.IsCurrent(stamp))
            {
                return Unavailable();
            }

            return new LongRestSweepFence(scope, highwater, dataset, admitted, live.RuntimeAuthorityGeneration,
                LongRestCapabilityState.Of(live.Availability), stamp);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or SqliteException or OverflowException)
        {
            return Unavailable();
        }
    }

    /// <summary>
    /// Whether a publication that landed while a capture was reading left every fact the capture records untouched. A republication
    /// (a Covenant write's new sequence, a maintenance pass that changed nothing) is not a reason to refuse the capture it interrupted.
    /// </summary>
    private static bool SameCapabilityFacts(CovenantRuntimeGenerationState captured, CovenantRuntimeGenerationState now) =>
        ReferenceEquals(captured, now)
        || !now.AuthorityRetired && now.RuntimeAuthorityGeneration == captured.RuntimeAuthorityGeneration
            && now.Availability.Canonical == CovenantCapabilityState.Healthy
            && now.Availability.DatasetGeneration == captured.Availability.DatasetGeneration
            && LongRestCapabilityState.Of(now.Availability) == LongRestCapabilityState.Of(captured.Availability);

    private static Error Unavailable() => new(ErrorCodes.LongRest.Unavailable, "The current Long Rest dataset, scope or configuration could not be proved.");
}
