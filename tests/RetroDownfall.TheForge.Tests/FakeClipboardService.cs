using RetroDownfall.TheForge.Ux.Services;

namespace RetroDownfall.TheForge.Tests;

internal sealed class FakeClipboardService : IClipboardService
{

    public string? LastText { get; private set; }

    public Task SetTextAsync(string text, CancellationToken cancellationToken = default)
    {

        LastText = text;

        return Task.CompletedTask;

    }

}
