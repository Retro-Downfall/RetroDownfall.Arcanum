using System.Text;

using Microsoft.Extensions.AI;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Logging;

using Microsoft.Extensions.Logging.Abstractions;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Covenant;

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

            Assert.Equal(SessionAttachmentIndexStatus.Pending, deferred.Status);

            Assert.True(deferred.ShouldRetry, "Contention on a healthy document must take the retry path.");

            SessionAttachmentIndexState transient = await _index!.GetStateAsync(healthy.Id, CancellationToken.None);

            // Never durably terminal: a pending row is what reconciliation re-selects if the retry is lost.
            Assert.Equal(SessionAttachmentIndexStatus.Pending, transient.Status);

            Assert.Null(transient.ExtractedAt);

            Assert.Equal(SessionAttachmentDocumentExtractionHost.BusyReason, transient.FailureReason);

            Assert.Single(retryTime.Delays);

            Assert.True(service.QueueReader.TryRead(out SessionAttachmentIndexRequest? retried));

            // The deferral read and parsed nothing, so its retry spends no attempt.
            Assert.Equal(0, retried!.Attempt);

            Assert.Equal(0, transient.AttemptCount);

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
    /// A deferral is never terminal in durable state: the automatic retry lives in memory, so a host
    /// that stops during its backoff must find the document again by reconciliation after a restart.
    /// </summary>
    [SkippableFact]
    public async Task ProcessOneAsync_DeferredDocument_HostStopsDuringTheBackoff_IsIndexedAfterARestart()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord healthy = await _attachments!.PersistNewAsync(
            sessionId, null, null, "healthy", "healthy.arccomp", Encoding.UTF8.GetBytes("ARCCOMP1\nhealthy document"),
            SessionAttachmentComprehensionPolicy.MimeType, SessionAttachmentKind.Binary);

        byte[] poison = Encoding.UTF8.GetBytes("ARCCOMP1\npoison");

        PoisonAwareExtractor extractor = new(poison);

        SessionAttachmentDocumentExtractionHost host = new(extractor.ExtractAsync, TimeSpan.FromMilliseconds(100));

        SignallingNeverTimeProvider retryTime = new();

        using (SessionAttachmentIndexingService service = CreateService(host, retryTime))
        {
            try
            {
                _ = await host.ExtractAsync(
                    _ => Task.FromResult<ReadOnlyMemory<byte>>(poison), poison.Length, 1024, CancellationToken.None);

                using CancellationTokenSource stopping = new();

                Task<SessionAttachmentIndexOutcome> processing = service.ProcessOneAsync(
                    Dequeue(service, new SessionAttachmentIndexRequest(healthy.Id, sessionId)),
                    stopping.Token);

                // The deferred attempt concluded and its automatic retry waits out its backoff when the host stops.
                await retryTime.Scheduled.WaitAsync(TimeSpan.FromSeconds(10));

                await stopping.CancelAsync();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);
            }
            finally
            {
                extractor.Release();
            }
        }

        await extractor.PoisonFinished.WaitAsync(TimeSpan.FromSeconds(5));

        SessionAttachmentIndexState stopped = await _index!.GetStateAsync(healthy.Id, CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexStatus.Pending, stopped.Status);

        Assert.Null(stopped.ExtractedAt);

        Assert.Equal(SessionAttachmentDocumentExtractionHost.BusyReason, stopped.FailureReason);

        // A busy deferral read and parsed nothing, so it spent no attempt.
        Assert.Equal(0, stopped.AttemptCount);

        // The restart: a fresh service, whose only way back to the document is reconciliation.
        using SessionAttachmentIndexingService restarted = CreateService(host, new ImmediateTimeProvider());

        Assert.Equal(
            SessionAttachmentIndexDisposition.Concluded,
            await restarted.ReconcileAndEnqueueAsync(_settings.ResolveEmbeddings(), CancellationToken.None));

        Assert.True(
            restarted.QueueReader.TryRead(out SessionAttachmentIndexRequest? requeued),
            "Reconciliation did not re-queue a document whose only attempt was deferred.");

        Assert.Equal(healthy.Id, requeued!.AttachmentId);

        SessionAttachmentIndexOutcome final = await ProcessUntilConcludedAsync(restarted, requeued);

        Assert.Equal(SessionAttachmentIndexStatus.Indexed, final.Status);

        Assert.Equal(SessionAttachmentIndexStatus.Indexed, (await _index.GetStateAsync(healthy.Id, CancellationToken.None)).Status);
    }

    /// <summary>
    /// The retry of a deferral is a best-effort enqueue. When other attachments fill the queue during its
    /// backoff the write is refused, and reconciliation, which the queue-full warning names, recovers it.
    /// </summary>
    [SkippableFact]
    public async Task ProcessOneAsync_DeferredDocument_RetryFindsTheQueueFull_IsRecoveredByReconciliation()
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

        int filled = 0;

        retryTime.OnDelay = () =>
        {
            while (service.TryEnqueue(new SessionAttachmentIndexRequest(Guid.NewGuid(), sessionId)))
            {
                filled++;
            }
        };

        try
        {
            _ = await host.ExtractAsync(
                _ => Task.FromResult<ReadOnlyMemory<byte>>(poison), poison.Length, 1024, CancellationToken.None);

            SessionAttachmentIndexOutcome deferred = await service.ProcessOneAsync(
                Dequeue(service, new SessionAttachmentIndexRequest(healthy.Id, sessionId)),
                CancellationToken.None);

            Assert.True(deferred.ShouldRetry);
        }
        finally
        {
            retryTime.OnDelay = null;

            extractor.Release();
        }

        await extractor.PoisonFinished.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(filled > 0, "Other attachments must have filled the queue during the backoff.");

        int healthyRetries = 0;

        while (service.QueueReader.TryRead(out SessionAttachmentIndexRequest? other))
        {
            healthyRetries += other.AttachmentId == healthy.Id ? 1 : 0;
        }

        Assert.Equal(0, healthyRetries);

        Assert.Equal(
            SessionAttachmentIndexDisposition.Concluded,
            await service.ReconcileAndEnqueueAsync(_settings.ResolveEmbeddings(), CancellationToken.None));

        SessionAttachmentIndexRequest? recovered = null;

        while (service.QueueReader.TryRead(out SessionAttachmentIndexRequest? candidate))
        {
            recovered ??= candidate.AttachmentId == healthy.Id ? candidate : null;
        }

        Assert.True(recovered is not null, "Reconciliation did not recover a deferred document whose retry found the queue full.");

        SessionAttachmentIndexOutcome final = await ProcessUntilConcludedAsync(service, recovered);

        Assert.Equal(SessionAttachmentIndexStatus.Indexed, final.Status);
    }

    /// <summary>
    /// The indexer takes a family slot only when it is free. Queueing for it inside the work lease would
    /// hold maintenance's stage-one drain behind other documents' parses, past its five-second bound.
    /// </summary>
    [SkippableFact]
    public async Task ProcessOneAsync_SameFamilyContention_DoesNotHoldTheWorkLeaseAgainstAMaintenanceDrain()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord healthy = await _attachments!.PersistNewAsync(
            sessionId, null, null, "healthy", "healthy.arccomp", Encoding.UTF8.GetBytes("ARCCOMP1\nhealthy document"),
            SessionAttachmentComprehensionPolicy.MimeType, SessionAttachmentKind.Binary);

        byte[] slow = Encoding.UTF8.GetBytes("ARCCOMP1\nslow");

        // The production family shape: a five-second document budget and the default slot wait.
        SessionAttachmentDocumentExtractionHost host = new(
            new SlowExtractor(slow, TimeSpan.FromMilliseconds(4500)).ExtractAsync,
            TimeSpan.FromSeconds(5));

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        SignallingNeverTimeProvider retryTime = new();

        using SessionAttachmentIndexingService service = CreateService(host, retryTime, gate);

        // Two same-family extractions elsewhere (turns, pins), each well within its own budget.
        Task<SessionAttachmentExtractionResult>[] holders =
        [
            Task.Run(() => host.ExtractAsync(_ => Task.FromResult<ReadOnlyMemory<byte>>(slow), slow.Length, 1024, CancellationToken.None)),
            Task.Run(async () =>
            {
                await Task.Delay(50);

                return await host.ExtractAsync(_ => Task.FromResult<ReadOnlyMemory<byte>>(slow), slow.Length, 1024, CancellationToken.None);
            }),
        ];

        await Task.Delay(200);

        using CancellationTokenSource stopping = new();

        Task<SessionAttachmentIndexOutcome> processing = service.ProcessOneAsync(
            Dequeue(service, new SessionAttachmentIndexRequest(healthy.Id, sessionId)),
            stopping.Token);

        await Task.Delay(300);

        // A maintenance transition (backup, encryption, erasure, reset) begins while the family is busy.
        Result<IGrimoireClosingOwner> begun = gate.BeginOrResumeExclusive(
            new CovenantExclusiveRecoveryOwner(
                Guid.NewGuid(),
                CovenantExclusiveOperation.CovenantReset,
                new CovenantDigest(Enumerable.Repeat((byte)7, 32).ToArray())));

        Assert.True(begun.IsSuccess, begun.IsFailure ? begun.Error.Message : null);

        Result drained;

        await using (IGrimoireClosingOwner closing = begun.Value)
        {
            try
            {
                drained = await gate.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(60));
            }
            finally
            {
                _ = await gate.AbortClosingAsync(closing, static _ => ValueTask.FromResult(true), CancellationToken.None);
            }
        }

        await stopping.CancelAsync();

        try
        {
            _ = await processing.WaitAsync(TimeSpan.FromSeconds(60));
        }
        catch (OperationCanceledException)
        {
            // Stopped in the retry backoff, after the lease was given back.
        }

        await Task.WhenAll(holders).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.True(drained.IsSuccess, drained.IsFailure ? drained.Error.Code + " " + drained.Error.Message : null);

        SessionAttachmentIndexState state = await _index!.GetStateAsync(healthy.Id, CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexStatus.Pending, state.Status);

        Assert.Equal(SessionAttachmentDocumentExtractionHost.BusyReason, state.FailureReason);
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
        TimeProvider retryTime,
        GrimoireConnectionAdmissionGate? gate = null)
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
            gate ?? new GrimoireConnectionAdmissionGate(TimeProvider.System),
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

    /// <summary>A cooperative parser that takes a fixed time on one payload and reads everything else normally.</summary>
    private sealed class SlowExtractor(byte[] slow, TimeSpan duration) : ISessionAttachmentDocumentExtractor
    {
        public async ValueTask ExtractAsync(
            ReadOnlyMemory<byte> bytes,
            ISessionAttachmentDocumentTextSink output,
            CancellationToken cancellationToken)
        {
            if (bytes.Span.SequenceEqual(slow))
            {
                await Task.Delay(duration, cancellationToken);

                _ = output.TryAppend("slow".AsSpan());

                return;
            }

            await new ComprehensionTextContainerExtractor().ExtractAsync(bytes, output, cancellationToken);
        }
    }

    /// <summary>A retry backoff that never elapses on its own, and says when it was scheduled.</summary>
    private sealed class SignallingNeverTimeProvider : TimeProvider
    {
        private readonly TaskCompletionSource _scheduled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Scheduled => _scheduled.Task;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _scheduled.TrySetResult();

            return new NoopTimer();
        }
    }

    /// <summary>Completes every retry delay at once, recording what was scheduled.</summary>
    private sealed class ImmediateTimeProvider : TimeProvider
    {
        private readonly List<TimeSpan> _delays = [];

        /// <summary>Runs as each delay is scheduled, before it elapses.</summary>
        internal Action? OnDelay { get; set; }

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

            OnDelay?.Invoke();

            ThreadPool.QueueUserWorkItem(_ => callback(state));

            return new NoopTimer();
        }
    }

    private sealed class NoopTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
