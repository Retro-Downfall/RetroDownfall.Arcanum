using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Core.Memory;

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<MemoryReviewAction>))]
public enum MemoryReviewAction
{
    Confirm = 1,

    Correct = 2,

    Retire = 3,

    Pin = 4,

    Unpin = 5,
}

public sealed record SagaReviewListRequest(
    SagaMemoryScopeKind ScopeKind,
    Guid? CampaignId,
    int Limit,
    string? Cursor)
{
    public Result Validate() => MemoryReviewContractValidation.First(
        MemoryReviewContractValidation.ValidateSagaScope(ScopeKind, CampaignId),
        MemoryReviewContractValidation.ValidatePage(Limit, Cursor));
}

public sealed record LexiconReviewListRequest(
    LexiconCurationScope Scope,
    int Limit,
    string? Cursor)
{
    public Result Validate() => MemoryReviewContractValidation.First(
        Scope is null
            ? MemoryReviewContractValidation.Invalid("An exact Lexicon review scope is required.")
            : Scope.Validate(),
        MemoryReviewContractValidation.ValidatePage(Limit, Cursor));
}

public sealed record CovenantReviewListRequest(
    CovenantScope Scope,
    Guid? CampaignId,
    CovenantLane Lane,
    int Limit,
    string? Cursor)
{
    public Result Validate() => MemoryReviewContractValidation.First(
        MemoryReviewContractValidation.ValidateCovenantScope(Scope, CampaignId, Lane),
        MemoryReviewContractValidation.ValidatePage(Limit, Cursor));
}

public sealed record SagaReviewDecision(
    string ObservationToken,
    string? ReplacementContent);

public sealed record LexiconReviewDecision(
    string ObservationToken,
    LexiconReplacementContent? ReplacementContent);

public sealed record CovenantReviewDecision(
    string ObservationToken,
    string? ReplacementContent);

public sealed record SagaReviewCurrentDto(
    SagaMemoryDto Memory,
    string ContentHash,
    SagaMemoryLifecycle Lifecycle,
    SagaRetrievalEligibility Eligibility,
    AnnalClaimHead? Claim);

public sealed record LexiconReviewCurrentDto(
    LexiconEntryDto Entry,
    LexiconCurationScope Scope,
    AnnalOrigin? CurrentOrigin,
    LexiconEntryLifecycle Lifecycle,
    LexiconRetrievalEligibility Eligibility,
    long CurationGeneration,
    string SnapshotDigest,
    LexiconCurationTarget Target);

public sealed record SagaReviewBulkPrepareRequest(
    Guid RequestId,
    SagaMemoryScopeKind ScopeKind,
    Guid? CampaignId,
    MemoryReviewAction Action,
    SagaReviewDecision[] Decisions)
{
    public Result Validate() => MemoryReviewContractValidation.First(
        MemoryReviewContractValidation.ValidateRequest(RequestId, Action, Decisions),
        MemoryReviewContractValidation.ValidateSagaScope(ScopeKind, CampaignId),
        MemoryReviewContractValidation.ValidateDecisions(
            Action,
            Decisions,
            static decision => decision?.ObservationToken,
            static decision => !string.IsNullOrWhiteSpace(decision?.ReplacementContent)));
}

public sealed record LexiconReviewBulkPrepareRequest(
    Guid RequestId,
    LexiconCurationScope Scope,
    MemoryReviewAction Action,
    LexiconReviewDecision[] Decisions)
{
    public Result Validate() => MemoryReviewContractValidation.First(
        MemoryReviewContractValidation.ValidateRequest(RequestId, Action, Decisions),
        Scope is null
            ? MemoryReviewContractValidation.Invalid("An exact Lexicon review scope is required.")
            : Scope.Validate(),
        MemoryReviewContractValidation.ValidateDecisions(
            Action,
            Decisions,
            static decision => decision?.ObservationToken,
            static decision => decision?.ReplacementContent is not null));
}

public sealed record CovenantReviewBulkPrepareRequest(
    Guid RequestId,
    CovenantScope Scope,
    Guid? CampaignId,
    CovenantLane Lane,
    MemoryReviewAction Action,
    CovenantReviewDecision[] Decisions)
{
    public Result Validate() => MemoryReviewContractValidation.First(
        MemoryReviewContractValidation.ValidateRequest(RequestId, Action, Decisions),
        MemoryReviewContractValidation.ValidateCovenantScope(Scope, CampaignId, Lane),
        MemoryReviewContractValidation.ValidateDecisions(
            Action,
            Decisions,
            static decision => decision?.ObservationToken,
            static decision => !string.IsNullOrWhiteSpace(decision?.ReplacementContent)));
}

public sealed record SagaReviewBulkApplyRequest(
    SagaReviewBulkPrepareRequest Request,
    string PreparedPlanToken)
{
    public Result Validate() => MemoryReviewContractValidation.First(
        Request is null
            ? MemoryReviewContractValidation.Invalid("A prepared Saga review request is required.")
            : Request.Validate(),
        MemoryReviewContractValidation.ValidateToken(PreparedPlanToken, "prepared review plan"));
}

