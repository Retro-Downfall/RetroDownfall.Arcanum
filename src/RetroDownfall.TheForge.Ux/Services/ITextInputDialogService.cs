namespace RetroDownfall.TheForge.Ux.Services;

/// <summary>
/// Single-line text prompt for clone name/version. Returns <see langword="null"/> on cancel (no-op).
/// </summary>
public interface ITextInputDialogService
{

    Task<string?> PromptAsync(string title, string label, string? defaultValue, CancellationToken cancellationToken);

}
