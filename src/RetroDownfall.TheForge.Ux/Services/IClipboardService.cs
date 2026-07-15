namespace RetroDownfall.TheForge.Ux.Services;

/// <summary>Copies text to the OS clipboard. Tests fake this interface.</summary>
public interface IClipboardService
{

    Task SetTextAsync(string text, CancellationToken cancellationToken = default);

}
