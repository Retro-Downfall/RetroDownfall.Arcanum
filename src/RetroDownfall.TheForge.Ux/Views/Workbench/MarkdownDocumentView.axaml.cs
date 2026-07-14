using System.ComponentModel;
using Avalonia.Controls;
using RetroDownfall.TheForge.Ux.ViewModels.Workbench;

namespace RetroDownfall.TheForge.Ux.Views.Workbench;

public partial class MarkdownDocumentView : UserControl
{

    private MarkdownDocumentViewModel? _viewModel;

    private bool _isSynchronizing;

    private bool _syncingFromPreview;

    public MarkdownDocumentView()
    {

        InitializeComponent();

        DataContextChanged += OnDataContextChanged;

        SourceEditor.TextChanged += OnSourceEditorTextChanged;

        SourceEditor.TextArea.Caret.PositionChanged += OnCaretPositionChanged;

        Illumination.GoToSourceRequested += OnIlluminationGoToSource;

    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {

        if (_viewModel is not null)
        {

            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        }

        _viewModel = DataContext as MarkdownDocumentViewModel;

        if (_viewModel is not null)
        {

            _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        }

        SynchronizeEditorFromViewModel();

    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {

        if (e.PropertyName == nameof(MarkdownDocumentViewModel.MarkdownSource))
        {

            SynchronizeEditorFromViewModel();

        }

    }

    private void OnSourceEditorTextChanged(object? sender, EventArgs e)
    {

        if (_isSynchronizing || _viewModel is null)
        {

            return;

        }

        _viewModel.MarkdownSource = SourceEditor.Text;

    }

    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {

        if (_syncingFromPreview || _viewModel is null || !_viewModel.SyncScrollEnabled)
        {

            return;

        }

        int line = Math.Max(0, SourceEditor.TextArea.Caret.Line - 1);

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

            SourceEditor.TextArea.Caret.Line = avaloniaLine;

            SourceEditor.TextArea.Caret.Column = 1;

            SourceEditor.TextArea.Caret.BringCaretToView();

        }
        finally
        {

            _syncingFromPreview = false;

        }

    }

    private void SynchronizeEditorFromViewModel()
    {

        _isSynchronizing = true;

        try
        {

            SourceEditor.Text = _viewModel?.MarkdownSource ?? string.Empty;

        }
        finally
        {

            _isSynchronizing = false;

        }

    }

}
