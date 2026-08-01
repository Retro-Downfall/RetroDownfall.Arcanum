using System.Text;

using Microsoft.Extensions.AI;

using Microsoft.Extensions.Logging.Abstractions;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Storage.Entities;

using RetroDownfall.Arcanum.Core.TheForge;

using RetroDownfall.Arcanum.Core.Weave;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Repositories;

using RetroDownfall.Arcanum.Infrastructure.Storage;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave;

[Collection("Grimoire")]

[Trait("Category", "Integration")]

public sealed class SessionAttachmentIndexingTests : IAsyncLifetime
{

    private const int Dimensions = 64;

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private string _attachmentsRoot = string.Empty;

    private ArcanumDbContext? _db;

    private ArcanumSettings _settings = null!;

    private SessionAttachmentStore? _attachments;

    private SessionAttachmentIndexRepository? _index;

    public SessionAttachmentIndexingTests(GrimoireFixture fixture)
    {

        _fixture = fixture;

    }

    public Task InitializeAsync()
    {

        _dbPath = _fixture.CopyDatabase();

        _attachmentsRoot = Path.Combine(
            Path.GetTempPath(),
            "arcanum-attachment-index-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_attachmentsRoot);

        _db = _fixture.CreateContext(_dbPath);

        _settings = CreateSettings();

        _attachments = new SessionAttachmentStore(
            _db,
            Options.Create(_settings),
            _attachmentsRoot,
            TestEncryptedBlobStore.Create());

        _index = new SessionAttachmentIndexRepository(
            _db,
            new WeaveIndexAvailability());

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

        if (Directory.Exists(_attachmentsRoot))
        {

            Directory.Delete(_attachmentsRoot, recursive: true);

        }

    }

    [SkippableFact]

    public async Task PersistAndPromote_EnqueueOnlyNewBoundVersions()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        RecordingQueue queue = new();

        SessionAttachmentStore store = new(
            _db!,
            Options.Create(_settings),
            _attachmentsRoot,
            TestEncryptedBlobStore.Create(),
            indexQueue: queue);

        Guid sessionId = Guid.NewGuid();

        byte[] bytes = Encoding.UTF8.GetBytes("event driven");

        SessionAttachmentRecord first = await store.PersistNewAsync(
            sessionId,
            null,
            null,
            "event",
            "event.txt",
            bytes,
            "text/plain",
            SessionAttachmentKind.Text);

        SessionAttachmentRecord duplicate = await store.PersistNewAsync(
            sessionId,
            null,
            null,
            "event",
            "event.txt",
            bytes,
            "text/plain",
            SessionAttachmentKind.Text);

        Assert.Equal(first.Id, duplicate.Id);

        Assert.Single(queue.Requests);

        SessionAttachmentRecord refreshed = await store.PersistNewAsync(
            sessionId,
            null,
            null,
            "event",
            "event.txt",
            Encoding.UTF8.GetBytes("event driven refresh"),
            "text/plain",
            SessionAttachmentKind.Text);

        Assert.Equal(2, refreshed.Version);

        Assert.Equal(2, queue.Requests.Count);

        string pendingTurn = Guid.NewGuid().ToString("N");

        SessionAttachmentRecord pending = await store.PersistNewAsync(
            null,
            pendingTurn,
            null,
            "pending",
            "pending.txt",
            Encoding.UTF8.GetBytes("pending content"),
            "text/plain",
            SessionAttachmentKind.Text);

        Assert.Equal(2, queue.Requests.Count);

        await store.PromotePendingAsync(pendingTurn, sessionId, null);

        Assert.Equal(3, queue.Requests.Count);

        Assert.Contains(
            queue.Requests,
            request => request.AttachmentId == pending.Id && request.SessionId == sessionId);

    }

    [SkippableFact]

    public async Task ForkAsync_EnqueuesCopiedAttachmentAfterCommit()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        RecordingQueue queue = new();

        SessionRepository sessions = new(
            _db!,
            _attachments!,
            new TestOptionsMonitor<ArcanumSettings>(_settings),
            queue);

        Session source = await sessions.CreateAsync(
            campaignId: null,
            title: "Attachment source",
            CancellationToken.None);

        _ = await PersistAsync(
            source.Id,
            "notes",
            "notes.txt",
            "text/plain",
            "fork me");

