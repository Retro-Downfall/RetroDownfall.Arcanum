namespace RetroDownfall.TheForge.Ux.Services;

/// <summary>
/// Minimal OK/Cancel confirmation seam for destructive actions (e.g. Workspace Explorer file delete
/// and recursive delete). ViewModels depend on this interface; tests fake it. The concrete Avalonia
/// implementation shows a <c>Window.ShowDialog</c> modal and returns <c>false</c> on cancel or when no
/// window is available. No new toast/Whispers framework.
/// </summary>
public interface IConfirmationDialogService
{

    Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken);

}
