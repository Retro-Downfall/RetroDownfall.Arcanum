using System.Globalization;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

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

    /// <summary>
    /// A Campaign memory reset clears the extraction watermark of every Session bound to that Campaign,
    /// however that Session's binding was written, and leaves every other Campaign's alone.
    /// </summary>
    /// <remarks>
    /// <b>The watermark is what makes a reset stick.</b> Deleting a Campaign's memories without clearing
    /// the watermarks leaves the extraction pass believing it has already read those transcripts, so the
    /// removed conclusions are never derived again and the Session is left permanently thinner than it
    /// was - a silent half-erasure nothing surfaces.
    ///
    /// <para>Two Sessions bound by the two production writers, because the Campaign identity the
    /// selection binds against is exactly what they disagreed about. The third, in another Campaign, is
    /// the negative half: a predicate wide enough to take every watermark on the installation would
    /// satisfy the first two assertions on its own.</para>
    ///
    /// <para>This is also where the turn-begin repository's own rendering is pinned, and a mutation
    /// against it needs to know which tree it is on. Campaign-scoped recall no longer depends on that
    /// writer - the classifier canonicalizes what a memory records whatever the binding holds - but this
    /// selection reads <c>session_campaign_bindings.CampaignId</c> itself and compares it exactly. On the
    /// shipped tree, reverting that writer reds this case <i>at the seed</i> with "The session could not
    /// be created.", because the binding guard refuses the write before a watermark exists. Removing the
    /// two binding identity guards as well lets the seed through, and then the failure is the one this
    /// case is about: the repository-bound Session's watermark survives its Campaign's memory reset.
    /// Both measured.</para>
    ///
    /// <para>The watermarks are written and read through <see cref="ISagaMemoryStore"/> rather than
    /// seeded, because <c>saga_extraction_watermarks.SessionId</c> holds the minority spelling that store
    /// renders and the selection has to reach it across that boundary. A seed choosing the spelling would
    /// decide the outcome.</para>
    /// </remarks>
    [SkippableFact]
    public async Task ApplyAsync_ResetMemory_ForOneCampaign_ClearsTheWatermarkOfEverySessionBoundToIt()
    {

        RequireSqlCipher();

        await SeedCampaignRowAsync(ResetCampaignA);

        await SeedCampaignRowAsync(ResetCampaignB);

        Guid boundByRepository = await SessionBindingWriters.BoundByTheRepositoryAsync(
            _db!, ResetCampaignA, CancellationToken.None);

        Guid boundByInitializer = await SessionBindingWriters.BoundByTheInitializerAsync(
            _db!, ResetCampaignA, CancellationToken.None);

        Guid inAnotherCampaign = await SessionBindingWriters.BoundByTheRepositoryAsync(
            _db!, ResetCampaignB, CancellationToken.None);

        ISagaMemoryStore store = CreateSagaMemoryStore();

        DateTimeOffset extractedAt = DateTimeOffset.Parse(OldTimestamp, CultureInfo.InvariantCulture);

        foreach (Guid session in new[] { boundByRepository, boundByInitializer, inAnotherCampaign })
        {

            await store.SetWatermarkAsync(session, extractedAt, CancellationToken.None);

            Assert.NotNull(await store.GetWatermarkAsync(session, CancellationToken.None));

        }

        // A reset with no memories to delete still has to clear these, so the Campaign carries one.
        _ = await SeedScopedSagaMemoryAsync(ResetCampaignA);

        await ApplyCampaignResetAsync(MemoryResetScope.Saga, ResetCampaignA);

        Assert.Null(await store.GetWatermarkAsync(boundByRepository, CancellationToken.None));

        Assert.Null(await store.GetWatermarkAsync(boundByInitializer, CancellationToken.None));

        Assert.NotNull(await store.GetWatermarkAsync(inAnotherCampaign, CancellationToken.None));

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

    /// <summary>
    /// A memory owned by one Campaign, spelled the way <see cref="SagaMemoryScopeClassifier"/> renders
    /// the Campaign it reads out of that Session's binding.
    /// </summary>
    /// <remarks>
    /// Canonical because the classifier canonicalizes the identity it hands on, which is true of a
    /// memory written at any point in an upgrade - not because the version-5 sweep has settled the
    /// column. The sweep settles the rows written before it; this seed stands for one written after.
    ///
    /// <para>It rendered a bare <c>ToString()</c> while the binding writers disagreed and the classifier
    /// still passed their spelling through, which is what half of every installation's memories carried
    /// - and the reason the reset predicate looked correct while selecting half of what it named.</para>
    /// </remarks>
    private Task<string> SeedScopedSagaMemoryAsync(Guid campaignId) =>
        SeedSagaMemoryAsync(2, campaignId.ToString("D").ToUpperInvariant());

    /// <summary>The Campaign row the binding writers need before they will bind a Session to it.</summary>
    private Task SeedCampaignRowAsync(Guid campaignId) =>
        ExecuteAsync(
            """
            INSERT OR IGNORE INTO "Campaigns"
                ("Id", "Name", "NameLower", "Path", "Type", "Settings", "CreatedAt", "UpdatedAt")
            VALUES (@id, @name, @name, @path, 0, '{}', @at, @at)
            """,
            ("@id", campaignId.ToString("D").ToUpperInvariant()),
            ("@name", campaignId.ToString("N")),
            ("@path", $"/campaigns/{campaignId:N}"),
            ("@at", OldTimestamp));

    private ISagaMemoryStore CreateSagaMemoryStore() =>
        new SagaMemoryStore(
            _db!,
            new WeaveIndexAvailability(),
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

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
