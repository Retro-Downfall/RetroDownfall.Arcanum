using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Api.Security;

/// <summary>
/// How a route declares what Covenant authority it needs and whether the context-policy header means
/// anything on it.
/// </summary>
/// <remarks>
/// Declared as metadata rather than checked inside a handler, because the decision has to be made
/// before model binding. An endpoint filter runs after binding, so a handler-side check would already
/// have had the body read, buffered, and deserialized — which for the large-upload routes means
/// hundreds of megabytes spooled for a caller who was never going to be allowed to send them
/// (§10.18).
///
/// <para>Each helper attaches both halves: the metadata the pre-binding middleware reads and the
/// filter that rechecks the epoch as defence in depth. A route cannot carry one without the other.
/// </para>
/// </remarks>
internal static class CovenantEndpointConventionBuilderExtensions
{

    /// <summary>
    /// Declares that this route may inject Covenant content, so <c>X-Arcanum-Context-Policy</c> is a
    /// meaningful request header here.
    /// </summary>
    /// <remarks>
    /// Routes without this metadata reject the header rather than ignoring it. A caller that sent
    /// <c>none</c> to a route that silently discarded it believes it suppressed something it did not.
    /// </remarks>
    internal static TBuilder AllowCovenantContext<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {

        builder.WithMetadata(CovenantContextPolicyRequirementMetadata.Instance);

        return builder;

    }

    /// <summary>
    /// Declares a route that always reads Covenant content, keys, provenance, or Covenant-derived
    /// history.
    /// </summary>
    internal static TBuilder RequireCovenantReadAuthority<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireCovenantAuthority(CovenantAuthorityRequirement.ProtectedRead);

    /// <summary>
    /// Declares a route whose protected arm depends on the data, not the URL.
    /// </summary>
    /// <remarks>
    /// A Session detail is protected only when that Session is tainted. The route still has to declare
    /// something, because the inventory test proves that every content-bearing route made a decision —
    /// not that every route made the same one.
    /// </remarks>
    internal static TBuilder RequireConditionalCovenantReadAuthority<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {

        builder.WithMetadata(CovenantConditionalReadRequirementMetadata.Instance);

        return builder;

    }

    /// <summary>
    /// Declares a route that may delete a Covenant-labelled artifact.
    /// </summary>
    /// <remarks>
    /// Attaches both halves, like every other helper here: the metadata the pre-binding middleware
    /// reads so it can issue a retention-purge authority before the body is bound, and the filter that
    /// publishes the already-issued context into the request's purge scope. A route cannot carry one
    /// without the other, and nothing between them can substitute a context of its own — the value has
    /// no public constructor and the scope accepts exactly one publication (§10.20.2).
    /// </remarks>
    internal static TBuilder RequireConditionalSensitivityRetentionPurge<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {

        builder.WithMetadata(CovenantConditionalSensitivityPurgeMetadata.Instance);

        builder.AddEndpointFilter(new CovenantSensitivePurgeEndpointFilter());

        return builder;

    }

    /// <summary>
    /// Declares a route that mutates under one exact operator requirement.
    /// </summary>
    /// <remarks>
    /// <see cref="CovenantAuthorityRequirement.ProtectedRead"/> is rejected here on purpose. Read
    /// authority is the weakest of the six and is issued on far more routes; letting a mutation
    /// declare it would let a context minted for an inspection page authorize an erasure.
    /// </remarks>
    internal static TBuilder RequireCovenantOperatorAuthority<TBuilder>(
        this TBuilder builder,
        CovenantAuthorityRequirement requirement)
        where TBuilder : IEndpointConventionBuilder
    {

        if (requirement is CovenantAuthorityRequirement.ProtectedRead)
        {

            throw new ArgumentOutOfRangeException(
                nameof(requirement),
                "Operator authority must name a mutation requirement; ProtectedRead is not one.");

        }

        return builder.RequireCovenantAuthority(requirement);

    }

