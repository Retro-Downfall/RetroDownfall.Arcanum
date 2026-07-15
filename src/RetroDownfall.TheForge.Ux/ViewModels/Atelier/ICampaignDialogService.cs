using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Workspaces;

namespace RetroDownfall.TheForge.Ux.ViewModels.Atelier;

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

    Task<NewCampaignInputs?> PromptNewCampaignAsync(CancellationToken cancellationToken);

    Task<EditCampaignInputs?> PromptEditCampaignAsync(CampaignDto existing, CancellationToken cancellationToken);

    /// <summary>Returns <c>"merge"</c> or <c>"replace"</c>, or <c>null</c> when cancelled.</summary>
    Task<string?> PromptImportStrategyAsync(CancellationToken cancellationToken);

}
