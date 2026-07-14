using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.TheForge.Ux.Markdown;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;

namespace RetroDownfall.TheForge.Ux.ViewModels.WorkspaceExplorer;

/// <summary>
/// Workspace Explorer — browse registered workspaces and files through Arcanum's workspace file APIs
/// (never local disk). Read paths (list/info/contents/index/divine) are primary; write/modify/delete are
/// optional and server-gated by <c>Arcanum:Workspaces:EnableFileWrite</c> (403
/// <c>Workspace.FileWriteDisabled</c>). Destructive deletes require confirmation.
/// </summary>
public sealed partial class WorkspaceExplorerViewModel : ViewModelBase
{

    private readonly IWorkspaceExplorerDataSource _dataSource;

    private readonly IConfirmationDialogService _confirmation;

    private readonly IMarkdownDocumentContentStore _markdownContentStore;

    private readonly INavigationService _navigation;

    private readonly FoundryFloorViewModel _foundryFloor;

    private bool _loaded;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isIndexing;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private string? _lastError;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private WorkspaceInfo? _selectedWorkspace;

    [ObservableProperty]
    private string? _currentRelativePath;

    [ObservableProperty]
    private FileEntry? _selectedEntry;

    [ObservableProperty]
    private FileEntry? _fileInfo;

    [ObservableProperty]
    private string _fileContentsText = string.Empty;

    [ObservableProperty]
    private bool _isWriteDisabled;

    [ObservableProperty]
    private string? _writeDisabledMessage;

    [ObservableProperty]
    private string _divinationQuery = string.Empty;

    [ObservableProperty]
    private bool _indexFeatureDisabled;

    [ObservableProperty]
    private string _newDirectoryName = string.Empty;

    public WorkspaceExplorerViewModel(
        IWorkspaceExplorerDataSource dataSource,
        IConfirmationDialogService confirmation,
        IMarkdownDocumentContentStore markdownContentStore,
        INavigationService navigation,
        FoundryFloorViewModel foundryFloor)
    {

        _dataSource = dataSource;

        _confirmation = confirmation;

        _markdownContentStore = markdownContentStore;

        _navigation = navigation;

        _foundryFloor = foundryFloor;

        Title = "Workspace Explorer";

    }

    public ObservableCollection<WorkspaceInfo> Workspaces { get; } = [];

    public ObservableCollection<FileEntry> Entries { get; } = [];

    public ObservableCollection<WorkspaceSearchResult> DivinationResults { get; } = [];

    public bool HasNoWorkspaces => Workspaces.Count == 0;

    public bool HasNoEntries => Entries.Count == 0;

    public bool CanGoUp => !string.IsNullOrEmpty(CurrentRelativePath);

    public bool IsFileSelected => SelectedEntry?.Type == FileEntryType.File;

    public bool CanOpenMarkdownPreview =>
        IsFileSelected
        && IsMarkdownFileName(SelectedEntry?.Name)
        && !string.IsNullOrEmpty(FileContentsText);

    public bool WritesEnabled => !IsWriteDisabled;

    public string EmptyWorkspacesState => "No workspaces registered.";

    public string EmptyEntriesState => "No files in this directory.";

    public string FeatureDisabledMessage =>
        "Workspace indexing / Divination is disabled — enable Arcanum:Embeddings:Enabled and Arcanum:Embeddings:CodebaseRetrievalEnabled server-side.";

    public string WriteDisabledBanner =>
        WriteDisabledMessage ?? "Workspace file write is disabled (Arcanum:Workspaces:EnableFileWrite).";

    partial void OnFileContentsTextChanged(string value) =>
        OnPropertyChanged(nameof(CanOpenMarkdownPreview));

    partial void OnIsVisibleChanged(bool value)
    {

        if (value && !_loaded)
        {

            _loaded = true;

            _ = RefreshWorkspacesAsync(CancellationToken.None);

        }

    }

