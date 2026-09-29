using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Core.Lexicon;

using RetroDownfall.Arcanum.Core.Serialization;

using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Core.Memory;

/// <summary>Whether an erase proved every local copy scrubbed, or only every row removed.</summary>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<MemoryLocalErasureOutcome>))]
public enum MemoryLocalErasureOutcome
{
    Verified = 1,

    RowsRemovedScrubPending = 2,
}

/// <summary>
/// Why a committed erase is not yet verified. Only <see cref="WalCheckpointPending"/> can clear later.
/// </summary>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<MemoryErasureScrubPendingReason>))]
public enum MemoryErasureScrubPendingReason
{
    WalCheckpointPending = 1,

    FullTextSecureDeleteUnverified = 2,

    VectorIndexScrubUnverified = 3,
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<MemoryErasureWalCheckpointAttempt>))]
public enum MemoryErasureWalCheckpointAttempt
{
    Truncated = 1,

    Busy = 2,

    Unavailable = 3,

    NotAttempted = 4,
}

/// <summary>The external channels an erase reports exposure evidence for, in receipt column order.</summary>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<MemoryExternalChannel>))]
public enum MemoryExternalChannel
{
    InferenceProviderAuthorship = 1,

    InferenceProviderContext = 2,

    EmbeddingProvider = 3,

    EncryptedBackup = 4,

    OtherExternal = 5,
}

/// <summary>What is known about one external channel. No value means "not disclosed".</summary>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<MemoryExternalEvidence>))]
public enum MemoryExternalEvidence
{
    Known = 1,

    ReceiptWindow = 2,

    NotRecorded = 3,

    NotApplicable = 4,
}

/// <summary>An erase never revokes anything outside the installation, and says so.</summary>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<MemoryExternalRevocation>))]
public enum MemoryExternalRevocation
{
    NotPerformed = 1,
}

/// <summary>The local copies an erase does not reach.</summary>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<MemoryRetainedLocalCopy>))]
public enum MemoryRetainedLocalCopy
{
    SessionTranscripts = 1,

    SearchAndSummaryDerivatives = 2,

    Attachments = 3,

    ResponseCaches = 4,

    ApplicationLogs = 5,

    AuditLog = 6,

    BackupArchives = 7,

    OtherLocalState = 8,
}

/// <summary>The scope-boundary and availability notes an erase states.</summary>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<MemoryErasureNote>))]
public enum MemoryErasureNote
{
    GlobalKeyStillProposableInCampaigns = 1,

    UnresolvedScopeStopsMatchingOnResolution = 2,

    OtherScopesUnaffected = 3,

    CovenantDrainsInFlightTurns = 4,
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<MemoryErasureReleaseOutcome>))]
public enum MemoryErasureReleaseOutcome
{
    Released = 1,

    NotFingerprinted = 2,
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<MemoryErasureKeyStatus>))]
public enum MemoryErasureKeyStatus
{
    Absent = 1,

    Present = 2,

    Unavailable = 3,

    Lost = 4,
}

/// <summary>
/// The durable bitmask form of a set of scrub-pending reasons: bit <c>1 &lt;&lt; (code - 1)</c> per reason.
/// </summary>
public static class MemoryErasureScrubPendingReasons
{
    private const int Count = 3;

    public static int ToMask(IEnumerable<MemoryErasureScrubPendingReason> reasons)
    {
        ArgumentNullException.ThrowIfNull(reasons);

        return MemoryErasureMasks.ToMask(reasons.Select(static reason => (int)reason), Count);
    }

    public static MemoryErasureScrubPendingReason[] FromMask(int mask) =>
        [.. MemoryErasureMasks.Codes(mask, Count).Select(static code => (MemoryErasureScrubPendingReason)code)];
}

/// <summary>
/// The durable bitmask form of a set of retained local copies: bit <c>1 &lt;&lt; (code - 1)</c> per copy.
/// </summary>
public static class MemoryRetainedLocalCopies
{
    private const int Count = 8;

    public static int ToMask(IEnumerable<MemoryRetainedLocalCopy> copies)
    {
        ArgumentNullException.ThrowIfNull(copies);

        return MemoryErasureMasks.ToMask(copies.Select(static copy => (int)copy), Count);
    }

    public static MemoryRetainedLocalCopy[] FromMask(int mask) =>
        [.. MemoryErasureMasks.Codes(mask, Count).Select(static code => (MemoryRetainedLocalCopy)code)];
}

public sealed record MemoryExternalExposureChannelDto(
    MemoryExternalChannel Channel,
    MemoryExternalEvidence Evidence);

public sealed record MemoryErasureExternalExposureDto(
    MemoryExternalRevocation Revocation,
    MemoryExternalExposureChannelDto[] Channels);

public sealed record LexiconErasurePlanFacts(bool GlobalEntryResurfaces);

