using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.ProvingGrounds;

namespace RetroDownfall.Arcanum.Tests.Backup;

public sealed class BackupRestoreMemoryEvaluationTests
{
    [Theory]
    [InlineData("prepared")]
    [InlineData("generation")]
    [InlineData("owner")]
    public async Task Restore_drops_incompatible_or_incomplete_evidence_without_rebinding_history(string invalid)
    {
        await using var h = await MemoryEvaluationSourceHarness.CreateAsync();

        await h.Saga.Store.InsertAsync("source", "fact", DateTimeOffset.UtcNow.AddDays(-1), null, null, "extraction", h.Saga.Embedding(), CancellationToken.None);

        var claim = (await h.Saga.Annals.GetClaimAsync(AnnalSubjectStore.Saga, "source", CancellationToken.None))!;

        var source = MemoryEvaluationEvidenceStoreTests.Source() with { SubjectId = "source", ClaimId = claim.ClaimId, VersionId = claim.CurrentVersionId };

        Guid generation = new((byte[])(await MemoryEvaluationLifecycleTests.ScalarAsync(h.Connection, "SELECT DatasetGeneration FROM covenant_state"))!);

        var valid = await SeedAsync(h.Connection, source, generation, true);

        var stale = await SeedAsync(h.Connection, invalid == "owner" ? source with { CanonicalOwnerSessionId = Guid.NewGuid().ToString("D").ToUpperInvariant() } : source,
            invalid == "generation" ? Guid.NewGuid() : generation, invalid != "prepared");

        var before = await MemoryEvaluationLifecycleTests.Store(h.Connection).GetAsync(valid.EvaluationId, CancellationToken.None);

        await MemoryEvaluationLifecycleTests.ExecuteAsync(h.Connection, "PRAGMA foreign_keys = OFF");

        Assert.True((await BackupRestoreMemoryEvaluationSanitation.SanitizeStagedAsync(h.Connection, CancellationToken.None)).IsSuccess);

        await MemoryEvaluationLifecycleTests.AssertAbsentAsync(h.Connection, stale.EvaluationId);

        var after = await MemoryEvaluationLifecycleTests.Store(h.Connection).GetAsync(valid.EvaluationId, CancellationToken.None);

        Assert.True(after.IsSuccess);

        Assert.Equal(before.Value.ReportDigest, after.Value.ReportDigest);

        Assert.Equal(before.Value.Sources.ToArray(), after.Value.Sources.ToArray());

        Assert.True((await MemoryEvaluationLifecycleTests.Store(h.Connection).CompleteAsync(stale.EvaluationId, MemoryEvaluationEvidenceStoreTests.Report(stale, [source]), CancellationToken.None)).IsFailure);

        Assert.True((await BackupRestoreMemoryEvaluationSanitation.VerifyCommittedAsync(h.Connection, CancellationToken.None)).IsSuccess);

        Assert.Equal(1L, await MemoryEvaluationLifecycleTests.ScalarAsync(h.Connection, "SELECT count(*) FROM saga_memories"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_restore_commits_use_authority_removal_when_evaluation_tables_are_absent(bool declaredLegacySchema)
    {
        using var file = EvolutionScratchDatabase.Create();

        await using var connection = await file.OpenAsync(CancellationToken.None);

        await MemoryEvaluationLifecycleTests.ExecuteAsync(connection, """
            CREATE TABLE saga_committed_uses (
                AssistantEntryId TEXT NOT NULL, MemoryId TEXT NOT NULL, ClaimId TEXT NOT NULL,
                VersionId TEXT NOT NULL, HashFormat INTEGER NOT NULL, ContentDigest BLOB NOT NULL);
            INSERT INTO saga_committed_uses VALUES ('assistant','memory','claim','version',1,zeroblob(32));
            """);

        if (declaredLegacySchema)
        {
            await MemoryEvaluationLifecycleTests.ExecuteAsync(connection, """
                CREATE TABLE grimoire_feature_schemas (FamilyCode INTEGER, TransactionTierCode INTEGER, SchemaVersion INTEGER);
                INSERT INTO grimoire_feature_schemas VALUES (0,0,18);
                """);
        }

        Assert.True((await BackupRestoreMemoryEvaluationSanitation.SanitizeStagedAsync(connection, CancellationToken.None)).IsSuccess);

        Assert.Equal(0L, await MemoryEvaluationLifecycleTests.ScalarAsync(connection, "SELECT count(*) FROM saga_committed_uses"));

        Assert.True((await BackupRestoreMemoryEvaluationSanitation.VerifyCommittedAsync(connection, CancellationToken.None)).IsSuccess);
    }

    private static async Task<MemoryEvaluationRegistration> SeedAsync(SqliteConnection connection, MemoryEvaluationSourceCoordinate source, Guid generation, bool terminal)
    {
        var r = MemoryEvaluationEvidenceStoreTests.Registration([source]);

        r = r with { Ownership = r.Ownership with { DatasetGeneration = generation } };

        var store = MemoryEvaluationLifecycleTests.Store(connection);

        Assert.True((await store.RegisterAsync(r, [source], CancellationToken.None)).IsSuccess);

        if (terminal) Assert.True((await store.CompleteAsync(r.EvaluationId, MemoryEvaluationEvidenceStoreTests.Report(r, [source]), CancellationToken.None)).IsSuccess);

        return r;
    }
}