    partial void OnSelectedWorkspaceChanged(WorkspaceInfo? value)
    {

        CurrentRelativePath = null;

        FileContentsText = string.Empty;

        FileInfo = null;

        SelectedEntry = null;

        DivinationResults.Clear();

        IsWriteDisabled = false;

        WriteDisabledMessage = null;

        if (value is not null)
        {

            _ = RefreshDirectoryAsync(CancellationToken.None);

        }

        else
        {

            Entries.Clear();

            OnPropertyChanged(nameof(HasNoEntries));

        }

    }

    partial void OnSelectedEntryChanged(FileEntry? value)
    {

        OnPropertyChanged(nameof(IsFileSelected));

        OnPropertyChanged(nameof(CanOpenMarkdownPreview));

        if (value is { Type: FileEntryType.File })
        {

            _ = OpenFileAsync(CancellationToken.None);

        }

        else
        {

            FileInfo = null;

            FileContentsText = string.Empty;

        }

    }

    partial void OnCurrentRelativePathChanged(string? value)
    {

        OnPropertyChanged(nameof(CanGoUp));

    }

    partial void OnIsWriteDisabledChanged(bool value)
    {

        OnPropertyChanged(nameof(WritesEnabled));

    }

    [RelayCommand]
    public async Task RefreshWorkspacesAsync(CancellationToken cancellationToken)
    {

        IsBusy = true;

        LastError = null;

        try
        {

            DataSourceResult<WorkspaceInfo[]> result = await _dataSource
                .ListWorkspacesAsync(cancellationToken)
                .ConfigureAwait(true);

            Workspaces.Clear();

            if (result.Success && result.Data is { } workspaces)
            {

                foreach (WorkspaceInfo workspace in workspaces)
                {

                    Workspaces.Add(workspace);

                }

                StatusText = Workspaces.Count == 0 ? "No workspaces registered." : $"{Workspaces.Count} workspaces.";

            }

            else
            {

                LastError = result.ErrorMessage ?? "Failed to load workspaces.";

                StatusText = "Workspaces unavailable.";

                _foundryFloor.AppendLine($"Workspace Explorer load failed: {LastError}");

            }

            OnPropertyChanged(nameof(HasNoWorkspaces));

        }

        catch (Exception ex)
        {

            LastError = ex.Message;

            _foundryFloor.AppendLine($"Workspace Explorer refresh error: {ex.Message}");

        }

        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    public async Task RefreshDirectoryAsync(CancellationToken cancellationToken)
    {

        if (SelectedWorkspace is not { } workspace)
        {

            StatusText = "Select a workspace.";

            return;

        }

        IsBusy = true;

        LastError = null;

        try
        {

            DataSourceResult<FileListResult> result = await _dataSource
                .ListFilesAsync(workspace.Id, CurrentRelativePath, recursive: false, searchPattern: null, cancellationToken)
                .ConfigureAwait(true);

            Entries.Clear();

            if (result.Success && result.Data is { } listing)
            {

                foreach (FileEntry entry in listing.Entries.OrderBy(static e => e.Type == FileEntryType.Directory ? 0 : 1).ThenBy(static e => e.Name, StringComparer.OrdinalIgnoreCase))
                {

                    Entries.Add(entry);

                }

                StatusText = string.IsNullOrEmpty(CurrentRelativePath)
                    ? $"{Entries.Count} entries (root)."
                    : $"{Entries.Count} entries in {CurrentRelativePath}.";

            }

            else
            {

                LastError = result.ErrorMessage ?? "Failed to list files.";

                StatusText = "Directory unavailable.";

                _foundryFloor.AppendLine($"Workspace Explorer list failed: {LastError}");

            }

            OnPropertyChanged(nameof(HasNoEntries));

        }

        catch (Exception ex)
        {

            LastError = ex.Message;

        }

        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    public async Task OpenDirectoryAsync(CancellationToken cancellationToken)
    {

        if (SelectedEntry is not { Type: FileEntryType.Directory } entry)
        {

            return;

        }

        CurrentRelativePath = entry.RelativePath;

        SelectedEntry = null;

        FileInfo = null;

        FileContentsText = string.Empty;

        await RefreshDirectoryAsync(cancellationToken).ConfigureAwait(true);

    }

    [RelayCommand]
    public async Task UpAsync(CancellationToken cancellationToken)
    {

        if (string.IsNullOrEmpty(CurrentRelativePath))
        {

            return;

        }

        string path = CurrentRelativePath.Replace('\\', '/').TrimEnd('/');

        int slash = path.LastIndexOf('/');

        CurrentRelativePath = slash <= 0 ? null : path[..slash];

        SelectedEntry = null;

        FileInfo = null;

        FileContentsText = string.Empty;

        await RefreshDirectoryAsync(cancellationToken).ConfigureAwait(true);

    }

    [RelayCommand]
    public async Task OpenFileAsync(CancellationToken cancellationToken)
    {

        if (SelectedWorkspace is not { } workspace || SelectedEntry is not { Type: FileEntryType.File } entry)
        {

            return;

        }

        IsBusy = true;

        LastError = null;

        try
        {

            DataSourceResult<FileEntry> infoResult = await _dataSource
                .GetFileInfoAsync(workspace.Id, entry.RelativePath, cancellationToken)
                .ConfigureAwait(true);

            if (infoResult.Success && infoResult.Data is not null)
            {

                FileInfo = infoResult.Data;

            }

            else
            {

                FileInfo = entry;

                if (!infoResult.Success)
                {

                    LastError = infoResult.ErrorMessage ?? "Failed to load file info.";

                }

            }

            DataSourceResult<FileReadResult> contentsResult = await _dataSource
                .GetFileContentsAsync(workspace.Id, entry.RelativePath, cancellationToken)
                .ConfigureAwait(true);

            if (contentsResult.Success && contentsResult.Data is { } read)
            {

                FileContentsText = read.Content;

                StatusText = $"Opened {entry.RelativePath} ({read.Size} bytes).";

            }

            else
            {

                FileContentsText = string.Empty;

                LastError = contentsResult.ErrorMessage ?? "Failed to read file contents.";

                StatusText = "File contents unavailable.";

                _foundryFloor.AppendLine($"Workspace Explorer read failed: {LastError}");

            }

        }

        catch (Exception ex)
        {

            LastError = ex.Message;

        }

        finally
        {

            IsBusy = false;

            OnPropertyChanged(nameof(CanOpenMarkdownPreview));

        }

    }

    [RelayCommand]
    private void OpenMarkdownPreview()
    {

        if (SelectedWorkspace is not { } workspace || SelectedEntry is not { Type: FileEntryType.File } entry)
        {

            StatusText = "Select a markdown file first.";

            return;

        }

        if (!IsMarkdownFileName(entry.Name))
        {

            StatusText = "Open Preview is only available for .md / .markdown files.";

            return;

        }

        if (string.IsNullOrEmpty(FileContentsText))
        {

            StatusText = "Open the file so contents load, then use Open Preview.";

            return;

        }

        string id = $"ws:{workspace.Id}:{entry.RelativePath}";

        string title = entry.Name;

        string? baseDirectory = string.IsNullOrWhiteSpace(entry.RelativePath)
            ? null
            : Path.GetDirectoryName(entry.RelativePath.Replace('\\', '/'))?.Replace('\\', '/');

        _markdownContentStore.Put(new MarkdownDocumentPayload(
            id,
            title,
            FileContentsText,
            workspace.Id,
            entry.RelativePath,
            string.IsNullOrWhiteSpace(baseDirectory) ? null : baseDirectory));

        _navigation.OpenDocument(DocumentKind.Markdown, id);

        StatusText = $"Opened The Illumination for {entry.RelativePath}.";

    }

    private static bool IsMarkdownFileName(string? name)
    {

        if (string.IsNullOrWhiteSpace(name))
        {

            return false;

        }

        return name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);

    }

    [RelayCommand]
    private void CopyPath()
    {

        if (SelectedEntry is { } entry)
        {

            StatusText = $"Path: {entry.RelativePath}";

        }

    }

    [RelayCommand]
    public async Task IndexWorkspaceAsync(CancellationToken cancellationToken)
    {

        if (SelectedWorkspace is not { } workspace)
        {

            StatusText = "Select a workspace.";

            return;

        }

        IsIndexing = true;

        LastError = null;

        IndexFeatureDisabled = false;

        try
        {

            DataSourceResult<bool> result = await _dataSource
                .IndexWorkspaceAsync(workspace.Id, cancellationToken)
                .ConfigureAwait(true);

            if (result.Success)
            {

                StatusText = "Workspace re-index requested.";

            }

            else if (result.ErrorCode == ErrorCodes.Embeddings.FeatureDisabled)
            {

                IndexFeatureDisabled = true;

                StatusText = "Indexing disabled.";

            }

            else
            {

                LastError = result.ErrorMessage ?? "Failed to index workspace.";

                StatusText = "Index failed.";

                _foundryFloor.AppendLine($"Workspace Explorer index failed: {LastError}");

            }

        }

        catch (Exception ex)
        {

            LastError = ex.Message;

        }

        finally
        {

            IsIndexing = false;

        }

    }

    [RelayCommand]
    public async Task DivineWorkspaceFilesAsync(CancellationToken cancellationToken)
    {

        if (SelectedWorkspace is not { } workspace)
        {

            StatusText = "Select a workspace.";

            return;

        }

        string query = DivinationQuery.Trim();

        if (string.IsNullOrEmpty(query))
        {

            StatusText = "Enter a Divination query.";

            return;

        }

        IsSearching = true;

        LastError = null;

        IndexFeatureDisabled = false;

        try
        {

            DataSourceResult<WorkspaceSearchResult[]> result = await _dataSource
                .DivineWorkspaceFilesAsync(workspace.Id, new WorkspaceSemanticSearchRequest(query), cancellationToken)
                .ConfigureAwait(true);

            DivinationResults.Clear();

            if (result.Success && result.Data is { } hits)
            {

                foreach (WorkspaceSearchResult hit in hits)
                {

                    DivinationResults.Add(hit);

                }

                StatusText = DivinationResults.Count == 0 ? "No Divination results." : $"{DivinationResults.Count} Divination results.";

            }

            else if (result.ErrorCode == ErrorCodes.Embeddings.FeatureDisabled)
            {

                IndexFeatureDisabled = true;

                StatusText = "Workspace Divination disabled.";

            }

            else
            {

                LastError = result.ErrorMessage ?? "Workspace Divination failed.";

                StatusText = "Divination failed.";

                _foundryFloor.AppendLine($"Workspace Explorer divination failed: {LastError}");

            }

        }

        catch (Exception ex)
        {

            LastError = ex.Message;

        }

        finally
        {

            IsSearching = false;

        }

    }

    [RelayCommand]
    public async Task SaveFileAsync(CancellationToken cancellationToken)
    {

        if (SelectedWorkspace is not { } workspace || SelectedEntry is not { Type: FileEntryType.File } entry)
        {

            StatusText = "Select a file to save.";

            return;

        }

        IsBusy = true;

        LastError = null;

        try
        {

            DataSourceResult<FileWriteResult> result = await _dataSource
                .WriteFileContentsAsync(workspace.Id, entry.RelativePath, FileContentsText, cancellationToken)
                .ConfigureAwait(true);

            if (result.Success)
            {

                StatusText = "File saved.";

            }

            else if (result.ErrorCode == ErrorCodes.Workspace.FileWriteDisabled)
            {

                ApplyWriteDisabled(result.ErrorMessage);

            }

            else
            {

                LastError = result.ErrorMessage ?? "Failed to save file.";

                StatusText = "Save failed.";

                _foundryFloor.AppendLine($"Workspace Explorer save failed: {LastError}");

            }

        }

        catch (Exception ex)
        {

            LastError = ex.Message;

        }

        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    public async Task DeleteFileAsync(CancellationToken cancellationToken)
    {

        if (SelectedWorkspace is not { } workspace || SelectedEntry is not { } entry)
        {

            StatusText = "Select a file or directory to delete.";

            return;

        }

        bool recursive = entry.Type == FileEntryType.Directory;

        string message = recursive
            ? $"Delete directory '{entry.RelativePath}' and all of its contents? This cannot be undone."
            : $"Delete file '{entry.RelativePath}'? This cannot be undone.";

        bool confirmed = await _confirmation
            .ConfirmAsync("Confirm delete", message, cancellationToken)
            .ConfigureAwait(true);

        if (!confirmed)
        {

            StatusText = "Delete cancelled.";

            return;

        }

        IsBusy = true;

        LastError = null;

        try
        {

            DataSourceResult<FileDeleteResult> result = await _dataSource
                .DeleteFileAsync(workspace.Id, entry.RelativePath, recursive, cancellationToken)
                .ConfigureAwait(true);

            if (result.Success)
            {

                StatusText = "Deleted.";

                SelectedEntry = null;

                FileInfo = null;

                FileContentsText = string.Empty;

                await RefreshDirectoryAsync(cancellationToken).ConfigureAwait(true);

            }

            else if (result.ErrorCode == ErrorCodes.Workspace.FileWriteDisabled)
            {

                ApplyWriteDisabled(result.ErrorMessage);

            }

            else
            {

                LastError = result.ErrorMessage ?? "Failed to delete.";

                StatusText = "Delete failed.";

                _foundryFloor.AppendLine($"Workspace Explorer delete failed: {LastError}");

            }

        }

        catch (Exception ex)
        {

            LastError = ex.Message;

        }

        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    public async Task CreateDirectoryAsync(CancellationToken cancellationToken)
    {

        if (SelectedWorkspace is not { } workspace)
        {

            StatusText = "Select a workspace.";

            return;

        }

        string name = NewDirectoryName.Trim().Trim('/');

        if (string.IsNullOrEmpty(name))
        {

            LastError = "Directory name is required.";

            return;

        }

        string relativePath = string.IsNullOrEmpty(CurrentRelativePath)
            ? name
            : $"{CurrentRelativePath.TrimEnd('/')}/{name}";

        IsBusy = true;

        LastError = null;

        try
        {

            DataSourceResult<DirectoryCreateResult> result = await _dataSource
                .CreateDirectoryAsync(workspace.Id, relativePath, cancellationToken)
                .ConfigureAwait(true);

            if (result.Success)
            {

                StatusText = $"Created {relativePath}.";

                NewDirectoryName = string.Empty;

                await RefreshDirectoryAsync(cancellationToken).ConfigureAwait(true);

            }

            else if (result.ErrorCode == ErrorCodes.Workspace.FileWriteDisabled)
            {

                ApplyWriteDisabled(result.ErrorMessage);

            }

            else
            {

                LastError = result.ErrorMessage ?? "Failed to create directory.";

                StatusText = "Create directory failed.";

                _foundryFloor.AppendLine($"Workspace Explorer mkdir failed: {LastError}");

            }

        }

        catch (Exception ex)
        {

            LastError = ex.Message;

        }

        finally
        {

            IsBusy = false;

        }

    }

    private void ApplyWriteDisabled(string? message)
    {

        IsWriteDisabled = true;

        WriteDisabledMessage = message ?? "Workspace file write is disabled. Set Arcanum:Workspaces:EnableFileWrite to true.";

        StatusText = "Writes disabled.";

        _foundryFloor.AppendLine($"Workspace Explorer: {WriteDisabledMessage}");

    }

}