public sealed record LexiconReviewBulkApplyRequest(
    LexiconReviewBulkPrepareRequest Request,
    string PreparedPlanToken)
{
    public Result Validate() => MemoryReviewContractValidation.First(
        Request is null
            ? MemoryReviewContractValidation.Invalid("A prepared Lexicon review request is required.")
            : Request.Validate(),
        MemoryReviewContractValidation.ValidateToken(PreparedPlanToken, "prepared review plan"));
}

public sealed record CovenantReviewBulkApplyRequest(
    CovenantReviewBulkPrepareRequest Request,
    string PreparedPlanToken)
{
    public Result Validate() => MemoryReviewContractValidation.First(
        Request is null
            ? MemoryReviewContractValidation.Invalid("A prepared Covenant review request is required.")
            : Request.Validate(),
        MemoryReviewContractValidation.ValidateToken(PreparedPlanToken, "prepared review plan"));
}

public sealed record SagaReviewItemDto(
    long EventSequence,
    string SubjectId,
    string VersionId,
    int Revision,
    AnnalOperation Operation,
    AnnalOrigin Origin,
    SagaMemoryScopeKind ScopeKind,
    Guid? CampaignId,
    Guid? SourceSessionId,
    string? Source,
    string? ContentHash,
    bool IsCurrent,
    SagaReviewCurrentDto? Current,
    string ObservationToken);

public sealed record SagaReviewPageDto(
    SagaReviewItemDto[] Items,
    long ReviewedThroughEventSequence,
    long FrozenThroughEventSequence,
    string? NextCursor,
    bool Truncated);

public sealed record LexiconReviewItemDto(
    long EventSequence,
    Guid EntryId,
    string VersionId,
    int Revision,
    AnnalOperation Operation,
    AnnalOrigin Origin,
    LexiconCurationScope Scope,
    Guid? SourceSessionId,
    string? ContentHash,
    bool IsCurrent,
    LexiconReviewCurrentDto? Current,
    string ObservationToken);

public sealed record LexiconReviewPageDto(
    LexiconReviewItemDto[] Items,
    long ReviewedThroughEventSequence,
    long FrozenThroughEventSequence,
    string? NextCursor,
    bool Truncated,
    bool ContainsProtectedContent);

public sealed record CovenantReviewItemDto(
    long EventSequence,
    Guid EntryId,
    Guid VersionId,
    CovenantOperation Operation,
    CovenantOrigin Origin,
    CovenantScope Scope,
    Guid? CampaignId,
    CovenantLane Lane,
    string Key,
    string CompiledContent,
    Guid? SourceTurnId,
    string? SourceToolCallId,
    bool IsCurrent,
    CovenantHeadDto? Current,
    CovenantSourceDto[] Sources,
    string ObservationToken);

public sealed record CovenantReviewPageDto(
    CovenantReviewItemDto[] Items,
    long ReviewedThroughEventSequence,
    long FrozenThroughEventSequence,
    string? NextCursor,
    bool Truncated);

public sealed record MemoryReviewBulkPlanItemDto(
    long EventSequence,
    string SubjectId,
    string VersionId,
    bool IsCurrent,
    string Origin,
    string Source,
    string Scope);

public sealed record MemoryReviewBulkPlanDto(
    MemoryReviewStore Store,
    Guid RequestId,
    MemoryReviewAction Action,
    MemoryReviewBulkPlanItemDto[] Items,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string PreparedPlanToken);

/// <summary>One decision's outcome in a bulk review.</summary>
/// <param name="ReleasedErasureFingerprint">
/// Whether a <c>Correct</c> deleted the erasure fingerprint of its replacement in the item's own scope:
/// <see langword="true"/> when it did, <see langword="false"/> when there was nothing to release, and
/// <see langword="null"/> when the store holds fingerprints this host could not check. Every other
/// action, and every replayed item, reports <see langword="false"/>.
/// </param>
public sealed record MemoryReviewBulkItemResultDto(
    long EventSequence,
    string SubjectId,
    string VersionId,
    string Outcome,
    string? ResultingVersionId,
    bool? ReleasedErasureFingerprint = false);

public sealed record MemoryReviewBulkResultDto(
    MemoryReviewStore Store,
    Guid RequestId,
    MemoryReviewAction Action,
    MemoryReviewBulkItemResultDto[] Items,
    long ReviewedThroughEventSequence,
    bool Replayed);

public interface ISagaMemoryReviewService
{
    Task<Result<SagaReviewPageDto>> ListAsync(
        SagaReviewListRequest request,
        CancellationToken cancellationToken);

    Task<Result<MemoryReviewBulkPlanDto>> PrepareAsync(
        SagaReviewBulkPrepareRequest request,
        CancellationToken cancellationToken);

    Task<Result<MemoryReviewBulkResultDto>> ApplyAsync(
        SagaReviewBulkApplyRequest request,
        CancellationToken cancellationToken);
}

public interface ILexiconMemoryReviewService
{
    Task<Result<LexiconReviewPageDto>> ListAsync(
        LexiconReviewListRequest request,
        ICovenantSnapshotReadLease? readLease,
        CancellationToken cancellationToken);

