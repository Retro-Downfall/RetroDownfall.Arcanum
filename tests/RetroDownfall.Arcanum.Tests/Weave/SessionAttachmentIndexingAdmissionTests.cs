using System.Collections.Concurrent;

using System.Data.Common;

using System.Text;

using System.Threading.Channels;

using Microsoft.Extensions.AI;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Logging;

using Microsoft.Extensions.Logging.Abstractions;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Storage.Entities;

using RetroDownfall.Arcanum.Core.Weave;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Tests.Fixtures;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave;

/// <summary>
/// What a Grimoire maintenance window does to one dequeued attachment-indexing request.
/// </summary>
/// <remarks>
/// A file of its own rather than more cases in <c>SessionAttachmentIndexingTests</c>. That suite is
/// the extraction, chunking, provenance and retrieval contract, and its processor is constructed
/// directly; nothing there drives the worker's loop. <c>SessionAttachmentIndexingQueueTests</c>
/// drives the loop's statics against an empty container that cannot resolve a processor at all. The
/// admission cases need both halves at once — a real Grimoire, a real admission gate, and the
/// service's own dequeue path — so they get their own fixture rather than distorting either.
/// </remarks>
[Collection("Grimoire")]

[Trait("Category", "Integration")]

public sealed class SessionAttachmentIndexingAdmissionTests : IAsyncLifetime
{

    private const int Dimensions = 64;

    /// <summary>
    /// Enough text for more than one automatic embedding batch, from the code-owned chunk mechanics.
    /// </summary>
    /// <remarks>
    /// The chunk size and overlap are not configuration — <c>AttachmentEmbeddingSettings</c> owns
    /// them — so the size is derived from the stride they imply rather than tuned down. Seventy
    /// strides clears the sixty-four-chunk automatic batch, which is what puts a boundary between two
    /// sequential effect groups for a closure to land in.
    /// </remarks>
    private const int MultiBatchCharacterCount = 70 * (1_000 - 100);

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private string _attachmentsRoot = string.Empty;

    private ArcanumDbContext? _db;

    private ArcanumSettings _settings = null!;

    private SessionAttachmentStore? _attachments;

    private SessionAttachmentIndexRepository? _index;

    public SessionAttachmentIndexingAdmissionTests(GrimoireFixture fixture)
    {

        _fixture = fixture;

    }

    public Task InitializeAsync()
    {

        _dbPath = _fixture.CopyDatabase();

        _attachmentsRoot = Path.Combine(
            Path.GetTempPath(),
            "arcanum-attachment-admission-" + Guid.NewGuid().ToString("N"));

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
    public async Task ProcessOneAsync_AdmittedRequest_ConcludesAndIndexes()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord attachment = await PersistAsync(sessionId, "alpha beta gamma");

        FakeWeaveService weave = new();

        RecordingAdmissionGate gate = new(OpenGate());

        ObservingScopeFactory scopes = BuildScopeFactory(weave);

        scopes.OnScopeCreated = () => Assert.True(gate.WorkLeaseIsHeld);

        scopes.OnScopeDisposed = () =>
        {

            Assert.True(gate.WorkLeaseIsHeld);

            return ValueTask.CompletedTask;

        };

        SessionAttachmentIndexingService service = CreateService(scopes, gate);

        SessionAttachmentIndexOutcome outcome = await service.ProcessOneAsync(
            new SessionAttachmentIndexRequest(attachment.Id, sessionId),
            CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexDisposition.Concluded, outcome.Disposition);

        Assert.Equal(SessionAttachmentIndexStatus.Indexed, outcome.Status);

        Assert.Equal(1, weave.EmbedBatchCallCount);

        Assert.Equal(1, gate.WorkLeaseAttempts);

        Assert.Equal(
            GrimoireWorkKind.SessionAttachmentIndexing,
            Assert.Single(gate.RequestedWorkKinds));

        Assert.False(gate.WorkLeaseIsHeld);

        Assert.NotEmpty(await _index!.GetChunksForAttachmentAsync(attachment.Id, CancellationToken.None));

    }

    [SkippableFact]
    public async Task ProcessOneAsync_RefusedItsWorkLease_TouchesNothingAndKeepsItsIdentity()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord attachment = await PersistAsync(sessionId, "content that must not be indexed");

        GrimoireConnectionAdmissionGate gate = OpenGate();

        FakeWeaveService weave = new();

        ObservingScopeFactory scopes = BuildScopeFactory(weave);

        SessionAttachmentIndexingService service = CreateService(scopes, gate);

        SessionAttachmentIndexRequest request = new(attachment.Id, sessionId, Attempt: 3);

        SessionAttachmentIndexRequest dequeued = Dequeue(service, request);

        await using IGrimoireClosingOwner closing = BeginClosing(gate, 61);

        SessionAttachmentIndexOutcome outcome = await service.ProcessOneAsync(
            dequeued,
            CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexDisposition.DeferredForMaintenance, outcome.Disposition);

        Assert.False(outcome.ShouldRetry);

        Assert.Equal(0, scopes.ScopesCreated);

        Assert.Equal(0, weave.EmbedBatchCallCount);

        // Nothing was classified: no state row was ever written, so the attachment is not Failed and
        // its attempt was not moved.
        Assert.Empty(await _index!.GetStatusesAsync([attachment.Id], CancellationToken.None));

        // The exact request is retained, attempt included.
        SessionAttachmentIndexRequest held = Assert.Single(service.DeferredRequests);

        Assert.Same(dequeued, held);

        Assert.Equal(3, held.Attempt);

        // And its pending identity is still held, so the deduplicating intake still refuses a
        // duplicate rather than putting a second copy of the same attachment on the queue.
        Assert.True(service.TryEnqueue(request with { Attempt = 9 }));

