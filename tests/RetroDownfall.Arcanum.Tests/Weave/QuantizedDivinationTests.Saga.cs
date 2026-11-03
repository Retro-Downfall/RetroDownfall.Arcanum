using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;
using System.Text;

namespace RetroDownfall.Arcanum.Tests.Weave;

public sealed partial class QuantizedDivinationTests
{
    private static readonly DateTimeOffset SagaInstant = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Saga_compact_threshold_pruning_does_not_allocate_ranking_records_per_row()
    {
        using EvolutionScratchDatabase scratch = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await scratch.OpenAsync(CancellationToken.None);

        await CreateSagaCorpusAsync(connection);

        await SeedSagaDistractorsAsync(connection);

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        DivinationService service = SagaService(db);

        Embedding<float> query = new(Axis(0));

        Task<Result<DivinationResult[]>> Search() => service.SearchAsync("saga_memory_embeddings", "MemoryId", "Embedding",
            query, 1, 0.5f, CancellationToken.None);

        Assert.True((await Search()).IsSuccess);

        long compactBytes = MeasureSagaScan(Search);

        await using SqliteCommand disable = connection.CreateCommand();

        disable.CommandText = "DROP TABLE saga_memory_embeddings_quantized";

        await disable.ExecuteNonQueryAsync();

        Assert.True((await Search()).IsSuccess);

        long exhaustiveBytes = MeasureSagaScan(Search);

        output.WriteLine($"Saga below-threshold scan allocated {compactBytes:N0} compact / {exhaustiveBytes:N0} exhaustive bytes.");

        // Query intervals and the two reusable statements are bounded query overhead. Constructing
        // SagaRankingSnapshot/Components for each of 2,048 discarded rows vastly exceeds this delta.
        Assert.True(compactBytes - exhaustiveBytes < 64 * 1024,
            $"Compact scan added {compactBytes - exhaustiveBytes:N0} bytes; ranking must not allocate for discarded rows.");
    }

    [Theory]
    [InlineData("unscoped")]
    [InlineData("scoped")]
    [InlineData("campaign")]
    public async Task Saga_compact_ranking_retains_boosted_lower_cosine_winner_and_eligibility(string path)
    {
        using EvolutionScratchDatabase scratch = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await scratch.OpenAsync(CancellationToken.None);

        await CreateSagaCorpusAsync(connection);

        await SeedSagaDistractorsAsync(connection);

        await AddSagaAsync(connection, "a-inferred", 0.9f, 3);

        await AddSagaAsync(connection, "z-operator", 0.85f, 1);

        await AddSagaAsync(connection, "retired", 1f, 1, retired: true);

        await AddSagaAsync(connection, "dormant", 1f, 3, old: true);

        await AddSagaAsync(connection, "not-yet-valid", 1f, 1, future: true);

        await AddSagaAsync(connection, "other-campaign", 1f, 3, campaign: "D0000000-0000-4000-8000-000000000000", source: "other");

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        DivinationService service = SagaService(db);

        Embedding<float> query = new(Axis(0));

        Task<Result<DivinationResult[]>> Search() => path switch
        {
            "scoped" => service.SearchScopedAsync("saga_memory_embeddings", "MemoryId", "Embedding",
                "saga_memories", "Id", "Source", "test", query, 1, 0.5f, CancellationToken.None),

            "campaign" => service.SearchCampaignScopedAsync("saga_memory_embeddings", "MemoryId", "Embedding",
                new("saga_memories", "Id", "ScopeKindCode", "CampaignId", 1, 2,
                    Guid.Parse("C0000000-0000-4000-8000-000000000000")), query, 1, 0.5f, CancellationToken.None),

            _ => service.SearchAsync("saga_memory_embeddings", "MemoryId", "Embedding", query, 1, 0.5f, CancellationToken.None),
        };

        if (path == "unscoped")
        {
            await using SqliteCommand remove = connection.CreateCommand();

            remove.CommandText = "DELETE FROM saga_memory_embeddings WHERE MemoryId = 'other-campaign'";

            await remove.ExecuteNonQueryAsync();
        }

        Result<DivinationResult[]> accelerated = await Search();

        Assert.True(accelerated.IsSuccess, accelerated.Error.Message);

        DivinationResult winner = Assert.Single(accelerated.Value);

        Assert.Equal("z-operator", winner.Id);

        Assert.True(winner.Similarity < 0.9f);

        Assert.True(winner.Ranking!.Score > 0.9f);

        await using SqliteCommand disable = connection.CreateCommand();

        disable.CommandText = "DROP TABLE saga_memory_embeddings_quantized";

        await disable.ExecuteNonQueryAsync();

        Result<DivinationResult[]> exhaustive = await Search();

        Assert.True(exhaustive.IsSuccess, exhaustive.Error.Message);

        Assert.Equal(exhaustive.Value, accelerated.Value);
    }

