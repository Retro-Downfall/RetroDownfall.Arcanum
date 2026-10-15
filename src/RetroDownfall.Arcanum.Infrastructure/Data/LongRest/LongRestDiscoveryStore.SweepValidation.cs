using System.Collections.Immutable;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.LongRest;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

internal sealed partial class LongRestDiscoveryStore
{
    internal static Task<LongRestDiscoveryGeneration?> ReadSweepGenerationAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid operation, CancellationToken ct) => ReadGenerationAsync(connection, transaction, "OperationId = $id", operation.ToString("N"), ct);

    internal static async Task ValidateSweepGenerationAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, LongRestMaintenanceCapability? capability, LongRestVerificationDepth depth, CancellationToken ct)
    {
        if (generation.Phase is LongRestSweepPhase.Invalidated or LongRestSweepPhase.Deferred or LongRestSweepPhase.Retired
            || (await ValidateReadAuthorityAsync(generation, capability, ct).ConfigureAwait(false)).IsFailure)
        {
            throw new InvalidDataException();
        }

        await VerifyFrozenSourcesAsync(connection, transaction, generation, capability, depth, ct).ConfigureAwait(false);
    }

    internal static Task<LongRestSourceLeaf> ReadSweepLeafAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, string memory, LongRestMaintenanceCapability? capability, CancellationToken ct) =>
        ReadFrozenLeafAsync(connection, transaction, generation, memory, capability, ct);

    internal static bool CanAssociateSweepWork(LongRestWorkIdentity historical, LongRestWorkIdentity current) =>
        LongRestDiscoveryHash.Work(historical).IsSuccess && historical with { Fence = current.Fence } == current
        && (historical.Fence == current.Fence || CanReadmit(historical.Fence, current.Fence));

    internal static void ValidateAdjudicationPaid(LongRestAdjudication accepted, LongRestRecipeIdentity recipe) =>
        ValidatePaidShape(accepted.PaidEvidence ?? throw new InvalidDataException(), accepted.Work, LongRestProviderPayloadKind.Adjudication,
            accepted.PayloadDigest, recipe.SummaryDestinationDigest);

    internal static Task VerifyAdjudicationPaidAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestAdjudication accepted, LongRestRecipeIdentity recipe, CancellationToken ct) =>
        VerifyCurrentPaidAsync(connection, transaction, accepted.Work, accepted.PaidEvidence ?? throw new InvalidDataException(),
            LongRestProviderPayloadKind.Adjudication, accepted.PayloadDigest, recipe.SummaryDestinationDigest, ct);

    internal static async Task ValidateCurrentSweepWorkAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestWorkIdentity work, ImmutableArray<LongRestSourceLeaf> inputs, CancellationToken ct)
    {
        LongRestDiscoveryGeneration generation = await ReadSweepGenerationAsync(connection, transaction, work.OperationId, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException();

        if (generation.Phase == LongRestSweepPhase.Retired || generation.GenerationId != work.GenerationId
            || generation.Scope != work.Scope || generation.Fence != work.Fence
            || LongRestDiscoveryHash.Recipe(generation.Recipe).Value != work.RecipeDigest || work.Kind != LongRestWorkKind.Anchor
            || inputs.IsDefaultOrEmpty || inputs.Length > LongRestPolicy.MaxTargets
            || LongRestDiscoveryHash.SourcePage(work.Scope, inputs).Value != work.SourceDigest
            || !await FrozenSourceMetadataMatchesAsync(connection, transaction, generation, ct).ConfigureAwait(false))
        {
            throw new InvalidDataException();
        }

        await using SqliteCommand command = Command(connection, transaction, """
            SELECT count(*) FROM long_rest_work_items unit
            WHERE unit.OperationId = $operation AND unit.GenerationId = $generation AND unit.KindCode = 3 AND unit.ItemId = $item
              AND unit.WorkDigest = $work AND unit.SourceDigest = $source AND unit.RecipeDigest = $recipe AND unit.DispositionCode = 1;
            """, ("$operation", work.OperationId.ToString("N")), ("$generation", Format(work.GenerationId)), ("$item", work.ItemId),
            ("$work", LongRestDiscoveryHash.Work(work).Value.Bytes), ("$source", work.SourceDigest.Bytes), ("$recipe", work.RecipeDigest.Bytes));

        if ((long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))! != 1)
        {
            throw new InvalidDataException();
        }
    }
}
