namespace RetroDownfall.TheForge.Ux.Services;

/// <summary>
/// Save/Open file pickers for spell/prompt import-export. Returns <see langword="null"/> when the
/// operator cancels — callers treat cancel as a silent no-op, not an error.
/// </summary>
public interface IArtifactFileDialogService
{

    Task<string?> PickSaveJsonPathAsync(string suggestedFileName, CancellationToken cancellationToken);

    Task<string?> PickOpenJsonPathAsync(CancellationToken cancellationToken);

    /// <summary>Save picker for CSV exports (Audit Browser). Cancel → <see langword="null"/>.</summary>
    Task<string?> PickSaveCsvPathAsync(string suggestedFileName, CancellationToken cancellationToken);

    /// <summary>Open picker for any file (Files &amp; Batches upload). Cancel → <see langword="null"/>.</summary>
    Task<string?> PickOpenAnyPathAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Save picker for arbitrary downloads (Files &amp; Batches content / JSONL export).
    /// Cancel → <see langword="null"/>.
    /// </summary>
    Task<string?> PickSaveAnyPathAsync(string suggestedFileName, string? defaultExtension, CancellationToken cancellationToken);

}
