using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Lexicon;

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<LexiconScopeKind>))]
public enum LexiconScopeKind
{
    Global = 1,
    Campaign = 2,
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<LexiconRetrievalEligibility>))]
public enum LexiconRetrievalEligibility
{
    Eligible = 1,
    Retired = 2,
}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<LexiconCurationOutcomeKind>))]
public enum LexiconCurationOutcomeKind
{
    Applied = 1,
    Unchanged = 2,
    AlreadyRetired = 3,
    NotRetired = 4,
    AlreadyPinned = 5,
    NotPinned = 6,
}

public sealed record LexiconCurationScope(
    LexiconScopeKind Kind,
    Guid? CampaignId)
{
    public Result Validate() =>
        Kind switch
        {
            LexiconScopeKind.Global when CampaignId is null => Result.Success(),
            LexiconScopeKind.Campaign when CampaignId is { } id && id != Guid.Empty => Result.Success(),
            LexiconScopeKind.Global => Invalid("A Global Lexicon target cannot also name a Campaign."),
            LexiconScopeKind.Campaign => Invalid("A Campaign Lexicon target requires a nonempty Campaign identity."),
            _ => Invalid("A Lexicon target must state a recognized scope."),
        };

    private static Error Invalid(string message) =>
        new(ErrorCodes.Lexicon.InvalidScope, message);
}

public sealed record LexiconEntryLifecycle(
    DateTimeOffset? RetiredAtUtc,
    DateTimeOffset? PinnedAtUtc);

public sealed record LexiconReplacementContent(
    string Type,
    string[] Facts);

public sealed record LexiconCurationAnnalHead(
    [property: JsonRequired] bool IsPresent,
    string? ClaimId,
    string? VersionId,
    int? Revision,
    AnnalOperation? Operation,
    AnnalContentHashFormat? ContentHashFormat,
    string? ContentHash)
{
    public Result Validate()
    {
        if (!IsPresent)
        {
            return ClaimId is null
                && VersionId is null
                && Revision is null
                && Operation is null
                && ContentHashFormat is null
                && ContentHash is null
                ? Result.Success()
                : Invalid("An absent Lexicon Annals head cannot carry head evidence.");
        }

        if (string.IsNullOrWhiteSpace(ClaimId)
            || string.IsNullOrWhiteSpace(VersionId)
            || Revision is not > 0
            || Operation is not (AnnalOperation.Assert or AnnalOperation.Correct or AnnalOperation.Retire)
            || ContentHashFormat is not (AnnalContentHashFormat.LegacyStoreDigest or AnnalContentHashFormat.LexiconStructuredSnapshot))
        {
            return Invalid("A present Lexicon Annals head requires complete recognized identity evidence.");
        }

        if (Operation == AnnalOperation.Retire)
        {
            return ContentHash is null
                ? Result.Success()
                : Invalid("A Lexicon retirement head cannot carry a content digest.");
        }

        return LexiconCurationValidation.IsSha256Hex(ContentHash)
            ? Result.Success()
            : Invalid("A content-bearing Lexicon Annals head requires a SHA-256 digest.");
    }

    private static Error Invalid(string message) =>
        new(ErrorCodes.Lexicon.InvalidCurationTarget, message);
}

public sealed record LexiconCurationSensitivityLabel(
    [property: JsonRequired] bool IsPresent,
    Guid? LabelId,
    ulong? ArtifactRevision,
    string? ArtifactContentDigest,
    GenerationProvenance? GenerationProvenance)
{
    public Result Validate()
    {
        if (!IsPresent)
        {
            return LabelId is null
                && ArtifactRevision is null
                && ArtifactContentDigest is null
                && GenerationProvenance is null
                ? Result.Success()
                : Invalid("An absent Lexicon sensitivity label cannot carry label evidence.");
        }

        return LabelId is { } labelId
            && labelId != Guid.Empty
            && ArtifactRevision is > 0
            && LexiconCurationValidation.IsSha256Hex(ArtifactContentDigest)
            && GenerationProvenance is not null
            ? Result.Success()
            : Invalid("A present Lexicon sensitivity label requires complete recognized evidence.");
    }

    private static Error Invalid(string message) =>
        new(ErrorCodes.Lexicon.InvalidCurationTarget, message);
}

public sealed record LexiconCurationTarget(
    LexiconCurationScope Scope,
    string NormalizedName,
    Guid EntryId,
    long CurationGeneration,
    string SnapshotDigest,
    LexiconEntryLifecycle Lifecycle,
    LexiconCurationAnnalHead AnnalHead,
    LexiconCurationSensitivityLabel SensitivityLabel)
{
    public Result Validate()
    {
        if (Scope is null || Scope.Validate().IsFailure
            || string.IsNullOrWhiteSpace(NormalizedName)
            || EntryId == Guid.Empty
            || CurationGeneration <= 0
            || !LexiconCurationValidation.IsSha256Hex(SnapshotDigest)
            || Lifecycle is null
            || AnnalHead is null
            || AnnalHead.Validate().IsFailure
            || SensitivityLabel is null
            || SensitivityLabel.Validate().IsFailure)
        {
            return new Error(
                ErrorCodes.Lexicon.InvalidCurationTarget,
                "The Lexicon curation target is incomplete or malformed.");
        }

        return Result.Success();
    }
}

public sealed record LexiconCurationResult(
    LexiconCurationOutcomeKind Outcome,
    LexiconEntryDetail Entry);

public sealed record LexiconAnnalFactProvenance(
    string AnnalVersionId,
    int FactOrdinal,
    Guid SessionId,
    Guid AttachmentId,
    string LogicalKey,
    int AttachmentVersion,
    string AttachmentContentHash,
    DateTimeOffset MaterializedAt,
    string SourceType);

public sealed record LexiconEntryDetail(
    LexiconEntryDto Entry,
    LexiconCurationScope Scope,
    AnnalOrigin? CurrentOrigin,
    LexiconEntryLifecycle Lifecycle,
    LexiconRetrievalEligibility Eligibility,
    long CurationGeneration,
    string SnapshotDigest,
    LexiconCurationTarget Target,
    AnnalClaimVersion[] AnnalHistory,
    LexiconAnnalFactProvenance[] HistoricalFactProvenance);

public sealed record LexiconInspectionResult<T>(
    T Value,
    bool ContainsProtectedContent);

internal static class LexiconCurationValidation
{
    internal static bool IsSha256Hex(string? value)
    {
        if (value is not { Length: 64 })
        {
            return false;
        }

        foreach (char character in value)
        {
            if (character is not ((>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F')))
            {
                return false;
            }
        }

        return true;
    }
}
