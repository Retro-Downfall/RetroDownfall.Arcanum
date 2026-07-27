using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// Marker for JSON results whose shape must remain valid while model-visible output is bounded.
/// </summary>
public interface IStructuredToolResult;

/// <summary>
/// Allows the materializer to retain a bounded leading slice and let the typed result update its
/// own omission counters.
/// </summary>
public interface IStructuredToolResult<TSelf> : IStructuredToolResult
    where TSelf : IStructuredToolResult<TSelf>
{
    [JsonIgnore]
    int MaterializationItemCount { get; }

    TSelf RetainLeadingItems(int itemCount);
}

/// <summary>
/// Smallest result used when even a typed empty result cannot fit the configured budget.
/// It is always emitted as valid source-generated JSON and is never substring-truncated.
/// </summary>
public sealed record MinimalStructuredToolResultEnvelope
{
    public string Status { get; init; } = "truncated";

    public bool Truncated { get; init; } = true;
}

public sealed record WorkspaceSearchToolResultItem(
    string Path,
    int Line,
    int Column,
    string Preview);

public sealed record WorkspaceSearchToolResultEnvelope
    : IStructuredToolResult<WorkspaceSearchToolResultEnvelope>
{
    public string Status { get; init; } = "ok";

    public string? Code { get; init; }

    public string? Message { get; init; }

    public WorkspaceSearchToolResultItem[] Matches { get; init; } = [];

    public int TotalMatchCount { get; init; }

    public int OmittedMatchCount { get; init; }

    public string? RegexEngine { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int FilesVisited { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int FilesSearched { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long BytesSearched { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int TraversalSteps { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int SkippedDirectorySymlinkCount { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int SkippedEscapingSymlinkCount { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int SkippedDuplicateFileCount { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int SkippedBinaryFileCount { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int SkippedFilteredFileCount { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int SkippedUnreadableFileCount { get; init; }

    public bool Truncated { get; init; }

    [JsonIgnore]
    public int MaterializationItemCount => Matches.Length;

    public WorkspaceSearchToolResultEnvelope RetainLeadingItems(int itemCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(itemCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(itemCount, Matches.Length);

        if (itemCount == Matches.Length)
        {
            return this;
        }

        return this with
        {
            Matches = Matches[..itemCount],
            OmittedMatchCount = OmittedMatchCount + Matches.Length - itemCount,
            Truncated = true,
        };
    }
}

public sealed record WorkspacePatchHunkDiagnostic(
    string Path,
    int Ordinal,
    string Header,
    int ExpectedLine,
    int? MatchedLine,
    bool Relocated,
    int[] CandidateLines,
    int TotalCandidateCount,
    int OmittedCandidateCount);

public sealed record WorkspacePatchToolResultItem(
    string Path,
    string Operation,
    string Status,
    int AppliedHunks,
    string? Message = null)
{
    public WorkspacePatchHunkDiagnostic[] Hunks { get; init; } = [];
}

public sealed record WorkspacePatchToolResultEnvelope
    : IStructuredToolResult<WorkspacePatchToolResultEnvelope>
{
    public string Status { get; init; } = "ok";

    public string? Code { get; init; }

    public string? Message { get; init; }

    public WorkspacePatchHunkDiagnostic? Diagnostic { get; init; }

    public WorkspacePatchToolResultItem[] Files { get; init; } = [];

    public int TotalFileCount { get; init; }

    public int OmittedFileCount { get; init; }

    public string[] AffectedPaths { get; init; } = [];

    public int TotalAffectedPathCount { get; init; }

    public int OmittedAffectedPathCount { get; init; }

    public string[] RecoveryArtifactPaths { get; init; } = [];

    public int TotalRecoveryArtifactPathCount { get; init; }

    public int OmittedRecoveryArtifactPathCount { get; init; }

    public bool Truncated { get; init; }

    [JsonIgnore]
    public int MaterializationItemCount =>
        Math.Max(
            Files.Length,
            Math.Max(
                AffectedPaths.Length,
                RecoveryArtifactPaths.Length));

    public WorkspacePatchToolResultEnvelope RetainLeadingItems(int itemCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(itemCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            itemCount,
            MaterializationItemCount);

        return RetainLeadingItems(
            Math.Min(itemCount, Files.Length),
            Math.Min(itemCount, AffectedPaths.Length),
            Math.Min(itemCount, RecoveryArtifactPaths.Length));
    }

    public WorkspacePatchToolResultEnvelope RetainLeadingItems(
        int fileCount,
        int affectedPathCount,
        int recoveryArtifactPathCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fileCount);
        ArgumentOutOfRangeException.ThrowIfNegative(affectedPathCount);
        ArgumentOutOfRangeException.ThrowIfNegative(
            recoveryArtifactPathCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            fileCount,
            Files.Length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            affectedPathCount,
            AffectedPaths.Length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            recoveryArtifactPathCount,
            RecoveryArtifactPaths.Length);

        if (fileCount == Files.Length
            && affectedPathCount == AffectedPaths.Length
            && recoveryArtifactPathCount == RecoveryArtifactPaths.Length)
        {
            return this;
        }

        return this with
        {
            Files = Files[..fileCount],
            OmittedFileCount =
                OmittedFileCount + Files.Length - fileCount,
            AffectedPaths = AffectedPaths[..affectedPathCount],
            OmittedAffectedPathCount =
                OmittedAffectedPathCount
                + AffectedPaths.Length
                - affectedPathCount,
            RecoveryArtifactPaths =
                RecoveryArtifactPaths[..recoveryArtifactPathCount],
            OmittedRecoveryArtifactPathCount =
                OmittedRecoveryArtifactPathCount
                + RecoveryArtifactPaths.Length
                - recoveryArtifactPathCount,
            Truncated = true,
        };
    }
}

public sealed record WorkspaceCheckToolResultItem(
    string? File,
    int? Line,
    int? Column,
    string Severity,
    string? Code,
    string Message);

public sealed record WorkspaceCheckToolResultEnvelope
    : IStructuredToolResult<WorkspaceCheckToolResultEnvelope>
{
    public string Status { get; init; } = "ok";

    public string? Code { get; init; }

    public string? Message { get; init; }

    public string? ProfileId { get; init; }

    public string? SelectedSdkVersion { get; init; }

    public WorkspaceCheckToolResultItem[] Diagnostics { get; init; } = [];

    public int TotalDiagnosticCount { get; init; }

    public int OmittedDiagnosticCount { get; init; }

    public int ErrorCount { get; init; }

    public int WarningCount { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TotalTestCount { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PassedTestCount { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FailedTestCount { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SkippedTestCount { get; init; }

    public int? ExitCode { get; init; }

    public string? StandardOutput { get; init; }

    public string? StandardError { get; init; }

    public bool Truncated { get; init; }

    [JsonIgnore]
    public int MaterializationItemCount =>
        Diagnostics.Length
        + (HasRawStreams ? 1 : 0);

    public WorkspaceCheckToolResultEnvelope RetainLeadingItems(int itemCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(itemCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            itemCount,
            MaterializationItemCount);

        if (itemCount == MaterializationItemCount)
        {
            return this;
        }

        int diagnosticCount = Math.Min(
            itemCount,
            Diagnostics.Length);
        bool retainRawStreams =
            HasRawStreams
            && itemCount > Diagnostics.Length;

        return this with
        {
            Diagnostics = Diagnostics[..diagnosticCount],
            OmittedDiagnosticCount =
                OmittedDiagnosticCount
                + Diagnostics.Length
                - diagnosticCount,
            StandardOutput =
                retainRawStreams ? StandardOutput : null,
            StandardError =
                retainRawStreams ? StandardError : null,
            Truncated = true,
        };
    }

    [JsonIgnore]
    private bool HasRawStreams =>
        StandardOutput is not null
        || StandardError is not null;
}
