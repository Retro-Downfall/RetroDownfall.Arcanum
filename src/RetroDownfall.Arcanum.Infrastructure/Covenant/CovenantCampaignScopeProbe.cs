using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Covenant;

/// <summary>
/// Answers the operation gate's one question about a Campaign: is this identity still live?
/// </summary>
/// <remarks>
/// The gate is a process-wide singleton and the Grimoire context is scoped, so this probe opens its
/// own scope per call. That is affordable because the probe is only ever consulted on the cold
/// exclusive-acquisition path: no ordinary read, write, turn, or accelerator lease touches it.
///
/// <para>The distinction between <see cref="CovenantCampaignScopeState.Deleted"/> and
/// <see cref="CovenantCampaignScopeState.Unknown"/> is deliberate. A Campaign the core deletion
/// journal has an event for was really deleted; one that was simply never registered may be a typo
/// or a stale client, and the two deserve different diagnoses even though both refuse the
/// acquisition.</para>
/// </remarks>
internal sealed class CovenantCampaignScopeProbe(IServiceScopeFactory scopeFactory)
    : ICovenantCampaignScopeProbe
{

    private const int CampaignOwnerKindCode = 1;

    public async ValueTask<Result<CovenantCampaignScopeState>> ResolveAsync(
        Guid campaignId,
        CancellationToken cancellationToken)
    {

        if (campaignId == Guid.Empty)
        {

            return new Error(ErrorCodes.Covenant.InvalidScope, "A Campaign scope requires a nonempty Campaign identity.");

        }

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        bool live = await db.Campaigns
            .AsNoTracking()
            .AnyAsync(campaign => campaign.Id == campaignId, cancellationToken)
            .ConfigureAwait(false);

        if (live)
        {

            return CovenantCampaignScopeState.Live;

        }

        bool deleted = await HasDeletionEventAsync(db, campaignId, cancellationToken).ConfigureAwait(false);

        return deleted
            ? CovenantCampaignScopeState.Deleted
            : CovenantCampaignScopeState.Unknown;

    }

    private static async Task<bool> HasDeletionEventAsync(
        ArcanumDbContext db,
        Guid campaignId,
        CancellationToken cancellationToken)
    {

        await using System.Data.Common.DbCommand command = db.Database.GetDbConnection().CreateCommand();

        command.CommandText = """
            SELECT 1
            FROM owner_deletion_events
            WHERE OwnerKindCode = $kind AND OwnerId = $owner
            LIMIT 1;
            """;

        AddParameter(command, "$kind", CampaignOwnerKindCode);

        AddParameter(command, "$owner", campaignId.ToString("D"));

        if (command.Connection!.State != System.Data.ConnectionState.Open)
        {

            await command.Connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        }

        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return result is not null and not DBNull;

    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {

        System.Data.Common.DbParameter parameter = command.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value;

        _ = command.Parameters.Add(parameter);

    }

}
