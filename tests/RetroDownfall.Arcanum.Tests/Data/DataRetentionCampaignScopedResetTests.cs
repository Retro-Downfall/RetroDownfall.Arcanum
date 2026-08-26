using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Data;

/// <summary>
/// Resetting one Campaign's memories, and leaving every other Campaign's exactly where they were.
/// </summary>
/// <remarks>
/// The rows are seeded through raw SQL rather than through the writers, because what is under test is
/// the erasure predicate: which rows a reset selects, counts, deletes, and then reconciles. A writer in
/// the loop would only add a second thing that could be wrong.
/// </remarks>
public sealed partial class DataRetentionServiceTests
{

    private static readonly Guid ResetCampaignA = new("A0000000-0000-4000-8000-000000000A11");

    private static readonly Guid ResetCampaignB = new("B0000000-0000-4000-8000-000000000B22");

    [SkippableFact]
    public async Task ApplyAsync_ResetMemory_ForOneCampaign_LeavesEveryOtherCampaignsSagaMemoriesAlone()
    {

        RequireSqlCipher();

        string ownedByA = await SeedScopedSagaMemoryAsync(ResetCampaignA);

        string ownedByB = await SeedScopedSagaMemoryAsync(ResetCampaignB);

        string installationScoped = await SeedGlobalSagaMemoryAsync();

        await ApplyCampaignResetAsync(MemoryResetScope.Saga, ResetCampaignA);

        Assert.Equal(0, await CountSagaAsync(ownedByA));

        Assert.Equal(1, await CountSagaAsync(ownedByB));

        Assert.Equal(1, await CountSagaAsync(installationScoped));

    }

    /// <summary>
    /// The embedding a memory cannot be ranked without goes with it, and nothing else's does.
    /// </summary>
    [SkippableFact]
    public async Task ApplyAsync_ResetMemory_ForOneCampaign_RemovesOnlyThatCampaignsEmbeddings()
    {

        RequireSqlCipher();

        string ownedByA = await SeedScopedSagaMemoryAsync(ResetCampaignA);

        string ownedByB = await SeedScopedSagaMemoryAsync(ResetCampaignB);

        await ApplyCampaignResetAsync(MemoryResetScope.Saga, ResetCampaignA);

        Assert.Equal(0, await CountAsync("saga_memory_embeddings", "MemoryId", ownedByA));

        Assert.Equal(1, await CountAsync("saga_memory_embeddings", "MemoryId", ownedByB));

    }

    /// <summary>
    /// A Campaign-scoped Lexicon reset leaves the global entity of the same name standing, and leaves
    /// the other Campaign's entity searchable rather than present-but-unindexed.
    /// </summary>
    [SkippableFact]
    public async Task ApplyAsync_ResetMemory_ForOneCampaign_LeavesOtherLexiconTiersIntactAndIndexed()
    {

        RequireSqlCipher();

        await SeedScopedLexiconEntryAsync("config", ResetCampaignA.ToString());

        await SeedScopedLexiconEntryAsync("config", ResetCampaignB.ToString());

        await SeedScopedLexiconEntryAsync("config", string.Empty);

        await ApplyCampaignResetAsync(MemoryResetScope.Lexicon, ResetCampaignA);

        Assert.Equal(0, await CountAsync("lexicon_entries", "ScopeCampaignId", ResetCampaignA.ToString()));

        Assert.Equal(1, await CountAsync("lexicon_entries", "ScopeCampaignId", ResetCampaignB.ToString()));

        Assert.Equal(1, await CountAsync("lexicon_entries", "ScopeCampaignId", string.Empty));

        // The external-content index still answers for the survivors. A reset that cleared lexicon_fts
        // outright would leave those two entities present and unfindable, which no assertion about
        // lexicon_entries alone can see.
        Assert.Equal(2, await CountLexiconFtsMatchesAsync("config"));

    }

    /// <summary>
    /// Only Saga and Lexicon record an owning Campaign, so naming one for any other store is refused
    /// rather than quietly widened into a reset of the whole store.
    /// </summary>
    [SkippableFact]
    public async Task PlanAsync_ResetMemory_ForACampaign_IsRefusedForStoresThatRecordNoOwner()
    {

        RequireSqlCipher();

        DataRetentionPlan plan = await CreateService().PlanAsync(
            new DataRetentionRequest(
                DataRetentionOperation.ResetMemory,
                ResetCampaignA,
                MemoryResetScope.Workspace),
            CancellationToken.None);

        DataRetentionBlocker blocker = Assert.Single(plan.Blockers);

        Assert.Equal(ErrorCodes.Data.InvalidRequest, blocker.ReasonCode);

    }

