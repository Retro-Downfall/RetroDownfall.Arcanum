using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.TheForge.Ux.Markdown;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.ViewModels;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;
using RetroDownfall.TheForge.Ux.ViewModels.WorkspaceExplorer;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class WorkspaceExplorerViewModelTests
{

    private static readonly WorkspaceInfo Workspace = new(
        "ws-1",
        "Campaign",
        "/tmp/campaign",
        WorkspaceType.Campaign,
        DateTimeOffset.UtcNow);

    private static readonly FileEntry File = new(
        "readme.md",
        "readme.md",
        "/tmp/campaign/readme.md",
        FileEntryType.File,
        42,
        DateTimeOffset.UtcNow);

    [Fact]
    public async Task RefreshWorkspaces_Populates()
    {

        FakeWorkspaceExplorerDataSource dataSource = new()
        {

            Workspaces = [Workspace],

        };

        WorkspaceExplorerViewModel viewModel = NewViewModel(dataSource);

        await viewModel.RefreshWorkspacesAsync(CancellationToken.None);

        Assert.Single(viewModel.Workspaces);

        Assert.Equal("ws-1", viewModel.Workspaces[0].Id);

    }

    [Fact]
    public async Task OpenFile_LoadsInfoAndContents()
    {

        FileReadResult read = new("readme.md", "# Hello", "utf-8", 42, DateTimeOffset.UtcNow);

        FakeWorkspaceExplorerDataSource dataSource = new()
        {

            FileInfoResult = new DataSourceResult<FileEntry>(File, true, null, null),

            FileContentsResult = new DataSourceResult<FileReadResult>(read, true, null, null),

        };

        WorkspaceExplorerViewModel viewModel = NewViewModel(dataSource);

        viewModel.SelectedWorkspace = Workspace;

        viewModel.SelectedEntry = File;

        await viewModel.OpenFileAsync(CancellationToken.None);

        Assert.Equal("readme.md", dataSource.LastFileInfoPath);

        Assert.Equal("readme.md", dataSource.LastFileContentsPath);

        Assert.Equal("# Hello", viewModel.FileContentsText);

        Assert.NotNull(viewModel.FileInfo);

    }

    [Fact]
    public async Task Index_FeatureDisabled()
    {

        FakeWorkspaceExplorerDataSource dataSource = new()
        {

            IndexResult = new DataSourceResult<bool>(
                false,
                false,
                ErrorCodes.Embeddings.FeatureDisabled,
                "disabled"),

        };

        WorkspaceExplorerViewModel viewModel = NewViewModel(dataSource);

        viewModel.SelectedWorkspace = Workspace;

        await viewModel.IndexWorkspaceAsync(CancellationToken.None);

        Assert.True(viewModel.IndexFeatureDisabled);

        Assert.Equal("Indexing disabled.", viewModel.StatusText);

    }

    [Fact]
    public async Task Save_Success()
    {

        FileWriteResult write = new("readme.md", 12, DateTimeOffset.UtcNow);

        FakeWorkspaceExplorerDataSource dataSource = new()
        {

            WriteResult = new DataSourceResult<FileWriteResult>(write, true, null, null),

        };

        WorkspaceExplorerViewModel viewModel = NewViewModel(dataSource);

        viewModel.SelectedWorkspace = Workspace;

        viewModel.SelectedEntry = File;

        viewModel.FileContentsText = "updated";

        await viewModel.SaveFileAsync(CancellationToken.None);

        Assert.Equal("readme.md", dataSource.LastWritePath);

        Assert.Equal("updated", dataSource.LastWriteContent);

        Assert.Equal("File saved.", viewModel.StatusText);

    }

    [Fact]
    public async Task Save_FileWriteDisabled_SetsIsWriteDisabled()
    {

        FakeWorkspaceExplorerDataSource dataSource = new()
        {

            WriteResult = new DataSourceResult<FileWriteResult>(
                null,
                false,
                ErrorCodes.Workspace.FileWriteDisabled,
                "writes off"),

        };

        WorkspaceExplorerViewModel viewModel = NewViewModel(dataSource);

        viewModel.SelectedWorkspace = Workspace;

        viewModel.SelectedEntry = File;

        viewModel.FileContentsText = "blocked";

        await viewModel.SaveFileAsync(CancellationToken.None);

        Assert.True(viewModel.IsWriteDisabled);

        Assert.Equal("writes off", viewModel.WriteDisabledMessage);

    }

    [Fact]
    public async Task Delete_RequiresConfirmation()
    {

        FakeWorkspaceExplorerDataSource dataSource = new();

        FakeConfirmationDialogService confirmation = new() { NextResult = false };

        WorkspaceExplorerViewModel viewModel = NewViewModel(dataSource, confirmation);

        viewModel.SelectedWorkspace = Workspace;

        viewModel.SelectedEntry = File;

        await viewModel.DeleteFileAsync(CancellationToken.None);

        Assert.Equal(0, dataSource.DeleteCallCount);

        Assert.Equal("Delete cancelled.", viewModel.StatusText);

        confirmation.NextResult = true;

        dataSource.DeleteResult = new DataSourceResult<FileDeleteResult>(
            new FileDeleteResult("readme.md", false, DateTimeOffset.UtcNow),
            true,
            null,
            null);

        await viewModel.DeleteFileAsync(CancellationToken.None);

        Assert.Equal(1, dataSource.DeleteCallCount);

        Assert.Equal("readme.md", dataSource.LastDeletePath);

    }

    private static WorkspaceExplorerViewModel NewViewModel(
        FakeWorkspaceExplorerDataSource dataSource,
        IConfirmationDialogService? confirmation = null) =>
        new(
            dataSource,
            confirmation ?? new FakeConfirmationDialogService(),
            new MarkdownDocumentContentStore(),
            new NavigationService(),
            new FoundryFloorViewModel(new NullLogService()));

    private sealed class FakeWorkspaceExplorerDataSource : IWorkspaceExplorerDataSource
    {

        public WorkspaceInfo[] Workspaces { get; init; } = [];

        public DataSourceResult<FileEntry> FileInfoResult { get; init; } =
            new(null, true, null, null);

        public DataSourceResult<FileReadResult> FileContentsResult { get; init; } =
            new(null, true, null, null);

        public DataSourceResult<bool> IndexResult { get; init; } =
            new(true, true, null, null);

        public DataSourceResult<FileWriteResult> WriteResult { get; init; } =
            new(null, true, null, null);

        public DataSourceResult<FileDeleteResult> DeleteResult { get; set; } =
            new(null, true, null, null);

        public int DeleteCallCount { get; private set; }

        public string? LastFileInfoPath { get; private set; }

        public string? LastFileContentsPath { get; private set; }

        public string? LastWritePath { get; private set; }

        public string? LastWriteContent { get; private set; }

        public string? LastDeletePath { get; private set; }

        public Task<DataSourceResult<WorkspaceInfo[]>> ListWorkspacesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<WorkspaceInfo[]>(Workspaces, true, null, null));

        public Task<DataSourceResult<FileListResult>> ListFilesAsync(string workspaceId, string? relativePath, bool? recursive, string? searchPattern, CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<FileListResult>(new FileListResult([], null), true, null, null));

        public Task<DataSourceResult<FileEntry>> GetFileInfoAsync(string workspaceId, string? relativePath, CancellationToken cancellationToken)
        {

            LastFileInfoPath = relativePath;

            return Task.FromResult(FileInfoResult);

        }

        public Task<DataSourceResult<FileReadResult>> GetFileContentsAsync(string workspaceId, string relativePath, CancellationToken cancellationToken)
        {

            LastFileContentsPath = relativePath;

            return Task.FromResult(FileContentsResult);

        }

        public Task<DataSourceResult<bool>> IndexWorkspaceAsync(string workspaceId, CancellationToken cancellationToken) =>
            Task.FromResult(IndexResult);

        public Task<DataSourceResult<WorkspaceSearchResult[]>> DivineWorkspaceFilesAsync(string workspaceId, WorkspaceSemanticSearchRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<WorkspaceSearchResult[]>([], true, null, null));

        public Task<DataSourceResult<FileWriteResult>> WriteFileContentsAsync(string workspaceId, string relativePath, string content, CancellationToken cancellationToken)
        {

            LastWritePath = relativePath;

            LastWriteContent = content;

            return Task.FromResult(WriteResult);

        }

        public Task<DataSourceResult<TextBlockReplaceResult>> ReplaceTextBlockAsync(string workspaceId, string relativePath, string oldString, string newString, int? expectedReplacements, CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<TextBlockReplaceResult>(null, true, null, null));

        public Task<DataSourceResult<FileDeleteResult>> DeleteFileAsync(string workspaceId, string relativePath, bool? recursive, CancellationToken cancellationToken)
        {

            DeleteCallCount++;

            LastDeletePath = relativePath;

            return Task.FromResult(DeleteResult);

        }

        public Task<DataSourceResult<DirectoryCreateResult>> CreateDirectoryAsync(string workspaceId, string relativePath, CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<DirectoryCreateResult>(null, true, null, null));

    }

    private sealed class FakeConfirmationDialogService : IConfirmationDialogService
    {

        public bool NextResult { get; set; }

        public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken) =>
            Task.FromResult(NextResult);

    }

}
