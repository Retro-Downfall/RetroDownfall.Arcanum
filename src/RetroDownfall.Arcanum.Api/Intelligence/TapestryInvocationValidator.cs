using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Intelligence.Subagents;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>Checks the carried invocation; neither scope selection nor retrieval grants authority.</summary>
public sealed class TapestryInvocationValidator(IServiceScopeFactory scopes,
    IOptionsMonitor<ArcanumSettings> settings, ICovenantAuthoritySnapshotProvider authority)
{
    internal async Task<TapestryInvocationSelection?> AuthorizeAsync(PingRequest request,
        ArcanumInvocationContext invocation, CancellationToken cancellationToken)
    {
        EmbeddingSettings embeddings = settings.CurrentValue.ResolveEmbeddings();

        if (!embeddings.Enabled || !embeddings.TapestryEnabled || !IsEligible(invocation)) return null;

        CanonicalCampaignResolutionRequest resolution = new(request.SessionId, request.CampaignId, request.WorkingDirectory);

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();

        var resolver = scope.ServiceProvider.GetRequiredService<ICanonicalCampaignContextResolver>();

        var resolved = await resolver.ResolveAsync(resolution, cancellationToken).ConfigureAwait(false);

        if (resolved.IsFailure || resolved.Value != invocation.Campaign || !IsEligible(invocation)) return null;

        var workspace = scope.ServiceProvider.GetRequiredService<IWorkspaceIndexingService>();

        TapestryScope[] eligible = [.. WizardIntelligenceProvider.BuildTapestryScopes(request, embeddings.Tapestry, workspace)];

        return eligible.Length == 0 ? null : new(invocation, resolution, Array.AsReadOnly(eligible));
    }

    internal async Task<Result> ValidateAsync(TapestryRetrievalObservation observation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!MatchesCurrentIdentity(observation) || !IsEligible(observation.Selection.Invocation)) return Refused();

        var original = observation.Selection.Resolution;

        PingRequest request = new(string.Empty, null, original.WorkingDirectory ?? string.Empty,
            SessionId: original.SessionId, CampaignId: original.ExplicitCampaignId);

        TapestryInvocationSelection? authorized = await AuthorizeAsync(request, observation.Selection.Invocation, cancellationToken).ConfigureAwait(false);

        if (authorized is null || observation.Generations.Count == 0
            || observation.Generations.Any(item => !authorized.Scopes.Contains(item.Scope)
                || !observation.Selection.Scopes.Contains(item.Scope))) return Refused();

        await using (AsyncServiceScope scope = scopes.CreateAsyncScope())
        {
            ITapestryStore store = scope.ServiceProvider.GetRequiredService<ITapestryStore>();

            foreach (TapestryScopeGeneration item in observation.Generations)
            {
                var current = await store.GetRetrievableGenerationAsync(item.Scope, observation.Identity, cancellationToken).ConfigureAwait(false);

                if (current.IsFailure || current.Value.Generation.GenerationId != item.GenerationId) return Refused();
            }
        }

        // Generation reads can await after the first canonical snapshot. Recheck the original
        // inputs after those reads and their scope finish, before the final epoch/model check.
        TapestryInvocationSelection? finalAuthorized = await AuthorizeAsync(request, observation.Selection.Invocation, cancellationToken).ConfigureAwait(false);

        if (finalAuthorized is null || observation.Generations.Any(item => !finalAuthorized.Scopes.Contains(item.Scope))) return Refused();

        return IsEligible(observation.Selection.Invocation) && MatchesCurrentIdentity(observation) ? Result.Success() : Refused();
    }

    private bool MatchesCurrentIdentity(TapestryRetrievalObservation observation)
    {
        EmbeddingSettings embeddings = settings.CurrentValue.ResolveEmbeddings();

        return embeddings.Enabled && embeddings.TapestryEnabled
            && !string.IsNullOrWhiteSpace(embeddings.Provider) && !string.IsNullOrWhiteSpace(embeddings.Model)
            && new EmbeddingProvenance(embeddings.Provider, embeddings.Model).Identity == observation.Identity.EmbeddingIdentity;
    }

    internal static Result Refused() => Result.Failure(new Error(ErrorCodes.Hub.InvalidAdmission,
        "The injected Tapestry source, invocation or embedding identity changed before dispatch."));

    private bool IsEligible(ArcanumInvocationContext invocation) => !SubagentExecutionAmbient.IsIsolated
        && invocation.CanReadCovenant && invocation.Campaign is not null
        && invocation.ReadAuthorityEpoch!.Matches(authority.Current);
}

internal sealed record TapestryInvocationSelection(ArcanumInvocationContext Invocation,
    CanonicalCampaignResolutionRequest Resolution, IReadOnlyList<TapestryScope> Scopes);
