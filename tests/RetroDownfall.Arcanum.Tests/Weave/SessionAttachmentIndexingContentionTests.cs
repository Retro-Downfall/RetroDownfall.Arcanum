using System.Text;

using Microsoft.Extensions.AI;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Logging;

using Microsoft.Extensions.Logging.Abstractions;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Weave;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave;

/// <summary>
/// A healthy document is never permanently failed because other documents of its parser family held
/// the family's slot. Driven through the real indexing service's dequeue path, a real Grimoire and a
/// production-shaped family host.
/// </summary>
[Collection("Grimoire")]

[Trait("Category", "Integration")]

public sealed class SessionAttachmentIndexingContentionTests(GrimoireFixture fixture) : IAsyncLifetime
{
    private const int Dimensions = 64;

    /// <summary>Each hostile workbook parses in about one to three seconds alone, so eight outlast a five-second budget.</summary>
    private const int HostileInFlight = 8;

    private string _dbPath = string.Empty;

    private string _attachmentsRoot = string.Empty;

    private ArcanumDbContext? _db;

    private ArcanumSettings _settings = null!;

    private SessionAttachmentStore? _attachments;

    private SessionAttachmentIndexRepository? _index;

    public Task InitializeAsync()
    {
        _dbPath = fixture.CopyDatabase();

        _attachmentsRoot = Path.Combine(Path.GetTempPath(), "arcanum-attachment-contention-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_attachmentsRoot);

        _db = fixture.CreateContext(_dbPath);

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
    public async Task ProcessOneAsync_QuarantinedFamily_RetriesTheHealthyDocumentUntilItIsIndexed()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord healthy = await _attachments!.PersistNewAsync(
            sessionId, null, null, "healthy", "healthy.arccomp", Encoding.UTF8.GetBytes("ARCCOMP1\nhealthy document"),
            SessionAttachmentComprehensionPolicy.MimeType, SessionAttachmentKind.Binary);

        byte[] poison = Encoding.UTF8.GetBytes("ARCCOMP1\npoison");

        PoisonAwareExtractor extractor = new(poison);

        SessionAttachmentDocumentExtractionHost host = new(extractor.ExtractAsync, TimeSpan.FromMilliseconds(100));

        ImmediateTimeProvider retryTime = new();

        using SessionAttachmentIndexingService service = CreateService(host, retryTime);

        try
        {
            // Another caller (a turn reference, a pin) runs a parser that overruns its own budget and ignores
            // cancellation, so the family stays quarantined while the indexer reaches the healthy document.
            SessionAttachmentExtractionResult overran = await host.ExtractAsync(
                _ => Task.FromResult<ReadOnlyMemory<byte>>(poison), poison.Length, 1024, CancellationToken.None);

            Assert.Equal(SessionAttachmentExtractionStatus.Failed, overran.Status);

            SessionAttachmentIndexOutcome deferred = await service.ProcessOneAsync(
                Dequeue(service, new SessionAttachmentIndexRequest(healthy.Id, sessionId)),
                CancellationToken.None);

            Assert.Equal(SessionAttachmentIndexDisposition.Concluded, deferred.Disposition);

            Assert.Equal(SessionAttachmentIndexStatus.Failed, deferred.Status);

            Assert.True(deferred.ShouldRetry, "Contention on a healthy document must take the retry path.");

            SessionAttachmentIndexState transient = await _index!.GetStateAsync(healthy.Id, CancellationToken.None);

            Assert.Equal(SessionAttachmentIndexStatus.Failed, transient.Status);

            Assert.Null(transient.ExtractedAt);

            Assert.Equal(SessionAttachmentDocumentExtractionHost.BusyReason, transient.FailureReason);

            Assert.Single(retryTime.Delays);

            Assert.True(service.QueueReader.TryRead(out SessionAttachmentIndexRequest? retried));

            Assert.Equal(1, retried!.Attempt);

            Assert.Equal(0, extractor.HealthyParses);

            extractor.Release();

            await extractor.PoisonFinished.WaitAsync(TimeSpan.FromSeconds(5));

            SessionAttachmentIndexOutcome final = await ProcessUntilConcludedAsync(service, retried);

            Assert.Equal(SessionAttachmentIndexStatus.Indexed, final.Status);
        }
        finally
        {
            extractor.Release();
        }

        SessionAttachmentIndexState indexed = await _index.GetStateAsync(healthy.Id, CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexStatus.Indexed, indexed.Status);

        Assert.NotNull(indexed.ExtractedAt);

        Assert.Null(indexed.FailureReason);

        SessionAttachmentIndexedChunk chunk = Assert.Single(await _index.GetChunksForAttachmentAsync(healthy.Id, CancellationToken.None));

        Assert.Equal("healthy document", chunk.Content);
    }

    [SkippableFact]
    public async Task ProcessOneAsync_HostileSameFamilyWorkbooksInFlight_HealthyWorkbookIsEventuallyIndexed()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        byte[] healthyBytes = ContentionWorkbooks.LargeHealthyWorkbook();

        SessionAttachmentRecord healthy = await _attachments!.PersistNewAsync(
            sessionId, null, null, "orders", "orders.xlsx", healthyBytes,
            SessionAttachmentComprehensionPolicy.ExcelWorkbookMimeType, SessionAttachmentKind.Binary);

        // Within every package bound, each takes seconds of the family's single slot and yields no text.
        byte[] hostile = ContentionWorkbooks.SlowRowsWorkbook();

        // The production family shape: the real Spreadsheet parser behind one slot and a five-second budget.
        SessionAttachmentDocumentExtractionHost host = new(new SpreadsheetDocumentExtractor().ExtractAsync, TimeSpan.FromSeconds(5));

        using SessionAttachmentIndexingService service = CreateService(host, new ImmediateTimeProvider());

        // Same-family extractions from other sessions' turns and pins, already in flight. Together they hold
        // the slot well past one document budget, each finishing within its own.
        Task<SessionAttachmentExtractionResult>[] inFlight =
        [
            .. Enumerable.Range(0, HostileInFlight).Select(_ => Task.Run(() => ExtractElsewhereAsync(host, hostile))),
        ];

        await Task.Delay(TimeSpan.FromMilliseconds(30));

        SessionAttachmentIndexOutcome final = await ProcessUntilConcludedAsync(
            service,
            Dequeue(service, new SessionAttachmentIndexRequest(healthy.Id, sessionId)));

        SessionAttachmentExtractionResult[] others = await Task.WhenAll(inFlight).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.All(others, other => Assert.NotEqual(SessionAttachmentExtractionStatus.Extracted, other.Status));

        SessionAttachmentIndexState state = await _index!.GetStateAsync(healthy.Id, CancellationToken.None);

        Assert.True(
            final.Status == SessionAttachmentIndexStatus.Indexed,
            $"The healthy workbook concluded {final.Status} (retry {final.ShouldRetry}): {state.FailureReason}");

        Assert.Equal(SessionAttachmentIndexStatus.Indexed, state.Status);

        Assert.NotEmpty(await _index.GetChunksForAttachmentAsync(healthy.Id, CancellationToken.None));
    }