        Assert.Equal(0, service.QueueReader.Count);

    }

    [SkippableFact]
    public async Task ProcessOneAsync_HoldsItsWorkLeaseUntilAfterEveryScopeHasDisposed()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord attachment = await PersistAsync(sessionId, "indexed while a closure waits");

        GrimoireConnectionAdmissionGate gate = OpenGate();

        FakeWeaveService weave = new();

        ObservingScopeFactory scopes = BuildScopeFactory(weave);

        IGrimoireClosingOwner? closing = null;

        Task<Result>? drain = null;

        bool drainWasStillWaitingOnTheLease = false;

        // The probe runs after the request's scope is already disposed. A drain started there is a
        // deterministic read of whether the work lease outlived it: the gate hands back a completed
        // task synchronously when no request or work lifetime remains, and an incomplete one while
        // this request's lease is still registered.
        scopes.OnScopeDisposed = () =>
        {

            if (drain is not null)
            {

                return ValueTask.CompletedTask;

            }

            closing = BeginClosing(gate, 62);

            Task<Result> started = gate
                .DrainRequestAndWorkAsync(closing, CancellationToken.None)
                .AsTask();

            drainWasStillWaitingOnTheLease = !started.IsCompleted;

            drain = started;

            return ValueTask.CompletedTask;

        };

        SessionAttachmentIndexingService service = CreateService(scopes, gate);

        SessionAttachmentIndexOutcome outcome = await service.ProcessOneAsync(
            new SessionAttachmentIndexRequest(attachment.Id, sessionId),
            CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexDisposition.Concluded, outcome.Disposition);

        Assert.True(
            drainWasStillWaitingOnTheLease,
            "The work lease was already released when the request's scope finished disposing, so a closure could conclude its drain while the scoped context was still going back.");

        Result drained = await drain!;

        Assert.True(drained.IsSuccess, drained.IsFailure ? drained.Error.Message : null);

        await closing!.DisposeAsync();

    }

    [SkippableFact]
    public async Task ProcessOneAsync_GenuineFailure_ClassifiesInsideTheSameWorkLease()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord attachment = await PersistAsync(sessionId, "content whose indexing throws");

        RecordingAdmissionGate gate = new(OpenGate());

        FakeWeaveService weave = new()
        {

            OnEmbed = static _ => throw new InvalidOperationException("simulated indexing fault."),

        };

        ObservingScopeFactory scopes = BuildScopeFactory(weave);

        List<bool> scopeCreationsInsideLease = [];

        List<bool> scopeDisposalsInsideLease = [];

        // A genuine failure opens a second scope to write its durable classification. Both scope
        // boundaries must stay inside the exact lease returned for this dequeue. Record rather than
        // assert in the callbacks: MarkFailedAsync deliberately catches scope-disposal exceptions,
        // so an assertion thrown from its callback would otherwise disappear into that recovery.
        scopes.OnScopeCreated = () => scopeCreationsInsideLease.Add(gate.WorkLeaseIsHeld);

        scopes.OnScopeDisposed = () =>
        {

            scopeDisposalsInsideLease.Add(gate.WorkLeaseIsHeld);

            return ValueTask.CompletedTask;

        };

        SessionAttachmentIndexingService service = CreateService(scopes, gate);

        SessionAttachmentIndexOutcome outcome = await service.ProcessOneAsync(
            new SessionAttachmentIndexRequest(attachment.Id, sessionId),
            CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexDisposition.Concluded, outcome.Disposition);

        Assert.Equal(SessionAttachmentIndexStatus.Failed, outcome.Status);

        Assert.True(outcome.ShouldRetry);

        Assert.Equal(2, scopes.ScopesCreated);

        Assert.Equal([true, true], scopeCreationsInsideLease);

        Assert.Equal([true, true], scopeDisposalsInsideLease);

        Assert.Equal(1, gate.WorkLeaseAttempts);

        Assert.Equal(
            GrimoireWorkKind.SessionAttachmentIndexing,
            Assert.Single(gate.RequestedWorkKinds));

        Assert.False(gate.WorkLeaseIsHeld);

        IReadOnlyDictionary<Guid, SessionAttachmentIndexStatus> statuses =
            await _index!.GetStatusesAsync([attachment.Id], CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexStatus.Failed, statuses[attachment.Id]);

        // A genuine failure is not a deferral: the identity is released so the retry can re-enter.
        Assert.Empty(service.DeferredRequests);

    }

    [SkippableFact]
    public async Task ProcessOneAsync_RetryableProviderOutcome_ReleasesLeaseBeforeRetryDelay()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord attachment = await PersistAsync(
            sessionId,
            "content whose provider result is retryable");

        GrimoireConnectionAdmissionGate innerGate = OpenGate();

        RecordingAdmissionGate gate = new(innerGate);

        IGrimoireClosingOwner? closing = null;

        Task<Result>? drain = null;

        bool drainCompletedWhenRetryDelayWasScheduled = false;

        ImmediateTimeProvider time = new(() =>
        {

            closing = BeginClosing(innerGate, 69);

            drain = innerGate
                .DrainRequestAndWorkAsync(closing, CancellationToken.None)
                .AsTask();

            drainCompletedWhenRetryDelayWasScheduled = drain.IsCompleted;

        });

        FakeWeaveService weave = new()
        {

            EmbedBatchResult = Result<Embedding<float>[]>.Failure(
                new Error(
                    ErrorCodes.Embeddings.ProviderUnavailable,
                    "Simulated embedding failure.")),

        };

        ObservingScopeFactory scopes = BuildScopeFactory(weave);

        SessionAttachmentIndexingService service = CreateService(scopes, gate, timeProvider: time);

        SessionAttachmentIndexOutcome outcome = await service.ProcessOneAsync(
            new SessionAttachmentIndexRequest(attachment.Id, sessionId),
            CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexDisposition.Concluded, outcome.Disposition);

        Assert.Equal(SessionAttachmentIndexStatus.Failed, outcome.Status);

        Assert.True(outcome.ShouldRetry);

        Assert.Equal([TimeSpan.FromSeconds(5)], time.Delays);

        Assert.True(
            drainCompletedWhenRetryDelayWasScheduled,
            "Maintenance was still waiting on a concluded work lease when automatic retry entered its delay.");

        Result drained = await drain!;

        Assert.True(drained.IsSuccess, drained.IsFailure ? drained.Error.Message : null);

        Assert.False(gate.WorkLeaseIsHeld);

        await closing!.DisposeAsync();

    }

    [SkippableFact]
    public async Task ProcessOneAsync_SuccessfulBatch_DisposesEffectGroupAfterDurableAppend()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord attachment = await PersistAsync(sessionId, "content appended inside its group");

        RecordingAdmissionGate gate = new(OpenGate());

        SessionAttachmentIndexState? stateAtDisposal = null;

        int chunkCountAtDisposal = -1;

        gate.OnEffectGroupDisposing = async () =>
        {

            stateAtDisposal = await _index!.GetStateAsync(attachment.Id, CancellationToken.None);

            chunkCountAtDisposal = (await _index
                .GetChunksForAttachmentAsync(attachment.Id, CancellationToken.None)).Length;

        };

        SessionAttachmentIndexingService service = CreateService(
            BuildScopeFactory(new FakeWeaveService()),
            gate);

        SessionAttachmentIndexOutcome outcome = await service.ProcessOneAsync(
            new SessionAttachmentIndexRequest(attachment.Id, sessionId),
            CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexStatus.Indexed, outcome.Status);

        Assert.NotNull(stateAtDisposal);

        Assert.Equal(SessionAttachmentIndexStatus.Pending, stateAtDisposal.Status);

        Assert.True(chunkCountAtDisposal > 0);

    }

    [SkippableFact]
    public async Task ProcessOneAsync_FailedProviderResult_DisposesEffectGroupAfterDurableFailure()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord attachment = await PersistAsync(sessionId, "provider result fails inside its group");

        RecordingAdmissionGate gate = new(OpenGate());

        SessionAttachmentIndexState? stateAtDisposal = null;

        gate.OnEffectGroupDisposing = async () =>
        {

            stateAtDisposal = await _index!.GetStateAsync(attachment.Id, CancellationToken.None);

        };

        FakeWeaveService weave = new()
        {

            EmbedBatchResult = Result<Embedding<float>[]>.Failure(
                new Error(
                    ErrorCodes.Embeddings.ProviderUnavailable,
                    "Simulated embedding failure.")),

        };

        SessionAttachmentIndexingService service = CreateService(
            BuildScopeFactory(weave),
            gate,
            timeProvider: new ImmediateTimeProvider(static () => { }));

        SessionAttachmentIndexOutcome outcome = await service.ProcessOneAsync(
            new SessionAttachmentIndexRequest(attachment.Id, sessionId),
            CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexStatus.Failed, outcome.Status);

        Assert.True(outcome.ShouldRetry);

        Assert.NotNull(stateAtDisposal);

        Assert.Equal(SessionAttachmentIndexStatus.Failed, stateAtDisposal.Status);

    }

    [SkippableFact]
    public async Task ProcessOneAsync_DimensionMismatch_DisposesEffectGroupAfterDurableFailure()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord attachment = await PersistAsync(sessionId, "wrong dimensions fail inside their group");

        RecordingAdmissionGate gate = new(OpenGate());

        SessionAttachmentIndexState? stateAtDisposal = null;

        gate.OnEffectGroupDisposing = async () =>
        {

            stateAtDisposal = await _index!.GetStateAsync(attachment.Id, CancellationToken.None);

        };

        SessionAttachmentIndexingService service = CreateService(
            BuildScopeFactory(new FakeWeaveService { OutputDimensions = Dimensions + 1 }),
            gate);

        SessionAttachmentIndexOutcome outcome = await service.ProcessOneAsync(
            new SessionAttachmentIndexRequest(attachment.Id, sessionId),
            CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexStatus.Failed, outcome.Status);

        Assert.False(outcome.ShouldRetry);

        Assert.NotNull(stateAtDisposal);

        Assert.Equal(SessionAttachmentIndexStatus.Failed, stateAtDisposal.Status);

    }

    [SkippableFact]
    public async Task ProcessOneAsync_ProviderCancellation_DisposesEffectGroupAfterDurableFailure()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord attachment = await PersistAsync(sessionId, "provider cancellation fails inside its group");

        RecordingAdmissionGate gate = new(OpenGate());

        SessionAttachmentIndexState? stateAtDisposal = null;

        gate.OnEffectGroupDisposing = async () =>
        {

            stateAtDisposal = await _index!.GetStateAsync(attachment.Id, CancellationToken.None);

        };

        FakeWeaveService weave = new()
        {

            OnEmbed = static _ => throw new OperationCanceledException("simulated provider interruption"),

        };

        SessionAttachmentIndexingService service = CreateService(
            BuildScopeFactory(weave),
            gate,
            timeProvider: new ImmediateTimeProvider(static () => { }));

        SessionAttachmentIndexOutcome outcome = await service.ProcessOneAsync(
            new SessionAttachmentIndexRequest(attachment.Id, sessionId),
            CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexStatus.Failed, outcome.Status);

        Assert.True(outcome.ShouldRetry);

        Assert.NotNull(stateAtDisposal);

        Assert.Equal(SessionAttachmentIndexStatus.Failed, stateAtDisposal.Status);

        Assert.Equal(
            "Attachment indexing was interrupted and will be retried.",
            stateAtDisposal.FailureReason);

    }

    [SkippableFact]
    public async Task ProcessOneAsync_HostCancellation_PropagatesWithoutFailureClassification()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord attachment = await PersistAsync(sessionId, "host cancellation is not failure");

        using CancellationTokenSource stopping = new();

        FakeWeaveService weave = new()
        {

            OnEmbed = _ =>
            {

                stopping.Cancel();

                stopping.Token.ThrowIfCancellationRequested();

                return Task.CompletedTask;

            },

        };

        SessionAttachmentIndexingService service = CreateService(
            BuildScopeFactory(weave),
            new RecordingAdmissionGate(OpenGate()));

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ProcessOneAsync(
                new SessionAttachmentIndexRequest(attachment.Id, sessionId),
                stopping.Token));

        SessionAttachmentIndexState state = await _index!.GetStateAsync(
            attachment.Id,
            CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexStatus.Pending, state.Status);

        Assert.Equal(0, state.AttemptCount);

        Assert.Null(state.FailureReason);

    }

    [SkippableFact]
    public async Task ProcessOneAsync_RevocationWinsTheFirstEffectRace_MakesNoProviderCall()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord attachment = await PersistAsync(sessionId, "content the frontier must refuse");

        GrimoireConnectionAdmissionGate gate = OpenGate();

        FakeWeaveService weave = new();

        ObservingScopeFactory scopes = BuildScopeFactory(weave);

        IGrimoireClosingOwner? closing = null;

        // Closing the gate as the request's scope is created lands between the work lease and the
        // first effect group, which is the exact window the frontier arbitrates. No sleep can place
        // it as precisely.
        scopes.OnScopeCreated = () => closing ??= BeginClosing(gate, 63);

        SessionAttachmentIndexingService service = CreateService(scopes, gate);

        SessionAttachmentIndexRequest request = new(attachment.Id, sessionId, Attempt: 2);

        SessionAttachmentIndexOutcome outcome = await service.ProcessOneAsync(
            Dequeue(service, request),
            CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexDisposition.DeferredForMaintenance, outcome.Disposition);

        Assert.Equal(1, scopes.ScopesCreated);

        Assert.Equal(0, weave.EmbedBatchCallCount);

        Assert.Empty(await _index!.GetChunksForAttachmentAsync(attachment.Id, CancellationToken.None));

        // The pending mark ran inside the lease and is allowed to have run; what must not have
        // happened is a failure classification or an attempt move.
        SessionAttachmentIndexState state = await _index.GetStateAsync(attachment.Id, CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexStatus.Pending, state.Status);

        Assert.Equal(2, state.AttemptCount);

        SessionAttachmentIndexRequest held = Assert.Single(service.DeferredRequests);

        Assert.Equal(2, held.Attempt);

        // A group refused part way through is still a deferral, so the identity is retained here as
        // well as on the lease-refusal path — and intake still deduplicates against it.
        Assert.True(service.TryEnqueue(request with { Attempt = 9 }));

        Assert.Equal(0, service.QueueReader.Count);

        await closing!.DisposeAsync();

    }

    [SkippableFact]
    public async Task ProcessOneAsync_EffectStartWins_IsNotCutAndTheClosureWaitsThroughIt()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord attachment = await PersistAsync(sessionId, "content the frontier already admitted");

        GrimoireConnectionAdmissionGate gate = OpenGate();

        IGrimoireClosingOwner? closing = null;

        Task<Result>? drain = null;

        // Closing from inside the provider call is the losing half of the race: the group is already
        // open, so maintenance must wait it out rather than revoke it.
        FakeWeaveService weave = new()
        {

            OnEmbed = _ =>
            {

                closing ??= BeginClosing(gate, 64);

                drain ??= gate.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

                return Task.CompletedTask;

            },

        };

        ObservingScopeFactory scopes = BuildScopeFactory(weave);

        SessionAttachmentIndexingService service = CreateService(scopes, gate);

        SessionAttachmentIndexOutcome outcome = await service.ProcessOneAsync(
            new SessionAttachmentIndexRequest(attachment.Id, sessionId),
            CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexDisposition.Concluded, outcome.Disposition);

        Result drained = await drain!;

        Assert.True(drained.IsSuccess, drained.IsFailure ? drained.Error.Message : null);

        Assert.Equal(1, weave.EmbedBatchCallCount);

        // The append the admitted provider call earned still landed, and so did its publication. A
        // revocation delivered into the group would have lost the batch that was already billed.
        Assert.NotEmpty(await _index!.GetChunksForAttachmentAsync(attachment.Id, CancellationToken.None));

        IReadOnlyDictionary<Guid, SessionAttachmentIndexStatus> statuses =
            await _index.GetStatusesAsync([attachment.Id], CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexStatus.Indexed, statuses[attachment.Id]);

        await closing!.DisposeAsync();

    }

    [SkippableFact]
    public async Task ProcessOneAsync_ClosedBetweenBatches_ResumesTheCheckpointAfterExactReopen()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        // Enough characters for more than one automatic embedding batch, so there is a boundary
        // between two sequential effect groups for a closure to land in.
        SessionAttachmentRecord attachment = await PersistAsync(
            sessionId,
            MultiBatchText());

        GrimoireConnectionAdmissionGate gate = OpenGate();

        IGrimoireClosingOwner? closing = null;

        // Closing as the first batch returns leaves that batch's group open and admitted, and the
        // second batch's group refused.
        FakeWeaveService weave = new()
        {

            OnEmbed = batchNumber =>
            {

                if (batchNumber == 1)
                {

                    closing ??= BeginClosing(gate, 65);

                }

                return Task.CompletedTask;

            },

        };

        ObservingScopeFactory scopes = BuildScopeFactory(weave);

        SessionAttachmentIndexingService service = CreateService(scopes, gate);

        SessionAttachmentIndexRequest request = Dequeue(
            service,
            new SessionAttachmentIndexRequest(attachment.Id, sessionId, Attempt: 1));

        SessionAttachmentIndexOutcome outcome = await service.ProcessOneAsync(
            request,
            CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexDisposition.DeferredForMaintenance, outcome.Disposition);

        // Exactly one provider call: the second batch never began, so nothing was billed twice and
        // nothing was billed for work that was then thrown away.
        Assert.Equal(1, weave.EmbedBatchCallCount);

        // The first batch's chunks are durable, which is what "defer having completed batches 1..N"
        // means and what makes the resumed run free.
        SessionAttachmentIndexedChunk[] partialChunks = await _index!
            .GetChunksForAttachmentAsync(attachment.Id, CancellationToken.None);

        Assert.Equal(64, partialChunks.Length);

        SessionAttachmentIndexState state = await _index.GetStateAsync(attachment.Id, CancellationToken.None);

        // The staged generation survives the deferral. MarkWithoutIndexAsync would have deleted it,
        // which is what makes a maintenance denial classified as a failure cost real money.
        Assert.Equal(SessionAttachmentIndexStatus.Pending, state.Status);

        Assert.Equal(1, state.AttemptCount);

        Assert.NotNull(state.PendingGenerationId);

        Assert.NotEqual(string.Empty, state.PendingGenerationId);

        Assert.Equal(64, state.NextChunkIndex);

        SessionAttachmentIndexRequest held = Assert.Single(service.DeferredRequests);

        Assert.Equal(1, held.Attempt);

        IGrimoireExclusiveClosedLease closed = await CloseAsync(gate, closing!);

        // Entering Closed advances the generation, but is not a reopen. The request must stay held
        // and the channel must stay empty until this exact lease restores ordinary admission.
        Task<int> resignalling = service.WaitForReopenAndResignalDeferredRequestsAsync(
            CancellationToken.None);

        Assert.False(resignalling.IsCompleted);

        Assert.Equal(0, service.QueueReader.Count);

        await ReopenAsync(closed, closing!);

        Assert.Equal(1, await resignalling);

        Assert.True(service.QueueReader.TryRead(out SessionAttachmentIndexRequest? resignalled));

        Assert.Same(request, resignalled);

        SessionAttachmentIndexOutcome resumed = await service.ProcessOneAsync(
            resignalled!,
            CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexDisposition.Concluded, resumed.Disposition);

        Assert.Equal(SessionAttachmentIndexStatus.Indexed, resumed.Status);

        Assert.Equal([64, 6], weave.BatchSizes);

        SessionAttachmentIndexState completed = await _index.GetStateAsync(
            attachment.Id,
            CancellationToken.None);

        Assert.Equal(SessionAttachmentIndexStatus.Indexed, completed.Status);

        Assert.Equal(1, completed.AttemptCount);

        Assert.Null(completed.PendingGenerationId);

        Assert.Equal(0, completed.NextChunkIndex);

        SessionAttachmentIndexedChunk[] finalChunks = await _index
            .GetChunksForAttachmentAsync(attachment.Id, CancellationToken.None);

        Assert.Equal(70, finalChunks.Length);

        Assert.Equal(Enumerable.Range(0, 70), finalChunks.Select(static chunk => chunk.ChunkIndex));

    }

    [SkippableFact]
    public async Task DeferredRequest_WaitsForTheExactClosedLeaseToReopenBeforeItIsResignalledOnce()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord attachment = await PersistAsync(sessionId, "content deferred then resumed");

        GrimoireConnectionAdmissionGate gate = OpenGate();

        ObservingScopeFactory scopes = BuildScopeFactory(new FakeWeaveService());

        SessionAttachmentIndexingService service = CreateService(scopes, gate);

        SessionAttachmentIndexRequest request = Dequeue(
            service,
            new SessionAttachmentIndexRequest(attachment.Id, sessionId, Attempt: 5));

        IGrimoireClosingOwner closing = BeginClosing(gate, 66);

        Assert.Equal(
            SessionAttachmentIndexDisposition.DeferredForMaintenance,
            (await service.ProcessOneAsync(request, CancellationToken.None)).Disposition);

        IGrimoireExclusiveClosedLease closed = await CloseAsync(gate, closing);

        // Entering Closed advances the generation, but the exact lease still owns admission.
        Task<int> resignalling = service.WaitForReopenAndResignalDeferredRequestsAsync(
            CancellationToken.None);

        Assert.False(resignalling.IsCompleted);

        Assert.Equal(0, service.QueueReader.Count);

        await ReopenAsync(closed, closing);

        Assert.Equal(1, await resignalling);

        Assert.True(service.QueueReader.TryRead(out SessionAttachmentIndexRequest? resignalled));

        // The exact instance, not a reconstruction, and with the attempt it was deferred on.
        Assert.Same(request, resignalled);

        Assert.Equal(5, resignalled!.Attempt);

        // Once, not once per iteration.
        Assert.Empty(service.DeferredRequests);

        Assert.Equal(
            0,
            await service.WaitForReopenAndResignalDeferredRequestsAsync(CancellationToken.None));

        Assert.Equal(0, service.QueueReader.Count);

    }

    [SkippableFact]
    public async Task RequestDeferredDuringClosed_ResignalsWhenItsRegisteredWaitObservesReopen()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord attachment = await PersistAsync(
            sessionId,
            "request refused by already closed admission");

        GrimoireConnectionAdmissionGate gate = OpenGate();

        SessionAttachmentIndexingService service = CreateService(
            BuildScopeFactory(new FakeWeaveService()),
            gate);

        IGrimoireClosingOwner closing = BeginClosing(gate, 70);

        IGrimoireExclusiveClosedLease closed = await CloseAsync(gate, closing);

        SessionAttachmentIndexRequest request = Dequeue(
            service,
            new SessionAttachmentIndexRequest(attachment.Id, sessionId, Attempt: 6));

        Assert.Equal(
            SessionAttachmentIndexDisposition.DeferredForMaintenance,
            (await service.ProcessOneAsync(request, CancellationToken.None)).Disposition);

        Task<int> resignalling = service.WaitForReopenAndResignalDeferredRequestsAsync(
            CancellationToken.None);

        Assert.False(resignalling.IsCompleted);

        await ReopenAsync(closed, closing);

        Assert.Equal(1, await resignalling);

        Assert.True(service.QueueReader.TryRead(out SessionAttachmentIndexRequest? resignalled));

        Assert.Same(request, resignalled);

    }

    [SkippableFact]
    public async Task RequestDeferredDuringClosed_ResignalsWhenReopenPrecedesWaitRegistration()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord attachment = await PersistAsync(
            sessionId,
            "request whose reopen precedes its wait registration");

        GrimoireConnectionAdmissionGate gate = OpenGate();

        SessionAttachmentIndexingService service = CreateService(
            BuildScopeFactory(new FakeWeaveService()),
            gate);

        IGrimoireClosingOwner closing = BeginClosing(gate, 71);

        IGrimoireExclusiveClosedLease closed = await CloseAsync(gate, closing);

        SessionAttachmentIndexRequest request = Dequeue(
            service,
            new SessionAttachmentIndexRequest(attachment.Id, sessionId, Attempt: 7));

        Assert.Equal(
            SessionAttachmentIndexDisposition.DeferredForMaintenance,
            (await service.ProcessOneAsync(request, CancellationToken.None)).Disposition);

        await ReopenAsync(closed, closing);

        Task<int> resignalling = service.WaitForReopenAndResignalDeferredRequestsAsync(
            CancellationToken.None);

        Assert.True(resignalling.IsCompleted);

        Assert.Equal(1, await resignalling);

        Assert.True(service.QueueReader.TryRead(out SessionAttachmentIndexRequest? resignalled));

        Assert.Same(request, resignalled);

    }

    [SkippableFact]
    public async Task ExecuteAsync_FullReopenSuffixRestoresEveryRequestBeforeConcurrentNewerIntake()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord first = await PersistAsync(sessionId, "first request must resume first");

        SessionAttachmentRecord second = await PersistAsync(sessionId, "second request must stay second");

        SessionAttachmentRecord tail = await PersistAsync(sessionId, "retained suffix must stay third");

        SessionAttachmentRecord newer = await PersistAsync(sessionId, "newer intake must stay fourth");

        TaskCompletionSource<bool> secondProviderStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource<bool> continueSecondProvider = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource<bool> fourRequestsCompleted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        ConcurrentQueue<string> providerInputs = new();

        FakeWeaveService weave = new()
        {

            OnEmbedInputs = async (callNumber, inputs) =>
            {

                providerInputs.Enqueue(Assert.Single(inputs));

                if (callNumber == 2)
                {

                    secondProviderStarted.TrySetResult(true);

                    await continueSecondProvider.Task;

                }

            },

        };

        GrimoireConnectionAdmissionGate gate = OpenGate();

        RequestDeferralBarrierLogger logger = new(first.Id);

        ObservingScopeFactory scopes = BuildScopeFactory(weave);

        scopes.OnScopeDisposed = () =>
        {

            if (weave.EmbedBatchCallCount == 4)
            {

                fourRequestsCompleted.TrySetResult(true);

            }

            return ValueTask.CompletedTask;

        };

        SessionAttachmentIndexingService service = CreateService(
            scopes,
            gate,
            logger);

        Assert.True(service.TryEnqueue(new SessionAttachmentIndexRequest(first.Id, sessionId)));

        IGrimoireClosingOwner closing = BeginClosing(gate, 69);

        Microsoft.Extensions.Hosting.IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        await logger.RequestDeferred.WaitAsync(TimeSpan.FromSeconds(10));

        int capacity = ArcanumRuntimeDefaults.Embeddings.Attachments.QueueCapacity;

        Assert.True(service.TryEnqueue(new SessionAttachmentIndexRequest(second.Id, sessionId)));

        for (int index = 2; index < capacity; index++)
        {

            Assert.True(service.TryEnqueue(new SessionAttachmentIndexRequest(Guid.NewGuid(), sessionId)));

        }

        Assert.True(service.TryEnqueue(new SessionAttachmentIndexRequest(tail.Id, sessionId)));

        Assert.Single(service.DeferredRequests);

        Assert.Equal(capacity, service.QueueReader.Count);

        IGrimoireExclusiveClosedLease closed = await CloseAsync(gate, closing);

        // Full closure is a state-transition barrier: the hosted loop must still be parked on the
        // actual reopen signal, with every later request in the bounded channel.
        Assert.Single(service.DeferredRequests);

        Assert.Equal(capacity, service.QueueReader.Count);

        await ReopenAsync(closed, closing);

        await secondProviderStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        try
        {

            Assert.True(service.TryEnqueue(new SessionAttachmentIndexRequest(newer.Id, sessionId)));

        }
        finally
        {

            continueSecondProvider.TrySetResult(true);

        }

        await fourRequestsCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, service.QueueReader.Count);

        await hosted.StopAsync(CancellationToken.None);

        IReadOnlyDictionary<Guid, SessionAttachmentIndexStatus> statuses =
            await _index!.GetStatusesAsync(
                [first.Id, second.Id, tail.Id, newer.Id],
                CancellationToken.None);

        Assert.Equal(4, statuses.Count);

        Assert.All(
            statuses.Values,
            static status => Assert.Equal(SessionAttachmentIndexStatus.Indexed, status));

        Assert.Equal(
            [
                "first request must resume first",
                "second request must stay second",
                "retained suffix must stay third",
                "newer intake must stay fourth",
            ],
            providerInputs);

    }

    [SkippableFact]
    public async Task ReconcileAndEnqueueAsync_RefusedItsWorkLease_OpensNoScope()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        GrimoireConnectionAdmissionGate gate = OpenGate();

        ObservingScopeFactory scopes = BuildScopeFactory(new FakeWeaveService());

        SessionAttachmentIndexingService service = CreateService(scopes, gate);

        await using IGrimoireClosingOwner closing = BeginClosing(gate, 67);

        Assert.Equal(
            SessionAttachmentIndexDisposition.DeferredForMaintenance,
            await service.ReconcileAndEnqueueAsync(
                _settings.ResolveEmbeddings(),
                CancellationToken.None));

        Assert.Equal(0, scopes.ScopesCreated);

    }

    [SkippableFact]
    public async Task ReconcileAndEnqueueAsync_Admitted_ConcludesAndFindsPendingWork()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord attachment = await PersistAsync(sessionId, "waiting to be reconciled");

        ObservingScopeFactory scopes = BuildScopeFactory(new FakeWeaveService());

        SessionAttachmentIndexingService service = CreateService(scopes, OpenGate());

        Assert.Equal(
            SessionAttachmentIndexDisposition.Concluded,
            await service.ReconcileAndEnqueueAsync(
                _settings.ResolveEmbeddings(),
                CancellationToken.None));

        Assert.Equal(1, scopes.ScopesCreated);

        Assert.True(service.QueueReader.TryRead(out SessionAttachmentIndexRequest? found));

        Assert.Equal(attachment.Id, found!.AttachmentId);

    }

    [SkippableFact]
    public async Task ExecuteAsync_RepeatedlyDeferred_DoesNotLogAFaultOrEnterTheFaultBackoff()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        SessionAttachmentRecord attachment = await PersistAsync(sessionId, "content deferred for the whole window");

        GrimoireConnectionAdmissionGate gate = OpenGate();

        FakeWeaveService weave = new();

        ObservingScopeFactory scopes = BuildScopeFactory(weave);

        TestCapturingLogger<SessionAttachmentIndexingService> logger = new();

        SessionAttachmentIndexingService service = CreateService(scopes, gate, logger);

        Assert.True(service.TryEnqueue(new SessionAttachmentIndexRequest(attachment.Id, sessionId)));

        await using IGrimoireClosingOwner closing = BeginClosing(gate, 68);


        Microsoft.Extensions.Hosting.IHostedService hosted = service;

        await hosted.StartAsync(CancellationToken.None);

        await Task.Delay(TimeSpan.FromMilliseconds(300));

        await hosted.StopAsync(CancellationToken.None);

        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Error);

        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Warning);

        Assert.Equal(0, scopes.ScopesCreated);

        Assert.Equal(0, weave.EmbedBatchCallCount);

        Assert.Empty(await _index!.GetStatusesAsync([attachment.Id], CancellationToken.None));

        Assert.Single(service.DeferredRequests);

    }

    /// <summary>Puts one request through the real intake and takes it off the queue again.</summary>
    /// <remarks>
    /// Every deferral case goes through this rather than calling the dequeue path with a bare
    /// request, because the guarantee under test is about the pending identity that intake creates.
    /// A request that never entered the queue has no identity to retain, and an assertion about
    /// retaining it would pass against a worker that dropped it.
    /// </remarks>
    private static SessionAttachmentIndexRequest Dequeue(
        SessionAttachmentIndexingService service,
        SessionAttachmentIndexRequest request)
    {

        Assert.True(service.TryEnqueue(request));

        Assert.True(service.QueueReader.TryRead(out SessionAttachmentIndexRequest? dequeued));

        return dequeued!;

    }

    private SessionAttachmentIndexingService CreateService(
        ObservingScopeFactory scopes,
        IGrimoireConnectionAdmissionGate gate,
        ILogger<SessionAttachmentIndexingService>? logger = null,
        TimeProvider? timeProvider = null) =>
        new(
            scopes,
            new TestOptionsMonitor<ArcanumSettings>(_settings),
            gate,
            timeProvider ?? TimeProvider.System,
            logger ?? NullLogger<SessionAttachmentIndexingService>.Instance);

    /// <summary>A container holding exactly what one indexing scope resolves.</summary>
    private ObservingScopeFactory BuildScopeFactory(IWeaveService weave)
    {

        ServiceCollection services = new();

        services.AddSingleton(_index!);

        services.AddSingleton(weave);

        services.AddSingleton<ISessionAttachmentStore>(_attachments!);

        services.AddSingleton<IOptionsMonitor<ArcanumSettings>>(
            new TestOptionsMonitor<ArcanumSettings>(_settings));

        services.AddSingleton<ILogger<SessionAttachmentIndexProcessor>>(
            NullLogger<SessionAttachmentIndexProcessor>.Instance);

        services.AddSingleton<SessionAttachmentIndexProcessor>();

        return new ObservingScopeFactory(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());

    }

    private Task<SessionAttachmentRecord> PersistAsync(Guid sessionId, string text) =>
        _attachments!.PersistNewAsync(
            sessionId,
            null,
            null,
            "notes",
            "notes.txt",
            Encoding.UTF8.GetBytes(text),
            "text/plain",
            SessionAttachmentKind.Text);

    /// <summary>A gate whose ordinary admission is open, which is every pre-existing case here.</summary>
    private static GrimoireConnectionAdmissionGate OpenGate() => new(TimeProvider.System);

    private static CovenantExclusiveRecoveryOwner Owner(byte seed) =>
        new(
            Guid.Parse($"00000000-0000-0000-0000-{seed:D12}"),
            CovenantExclusiveOperation.CovenantReset,
            new CovenantDigest(Enumerable.Repeat(seed, 32).ToArray()));

    private static IGrimoireClosingOwner BeginClosing(
        GrimoireConnectionAdmissionGate gate,
        byte seed)
    {

        Result<IGrimoireClosingOwner> begun = gate.BeginOrResumeExclusive(Owner(seed));

        Assert.True(begun.IsSuccess, begun.IsFailure ? begun.Error.Message : null);

        return begun.Value;

    }

    /// <summary>Advances the exact closing owner through drain to a fully closed lease.</summary>
    private static async Task<IGrimoireExclusiveClosedLease> CloseAsync(
        GrimoireConnectionAdmissionGate gate,
        IGrimoireClosingOwner closing)
    {

        Result drained = await gate.DrainRequestAndWorkAsync(closing, CancellationToken.None);

        Assert.True(drained.IsSuccess, drained.IsFailure ? drained.Error.Message : null);

        Result<IGrimoireExclusiveClosedLease> closed = await gate.CloseConnectionAdmissionAsync(
            closing,
            CancellationToken.None);

        Assert.True(closed.IsSuccess, closed.IsFailure ? closed.Error.Message : null);

        return closed.Value;

    }

    /// <summary>Uses the exact closed lease to restore ordinary admission.</summary>
    private static async Task ReopenAsync(
        IGrimoireExclusiveClosedLease closed,
        IGrimoireClosingOwner closing)
    {

        Result completed = await closed.CompleteAsync(
            CovenantExclusiveLeaseDisposition.RollbackAndReopen,
            CancellationToken.None);

        Assert.True(completed.IsSuccess, completed.IsFailure ? completed.Error.Message : null);

        await closed.DisposeAsync();

        await closing.DisposeAsync();

    }

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

            },

        },

    };

    /// <summary>Line-broken filler, so the chunker's line awareness has ordinary text to work on.</summary>
    private static string MultiBatchText()
    {

        StringBuilder builder = new(MultiBatchCharacterCount + 128);

        while (builder.Length < MultiBatchCharacterCount)
        {

            builder.Append("the quick brown fox jumps over the lazy dog and keeps on running").Append('\n');

        }

        return builder.ToString();

    }

    private static float[] CreateVector(int dimensions = Dimensions)
    {

        float[] vector = new float[dimensions];

        vector[0] = 1f;

        return vector;

    }

    /// <summary>
    /// A scope factory that counts the scopes one request creates and can act at their boundaries.
    /// </summary>
    /// <remarks>
    /// Counting is what proves the "no scope" half of a maintenance deferral: a request refused its
    /// work lease must not reach the container at all, and an assertion on the provider call count
    /// alone would pass for a request that built a scope, opened a connection and then found the
    /// attachment ineligible.
    /// </remarks>
    private sealed class ObservingScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory
    {

        private int _scopesCreated;

        internal int ScopesCreated => Volatile.Read(ref _scopesCreated);

        internal Action? OnScopeCreated { get; set; }

        internal Func<ValueTask>? OnScopeDisposed { get; set; }

        public IServiceScope CreateScope()
        {

            _ = Interlocked.Increment(ref _scopesCreated);

            IServiceScope scope = inner.CreateScope();

            OnScopeCreated?.Invoke();

            return new ObservingScope(scope, OnScopeDisposed);

        }

    }

    private sealed class ObservingScope(
        IServiceScope inner,
        Func<ValueTask>? onDisposing) : IServiceScope, IAsyncDisposable
    {

        public IServiceProvider ServiceProvider => inner.ServiceProvider;

        public void Dispose() => inner.Dispose();

        public async ValueTask DisposeAsync()
        {

            if (inner is IAsyncDisposable asyncInner)
            {

                await asyncInner.DisposeAsync();

            }
            else
            {

                inner.Dispose();

            }

            if (onDisposing is not null)
            {

                await onDisposing();

            }

        }

    }

    private sealed class FakeWeaveService : IWeaveService
    {

        private int _embedBatchCallCount;

        private readonly ConcurrentQueue<int> _batchSizes = new();

        internal int EmbedBatchCallCount => Volatile.Read(ref _embedBatchCallCount);

        internal IReadOnlyList<int> BatchSizes => [.. _batchSizes];

        /// <summary>Receives the one-based batch number, so a test can act at a group boundary.</summary>
        internal Func<int, Task>? OnEmbed { get; init; }

        /// <summary>Receives the exact provider inputs in request-processing order.</summary>
        internal Func<int, IReadOnlyList<string>, Task>? OnEmbedInputs { get; init; }

        internal Result<Embedding<float>[]>? EmbedBatchResult { get; init; }

        internal int OutputDimensions { get; init; } = Dimensions;

        public bool IsAvailable => true;

        public Task<Result<Embedding<float>>> EmbedAsync(
            string text,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result<Embedding<float>>.Success(new Embedding<float>(CreateVector())));

        public async Task<Result<Embedding<float>[]>> EmbedBatchAsync(
            IReadOnlyList<string> texts,
            CancellationToken cancellationToken)
        {

            int batchNumber = Interlocked.Increment(ref _embedBatchCallCount);

            _batchSizes.Enqueue(texts.Count);

            if (OnEmbed is not null)
            {

                await OnEmbed(batchNumber);

            }

            if (OnEmbedInputs is not null)
            {

                await OnEmbedInputs(batchNumber, texts);

            }

            return EmbedBatchResult ?? Result<Embedding<float>[]>.Success(
                [.. texts.Select(_ => new Embedding<float>(CreateVector(OutputDimensions)))]);

        }

        public Task<Result<(string Chunk, int Offset)[]>> ChunkAsync(
            string text,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

    }

    /// <summary>Records the exact work lease the service asks the real gate to create.</summary>
    private sealed class RecordingAdmissionGate(
        IGrimoireConnectionAdmissionGate inner) : IGrimoireConnectionAdmissionGate
    {

        private RecordingWorkLease? _workLease;

        private int _workLeaseAttempts;

        internal int WorkLeaseAttempts => Volatile.Read(ref _workLeaseAttempts);

        internal List<GrimoireWorkKind> RequestedWorkKinds { get; } = [];

        internal bool WorkLeaseIsHeld => _workLease is { IsHeld: true };

        internal Func<ValueTask>? OnEffectGroupDisposing { get; set; }

        public long CurrentGeneration => inner.CurrentGeneration;

        public bool TryAcquireRequestLease(
            GrimoireRequestKind kind,
            out IGrimoireRequestLease? lease) =>
            inner.TryAcquireRequestLease(kind, out lease);

        public bool TryAcquireWorkLease(
            GrimoireWorkKind kind,
            out IGrimoireWorkLease? lease)
        {

            _ = Interlocked.Increment(ref _workLeaseAttempts);

            RequestedWorkKinds.Add(kind);

            if (!inner.TryAcquireWorkLease(kind, out IGrimoireWorkLease? admitted))
            {

                lease = null;

                return false;

            }

            _workLease = new RecordingWorkLease(
                admitted!,
                OnEffectGroupDisposing ?? (static () => ValueTask.CompletedTask));

            lease = _workLease;

            return true;

        }

        public IGrimoireConnectionOpenTicket AcquireOrdinaryOpen(DbConnection connection) =>
            inner.AcquireOrdinaryOpen(connection);

        public Result<IGrimoireClosingOwner> BeginOrResumeExclusive(
            CovenantExclusiveRecoveryOwner owner,
            IGrimoireRequestLease? initiatingRequest = null,
            DbConnection? scopedConnection = null) =>
            inner.BeginOrResumeExclusive(owner, initiatingRequest, scopedConnection);

        public ValueTask<Result> DrainRequestAndWorkAsync(
            IGrimoireClosingOwner closingOwner,
            CancellationToken cancellationToken) =>
            inner.DrainRequestAndWorkAsync(closingOwner, cancellationToken);

        public ValueTask<Result<IGrimoireExclusiveClosedLease>> CloseConnectionAdmissionAsync(
            IGrimoireClosingOwner closingOwner,
            CancellationToken cancellationToken) =>
            inner.CloseConnectionAdmissionAsync(closingOwner, cancellationToken);

        public ValueTask<Result> AbortClosingAsync(
            IGrimoireClosingOwner closingOwner,
            Func<CancellationToken, ValueTask<bool>> proveNoDestructiveEffectAsync,
            CancellationToken cancellationToken) =>
            inner.AbortClosingAsync(
                closingOwner,
                proveNoDestructiveEffectAsync,
                cancellationToken);

        public Task<long> WaitForNextOpenGenerationAsync(
            long observedGeneration,
            CancellationToken cancellationToken) =>
            inner.WaitForNextOpenGenerationAsync(observedGeneration, cancellationToken);

        public ValueTask<Result<IGrimoireExpiredLeaseAdoptionInterlock>>
            AcquireExpiredLeaseAdoptionInterlockAsync(
                CovenantExclusiveRecoveryOwner candidateOwner,
                Func<CovenantExclusiveRecoveryOwner, CancellationToken, ValueTask<bool>>
                    revalidateDurableOwnerAsync,
                CancellationToken cancellationToken) =>
            inner.AcquireExpiredLeaseAdoptionInterlockAsync(
                candidateOwner,
                revalidateDurableOwnerAsync,
                cancellationToken);

    }

    private sealed class RecordingWorkLease(
        IGrimoireWorkLease inner,
        Func<ValueTask> onEffectGroupDisposing) : IGrimoireWorkLease
    {

        private int _disposed;

        internal bool IsHeld => Volatile.Read(ref _disposed) == 0;

        public GrimoireWorkKind Kind => inner.Kind;

        public long Generation => inner.Generation;

        public CancellationToken MaintenanceRevocation => inner.MaintenanceRevocation;

        public bool TryBeginExternalEffectGroup(
            out IGrimoireExternalEffectGroup? effectGroup)
        {

            if (!inner.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? admitted))
            {

                effectGroup = null;

                return false;

            }

            effectGroup = new ObservingExternalEffectGroup(admitted!, onEffectGroupDisposing);

            return true;

        }

        public async ValueTask DisposeAsync()
        {

            await inner.DisposeAsync();

            _ = Interlocked.Exchange(ref _disposed, 1);

        }

    }

    private sealed class ObservingExternalEffectGroup(
        IGrimoireExternalEffectGroup inner,
        Func<ValueTask> onDisposing) : IGrimoireExternalEffectGroup
    {

        public async ValueTask DisposeAsync()
        {

            try
            {

                await onDisposing();

            }
            finally
            {

                await inner.DisposeAsync();

            }

        }

    }

    /// <summary>Completes delays immediately while exposing their scheduling boundary.</summary>
    private sealed class ImmediateTimeProvider(Action onDelayScheduled) : TimeProvider
    {

        internal List<TimeSpan> Delays { get; } = [];

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {

            Delays.Add(dueTime);

            onDelayScheduled();

            ThreadPool.QueueUserWorkItem(_ => callback(state));

            return NoopTimer.Instance;

        }

        private sealed class NoopTimer : ITimer
        {

            internal static NoopTimer Instance { get; } = new();

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {

            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        }

    }

    /// <summary>A deterministic barrier reached when one exact request is held for maintenance.</summary>
    private sealed class RequestDeferralBarrierLogger(Guid attachmentId) :
        ILogger<SessionAttachmentIndexingService>
    {

        private readonly TaskCompletionSource<bool> _requestDeferred = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task RequestDeferred => _requestDeferred.Task;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {

            string message = formatter(state, exception);

            if (message.Contains(attachmentId.ToString(), StringComparison.Ordinal)
                && message.Contains("indexing deferred", StringComparison.Ordinal))
            {

                _requestDeferred.TrySetResult(true);

            }

        }

    }

}
