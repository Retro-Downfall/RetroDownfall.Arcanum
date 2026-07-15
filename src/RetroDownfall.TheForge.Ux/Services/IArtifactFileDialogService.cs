namespace RetroDownfall.TheForge.Ux.Services;

/// <summary>
/// Save/Open file pickers for spell/prompt import-export. Returns <see langword="null"/> when the
/// operator cancels — callers treat cancel as a silent no-op, not an error.
/// </summary>
public interface IArtifactFileDialogService
{

    Task<string?> PickSaveJsonPathAsync(string suggestedFileName, CancellationToken cancellationToken);

    Task<string?> PickOpenJsonPathAsync(CancellationToken cancellationToken);

}
