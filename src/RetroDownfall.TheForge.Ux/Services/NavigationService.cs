using RetroDownfall.TheForge.Ux.Models;

namespace RetroDownfall.TheForge.Ux.Services;

/// <summary>View routing + Workbench tab management. Deliberately event-based rather than
/// referencing <c>MainViewModel</c> directly, to avoid a circular DI dependency — the shell
/// subscribes to these events to open/close/activate Workbench tabs and focus panels.
/// Optional <paramref name="workspace"/> is the API workspace path (trimmed); identity
/// normalization happens when building <c>DocumentKey</c>.
/// </summary>
public interface INavigationService
{

    event Action<DocumentKind, string, string?>? DocumentOpenRequested;

    event Action<DocumentKind, string, string?>? DocumentCloseRequested;

    event Action<PanelKind>? PanelFocusRequested;

    /// <summary>
    /// Opens or focuses the singleton Proving Grounds tab. When <paramref name="prefill"/> is
    /// provided, the shell applies it unless the draft is dirty (returns <see langword="false"/>).
    /// </summary>
    event Func<ProvingGroundsPrefill?, bool>? ProvingGroundsOpenRequested;

    /// <summary>Opens or focuses the singleton Comparison Workbench tab.</summary>
    event Action? ComparisonWorkbenchOpenRequested;

    void OpenDocument(DocumentKind kind, string id, string? workspace = null);

    void CloseDocument(DocumentKind kind, string id, string? workspace = null);

    void FocusPanel(PanelKind panel);

    /// <summary>Opens or focuses Proving Grounds; optionally prefills when the draft is clean.</summary>
    bool OpenOrFocusProvingGrounds(ProvingGroundsPrefill? prefill = null);

    /// <summary>Opens or focuses Comparison Workbench.</summary>
    void OpenOrFocusComparisonWorkbench();

    /// <summary>
    /// Focuses Workspace Explorer and selects the workspace with the given id after ensuring the list is loaded.
    /// </summary>
    event Func<string, CancellationToken, Task>? WorkspaceOpenRequested;

    Task OpenWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default);

    event Func<Guid, CancellationToken, Task<bool>>? CampaignFocusRequested;

    event Func<Guid, CancellationToken, Task<bool>>? ApprenticeFocusRequested;

    Task<bool> FocusCampaignAsync(Guid campaignId, CancellationToken cancellationToken = default);

    Task<bool> FocusApprenticeAsync(Guid apprenticeId, CancellationToken cancellationToken = default);

}

public sealed class NavigationService : INavigationService
{

    public event Action<DocumentKind, string, string?>? DocumentOpenRequested;

    public event Action<DocumentKind, string, string?>? DocumentCloseRequested;

    public event Action<PanelKind>? PanelFocusRequested;

    public event Func<ProvingGroundsPrefill?, bool>? ProvingGroundsOpenRequested;

    public event Action? ComparisonWorkbenchOpenRequested;

    public event Func<string, CancellationToken, Task>? WorkspaceOpenRequested;

    public event Func<Guid, CancellationToken, Task<bool>>? CampaignFocusRequested;

    public event Func<Guid, CancellationToken, Task<bool>>? ApprenticeFocusRequested;

    public void OpenDocument(DocumentKind kind, string id, string? workspace = null) =>
        DocumentOpenRequested?.Invoke(kind, id, WorkspacePathHelper.ForApi(workspace));

    public void CloseDocument(DocumentKind kind, string id, string? workspace = null) =>
        DocumentCloseRequested?.Invoke(kind, id, WorkspacePathHelper.ForApi(workspace));

    public void FocusPanel(PanelKind panel) => PanelFocusRequested?.Invoke(panel);

    public bool OpenOrFocusProvingGrounds(ProvingGroundsPrefill? prefill = null) =>
        ProvingGroundsOpenRequested?.Invoke(prefill) ?? false;

    public void OpenOrFocusComparisonWorkbench() => ComparisonWorkbenchOpenRequested?.Invoke();

    public Task OpenWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default)
    {

        Func<string, CancellationToken, Task>? handler = WorkspaceOpenRequested;

        if (handler is null)
        {

            return Task.CompletedTask;

        }

        return handler(workspaceId, cancellationToken);

    }

    public Task<bool> FocusCampaignAsync(
        Guid campaignId,
        CancellationToken cancellationToken = default)
    {

        Func<Guid, CancellationToken, Task<bool>>? handler = CampaignFocusRequested;

        return handler is null
            ? Task.FromResult(false)
            : handler(campaignId, cancellationToken);

    }

    public Task<bool> FocusApprenticeAsync(
        Guid apprenticeId,
        CancellationToken cancellationToken = default)
    {

        Func<Guid, CancellationToken, Task<bool>>? handler = ApprenticeFocusRequested;

        return handler is null
            ? Task.FromResult(false)
            : handler(apprenticeId, cancellationToken);

    }

}