    /// <summary>
    /// Runs a dequeued request through the real dequeue path, following the service's own automatic
    /// retries, until an attempt concludes without asking for another.
    /// </summary>
    private static async Task<SessionAttachmentIndexOutcome> ProcessUntilConcludedAsync(
        SessionAttachmentIndexingService service,
        SessionAttachmentIndexRequest request)
    {
        DateTimeOffset giveUp = DateTimeOffset.UtcNow.AddSeconds(60);

        while (true)
        {
            SessionAttachmentIndexOutcome outcome = await service.ProcessOneAsync(request, CancellationToken.None);

            if (!outcome.ShouldRetry || DateTimeOffset.UtcNow > giveUp)
            {
                return outcome;
            }

            Assert.True(service.QueueReader.TryRead(out SessionAttachmentIndexRequest? next), "A retried attempt must re-enter the queue.");

            request = next!;

            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }
    }

    private static Task<SessionAttachmentExtractionResult> ExtractElsewhereAsync(
        SessionAttachmentDocumentExtractionHost host,
        byte[] bytes)
    {
        SessionAttachmentRecord record = new(
            Guid.NewGuid(), Guid.NewGuid(), null, null, SessionAttachmentState.Bound,
            "elsewhere", "elsewhere.xlsx", 1, "untrusted/path", "hash",
            SessionAttachmentComprehensionPolicy.ExcelWorkbookMimeType, bytes.Length, SessionAttachmentKind.Binary,
            DateTimeOffset.UtcNow);

        return SessionAttachmentTextExtractor.ExtractDocumentAsync(
            new NoOpSessionAttachmentStore(readBytes: (_, _) => Task.FromResult<ReadOnlyMemory<byte>>(bytes)),
            record,
            new ArcanumSettings
            {
                Features = new FeatureSettings { AttachmentRetrieval = true, Comprehension = true },
            },
            ArcanumSettingClamps.MaxAttachFileSizeBytes(ArcanumRuntimeDefaults.CliMaxAttachFileSizeBytes),
            CancellationToken.None,
            host);
    }

