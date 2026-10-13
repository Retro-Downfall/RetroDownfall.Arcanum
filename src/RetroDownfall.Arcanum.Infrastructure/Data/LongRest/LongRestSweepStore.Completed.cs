using System.Text.Json;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

internal interface ILongRestCompletedRequestReplay
{
    Task<Result<Guid?>> FindCompletedRequestReplayAsync(CanonicalCampaignContext scope, LongRestSweepScope partition,
        LongRestRecipeIdentity recipe, CancellationToken ct);
}

internal sealed partial class LongRestSweepStore : ILongRestCompletedRequestReplay
{
    /// <summary>Retained complete corpus evidence; the caller separately proves its operation lifecycle and ownership.</summary>
    public Task<Result<Guid?>> FindCompletedReplayAsync(CanonicalCampaignContext scope, LongRestSweepScope partition, LongRestRecipeIdentity recipe, CancellationToken ct) =>
        FindCompletedReplayCoreAsync(scope, partition, recipe, null, ct);

    public Task<Result<Guid?>> FindCompletedRequestReplayAsync(CanonicalCampaignContext scope, LongRestSweepScope partition,
        LongRestRecipeIdentity recipe, CancellationToken ct) =>
        FindCompletedReplayCoreAsync(scope, partition, recipe, LongRunningOperationState.Completed, ct);

    /// <summary>A read-only whole-scope candidate. The request owner must verify live configuration and lineage.</summary>
    public Task<Result<Guid?>> FindCompletedReplayAsync(CanonicalCampaignContext scope, LongRestRecipeIdentity recipe, CancellationToken ct) =>
        FindCompletedReplayCoreAsync(scope, null, recipe, null, ct);

    private Task<Result<Guid?>> FindCompletedReplayCoreAsync(CanonicalCampaignContext scope, LongRestSweepScope? partition,
        LongRestRecipeIdentity recipe, LongRunningOperationState? requiredState, CancellationToken ct) =>
        WithinAsync<Guid?>(async (connection, transaction) =>
    {
        if (partition is { } exact && (!exact.IsInitialized || exact.CampaignId != scope.CampaignId
            || exact.Kind != (scope.IsCampaignBound ? LongRestSweepScopeKind.Campaign : LongRestSweepScopeKind.Global)))
        {
            throw new InvalidDataException();
        }

        var recipeDigest = LongRestDiscoveryHash.Recipe(recipe);

        if (recipeDigest.IsFailure)
        {
            throw new InvalidDataException();
        }

        string? cursor = null;

        while (true)
        {
            List<Guid> page = [];

            await using (SqliteCommand command = Command(connection, transaction, """
                SELECT sweep.OperationId FROM long_rest_sweeps sweep
                JOIN long_rest_discovery_generations generation ON generation.GenerationId = sweep.GenerationId AND generation.OperationId = sweep.OperationId
                JOIN LongRunningOperations operation ON operation.Id = sweep.OperationId AND operation.Kind = 'long-rest-sweep'
                WHERE sweep.PhaseCode = 6 AND generation.PhaseCode = 6 AND generation.Published = 1
                  AND ($operationState IS NULL OR operation.State = $operationState)
                  AND sweep.ScopeKindCode = $scope AND sweep.CampaignId IS $campaign AND generation.RecipeDigest = $recipe
                  AND sweep.ConfigurationJson = json_extract(generation.FenceJson, '$.configuration')
                  AND ($cursor IS NULL OR sweep.OperationId > $cursor)
                ORDER BY sweep.OperationId LIMIT 128;
                """, ("$scope", scope.IsCampaignBound ? 2 : 1), ("$campaign", scope.CampaignId is { } campaign ? Format(campaign) : null),
                ("$recipe", recipeDigest.Value.Bytes), ("$cursor", cursor), ("$operationState", requiredState is { } state ? (int)state : null)))
            {
                await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    page.Add(Guid.Parse(reader.GetString(0)));
                }
            }

            if (page.Count == 0)
            {
                return null;
            }

            foreach (Guid operation in page)
            {
                try
                {
                    LongRestDiscoveryGeneration generation = await LongRestDiscoveryStore.ReadSweepGenerationAsync(connection, transaction, operation, ct).ConfigureAwait(false) ?? throw new InvalidDataException();

                    if (generation.Fence.CanonicalScope != scope || generation.Recipe != recipe || (partition is { } expected && generation.Scope != expected))
                    {
                        continue;
                    }

                    await LongRestDiscoveryStore.ValidateCompletedCorpusMetadataAsync(connection, transaction, generation, partition, ct).ConfigureAwait(false);

                    return operation;
                }
                catch (Exception exception) when (exception is InvalidDataException or ArgumentException or InvalidOperationException or FormatException or JsonException or OverflowException)
                {
                    // A stale or damaged candidate is not authority and does not consume new work.
                }
            }

            cursor = page[^1].ToString("N");
        }
    }, ct);
}
