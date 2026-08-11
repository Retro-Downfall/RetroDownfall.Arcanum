using System.Collections.Concurrent;

using System.Diagnostics.CodeAnalysis;

using System.Threading.Channels;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Hosting;

using Microsoft.Extensions.Logging;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Infrastructure.Weave;

/// <summary>
/// Bounded event-driven attachment indexing queue with periodic orphan/stale reconciliation.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class SessionAttachmentIndexingService : BackgroundService, ISessionAttachmentIndexQueue
{

    private static readonly TimeSpan AutomaticRetryDelay = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan ReconciliationPeriod = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;

    private readonly IOptionsMonitor<ArcanumSettings> _options;

    private readonly ILogger<SessionAttachmentIndexingService> _logger;

    private readonly Channel<SessionAttachmentIndexRequest> _channel;

    private readonly ConcurrentDictionary<Guid, byte> _pending = new();

    public SessionAttachmentIndexingService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<ArcanumSettings> options,
        ILogger<SessionAttachmentIndexingService> logger)
    {

        _scopeFactory = scopeFactory;

        _options = options;

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

            if (_channel.Writer.TryWrite(request))
            {

                return true;

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

                if (!wasEnabled)
                {

                    wasEnabled = true;

                    await ReconcileAndEnqueueAsync(embeddings, stoppingToken).ConfigureAwait(false);

                }

                QueueSignal signal = await WaitForWorkAsync(
                    _channel.Reader,
                    wait,
                    ReconciliationPeriod,
                    stoppingToken).ConfigureAwait(false);

                if (signal == QueueSignal.ReconciliationDue)
                {

                    await ReconcileAndEnqueueAsync(embeddings, stoppingToken).ConfigureAwait(false);

                    continue;

                }

                if (signal == QueueSignal.QueueCompleted)
                {

                    return;

                }

                int batchSize = ArcanumSettingClamps.EmbeddingsAttachmentMaxAttachmentsPerBatch(
                    embeddings.Attachments.MaxAttachmentsPerBatch);

                for (int processed = 0; processed < batchSize; processed++)
                {

                    if (!_channel.Reader.TryRead(out SessionAttachmentIndexRequest? request)
                        || request is null)
                    {

                        break;

                    }

                    await ProcessOneAsync(request, stoppingToken).ConfigureAwait(false);

                }

                await ReconcileAndEnqueueAsync(embeddings, stoppingToken).ConfigureAwait(false);

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

    private async Task ProcessOneAsync(
        SessionAttachmentIndexRequest request,
        CancellationToken stoppingToken)
    {

        SessionAttachmentIndexOutcome outcome;

        try
        {

            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

            SessionAttachmentIndexProcessor processor = scope.ServiceProvider
                .GetRequiredService<SessionAttachmentIndexProcessor>();

            outcome = await processor.ProcessAsync(request, stoppingToken).ConfigureAwait(false);

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
                SessionAttachmentIndexStatus.Failed,
                ShouldRetry: true);

            await MarkFailedAsync(
                request,
                "Attachment indexing failed unexpectedly.").ConfigureAwait(false);

        }
        finally
        {

            _pending.TryRemove(request.AttachmentId, out _);

        }

        if (ShouldAutomaticallyRetry(outcome, stoppingToken))
        {

            await Task.Delay(AutomaticRetryDelay, stoppingToken).ConfigureAwait(false);

            _ = TryEnqueue(request with { Attempt = NextAttempt(request.Attempt) });

        }

    }

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

    private async Task ReconcileAndEnqueueAsync(
        EmbeddingSettings embeddings,
        CancellationToken cancellationToken)
    {

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

    }

}