        Result<Session> result = await sessions.ForkAsync(
            source.Id,
            new ForkSessionRequest(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        IReadOnlyList<SessionAttachmentRecord> copied = await _attachments!.ListBoundAsync(
            result.Value.Id,
            CancellationToken.None);

        SessionAttachmentRecord forked = Assert.Single(copied);

        SessionAttachmentIndexRequest request = Assert.Single(queue.Requests);

        Assert.Equal(forked.Id, request.AttachmentId);

        Assert.Equal(result.Value.Id, request.SessionId);

    }

    [SkippableFact]

    public async Task PurgeSessionAsync_RemovesAttachmentChunksEmbeddingsAndState()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        GrimoireRepository repository = new(
            _db!,
            _attachments!,
            NullLogger<GrimoireRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(_settings),
            _index);

        (Guid sessionId, _) = await repository.BeginAssistantReplyAsync(
            sessionId: null,
            prompt: "index then purge",
            model: "test-model",
            cancellationToken: CancellationToken.None);

        SessionAttachmentRecord attachment = await PersistAsync(
            sessionId,
            "notes",
            "notes.txt",
            "text/plain",
            "purge me");

        _ = await CreateProcessor(new FakeWeaveService()).ProcessAsync(
            new(attachment.Id, sessionId),
            CancellationToken.None);

        Assert.NotEmpty(await _index!.GetChunksForAttachmentAsync(
            attachment.Id,
            CancellationToken.None));

        Assert.Equal(1, await repository.PurgeSessionAsync(sessionId, CancellationToken.None));

        Assert.Empty(await _index.GetChunksForAttachmentAsync(
            attachment.Id,
            CancellationToken.None));

        Assert.Empty(await _index.GetStatusesAsync(
            [attachment.Id],
            CancellationToken.None));

    }

    [SkippableFact]

    public async Task ProcessAsync_TextAttachment_PersistsProvenanceAndIndexedStatus()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord attachment = await PersistAsync(
            sessionId,
            "notes",
            "notes.md",
            "text/markdown",
            "alpha\nbeta\ngamma");

        SessionAttachmentIndexProcessor processor = CreateProcessor(new FakeWeaveService());

        SessionAttachmentIndexOutcome outcome = await processor.ProcessAsync(
            new SessionAttachmentIndexRequest(attachment.Id, sessionId),
            CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexStatus.Indexed, outcome.Status);

        Assert.False(outcome.ShouldRetry);

        SessionAttachmentIndexState state = await _index!.GetStateAsync(
            attachment.Id,
            CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexStatus.Indexed, state.Status);

        SessionAttachmentIndexedChunk[] chunks = await _index.GetChunksForAttachmentAsync(
            attachment.Id,
            CancellationToken.None);

        Assert.NotEmpty(chunks);

