using RetroDownfall.TheForge.Ux.ViewModels.Atelier;

namespace RetroDownfall.TheForge.Ux.Services;

/// <summary>
/// Modal prompt seams for Atelier New Spell / New Prompt / New Session commands. ViewModels depend
/// on this interface; tests fake it. The concrete Avalonia implementation builds Whispers-style
/// <c>Window.ShowDialog</c> modals and returns <c>null</c> on cancel.
/// </summary>
public interface IArtifactCreationDialogService
{

    Task<NewSpellInputs?> PromptNewSpellAsync(
        IReadOnlyList<WorkspaceOption> workspaces,
        WorkspaceOption? preselected,
        CancellationToken cancellationToken);

    Task<NewPromptInputs?> PromptNewPromptAsync(Guid? campaignId, string? campaignName, CancellationToken cancellationToken);

    Task<NewSessionInputs?> PromptNewSessionAsync(Guid? campaignId, string? campaignName, CancellationToken cancellationToken);

}
