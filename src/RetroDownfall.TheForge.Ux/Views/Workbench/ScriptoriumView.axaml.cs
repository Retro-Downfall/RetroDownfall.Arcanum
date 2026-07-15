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

        ParameterSchemaEditor.TextChanged += OnParameterSchemaEditorTextChanged;

        DefaultParametersEditor.TextChanged += OnDefaultParametersEditorTextChanged;

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

        SynchronizeEditorsFromViewModel();

    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {

        if (e.PropertyName is nameof(ScriptoriumViewModel.Template)
            or nameof(ScriptoriumViewModel.ParameterSchemaJson)
            or nameof(ScriptoriumViewModel.DefaultParametersJson))
        {

            SynchronizeEditorsFromViewModel();

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

    private void OnParameterSchemaEditorTextChanged(object? sender, EventArgs e)
    {

        if (_isSynchronizing || _viewModel is null)
        {

            return;

        }

        _viewModel.ParameterSchemaJson = ParameterSchemaEditor.Text;

    }

    private void OnDefaultParametersEditorTextChanged(object? sender, EventArgs e)
    {

        if (_isSynchronizing || _viewModel is null)
        {

            return;

        }

        _viewModel.DefaultParametersJson = DefaultParametersEditor.Text;

    }

    private void SynchronizeEditorsFromViewModel()
    {

        _isSynchronizing = true;

        try
        {

            TemplateEditor.Text = _viewModel?.Template ?? string.Empty;

            ParameterSchemaEditor.Text = _viewModel?.ParameterSchemaJson ?? string.Empty;

            DefaultParametersEditor.Text = _viewModel?.DefaultParametersJson ?? string.Empty;

        }
        finally
        {

            _isSynchronizing = false;

        }

    }

}
