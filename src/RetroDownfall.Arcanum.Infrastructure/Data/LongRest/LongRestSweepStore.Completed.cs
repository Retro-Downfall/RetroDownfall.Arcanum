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

    // The completed generations that could answer one request, newest first, so the first one that proves out is the freshest and a
    // newer generation is always examined before an older one. The partition is part of the query when the caller names one, and the
    // whole-scope form leaves it out. The page is a keyset (watermark, operation), not an offset, so nothing is skipped or repeated and
    // nothing is capped: a candidate the lookup never reached would be a false miss, and a miss starts a paid re-sweep.
    private const string CompletedCandidatesHead = """
        SELECT sweep.OperationId, generation.InsertionHighWatermark FROM long_rest_sweeps sweep
        JOIN long_rest_discovery_generations generation ON generation.GenerationId = sweep.GenerationId AND generation.OperationId = sweep.OperationId
        JOIN LongRunningOperations operation ON operation.Id = sweep.OperationId AND operation.Kind = 'long-rest-sweep'
        WHERE sweep.PhaseCode = 6 AND generation.PhaseCode = 6 AND generation.Published = 1
          AND ($operationState IS NULL OR operation.State = $operationState)
          AND sweep.ScopeKindCode = $scope AND sweep.CampaignId IS $campaign
        """;

    private const string CompletedCandidatesTail = """
          AND generation.RecipeDigest = $recipe
          AND sweep.ConfigurationJson = json_extract(generation.FenceJson, '$.configuration')
          AND ($cursorMark IS NULL OR generation.InsertionHighWatermark < $cursorMark
            OR (generation.InsertionHighWatermark = $cursorMark AND sweep.OperationId > $cursorOperation))
        ORDER BY generation.InsertionHighWatermark DESC, sweep.OperationId LIMIT 128;
        """;

    internal static readonly string CompletedExactPartitionSql = CompletedCandidatesHead + "\n  AND sweep.PartitionDigest IS $partition\n" + CompletedCandidatesTail;

    internal static readonly string CompletedWholeScopeSql = CompletedCandidatesHead + "\n" + CompletedCandidatesTail;

    private const int CompletedPageSize = 128;

    private Task<Result<Guid?>> FindCompletedReplayCoreAsync(CanonicalCampaignContext scope, LongRestSweepScope? partition,
        LongRestRecipeIdentity recipe, LongRunningOperationState? requiredState, CancellationToken ct) =>
        WithinReadAsync<Guid?>(async (connection, transaction) =>
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

        long? cursorMark = null;

        string? cursorOperation = null;

        while (true)
        {
            List<(Guid Operation, long Mark)> page = [];

            await using (SqliteCommand command = Command(connection, transaction, partition is null ? CompletedWholeScopeSql : CompletedExactPartitionSql,
                ("$scope", scope.IsCampaignBound ? 2 : 1), ("$campaign", scope.CampaignId is { } campaign ? Format(campaign) : null),
                ("$recipe", recipeDigest.Value.Bytes), ("$cursorMark", cursorMark), ("$cursorOperation", cursorOperation),
                ("$operationState", requiredState is { } state ? (int)state : null)))
            {
                if (partition is { } named)
                {
                    _ = command.Parameters.AddWithValue("$partition", (object?)named.ProtectedPartitionDigest?.Bytes ?? DBNull.Value);
                }

                await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    page.Add((Guid.Parse(reader.GetString(0)), reader.GetInt64(1)));
                }
            }

            foreach ((Guid operation, long _) in page)
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

            if (page.Count < CompletedPageSize)
            {
                return null;
            }

            (Guid last, long mark) = page[^1];

            cursorMark = mark;

            cursorOperation = last.ToString("N");
        }
    }, ct);
}
