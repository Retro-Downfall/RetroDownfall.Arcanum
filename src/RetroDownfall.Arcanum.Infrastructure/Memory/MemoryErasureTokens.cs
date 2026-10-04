using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Memory;

/// <summary>What a Saga or Lexicon erase preflight token binds, and nothing else.</summary>
/// <remarks>
/// Both digests are keyed and bind identifiers, counts and flags only, so the token never carries
/// content. <see cref="ContentBinding"/> is Saga's keyed binding of the live row's exact content, which
/// apply recomputes and compares, and <see cref="DatasetGeneration"/> is present only when the plan
/// measured a sensitivity label.
/// </remarks>
internal sealed record MemoryErasurePlanTokenFacts(
    MemoryReviewStore Store,
    byte[] RequestDigest,
    byte[] EffectDigest,
    byte[]? ContentBinding,
    Guid? DatasetGeneration);

/// <summary>
/// What a <c>reset-key</c> preflight token binds: the key state, and the unverifiable fingerprints and
/// receipts it measured in each store.
/// </summary>
/// <remarks>
/// Per store rather than in total (spec §5.7): a reset discards what the preview showed, store by
/// store, so a row that moves from one store to another between prepare and apply is a stale plan
/// even when every total is unchanged.
/// </remarks>
internal sealed record MemoryErasureKeyResetTokenFacts(
    MemoryErasureKeyStatus KeyStatus,
    long UnverifiableCovenantFingerprints,
    long UnverifiableSagaFingerprints,
    long UnverifiableLexiconFingerprints,
    long UnverifiableCovenantReceipts,
    long UnverifiableSagaReceipts,
    long UnverifiableLexiconReceipts);

/// <summary>One issued erasure token and the wall-clock times that describe it.</summary>
/// <remarks>
/// The codec stamps both times from the same clock that bounds the token, so a preflight never
/// reports an expiry the token does not have.
/// </remarks>
internal sealed record MemoryErasureIssuedToken(
    string Token,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc);

/// <summary>The erasure purposes of the short-lived memory token codec.</summary>
/// <remarks>
/// Every read failure, whatever its cause, is one <see cref="ErrorCodes.MemoryErasure.InvalidPreflight"/>
/// refusal, so a token that expired, was tampered with, or belongs to another purpose are
/// indistinguishable to the caller.
/// </remarks>
internal interface IMemoryErasureTokenCodec
{
    Result<MemoryErasureIssuedToken> IssueErasurePlan(MemoryErasurePlanTokenFacts facts);

    Result<MemoryErasurePlanTokenFacts> ReadErasurePlan(string token);

    Result<MemoryErasureIssuedToken> IssueErasureKeyReset(MemoryErasureKeyResetTokenFacts facts);

    Result<MemoryErasureKeyResetTokenFacts> ReadErasureKeyReset(string token);
}
