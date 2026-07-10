using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using RetroDownfall.TheForge.Ux.ViewModels.Hearth;

namespace RetroDownfall.TheForge.Ux.Views.Hearth;

public partial class HearthView : UserControl
{

    private HearthViewModel? _boundViewModel;

    public HearthView()
    {

        InitializeComponent();

        DataContextChanged += OnDataContextChanged;

        CommandInput.KeyDown += OnCommandInputKeyDown;

    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {

        if (_boundViewModel is not null)
        {

            _boundViewModel.PropertyChanged -= OnViewModelPropertyChanged;

            _boundViewModel.Lines.CollectionChanged -= OnLinesCollectionChanged;

        }

        _boundViewModel = DataContext as HearthViewModel;

        if (_boundViewModel is not null)
        {

            _boundViewModel.PropertyChanged += OnViewModelPropertyChanged;

            _boundViewModel.Lines.CollectionChanged += OnLinesCollectionChanged;

        }

    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {

        if (e.PropertyName == nameof(HearthViewModel.IsRunning)
            && _boundViewModel is { IsRunning: false })
        {

            Dispatcher.UIThread.Post(() => CommandInput.Focus(), DispatcherPriority.Background);

        }

    }

    private void OnLinesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {

        if (_boundViewModel is null || _boundViewModel.Lines.Count == 0)
        {

            return;

        }

        Dispatcher.UIThread.Post(
            () => OutputList.ScrollIntoView(_boundViewModel.Lines[^1]),
            DispatcherPriority.Background);

    }

    private void OnCommandInputKeyDown(object? sender, KeyEventArgs e)
    {

        if (_boundViewModel is null)
        {

            return;

        }

        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {

            if (_boundViewModel.RunCommand.CanExecute(null))
            {

                _boundViewModel.RunCommand.Execute(null);

            }

            e.Handled = true;

            return;

        }

        if (e.Key == Key.Escape)
        {

            _boundViewModel.CommandText = string.Empty;

            e.Handled = true;

        }

    }

}
