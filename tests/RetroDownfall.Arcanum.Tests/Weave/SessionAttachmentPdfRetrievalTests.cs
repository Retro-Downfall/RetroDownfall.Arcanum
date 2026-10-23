using Microsoft.Extensions.AI;

using Microsoft.Extensions.Logging.Abstractions;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Weave;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Intelligence;

using RetroDownfall.Arcanum.Infrastructure.Repositories;

using RetroDownfall.Arcanum.Infrastructure.Storage;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.Pdf;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

using RetroDownfall.Arcanum.Tests.Weave.Comprehension.Pdf;

namespace RetroDownfall.Arcanum.Tests.Weave;

/// <summary>
/// A real PDF through the production path of the retrieval context: the registry's isolated host (its worker run in this
/// process), the indexer under an admitted work lease, the Grimoire, semantic search and the prompt. The text found is the
/// source text, its chunk reports the page it came from, and text a reader would not see is found like any other.
/// </summary>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class SessionAttachmentPdfRetrievalTests : IAsyncLifetime
{
    private const int Dimensions = 64;

    private const string UnseenTextFrame = "source-format: PDF document; layout not preserved; may include text a reader would not see\n";

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private string _attachmentsRoot = string.Empty;

    private ArcanumDbContext? _db;

    private ArcanumSettings _settings = null!;

    private SessionAttachmentStore? _attachments;

    private SessionAttachmentIndexRepository? _index;

    public SessionAttachmentPdfRetrievalTests(GrimoireFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync()
    {
        _dbPath = _fixture.CopyDatabase();

        _attachmentsRoot = Path.Combine(Path.GetTempPath(), "arcanum-attachment-pdf-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_attachmentsRoot);

        _db = _fixture.CreateContext(_dbPath);

        _settings = new ArcanumSettings
        {
            Features = new FeatureSettings { AttachmentRetrieval = true, Comprehension = true },

            Integrations = new IntegrationSettings
            {
                Embeddings = new EmbeddingIntegrationSettings { Dimensions = Dimensions },
            },
        };

        _attachments = new SessionAttachmentStore(_db, Options.Create(_settings), _attachmentsRoot, TestEncryptedBlobStore.Create());

        _index = new SessionAttachmentIndexRepository(_db, new WeaveIndexAvailability(), new TestOptionsMonitor<ArcanumSettings>(_settings));

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
    public async Task SemanticSearch_FindsAPageThreeLineOfARealPdf_AndReportsPageThree()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        // cups-multi.pdf is ten pages from macOS Quartz, which wraps each source line at 80 columns; line 75 of its source
        // is on page 3, away from both page edges. The marker is the part of the line before the wrap.
        const string marker = "fox number 75 jumps over the lazy dog 525 times";

        Assert.Contains(marker, new PdfTextExtractor().Extract(PdfFixtures.Read("cups-multi.pdf"), PdfExtractionLimits.Default, CancellationToken.None).Pages[2]);

        (Guid sessionId, SessionAttachmentIndexOutcome outcome) = await IndexAsync("cups-multi.pdf", marker);

        Assert.Equal(SessionAttachmentIndexStatus.Indexed, outcome.Status);

        SessionAttachmentRetrievedChunk[] hits = await SearchAsync(sessionId);

        Assert.NotEmpty(hits);

        Assert.All(hits, static hit =>
        {
            Assert.Contains(marker, hit.Content, StringComparison.Ordinal);

            // Page 3, not page 1: a mapping that reported every chunk on the first page fails here.
            Assert.Equal((3, 3), (hit.StartLine, hit.EndLine));
        });

        // The whole source line, against the committed source text rather than a non-empty check.
        string sourceLine = File.ReadLines(Path.Combine(AppContext.BaseDirectory, "TestData", "Comprehension", "Pdf", "cups-multi.txt"))
            .Single(static line => line.StartsWith("Line 075:", StringComparison.Ordinal));

        Assert.Contains(PdfFixtures.WithoutWhitespace(sourceLine), PdfFixtures.WithoutWhitespace(hits[0].Content), StringComparison.Ordinal);

        string prompt = SystemPromptBuilder.Build(
            new PingRequest("what does line 75 say", SessionId: sessionId), codexContent: null, sessionAttachmentContext: hits);

        Assert.Contains($"; pages 3-3\n", prompt, StringComparison.Ordinal);

        Assert.Contains(UnseenTextFrame, prompt, StringComparison.Ordinal);
    }

    [SkippableTheory]
    [InlineData("WHITE-ON-WHITE-CHARLIE")]
    [InlineData("OFFPAGE-ECHO")]
    public async Task SemanticSearch_FindsTextAReaderWouldNotSee(string marker)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        (Guid sessionId, SessionAttachmentIndexOutcome outcome) = await IndexAsync("hidden.pdf", marker);

        Assert.Equal(SessionAttachmentIndexStatus.Indexed, outcome.Status);

        SessionAttachmentRetrievedChunk hit = Assert.Single(await SearchAsync(sessionId));

        Assert.Contains(marker, hit.Content, StringComparison.Ordinal);

        Assert.Equal((1, 1), (hit.StartLine, hit.EndLine));

        Assert.Contains(
            UnseenTextFrame,
            SystemPromptBuilder.Build(new PingRequest("hidden", SessionId: sessionId), codexContent: null, sessionAttachmentContext: [hit]),
            StringComparison.Ordinal);
    }

    private async Task<(Guid SessionId, SessionAttachmentIndexOutcome Outcome)> IndexAsync(string fixture, string marker)
    {
        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord attachment = await _attachments!.PersistNewAsync(
            sessionId, null, null, "report", "report.pdf", PdfFixtures.Read(fixture),
            SessionAttachmentComprehensionPolicy.PdfMimeType, SessionAttachmentKind.Binary);

        // No host is injected: the processor takes the registry's PDF host, whose worker runs here in this process.
        using IDisposable pdfWorker = InProcessPdfWorker.Use(out InProcessPdfWorker worker);

        SessionAttachmentIndexOutcome outcome = await new SessionAttachmentIndexProcessor(
                new TestOptionsMonitor<ArcanumSettings>(_settings),
                new MarkerWeaveService(marker),
                _attachments,
                _index!,
                NullLogger<SessionAttachmentIndexProcessor>.Instance)
            .ProcessUnderOpenAdmissionAsync(new SessionAttachmentIndexRequest(attachment.Id, sessionId), CancellationToken.None);

        Assert.Equal(1, worker.Runs);

        return (sessionId, outcome);
    }

    private Task<SessionAttachmentRetrievedChunk[]> SearchAsync(Guid sessionId) =>
        new SessionAttachmentRetrievalService(
                new TestOptionsMonitor<ArcanumSettings>(_settings),
                new DivinationService(
                    _db!,
                    new WeaveIndexAvailability(),
                    NullLogger<DivinationService>.Instance,
                    new TestOptionsMonitor<ArcanumSettings>(_settings)),
                _index!,
                NullLogger<SessionAttachmentRetrievalService>.Instance)
            .SearchAsync(sessionId, new Embedding<float>(MarkerWeaveService.Vector(matches: true)), false, CancellationToken.None);

    /// <summary>An embedding that places text holding the marker on one axis and every other text on another.</summary>
    private sealed class MarkerWeaveService(string marker) : IWeaveService
    {
        public bool IsAvailable => true;

        public static float[] Vector(bool matches)
        {
            float[] vector = new float[Dimensions];

            vector[matches ? 0 : 1] = 1f;

            return vector;
        }

        public Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken) =>
            Task.FromResult(Result<Embedding<float>>.Success(new Embedding<float>(Vector(text.Contains(marker, StringComparison.Ordinal)))));

        public Task<Result<Embedding<float>[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) =>
            Task.FromResult(Result<Embedding<float>[]>.Success(
                [.. texts.Select(text => new Embedding<float>(Vector(text.Contains(marker, StringComparison.Ordinal))))]));

        public Task<Result<(string Chunk, int Offset)[]>> ChunkAsync(string text, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
