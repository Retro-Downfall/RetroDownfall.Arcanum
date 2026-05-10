using System.Text;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// Periodically enqueues conversations that need campaign-log processing and runs headless inference to
/// update <c>Conversation.Summary</c> and <c>Conversation.LastSummarizedMessageAt</c> when each id is consumed.
/// </summary>
internal sealed class CampaignLoggerBackgroundService(
    IServiceScopeFactory scopeFactory,
    CampaignLoggerQueue queue,
    IOptionsMonitor<ArcanumSettings> options,
    ILogger<CampaignLoggerBackgroundService> hostLogger)
    : BackgroundService
{

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        Task sweepTask = Task.Run(
            () => RunSweepLoopAsync(stoppingToken),
            CancellationToken.None);

        Task consumeTask = ConsumeQueueAsync(stoppingToken);

        await Task.WhenAll(sweepTask, consumeTask).ConfigureAwait(false);
    }

    private async Task RunSweepLoopAsync(CancellationToken stoppingToken)
    {
        try
        {
            int sweepMinutes = ArcanumSettingClamps.CampaignLogSweepIntervalMinutes(
                options.CurrentValue.Intelligence.CampaignLogSweepIntervalMinutes);

            using PeriodicTimer timer = new(TimeSpan.FromMinutes(sweepMinutes));

            try
            {
                int threshold = ArcanumSettingClamps.CampaignLogThreshold(
                    options.CurrentValue.Intelligence.CampaignLogThreshold);

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
                    int threshold = ArcanumSettingClamps.CampaignLogThreshold(
                        options.CurrentValue.Intelligence.CampaignLogThreshold);

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

        int idleMinutes = ArcanumSettingClamps.CampaignLogIdleTimeoutMinutes(
            options.CurrentValue.Intelligence.CampaignLogIdleTimeoutMinutes);

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
        try
        {
            await foreach (
                Guid conversationId in queue.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await using AsyncServiceScope iterationScope = scopeFactory.CreateAsyncScope();

                    IGrimoireRepository grimoire =
                        iterationScope.ServiceProvider.GetRequiredService<IGrimoireRepository>();

                    IArcanumIntelligenceProvider intelligence =
                        iterationScope.ServiceProvider.GetRequiredService<IArcanumIntelligenceProvider>();

                    Conversation? conversation = await grimoire
                        .GetConversationAsync(conversationId, stoppingToken)
                        .ConfigureAwait(false);

                    if (conversation is null)
                    {
                        hostLogger.LogWarning(
                            "Campaign Logger: Conversation {ConversationId} no longer exists; skipping.",
                            conversationId);

                        continue;
                    }

                    DateTime watermark = conversation.LastSummarizedMessageAt ?? DateTime.MinValue;

                    List<ChatMessage> batch = conversation.Messages
                        .Where(m => m.Timestamp > watermark)
                        .OrderBy(m => m.Timestamp)
                        .ToList();

                    if (batch.Count == 0)
                    {
                        hostLogger.LogInformation(
                            "Campaign Logger: No messages to summarize for conversation {ConversationId}; skipping.",
                            conversationId);

                        continue;
                    }

                    DateTime batchEndUtc = batch[^1].Timestamp;

                    StringBuilder userPayload = new();

                    if (!string.IsNullOrWhiteSpace(conversation.Summary))
                    {
                        _ = userPayload.AppendLine("## Previous Summary");

                        _ = userPayload.AppendLine(conversation.Summary.Trim());

                        _ = userPayload.AppendLine();
                    }

                    foreach (ChatMessage m in batch)
                    {
                        _ = userPayload.Append('[');

                        _ = userPayload.Append(m.Role.ToString());

                        _ = userPayload.Append("]: ");

                        _ = userPayload.AppendLine(m.Content);
                    }

                    const string systemPersona =
                        "You are an AI tasked with maintaining a rolling campaign summary of a technical workspace conversation. Combine the previous summary (if any) with the new messages into a single, cohesive, highly condensed summary. Preserve critical technical details, file paths, and decisions. Discard conversational filler.";

                    List<CoreChatMessage> statelessMessages =
                    [
                        new CoreChatMessage("system", systemPersona),

                        new CoreChatMessage("user", userPayload.ToString().TrimEnd()),
                    ];

                    ArcanumSettings arc = options.CurrentValue;

                    string? model = null;

                    if (!string.IsNullOrWhiteSpace(arc.FastModel))
                    {
                        model = arc.FastModel.Trim();
                    }
                    else if (!string.IsNullOrWhiteSpace(arc.DefaultModel))
                    {
                        model = arc.DefaultModel.Trim();
                    }

                    PingRequest ping = new(
                        Prompt: string.Empty,
                        Model: model,
                        WorkingDirectory: string.Empty,
                        UnattendedMode: true,
                        DisableMcpTools: true,
                        StatelessMessages: statelessMessages,
                        SkipSpellRouting: true);

                    try
                    {
                        Result<PromptTurnResult> result = await intelligence
                            .ExecutePromptAsync(ping, stoppingToken)
                            .ConfigureAwait(false);

                        if (result.IsFailure)
                        {
                            hostLogger.LogWarning(
                                "Campaign Logger: Summarization failed for conversation {ConversationId}: {Code} {Message}",
                                conversationId,
                                result.Error.Code,
                                result.Error.Message);

                            continue;
                        }

                        string summaryText = result.Value.Text.Trim();

                        await grimoire
                            .UpdateConversationCampaignRollupAsync(conversationId, summaryText, batchEndUtc, stoppingToken)
                            .ConfigureAwait(false);

                        hostLogger.LogInformation(
                            "Campaign Logger: Updated campaign summary for conversation {ConversationId} through {BatchEndUtc:o}.",
                            conversationId,
                            batchEndUtc);
                    }
                    catch (Exception inferEx)
                    {
                        hostLogger.LogWarning(
                            inferEx,
                            "Campaign Logger: Summarization threw for conversation {ConversationId}.",
                            conversationId);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
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
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

}
