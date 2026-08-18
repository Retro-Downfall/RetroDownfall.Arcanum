using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Builds the one <see cref="ArcanumInvocationContext"/> an authenticated request carries.
/// </summary>
/// <remarks>
/// Only reachable from an endpoint holding a live <see cref="HttpContext"/>, because that is the only
/// place the master API key has actually been presented. Everything downstream receives the finished
/// value; nothing downstream can build one with authority (§10.12).
///
/// <para>The read epoch comes from <see cref="IOperatorAuthorityContextIssuer"/>, which reads the
/// published authority snapshot and refuses a tainted or unestablished installation. A refusal is not
/// an error here: it produces a context with no epoch, which reads as "this turn carries no Covenant
/// authority" and lets ordinary inference continue exactly as it does today.</para>
///
/// <para>Attendance and tool policy are read from the request rather than assumed. An unattended turn
/// cannot answer an interactive Ward, and the retirement arm of the Covenant mutation surface is a
/// Forbidden Art, so attendance is part of what decides whether that surface exists at all.</para>
/// </remarks>
internal static class ArcanumInvocationContexts
{

    /// <summary>
    /// The context for a session-backed operator turn: the only surface that may stage a mutation.
    /// </summary>
    internal static ArcanumInvocationContext ForSessionTurn(
        HttpContext httpContext,
        PingRequest request,
        CanonicalCampaignContext? campaign = null) =>
        Build(httpContext, ArcanumExecutionSurface.SessionBackedOperatorTurn, request, campaign);

    /// <summary>
    /// The context for a stateless operator turn. It may consume eligible context and exposes no
    /// mutation tool, because it has no durable assistant entry to publish one against.
    /// </summary>
    internal static ArcanumInvocationContext ForStatelessTurn(
        HttpContext httpContext,
        PingRequest request,
        CanonicalCampaignContext? campaign = null) =>
        Build(httpContext, ArcanumExecutionSurface.StatelessOperatorTurn, request, campaign);

    /// <summary>
    /// The context for an authenticated preview or inspection. It builds a plan and dispatches nothing.
    /// </summary>
    internal static ArcanumInvocationContext ForInspection(
        HttpContext httpContext,
        bool unattended = true,
        CanonicalCampaignContext? campaign = null) =>
        Create(
            httpContext,
            ArcanumExecutionSurface.ContextInspection,
            unattended ? InvocationAttendance.Unattended : InvocationAttendance.Attended,
            ToolPolicy.NoTools,
            campaign);

    /// <summary>
    /// Chooses the surface from whether the request is session-backed, then builds it.
    /// </summary>
    internal static ArcanumInvocationContext ForTurn(
        HttpContext httpContext,
        PingRequest request,
        CanonicalCampaignContext? campaign = null) =>
        request.SessionId is null
            ? ForStatelessTurn(httpContext, request, campaign)
            : ForSessionTurn(httpContext, request, campaign);

    private static ArcanumInvocationContext Build(
        HttpContext httpContext,
        ArcanumExecutionSurface surface,
        PingRequest request,
        CanonicalCampaignContext? campaign)
    {

        ArgumentNullException.ThrowIfNull(request);

        return Create(
            httpContext,
            surface,
            request.UnattendedMode ? InvocationAttendance.Unattended : InvocationAttendance.Attended,
            ResolveToolPolicy(request),
            campaign);

    }

    private static ArcanumInvocationContext Create(
        HttpContext httpContext,
        ArcanumExecutionSurface surface,
        InvocationAttendance attendance,
        ToolPolicy toolPolicy,
        CanonicalCampaignContext? campaign)
    {

        ArgumentNullException.ThrowIfNull(httpContext);

        Result<ArcanumInvocationContext> created = ArcanumInvocationContext.Create(
            surface,
            campaign,
            attendance,
            ResolveContextPolicy(httpContext),
            toolPolicy,
            ResolveReadAuthorityEpoch(httpContext));

        // Construction only fails when a surface was handed authority it may never carry, and every
        // surface here is an operator surface. Falling back to the authority-free context rather than
        // throwing keeps a composition mistake from becoming a 500 on the inference path.
        return created.IsSuccess
            ? created.Value
            : ArcanumInvocationContext.None;

    }

    private static CovenantContextPolicy ResolveContextPolicy(HttpContext httpContext) =>
        httpContext.Items.TryGetValue(NoContextRequestFeatureKey, out object? value) && value is true
            ? CovenantContextPolicy.None
            : CovenantContextPolicy.Default;

    private static CovenantReadAuthorityEpoch? ResolveReadAuthorityEpoch(HttpContext httpContext)
    {

        // Tolerate a partially composed context: unit tests drive these writers with a bare
        // DefaultHttpContext, and an absent issuer simply means this turn carries no Covenant
        // authority — the same answer a tainted installation gives.
        IOperatorAuthorityContextIssuer? issuer = httpContext.RequestServices
            ?.GetService<IOperatorAuthorityContextIssuer>();

        if (issuer is null)
        {
            return null;
        }

        Result<CovenantReadAuthorityEpoch> epoch = issuer.IssueReadEpoch();

        return epoch.IsSuccess
            ? epoch.Value
            : null;

    }

    /// <summary>
    /// The covenant-authority view of what this turn advertises. It errs closed on purpose: the only
    /// eligibility question downstream is <c>ToolPolicy is not NoTools</c>, so an unrecognized value
    /// would otherwise pass that gate. The wire converter refuses undefined values, which leaves
    /// in-process construction as the only way one can arrive — and an unknown restriction must never
    /// be read as permission.
    /// </summary>
    private static ToolPolicy ResolveToolPolicy(PingRequest request) =>
        request.DisableAllTools || request.DisableMcpTools
            ? ToolPolicy.NoTools
            : request.ToolPolicy switch
            {
                null => ToolPolicy.AllTools,
                ToolPolicy.AllTools
                    or ToolPolicy.NoTools
                    or ToolPolicy.ReadOnlyTools
                    or ToolPolicy.NoForbiddenArts => request.ToolPolicy.Value,
                _ => ToolPolicy.NoTools,
            };

    /// <summary>
    /// The irrevocable request marker set by the pre-binding no-context middleware.
    /// </summary>
    /// <remarks>
    /// Read here rather than re-derived from the body, because the wire signal is evaluated before body
    /// binding on purpose: no handler, retry, Prompt, Spell, preview, or provider adapter may re-enable
    /// Covenant for a request that arrived with <c>X-Arcanum-Context-Policy: none</c>. The middleware
    /// that sets it belongs to the public-surface slice; until then the marker is simply absent and
    /// every route takes its typed default.
    /// </remarks>
    internal const string NoContextRequestFeatureKey = "Arcanum.Covenant.NoContext";

}
