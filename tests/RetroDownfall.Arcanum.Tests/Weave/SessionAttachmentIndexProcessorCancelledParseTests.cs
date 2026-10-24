using System.Diagnostics;

using Microsoft.Extensions.AI;

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
/// An in-process parse the indexer's host abandons by cancellation (the host stops while a parser that ignores cancellation
/// still runs) keeps the family quarantined until it really ends. The indexer never queues for a slot itself, so the
/// quarantine is observed by the interactive callers (attach, refresh, reference, pin) that share the family: one already
/// queued behind the parse is released at once, and a later one is refused at once, neither waiting out the slot wait.
/// </summary>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class SessionAttachmentIndexProcessorCancelledParseTests(GrimoireFixture fixture) : IAsyncLifetime
{
    private const int Dimensions = 64;

    /// <summary>
    /// The longest a refusal that the quarantine makes immediate may take. Without the quarantine the caller waits out the
    /// whole slot wait, which is twelve times as long.
    /// </summary>
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan SlotWait = TimeSpan.FromSeconds(60);

    private static readonly byte[] Interactive = "ARCCOMP1\nINTERACTIVE"u8.ToArray();

    private string _dbPath = string.Empty;

    private string _attachmentsRoot = string.Empty;

    private ArcanumDbContext? _db;

    private ArcanumSettings _settings = null!;

    private SessionAttachmentStore? _attachments;

    private SessionAttachmentIndexRepository? _index;

    public Task InitializeAsync()
    {
        _dbPath = fixture.CopyDatabase();

        _attachmentsRoot = Path.Combine(Path.GetTempPath(), "arcanum-attachment-cancelled-parse-" + Guid.NewGuid().ToString("N"));

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
    public async Task ProcessAsync_HostCancellationWhileAnInProcessParseRuns_QuarantinesTheFamilyForInteractiveCallersUntilTheParseEnds()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        SessionAttachmentRecord abandoned = await PersistContainerAsync("poison");

        SessionAttachmentRecord next = await PersistContainerAsync("healthy document");

        using ManualResetEventSlim release = new(false);

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

        SessionAttachmentDocumentExtractionHost host = new(
            (bytes, output, cancellationToken) =>
            {
                if (bytes.Span.EndsWith("poison"u8))
                {
                    entered.TrySetResult();

                    // An uncooperative parser: it ignores cancellation, so its caller abandons it still running.
                    release.Wait(TimeSpan.FromSeconds(30));

                    ended.TrySetResult();

                    return ValueTask.CompletedTask;
                }

                return new ComprehensionTextContainerExtractor().ExtractAsync(bytes, output, cancellationToken);
            },
            TimeSpan.FromSeconds(30),
            slotWait: SlotWait);

        using CancellationTokenSource shutdown = new();

        try
        {
            Task<SessionAttachmentIndexOutcome> processing = ProcessAsync(abandoned, host, shutdown.Token);

            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            int reads = 0;

            // An interactive caller queues for the slot behind the indexer's parse, which the indexer itself never does.
            Task<SessionAttachmentExtractionResult> queued = host.ExtractAsync(
                _ =>
                {
                    Interlocked.Increment(ref reads);

                    return Task.FromResult<ReadOnlyMemory<byte>>(Interactive);
                },
                Interactive.Length,
                1024,
                CancellationToken.None,
                waitForSlot: true);

            Assert.False(queued.IsCompleted);

            await shutdown.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing.WaitAsync(TimeSpan.FromSeconds(10)));

            // The abandonment released the caller already queued, and refuses the next at once, whether or not it would wait.
            AssertDeferred(await PromptlyAsync(queued, "A caller queued before the host stopped"));

            AssertDeferred(await PromptlyAsync(
                host.ExtractAsync(
                    _ =>
                    {
                        Interlocked.Increment(ref reads);

                        return Task.FromResult<ReadOnlyMemory<byte>>(Interactive);
                    },
                    Interactive.Length,
                    1024,
                    CancellationToken.None,
                    waitForSlot: true),
                "A caller that would wait for the slot"));

            Assert.Equal(0, Volatile.Read(ref reads));

            // The indexer's own next document is deferred as busy until the parse ends.
            Assert.Same(
                SessionAttachmentIndexOutcome.DeferredForBusyFamily,
                await ProcessAsync(next, host, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            release.Set();
        }

        await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Once the parse really ends it gives the slot back, and the family takes the next document again.
        Stopwatch clock = Stopwatch.StartNew();

        SessionAttachmentIndexOutcome recovered;

        while ((recovered = await ProcessAsync(next, host, CancellationToken.None)) == SessionAttachmentIndexOutcome.DeferredForBusyFamily
            && clock.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(10);
        }

        Assert.Equal(SessionAttachmentIndexStatus.Indexed, recovered.Status);
    }

    private static async Task<T> PromptlyAsync<T>(Task<T> task, string caller)
    {
        Task finished = await Task.WhenAny(task, Task.Delay(Prompt));

        Assert.True(
            ReferenceEquals(finished, task),
            $"{caller} was not refused within {Prompt}: it is still queued behind a parse its caller abandoned.");

        return await task;
    }

    private static void AssertDeferred(SessionAttachmentExtractionResult refusal)
    {
        Assert.Equal(SessionAttachmentExtractionStatus.Deferred, refusal.Status);

        Assert.Equal(SessionAttachmentDocumentHost.BusyReason, refusal.FailureReason);

        Assert.Empty(refusal.Text);
    }

    private Task<SessionAttachmentRecord> PersistContainerAsync(string text) =>
        _attachments!.PersistNewAsync(
            Guid.NewGuid(), null, null, "container", "container.arccomp", System.Text.Encoding.UTF8.GetBytes("ARCCOMP1\n" + text),
            SessionAttachmentComprehensionPolicy.MimeType, SessionAttachmentKind.Binary);

    private Task<SessionAttachmentIndexOutcome> ProcessAsync(
        SessionAttachmentRecord attachment,
        SessionAttachmentDocumentHost host,
        CancellationToken cancellationToken) =>
        new SessionAttachmentIndexProcessor(
                new TestOptionsMonitor<ArcanumSettings>(_settings),
                new UnitWeaveService(),
                _attachments!,
                _index!,
                NullLogger<SessionAttachmentIndexProcessor>.Instance,
                host)
            .ProcessAsync(new SessionAttachmentIndexRequest(attachment.Id, attachment.SessionId!.Value, 0), new IdleWorkLease(), cancellationToken);

    /// <summary>A work lease maintenance never revokes, and which grants every effect group (the embedding batches take one).</summary>
    private sealed class IdleWorkLease : IGrimoireWorkLease
    {
        public GrimoireWorkKind Kind => GrimoireWorkKind.SessionAttachmentIndexing;

        public long Generation => 1;

        public CancellationToken MaintenanceRevocation => CancellationToken.None;

        public bool MaintenanceRevocationPending => false;

        public bool TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? effectGroup)
        {
            effectGroup = new Group();

            return true;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class Group : IGrimoireExternalEffectGroup
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
