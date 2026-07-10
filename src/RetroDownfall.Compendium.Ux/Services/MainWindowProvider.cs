using Avalonia.Controls;

namespace RetroDownfall.Compendium.Ux.Services;

public sealed class MainWindowProvider : IMainWindowProvider
{

    private Window? _mainWindow;

    public void SetMainWindow(Window window)
    {

        _mainWindow = window;

    }

    public Window? GetMainWindow() => _mainWindow;

}
