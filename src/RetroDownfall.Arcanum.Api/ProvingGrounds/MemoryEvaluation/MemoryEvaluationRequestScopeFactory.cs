using Microsoft.AspNetCore.Http;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Api.ProvingGrounds.MemoryEvaluation;

internal sealed class MemoryEvaluationRequestScopeFactory(ICanonicalCampaignContextResolver resolver, ICovenantOperationGate gate, IOperatorAuthorityContextIssuer issuer)
{
    public async Task<Result<MemoryEvaluationRequestScope>> OpenAsync(HttpContext httpContext, MemoryEvaluationScope scope, CancellationToken ct)
    {
        CovenantRequestFeatures.MarkProtectedResponse(httpContext);

        CovenantAuthorityFeature? original = CovenantRequestFeatures.Authority(httpContext);

        if (original is null || original.Requirement != CovenantAuthorityRequirement.ProtectedRead
            || !original.Context.Satisfies(CovenantAuthorityRequirement.ProtectedRead)
            || original.AuthorityEpoch != original.Context.AuthorityEpoch || issuer.Revalidate(original.Context).IsFailure
            || ArcanumInvocationContexts.ResolveContextPolicy(httpContext) != CovenantContextPolicy.Default)
            return new Error(ErrorCodes.MemoryEvaluation.StaleAuthority, "Evaluation requires the original protected read request authority.");

        if (scope is null || scope.Kind switch
            {
                MemoryEvaluationScopeKind.Global => scope.CampaignId is not null,
                MemoryEvaluationScopeKind.Campaign => scope.CampaignId is null || scope.CampaignId == Guid.Empty,
                _ => true
            }) return new Error(ErrorCodes.MemoryEvaluation.InvalidScope, "An explicit Global or Campaign scope is required.");

        var canonical = await resolver.ResolveAsync(new(null, scope.CampaignId, null), ct).ConfigureAwait(false);

        if (canonical.IsFailure) return canonical.Error;

        if (canonical.Value.CampaignId != scope.CampaignId)
            return new Error(ErrorCodes.MemoryEvaluation.InvalidScope, "Canonical scope does not match the submitted scope.");

        var invocation = ArcanumInvocationContexts.ForMemoryEvaluation(httpContext, canonical.Value, issuer);

        if (invocation.IsFailure) return invocation.Error;

        var acquired = await gate.AcquireTurnAsync(canonical.Value, ct).ConfigureAwait(false);

        if (acquired.IsFailure) return acquired.Error;

        var retained = new MemoryEvaluationRequestScope(httpContext, original, issuer, gate, invocation.Value, acquired.Value);

        try
        {
            var valid = await retained.RevalidateAsync(ct).ConfigureAwait(false);

            if (valid.IsFailure)
            {
                await retained.DisposeAsync().ConfigureAwait(false);

                return valid.Error;
            }

            httpContext.Response.RegisterForDisposeAsync(retained);

            return retained;
        }
        catch
        {
            await retained.DisposeAsync().ConfigureAwait(false);

            throw;
        }
    }
}