public sealed record CovenantErasurePlanFacts(
    int ConfirmedVersions,
    int ProposedVersions,
    int ProvenanceLeaves,
    int MutationReceipts,
    int CurationRows,
    int OutboxRows,
    int SearchDocuments,
    bool ReclaimsKey,
    bool RetainsCampaignMask,
    bool GlobalConfirmedResurfaces,
    bool IsPinned,
    int AffectedCampaigns);

public sealed record MemoryErasurePlanDto(
    int ErasedItemCount,
    long RowsToRemove,
    int LabelsToRemove,
    int RetirementSuppressionsToRemove,
    bool Pinned,
    LexiconErasurePlanFacts? Lexicon,
    CovenantErasurePlanFacts? Covenant);

public sealed record MemoryErasurePreflightDto(
    MemoryReviewStore Store,
    Guid MutationId,
    string RequestDigest,
    string EffectDigest,
    MemoryErasurePlanDto Plan,
    MemoryErasureExternalExposureDto External,
    MemoryRetainedLocalCopy[] RetainedLocalCopies,
    MemoryErasureNote[] Notes,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string PreflightToken);

public sealed record MemoryErasureLocalResultDto(
    MemoryLocalErasureOutcome Outcome,
    MemoryErasureScrubPendingReason[] PendingReasons,
    MemoryErasureWalCheckpointAttempt WalCheckpointAttempt,
    int ErasedItemCount,
    long RemovedRowCount,
    int RemovedLabelCount,
    int RemovedRetirementSuppressionCount,
    bool SuppressionFingerprintRecorded);

public sealed record MemoryErasureResultDto(
    MemoryReviewStore Store,
    Guid MutationId,
    bool Replayed,
    string EffectDigest,
    MemoryErasureLocalResultDto Local,
    MemoryErasureExternalExposureDto External,
    MemoryRetainedLocalCopy[] RetainedLocalCopies,
    MemoryErasureNote[] Notes);

public sealed record MemoryErasureReleaseResultDto(
    MemoryReviewStore Store,
    MemoryErasureReleaseOutcome Outcome,
    int ReleasedCount);

public sealed record MemoryErasureStoreCountsDto(
    MemoryReviewStore Store,
    long Fingerprints,
    long Unverifiable,
    long Receipts);

public sealed record MemoryErasureStatusDto(
    MemoryErasureKeyStatus KeyStatus,
    MemoryErasureStoreCountsDto[] Stores,
    long PendingScrubReceipts);

public sealed record MemoryErasureScrubResultDto(
    MemoryErasureWalCheckpointAttempt WalCheckpointAttempt,
    long Verified,
    long StillPending);

public sealed record MemoryErasureKeyResetPreflightDto(
    MemoryErasureKeyStatus KeyStatus,
    MemoryErasureStoreCountsDto[] Stores,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string PreflightToken);

public sealed record MemoryErasureKeyResetRequest(string PreflightToken);

public sealed record MemoryErasureKeyResetResultDto(
    MemoryErasureKeyStatus KeyStatus,
    long FingerprintsDiscarded,
    long ReceiptsDiscarded,
    bool KeyCreated);

public sealed record SagaErasePrepareRequest(
    string MemoryId,
    string ExpectedContentHash,
    string? ExpectedClaimVersionId,
    Guid MutationId);

public sealed record SagaEraseRequest(
    string MemoryId,
    string ExpectedContentHash,
    string? ExpectedClaimVersionId,
    Guid MutationId,
    string PreflightToken);

public sealed record SagaErasureReleaseRequest(
    SagaMemoryScopeKind ScopeKind,
    Guid? CampaignId,
    string Content);

public sealed record LexiconErasePrepareRequest(
    LexiconCurationTarget Target,
    Guid MutationId);

public sealed record LexiconEraseRequest(
    LexiconCurationTarget Target,
    Guid MutationId,
    string PreflightToken);

public sealed record LexiconErasureReleaseRequest(
    LexiconCurationScope Scope,
    string Name);

/// <summary>The one-bit-per-code mask both durable erasure masks share, over codes 1 to count.</summary>
internal static class MemoryErasureMasks
{
    internal static int ToMask(IEnumerable<int> codes, int count)
    {
        int mask = 0;

        foreach (int code in codes)
        {
            if (code < 1 || code > count)
            {
                throw new ArgumentOutOfRangeException(nameof(codes), "An erasure mask has no bit for this value.");
            }

            mask |= 1 << (code - 1);
        }

        return mask;
    }

    /// <summary>The codes a mask sets, in code order.</summary>
    internal static IEnumerable<int> Codes(int mask, int count)
    {
        if (mask < 0 || mask >= 1 << count)
        {
            throw new ArgumentOutOfRangeException(nameof(mask), "This erasure mask sets a bit no value owns.");
        }

        return Enumerable.Range(1, count).Where(code => (mask & (1 << (code - 1))) != 0);
    }
}
