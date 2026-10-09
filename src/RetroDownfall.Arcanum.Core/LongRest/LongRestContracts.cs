using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.LongRest;

/// <summary>The evidence the caller explicitly declares for one bounded Long Rest transformation.</summary>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<LongRestTransformationKind>))]
public enum LongRestTransformationKind
{
    ExactDuplicates = 1,

    EquivalentObservations = 2,

    Supersession = 3,
}

/// <summary>Whether the receipt authorizes an effective canonical projection change.</summary>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<LongRestOutcome>))]
public enum LongRestOutcome
{
    Applied = 1,

    NoChange = 2,
}

/// <summary>A stable, content-free explanation for a policy decision.</summary>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<LongRestReason>))]
public enum LongRestReason
{
    None = 0,

    SingleInput = 1,

    Pinned = 2,

    Retired = 3,

    DifferentScope = 4,

    Protected = 5,

    DifferentContent = 6,

    IncompatibleValidity = 7,

    DependencyConflict = 8,

    AlreadyConsolidated = 9,

    EmbeddingMissing = 10,
}

/// <summary>An exact durable subject and immutable content binding observed by the caller.</summary>
public sealed record LongRestTarget(string MemoryId, string ExpectedVersionId, string ExpectedContentHash);

/// <summary>Explicit bounded evidence; semantic declarations do not perform discovery or model work.</summary>
public sealed record LongRestRequest(
    LongRestTransformationKind Kind,
    LongRestTarget[] Targets,
    string? SurvivorVersionId = null);

/// <summary>A receipt's exact retained input coordinates, without memory content.</summary>
public sealed record LongRestReceiptTarget(
    string MemoryId,
    string ClaimId,
    string VersionId,
    int Revision,
    AnnalContentHashFormat ContentHashFormat,
    string ContentHash);

/// <summary>Immutable content-addressed policy evidence; all output versions already exist.</summary>
public sealed record LongRestReceipt(
    string ReceiptId,
    int PolicyVersion,
    LongRestTransformationKind Kind,
    LongRestOutcome Outcome,
    LongRestReason Reason,
    string InputHash,
    string OutputHash,
    LongRestReceiptTarget[] Targets,
    string? SurvivorMemoryId,
    string? SurvivorClaimId,
    string? SurvivorVersionId,
    string[] SupersededVersionIds);
