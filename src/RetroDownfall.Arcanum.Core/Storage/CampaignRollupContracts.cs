using System.Collections.Immutable;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>Code-owned allocation limits; pages bound work, never Campaign lifetime progress.</summary>
public static class CampaignRollupLimits
{

    public const int SummaryUtf8Bytes = 8192;

    public const int InputPageUtf8Bytes = 32768;

    public const int OutputTokens = 2048;

}

/// <summary>An immutable summary and the exact inputs that gave it its authority and sensitivity.</summary>
public sealed record CampaignRollupArtifact(
    Guid ArtifactId,
    Guid CampaignId,
    Guid? SessionId,
    long Revision,
    string Content,
    CovenantDigest ContentDigest,
    ContentSensitivity Sensitivity,
    GenerationProvenance Provenance,
    CovenantDigest SensitivityDigest,
    long SourceGeneration,
    CovenantDigest SourceManifestDigest,
    long SummarizedThroughSequence,
    long SourceCount);

/// <summary>One native entry from a Session, excluding every copied fork prefix.</summary>
public sealed record CampaignContributionEntry(
    Guid EntryId,
    long Sequence,
    int Role,
    string Content,
    DateTimeOffset CreatedAtUtc,
    CovenantDigest ContentDigest,
    CovenantDigest SensitivityDigest,
    ContentSensitivity Sensitivity,
    GenerationProvenance Provenance);

/// <summary>A bounded, immutable page to replace one Session's own Campaign contribution.</summary>
public sealed record CampaignContributionInput(
    Guid SessionId,
    Guid CampaignId,
    long ExpectedRevision,
    long SourceGeneration,
    long InheritedThroughSequence,
    long SummarizedThroughSequence,
    CampaignRollupArtifact? Previous,
    ImmutableArray<CampaignContributionEntry> Entries,
    CovenantDigest SourceManifestDigest,
    bool HasMore)
{

    public ContentSensitivity Sensitivity
    {
        get
        {
            ContentSensitivity current = Previous?.Sensitivity ?? ContentSensitivity.None;

            foreach (CampaignContributionEntry entry in Entries)
            {
                current = ContentSensitivityAlgebra.Maximum(current, entry.Sensitivity);
            }

            return current;
        }
    }

    public GenerationProvenance Provenance
    {
        get
        {
            GenerationProvenance current = Previous?.Provenance ?? GenerationProvenance.CreateExact([]);

            foreach (CampaignContributionEntry entry in Entries)
            {
                current = current.Merge(entry.Provenance);
            }

            return current;
        }
    }

}

/// <summary>A bounded fold page over persisted contribution artifacts, never raw Session history.</summary>
public sealed record CampaignRollupInput(
    Guid CampaignId,
    long ExpectedRevision,
    long SourceGeneration,
    CampaignRollupArtifact? Previous,
    ImmutableArray<CampaignRollupArtifact> Contributions,
    CovenantDigest SourceManifestDigest,
    Guid? LastFoldedSessionId,
    bool HasMore)
{

    public ContentSensitivity Sensitivity
    {
        get
        {
            ContentSensitivity current = Previous?.Sensitivity ?? ContentSensitivity.None;

            foreach (CampaignRollupArtifact artifact in Contributions)
            {
                current = ContentSensitivityAlgebra.Maximum(current, artifact.Sensitivity);
            }

            return current;
        }
    }

    public GenerationProvenance Provenance
    {
        get
        {
            GenerationProvenance current = Previous?.Provenance ?? GenerationProvenance.CreateExact([]);

            foreach (CampaignRollupArtifact artifact in Contributions)
            {
                current = current.Merge(artifact.Provenance);
            }

            return current;
        }
    }

}

/// <summary>Content-free projection for inspection, retention, and pending-work discovery.</summary>
public sealed record CampaignRollupStatus(
    Guid CampaignId,
    Guid? CurrentArtifactId,
    long Revision,
    long SourceGeneration,
    long SourceCount,
    bool RefoldRequired,
    ContentSensitivity Sensitivity,
    DateTimeOffset? UpdatedAtUtc);

/// <summary>
/// Exact snapshot/CAS lane for Campaign summaries. Null authority permits proven clean inputs only.
/// The store checks authority before reading protected text and again before publication.
/// </summary>
public interface ICampaignRollupStore
{

    Task<Result<CampaignRollupArtifact?>> ReadCurrentAsync(
        Guid campaignId,
        ICovenantSnapshotReadLease? authority,
        CancellationToken cancellationToken);

    Task<Result<CampaignContributionInput?>> PrepareContributionAsync(
        Guid sessionId,
        long? throughSequence,
        ICovenantSnapshotReadLease? authority,
        CancellationToken cancellationToken);

    Task<Result<CampaignRollupInput?>> PrepareRollupAsync(
        Guid campaignId,
        ICovenantSnapshotReadLease? authority,
        CancellationToken cancellationToken);

    Task<Result<CampaignRollupArtifact>> PublishContributionAsync(
        CampaignContributionInput input,
        string content,
        CampaignMaintenancePublication? maintenancePublication,
        ICovenantSnapshotReadLease? authority,
        CancellationToken cancellationToken);

    Task<Result<CampaignRollupArtifact>> PublishRollupAsync(
        CampaignRollupInput input,
        string content,
        CampaignMaintenancePublication? maintenancePublication,
        ICovenantSnapshotReadLease? authority,
        CancellationToken cancellationToken);

    Task<Result> ValidateAsync(
        CampaignRollupArtifact artifact,
        ICovenantSnapshotReadLease? authority,
        CancellationToken cancellationToken);

    Task<Result<CampaignRollupStatus>> ReadStatusAsync(
        Guid campaignId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> FindPendingContributionsAsync(
        DateTimeOffset idleCutoffUtc,
        int pageSize,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> FindPendingContributionsForCampaignAsync(
        Guid campaignId,
        DateTimeOffset idleCutoffUtc,
        int pageSize,
        CancellationToken cancellationToken);

}
