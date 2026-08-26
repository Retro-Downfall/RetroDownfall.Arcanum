using System.Globalization;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Lexicon;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Annals;

/// <summary>
/// A claim describes a memory that exists. When the operator forgets the memory, the claim goes with it,
/// in the same transaction, through the store the operator actually reaches.
/// </summary>
/// <remarks>
/// The Annals deliberately has no delete guard. Keeping a claim after its subject was erased would leave
/// a record pointing at content the operator asked to remove, which is the opposite of what an
/// append-only substrate is for.
/// </remarks>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class AnnalsErasureTests : IAsyncLifetime
{

    private const int TestDimensions = 64;

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    public AnnalsErasureTests(GrimoireFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {

        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        return Task.CompletedTask;

    }

    public async Task DisposeAsync()
    {

        if (_db is not null)
        {

            await _db.DisposeAsync();

        }

        if (File.Exists(_dbPath))
        {

            File.Delete(_dbPath);

        }

    }

    [SkippableFact]
    public async Task Deleting_one_saga_memory_removes_its_claim_and_leaves_every_other_claim_standing()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ISagaMemoryStore store = CreateSagaStore(annals: true);

        await InsertMemoryAsync(store, "mem-doomed", "a conclusion to forget");

        await InsertMemoryAsync(store, "mem-kept", "a conclusion to keep");

        Assert.True(await store.DeleteAsync("mem-doomed", CancellationToken.None));

        Assert.Equal(0, await CountClaimsAsync(1, "mem-doomed"));

        Assert.Equal(1, await CountClaimsAsync(1, "mem-kept"));

        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM annal_versions;"));

        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM annal_heads;"));

    }

    [SkippableFact]
    public async Task Resetting_the_whole_saga_store_leaves_no_saga_claim_and_leaves_lexicon_claims_untouched()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ISagaMemoryStore store = CreateSagaStore(annals: true);

        await InsertMemoryAsync(store, "mem-1", "one");

        await InsertMemoryAsync(store, "mem-2", "two");

        ILexiconService lexicon = CreateLexiconService(annals: true);

        _ = await lexicon.UpsertAsync("config", "Project", ["a fact"], LexiconScope.Global, CancellationToken.None);

        await store.DeleteAllAsync(CancellationToken.None);

        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM annal_claims WHERE SubjectStoreCode = 1;"));

        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM annal_claims WHERE SubjectStoreCode = 2;"));

        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM annal_versions;"));

        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM annal_heads;"));

    }

    /// <summary>
    /// A corrected entity has more than one version and a dependency edge between them; deleting it must
    /// take the whole chain, not just the head.
    /// </summary>
    [SkippableFact]
    public async Task Deleting_one_lexicon_entity_removes_its_claim_and_every_revision_of_it()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ILexiconService lexicon = CreateLexiconService(annals: true);

        _ = await lexicon.UpsertAsync("config", "Project", ["first"], LexiconScope.Global, CancellationToken.None);

        _ = await lexicon.UpsertAsync("config", "Project", ["second"], LexiconScope.Global, CancellationToken.None);

        _ = await lexicon.UpsertAsync("other", "Project", ["kept"], LexiconScope.Global, CancellationToken.None);

        Assert.Equal(3, await CountAsync("SELECT COUNT(*) FROM annal_versions;"));

        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM annal_dependencies;"));

        Result<bool> deleted = await lexicon.DeleteByNameAsync("config", LexiconScope.Global, CancellationToken.None);

        Assert.True(deleted.IsSuccess && deleted.Value);

        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM annal_claims;"));

        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM annal_versions;"));

        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM annal_heads;"));

        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM annal_dependencies;"));

    }

    /// <summary>
    /// Erasure is not gated. A claim written while the Annals was on has to stay removable after it is
    /// turned off, or disabling the feature would strand records no surface can reach and no reset can
    /// clear.
    /// </summary>
    [SkippableFact]
    public async Task A_claim_written_while_the_gate_was_on_is_deleted_after_the_gate_is_turned_off()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await InsertMemoryAsync(CreateSagaStore(annals: true), "mem-stranded", "a conclusion");

        Assert.Equal(1, await CountClaimsAsync(1, "mem-stranded"));

        ISagaMemoryStore afterDisable = CreateSagaStore(annals: false);

        Assert.True(await afterDisable.DeleteAsync("mem-stranded", CancellationToken.None));

        Assert.Equal(0, await CountClaimsAsync(1, "mem-stranded"));

    }

    private static Task InsertMemoryAsync(ISagaMemoryStore store, string id, string content) =>
        store.InsertAsync(
            id,
            content,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
            sessionId: null,
            null,
            "extraction",
            new float[TestDimensions],
            CancellationToken.None);

    private ISagaMemoryStore CreateSagaStore(bool annals) =>
        new SagaMemoryStore(
            _db!,
            new WeaveIndexAvailability(),
            new TestOptionsMonitor<ArcanumSettings>(
                new ArcanumSettings
                {
                    Features = new FeatureSettings { Annals = annals },
                    Integrations = new IntegrationSettings
                    {
                        Embeddings = new EmbeddingIntegrationSettings { Dimensions = TestDimensions },
                    },
                }));

    private ILexiconService CreateLexiconService(bool annals) =>
        new LexiconService(
            _db!,
            NullLogger<LexiconService>.Instance,
            new TestOptionsMonitor<ArcanumSettings>(
                new ArcanumSettings { Features = new FeatureSettings { Annals = annals } }));

    private Task<int> CountClaimsAsync(int subjectStoreCode, string subjectId) =>
        CountAsync(
            $"SELECT COUNT(*) FROM annal_claims WHERE SubjectStoreCode = {subjectStoreCode} AND SubjectId = '{subjectId}';");

    private async Task<int> CountAsync(string sql)
    {

        if (_db!.Database.GetDbConnection().State != System.Data.ConnectionState.Open)
        {

            await _db.Database.OpenConnectionAsync(CancellationToken.None);

        }

        await using SqliteCommand command = (SqliteCommand)_db.Database.GetDbConnection().CreateCommand();

        command.CommandText = sql;

        return Convert.ToInt32(
            await command.ExecuteScalarAsync(CancellationToken.None),
            CultureInfo.InvariantCulture);

    }

}
