using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.TheForge.Ux.ViewModels.Atelier;

/// <summary>
/// Testable creation seam for Atelier New Spell / New Prompt / New Session commands. The node/VM
/// builds the <c>Create*Request</c> from dialog inputs; implementations forward it to the route
/// services and map <c>ApiResponse</c> failures to error strings (no throwing).
/// </summary>
public interface IArtifactCreationDataSource
{

    Task<(bool Success, string? Error)> CreateSpellAsync(string workspacePath, CreateSpellRequest request, CancellationToken cancellationToken);

    Task<(PromptDetailDto? Prompt, string? Error)> CreatePromptAsync(CreatePromptRequest request, CancellationToken cancellationToken);

    Task<(SessionDetailDto? Session, string? Error)> CreateSessionAsync(CreateSessionRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkspaceOption>> ListWorkspaceOptionsAsync(CancellationToken cancellationToken);

}
