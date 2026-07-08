using System.ComponentModel;
using Avalonia.Controls;
using RetroDownfall.TheForge.Ux.ViewModels.Workbench;

namespace RetroDownfall.TheForge.Ux.Views.Workbench;

public partial class SpellEditorView : UserControl
{

    private SpellEditorViewModel? _viewModel;

    private bool _isSynchronizing;

    public SpellEditorView()
    {

        InitializeComponent();

        DataContextChanged += OnDataContextChanged;

        BodyEditor.TextChanged += OnBodyEditorTextChanged;

        SkillJsonEditor.TextChanged += OnSkillJsonEditorTextChanged;

    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {

        if (_viewModel is not null)
        {

            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        }

        _viewModel = DataContext as SpellEditorViewModel;

        if (_viewModel is not null)
        {

            _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        }

        SynchronizeEditorsFromViewModel();

    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {

        if (e.PropertyName is nameof(SpellEditorViewModel.MarkdownBody) or nameof(SpellEditorViewModel.SkillJson))
        {

            SynchronizeEditorsFromViewModel();

        }

    }

    private void OnBodyEditorTextChanged(object? sender, EventArgs e)
    {

        if (_isSynchronizing || _viewModel is null)
        {

            return;

        }

        _viewModel.MarkdownBody = BodyEditor.Text;

    }

    private void OnSkillJsonEditorTextChanged(object? sender, EventArgs e)
    {

        if (_isSynchronizing || _viewModel is null)
        {

            return;

        }

        _viewModel.SkillJson = SkillJsonEditor.Text;

    }

    private void SynchronizeEditorsFromViewModel()
    {

        _isSynchronizing = true;

        try
        {

            BodyEditor.Text = _viewModel?.MarkdownBody ?? string.Empty;

            SkillJsonEditor.Text = _viewModel?.SkillJson ?? string.Empty;

        }
        finally
        {

            _isSynchronizing = false;

        }

    }

}
