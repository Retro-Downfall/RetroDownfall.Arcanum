namespace RetroDownfall.TheForge.Ux.Services;

/// <summary>
/// Minimal OK/Cancel confirmation seam for destructive actions (e.g. Workspace Explorer file delete
/// and recursive delete). ViewModels depend on this interface; tests fake it. The concrete Avalonia
/// implementation shows a <c>Window.ShowDialog</c> modal and returns <c>false</c> on cancel or when no
/// window is available. Pass <paramref name="confirmIsDefault"/> = <c>false</c> so Cancel is the
/// default button (used for unregister-only campaign delete).
/// </summary>
public interface IConfirmationDialogService
{

    Task<bool> ConfirmAsync(
        string title,
        string message,
        CancellationToken cancellationToken,
        bool confirmIsDefault = true);

}
