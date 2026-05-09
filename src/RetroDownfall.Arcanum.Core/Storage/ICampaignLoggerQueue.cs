namespace RetroDownfall.Arcanum.Core.Storage;

public interface ICampaignLoggerQueue
{
    ValueTask QueueAsync(Guid conversationId, CancellationToken cancellationToken = default);
}
