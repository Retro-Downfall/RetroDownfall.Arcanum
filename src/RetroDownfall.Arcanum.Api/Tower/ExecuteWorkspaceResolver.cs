using RetroDownfall.Arcanum.Api.Spells;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Api.Tower;

internal static class ExecuteWorkspaceResolver
{

    public static async Task<Result<string?>> ResolveAsync(
        string? queryWorkspace,
        string? bodyWorkspace,
        Guid? campaignId,
        SpellWorkspaceResolver workspaceResolver,
        ICampaignRepository campaignRepository,
        CancellationToken cancellationToken)
    {
        string? explicitWorkspace = !string.IsNullOrWhiteSpace(queryWorkspace)
            ? queryWorkspace
            : bodyWorkspace;

        if (!string.IsNullOrWhiteSpace(explicitWorkspace))
        {
            return workspaceResolver.Resolve(explicitWorkspace);
        }

        if (campaignId is Guid id)
        {
            Campaign? campaign = await campaignRepository
                .GetByIdAsync(id, cancellationToken)
                .ConfigureAwait(false);

            if (campaign is null)
            {
                return Result<string?>.Failure(
                    new Error(ErrorCodes.Campaign.NotFound, "No campaign exists with that identifier."));
            }

            return Result<string?>.Success(campaign.Path);
        }

        return workspaceResolver.Resolve(null);
    }

}
