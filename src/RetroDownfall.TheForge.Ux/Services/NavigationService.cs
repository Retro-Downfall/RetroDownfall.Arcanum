using RetroDownfall.TheForge.Ux.Models;

namespace RetroDownfall.TheForge.Ux.Services;

/// <summary>View routing + Workbench tab management. Deliberately event-based rather than
/// referencing <c>MainViewModel</c> directly, to avoid a circular DI dependency — the shell
/// (Phase 3) subscribes to these events to open/close/activate Workbench tabs and focus panels.
/// </summary>
public interface INavigationService
{

    event Action<DocumentKind, string>? DocumentOpenRequested;

    event Action<DocumentKind, string>? DocumentCloseRequested;

    event Action<PanelKind>? PanelFocusRequested;

    void OpenDocument(DocumentKind kind, string id);

    void CloseDocument(DocumentKind kind, string id);

    void FocusPanel(PanelKind panel);

}

public sealed class NavigationService : INavigationService
{

    public event Action<DocumentKind, string>? DocumentOpenRequested;

    public event Action<DocumentKind, string>? DocumentCloseRequested;

    public event Action<PanelKind>? PanelFocusRequested;

    public void OpenDocument(DocumentKind kind, string id) => DocumentOpenRequested?.Invoke(kind, id);

    public void CloseDocument(DocumentKind kind, string id) => DocumentCloseRequested?.Invoke(kind, id);

    public void FocusPanel(PanelKind panel) => PanelFocusRequested?.Invoke(panel);

}
