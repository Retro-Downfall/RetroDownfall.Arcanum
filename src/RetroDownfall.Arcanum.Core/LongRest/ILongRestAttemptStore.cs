using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Core.LongRest;

public interface ILongRestAttemptStore
{
    Task<Result<SessionTurnClaimLease>> RenewClaimAsync(SessionTurnClaimLease claim, CanonicalCampaignContext scope, CancellationToken ct);

    Task<Result<LongRestDispatchAttempt>> PrepareAttemptAsync(LongRestWorkIdentity work, LongRestProviderPayloadKind payloadKind, CovenantDigest payloadDigest, CovenantDigest destinationDigest, CancellationToken ct);

    Task<Result<LongRestDispatchAttempt>> RecordDisclosureAsync(LongRestDispatchAttempt attempt, CovenantDigest receiptDigest, CancellationToken ct);

    Task<Result<LongRestDispatchAttempt>> MarkSentAsync(LongRestDispatchAttempt attempt, CancellationToken ct);

    Task<Result<LongRestDispatchAttempt>> CompleteAttemptAsync(LongRestDispatchAttempt attempt, LongRestDispatchCompletion completion, CancellationToken ct);

    Task<Result> ValidateAttemptAsync(LongRestDispatchAttempt attempt, CancellationToken ct);
}
