using Avalonia.Controls;
using RetroDownfall.TheForge.Ux.Services.Whispers;
using RetroDownfall.TheForge.Ux.Views.Whispers;

namespace RetroDownfall.TheForge.Ux.Views;

public partial class MainWindow : Window
{

    public MainWindow()
    {

        InitializeComponent();

    }

    public void Initialize(IWhispersService whispersService)
    {

        WhispersHost.Initialize(whispersService);

    }

}
