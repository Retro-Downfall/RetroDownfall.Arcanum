using System.Collections.Concurrent;

using System.Diagnostics;

using System.Reflection;

using System.Runtime.ExceptionServices;

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

using RetroDownfall.Arcanum.Infrastructure.Repositories;

using RetroDownfall.Arcanum.Infrastructure.Storage;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

using RetroDownfall.Arcanum.Tests.Weave.Comprehension.Pdf;

namespace RetroDownfall.Arcanum.Tests.Weave;

/// <summary>
/// PDFs through the real <see cref="SessionAttachmentIndexingService"/> dequeue path (the work lease from the real
/// admission gate, the production-shaped processor with no host injected so the registry's PDF host is used, a real
/// Grimoire) and, wherever the test says so, the real worker: the managed Cli apphost re-launched through the production
/// launch under the real jail, with no network, a cleared environment and the code-owned limits.
/// </summary>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class SessionAttachmentPdfWorkerIndexingTests : IAsyncLifetime
{
    private const int Dimensions = 64;

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private string _attachmentsRoot = string.Empty;

    private ArcanumDbContext? _db;

    private ArcanumSettings _settings = null!;

    private SessionAttachmentStore? _attachments;

    private SessionAttachmentIndexRepository? _index;

    public SessionAttachmentPdfWorkerIndexingTests(GrimoireFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync()
    {
        _dbPath = _fixture.CopyDatabase();

        _attachmentsRoot = Path.Combine(Path.GetTempPath(), "arcanum-attachment-pdf-service-" + Guid.NewGuid().ToString("N"));

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

    // ---------------------------------------------------------------- a hostile PDF is stopped by the clock

    [SkippableFact]
    public async Task ProcessOneAsync_PdfThatWouldSpinForever_IsStoppedByTheDeadlineAndTheHealthyPdfQueuedBehindItIsIndexed()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        // A chain of 30 forms each invoking the next twice: about 40 minutes of CPU and under 60 MB, so neither a memory
        // nor a CPU limit ends it. The real worker's clock does, and the document after it is unaffected.
        using IDisposable worker = ManagedApphostDocumentWorker.Use();

        const string marker = "fox number 75 jumps over the lazy dog 525 times";

        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord hostile = await PersistAsync(sessionId, "xobject-fan-out.pdf");

        SessionAttachmentRecord healthy = await PersistAsync(sessionId, "cups-multi.pdf");

        using SessionAttachmentIndexingService service = CreateService(new MarkerWeaveService(marker));

        Assert.True(service.TryEnqueue(new SessionAttachmentIndexRequest(hostile.Id, sessionId)));

        Assert.True(service.TryEnqueue(new SessionAttachmentIndexRequest(healthy.Id, sessionId)));

        Assert.True(service.QueueReader.TryRead(out SessionAttachmentIndexRequest? first));

        Assert.Equal(hostile.Id, first!.AttachmentId);

        Stopwatch clock = Stopwatch.StartNew();

        SessionAttachmentIndexOutcome stopped = await service.ProcessOneAsync(first, CancellationToken.None);

        clock.Stop();

        // Neither a crash nor a refusal: it ran until the worker's own 8 s deadline ended it, well inside the 10 s wall clock.
        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(7), $"The hostile PDF ended after {clock.Elapsed}: not the clock.");

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(25), $"The hostile PDF held the indexer for {clock.Elapsed}.");

        Assert.Equal(SessionAttachmentIndexDisposition.Concluded, stopped.Disposition);

        Assert.Equal(SessionAttachmentIndexStatus.Failed, stopped.Status);

        Assert.False(stopped.ShouldRetry);

        SessionAttachmentIndexState hostileState = await _index!.GetStateAsync(hostile.Id, CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexStatus.Failed, hostileState.Status);

        Assert.Equal(DocumentWorkerProfile.Pdf.DeadlineReason, hostileState.FailureReason);

        Assert.NotNull(hostileState.ExtractedAt);

        Assert.Empty(await _index.GetChunksForAttachmentAsync(hostile.Id, CancellationToken.None));

        // The healthy PDF, queued behind it, is dequeued next and reaches Indexed; the stopped one was not re-queued.
        Assert.True(service.QueueReader.TryRead(out SessionAttachmentIndexRequest? second));

        Assert.Equal(healthy.Id, second!.AttachmentId);

        Assert.False(service.QueueReader.TryRead(out _), "The terminal failure was queued again.");

        SessionAttachmentIndexOutcome indexed = await service.ProcessOneAsync(second, CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexStatus.Indexed, indexed.Status);

        SessionAttachmentIndexState healthyState = await _index.GetStateAsync(healthy.Id, CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexStatus.Indexed, healthyState.Status);

        Assert.Null(healthyState.FailureReason);

        // The page-3 line, found by search through the real worker's output, reports page 3 and not page 1.
        SessionAttachmentRetrievedChunk[] hits = await SearchAsync(sessionId, new MarkerWeaveService(marker));

        Assert.NotEmpty(hits);

        Assert.All(hits, static hit =>
        {
            Assert.Contains(marker, hit.Content, StringComparison.Ordinal);

            Assert.Equal((3, 3), (hit.StartLine, hit.EndLine));
        });

        string sourceLine = File.ReadLines(Path.Combine(AppContext.BaseDirectory, "TestData", "Comprehension", "Pdf", "cups-multi.txt"))
            .Single(static line => line.StartsWith("Line 075:", StringComparison.Ordinal));

        Assert.Contains(PdfFixtures.WithoutWhitespace(sourceLine), PdfFixtures.WithoutWhitespace(hits[0].Content), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- PDFs no reader can open

    public static TheoryData<string, string> UnreadablePdfs() => new()
    {
        { "encrypted-rc4-password.pdf", DocumentWorkerProfile.Pdf.PasswordRequiredReason },
        { "encrypted-aes-password.pdf", DocumentWorkerProfile.Pdf.PasswordRequiredReason },
        { "truncated.pdf", DocumentWorkerProfile.Pdf.MalformedReason },
        { "xref-stream-garbled.pdf", DocumentWorkerProfile.Pdf.MalformedReason },
        { "bad-trailer.pdf", DocumentWorkerProfile.Pdf.MalformedReason },
        { "missing-root.pdf", DocumentWorkerProfile.Pdf.MalformedReason },
        { "garbage.pdf", DocumentWorkerProfile.Pdf.MalformedReason },
    };

    [SkippableTheory]
    [MemberData(nameof(UnreadablePdfs))]
    public async Task ProcessOneAsync_PdfNoReaderCanOpen_FailsForGoodWithABoundedReasonAndIsNeverRetried(string fixture, string reason)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using IDisposable worker = ManagedApphostDocumentWorker.Use();

        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord attachment = await PersistAsync(sessionId, fixture);

        using SessionAttachmentIndexingService service = CreateService(new MarkerWeaveService("unused"));

        SessionAttachmentIndexOutcome outcome = await service.ProcessOneAsync(Dequeue(service, attachment, sessionId), CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexDisposition.Concluded, outcome.Disposition);

        Assert.Equal(SessionAttachmentIndexStatus.Failed, outcome.Status);

        Assert.False(outcome.ShouldRetry);

        Assert.False(service.QueueReader.TryRead(out _), "A terminal failure was queued for another attempt.");

        SessionAttachmentIndexState state = await _index!.GetStateAsync(attachment.Id, CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexStatus.Failed, state.Status);

        Assert.Equal(reason, state.FailureReason);

        Assert.NotNull(state.ExtractedAt);

        // The reason names the condition and nothing else: no parser type, no file name, no path, no text of the document.
        foreach (string leak in new[] { "Exception", "PdfPig", "UglyToad", "System.", fixture, _attachmentsRoot, Path.GetTempPath(), "ENCRYPTED-CODEWORD", "\n" })
        {
            Assert.DoesNotContain(leak, state.FailureReason!, StringComparison.Ordinal);
        }

        Assert.Empty(await _index.GetChunksForAttachmentAsync(attachment.Id, CancellationToken.None));
    }

    [SkippableFact]
    public async Task ProcessOneAsync_ImageOnlyPdf_IsNotEligibleAndNotFailed()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using IDisposable worker = ManagedApphostDocumentWorker.Use();

        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord scan = await PersistAsync(sessionId, "image-only.pdf");

        SessionAttachmentRecord locked = await PersistAsync(sessionId, "encrypted-aes-password.pdf");

        using SessionAttachmentIndexingService service = CreateService(new MarkerWeaveService("unused"));

        SessionAttachmentIndexOutcome nothingToRead = await service.ProcessOneAsync(Dequeue(service, scan, sessionId), CancellationToken.None);

        SessionAttachmentIndexOutcome cannotRead = await service.ProcessOneAsync(Dequeue(service, locked, sessionId), CancellationToken.None);

        // The two are distinct: a scan has no text layer to read and is the defined input for recognition later, while a
        // locked document is a failure. Neither is retried.
        Assert.Equal(SessionAttachmentIndexStatus.NotEligible, nothingToRead.Status);

        Assert.False(nothingToRead.ShouldRetry);

        Assert.Equal(SessionAttachmentIndexStatus.Failed, cannotRead.Status);

        SessionAttachmentIndexState scanState = await _index!.GetStateAsync(scan.Id, CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexStatus.NotEligible, scanState.Status);

        Assert.Null(scanState.FailureReason);

        Assert.Empty(await _index.GetChunksForAttachmentAsync(scan.Id, CancellationToken.None));

        Assert.False(service.QueueReader.TryRead(out _));
    }

    // ---------------------------------------------------------------- text, including text no reader sees

    [SkippableTheory]
    [InlineData("hidden.pdf", "WHITE-ON-WHITE-CHARLIE")]
    [InlineData("hidden.pdf", "OFFPAGE-ECHO")]
    [InlineData("hidden.pdf", "INVISIBLE-LINE-BRAVO")]
    [InlineData("hidden.pdf", "TINY-TEXT-DELTA")]
    public async Task ProcessOneAsync_TextAReaderWouldNotSee_IsExtractedByTheRealWorkerIndexedAndFoundBySearch(string fixture, string marker)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using IDisposable worker = ManagedApphostDocumentWorker.Use();

        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord attachment = await PersistAsync(sessionId, fixture);

        using SessionAttachmentIndexingService service = CreateService(new MarkerWeaveService(marker));

        SessionAttachmentIndexOutcome outcome = await service.ProcessOneAsync(Dequeue(service, attachment, sessionId), CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexStatus.Indexed, outcome.Status);

        SessionAttachmentRetrievedChunk hit = Assert.Single(await SearchAsync(sessionId, new MarkerWeaveService(marker)));

        // The exact source string, in the text the model would be given, with its page.
        Assert.Contains(marker, hit.Content, StringComparison.Ordinal);

        Assert.StartsWith("[PDF page 1]\n", hit.Content, StringComparison.Ordinal);

        Assert.Equal((1, 1), (hit.StartLine, hit.EndLine));
    }

    [SkippableFact]
    public async Task ProcessOneAsync_PdfWithActiveContent_IndexesItsVisibleTextAndNoPayload()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using IDisposable worker = ManagedApphostDocumentWorker.Use();

        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord attachment = await PersistAsync(sessionId, "active-content.pdf");

        using SessionAttachmentIndexingService service = CreateService(new MarkerWeaveService("unused"));

        SessionAttachmentIndexOutcome outcome = await service.ProcessOneAsync(Dequeue(service, attachment, sessionId), CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexStatus.Indexed, outcome.Status);

        SessionAttachmentIndexedChunk chunk = Assert.Single(await _index!.GetChunksForAttachmentAsync(attachment.Id, CancellationToken.None));

        Assert.Equal("[PDF page 1]\nACTIVE-CONTENT-VISIBLE-TEXT", chunk.Content.TrimEnd('\n'));

        foreach (string payload in new[]
        {
            "JS-ACTION-SENTINEL", "LAUNCH-ACTION-SENTINEL", "EMBEDDED-FILE-SENTINEL-CONTENT", "uri-sentinel.invalid",
            "FORM-FIELD-SENTINEL-VALUE", "FORM-APPEARANCE-SENTINEL", "attached.txt", "cmd.exe",
        })
        {
            Assert.DoesNotContain(payload, chunk.Content, StringComparison.Ordinal);
        }
    }

    // ---------------------------------------------------------------- the gates off: byte-identical to before

    [SkippableTheory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task ProcessOneAsync_PdfWithAGateOff_IsNotEligibleNeverReadAndNoWorkerStarts(bool attachmentRetrieval, bool comprehension)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord attachment = await PersistAsync(sessionId, "multi-page.pdf");

        // Persisting reads nothing; from here on every call on the store is recorded.
        _settings.Features.AttachmentRetrieval = attachmentRetrieval;

        _settings.Features.Comprehension = comprehension;

        using IDisposable worker = InProcessPdfWorker.Use(out InProcessPdfWorker spawned);

        RecordingStore store = RecordingStore.Wrap(_attachments!);

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        using SessionAttachmentIndexingService service = CreateService(new MarkerWeaveService("unused"), gate, store.Proxy);

        // The queue refuses nothing a PDF needs: with retrieval off it refuses at once, and with it on the processor judges.
        SessionAttachmentIndexRequest request = new(attachment.Id, sessionId);

        SessionAttachmentIndexOutcome outcome = await service.ProcessOneAsync(request, CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexDisposition.Concluded, outcome.Disposition);

        Assert.Equal(SessionAttachmentIndexStatus.NotEligible, outcome.Status);

        Assert.False(outcome.ShouldRetry);

        Assert.Equal(0, spawned.Runs);

        Assert.DoesNotContain(store.Calls, static call => call.Contains("Read", StringComparison.Ordinal));

        Assert.Empty(await _index!.GetChunksForAttachmentAsync(attachment.Id, CancellationToken.None));

        if (!attachmentRetrieval)
        {
            // The parent gate off: the processor concludes before it opens the row, so nothing at all is written.
            _ = await Assert.ThrowsAsync<InvalidOperationException>(() => _index.GetStateAsync(attachment.Id, CancellationToken.None));

            return;
        }

        // With retrieval on and the document gate off the processor judges the PDF, and the verdict is durable: not
        // eligible, with no reason, no extraction time and nothing indexed. That is what a PDF was before this feature.
        SessionAttachmentIndexState state = await _index.GetStateAsync(attachment.Id, CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexStatus.NotEligible, state.Status);

        Assert.Null(state.FailureReason);

        Assert.Null(state.ExtractedAt);
    }

    // ---------------------------------------------------------------- maintenance pending: no new worker starts

    [SkippableFact]
    public async Task ProcessOneAsync_MaintenanceOwnsAdmission_StartsNoWorkerAndKeepsTheRequestForReopen()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord attachment = await PersistAsync(sessionId, "multi-page.pdf");

        using IDisposable worker = InProcessPdfWorker.Use(out InProcessPdfWorker spawned);

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        Result<IGrimoireClosingOwner> closing = gate.BeginOrResumeExclusive(Owner());

        Assert.True(closing.IsSuccess);

        using SessionAttachmentIndexingService service = CreateService(new MarkerWeaveService("unused"), gate);

        SessionAttachmentIndexOutcome outcome = await service.ProcessOneAsync(Dequeue(service, attachment, sessionId), CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexDisposition.DeferredForMaintenance, outcome.Disposition);

        Assert.Equal(0, spawned.Runs);

        Assert.Equal(attachment.Id, Assert.Single(service.DeferredRequests).AttachmentId);

        // The processor was never created, so not even a row was opened: nothing about the document changed.
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => _index!.GetStateAsync(attachment.Id, CancellationToken.None));

        _ = await gate.AbortClosingAsync(closing.Value, static _ => ValueTask.FromResult(true), CancellationToken.None);

        await closing.Value.DisposeAsync();
    }

    [SkippableFact]
    public async Task ProcessAsync_MaintenanceBegunAfterTheLeaseWasTaken_RefusesTheEffectGroupAndStartsNoWorker()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        // The lease predates the maintenance request, so the processor is already running when maintenance becomes
        // pending. With no group open the real gate revokes the lease at once and refuses the effect group the worker
        // needs. Four layers each stop the worker on their own (the processor's check of the revocation before the read,
        // the group refusal after it, the worker step's check of the revocation, the worker's check of its token), so this
        // test with the real gate fails only if all of them are gone; the refusal by itself is pinned against a lease that
        // revokes nothing in SessionAttachmentIndexProcessorIsolatedExtractionTests.
        SessionAttachmentRecord attachment = await PersistAsync(Guid.NewGuid(), "multi-page.pdf");

        using IDisposable worker = InProcessPdfWorker.Use(out InProcessPdfWorker spawned);

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        Assert.True(gate.TryAcquireWorkLease(GrimoireWorkKind.SessionAttachmentIndexing, out IGrimoireWorkLease? lease));

        Result<IGrimoireClosingOwner> closing = gate.BeginOrResumeExclusive(Owner());

        Assert.True(closing.IsSuccess);

        Assert.True(lease!.MaintenanceRevocationPending);

        // The gate's own answer, before the processor asks: no group while maintenance is pending.
        Assert.False(lease.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? refused));

        Assert.Null(refused);

        RecordingStore store = RecordingStore.Wrap(_attachments!);

        SessionAttachmentIndexOutcome outcome = await new SessionAttachmentIndexProcessor(
                new TestOptionsMonitor<ArcanumSettings>(_settings),
                new MarkerWeaveService("unused"),
                store.Proxy,
                _index!,
                NullLogger<SessionAttachmentIndexProcessor>.Instance)
            .ProcessAsync(new SessionAttachmentIndexRequest(attachment.Id, attachment.SessionId!.Value, Attempt: 3), lease, CancellationToken.None);

        Assert.Same(SessionAttachmentIndexOutcome.DeferredForMaintenance, outcome);

        Assert.Equal(0, spawned.Runs);

        // A lease maintenance already revoked reads nothing either.
        Assert.DoesNotContain(nameof(ISessionAttachmentStore.ReadBytesAsync), store.Calls);

        await lease.DisposeAsync();

        Assert.True((await gate.DrainRequestAndWorkAsync(closing.Value, CancellationToken.None)).IsSuccess);

        SessionAttachmentIndexState state = await _index!.GetStateAsync(attachment.Id, CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexStatus.Pending, state.Status);

        Assert.Equal(3, state.AttemptCount);

        Assert.Null(state.ExtractedAt);
    }

    [SkippableFact]
    public async Task ProcessAsync_MaintenanceBegunDuringTheSnapshotRead_StartsNoWorkerAndTheDrainSucceeds()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        // The snapshot is read before any effect group is begun, so maintenance that arrives during the read finds no
        // open group: the gate revokes the lease at once and refuses the group the worker would need. The refused group
        // stands the document down, so no worker starts whose verdict would be thrown away and the drain waits for none.
        SessionAttachmentRecord attachment = await PersistAsync(Guid.NewGuid(), "multi-page.pdf");

        using IDisposable worker = InProcessPdfWorker.Use(out InProcessPdfWorker spawned);

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        Assert.True(gate.TryAcquireWorkLease(GrimoireWorkKind.SessionAttachmentIndexing, out IGrimoireWorkLease? lease));

        Result<IGrimoireClosingOwner>? closing = null;

        Task<Result>? drain = null;

        bool? revokedDuringTheRead = null;

        RecordingStore store = RecordingStore.Wrap(_attachments!);

        store.BeforeRead = () =>
        {
            closing = gate.BeginOrResumeExclusive(Owner());

            Assert.True(closing.IsSuccess);

            revokedDuringTheRead = lease!.MaintenanceRevocation.IsCancellationRequested;

            drain = gate.DrainRequestAndWorkAsync(closing.Value, CancellationToken.None).AsTask();
        };

        SessionAttachmentIndexOutcome outcome = await new SessionAttachmentIndexProcessor(
                new TestOptionsMonitor<ArcanumSettings>(_settings),
                new MarkerWeaveService("unused"),
                store.Proxy,
                _index!,
                NullLogger<SessionAttachmentIndexProcessor>.Instance)
            .ProcessAsync(new SessionAttachmentIndexRequest(attachment.Id, attachment.SessionId!.Value, Attempt: 3), lease!, CancellationToken.None);

        Assert.Same(SessionAttachmentIndexOutcome.DeferredForMaintenance, outcome);

        Assert.Equal(0, spawned.Runs);

        Assert.Contains(nameof(ISessionAttachmentStore.ReadBytesAsync), store.Calls);

        // No group was open while the snapshot was read, so the gate revoked the lease the moment maintenance began.
        Assert.True(revokedDuringTheRead);

        await lease!.DisposeAsync();

        Assert.True((await drain!.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);

        SessionAttachmentIndexState state = await _index!.GetStateAsync(attachment.Id, CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexStatus.Pending, state.Status);

        Assert.Equal(3, state.AttemptCount);

        Assert.Null(state.ExtractedAt);

        Assert.Empty(await _index.GetChunksForAttachmentAsync(attachment.Id, CancellationToken.None));

        // The snapshot gave the registry's PDF slot back when the refused group stood the document down, so under a lease
        // of a gate with no maintenance the next PDF is read, run and indexed rather than deferred as busy.
        SessionAttachmentRecord next = await PersistAsync(Guid.NewGuid(), "multi-page.pdf");

        Assert.True(new GrimoireConnectionAdmissionGate(TimeProvider.System).TryAcquireWorkLease(GrimoireWorkKind.SessionAttachmentIndexing, out IGrimoireWorkLease? nextLease));

        await using (nextLease)
        {
            SessionAttachmentIndexOutcome indexed = await new SessionAttachmentIndexProcessor(
                    new TestOptionsMonitor<ArcanumSettings>(_settings),
                    new MarkerWeaveService("unused"),
                    _attachments!,
                    _index!,
                    NullLogger<SessionAttachmentIndexProcessor>.Instance)
                .ProcessAsync(new SessionAttachmentIndexRequest(next.Id, next.SessionId!.Value, Attempt: 0), nextLease!, CancellationToken.None);

            Assert.Equal(SessionAttachmentIndexStatus.Indexed, indexed.Status);
        }

        Assert.Equal(1, spawned.Runs);
    }

    // ---------------------------------------------------------------- helpers

    private Task<SessionAttachmentRecord> PersistAsync(Guid sessionId, string fixture) =>
        _attachments!.PersistNewAsync(
            sessionId, null, null, "report", "report.pdf", PdfFixtures.Read(fixture),
            SessionAttachmentComprehensionPolicy.PdfMimeType, SessionAttachmentKind.Binary);

    private static SessionAttachmentIndexRequest Dequeue(
        SessionAttachmentIndexingService service,
        SessionAttachmentRecord attachment,
        Guid sessionId)
    {
        Assert.True(service.TryEnqueue(new SessionAttachmentIndexRequest(attachment.Id, sessionId)));

        Assert.True(service.QueueReader.TryRead(out SessionAttachmentIndexRequest? dequeued));

        return dequeued!;
    }

    /// <summary>The production-shaped service: the processor is resolved from a scope with no document host registered.</summary>
    private SessionAttachmentIndexingService CreateService(
        IWeaveService weave,
        GrimoireConnectionAdmissionGate? gate = null,
        ISessionAttachmentStore? store = null)
    {
        ServiceCollection services = new();

        services.AddSingleton(_index!);

        services.AddSingleton<ISessionAttachmentIndexWriter>(_index!);

        services.AddSingleton(weave);

        services.AddSingleton(store ?? _attachments!);

        services.AddSingleton<IOptionsMonitor<ArcanumSettings>>(new TestOptionsMonitor<ArcanumSettings>(_settings));

        services.AddSingleton<ILogger<SessionAttachmentIndexProcessor>>(NullLogger<SessionAttachmentIndexProcessor>.Instance);

        services.AddScoped<SessionAttachmentIndexProcessor>();

        return new SessionAttachmentIndexingService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new TestOptionsMonitor<ArcanumSettings>(_settings),
            gate ?? new GrimoireConnectionAdmissionGate(TimeProvider.System),
            TimeProvider.System,
            NullLogger<SessionAttachmentIndexingService>.Instance);
    }

    private Task<SessionAttachmentRetrievedChunk[]> SearchAsync(Guid sessionId, MarkerWeaveService weave) =>
        new SessionAttachmentRetrievalService(
                new TestOptionsMonitor<ArcanumSettings>(_settings),
                new DivinationService(
                    _db!,
                    new WeaveIndexAvailability(),
                    NullLogger<DivinationService>.Instance,
                    new TestOptionsMonitor<ArcanumSettings>(_settings)),
                _index!,
                NullLogger<SessionAttachmentRetrievalService>.Instance)
            .SearchAsync(sessionId, new Embedding<float>(weave.QueryVector()), false, CancellationToken.None);

    private static CovenantExclusiveRecoveryOwner Owner() =>
        new(Guid.NewGuid(), CovenantExclusiveOperation.CovenantReset, new CovenantDigest(Enumerable.Repeat((byte)7, 32).ToArray()));

    /// <summary>An embedding that places text holding the marker on one axis and every other text on another.</summary>
    private sealed class MarkerWeaveService(string marker) : IWeaveService
    {
        public bool IsAvailable => true;

        public float[] QueryVector() => Vector(matches: true);

        private static float[] Vector(bool matches)
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

    /// <summary>A pass-through over the real store that records the name of every call, to prove what was never called.</summary>
    public class RecordingStore : DispatchProxy
    {
        private ISessionAttachmentStore _target = null!;

        private readonly ConcurrentQueue<string> _calls = new();

        public ISessionAttachmentStore Proxy => (ISessionAttachmentStore)(object)this;

        public IReadOnlyCollection<string> Calls => _calls;

        /// <summary>Runs inside the store's snapshot read, before the real read starts.</summary>
        public Action? BeforeRead { get; set; }

        public static RecordingStore Wrap(ISessionAttachmentStore target)
        {
            ISessionAttachmentStore proxy = Create<ISessionAttachmentStore, RecordingStore>();

            RecordingStore recording = (RecordingStore)(object)proxy;

            recording._target = target;

            return recording;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);

            _calls.Enqueue(targetMethod.Name);

            if (targetMethod.Name == nameof(ISessionAttachmentStore.ReadBytesAsync))
            {
                BeforeRead?.Invoke();
            }

            try
            {
                return targetMethod.Invoke(_target, args);
            }
            catch (TargetInvocationException exception)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException!).Throw();

                throw;
            }
        }
    }
}