    private static SessionAttachmentIndexRequest Dequeue(
        SessionAttachmentIndexingService service,
        SessionAttachmentIndexRequest request)
    {
        Assert.True(service.TryEnqueue(request));

        Assert.True(service.QueueReader.TryRead(out SessionAttachmentIndexRequest? dequeued));

        return dequeued!;
    }

    private SessionAttachmentIndexingService CreateService(
        SessionAttachmentDocumentExtractionHost comprehension,
        TimeProvider retryTime)
    {
        ServiceCollection services = new();

        services.AddSingleton(_index!);

        services.AddSingleton<ISessionAttachmentIndexWriter>(_index!);

        services.AddSingleton<IWeaveService>(new ConstantWeaveService());

        services.AddSingleton<ISessionAttachmentStore>(_attachments!);

        services.AddSingleton<IOptionsMonitor<ArcanumSettings>>(new TestOptionsMonitor<ArcanumSettings>(_settings));

        services.AddSingleton<ILogger<SessionAttachmentIndexProcessor>>(NullLogger<SessionAttachmentIndexProcessor>.Instance);

        services.AddSingleton(comprehension);

        services.AddSingleton<SessionAttachmentIndexProcessor>();

        return new SessionAttachmentIndexingService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new TestOptionsMonitor<ArcanumSettings>(_settings),
            new GrimoireConnectionAdmissionGate(TimeProvider.System),
            retryTime,
            NullLogger<SessionAttachmentIndexingService>.Instance);
    }

    /// <summary>A parser that stalls past its budget on one poison payload and reads everything else normally.</summary>
    private sealed class PoisonAwareExtractor(byte[] poison) : ISessionAttachmentDocumentExtractor
    {
        private readonly ManualResetEventSlim _release = new(false);

        private readonly TaskCompletionSource _poisonFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _healthyParses;

        internal Task PoisonFinished => _poisonFinished.Task;

        internal int HealthyParses => Volatile.Read(ref _healthyParses);

        internal void Release() => _release.Set();

        public ValueTask ExtractAsync(
            ReadOnlyMemory<byte> bytes,
            ISessionAttachmentDocumentTextSink output,
            CancellationToken cancellationToken)
        {
            if (bytes.Span.SequenceEqual(poison))
            {
                // Deliberately ignores cancellation: the family stays quarantined until the test releases it.
                _release.Wait(TimeSpan.FromSeconds(30));

                _poisonFinished.TrySetResult();

                return ValueTask.CompletedTask;
            }

            Interlocked.Increment(ref _healthyParses);

            return new ComprehensionTextContainerExtractor().ExtractAsync(bytes, output, cancellationToken);
        }
    }

    /// <summary>Completes every retry delay at once, recording what was scheduled.</summary>
    private sealed class ImmediateTimeProvider : TimeProvider
    {
        private readonly List<TimeSpan> _delays = [];

        internal IReadOnlyList<TimeSpan> Delays
        {
            get
            {
                lock (_delays)
                {
                    return [.. _delays];
                }
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_delays)
            {
                _delays.Add(dueTime);
            }

            ThreadPool.QueueUserWorkItem(_ => callback(state));

            return new NoopTimer();
        }

        private sealed class NoopTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class ConstantWeaveService : IWeaveService
    {
        public bool IsAvailable => true;

        public Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken) =>
            Task.FromResult(Result<Embedding<float>>.Success(new Embedding<float>(Vector())));

        public Task<Result<Embedding<float>[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) =>
            Task.FromResult(Result<Embedding<float>[]>.Success([.. texts.Select(_ => new Embedding<float>(Vector()))]));

        public Task<Result<(string Chunk, int Offset)[]>> ChunkAsync(string text, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private static float[] Vector()
        {
            float[] vector = new float[Dimensions];

            vector[0] = 1f;

            return vector;
        }
    }
}

