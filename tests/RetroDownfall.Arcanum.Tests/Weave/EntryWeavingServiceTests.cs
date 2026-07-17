using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave;

[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class EntryWeavingServiceTests : IAsyncLifetime
{

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    public EntryWeavingServiceTests(GrimoireFixture fixture)
    {

        _fixture = fixture;

    }

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
    public async Task RunTickAsync_EmbedsUnembeddedEntries_AndSkipsAlreadyEmbeddedOnNextTick()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        Guid entryOneId = await CreateEntryAsync(sessionId, "first entry content");

        Guid entryTwoId = await CreateEntryAsync(sessionId, "second entry content");

        FakeWeaveService weave = new();

        EntryWeavingService service = CreateService(weave, out EmbeddingSettings embeddings);

        await service.RunTickAsync(embeddings, CancellationToken.None);

        Assert.Equal(2, await CountEntryEmbeddingsAsync());

        Assert.Equal(1, weave.EmbedBatchCallCount);

        // Second tick: the LEFT JOIN excludes both already-embedded rows, so no new embed call happens.
        await service.RunTickAsync(embeddings, CancellationToken.None);

        Assert.Equal(1, weave.EmbedBatchCallCount);

        Assert.Equal(2, await CountEntryEmbeddingsAsync());

        _ = entryOneId;

        _ = entryTwoId;

    }

    [SkippableFact]
    public async Task RunTickAsync_SkipsEmptyContentEntries()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        await CreateEntryAsync(sessionId, "   ");

        await CreateEntryAsync(sessionId, "real content");

        FakeWeaveService weave = new();

        EntryWeavingService service = CreateService(weave, out EmbeddingSettings embeddings);

        await service.RunTickAsync(embeddings, CancellationToken.None);

        Assert.Equal(1, await CountEntryEmbeddingsAsync());

        Assert.Single(weave.LastBatch!);

        Assert.Equal("real content", weave.LastBatch![0]);

    }

    [SkippableFact]
    public async Task RunTickAsync_TruncatesContentToChunkSizeChars()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        const int chunkSize = 128;

        string longContent = new('x', chunkSize + 200);

        await CreateEntryAsync(sessionId, longContent);

        FakeWeaveService weave = new();

        EntryWeavingService service = CreateService(weave, out EmbeddingSettings embeddings, chunkSizeChars: chunkSize);

        await service.RunTickAsync(embeddings, CancellationToken.None);

        Assert.Single(weave.LastBatch!);

        Assert.Equal(chunkSize, weave.LastBatch![0].Length);

        Assert.Equal(new string('x', chunkSize), weave.LastBatch![0]);

    }

    [SkippableFact]
    public async Task RunTickAsync_EmbeddingFailure_LogsAndContinues_WritesNoRows()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        await CreateEntryAsync(sessionId, "content that will fail to embed");

        FakeWeaveService weave = new() { FailNextBatch = true };

        EntryWeavingService service = CreateService(weave, out EmbeddingSettings embeddings);

        // Never throws — the tick logs a warning and returns without writing any rows.
        await service.RunTickAsync(embeddings, CancellationToken.None);

        Assert.Equal(0, await CountEntryEmbeddingsAsync());

    }

    [SkippableFact]
    public async Task RunTickAsync_RespectsBatchSizeAcrossMultipleTicks()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        for (int i = 0; i < 5; i++)
        {

            await CreateEntryAsync(sessionId, $"entry number {i}");

        }

        FakeWeaveService weave = new();

        EntryWeavingService service = CreateService(weave, out EmbeddingSettings embeddings, batchSize: 2);

        await service.RunTickAsync(embeddings, CancellationToken.None);

        Assert.Equal(2, await CountEntryEmbeddingsAsync());

        await service.RunTickAsync(embeddings, CancellationToken.None);

        Assert.Equal(4, await CountEntryEmbeddingsAsync());

        await service.RunTickAsync(embeddings, CancellationToken.None);

        Assert.Equal(5, await CountEntryEmbeddingsAsync());

    }

    [SkippableFact]
    public async Task ExecuteAsync_IdlesWhenDisabled_NeverCallsEmbedBatch()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        await CreateEntryAsync(sessionId, "should not be embedded while disabled");

        FakeWeaveService weave = new();

        ArcanumSettings disabledSettings = new()
        {
            Embeddings = new EmbeddingSettings
            {
                Enabled = false,
                SessionSearchEnabled = false,
            },
        };

        IServiceScopeFactory scopeFactory = BuildScopeFactory();

        EntryWeavingService service = new(
            new TestOptionsMonitor<ArcanumSettings>(disabledSettings),
            weave,
            new WeaveIndexAvailability(),
            scopeFactory,
            NullLogger<EntryWeavingService>.Instance);

        IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        await Task.Delay(TimeSpan.FromMilliseconds(300));

        await hosted.StopAsync(CancellationToken.None);

        Assert.Equal(0, weave.EmbedBatchCallCount);

        Assert.Equal(0, await CountEntryEmbeddingsAsync());

    }

    [SkippableFact]
    public async Task ExecuteAsync_TickThrowsRepeatedly_BacksOffInsteadOfTightLooping()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync();

        await CreateEntryAsync(sessionId, "entry that will never successfully embed");

        FakeWeaveService weave = new() { ThrowOnEmbed = true };

        EntryWeavingService service = CreateService(weave, out _);

        IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        await Task.Delay(TimeSpan.FromMilliseconds(300));

        await hosted.StopAsync(CancellationToken.None);

        // Without a backoff after a failed tick, this would spin as fast as the CPU allows within
        // 300ms — dozens or hundreds of calls. With the 1s backoff, at most one or two ticks can run.
        Assert.True(
            weave.EmbedBatchCallCount <= 2,
            $"Expected at most 2 tick attempts within 300ms given a 1s backoff after failure; got {weave.EmbedBatchCallCount}.");

    }

    private EntryWeavingService CreateService(
        FakeWeaveService weave,
        out EmbeddingSettings embeddings,
        int batchSize = 32,
        int chunkSizeChars = 1000)
    {

        embeddings = new EmbeddingSettings
        {
            Enabled = true,
            SessionSearchEnabled = true,
            Provider = "test",
            Model = "test-embed",
            BatchSize = batchSize,
            ChunkSizeChars = chunkSizeChars,
        };

        IServiceScopeFactory scopeFactory = BuildScopeFactory();

        return new EntryWeavingService(
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings { Embeddings = embeddings }),
            weave,
            new WeaveIndexAvailability(),
            scopeFactory,
            NullLogger<EntryWeavingService>.Instance);

    }

    private IServiceScopeFactory BuildScopeFactory()
    {

        ServiceCollection services = new();

        services.AddSingleton(_db!);

        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

    }

    private async Task<Guid> CreateSessionAsync()
    {

        Session session = new()
        {
            Id = Guid.NewGuid(),
            Status = "active",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        _db!.Sessions.Add(session);

        await _db.SaveChangesAsync();

        return session.Id;

    }

    private async Task<Guid> CreateEntryAsync(Guid sessionId, string content)
    {

        Entry entry = new()
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            Role = MessageRole.User,
            Content = content,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _db!.Entries.Add(entry);

        await _db.SaveChangesAsync();

        return entry.Id;

    }

    private async Task<int> CountEntryEmbeddingsAsync()
    {

        DbConnection connection = _db!.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {

            await connection.OpenAsync();

        }

        await using DbCommand cmd = connection.CreateCommand();

        cmd.CommandText = """SELECT COUNT(*) FROM "entry_embeddings";""";

        object? result = await cmd.ExecuteScalarAsync();

        return Convert.ToInt32(result);

    }

    private sealed class FakeWeaveService : IWeaveService
    {

        public bool FailNextBatch { get; set; }

        public bool ThrowOnEmbed { get; set; }

        public int EmbedBatchCallCount { get; private set; }

        public List<string>? LastBatch { get; private set; }

        public bool IsAvailable => true;

        public Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken) =>
            throw new NotSupportedException("EntryWeavingService only calls EmbedBatchAsync.");

        public Task<Result<Embedding<float>[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        {

            EmbedBatchCallCount++;

            LastBatch = [.. texts];

            if (ThrowOnEmbed)
            {

                throw new InvalidOperationException("Simulated unexpected tick failure.");

            }

            if (FailNextBatch)
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
            throw new NotSupportedException("Not used by EntryWeavingService.");

    }

}