    private static TBuilder RequireCovenantAuthority<TBuilder>(
        this TBuilder builder,
        CovenantAuthorityRequirement requirement)
        where TBuilder : IEndpointConventionBuilder
    {

        if (requirement is < CovenantAuthorityRequirement.ProtectedRead
            or > CovenantAuthorityRequirement.SensitivityRetentionPurge)
        {

            throw new ArgumentOutOfRangeException(nameof(requirement));

        }

        builder.WithMetadata(new CovenantAuthorityRequirementMetadata(requirement));

        builder.AddEndpointFilter(new CovenantAuthorityEndpointFilter(requirement));

        return builder;

    }

}

/// <summary>
/// Publishes the retention-purge authority the pre-binding middleware issued into the request scope the
/// purger reads.
/// </summary>
/// <remarks>
/// A filter rather than a handler parameter, because the routes this runs on reach the purger through
/// three different repositories and a service, none of which has an <c>HttpContext</c>. Publishing here
/// keeps the transfer inside the authenticated boundary: the value crosses from the request pipeline to
/// the request's own DI scope and nothing on the way can forge, replace, or widen it.
///
/// <para>An absent feature is not an error. On an installation with no Covenant arm the middleware
/// issues nothing, the scope stays empty, and the purger reports every artifact unlabelled — which is
/// the truth there. The refusal belongs where a label is actually found.</para>
/// </remarks>
internal sealed class CovenantSensitivePurgeEndpointFilter : IEndpointFilter
{

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {

        ArgumentNullException.ThrowIfNull(context);

        ArgumentNullException.ThrowIfNull(next);

        HttpContext httpContext = context.HttpContext;

        if (CovenantRequestFeatures.Authority(httpContext) is
            { Requirement: CovenantAuthorityRequirement.SensitivityRetentionPurge } feature)
        {

            CovenantSensitivePurgeAuthorityScope? scope =
                httpContext.RequestServices.GetService(typeof(CovenantSensitivePurgeAuthorityScope))
                    as CovenantSensitivePurgeAuthorityScope;

            if (scope is not null && scope.Publish(feature.Context).IsFailure)
            {

                return CovenantAuthorityRefusal.Forbidden(httpContext);

            }

        }

        return await next(context).ConfigureAwait(false);

    }

}

/// <summary>
/// Rechecks, after binding and immediately before the handler, that the authority issued for this
/// request is still current and was issued for exactly this route's requirement.
/// </summary>
/// <remarks>
/// Defence in depth: the middleware is the primary decision, and this filter exists because the
/// window between them is real. Authority can be revoked by a host-tools taint transition or a key
/// rotation while a large body is being read, and a handler that ran on a context minted before that
/// transition would act with authority the installation has since withdrawn (§10.12).
///
/// <para>It rechecks rather than reissues. A reissue would silently adopt whatever generation is now
/// current, which is precisely the mid-flight change the recheck exists to catch.</para>
/// </remarks>
internal sealed class CovenantAuthorityEndpointFilter(CovenantAuthorityRequirement requirement)
    : IEndpointFilter
{

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {

        ArgumentNullException.ThrowIfNull(context);

        ArgumentNullException.ThrowIfNull(next);

        HttpContext httpContext = context.HttpContext;

        CovenantAuthorityFeature? feature = CovenantRequestFeatures.Authority(httpContext);

        if (feature is null || feature.Requirement != requirement)
        {

            return CovenantAuthorityRefusal.Forbidden(httpContext);

        }

        IOperatorAuthorityContextIssuer? issuer =
            httpContext.RequestServices.GetService(typeof(IOperatorAuthorityContextIssuer))
                as IOperatorAuthorityContextIssuer;

        if (issuer is not null && issuer.Revalidate(feature.Context).IsFailure)
        {

            return CovenantAuthorityRefusal.Stale(httpContext);

        }

        return await next(context).ConfigureAwait(false);

    }

}
