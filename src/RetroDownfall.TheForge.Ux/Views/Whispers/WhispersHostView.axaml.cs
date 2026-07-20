using Avalonia.Controls;
using Avalonia.Interactivity;
using RetroDownfall.TheForge.Ux.Services.Whispers;

namespace RetroDownfall.TheForge.Ux.Views.Whispers;

public partial class WhispersHostView : UserControl
{

    private IWhispersService? _whispersService;

    public WhispersHostView()
    {

        InitializeComponent();

        // Local DataContext blocks inheriting MainWindow's MainViewModel before Initialize.
        DataContext = null;

    }

    public void Initialize(IWhispersService whispersService)
    {

        _whispersService = whispersService;

        DataContext = whispersService;

    }

    private void OnDismissClick(object? sender, RoutedEventArgs e)
    {

        if (sender is Button { Tag: Guid id })
        {

            _whispersService?.Dismiss(id);

        }

    }

}
