namespace RetroDownfall.Arcanum.Core.Storage;

public interface ICampaignLoggerQueue
{

    /// <summary>
    /// Attempts to enqueue <paramref name="conversationId"/> for Campaign Log consolidation.
    /// Returns <see langword="true"/> when accepted or coalesced with an already-pending id;
    /// <see langword="false"/> when the bounded queue rejected the write (session remains eligible
    /// for a later sweep). Never blocks the caller.
    /// </summary>
    bool TryQueue(Guid conversationId);

}
