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

    /// <summary>
    /// Unknown-Usage Settlement. Closes a <c>Sent</c> attempt whose SDK call ended without a result and without cancellation, inside
    /// the frame that owns the send: state failed, no completion evidence, and for <see cref="LongRestUnresolvedSend.UsageUnknown"/>
    /// one more failure toward the cap. It never writes usage. The same predicate settles an interrupted send on a new boot.
    /// </summary>
    Task<Result<LongRestDispatchAttempt>> SettleUnresolvedSendAsync(LongRestDispatchAttempt attempt, LongRestUnresolvedSend outcome, CancellationToken ct);

    /// <summary>
    /// Withdraws a <c>Prepared</c> attempt that no request ever followed: the provider call ended, with a refusal or a failure, before
    /// the physical send was recorded. Nothing reached the provider, so nothing is owed to a reboot settlement and a same-boot clean
    /// readmission must not be stopped by the row. A unit that had never sent returns to having no attempt at all. A unit whose earlier
    /// attempts did send keeps their counters and becomes the failed shape of a send that never left the process
    /// (<see cref="LongRestUnresolvedSend.NotSent"/>): no failure is charged toward the Unknown-Usage Cap. Only the exact current
    /// <c>Prepared</c> attempt without a disclosure receipt can be withdrawn; a disclosed or sent attempt is never touched.
    /// </summary>
    Task<Result> ReleaseUnsentAttemptAsync(LongRestDispatchAttempt attempt, CancellationToken ct);

    Task<Result> ValidateAttemptAsync(LongRestDispatchAttempt attempt, CancellationToken ct);
}