        Assert.All(chunks, chunk =>
        {

            Assert.Equal(sessionId, chunk.SessionId);

            Assert.Equal(attachment.Id, chunk.AttachmentId);

            Assert.Equal("notes", chunk.LogicalKey);

            Assert.Equal(1, chunk.Version);

            Assert.Equal("notes.md", chunk.OriginalFileName);

            Assert.Equal("text/markdown", chunk.MimeType);

            Assert.Equal(attachment.ContentSha256, chunk.ContentSha256);

            Assert.Equal(Dimensions, chunk.EmbeddingDimension);

        });

    }

    [SkippableFact]

    public async Task ProcessAsync_UnsupportedBinary_MarksNotEligibleWithoutEmbedding()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord attachment = await _attachments!.PersistNewAsync(
            sessionId,
            null,
            null,
            "binary",
            "binary.pdf",
            new byte[] { 0x00, 0x01, 0x02 },
            "application/pdf",
            SessionAttachmentKind.Text);

        FakeWeaveService weave = new();

        SessionAttachmentIndexOutcome outcome = await CreateProcessor(weave).ProcessAsync(
            new SessionAttachmentIndexRequest(attachment.Id, sessionId),
            CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexStatus.NotEligible, outcome.Status);

        Assert.Equal(0, weave.EmbedBatchCallCount);

        Assert.Empty(await _index!.GetChunksForAttachmentAsync(attachment.Id, CancellationToken.None));

    }

    [SkippableFact]

    public async Task ProcessAsync_EmbeddingFailure_MarksFailedAndRequestsRetry()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord attachment = await PersistAsync(
            sessionId,
            "notes",
            "notes.txt",
            "text/plain",
            "retry me");

        SessionAttachmentIndexOutcome outcome = await CreateProcessor(
            new FakeWeaveService { FailEmbedding = true }).ProcessAsync(
                new SessionAttachmentIndexRequest(attachment.Id, sessionId),
                CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexStatus.Failed, outcome.Status);

        Assert.True(outcome.ShouldRetry);

        Assert.Equal(
            SessionAttachmentIndexStatus.Failed,
            (await _index!.GetStateAsync(attachment.Id, CancellationToken.None)).Status);

    }

    [SkippableFact]

    public async Task ProcessAsync_DimensionMismatch_MarksFailedWithoutPartialChunks()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord attachment = await PersistAsync(
            sessionId,
            "notes",
            "notes.txt",
            "text/plain",
            "wrong dimensions");

        SessionAttachmentIndexOutcome outcome = await CreateProcessor(
            new FakeWeaveService { OutputDimensions = Dimensions + 1 }).ProcessAsync(
                new SessionAttachmentIndexRequest(attachment.Id, sessionId),
                CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexStatus.Failed, outcome.Status);

        Assert.False(outcome.ShouldRetry);

        Assert.Empty(await _index!.GetChunksForAttachmentAsync(attachment.Id, CancellationToken.None));

    }

    [SkippableFact]

    public async Task SearchAsync_DefaultsToLatestVersionAndEnforcesSessionIsolation()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid firstSession = Guid.NewGuid();

        Guid secondSession = Guid.NewGuid();

        SessionAttachmentRecord firstV1 = await PersistAsync(
            firstSession,
            "notes",
            "notes.txt",
            "text/plain",
            "historical content");

        SessionAttachmentRecord firstV2 = await PersistAsync(
            firstSession,
            "notes",
            "notes.txt",
            "text/plain",
            "latest content");

        SessionAttachmentRecord other = await PersistAsync(
            secondSession,
            "other",
            "other.txt",
            "text/plain",
            "other session content");

        SessionAttachmentIndexProcessor processor = CreateProcessor(new FakeWeaveService());

        await processor.ProcessAsync(new(firstV1.Id, firstSession), CancellationToken.None);

        await processor.ProcessAsync(new(firstV2.Id, firstSession), CancellationToken.None);

        await processor.ProcessAsync(new(other.Id, secondSession), CancellationToken.None);

        SessionAttachmentRetrievalService retrieval = new(
            new TestOptionsMonitor<ArcanumSettings>(_settings),
            new DivinationService(
                _db!,
                new WeaveIndexAvailability(),
                NullLogger<DivinationService>.Instance),
            _index!,
            NullLogger<SessionAttachmentRetrievalService>.Instance);

        Embedding<float> query = new(CreateVector(Dimensions));

        SessionAttachmentRetrievedChunk[] latest = await retrieval.SearchAsync(
            firstSession,
            query,
            includeHistorical: false,
            CancellationToken.None);

        Assert.NotEmpty(latest);

        Assert.All(latest, hit => Assert.Equal(firstV2.Id, hit.AttachmentId));

        Assert.DoesNotContain(latest, hit => hit.AttachmentId == other.Id);

        SessionAttachmentRetrievedChunk[] historical = await retrieval.SearchAsync(
            firstSession,
            query,
            includeHistorical: true,
            CancellationToken.None);

        Assert.Contains(historical, hit => hit.AttachmentId == firstV1.Id);

        Assert.Contains(historical, hit => hit.AttachmentId == firstV2.Id);

        Assert.DoesNotContain(historical, hit => hit.AttachmentId == other.Id);

    }

    [SkippableFact]

    public async Task ProcessAsync_LatestVersionFailure_RemovesHistoricalVersionFromDefaultScope()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord first = await PersistAsync(
            sessionId,
            "notes",
            "notes.txt",
            "text/plain",
            "historical content");

        SessionAttachmentRecord second = await PersistAsync(
            sessionId,
            "notes",
            "notes.txt",
            "text/plain",
            "latest content");

        await CreateProcessor(new FakeWeaveService()).ProcessAsync(
            new(first.Id, sessionId),
            CancellationToken.None);

        SessionAttachmentIndexOutcome failed = await CreateProcessor(
            new FakeWeaveService { FailEmbedding = true }).ProcessAsync(
                new(second.Id, sessionId),
                CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexStatus.Failed, failed.Status);

        SessionAttachmentRetrievedChunk[] latest = await CreateRetrievalService().SearchAsync(
            sessionId,
            new Embedding<float>(CreateVector(Dimensions)),
            includeHistorical: false,
            CancellationToken.None);

        Assert.Empty(latest);

        SessionAttachmentRetrievedChunk[] historical = await CreateRetrievalService().SearchAsync(
            sessionId,
            new Embedding<float>(CreateVector(Dimensions)),
            includeHistorical: true,
            CancellationToken.None);

        Assert.Contains(historical, hit => hit.AttachmentId == first.Id);

        Assert.DoesNotContain(historical, hit => hit.AttachmentId == second.Id);

    }

    [SkippableFact]

    public async Task SearchAsync_OutOfOrderHistoricalIndexing_PreservesLatestScope()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord first = await PersistAsync(
            sessionId,
            "notes",
            "notes.txt",
            "text/plain",
            "historical content");

        SessionAttachmentRecord second = await PersistAsync(
            sessionId,
            "notes",
            "notes.txt",
            "text/plain",
            "latest content");

        SessionAttachmentIndexProcessor processor = CreateProcessor(new FakeWeaveService());

        _ = await processor.ProcessAsync(new(second.Id, sessionId), CancellationToken.None);

        _ = await processor.ProcessAsync(new(first.Id, sessionId), CancellationToken.None);

        SessionAttachmentRetrievedChunk[] latest = await CreateRetrievalService().SearchAsync(
            sessionId,
            new Embedding<float>(CreateVector(Dimensions)),
            includeHistorical: false,
            CancellationToken.None);

        Assert.NotEmpty(latest);

        Assert.All(latest, hit => Assert.Equal(second.Id, hit.AttachmentId));

    }

    private SessionAttachmentRetrievalService CreateRetrievalService() =>
        new(
            new TestOptionsMonitor<ArcanumSettings>(_settings),
            new DivinationService(
                _db!,
                new WeaveIndexAvailability(),
                NullLogger<DivinationService>.Instance),
            _index!,
            NullLogger<SessionAttachmentRetrievalService>.Instance);

    private SessionAttachmentIndexProcessor CreateProcessor(IWeaveService weave) =>
        new(
            new TestOptionsMonitor<ArcanumSettings>(_settings),
            weave,
            _attachments!,
            _index!,
            NullLogger<SessionAttachmentIndexProcessor>.Instance);

    private async Task<SessionAttachmentRecord> PersistAsync(
        Guid sessionId,
        string logicalKey,
        string fileName,
        string mimeType,
        string text) =>
        await _attachments!.PersistNewAsync(
            sessionId,
            null,
            null,
            logicalKey,
            fileName,
            Encoding.UTF8.GetBytes(text),
            mimeType,
            SessionAttachmentKind.Text);

    private static ArcanumSettings CreateSettings() => new()
    {

        Features = new FeatureSettings
        {

            AttachmentRetrieval = true,

        },

        Integrations = new IntegrationSettings
        {

            Embeddings = new EmbeddingIntegrationSettings
            {

                Dimensions = Dimensions,

                AttachmentIndexing = new AttachmentIndexingIntegrationSettings
                {

                    ChunkSizeCharacters = 128,

                    ChunkOverlapCharacters = 16,

                    MaxChunksPerAttachment = 8,

                    MaxRetrievedChunks = 20,

                },

            },

        },

    };

    private static float[] CreateVector(int dimensions)
    {

        float[] vector = new float[dimensions];

        vector[0] = 1f;

        return vector;

    }

    private sealed class FakeWeaveService : IWeaveService
    {

        public bool FailEmbedding { get; init; }

        public int OutputDimensions { get; init; } = Dimensions;

        public int EmbedBatchCallCount { get; private set; }

        public bool IsAvailable => true;

        public Task<Result<Embedding<float>>> EmbedAsync(
            string text,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result<Embedding<float>>.Success(new Embedding<float>(CreateVector(OutputDimensions))));

        public Task<Result<Embedding<float>[]>> EmbedBatchAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken)
        {

            EmbedBatchCallCount++;

            if (FailEmbedding)
            {

                return Task.FromResult(Result<Embedding<float>[]>.Failure(new Error(
                    ErrorCodes.Embeddings.ProviderUnavailable,
                    "Simulated embedding failure.")));

            }

            Embedding<float>[] generated = texts
                .Select(_ => new Embedding<float>(CreateVector(OutputDimensions)))
                .ToArray();

            return Task.FromResult(Result<Embedding<float>[]>.Success(generated));

        }

        public Task<Result<(string Chunk, int Offset)[]>> ChunkAsync(
            string text,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

    }

    private sealed class RecordingQueue : ISessionAttachmentIndexQueue
    {

        public List<SessionAttachmentIndexRequest> Requests { get; } = [];

        public bool TryEnqueue(SessionAttachmentIndexRequest request)
        {

            Requests.Add(request);

            return true;

        }

    }

}
