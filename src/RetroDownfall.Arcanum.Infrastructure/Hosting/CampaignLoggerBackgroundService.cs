using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// Until summarization persists <c>Conversation.LastSummarizedMessageAt</c>, hybrid sweeps may re-enqueue the same ids while they still match the threshold or idle rule.
/// </summary>
internal sealed class CampaignLoggerBackgroundService(
    IServiceScopeFactory scopeFactory,
    CampaignLoggerQueue queue,
    IOptions<ArcanumSettings> options,
    ILogger<CampaignLoggerBackgroundService> hostLogger)
    : BackgroundService
{

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        int threshold = options.Value.Intelligence.CampaignLogThreshold;

        int sweepMinutes = Math.Max(1, options.Value.Intelligence.CampaignLogSweepIntervalMinutes);

        Task sweepTask = Task.Run(
            () => RunSweepLoopAsync(threshold, sweepMinutes, stoppingToken),
            CancellationToken.None);

        Task consumeTask = ConsumeQueueAsync(stoppingToken);

        await Task.WhenAll(sweepTask, consumeTask).ConfigureAwait(false);
    }

    private async Task RunSweepLoopAsync(int threshold, int sweepMinutes, CancellationToken stoppingToken)
    {
        try
        {
            using PeriodicTimer timer = new(TimeSpan.FromMinutes(sweepMinutes));

            try
            {
                await RunSweepAsync(threshold, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                hostLogger.LogError(ex, "Campaign Logger initial sweep failed; will retry on next interval.");
            }

            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await RunSweepAsync(threshold, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    hostLogger.LogError(ex, "Campaign Logger sweep iteration failed; will retry on next interval.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunSweepAsync(int threshold, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        int idleMinutes = options.Value.Intelligence.CampaignLogIdleTimeoutMinutes;

        DateTime idleCutoff = DateTime.UtcNow.AddMinutes(-idleMinutes);

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        IGrimoireRepository repository = scope.ServiceProvider.GetRequiredService<IGrimoireRepository>();

        List<Guid> ids = await repository
            .GetConversationsNeedingSummarizationAsync(threshold, idleCutoff, cancellationToken)
            .ConfigureAwait(false);

        hostLogger.LogInformation(
            "Campaign Logger sweep executed. Found {Count} sessions to consolidate.",
            ids.Count);

        foreach (Guid id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await queue.QueueAsync(id, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ConsumeQueueAsync(CancellationToken stoppingToken)
    {
        await foreach (
            Guid conversationId in queue.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await using AsyncServiceScope iterationScope = scopeFactory.CreateAsyncScope();

                hostLogger.LogInformation(
                    "Campaign Logger: Processing conversation {ConversationId}",
                    conversationId);
            }
            catch (Exception ex)
            {
                hostLogger.LogError(
                    ex,
                    "Campaign Logger: Failed processing conversation {ConversationId}",
                    conversationId);
            }
        }
    }

}
