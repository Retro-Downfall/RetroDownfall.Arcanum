namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Narrow runtime/startup seam for host capability. Core configuration validation remains
/// infrastructure-independent while the runtime can report whether <c>workspace_check</c> is
/// currently safe and available to advertise.
/// </summary>
public interface IWorkspaceCheckAdvertisementEligibility
{
    bool IsCurrentlyEligible { get; }
}

public sealed record WorkspaceCheckCapabilityStatus(
    bool IsAvailable,
    bool IsHealthDegraded,
    string Reason);

public interface IWorkspaceCheckCapabilityReporter
    : IWorkspaceCheckAdvertisementEligibility
{

    WorkspaceCheckCapabilityStatus GetStatus(
        string? workspaceRoot = null);

    ValueTask<WorkspaceCheckCapabilityStatus> GetStatusAsync(
        string? workspaceRoot = null,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(
            GetStatus(workspaceRoot));
}
