using System.Text;

using Microsoft.Extensions.AI;

using Microsoft.Extensions.Logging.Abstractions;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Api.Tower;

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

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave;

/// <summary>
/// Page provenance through the index: a document of a pages-unit format that reports page marks stores the page
/// of each chunk's first and last character in the existing range columns, leaves its character offsets alone,
/// and is indexed under its own pipeline fingerprint. Every other format keeps its lines.
/// </summary>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class SessionAttachmentPageProvenanceTests : IAsyncLifetime
{
    private const int Dimensions = 64;

    // The index slices fixed windows of 1000 characters every 900, so these page lengths put a window across
    // page 1-2, across page 2-3, and wholly inside page 3.
    private const int PageLines = 49;

    private const int LineLength = 50;

    private const int PageLength = PageLines * LineLength;

    private const int ChunkSize = 1_000;

    private const int Overlap = 100;

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private string _attachmentsRoot = string.Empty;

    private ArcanumDbContext? _db;

    private ArcanumSettings _settings = null!;

    private SessionAttachmentStore? _attachments;

    private SessionAttachmentIndexRepository? _index;

    public SessionAttachmentPageProvenanceTests(GrimoireFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync()
    {
        _dbPath = _fixture.CopyDatabase();

        _attachmentsRoot = Path.Combine(Path.GetTempPath(), "arcanum-attachment-pages-" + Guid.NewGuid().ToString("N"));

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

        _attachments = new SessionAttachmentStore(
            _db,
            Options.Create(_settings),
            _attachmentsRoot,
            TestEncryptedBlobStore.Create());

        _index = new SessionAttachmentIndexRepository(
            _db,
            new WeaveIndexAvailability(),
            new TestOptionsMonitor<ArcanumSettings>(_settings));

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
    public async Task ProcessAsync_PdfPages_StoreTheFirstAndLastPageOfEachChunkAndKeepItsCharacterOffsets()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string[] pages = [PageText(1), PageText(2), PageText(3)];

        SessionAttachmentRecord attachment = await PersistPdfAsync(Guid.NewGuid());

        SessionAttachmentIndexOutcome outcome = await ProcessAsync(attachment, PagesHost(pages));

        Assert.Equal(SessionAttachmentIndexStatus.Indexed, outcome.Status);

        SessionAttachmentIndexedChunk[] chunks = await _index!.GetChunksForAttachmentAsync(attachment.Id, CancellationToken.None);

        Assert.Equal(PageLength * 3, chunks[^1].CharacterEnd);

        Assert.Equal(9, chunks.Length);

        foreach (SessionAttachmentIndexedChunk chunk in chunks)
        {
            // The offsets are the ones a lines document gets: fixed windows over the extracted text.
            Assert.Equal(chunk.ChunkIndex * (ChunkSize - Overlap), chunk.CharacterStart);

            Assert.Equal(Math.Min(PageLength * 3, chunk.CharacterStart + ChunkSize), chunk.CharacterEnd);

            Assert.Equal(PageOf(chunk.CharacterStart), chunk.StartLine);

            Assert.Equal(PageOf(chunk.CharacterEnd - 1), chunk.EndLine);
        }

        // Asserted away from page 1, so a constant or a copy of the first page cannot pass.
        Assert.Equal((1, 1), (chunks[0].StartLine, chunks[0].EndLine));

        Assert.Equal((1, 2), (chunks[2].StartLine, chunks[2].EndLine));

        Assert.Equal((2, 3), (chunks[5].StartLine, chunks[5].EndLine));

        Assert.Equal((3, 3), (chunks[6].StartLine, chunks[6].EndLine));

        Assert.Equal((3, 3), (chunks[8].StartLine, chunks[8].EndLine));
    }

    [SkippableFact]
    public async Task ProcessAsync_PdfPages_ReportPagesNotTheLinesTheTextHas()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string[] pages = [PageText(1), PageText(2), PageText(3)];

        SessionAttachmentRecord attachment = await PersistPdfAsync(Guid.NewGuid());

        await ProcessAsync(attachment, PagesHost(pages));

        SessionAttachmentIndexedChunk[] chunks = await _index!.GetChunksForAttachmentAsync(attachment.Id, CancellationToken.None);

        // Page 3's first line is line 99 of the text, and chunk 6 starts well inside it.
        Assert.True(string.Concat(pages).Take(chunks[6].CharacterStart).Count(static c => c == '\n') > 100);

        Assert.All(chunks, static chunk => Assert.InRange(chunk.EndLine, 1, 3));
    }

    [SkippableFact]
    public async Task ProcessAsync_PdfPages_AreTheDocumentsOwnNumbersNotPositions()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        // A document that opens on page 7 and skips to 12 stores 7 and 12, not 1 and 2.
        SessionAttachmentRecord attachment = await PersistPdfAsync(Guid.NewGuid());

        SessionAttachmentDocumentExtractionHost host = new(
            (_, output, _) =>
            {
                output.BeginPage(7);

                output.TryAppend(new string('a', 450));

                output.BeginPage(12);

                output.TryAppend(new string('b', 450));

                return ValueTask.CompletedTask;
            },
            TimeSpan.FromSeconds(30));

        await ProcessAsync(attachment, host);

        SessionAttachmentIndexedChunk[] chunks = await _index!.GetChunksForAttachmentAsync(attachment.Id, CancellationToken.None);

        SessionAttachmentIndexedChunk only = Assert.Single(chunks);

        Assert.Equal((7, 12), (only.StartLine, only.EndLine));

        Assert.Equal((0, 900), (only.CharacterStart, only.CharacterEnd));
    }

    [SkippableFact]
    public async Task ProcessAsync_PdfChunkEdgesOnPageEdges_BelongToTheirOwnPageOnly()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        // Pages of 900, 1000 and 100 characters against windows of [0,1000), [900,1900) and [1800,2000): the second
        // window starts on page 2's first character and ends on its last, so neither edge may leak into a neighbour.
        SessionAttachmentRecord attachment = await PersistPdfAsync(Guid.NewGuid());

        await ProcessAsync(attachment, PagesHost(new string('a', 900), new string('b', 1000), new string('c', 100)));

        SessionAttachmentIndexedChunk[] chunks = await _index!.GetChunksForAttachmentAsync(attachment.Id, CancellationToken.None);

        Assert.Equal(
            [(0, 1000, 1, 2), (900, 1900, 2, 2), (1800, 2000, 2, 3)],
            chunks.Select(static chunk => (chunk.CharacterStart, chunk.CharacterEnd, chunk.StartLine, chunk.EndLine)));
    }

    [SkippableFact]
    public async Task ProcessAsync_PdfWithManyUnevenPages_MapsEveryChunkEdgeToItsPage()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        // Sixty pages of 1 to 300 characters: windows that sit in one page, that cross two, and that cross many.
        string[] pages = [.. Enumerable.Range(1, 60).Select(static page => new string('x', 1 + (page * 37 % 300)))];

        int[] pageStarts = new int[pages.Length];

        for (int index = 1; index < pages.Length; index++)
        {
            pageStarts[index] = pageStarts[index - 1] + pages[index - 1].Length;
        }

        int total = pageStarts[^1] + pages[^1].Length;

        int PageAtOffset(int offset) => Array.FindLastIndex(pageStarts, start => start <= offset) + 1;

        SessionAttachmentRecord attachment = await PersistPdfAsync(Guid.NewGuid());

        await ProcessAsync(attachment, PagesHost(pages));

        SessionAttachmentIndexedChunk[] chunks = await _index!.GetChunksForAttachmentAsync(attachment.Id, CancellationToken.None);

        Assert.Equal(total, chunks[^1].CharacterEnd);

        Assert.True(chunks.Length > 5);

        Assert.All(chunks, chunk =>
        {
            Assert.Equal(PageAtOffset(chunk.CharacterStart), chunk.StartLine);

            Assert.Equal(PageAtOffset(chunk.CharacterEnd - 1), chunk.EndLine);
        });

        Assert.Contains(chunks, static chunk => chunk.EndLine - chunk.StartLine >= 4);
    }

    [SkippableFact]
    public async Task ProcessAsync_LinesFormat_IgnoresPageMarksAndKeepsItsLineNumbers()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string[] pages = [PageText(1), PageText(2), PageText(3)];

        SessionAttachmentRecord attachment = await _attachments!.PersistNewAsync(
            Guid.NewGuid(), null, null, "document", "document.arccomp",
            Encoding.UTF8.GetBytes("ARCCOMP1\nignored"), SessionAttachmentComprehensionPolicy.MimeType, SessionAttachmentKind.Binary);

        SessionAttachmentIndexOutcome outcome = await ProcessAsync(attachment, PagesHost(pages));

        Assert.Equal(SessionAttachmentIndexStatus.Indexed, outcome.Status);

        SessionAttachmentIndexedChunk[] chunks = await _index!.GetChunksForAttachmentAsync(attachment.Id, CancellationToken.None);

        string text = string.Concat(pages);

        Assert.All(chunks, chunk =>
        {
            Assert.Equal(1 + Newlines(text, chunk.CharacterStart), chunk.StartLine);

            Assert.Equal(1 + Newlines(text, chunk.CharacterEnd - 1), chunk.EndLine);
        });

        Assert.True(chunks[6].StartLine > 100);
    }

    [SkippableFact]
    public async Task ProcessAsync_PlainText_KeepsItsLineNumbers()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string text = string.Concat(PageText(1), PageText(2), PageText(3));

        SessionAttachmentRecord attachment = await _attachments!.PersistNewAsync(
            Guid.NewGuid(), null, null, "notes", "notes.txt", Encoding.UTF8.GetBytes(text), "text/plain", SessionAttachmentKind.Text);

        SessionAttachmentIndexOutcome outcome = await ProcessAsync(attachment, host: null);

        Assert.Equal(SessionAttachmentIndexStatus.Indexed, outcome.Status);

        SessionAttachmentIndexedChunk[] chunks = await _index!.GetChunksForAttachmentAsync(attachment.Id, CancellationToken.None);

        Assert.All(chunks, chunk =>
        {
            Assert.Equal(1 + Newlines(text, chunk.CharacterStart), chunk.StartLine);

            Assert.Equal(1 + Newlines(text, chunk.CharacterEnd - 1), chunk.EndLine);
        });
    }

    [SkippableFact]
    public async Task ProcessAsync_PdfWithPageMarks_IsIndexedUnderThePagesFingerprint()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        RecordingIndexWriter writer = new(_index!);

        SessionAttachmentRecord attachment = await PersistPdfAsync(Guid.NewGuid());

        await ProcessAsync(attachment, PagesHost(PageText(1)), writer);

        Assert.Equal($"comprehension-v2:{ChunkSize}:{Overlap}", Assert.Single(writer.Fingerprints));
    }

    [SkippableFact]
    public async Task ProcessAsync_LinesDocument_KeepsTheLinesFingerprint()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        RecordingIndexWriter writer = new(_index!);

        SessionAttachmentRecord attachment = await _attachments!.PersistNewAsync(
            Guid.NewGuid(), null, null, "document", "document.arccomp",
            Encoding.UTF8.GetBytes("ARCCOMP1\nignored"), SessionAttachmentComprehensionPolicy.MimeType, SessionAttachmentKind.Binary);

        await ProcessAsync(attachment, PagesHost(PageText(1)), writer);

        Assert.Equal($"comprehension-v1:{ChunkSize}:{Overlap}", Assert.Single(writer.Fingerprints));
    }

    [SkippableFact]
    public async Task ProcessAsync_PlainText_KeepsTheTextFingerprint()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        RecordingIndexWriter writer = new(_index!);

        SessionAttachmentRecord attachment = await _attachments!.PersistNewAsync(
            Guid.NewGuid(), null, null, "notes", "notes.txt", "plain notes"u8.ToArray(), "text/plain", SessionAttachmentKind.Text);

        await ProcessAsync(attachment, host: null, writer);

        Assert.Equal($"v1:{ChunkSize}:{Overlap}", Assert.Single(writer.Fingerprints));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessAsync_PdfThatReportsNoPageForItsFirstText_FailsClosedWithoutIndexingLinesAsPages(bool textBeforeFirstPage)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SessionAttachmentRecord attachment = await PersistPdfAsync(Guid.NewGuid());

        SessionAttachmentDocumentExtractionHost host = new(
            (_, output, _) =>
            {
                output.TryAppend("text no page claims");

                if (textBeforeFirstPage)
                {
                    output.BeginPage(1);

                    output.TryAppend("a page that starts too late");
                }

                return ValueTask.CompletedTask;
            },
            TimeSpan.FromSeconds(30));

        RecordingIndexWriter writer = new(_index!);

        SessionAttachmentIndexOutcome outcome = await ProcessAsync(attachment, host, writer);

        Assert.Equal(SessionAttachmentIndexStatus.Failed, outcome.Status);

        Assert.False(outcome.ShouldRetry);

        Assert.Empty(writer.Fingerprints);

        Assert.Empty(await _index!.GetChunksForAttachmentAsync(attachment.Id, CancellationToken.None));

        Assert.Equal(
            "Document page provenance is unavailable.",
            (await _index.GetStateAsync(attachment.Id, CancellationToken.None)).FailureReason);
    }

    [SkippableFact]
    public async Task Retrieval_OfAPdfPageChunk_RendersItsRangeAsPages()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord attachment = await PersistPdfAsync(sessionId);

        await ProcessAsync(attachment, PagesHost(PageText(1), PageText(2), PageText(3)));

        SessionAttachmentRetrievedChunk[] hits = await CreateRetrievalService().SearchAsync(
            sessionId, new Embedding<float>(CreateVector()), false, CancellationToken.None);

        Assert.NotEmpty(hits);

        string prompt = SystemPromptBuilder.Build(
            new PingRequest("read the document", SessionId: sessionId), codexContent: null, sessionAttachmentContext: hits);

        Assert.All(hits, hit =>
        {
            Assert.Equal(PageOf(hit.CharacterStart), hit.StartLine);

            Assert.Contains(
                $"chunk/range: {hit.ChunkIndex + 1}; chars {hit.CharacterStart}-{hit.CharacterEnd}; pages {hit.StartLine}-{hit.EndLine}\n",
                prompt,
                StringComparison.Ordinal);
        });

        Assert.DoesNotContain("; lines ", prompt, StringComparison.Ordinal);

        Assert.Contains("source-format: PDF document; layout not preserved", prompt, StringComparison.Ordinal);
    }

    private static string PageText(int page) => string.Concat(
        Enumerable.Range(1, PageLines).Select(line => $"p{page} line {line:00}".PadRight(LineLength - 1, '.') + "\n"));

    private static int PageOf(int offset) => offset / PageLength + 1;

    private static int Newlines(string text, int endExclusive) => text.AsSpan(0, endExclusive).Count('\n');

    private static float[] CreateVector()
    {
        float[] vector = new float[Dimensions];

        vector[0] = 1f;

        return vector;
    }

    private Task<SessionAttachmentRecord> PersistPdfAsync(Guid sessionId) =>
        _attachments!.PersistNewAsync(
            sessionId, null, null, "report", "report.pdf", "%PDF-1.7 placeholder"u8.ToArray(),
            SessionAttachmentComprehensionPolicy.PdfMimeType, SessionAttachmentKind.Binary);

    private static SessionAttachmentDocumentExtractionHost PagesHost(params string[] pages) => new(
        (_, output, _) =>
        {
            for (int index = 0; index < pages.Length; index++)
            {
                output.BeginPage(index + 1);

                output.TryAppend(pages[index]);
            }

            return ValueTask.CompletedTask;
        },
        TimeSpan.FromSeconds(30));

    private Task<SessionAttachmentIndexOutcome> ProcessAsync(
        SessionAttachmentRecord attachment,
        SessionAttachmentDocumentExtractionHost? host,
        ISessionAttachmentIndexWriter? writer = null) =>
        new SessionAttachmentIndexProcessor(
                new TestOptionsMonitor<ArcanumSettings>(_settings),
                new FakeWeaveService(),
                _attachments!,
                writer ?? _index!,
                NullLogger<SessionAttachmentIndexProcessor>.Instance,
                host)
            .ProcessUnderOpenAdmissionAsync(new(attachment.Id, attachment.SessionId!.Value), CancellationToken.None);

    private SessionAttachmentRetrievalService CreateRetrievalService() =>
        new(
            new TestOptionsMonitor<ArcanumSettings>(_settings),
            new DivinationService(
                _db!,
                new WeaveIndexAvailability(),
                NullLogger<DivinationService>.Instance,
                new TestOptionsMonitor<ArcanumSettings>(_settings)),
            _index!,
            NullLogger<SessionAttachmentRetrievalService>.Instance);

    private sealed class FakeWeaveService : IWeaveService
    {
        public bool IsAvailable => true;

        public Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken) =>
            Task.FromResult(Result<Embedding<float>>.Success(new Embedding<float>(CreateVector())));

        public Task<Result<Embedding<float>[]>> EmbedBatchAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result<Embedding<float>[]>.Success(
                [.. texts.Select(static _ => new Embedding<float>(CreateVector()))]));

        public Task<Result<(string Chunk, int Offset)[]>> ChunkAsync(string text, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    /// <summary>Forwards to the real repository and remembers the pipeline fingerprint each replacement began with.</summary>
    private sealed class RecordingIndexWriter(ISessionAttachmentIndexWriter inner) : ISessionAttachmentIndexWriter
    {
        public List<string> Fingerprints { get; } = [];

        public Task<SessionAttachmentIndexRequest[]> ReconcileAndFindPendingAsync(
            int expectedDimensions,
            int maxAttachments,
            CancellationToken cancellationToken) =>
            inner.ReconcileAndFindPendingAsync(expectedDimensions, maxAttachments, cancellationToken);

        public Task SetPendingAsync(SessionAttachmentRecord attachment, int attempt, CancellationToken cancellationToken) =>
            inner.SetPendingAsync(attachment, attempt, cancellationToken);

        public Task MarkWithoutIndexAsync(
            Guid attachmentId,
            string contentSha256,
            SessionAttachmentIndexStatus status,
            int attempt,
            string? failureReason,
            DateTimeOffset? extractedAt,
            CancellationToken cancellationToken) =>
            inner.MarkWithoutIndexAsync(
                attachmentId, contentSha256, status, attempt, failureReason, extractedAt, cancellationToken);

        public Task<SessionAttachmentIndexCheckpoint> BeginReplaceAsync(
            SessionAttachmentRecord attachment,
            int expectedDimensions,
            string pipelineFingerprint,
            DateTimeOffset extractedAt,
            CancellationToken cancellationToken)
        {
            Fingerprints.Add(pipelineFingerprint);

            return inner.BeginReplaceAsync(attachment, expectedDimensions, pipelineFingerprint, extractedAt, cancellationToken);
        }

        public Task AppendReplaceBatchAsync(
            SessionAttachmentRecord attachment,
            string generationId,
            IReadOnlyList<SessionAttachmentTextChunk> chunks,
            IReadOnlyList<Embedding<float>> embeddings,
            int expectedDimensions,
            DateTimeOffset extractedAt,
            DateTimeOffset indexedAt,
            CancellationToken cancellationToken) =>
            inner.AppendReplaceBatchAsync(
                attachment, generationId, chunks, embeddings, expectedDimensions, extractedAt, indexedAt, cancellationToken);

        public Task CompleteReplaceAsync(
            SessionAttachmentRecord attachment,
            string generationId,
            int expectedChunkCount,
            DateTimeOffset extractedAt,
            DateTimeOffset indexedAt,
            int attempt,
            CancellationToken cancellationToken) =>
            inner.CompleteReplaceAsync(
                attachment, generationId, expectedChunkCount, extractedAt, indexedAt, attempt, cancellationToken);
    }
}
