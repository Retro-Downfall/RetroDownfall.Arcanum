using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Api.Tower;

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
    CanonicalCampaignContext Campaign,
    bool UsesGlobalWorkspaceSelection = false);

/// <summary>
/// Resolves canonical Campaign authority for a ping-shaped request at an HTTP seam.
/// </summary>
/// <remarks>
/// Replaces <c>PingRequestResolver</c>, which answered a strictly weaker question. That helper returned
/// as soon as any source produced a directory, so a request naming Campaign C while supplying a path
/// inside Campaign D was accepted, and which Campaign won depended on which field happened to be
/// populated. The canonical resolver verifies the supplied sources. An authenticated native Guild turn
/// may independently select a workspace for an existing Global Session while retaining its Global binding.
///
/// <para>The working-directory fill from the old helper is preserved verbatim: when a request names a
/// Campaign and supplies no directory, the Campaign's recorded path is filled in. That behaviour feeds
/// workspace containment, not authority, so changing it here would alter tool reach for reasons that
/// have nothing to do with Campaign binding.</para>
/// </remarks>
internal static class CanonicalPingResolution
{
    private sealed record GlobalWorkspaceSelection(Guid SessionId, string Directory);

    internal static bool HasGlobalWorkspaceSelection(HttpContext context, PingRequest request) =>
        context.Features.Get<GlobalWorkspaceSelection>() is { } selection
        && request.SessionId == selection.SessionId && request.CampaignId is null
        && string.Equals(request.WorkingDirectory, selection.Directory, StringComparison.Ordinal);

    /// <summary>
    /// Resolves authority and fills the workspace directory, or returns the first typed failure. The answer is the resolver's alone: it never
    /// carries the Campaign's registered root and never reads the registry, so inspection and every caller that is not a turn keep exactly what
    /// the resolver says.
    /// </summary>
    internal static Task<Result<CanonicalPingTurn>> ResolveAsync(
        PingRequest request,
        IServiceProvider services,
        CancellationToken cancellationToken) =>
        ResolveCoreAsync(request, services, null, cancellationToken);

    /// <summary>
    /// Resolves a turn's authority at the HTTP seam (Long Rest-Scoped Enrichment). When the request is a Campaign turn that Long Rest prepares
    /// maintenance for, the Campaign additionally carries its registered root for the whole turn, so the invocation, the turn lease and the
    /// maintenance capability all agree with the fence the Long Rest request is admitted under. Every other request, and every request while
    /// Long Rest is off, gets <see cref="ResolveAsync"/>'s answer and reads no registry.
    /// </summary>
    internal static async Task<Result<CanonicalPingTurn>> ResolveTurnAsync(
        PingRequest request,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        Result<PingRequest> prepared = await SessionRecoveryRequests.PrepareAsync(request, httpContext, cancellationToken).ConfigureAwait(false);

        if (prepared.IsFailure) return prepared.Error;

        PingRequest effective = prepared.Value;

        ArcanumSettings? settings = httpContext.RequestServices.GetService<IOptionsMonitor<ArcanumSettings>>()?.CurrentValue;

        bool maySelectGlobalWorkspace = settings?.Features.Guild == true
            && effective is { SessionId: not null, CampaignId: null, DisableAllTools: false, DisableMcpTools: false, ForwardClientTools: false }
            && !InferenceContextBuilder.HasStatelessMessages(effective)
            && ApiKeyAuthenticator.HasAuthenticatedOperator(httpContext);

        Result<CanonicalPingTurn> resolved = await ResolveCoreAsync(
            effective,
            httpContext.RequestServices,
            ArcanumInvocationContexts.ResolveContextPolicy(httpContext),
            cancellationToken,
            maySelectGlobalWorkspace ? settings : null).ConfigureAwait(false);

        if (resolved.IsSuccess && resolved.Value.UsesGlobalWorkspaceSelection && effective.SessionId is { } sessionId)
        {
            httpContext.Features.Set(new GlobalWorkspaceSelection(sessionId, effective.WorkingDirectory));
        }

        return resolved;
    }

    private static async Task<Result<CanonicalPingTurn>> ResolveCoreAsync(
        PingRequest request,
        IServiceProvider services,
        CovenantContextPolicy? turnContextPolicy,
        CancellationToken cancellationToken,
        ArcanumSettings? globalWorkspaceSettings = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        ArgumentNullException.ThrowIfNull(services);

        ICanonicalCampaignContextResolver resolver =
            services.GetRequiredService<ICanonicalCampaignContextResolver>();

        CanonicalCampaignResolutionRequest selection = new(request.SessionId, request.CampaignId,
            string.IsNullOrWhiteSpace(request.WorkingDirectory) ? null : request.WorkingDirectory);

        Result<CanonicalCampaignContext> campaign = await resolver.ResolveAsync(selection, cancellationToken).ConfigureAwait(false);

        bool usesGlobalWorkspaceSelection = false;

        if (globalWorkspaceSettings is not null && campaign.IsFailure && campaign.Error.Code == ErrorCodes.Covenant.CampaignBindingConflict
            && Core.Configuration.CampaignPathPolicy.ValidateAndNormalizePath(request.WorkingDirectory, globalWorkspaceSettings).IsSuccess)
        {
            Result<CanonicalCampaignContext> binding = await resolver
                .ResolveAsync(selection with { WorkingDirectory = null }, cancellationToken).ConfigureAwait(false);

            if (binding.IsSuccess && binding.Value == CanonicalCampaignContext.GlobalOnly)
            {
                campaign = binding;

                usesGlobalWorkspaceSelection = true;
            }
        }

        if (campaign.IsFailure)
        {
            return campaign.Error;
        }

        CanonicalCampaignContext resolved = campaign.Value;

        // The turn boundary is where a Long Rest request begins: a request that Long Rest prepares maintenance for carries its Campaign's
        // registered root for the whole turn, so the invocation, the turn lease and the maintenance capability all agree with the fence.
        // Any other request, and every request with Long Rest off, keeps the resolver's answer and reads nothing.
        if (turnContextPolicy is { } contextPolicy
            && ArcanumInvocationContexts.IsLongRestTurn(
                services.GetService<IOptionsMonitor<ArcanumSettings>>()?.CurrentValue,
                request,
                resolved,
                contextPolicy))
        {
            Result<CanonicalCampaignContext> enriched = await services
                .GetRequiredService<ICanonicalCampaignRootEnricher>()
                .EnrichAsync(resolved, cancellationToken)
                .ConfigureAwait(false);

            if (enriched.IsFailure)
            {
                return enriched.Error;
            }

            resolved = enriched.Value;
        }

        Result<PingRequest> filled = await FillWorkingDirectoryAsync(
            request,
            services,
            cancellationToken).ConfigureAwait(false);

        return filled.IsFailure
            ? filled.Error
            : Result<CanonicalPingTurn>.Success(new CanonicalPingTurn(filled.Value, resolved, usesGlobalWorkspaceSelection));
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
