using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Workspaces;

namespace RetroDownfall.TheForge.Ux.ViewModels.Atelier;

/// <summary>Options for the New Campaign dialog (prefills, loopback browse, remote host-path copy).</summary>
public sealed record NewCampaignDialogOptions(
    string? PrefillName = null,
    string? PrefillPath = null,
    WorkspaceType? PrefillType = null,
    string? PrefillDescription = null,
    bool AllowLocalFolderBrowse = false,
    string? PathFieldLabel = null,
    string? IntroText = null);

/// <summary>Inputs for registering a new campaign via <see cref="RegisterCampaignRequest"/>.</summary>
public sealed record NewCampaignInputs(
    string Name,
    string Path,
    WorkspaceType Type,
    string? Description);

/// <summary>Inputs for updating a campaign via <see cref="UpdateCampaignRequest"/> (Settings editor deferred).</summary>
public sealed record EditCampaignInputs(
    string Name,
    WorkspaceType Type,
    string? Description);

/// <summary>
/// Modal seams for Atelier campaign New / Edit / Import-strategy dialogs. Tests fake this; the
/// Avalonia implementation builds Whispers-style <c>Window.ShowDialog</c> modals.
/// </summary>
public interface ICampaignDialogService
{

    Task<NewCampaignInputs?> PromptNewCampaignAsync(
        NewCampaignDialogOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Prompts for a campaign folder path. When <paramref name="allowLocalFolderBrowse"/> is true
    /// (loopback), offers Browse…; otherwise typed path on the Arcanum host only.
    /// </summary>
    Task<string?> PromptOpenCampaignPathAsync(
        bool allowLocalFolderBrowse,
        CancellationToken cancellationToken = default);

    Task<EditCampaignInputs?> PromptEditCampaignAsync(CampaignDto existing, CancellationToken cancellationToken);

    /// <summary>Returns <c>"merge"</c> or <c>"replace"</c>, or <c>null</c> when cancelled.</summary>
    Task<string?> PromptImportStrategyAsync(CancellationToken cancellationToken);

}
