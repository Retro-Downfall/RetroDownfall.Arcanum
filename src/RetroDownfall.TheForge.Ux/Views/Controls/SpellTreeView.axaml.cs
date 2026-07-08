using Avalonia.Controls;
using Avalonia.Input;
using RetroDownfall.TheForge.Ux.ViewModels.Atelier;

namespace RetroDownfall.TheForge.Ux.Views.Controls;

public partial class SpellTreeView : UserControl
{

    public SpellTreeView()
    {

        InitializeComponent();

    }

    private void OnNodeDoubleTapped(object? sender, TappedEventArgs e)
    {

        if (sender is Control { DataContext: AtelierNodeViewModel { PrimaryCommand: { } command } }
            && command.CanExecute(null))
        {

            command.Execute(null);

            e.Handled = true;

        }

    }

}
