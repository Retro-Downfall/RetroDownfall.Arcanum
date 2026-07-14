using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.TheForge.Ux.Markdown;
using RetroDownfall.TheForge.Ux.Models;

namespace RetroDownfall.TheForge.Ux.ViewModels.Workbench;

/// <summary>
/// Standalone Workbench document for The Illumination — preview-first markdown tab opened from
/// Workspace Explorer (or elsewhere) with content supplied at construction.
/// </summary>
public sealed partial class MarkdownDocumentViewModel : ViewModelBase, IDisposable
{

    private readonly IMarkdownDocumentContentStore? _contentStore;

    private readonly string _documentId;

    private bool _disposed;

    [ObservableProperty]
    private string _markdownSource = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSourceVisible))]
    [NotifyPropertyChangedFor(nameof(IsPreviewVisible))]
    [NotifyPropertyChangedFor(nameof(IsSplitterVisible))]
    private MarkdownViewMode _viewMode = MarkdownViewMode.Preview;

    [ObservableProperty]
    private bool _loadRemoteImages;

    [ObservableProperty]
    private bool _syncScrollEnabled = true;

    [ObservableProperty]
    private string? _workspaceId;

    [ObservableProperty]
    private string? _relativePath;

    [ObservableProperty]
    private string? _baseRelativeDirectory;

    public MarkdownDocumentViewModel(
        string documentId,
        string title,
        string content,
        IMarkdownDocumentContentStore? contentStore = null,
        string? workspaceId = null,
        string? relativePath = null,
        string? baseRelativeDirectory = null)
    {

        _documentId = documentId;

        _contentStore = contentStore;

        Title = string.IsNullOrWhiteSpace(title) ? "Markdown" : title;

        MarkdownSource = content ?? string.Empty;

        WorkspaceId = workspaceId;

        RelativePath = relativePath;

        BaseRelativeDirectory = baseRelativeDirectory;

    }

    public override DocumentKind? Kind => DocumentKind.Markdown;

    public string DocumentId => _documentId;

    public bool IsSourceVisible => MarkdownViewModeHelper.IsSourceVisible(ViewMode);

    public bool IsPreviewVisible => MarkdownViewModeHelper.IsPreviewVisible(ViewMode);

    public bool IsSplitterVisible => MarkdownViewModeHelper.IsSplitterVisible(ViewMode);

    [RelayCommand]
    private void SetViewMode(MarkdownViewMode mode) => ViewMode = mode;

    public void Dispose()
    {

        if (_disposed)
        {

            return;

        }

        _disposed = true;

        _contentStore?.Remove(_documentId);

    }

}
