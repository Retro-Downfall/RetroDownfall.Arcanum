using RetroDownfall.TheForge.Ux.Services;

namespace RetroDownfall.TheForge.Tests;

/// <summary>
/// Answers every confirmation with a scripted result and records what was asked, so a test can pin
/// both "the operator was asked" and "declining actually cancelled the destructive call".
/// </summary>
internal sealed class ScriptedConfirmationDialogService(bool confirm) : IConfirmationDialogService
{

    public List<(string Title, string Message)> Prompts { get; } = [];

    public Task<bool> ConfirmAsync(
        string title,
        string message,
        CancellationToken cancellationToken,
        bool confirmIsDefault = true)
    {

        Prompts.Add((title, message));

        return Task.FromResult(confirm);

    }

}
