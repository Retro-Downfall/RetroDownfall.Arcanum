namespace RetroDownfall.TheForge.Ux.Services;

/// <summary>
/// UI prompt for pasting a master API key when the OS credential store has none.
/// </summary>
public interface IApiKeyPrompt
{

    /// <summary>
    /// Shows a modal paste dialog. Returns the trimmed key, or <see langword="null"/> if cancelled.
    /// </summary>
    Task<string?> PromptForApiKeyAsync(CancellationToken cancellationToken);

}
