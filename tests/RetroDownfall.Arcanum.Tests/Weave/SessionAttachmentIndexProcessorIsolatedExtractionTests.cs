using Microsoft.Extensions.AI;

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

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.Pdf;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

using RetroDownfall.Arcanum.Tests.Weave.Comprehension.Pdf;

namespace RetroDownfall.Arcanum.Tests.Weave;

/// <summary>
/// How the indexer runs a document parsed out of process: inside an effect group of its work lease, refused up front when
/// maintenance owns admission, linked to the lease's revocation, and stood down without spending an attempt when
/// maintenance arrives while the worker runs. An in-process document keeps extracting outside any group, as before.
/// </summary>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class SessionAttachmentIndexProcessorIsolatedExtractionTests : IAsyncLifetime
{
    private const int Dimensions = 64;

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private string _attachmentsRoot = string.Empty;

    private ArcanumDbContext? _db;

    private ArcanumSettings _settings = null!;

    private SessionAttachmentStore? _attachments;

    private SessionAttachmentIndexRepository? _index;

    public SessionAttachmentIndexProcessorIsolatedExtractionTests(GrimoireFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync()
    {
        _dbPath = _fixture.CopyDatabase();

        _attachmentsRoot = Path.Combine(Path.GetTempPath(), "arcanum-attachment-isolated-" + Guid.NewGuid().ToString("N"));

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
    public async Task ProcessAsync_Pdf_RunsItsWorkerInsideAnEffectGroupAndIndexesItsPages()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SessionAttachmentRecord attachment = await PersistPdfAsync("multi-page.pdf");

        FakeWorkLease lease = new();

        bool? groupOpenWhileTheWorkerRan = null;

        SessionAttachmentIsolatedDocumentHost host = IsolatedHost((_, bytes, cancellationToken) =>
        {
            groupOpenWhileTheWorkerRan = lease.OpenGroups == 1;

            return new InProcessPdfWorker().RunAsync(DocumentWorkerFormat.Pdf, bytes, cancellationToken);
        });

        SessionAttachmentIndexOutcome outcome = await ProcessAsync(attachment, host, lease);

        Assert.Equal(SessionAttachmentIndexStatus.Indexed, outcome.Status);

        Assert.True(groupOpenWhileTheWorkerRan);

        // The extraction's group closed with the worker, before the first embedding batch took its own.
        Assert.Equal(0, lease.OpenGroups);
    }

    [SkippableFact]
    public async Task ProcessAsync_InProcessDocument_ExtractsOutsideAnyEffectGroup()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SessionAttachmentRecord attachment = await _attachments!.PersistNewAsync(
            Guid.NewGuid(), null, null, "container", "container.arccomp", "ARCCOMP1\nin-process document"u8.ToArray(),
            SessionAttachmentComprehensionPolicy.MimeType, SessionAttachmentKind.Binary);

        FakeWorkLease lease = new();

        int? openGroupsWhileParsing = null;

        SessionAttachmentDocumentExtractionHost host = new(
            (bytes, output, cancellationToken) =>
            {
                openGroupsWhileParsing = lease.OpenGroups;

                return new ComprehensionTextContainerExtractor().ExtractAsync(bytes, output, cancellationToken);
            },
            TimeSpan.FromSeconds(30));

        SessionAttachmentIndexOutcome outcome = await ProcessAsync(attachment, host, lease);

        Assert.Equal(SessionAttachmentIndexStatus.Indexed, outcome.Status);

        Assert.Equal(0, openGroupsWhileParsing);
    }

    [SkippableFact]
    public async Task ProcessAsync_PdfWhoseEffectGroupIsRefused_IsDeferredForMaintenanceWithoutReadingOrRunning()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SessionAttachmentRecord attachment = await PersistPdfAsync("multi-page.pdf");

        FakeWorkLease lease = new() { RefuseGroups = true };

        int runs = 0;

        SessionAttachmentIsolatedDocumentHost host = IsolatedHost((_, _, _) =>
        {
            runs++;

            return Task.FromResult(DocumentWorkerResult.Completed(PdfExtractionOutcome.Without(PdfExtractionKind.NoText)));
        });

        SessionAttachmentIndexOutcome outcome = await ProcessAsync(attachment, host, lease, attempt: 3);

        Assert.Same(SessionAttachmentIndexOutcome.DeferredForMaintenance, outcome);

        Assert.Equal(0, runs);

        await AssertDurablyPendingAsync(attachment, attempt: 3);
    }

    [SkippableFact]
    public async Task ProcessAsync_RevocationWhileTheWorkerRuns_KillsItAndIsDeferredForMaintenance()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SessionAttachmentRecord attachment = await PersistPdfAsync("multi-page.pdf");

        FakeWorkLease lease = new();

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        CancellationToken workerToken = default;

        SessionAttachmentIsolatedDocumentHost host = IsolatedHost(async (_, _, cancellationToken) =>
        {
            workerToken = cancellationToken;

            entered.TrySetResult();

            await Task.Delay(Timeout.Infinite, cancellationToken);

            throw new InvalidOperationException("unreachable");
        });

        Task<SessionAttachmentIndexOutcome> processing = ProcessAsync(attachment, host, lease, attempt: 2);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await lease.Revocation.CancelAsync();

        SessionAttachmentIndexOutcome outcome = await processing.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(workerToken.IsCancellationRequested, "Revocation did not reach the worker.");

        Assert.Same(SessionAttachmentIndexOutcome.DeferredForMaintenance, outcome);

        Assert.Equal(0, lease.OpenGroups);

        await AssertDurablyPendingAsync(attachment, attempt: 2);
    }

    [SkippableFact]
    public async Task ProcessAsync_MaintenanceBegunWhileTheWorkerRuns_TheGateWaitsForItThenTheDocumentStandsDown()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        // The production gate: maintenance does not cancel a lease whose effect group is open; it waits for the group,
        // and signals revocation when the group closes. The document must then stand down unspent rather than go on to
        // write an index generation maintenance is waiting to take the Grimoire from.
        SessionAttachmentRecord attachment = await PersistPdfAsync("multi-page.pdf");

        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        Assert.True(gate.TryAcquireWorkLease(GrimoireWorkKind.SessionAttachmentIndexing, out IGrimoireWorkLease? lease));

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource<DocumentWorkerResult> release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        CancellationToken workerToken = default;

        SessionAttachmentIsolatedDocumentHost host = IsolatedHost((_, bytes, cancellationToken) =>
        {
            workerToken = cancellationToken;

            entered.TrySetResult();

            return release.Task;
        });

        Task<SessionAttachmentIndexOutcome> processing = ProcessAsync(attachment, host, lease!, attempt: 4);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Result<IGrimoireClosingOwner> closing = gate.BeginOrResumeExclusive(Owner());

        Assert.True(closing.IsSuccess);

        Assert.True(lease!.MaintenanceRevocationPending);

        Assert.False(workerToken.IsCancellationRequested, "The gate cancelled a lease whose effect group was open.");

        release.SetResult(await new InProcessPdfWorker().RunAsync(DocumentWorkerFormat.Pdf, PdfFixtures.Read("multi-page.pdf"), CancellationToken.None));

        SessionAttachmentIndexOutcome outcome = await processing.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Same(SessionAttachmentIndexOutcome.DeferredForMaintenance, outcome);

        await lease.DisposeAsync();

        Assert.True((await gate.DrainRequestAndWorkAsync(closing.Value, CancellationToken.None)).IsSuccess);

        await AssertDurablyPendingAsync(attachment, attempt: 4);
    }

    [SkippableFact]
    public async Task ProcessAsync_PdfFamilyBusyElsewhere_IsDeferredForTheBusyFamily()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SessionAttachmentRecord attachment = await PersistPdfAsync("multi-page.pdf");

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource<DocumentWorkerResult> release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        SessionAttachmentIsolatedDocumentHost host = IsolatedHost((_, _, _) =>
        {
            entered.TrySetResult();

            return release.Task;
        });

        // An interactive caller holds the PDF family's one slot.
        Task<SessionAttachmentExtractionResult> elsewhere = host.ExtractAsync(
            _ => Task.FromResult<ReadOnlyMemory<byte>>("%PDF"u8.ToArray()), 4, 1024, CancellationToken.None);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        try
        {
            // At once: the indexer never queues for the slot, so it cannot hold its lease behind another document.
            SessionAttachmentIndexOutcome outcome = await ProcessAsync(attachment, host, new FakeWorkLease(), attempt: 5)
                .WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Same(SessionAttachmentIndexOutcome.DeferredForBusyFamily, outcome);

            SessionAttachmentIndexState state = await _index!.GetStateAsync(attachment.Id, CancellationToken.None);

            Assert.Equal(SessionAttachmentIndexStatus.Pending, state.Status);

            Assert.Equal(SessionAttachmentDocumentHost.BusyReason, state.FailureReason);

            Assert.Null(state.ExtractedAt);
        }
        finally
        {
            release.SetResult(DocumentWorkerResult.Completed(PdfExtractionOutcome.Without(PdfExtractionKind.NoText)));

            await elsewhere.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    public static TheoryData<string, string> TerminalWorkerResults() => new()
    {
        { "failed", ProcessDocumentExtractionWorker.UnavailableReason },
        { "password", SessionAttachmentIsolatedDocumentHost.PasswordRequiredReason },
        { "malformed", SessionAttachmentIsolatedDocumentHost.MalformedReason },
    };

    [SkippableTheory]
    [MemberData(nameof(TerminalWorkerResults))]
    public async Task ProcessAsync_PdfTheWorkerCannotRead_IsDurablyFailedAndNotRetried(string verdict, string reason)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SessionAttachmentRecord attachment = await PersistPdfAsync("multi-page.pdf");

        DocumentWorkerResult result = verdict switch
        {
            "failed" => DocumentWorkerResult.Failed(ProcessDocumentExtractionWorker.UnavailableReason),
            "password" => DocumentWorkerResult.Completed(PdfExtractionOutcome.Without(PdfExtractionKind.PasswordRequired)),
            _ => DocumentWorkerResult.Completed(PdfExtractionOutcome.Without(PdfExtractionKind.Malformed)),
        };

        SessionAttachmentIndexOutcome outcome = await ProcessAsync(
            attachment,
            IsolatedHost((_, _, _) => Task.FromResult(result)),
            new FakeWorkLease());

        Assert.Equal(SessionAttachmentIndexStatus.Failed, outcome.Status);

        Assert.False(outcome.ShouldRetry);

        SessionAttachmentIndexState state = await _index!.GetStateAsync(attachment.Id, CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexStatus.Failed, state.Status);

        Assert.Equal(reason, state.FailureReason);

        Assert.NotNull(state.ExtractedAt);
    }

    [SkippableFact]
    public async Task ProcessAsync_PdfWithoutText_IsDurablyNotEligible()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SessionAttachmentRecord attachment = await PersistPdfAsync("image-only.pdf");

        SessionAttachmentIndexOutcome outcome = await ProcessAsync(
            attachment,
            IsolatedHost((_, bytes, cancellationToken) => new InProcessPdfWorker().RunAsync(DocumentWorkerFormat.Pdf, bytes, cancellationToken)),
            new FakeWorkLease());

        Assert.Equal(SessionAttachmentIndexStatus.NotEligible, outcome.Status);

        Assert.False(outcome.ShouldRetry);

        Assert.Equal(SessionAttachmentIndexStatus.NotEligible, (await _index!.GetStateAsync(attachment.Id, CancellationToken.None)).Status);
    }

    [SkippableFact]
    public async Task ProcessAsync_HostilePdfStoppedByTheWorkersLimits_IsFailedAndAHealthyPdfBehindItIsIndexed()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        // The registry's own PDF host and process worker, re-launched as the Cli apphost: no host is injected.
        using IDisposable worker = ManagedApphostDocumentWorker.Use();

        SessionAttachmentRecord hostile = await PersistPdfAsync("flate-letters-20m.pdf");

        SessionAttachmentRecord healthy = await PersistPdfAsync("multi-page.pdf");

        SessionAttachmentIndexOutcome stopped = await ProcessAsync(hostile, host: null, new FakeWorkLease());

        Assert.Equal(SessionAttachmentIndexStatus.Failed, stopped.Status);

        Assert.False(stopped.ShouldRetry);

        Assert.Contains(
            (await _index!.GetStateAsync(hostile.Id, CancellationToken.None)).FailureReason,
            new[] { ProcessDocumentExtractionWorker.ResourceLimitsReason, ProcessDocumentExtractionWorker.DeadlineReason });

        SessionAttachmentIndexOutcome indexed = await ProcessAsync(healthy, host: null, new FakeWorkLease());

        Assert.Equal(SessionAttachmentIndexStatus.Indexed, indexed.Status);

        SessionAttachmentIndexedChunk chunk = Assert.Single(await _index.GetChunksForAttachmentAsync(healthy.Id, CancellationToken.None));

        Assert.Equal((1, 4), (chunk.StartLine, chunk.EndLine));
    }

    private async Task AssertDurablyPendingAsync(SessionAttachmentRecord attachment, int attempt)
    {
        SessionAttachmentIndexState state = await _index!.GetStateAsync(attachment.Id, CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexStatus.Pending, state.Status);

        Assert.Equal(attempt, state.AttemptCount);

        Assert.Null(state.ExtractedAt);

        Assert.Null(state.PendingGenerationId);

        Assert.Null(state.PublishedGenerationId);
    }

    private Task<SessionAttachmentRecord> PersistPdfAsync(string fixture) =>
        _attachments!.PersistNewAsync(
            Guid.NewGuid(), null, null, "report", "report.pdf", PdfFixtures.Read(fixture),
            SessionAttachmentComprehensionPolicy.PdfMimeType, SessionAttachmentKind.Binary);

    private Task<SessionAttachmentIndexOutcome> ProcessAsync(
        SessionAttachmentRecord attachment,
        SessionAttachmentDocumentHost? host,
        IGrimoireWorkLease lease,
        int attempt = 0) =>
        new SessionAttachmentIndexProcessor(
                new TestOptionsMonitor<ArcanumSettings>(_settings),
                new UnitWeaveService(),
                _attachments!,
                _index!,
                NullLogger<SessionAttachmentIndexProcessor>.Instance,
                host)
            .ProcessAsync(new SessionAttachmentIndexRequest(attachment.Id, attachment.SessionId!.Value, attempt), lease, CancellationToken.None);

    private static SessionAttachmentIsolatedDocumentHost IsolatedHost(
        Func<DocumentWorkerFormat, ReadOnlyMemory<byte>, CancellationToken, Task<DocumentWorkerResult>> run) =>
        new(new DelegateWorker(run), DocumentWorkerFormat.Pdf);

    private static CovenantExclusiveRecoveryOwner Owner() =>
        new(Guid.NewGuid(), CovenantExclusiveOperation.CovenantReset, new CovenantDigest(Enumerable.Repeat((byte)7, 32).ToArray()));

    private sealed class DelegateWorker(
        Func<DocumentWorkerFormat, ReadOnlyMemory<byte>, CancellationToken, Task<DocumentWorkerResult>> run) : IDocumentExtractionWorker
    {
        public Task<DocumentWorkerResult> RunAsync(DocumentWorkerFormat format, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) =>
            run(format, bytes, cancellationToken);
    }

    /// <summary>A work lease that counts the effect groups it grants and whose revocation the test signals.</summary>
    private sealed class FakeWorkLease : IGrimoireWorkLease
    {
        private int _openGroups;

        public CancellationTokenSource Revocation { get; } = new();

        public bool RefuseGroups { get; init; }

        public int OpenGroups => Volatile.Read(ref _openGroups);

        public GrimoireWorkKind Kind => GrimoireWorkKind.SessionAttachmentIndexing;

        public long Generation => 1;

        public CancellationToken MaintenanceRevocation => Revocation.Token;

        public bool MaintenanceRevocationPending => Revocation.IsCancellationRequested;

        public bool TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? effectGroup)
        {
            if (RefuseGroups || Revocation.IsCancellationRequested)
            {
                effectGroup = null;

                return false;
            }

            // A lease grants one group at a time, as the gate does.
            Assert.Equal(1, Interlocked.Increment(ref _openGroups));

            effectGroup = new Group(this);

            return true;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class Group(FakeWorkLease lease) : IGrimoireExternalEffectGroup
        {
            public ValueTask DisposeAsync()
            {
                Interlocked.Decrement(ref lease._openGroups);

                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class UnitWeaveService : IWeaveService
    {
        public bool IsAvailable => true;

        public Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken) =>
            Task.FromResult(Result<Embedding<float>>.Success(new Embedding<float>(Vector())));

        public Task<Result<Embedding<float>[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) =>
            Task.FromResult(Result<Embedding<float>[]>.Success([.. texts.Select(static _ => new Embedding<float>(Vector()))]));

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