/// <summary>Workbooks for the family-contention and expansion-cost cases, built without any family fixture.</summary>
internal static class ContentionWorkbooks
{
    internal const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    internal const string Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private const string PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>
    /// Two worksheets of about 31 MB of empty rows (one in forty spelled with a different case, so the
    /// parts deflate at about 100:1 rather than past the ratio bound). No cells, so no cell bound applies:
    /// a package of about 620 KB within every package bound that costs seconds to parse and yields nothing.
    /// </summary>
    internal static byte[] SlowRowsWorkbook()
    {
        string[] sheets = [.. Enumerable.Range(0, 2).Select(static index => RowFiller(index, 31_000_000))];

        return Workbook(sheets, sharedStrings: null);
    }

    /// <summary>A legitimate 14,000-row, 6-column workbook with 25,000 shared strings (about 740 KB).</summary>
    internal static byte[] LargeHealthyWorkbook()
    {
        Random random = new(3);

        StringBuilder rows = new();

        StringBuilder strings = new();

        for (int index = 0; index < 25_000; index++)
        {
            strings.Append("<si><t>customer ").Append(random.Next(0, 1_000_000)).Append(" order note</t></si>");
        }

        for (int row = 1; row <= 14_000; row++)
        {
            rows.Append("<row r=\"").Append(row).Append("\">");

            for (int column = 0; column < 6; column++)
            {
                char letter = (char)('A' + column);

                if (column % 2 == 0)
                {
                    rows.Append("<c r=\"").Append(letter).Append(row).Append("\" t=\"s\"><v>").Append(random.Next(0, 25_000)).Append("</v></c>");
                }
                else
                {
                    rows.Append("<c r=\"").Append(letter).Append(row).Append("\"><v>").Append(random.Next(0, 100_000)).Append('.').Append(random.Next(0, 100)).Append("</v></c>");
                }
            }

            rows.Append("</row>");
        }

        return Workbook([rows.ToString()], strings.ToString());
    }

    /// <summary>A workbook of one worksheet part per entry of <paramref name="sheetRows"/> (each the inner XML of sheetData).</summary>
    internal static byte[] Workbook(IReadOnlyList<string> sheetRows, string? sharedStrings)
    {
        List<(string Name, string Content)> entries =
        [
            ("_rels/.rels", Rels($"<Relationship Id=\"rId1\" Type=\"{Relationships}/officeDocument\" Target=\"xl/workbook.xml\"/>")),
        ];

        StringBuilder sheets = new();

        StringBuilder relationships = new();

        for (int index = 0; index < sheetRows.Count; index++)
        {
            sheets.Append($"<sheet name=\"S{index}\" sheetId=\"{index + 1}\" r:id=\"rIdS{index + 1}\"/>");

            relationships.Append($"<Relationship Id=\"rIdS{index + 1}\" Type=\"{Relationships}/worksheet\" Target=\"worksheets/sheet{index + 1}.xml\"/>");

            entries.Add(($"xl/worksheets/sheet{index + 1}.xml", $"<worksheet xmlns=\"{Main}\"><sheetData>{sheetRows[index]}</sheetData></worksheet>"));
        }

        if (sharedStrings is not null)
        {
            relationships.Append($"<Relationship Id=\"rIdSS\" Type=\"{Relationships}/sharedStrings\" Target=\"sharedStrings.xml\"/>");

            entries.Add(("xl/sharedStrings.xml", $"<sst xmlns=\"{Main}\">{sharedStrings}</sst>"));
        }

        entries.Add(("xl/workbook.xml", $"<workbook xmlns=\"{Main}\" xmlns:r=\"{Relationships}\"><sheets>{sheets}</sheets></workbook>"));

        entries.Add(("xl/_rels/workbook.xml.rels", Rels(relationships.ToString())));

        return ComprehensionPackageBuilder.Build([.. entries]);
    }

    internal static string Rels(string relationships) =>
        $"<Relationships xmlns=\"{PackageRelationships}\">{relationships}</Relationships>";

    private static string RowFiller(int seed, int targetCharacters)
    {
        Random random = new(seed);

        StringBuilder rows = new(targetCharacters + 64);

        while (rows.Length < targetCharacters)
        {
            rows.Append(random.Next(0, 40) == 0 ? "<roW/>" : "<row/>");
        }

        return rows.ToString();
    }
}
