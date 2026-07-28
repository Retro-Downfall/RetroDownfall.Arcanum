using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave;

[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class WorkspaceIndexingServiceTests : IAsyncLifetime
{

    private readonly GrimoireFixture _fixture;

    private readonly TempWorkspace _workspace = new();

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    public WorkspaceIndexingServiceTests(GrimoireFixture fixture)
    {

        _fixture = fixture;

    }

    public async Task InitializeAsync()
    {

        await _workspace.InitializeAsync();

        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

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

        await _workspace.DisposeAsync();

    }

    [SkippableFact]
    public async Task IndexWorkspaceAsync_OnlyIndexesConfiguredExtensions()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _workspace.WriteFile("src/Foo.cs", "public class Foo {}");

        _workspace.WriteFile("assets/logo.png", "not really a png but has the wrong extension");

        FakeWeaveService weave = new();

        WorkspaceIndexingService service = CreateService(weave, out EmbeddingSettings embeddings);

        await service.IndexWorkspaceAsync(_workspace.Root, embeddings, CancellationToken.None);

        List<string> indexedPaths = await GetIndexedRelativePathsAsync();

        Assert.Contains("src/Foo.cs".Replace('/', Path.DirectorySeparatorChar), indexedPaths);

        Assert.DoesNotContain("assets/logo.png".Replace('/', Path.DirectorySeparatorChar), indexedPaths);

    }

    [SkippableFact]
    public async Task IndexWorkspaceAsync_SkipsFilesLargerThanMaxFileSizeChars()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _workspace.WriteFile("small.txt", "short content");

        int maxFileSizeChars = ArcanumSettingClamps.EmbeddingsCodebaseMaxFileSizeChars(
            ArcanumRuntimeDefaults.Embeddings.Codebase.MaxFileSizeChars);
        _workspace.WriteFile("big.txt", new string('x', maxFileSizeChars + 1));

        FakeWeaveService weave = new();

        WorkspaceIndexingService service = CreateService(weave, out EmbeddingSettings embeddings);

        await service.IndexWorkspaceAsync(_workspace.Root, embeddings, CancellationToken.None);

        List<string> indexedPaths = await GetIndexedRelativePathsAsync();

        Assert.Contains("small.txt", indexedPaths);

        Assert.DoesNotContain("big.txt", indexedPaths);

    }

    [SkippableFact]
    public async Task IndexWorkspaceAsync_SkipsUnchangedFiles_ReindexesModifiedFiles()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string fullPath = _workspace.WriteFile("notes.md", "version one");

        FakeWeaveService weave = new();

        WorkspaceIndexingService service = CreateService(weave, out EmbeddingSettings embeddings);

        await service.IndexWorkspaceAsync(_workspace.Root, embeddings, CancellationToken.None);

        Assert.Equal(1, weave.EmbedBatchCallCount);

        // Unchanged: a second tick performs no new embedding work.
        await service.IndexWorkspaceAsync(_workspace.Root, embeddings, CancellationToken.None);

        Assert.Equal(1, weave.EmbedBatchCallCount);

        // Ensure the filesystem's LastWriteTimeUtc resolution (some platforms are 1s-granular)
        // actually advances before rewriting.
        await Task.Delay(1100);

        File.WriteAllText(fullPath, "version two, with different content");

        await service.IndexWorkspaceAsync(_workspace.Root, embeddings, CancellationToken.None);

        Assert.Equal(2, weave.EmbedBatchCallCount);

        List<string> contents = await GetChunkContentsAsync("notes.md");

        Assert.Contains("version two", string.Join(' ', contents));

    }

    [SkippableFact]
    public async Task IndexWorkspaceAsync_ChunksEmbedsAndPersistsFile()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _workspace.WriteFile("README.md", "# Title\n\nSome documentation content.");

        FakeWeaveService weave = new();

        WorkspaceIndexingService service = CreateService(weave, out EmbeddingSettings embeddings);

        await service.IndexWorkspaceAsync(_workspace.Root, embeddings, CancellationToken.None);

        int chunkCount = await CountRowsAsync("workspace_file_chunks");

        int embeddingCount = await CountRowsAsync("workspace_file_embeddings");

        Assert.Equal(1, chunkCount);

        Assert.Equal(1, embeddingCount);

    }

    [SkippableFact]
    public async Task IndexWorkspaceAsync_EmbeddingFailureForOneFile_ContinuesWithOthers()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _workspace.WriteFile("a.txt", "alpha content");

        _workspace.WriteFile("b.txt", "beta content");

        FakeWeaveService weave = new() { FailForContentContaining = "alpha" };

        WorkspaceIndexingService service = CreateService(weave, out EmbeddingSettings embeddings);

        await service.IndexWorkspaceAsync(_workspace.Root, embeddings, CancellationToken.None);

        List<string> indexedPaths = await GetIndexedRelativePathsAsync();

        Assert.DoesNotContain("a.txt", indexedPaths);

        Assert.Contains("b.txt", indexedPaths);

    }

    [SkippableFact]
    public async Task IndexWorkspaceAsync_PrunesIgnoredDirectories_NeverDescendsIntoThem()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _workspace.WriteFile("src/Foo.cs", "public class Foo {}");

        // Matches the configured extension but lives under an ignored directory segment — the walk
        // must prune node_modules before recursing into it, not just filter this file out afterward.
        _workspace.WriteFile("node_modules/pkg/index.js".Replace('/', Path.DirectorySeparatorChar), "console.log('nope');");

        FakeWeaveService weave = new();

        WorkspaceIndexingService service = CreateService(weave, out EmbeddingSettings embeddings);

        await service.IndexWorkspaceAsync(_workspace.Root, embeddings, CancellationToken.None);

        List<string> indexedPaths = await GetIndexedRelativePathsAsync();

        Assert.Contains("src/Foo.cs".Replace('/', Path.DirectorySeparatorChar), indexedPaths);

        Assert.DoesNotContain("node_modules/pkg/index.js".Replace('/', Path.DirectorySeparatorChar), indexedPaths);

    }

    [SkippableFact]
    public async Task IndexWorkspaceAsync_FileDeletedSinceLastIndex_RemovesOrphanedChunks()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _workspace.WriteFile("kept.md", "still here");

        string removedFile = _workspace.WriteFile("removed.md", "will be deleted");

        FakeWeaveService weave = new();

        WorkspaceIndexingService service = CreateService(weave, out EmbeddingSettings embeddings);

        await service.IndexWorkspaceAsync(_workspace.Root, embeddings, CancellationToken.None);

        List<string> indexedAfterFirstTick = await GetIndexedRelativePathsAsync();

        Assert.Contains("kept.md", indexedAfterFirstTick);

        Assert.Contains("removed.md", indexedAfterFirstTick);

        File.Delete(removedFile);

        await service.IndexWorkspaceAsync(_workspace.Root, embeddings, CancellationToken.None);

        List<string> indexedAfterSecondTick = await GetIndexedRelativePathsAsync();

        Assert.Contains("kept.md", indexedAfterSecondTick);

        Assert.DoesNotContain("removed.md", indexedAfterSecondTick);

        // The BLOB/vec embedding rows must be cleaned up alongside the chunk row (no orphaned
        // embeddings left pointing at a ChunkId whose chunk metadata no longer exists).
        Assert.Equal(1, await CountRowsAsync("workspace_file_embeddings"));

    }

    [SkippableFact]
    public async Task IndexWorkspaceAsync_NeverIndexesSymlinkEscapingWorkspace()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {

            return;

        }

        string outsideFile = Path.Combine(Path.GetTempPath(), $"arcanum-outside-{Guid.NewGuid():N}.md");

        await File.WriteAllTextAsync(outsideFile, "outside secret content");

        try
        {

            string linkPath = Path.Combine(_workspace.Root, "escape-link.md");

            File.CreateSymbolicLink(linkPath, outsideFile);

            FakeWeaveService weave = new();

            WorkspaceIndexingService service = CreateService(weave, out EmbeddingSettings embeddings);

            await service.IndexWorkspaceAsync(_workspace.Root, embeddings, CancellationToken.None);

            // Rejected at the pre-check (WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck) —
            // the outside file's content must never reach the embedding provider or persisted chunks.
            Assert.Equal(0, weave.EmbedBatchCallCount);

            Assert.Equal(0, await CountRowsAsync("workspace_file_chunks"));

        }
        finally
        {

            File.Delete(outsideFile);

        }

    }

    [SkippableFact]
    public async Task ExecuteAsync_IdlesWhenDisabled_NeverIndexesRegisteredWorkspace()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _workspace.WriteFile("idle.txt", "should not be indexed while disabled");

        FakeWeaveService weave = new();

        ArcanumSettings disabledSettings = new()
        {
            Features = new FeatureSettings
            {
                Embeddings = false,
                CodebaseRetrieval = false,
            },
        };

        IServiceScopeFactory scopeFactory = BuildScopeFactory();

        WorkspaceIndexingService service = new(
            new TestOptionsMonitor<ArcanumSettings>(disabledSettings),
            weave,
            new WeaveIndexAvailability(),
            scopeFactory,
            NullLogger<WorkspaceIndexingService>.Instance);

        service.RegisterWorkspace(_workspace.Root);

        IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        await Task.Delay(TimeSpan.FromMilliseconds(300));

        await hosted.StopAsync(CancellationToken.None);

        Assert.Equal(0, weave.EmbedBatchCallCount);

        Assert.Equal(0, await CountRowsAsync("workspace_file_chunks"));

    }

    [SkippableFact]
    public async Task IndexNowAsync_IndexesFile_WhenWorkspaceUnderAllowedCampaignRoot()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _workspace.WriteFile("allowed.md", "content under an allowed root");

        FakeWeaveService weave = new();

        WorkspaceIndexingService service = CreateService(weave, out _, campaignAllowedRoots: [_workspace.Root]);

        await service.IndexNowAsync(_workspace.Root, CancellationToken.None);

        List<string> indexedPaths = await GetIndexedRelativePathsAsync();

        Assert.Contains("allowed.md", indexedPaths);

    }

    [SkippableFact]
    public async Task IndexNowAsync_RejectsWorkspace_WhenNotUnderAnyAllowedCampaignRoot()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _workspace.WriteFile("secret.md", "should never be indexed");

        FakeWeaveService weave = new();

        // Empty Security.CampaignRoots is secure-by-default (WorkspaceRootPolicy.EnforceAllowedRoots
        // denies everything), so this call must be a graceful no-op rather than indexing an
        // unvalidated directory — see CampaignPathPolicy.ValidateAndNormalizePath.
        WorkspaceIndexingService service = CreateService(weave, out _, campaignAllowedRoots: []);

        await service.IndexNowAsync(_workspace.Root, CancellationToken.None);

        Assert.Equal(0, weave.EmbedBatchCallCount);

        Assert.Equal(0, await CountRowsAsync("workspace_file_chunks"));

    }

    [SkippableFact]
    public async Task ExecuteAsync_NeverIndexesRegisteredWorkspace_WhenNotUnderAnyAllowedCampaignRoot()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        _workspace.WriteFile("secret.md", "should never be indexed via the background tick either");

        FakeWeaveService weave = new();

        WorkspaceIndexingService service = CreateService(weave, out _, campaignAllowedRoots: []);

        // RegisterWorkspace must silently reject the path (Security.CampaignRoots is empty above),
        // so the background tick never has this workspace queued for indexing.
        service.RegisterWorkspace(_workspace.Root);

        IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        await Task.Delay(TimeSpan.FromMilliseconds(300));

        await hosted.StopAsync(CancellationToken.None);

        Assert.Equal(0, weave.EmbedBatchCallCount);

        Assert.Equal(0, await CountRowsAsync("workspace_file_chunks"));

    }

    [SkippableFact]
    public void RegisterWorkspace_IsThreadSafe_UnderConcurrentCalls()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FakeWeaveService weave = new();

        WorkspaceIndexingService service = CreateService(weave, out _);

        string[] paths = Enumerable.Range(0, 50)
            .Select(i => Path.Combine(_workspace.Root, $"workspace-{i}"))
            .ToArray();

        Parallel.ForEach(paths, service.RegisterWorkspace);

        // Re-registering an already-known path (idempotent) exercises the concurrent-write path once more.
        Parallel.ForEach(paths, service.RegisterWorkspace);

    }

    private WorkspaceIndexingService CreateService(
        FakeWeaveService weave,
        out EmbeddingSettings embeddings,
        string[]? campaignAllowedRoots = null)
    {

        embeddings = ArcanumRuntimeDefaults.Embeddings;

        IServiceScopeFactory scopeFactory = BuildScopeFactory();

        ArcanumSettings settings = new()
        {
            Features = new FeatureSettings
            {
                Embeddings = true,
                CodebaseRetrieval = true,
            },
            Integrations = new IntegrationSettings
            {
                Embeddings = new EmbeddingIntegrationSettings
                {
                    Provider = "test",
                    Model = "test-embed",
                },
            },
            Security = new SecuritySettings
            {
                CampaignRoots = campaignAllowedRoots ?? [_workspace.Root],
            },
        };

        return new WorkspaceIndexingService(
            new TestOptionsMonitor<ArcanumSettings>(settings),
            weave,
            new WeaveIndexAvailability(),
            scopeFactory,
            NullLogger<WorkspaceIndexingService>.Instance);

    }

    private IServiceScopeFactory BuildScopeFactory()
    {

        ServiceCollection services = new();

        services.AddSingleton(_db!);

        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

    }

    private async Task<List<string>> GetIndexedRelativePathsAsync()
    {

        DbConnection connection = _db!.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {

            await connection.OpenAsync();

        }

        await using DbCommand cmd = connection.CreateCommand();

        cmd.CommandText = """SELECT DISTINCT "RelativePath" FROM "workspace_file_chunks";""";

        List<string> results = [];

        await using DbDataReader reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {

            results.Add(reader.GetString(0));

        }

        return results;

    }

    private async Task<List<string>> GetChunkContentsAsync(string relativePath)
    {

        DbConnection connection = _db!.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {

            await connection.OpenAsync();

        }

        await using DbCommand cmd = connection.CreateCommand();

        cmd.CommandText = """SELECT "Content" FROM "workspace_file_chunks" WHERE "RelativePath" = @relativePath;""";

        DbParameter param = cmd.CreateParameter();

        param.ParameterName = "@relativePath";

        param.Value = relativePath;

        cmd.Parameters.Add(param);

        List<string> results = [];

        await using DbDataReader reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {

            results.Add(reader.GetString(0));

        }

        return results;

    }

    private async Task<int> CountRowsAsync(string tableName)
    {

        DbConnection connection = _db!.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {

            await connection.OpenAsync();

        }

        await using DbCommand cmd = connection.CreateCommand();

        cmd.CommandText = $"""SELECT COUNT(*) FROM "{tableName}";""";

        object? result = await cmd.ExecuteScalarAsync();

        return Convert.ToInt32(result);

    }

    private sealed class FakeWeaveService : IWeaveService
    {

        public string? FailForContentContaining { get; set; }

        public int EmbedBatchCallCount { get; private set; }

        public bool IsAvailable => true;

        public Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken) =>
            throw new NotSupportedException("WorkspaceIndexingService only calls EmbedBatchAsync.");

        public Task<Result<Embedding<float>[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        {

            EmbedBatchCallCount++;

            if (FailForContentContaining is { } needle && texts.Any(t => t.Contains(needle, StringComparison.Ordinal)))
            {

                return Task.FromResult(Result<Embedding<float>[]>.Failure(
                    new Error(ErrorCodes.Embeddings.ProviderUnavailable, "Simulated embedding failure.")));

            }

            Embedding<float>[] generated = new Embedding<float>[texts.Count];

            for (int i = 0; i < texts.Count; i++)
            {

                generated[i] = new Embedding<float>(new float[] { 1f, 0f, 0f });

            }

            return Task.FromResult(Result<Embedding<float>[]>.Success(generated));

        }

        public Task<Result<(string Chunk, int Offset)[]>> ChunkAsync(string text, CancellationToken cancellationToken) =>
            Task.FromResult(Result<(string Chunk, int Offset)[]>.Success(
                string.IsNullOrEmpty(text) ? [] : [(text, 0)]));

    }

}
