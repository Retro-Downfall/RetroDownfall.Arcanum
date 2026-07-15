using System.ComponentModel;
using Avalonia.Controls;
using AvaloniaEdit;
using RetroDownfall.TheForge.Ux.ViewModels.Workbench;

namespace RetroDownfall.TheForge.Ux.Views.Workbench;

public partial class SpellEditorView : UserControl
{

    private SpellEditorViewModel? _viewModel;

    private bool _isSynchronizing;

    private bool _syncingFromPreview;

    public SpellEditorView()
    {

        InitializeComponent();

        DataContextChanged += OnDataContextChanged;

        BodyEditor.TextChanged += OnBodyEditorTextChanged;

        BodyEditor.TextArea.Caret.PositionChanged += OnCaretPositionChanged;

        SpellJsonEditor.TextChanged += OnSpellJsonEditorTextChanged;

        Illumination.GoToSourceRequested += OnIlluminationGoToSource;

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

        if (e.PropertyName is nameof(SpellEditorViewModel.MarkdownBody)
            or nameof(SpellEditorViewModel.SpellJson)
            or nameof(SpellEditorViewModel.CanEditMetadata))
        {

            SynchronizeEditorsFromViewModel();

        }

        if (e.PropertyName is nameof(SpellEditorViewModel.SelectedVersion) && _viewModel is not null)
        {

            _ = _viewModel.RefreshMirrorDiffAsync(CancellationToken.None);

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

    private void OnSpellJsonEditorTextChanged(object? sender, EventArgs e)
    {

        if (_isSynchronizing || _viewModel is null)
        {

            return;

        }

        _viewModel.SpellJson = SpellJsonEditor.Text;

    }

    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {

        if (_syncingFromPreview || _viewModel is null || !_viewModel.SyncScrollEnabled)
        {

            return;

        }

        int line = Math.Max(0, BodyEditor.TextArea.Caret.Line - 1);

        Illumination.NavigateToSourceLine = line;

    }

    private void OnIlluminationGoToSource(object? sender, int sourceLine)
    {

        if (_viewModel is null || !_viewModel.SyncScrollEnabled)
        {

            return;

        }

        _syncingFromPreview = true;

        try
        {

            int avaloniaLine = Math.Max(1, sourceLine + 1);

            BodyEditor.TextArea.Caret.Line = avaloniaLine;

            BodyEditor.TextArea.Caret.Column = 1;

            BodyEditor.TextArea.Caret.BringCaretToView();

        }
        finally
        {

            _syncingFromPreview = false;

        }

    }

    private void SynchronizeEditorsFromViewModel()
    {

        _isSynchronizing = true;

        try
        {

            BodyEditor.Text = _viewModel?.MarkdownBody ?? string.Empty;

            SpellJsonEditor.Text = _viewModel?.SpellJson ?? string.Empty;

            SpellJsonEditor.IsReadOnly = _viewModel is null || !_viewModel.CanEditMetadata;

        }
        finally
        {

            _isSynchronizing = false;

        }

    }

}
