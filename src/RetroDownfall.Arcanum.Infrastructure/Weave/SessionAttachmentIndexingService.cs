using System.Collections.Concurrent;

using System.Diagnostics.CodeAnalysis;

using System.Threading.Channels;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Hosting;

using Microsoft.Extensions.Logging;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Weave;

using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Weave;

/// <summary>
/// Bounded event-driven attachment indexing queue with periodic orphan/stale reconciliation.
/// </summary>
[ExcludeFromCodeCoverage] // Reason: IHostedService queue scheduler; covered via SessionAttachmentIndexingAdmissionTests and SessionAttachmentIndexingQueueTests exercising the dequeue, reconciliation and wait logic directly.
internal sealed class SessionAttachmentIndexingService : BackgroundService, ISessionAttachmentIndexQueue
{

    private static readonly TimeSpan AutomaticRetryDelay = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan ReconciliationPeriod = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;

    private readonly IOptionsMonitor<ArcanumSettings> _options;

    private readonly IGrimoireConnectionAdmissionGate _admissionGate;

    private readonly TimeProvider _timeProvider;

    private readonly ILogger<SessionAttachmentIndexingService> _logger;

    private readonly Channel<SessionAttachmentIndexRequest> _channel;

    private readonly object _channelWriteSync = new();

    private readonly ConcurrentDictionary<Guid, byte> _pending = new();

    /// <summary>
    /// The exact requests a maintenance window stood down, waiting for admission to reopen.
    /// </summary>
    /// <remarks>
    /// Owned by the loop, which is the channel's only reader, so a plain list needs no lock —
    /// producers reach <see cref="_pending"/> and <see cref="_channel"/> and never this.
    ///
    /// <para>Each entry carries the generation immediately before the one observed ahead of its
    /// refused lease. The gate can refuse in either <c>Closing(G)</c> or <c>Closed(G)</c>, and reopening
    /// from <c>Closed(G)</c> reports the same G. Waiting after G - 1 works for both without reading the
    /// gate's private state: a nonordinary gate still parks the wait, while an already reopened
    /// ordinary gate returns G immediately.</para>
    ///
    /// <para>Entries do not survive the process, and that is right rather than tolerated. The durable
    /// row still says pending, reconciliation re-selects it after a restart, and
    /// <see cref="_pending"/> is in-memory too — so the identity that was suppressing duplicates dies
    /// with the thing it was protecting.</para>
    /// </remarks>
    private readonly List<DeferredRequest> _deferred = [];

    /// <summary>Ordered channel tail that could not fit during reopen reordering.</summary>
    private readonly Queue<SessionAttachmentIndexRequest> _resignalSuffix = new();