    /// <summary>
    /// A Campaign-targeted plan pins the Campaign in the candidate the apply re-checks.
    /// </summary>
    /// <remarks>
    /// Asserted on the candidate rather than through a mismatched apply, because a mismatched apply is
    /// already refused by the plan-id guard and would pass whatever this candidate said. The candidate
    /// is what stops the subtler case the plan id cannot see: an installation where only one Campaign
    /// holds memories, where a targeted preview and an untargeted reset agree on the row count and the
    /// wider erasure would otherwise go through.
    /// </remarks>
    [SkippableFact]
    public async Task PlanAsync_ResetMemory_PinsTheCampaignInTheCandidateItPreviews()
    {

        RequireSqlCipher();

        _ = await SeedScopedSagaMemoryAsync(ResetCampaignA);

        IDataRetentionService service = CreateService();

        DataRetentionPlan targeted = await service.PlanAsync(
            new DataRetentionRequest(
                DataRetentionOperation.ResetMemory,
                ResetCampaignA,
                MemoryResetScope.Saga),
            CancellationToken.None);

        DataRetentionPlan untargeted = await service.PlanAsync(
            new DataRetentionRequest(
                DataRetentionOperation.ResetMemory,
                TargetId: null,
                MemoryResetScope.Saga),
            CancellationToken.None);

        Assert.Equal([$"Saga:{ResetCampaignA:D}"], targeted.CandidateIds);

        Assert.Equal(["Saga"], untargeted.CandidateIds);

    }

    private async Task ApplyCampaignResetAsync(MemoryResetScope scope, Guid campaignId)
    {

        IDataRetentionService service = CreateService();

        DataRetentionRequest request = new(DataRetentionOperation.ResetMemory, campaignId, scope);

        DataRetentionPlan plan = await service.PlanAsync(request, CancellationToken.None);

        Result<DataRetentionApplyResult> applied = await service.ApplyAsync(
            new DataRetentionApplyRequest(request, plan.PlanId),
            CancellationToken.None);

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : string.Empty);

        Assert.True(applied.Value.Reconciled);

    }

    private Task<string> SeedScopedSagaMemoryAsync(Guid campaignId) =>
        SeedSagaMemoryAsync(2, campaignId.ToString());

    private Task<string> SeedGlobalSagaMemoryAsync() => SeedSagaMemoryAsync(1, null);

    private async Task<string> SeedSagaMemoryAsync(int scopeKindCode, string? campaignId)
    {

        string id = Guid.NewGuid().ToString();

        await ExecuteAsync(
            """
            INSERT INTO saga_memories
                ("Id", "Content", "CreatedAt", "SessionId", "Tags", "Source", ScopeKindCode, CampaignId)
            VALUES (@id, 'a conclusion', @at, NULL, NULL, 'test', @kind, @campaignId)
            """,
            ("@id", id),
            ("@at", OldTimestamp),
            ("@kind", scopeKindCode),
            ("@campaignId", (object?)campaignId ?? DBNull.Value));

        await ExecuteAsync(
            """
            INSERT INTO saga_memory_embeddings ("MemoryId", "Embedding", "Dim")
            VALUES (@id, zeroblob(8), 2)
            """,
            ("@id", id));

        return id;

    }

    private Task SeedScopedLexiconEntryAsync(string nameNormalized, string scopeCampaignId) =>
        ExecuteAsync(
            """
            INSERT INTO lexicon_entries
                (Id, Name, NameNormalized, ScopeCampaignId, Type, FactsJson, FactsText, UpdatedAt)
            VALUES (@id, @name, @name, @scope, 'Concept', '[]', @name, @at)
            """,
            ("@id", Guid.NewGuid().ToString("N")),
            ("@name", nameNormalized),
            ("@scope", scopeCampaignId),
            ("@at", OldTimestamp));

    private async Task<int> CountSagaAsync(string memoryId) =>
        await CountAsync("saga_memories", "Id", memoryId);

    private async Task<int> CountLexiconFtsMatchesAsync(string term)
    {

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT COUNT(*) FROM lexicon_fts WHERE lexicon_fts MATCH @term";

        _ = command.Parameters.AddWithValue("@term", term);

        return Convert.ToInt32(
            await command.ExecuteScalarAsync(CancellationToken.None),
            System.Globalization.CultureInfo.InvariantCulture);

    }

}
