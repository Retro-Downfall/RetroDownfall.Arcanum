using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.TheForge.Ux.ViewModels.WarTable;

namespace RetroDownfall.TheForge.Ux.Views.WarTable;

public partial class WarTableView : UserControl
{

    public WarTableView()
    {

        InitializeComponent();

    }

    private async void OnApprenticeDoubleTapped(object? sender, TappedEventArgs e)
    {

        if (DataContext is not WarTableViewModel viewModel)
        {

            return;

        }

        ApprenticeSummaryDto? summary = ResolveTappedSummary(e.Source as Visual);

        if (summary is null)
        {

            return;

        }

        if (viewModel.SelectApprenticeCommand.CanExecute(summary))
        {

            await viewModel.SelectApprenticeCommand.ExecuteAsync(summary);

            e.Handled = true;

        }

    }

    private static ApprenticeSummaryDto? ResolveTappedSummary(Visual? source)
    {

        for (Visual? current = source; current is not null; current = current.GetVisualParent())
        {

            if (current is Control { DataContext: ApprenticeSummaryDto summary })
            {

                return summary;

            }

        }

        return null;

    }

}