    public SessionAttachmentIndexingService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<ArcanumSettings> options,
        IGrimoireConnectionAdmissionGate admissionGate,
        TimeProvider timeProvider,
        ILogger<SessionAttachmentIndexingService> logger)
    {

        _scopeFactory = scopeFactory;

        _options = options;

        _admissionGate = admissionGate;

        _timeProvider = timeProvider;

        _logger = logger;

        int capacity = ArcanumSettingClamps.EmbeddingsAttachmentQueueCapacity(
            options.CurrentValue.ResolveEmbeddings().Attachments.QueueCapacity);

        _channel = Channel.CreateBounded<SessionAttachmentIndexRequest>(
            new BoundedChannelOptions(capacity)
            {

                FullMode = BoundedChannelFullMode.Wait,

                SingleReader = true,

                SingleWriter = false,

            });

    }

    /// <summary>The queue's reader, so a test can observe what a re-signal actually wrote.</summary>
    internal ChannelReader<SessionAttachmentIndexRequest> QueueReader => _channel.Reader;

    /// <summary>The exact requests currently standing down for maintenance, oldest first.</summary>
    internal IReadOnlyList<SessionAttachmentIndexRequest> DeferredRequests =>
        [.. _deferred.Select(static deferred => deferred.Request)];

    public bool TryEnqueue(SessionAttachmentIndexRequest request)
    {

        try
        {

            EmbeddingSettings embeddings = _options.CurrentValue.ResolveEmbeddings();

            if (!embeddings.Enabled || !embeddings.AttachmentRetrievalEnabled)
            {

                return false;

            }

            if (!_pending.TryAdd(request.AttachmentId, 0))
            {

                return true;

            }

            lock (_channelWriteSync)
            {

                FlushResignalSuffixWhileLocked();

                if (_resignalSuffix.Count == 0 && _channel.Writer.TryWrite(request))
                {

                    return true;

                }

            }

            _pending.TryRemove(request.AttachmentId, out _);

            _logger.LogWarning(
                "Session attachment indexing queue is full; attachment {AttachmentId} will be recovered by reconciliation.",
                request.AttachmentId);

            return false;

        }
        catch (Exception ex)
        {

            _pending.TryRemove(request.AttachmentId, out _);

            _logger.LogDebug(ex, "Session attachment indexing enqueue failed for {AttachmentId}.", request.AttachmentId);

            return false;

        }

    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {

        await Task.Yield();

        bool wasEnabled = false;

        QueueWait wait = new();

        while (!stoppingToken.IsCancellationRequested)
        {

            try
            {

                EmbeddingSettings embeddings = _options.CurrentValue.ResolveEmbeddings();

                bool enabled = embeddings.Enabled && embeddings.AttachmentRetrievalEnabled;

                if (!enabled)
                {

                    wasEnabled = false;

                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);

                    continue;

                }

                // A held identity is older than every request still in the channel. Wait here for
                // actual ordinary admission rather than dequeuing later work or treating the close-
                // generation increment as a reopen.
                _ = await WaitForReopenAndResignalDeferredRequestsAsync(stoppingToken)
                    .ConfigureAwait(false);

                if (!wasEnabled)
                {

                    wasEnabled = true;

                    _ = await ReconcileAndEnqueueAsync(embeddings, stoppingToken).ConfigureAwait(false);

                }

                QueueSignal signal = await WaitForWorkAsync(
                    _channel.Reader,
                    wait,
                    ReconciliationPeriod,
                    stoppingToken).ConfigureAwait(false);

                if (signal == QueueSignal.ReconciliationDue)
                {

                    _ = await ReconcileAndEnqueueAsync(embeddings, stoppingToken).ConfigureAwait(false);

                    continue;

                }

                if (signal == QueueSignal.QueueCompleted)
                {

                    return;

                }

                int batchSize = ArcanumSettingClamps.EmbeddingsAttachmentMaxAttachmentsPerBatch(
                    embeddings.Attachments.MaxAttachmentsPerBatch);

                bool deferred = false;

                for (int processed = 0; processed < batchSize; processed++)
                {

                    if (!_channel.Reader.TryRead(out SessionAttachmentIndexRequest? request)
                        || request is null)
                    {

                        break;

                    }

                    SessionAttachmentIndexOutcome outcome = await ProcessOneAsync(request, stoppingToken)
                        .ConfigureAwait(false);

                    if (outcome.Disposition == SessionAttachmentIndexDisposition.DeferredForMaintenance)
                    {

                        // Once maintenance owns admission every remaining request is refused too, so
                        // draining the rest would only convert the queue into the deferred list one
                        // refused lease at a time. They keep their place instead.
                        deferred = true;

                        break;

                    }

                }

                if (deferred)
                {

                    continue;

                }

                _ = await ReconcileAndEnqueueAsync(embeddings, stoppingToken).ConfigureAwait(false);

            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {

                return;

            }
            catch (Exception ex)
            {

                _logger.LogWarning(ex, "Session attachment indexing loop failed; retrying.");

                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);

            }

        }

    }

    /// <summary>Why <see cref="WaitForWorkAsync"/> returned.</summary>
    internal enum QueueSignal
    {

        Work,

        ReconciliationDue,

        QueueCompleted,

    }

    /// <summary>
    /// The one outstanding channel waiter and reconciliation delay, carried across loop iterations.
    /// </summary>
    internal sealed class QueueWait
    {

        public Task<bool>? PendingRead { get; set; }

        public Task? PendingPeriod { get; set; }

    }

    /// <summary>
    /// Waits for either queued work or the next reconciliation period, whichever comes first.
    ///
    /// <para>The loser of that race is kept rather than abandoned. An abandoned
    /// <c>WaitToReadAsync</c> stays queued on the channel's waiting-reader list until a write finally
    /// drains it — cancelling it marks it completed but does not unlink it — so re-issuing one every
    /// reconciliation period would grow that list, and the stopping token's registration list, for
    /// the life of a host nobody ever attaches a file to. Exactly one of each is outstanding here,
    /// and each is replaced only once it has been consumed.</para>
    /// </summary>
    internal static async Task<QueueSignal> WaitForWorkAsync(
        ChannelReader<SessionAttachmentIndexRequest> reader,
        QueueWait wait,
        TimeSpan reconciliationPeriod,
        CancellationToken cancellationToken)
    {

        wait.PendingRead ??= reader.WaitToReadAsync(cancellationToken).AsTask();

        wait.PendingPeriod ??= Task.Delay(reconciliationPeriod, cancellationToken);

        Task completed = await Task.WhenAny(wait.PendingRead, wait.PendingPeriod).ConfigureAwait(false);

        if (completed == wait.PendingPeriod)
        {

            wait.PendingPeriod = null;

            return QueueSignal.ReconciliationDue;

        }

        bool hasWork = await wait.PendingRead.ConfigureAwait(false);

        wait.PendingRead = null;

        return hasWork ? QueueSignal.Work : QueueSignal.QueueCompleted;

    }

    /// <summary>Runs one dequeued request, reporting whether maintenance stood it down.</summary>
    /// <remarks>
    /// The work lease is declared before the <see langword="try"/> so that it outlives every scope
    /// this request opens — the processing one, and the second one a genuine failure opens to write
    /// its durable classification. That ordering is the point of the lease, not an incidental detail:
    /// releasing it inside the <see langword="try"/> would let a transition's stage one conclude the
    /// worker had drained while a pooled context and its enrolled physical handle were still going
    /// back, and would leave the failure classification's own write outside the drain set entirely.
    /// </remarks>
    internal async Task<SessionAttachmentIndexOutcome> ProcessOneAsync(
        SessionAttachmentIndexRequest request,
        CancellationToken stoppingToken)
    {

        long observedGeneration = _admissionGate.CurrentGeneration;

        if (!_admissionGate.TryAcquireWorkLease(
                GrimoireWorkKind.SessionAttachmentIndexing,
                out IGrimoireWorkLease? workLease))
        {

            Defer(request, observedGeneration);

            return SessionAttachmentIndexOutcome.DeferredForMaintenance;

        }

        SessionAttachmentIndexOutcome outcome;

        // Keep retry scheduling outside this block. The lease owns one completed work unit: every
        // scope, durable disposition and pending-identity decision, but not the later backoff before
        // a new dequeue attempt.
        {

            await using IGrimoireWorkLease lease = workLease!;

            bool retainedForMaintenance = false;

            try
            {

                // A maintenance outcome becomes retained queue state only after this scope returns
                // cleanly. If disposal itself fails, this request concluded as a genuine failure and
                // must release its identity so the incremented retry can enter the channel.
                {

                    await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

                    SessionAttachmentIndexProcessor processor = scope.ServiceProvider
                        .GetRequiredService<SessionAttachmentIndexProcessor>();

                    outcome = await processor.ProcessAsync(request, lease, stoppingToken).ConfigureAwait(false);

                }

                if (outcome.Disposition == SessionAttachmentIndexDisposition.DeferredForMaintenance)
                {

                    Defer(request, observedGeneration);

                    retainedForMaintenance = true;

                }

            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {

                throw;

            }
            catch (OperationCanceledException ex)
            {

                _logger.LogWarning(
                    ex,
                    "Session attachment {AttachmentId} indexing was interrupted and will be retried.",
                    request.AttachmentId);

                outcome = new SessionAttachmentIndexOutcome(
                    SessionAttachmentIndexDisposition.Concluded,
                    SessionAttachmentIndexStatus.Failed,
                    ShouldRetry: true);

                await MarkFailedAsync(
                    request,
                    "Attachment indexing was interrupted and will be retried.").ConfigureAwait(false);

            }
            catch (Exception ex)
            {

                _logger.LogWarning(ex, "Session attachment {AttachmentId} indexing failed.", request.AttachmentId);

                outcome = new SessionAttachmentIndexOutcome(
                    SessionAttachmentIndexDisposition.Concluded,
                    SessionAttachmentIndexStatus.Failed,
                    ShouldRetry: true);

                await MarkFailedAsync(
                    request,
                    "Attachment indexing failed unexpectedly.").ConfigureAwait(false);

            }
            finally
            {

                // The pending identity is released only by a request that actually concluded. A
                // deferral keeps it, because it is what a resumed run resumes and what keeps a
                // producer's enqueue for the same attachment deduplicated while it waits.
                if (!retainedForMaintenance)
                {

                    _pending.TryRemove(request.AttachmentId, out _);

                }

            }

        }

        if (ShouldAutomaticallyRetry(outcome, stoppingToken))
        {

            await Task.Delay(AutomaticRetryDelay, _timeProvider, stoppingToken).ConfigureAwait(false);

            _ = TryEnqueue(request with { Attempt = NextAttempt(request.Attempt) });

        }

        return outcome;

    }

    /// <summary>Stands one request down without spending anything it was carrying.</summary>
    private void Defer(SessionAttachmentIndexRequest request, long observedGeneration)
    {

        long waitAfterGeneration = observedGeneration > 0
            ? observedGeneration - 1
            : 0;

        _deferred.Add(new DeferredRequest(request, waitAfterGeneration));

        _logger.LogDebug(
            "Session attachment {AttachmentId} indexing deferred: maintenance owns Grimoire admission.",
            request.AttachmentId);

    }

    /// <summary>Waits for ordinary admission, then puts eligible stood-down requests back.</summary>
    /// <remarks>
    /// The write is <see cref="ChannelWriter{T}.TryWrite"/> and never <c>WriteAsync</c>. The channel
    /// is bounded with <see cref="BoundedChannelFullMode.Wait"/> and this runs on the loop that is
    /// the channel's only reader, so awaiting a write on a full channel would deadlock the reader
    /// against itself. Existing later work is taken off only after admission actually reopens, then
    /// written back behind the deferred prefix. A refused write leaves the ordered suffix in memory
    /// and the next pass fills the room normal dequeue progress made.
    ///
    /// <para>It writes to the channel directly rather than through <see cref="TryEnqueue"/>. That
    /// path deduplicates on the pending set, which a deferral deliberately still holds, so it would
    /// report success and write nothing.</para>
    /// </remarks>
    internal async Task<int> WaitForReopenAndResignalDeferredRequestsAsync(
        CancellationToken cancellationToken)
    {

        if (_deferred.Count == 0)
        {

            lock (_channelWriteSync)
            {

                FlushResignalSuffixWhileLocked();

            }

            return 0;

        }

        long openGeneration = await _admissionGate.WaitForNextOpenGenerationAsync(
            _deferred[^1].WaitAfterGeneration,
            cancellationToken).ConfigureAwait(false);

        lock (_channelWriteSync)
        {

            Queue<SessionAttachmentIndexRequest> ready = new();

            int eligibleCount = 0;

            while (eligibleCount < _deferred.Count
                && _deferred[eligibleCount].WaitAfterGeneration < openGeneration)
            {

                ready.Enqueue(_deferred[eligibleCount].Request);

                eligibleCount++;

            }

            if (eligibleCount == 0)
            {

                return 0;

            }

            while (_channel.Reader.TryRead(out SessionAttachmentIndexRequest? later))
            {

                ready.Enqueue(later);

            }

            while (_resignalSuffix.TryDequeue(out SessionAttachmentIndexRequest? later))
            {

                ready.Enqueue(later);

            }

            _deferred.RemoveRange(0, eligibleCount);

            int resignalled = 0;

            int readyIndex = 0;

            while (ready.TryDequeue(out SessionAttachmentIndexRequest? request))
            {

                if (_channel.Writer.TryWrite(request))
                {

                    if (readyIndex < eligibleCount)
                    {

                        resignalled++;

                    }

                    readyIndex++;

                    continue;

                }

                _resignalSuffix.Enqueue(request);

                while (ready.TryDequeue(out SessionAttachmentIndexRequest? unwritten))
                {

                    _resignalSuffix.Enqueue(unwritten);

                }

                break;

            }

            return resignalled;

        }

    }

    /// <summary>Fills newly available channel space from the oldest retained suffix.</summary>
    private void FlushResignalSuffixWhileLocked()
    {

        while (_resignalSuffix.TryPeek(out SessionAttachmentIndexRequest? request)
            && _channel.Writer.TryWrite(request))
        {

            _ = _resignalSuffix.Dequeue();

        }

    }

    /// <summary>One stood-down request and the predecessor-generation floor its reopen must pass.</summary>
    private readonly record struct DeferredRequest(
        SessionAttachmentIndexRequest Request,
        long WaitAfterGeneration);

    internal static bool ShouldAutomaticallyRetry(
        SessionAttachmentIndexOutcome outcome,
        CancellationToken stoppingToken) =>
        outcome.ShouldRetry && !stoppingToken.IsCancellationRequested;

    internal static int NextAttempt(int attempt) =>
        attempt == int.MaxValue ? int.MaxValue : attempt + 1;

    private async Task MarkFailedAsync(
        SessionAttachmentIndexRequest request,
        string failureReason)
    {

        try
        {

            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

            SessionAttachmentIndexProcessor processor = scope.ServiceProvider
                .GetRequiredService<SessionAttachmentIndexProcessor>();

            await processor.MarkFailedAsync(
                request,
                failureReason,
                CancellationToken.None).ConfigureAwait(false);

        }
        catch (Exception ex)
        {

            _logger.LogDebug(
                ex,
                "Session attachment {AttachmentId} failure status could not be persisted.",
                request.AttachmentId);

        }

    }

    /// <summary>Reconciles durable index state, reporting whether maintenance stood it down.</summary>
    /// <remarks>
    /// This is a background work unit of its own rather than part of a dequeued request, and it takes
    /// its own lease for the same reason one does: it opens a scope and a Grimoire transaction that
    /// mutates four tables, on transition to enabled, on every reconciliation period, and after every
    /// drained batch. Unleased, that is an unenrolled pooled handle opened on a short cadence inside
    /// the window a transition exists to keep clear — and it reached the loop's catch-all, reporting
    /// an expected refusal as a fault once per period for the length of the window.
    ///
    /// <para>It holds no external-effect group because it makes no external call. The lease alone is
    /// what a scope-and-write unit needs.</para>
    /// </remarks>
    internal async Task<SessionAttachmentIndexDisposition> ReconcileAndEnqueueAsync(
        EmbeddingSettings embeddings,
        CancellationToken cancellationToken)
    {

        if (!_admissionGate.TryAcquireWorkLease(
                GrimoireWorkKind.SessionAttachmentIndexing,
                out IGrimoireWorkLease? workLease))
        {

            _logger.LogDebug(
                "Session attachment index reconciliation deferred: maintenance owns Grimoire admission.");

            return SessionAttachmentIndexDisposition.DeferredForMaintenance;

        }

        await using IGrimoireWorkLease lease = workLease!;

        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

        SessionAttachmentIndexRepository repository = scope.ServiceProvider
            .GetRequiredService<SessionAttachmentIndexRepository>();

        int dimensions = ArcanumSettingClamps.EmbeddingsDimensions(embeddings.Dimensions);

        int capacity = ArcanumSettingClamps.EmbeddingsAttachmentQueueCapacity(
            embeddings.Attachments.QueueCapacity);

        SessionAttachmentIndexRequest[] pending = await repository.ReconcileAndFindPendingAsync(
            dimensions,
            capacity,
            cancellationToken).ConfigureAwait(false);

        foreach (SessionAttachmentIndexRequest request in pending)
        {

            _ = TryEnqueue(request);

        }

        return SessionAttachmentIndexDisposition.Concluded;

    }

}
