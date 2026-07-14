using System.ComponentModel;
using Avalonia.Controls;
using RetroDownfall.TheForge.Ux.ViewModels.Workbench;

namespace RetroDownfall.TheForge.Ux.Views.Workbench;

public partial class ScriptoriumView : UserControl
{

    private ScriptoriumViewModel? _viewModel;

    private bool _isSynchronizing;

    public ScriptoriumView()
    {

        InitializeComponent();

        DataContextChanged += OnDataContextChanged;

        TemplateEditor.TextChanged += OnTemplateEditorTextChanged;

    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {

        if (_viewModel is not null)
        {

            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        }

        _viewModel = DataContext as ScriptoriumViewModel;

        if (_viewModel is not null)
        {

            _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        }

        SynchronizeTemplateFromViewModel();

    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {

        if (e.PropertyName == nameof(ScriptoriumViewModel.Template))
        {

            SynchronizeTemplateFromViewModel();

        }

    }

    private void OnTemplateEditorTextChanged(object? sender, EventArgs e)
    {

        if (_isSynchronizing || _viewModel is null)
        {

            return;

        }

        _viewModel.Template = TemplateEditor.Text;

    }

    private void SynchronizeTemplateFromViewModel()
    {

        _isSynchronizing = true;

        try
        {

            TemplateEditor.Text = _viewModel?.Template ?? string.Empty;

        }
        finally
        {

            _isSynchronizing = false;

        }

    }

}