    Task<Result<MemoryReviewBulkPlanDto>> PrepareAsync(
        LexiconReviewBulkPrepareRequest request,
        ICovenantSnapshotReadLease? readLease,
        CancellationToken cancellationToken);

    Task<Result<MemoryReviewBulkResultDto>> ApplyAsync(
        LexiconReviewBulkApplyRequest request,
        CovenantWriteLease? writeLease,
        CancellationToken cancellationToken);
}

public interface ICovenantMemoryReviewService
{
    ValueTask<Result<CovenantReviewPageDto>> ListAsync(
        CovenantReviewListRequest request,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken);

    ValueTask<Result<MemoryReviewBulkPlanDto>> PrepareAsync(
        CovenantReviewBulkPrepareRequest request,
        ICovenantSnapshotReadLease readLease,
        CancellationToken cancellationToken);

    ValueTask<Result<MemoryReviewBulkResultDto>> ApplyAsync(
        CovenantReviewBulkApplyRequest request,
        CovenantWriteLease writeLease,
        CancellationToken cancellationToken);
}

internal static class MemoryReviewContractValidation
{
    internal static Result First(params Result[] results)
    {
        foreach (Result result in results)
        {
            if (result.IsFailure)
            {
                return result;
            }
        }

        return Result.Success();
    }

    internal static Error Invalid(string message) =>
        new(ErrorCodes.Validation.InvalidBody, message);

    internal static Result ValidateSagaScope(SagaMemoryScopeKind scopeKind, Guid? campaignId) =>
        scopeKind switch
        {
            SagaMemoryScopeKind.Global when campaignId is null => Result.Success(),
            SagaMemoryScopeKind.Campaign when campaignId is { } id && id != Guid.Empty => Result.Success(),
            SagaMemoryScopeKind.LegacyUnresolved when campaignId is null => Result.Success(),
            _ => Invalid("A Saga review request requires one exact Global, Campaign, or unresolved scope."),
        };

    internal static Result ValidateCovenantScope(
        CovenantScope scope,
        Guid? campaignId,
        CovenantLane lane)
    {
        bool validScope = scope switch
        {
            CovenantScope.Global => campaignId is null,
            CovenantScope.Campaign => campaignId is { } id && id != Guid.Empty,
            _ => false,
        };

        return validScope && lane is CovenantLane.Confirmed or CovenantLane.Proposed
            ? Result.Success()
            : Invalid("A Covenant review request requires one exact scope and one recognized lane.");
    }

    internal static Result ValidatePage(int limit, string? cursor)
    {
        if (limit is < 1 or > MemoryReviewLimits.MaxPageSize)
        {
            return Invalid($"A memory review page must contain between 1 and {MemoryReviewLimits.MaxPageSize} items.");
        }

        return cursor is null
            || (!string.IsNullOrWhiteSpace(cursor) && cursor.Length <= MemoryReviewLimits.MaxTokenCharacters)
            ? Result.Success()
            : Invalid("A memory review cursor is malformed or over its bound.");
    }

    internal static Result ValidateRequest<TDecision>(
        Guid requestId,
        MemoryReviewAction action,
        TDecision[]? decisions)
    {
        if (requestId == Guid.Empty)
        {
            return Invalid("A client-generated memory review request identity is required.");
        }

        if (!Enum.IsDefined(action))
        {
            return Invalid("A recognized memory review action is required.");
        }

        return decisions is { Length: >= 1 and <= MemoryReviewLimits.MaxBulkOperations }
            ? Result.Success()
            : Invalid($"A memory review batch must contain between 1 and {MemoryReviewLimits.MaxBulkOperations} decisions.");
    }

    internal static Result ValidateDecisions<TDecision>(
        MemoryReviewAction action,
        TDecision[]? decisions,
        Func<TDecision, string?> tokenOf,
        Func<TDecision, bool> hasReplacement)
    {
        if (decisions is null)
        {
            return Invalid("Memory review decisions are required.");
        }

        HashSet<string> observations = new(StringComparer.Ordinal);

        foreach (TDecision decision in decisions)
        {
            string? token = decision is null ? null : tokenOf(decision);

            Result validatedToken = ValidateToken(token, "review observation");

            if (validatedToken.IsFailure)
            {
                return validatedToken;
            }

            if (!observations.Add(token!))
            {
                return Invalid("A memory review batch cannot name one observation more than once.");
            }

            bool replacement = hasReplacement(decision);

            if ((action == MemoryReviewAction.Correct) != replacement)
            {
                return Invalid(action == MemoryReviewAction.Correct
                    ? "Every correction decision requires replacement content."
                    : "Only a correction decision may carry replacement content.");
            }
        }

        return Result.Success();
    }

    internal static Result ValidateToken(string? token, string name) =>
        !string.IsNullOrWhiteSpace(token) && token.Length <= MemoryReviewLimits.MaxTokenCharacters
            ? Result.Success()
            : Invalid($"A bounded {name} token is required.");
}
