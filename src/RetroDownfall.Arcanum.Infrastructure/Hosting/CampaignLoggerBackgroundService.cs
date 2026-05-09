using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// Until summarization persists <c>Conversation.LastSummarizedMessageAt</c>, the resilience pass may re-enqueue the same ids on every host start when they still exceed the threshold.
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

        await using (AsyncServiceScope resilienceScope = scopeFactory.CreateAsyncScope())
        {
            IGrimoireRepository repository = resilienceScope.ServiceProvider.GetRequiredService<IGrimoireRepository>();

            List<Guid> ids = await repository
                .GetConversationsNeedingSummarizationAsync(threshold, stoppingToken)
                .ConfigureAwait(false);

            foreach (Guid id in ids)
            {
                await queue.QueueAsync(id, stoppingToken).ConfigureAwait(false);
            }
        }

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
