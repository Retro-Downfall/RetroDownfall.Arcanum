using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using RetroDownfall.TheForge.Ux.ViewModels.Workbench;

namespace RetroDownfall.TheForge.Ux.Views.Workbench;

public partial class TomeView : UserControl
{

    public TomeView()
    {

        InitializeComponent();

    }

    private async void OnInputKeyDown(object? sender, KeyEventArgs e)
    {

        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {

            return;

        }

        e.Handled = true;

        if (DataContext is not TomeViewModel viewModel)
        {

            return;

        }

        if (viewModel.SendCommand.CanExecute(null))
        {

            await viewModel.SendCommand.ExecuteAsync(null);

        }

    }

}
