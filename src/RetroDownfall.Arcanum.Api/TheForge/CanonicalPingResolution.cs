using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Api.TheForge;

/// <summary>
/// One turn's canonical Campaign authority, plus the request the workspace boundary will run under.
/// </summary>
/// <remarks>
/// Two values rather than one because they answer different questions.
/// <see cref="Campaign"/> is authority: it is derived from registered physical identity and is what
/// memory scope, tool exposure, and commit are bound to. <see cref="Request"/> still carries a working
/// directory because the existing workspace-containment boundary resolves tool targets from it, and
/// that boundary is unchanged by this slice (§10.12).
/// </remarks>
internal sealed record CanonicalPingTurn(
    PingRequest Request,
    CanonicalCampaignContext Campaign);

/// <summary>
/// Resolves canonical Campaign authority for a ping-shaped request at an HTTP seam.
/// </summary>
/// <remarks>
/// Replaces <c>PingRequestResolver</c>, which answered a strictly weaker question. That helper returned
/// as soon as any source produced a directory, so a request naming Campaign C while supplying a path
/// inside Campaign D was accepted, and which Campaign won depended on which field happened to be
/// populated. Here every supplied source is verified against every other one, and a disagreement is a
/// typed conflict.
///
/// <para>The working-directory fill from the old helper is preserved verbatim: when a request names a
/// Campaign and supplies no directory, the Campaign's recorded path is filled in. That behaviour feeds
/// workspace containment, not authority, so changing it here would alter tool reach for reasons that
/// have nothing to do with Campaign binding.</para>
/// </remarks>
internal static class CanonicalPingResolution
{

    /// <summary>
    /// Resolves authority and fills the workspace directory, or returns the first typed failure.
    /// </summary>
    internal static async Task<Result<CanonicalPingTurn>> ResolveAsync(
        PingRequest request,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(request);

        ArgumentNullException.ThrowIfNull(services);

        ICanonicalCampaignContextResolver resolver =
            services.GetRequiredService<ICanonicalCampaignContextResolver>();

        Result<CanonicalCampaignContext> campaign = await resolver
            .ResolveAsync(
                new CanonicalCampaignResolutionRequest(
                    request.SessionId,
                    request.CampaignId,
                    string.IsNullOrWhiteSpace(request.WorkingDirectory) ? null : request.WorkingDirectory),
                cancellationToken)
            .ConfigureAwait(false);

        if (campaign.IsFailure)
        {
            return campaign.Error;
        }

        Result<PingRequest> filled = await FillWorkingDirectoryAsync(
            request,
            services,
            cancellationToken).ConfigureAwait(false);

        return filled.IsFailure
            ? filled.Error
            : Result<CanonicalPingTurn>.Success(new CanonicalPingTurn(filled.Value, campaign.Value));

    }

    private static Task<Result<PingRequest>> FillWorkingDirectoryAsync(
        PingRequest request,
        IServiceProvider services,
        CancellationToken cancellationToken) =>
        CampaignWorkspaceFill.ApplyAsync(
            request,
            services.GetRequiredService<ICampaignRepository>(),
            cancellationToken);

}

/// <summary>
/// Fills a request's workspace directory from the Campaign it named, and nothing else.
/// </summary>
/// <remarks>
/// Extracted from the deleted <c>PingRequestResolver</c> so the one piece of that helper worth keeping
/// survives without its authority-shaped name. This decides where workspace tools may reach, not which
/// Campaign a turn belongs to; conflating the two is exactly what the canonical resolver replaced
/// (§10.12).
///
/// <para>Available to unattended internal callers, which have no Campaign authority to resolve but still
/// need the recorded path to run a tool in the right directory.</para>
/// </remarks>
internal static class CampaignWorkspaceFill
{

    internal static async Task<Result<PingRequest>> ApplyAsync(
        PingRequest request,
        ICampaignRepository campaigns,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(request);

        ArgumentNullException.ThrowIfNull(campaigns);

        if (request.CampaignId is not Guid campaignId)
        {
            return Result<PingRequest>.Success(request);
        }

        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            return Result<PingRequest>.Success(request);
        }

        Campaign? campaign = await campaigns
            .GetByIdAsync(campaignId, cancellationToken)
            .ConfigureAwait(false);

        return campaign is null
            ? Result<PingRequest>.Failure(
                new Error(ErrorCodes.Campaign.NotFound, "No campaign exists with that identifier."))
            : Result<PingRequest>.Success(request with { WorkingDirectory = campaign.Path });

    }

}