    private static DivinationService SagaService(ArcanumDbContext db) =>
        new(db, NullLogger<DivinationService>.Instance,
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings { Features = new FeatureSettings { LongRest = true } }),
            new SagaTime());

    private static long MeasureSagaScan(Func<Task<Result<DivinationResult[]>>> search)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();

        Task<Result<DivinationResult[]>> pending = search();

        Assert.True(pending.IsCompletedSuccessfully, "Allocation measurement needs synchronous SQLite execution.");

        Assert.True(pending.Result.IsSuccess, pending.Result.Error.Message);

        Assert.Empty(pending.Result.Value);

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static async Task CreateSagaCorpusAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();

        // Real direct-SQL ranking readers consume this minimal retained schema. No evidence catalog
        // means an empty evidence cut; version/origin/lifecycle behavior is the production policy.
        command.CommandText = """
            CREATE TABLE grimoire_feature_schemas(FamilyCode INTEGER, TransactionTierCode INTEGER, SchemaVersion INTEGER);
            INSERT INTO grimoire_feature_schemas VALUES(0,0,37);
            CREATE TABLE saga_memories(Id TEXT PRIMARY KEY, CreatedAt TEXT, RetiredAtUtc TEXT, PinnedAtUtc TEXT,
                ScopeKindCode INTEGER, CampaignId TEXT, Source TEXT);
            CREATE TABLE saga_memory_embeddings(MemoryId TEXT PRIMARY KEY, Embedding BLOB NOT NULL, Dim INTEGER);
            CREATE TABLE saga_memory_embeddings_quantized(MemoryId TEXT PRIMARY KEY, Representation BLOB NOT NULL CHECK(typeof(Representation)='blob' AND length(Representation)<=65624)) WITHOUT ROWID;
            CREATE TABLE annal_claims(ClaimId TEXT, SubjectStoreCode INTEGER, SubjectId TEXT);
            CREATE TABLE annal_heads(CurrentVersionId TEXT, ClaimId TEXT, SubjectStoreCode INTEGER);
            CREATE TABLE annal_versions(VersionId TEXT, ClaimId TEXT, OriginCode INTEGER, RecordedAtUtc TEXT,
                ValidFromUtc TEXT, ValidToUtc TEXT, ContentHashFormatCode INTEGER, ContentHash BLOB,
                OperationCode INTEGER, ScopeKindCode INTEGER, CampaignId TEXT, SensitivityCode INTEGER);
            CREATE TABLE long_rest_suppressions(SourceVersionId TEXT, SurvivorVersionId TEXT, ReceiptId TEXT);
            CREATE TABLE long_rest_receipts(ReceiptId TEXT, SurvivorMemoryId TEXT);
            CREATE TABLE artifact_sensitivity(ArtifactKindCode INTEGER, ArtifactId TEXT);
            """;

        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedSagaDistractorsAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            WITH RECURSIVE rows(n) AS (VALUES(1) UNION ALL SELECT n+1 FROM rows WHERE n<2048)
            INSERT INTO saga_memories SELECT printf('filler-%08d',n),@time,NULL,NULL,1,NULL,'test' FROM rows;
            INSERT INTO saga_memory_embeddings SELECT Id,@vector,768 FROM saga_memories;
            """;

        command.Parameters.AddWithValue("@time", UtcInstantText.Format(SagaInstant));

        command.Parameters.AddWithValue("@vector", EmbeddingBlobCodec.Encode(Axis(1)));

        await command.ExecuteNonQueryAsync();

        await using SqliteTransaction transaction = connection.BeginTransaction();

        for (int index = 1; index <= 2048; index++)
        {
            await QuantizedEmbeddingStore.UpsertAsync(connection, transaction, "saga_memory_embeddings",
                $"filler-{index:D8}", Axis(1), CancellationToken.None);
        }

        await transaction.CommitAsync();
    }

    private static async Task AddSagaAsync(SqliteConnection connection, string id, float cosine, int origin,
        bool retired = false, bool old = false, bool future = false, string? campaign = null, string source = "test")
    {
        float[] vector = Axis(0);

        vector[0] = cosine;

        vector[1] = MathF.Sqrt(1f - cosine * cosine);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO saga_memories VALUES(@id,@time,@retired,NULL,@scope,@campaign,@source);
            INSERT INTO saga_memory_embeddings VALUES(@id,@vector,768);
            INSERT INTO saga_memory_embeddings_quantized VALUES(@id,@compact);
            INSERT INTO annal_claims VALUES(@id,1,@id);
            INSERT INTO annal_heads VALUES(@id,@id,1);
            INSERT INTO annal_versions VALUES(@id,@id,@origin,@time,@valid,NULL,1,X'00',1,@scope,@campaign,0);
            """;

        command.Parameters.AddWithValue("@id", id);

        command.Parameters.AddWithValue("@time", UtcInstantText.Format(old ? SagaInstant.AddDays(-500) : SagaInstant));

        command.Parameters.AddWithValue("@retired", retired ? UtcInstantText.Format(SagaInstant) : DBNull.Value);

        command.Parameters.AddWithValue("@scope", campaign is null ? 1 : 2);

        command.Parameters.AddWithValue("@campaign", (object?)campaign ?? DBNull.Value);

        command.Parameters.AddWithValue("@source", source);

        command.Parameters.AddWithValue("@vector", EmbeddingBlobCodec.Encode(vector));

        command.Parameters.AddWithValue("@compact", QuantizedEmbeddingCodec.Encode(vector, Encoding.UTF8.GetBytes(id))!);

        command.Parameters.AddWithValue("@origin", origin);

        command.Parameters.AddWithValue("@valid", UtcInstantText.Format(future ? SagaInstant.AddDays(1) : SagaInstant.AddDays(-600)));

        await command.ExecuteNonQueryAsync();
    }

    private sealed class SagaTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => SagaInstant;
    }
}
