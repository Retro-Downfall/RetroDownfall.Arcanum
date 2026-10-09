using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>The content-free history and sensitivity facts frozen before maintenance.</summary>
public sealed record SessionTurnClaimInputSnapshot(
    Guid SessionId,
    long HistoryRevision,
    DateTimeOffset? HistoryWatermarkUtc,
    long SensitivityRevision);

/// <summary>Consumes a durable turn reservation in the transaction that creates its Entries.</summary>
public interface ISessionTurnClaimBeginStore
{

    ValueTask<Result<SessionTurnClaimInputSnapshot>> ReadClaimInputAsync(
        Guid sessionId,
        CancellationToken cancellationToken);

    ValueTask<Result<AssistantReplyBeginReceipt>> BeginClaimedAssistantReplyAsync(
        SessionTurnClaimLease claimLease,
        CanonicalCampaignContext campaign,
        string prompt,
        string model,
        CancellationToken cancellationToken);

}
