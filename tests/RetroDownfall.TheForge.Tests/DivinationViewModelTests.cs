using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.ViewModels;
using RetroDownfall.TheForge.Ux.ViewModels.Divination;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;
using RetroDownfall.TheForge.Ux.ViewModels.WorkspaceExplorer;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class DivinationViewModelTests
{

    private static readonly Guid SessionId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public async Task SearchSessions_PopulatesResults()
    {

        SemanticSessionSearchResult hit = new(
            SessionId,
            "Forge chat",
            Guid.NewGuid(),
            "user",
            "hello there",
            0.88f,
            DateTimeOffset.UtcNow);

        SemanticSearchResult search = new([hit], false, null);

        FakeDivinationDataSource dataSource = new()
        {

            SessionsResult = new DataSourceResult<SemanticSearchResult>(search, true, null, null),

        };

        DivinationViewModel viewModel = NewViewModel(dataSource);

        viewModel.SessionQuery = "hello";

        await viewModel.SearchSessionsAsync(CancellationToken.None);

        Assert.Equal("hello", dataSource.LastSessionQuery);

        Assert.Single(viewModel.SessionResults);

        Assert.Equal(SessionId, viewModel.SessionResults[0].SessionId);

    }

    [Fact]
    public async Task SearchSessions_FeatureDisabled_SetsFlag()
    {

        FakeDivinationDataSource dataSource = new()
        {

            SessionsResult = new DataSourceResult<SemanticSearchResult>(
                null,
                false,
                ErrorCodes.Embeddings.FeatureDisabled,
                "disabled"),

        };

        DivinationViewModel viewModel = NewViewModel(dataSource);

        viewModel.SessionQuery = "query";

        await viewModel.SearchSessionsAsync(CancellationToken.None);

        Assert.True(viewModel.SessionsFeatureDisabled);

        Assert.Equal("Session Divination disabled.", viewModel.StatusText);

    }

    [Fact]
    public void OpenSessionResult_OpensDocument()
    {

        SemanticSessionSearchResult hit = new(
            SessionId,
            "Forge chat",
            Guid.NewGuid(),
            "user",
            "preview",
            0.75f,
            DateTimeOffset.UtcNow);

        NavigationService navigation = new();

        (DocumentKind Kind, string Id)? opened = null;

        navigation.DocumentOpenRequested += (kind, id) => opened = (kind, id);

        DivinationViewModel viewModel = NewViewModel(new FakeDivinationDataSource(), navigation);

        viewModel.OpenSessionResultCommand.Execute(hit);

        Assert.Equal((DocumentKind.Session, SessionId.ToString("D")), opened);

    }

    [Fact]
    public async Task SearchSaga_FeatureDisabled_SetsFlag()
    {

        FakeDivinationDataSource dataSource = new()
        {

            SagaResult = new DataSourceResult<SagaSearchResult>(
                null,
                false,
                ErrorCodes.Embeddings.FeatureDisabled,
                "disabled"),

        };

        DivinationViewModel viewModel = NewViewModel(dataSource);

        viewModel.SagaQuery = "query";

        await viewModel.SearchSagaAsync(CancellationToken.None);

        Assert.True(viewModel.SagaFeatureDisabled);

        Assert.Equal("Saga Divination disabled.", viewModel.StatusText);

    }

    private static DivinationViewModel NewViewModel(
        FakeDivinationDataSource dataSource,
        NavigationService? navigation = null) =>
        new(
            dataSource,
            new FakeWorkspaceExplorerDataSource(),
            navigation ?? new NavigationService(),
            new FoundryFloorViewModel(new NullLogService()));

    private sealed class FakeDivinationDataSource : IDivinationDataSource
    {

        public DataSourceResult<SemanticSearchResult> SessionsResult { get; init; } =
            new(null, true, null, null);

        public DataSourceResult<WorkspaceSearchResult[]> WorkspaceResult { get; init; } =
            new([], true, null, null);

        public DataSourceResult<SagaSearchResult> SagaResult { get; init; } =
            new(null, true, null, null);

        public string? LastSessionQuery { get; private set; }

        public Task<DataSourceResult<SemanticSearchResult>> DivineSessionsAsync(SemanticSearchRequest request, CancellationToken cancellationToken)
        {

            LastSessionQuery = request.Query;

            return Task.FromResult(SessionsResult);

        }

        public Task<DataSourceResult<WorkspaceSearchResult[]>> DivineWorkspaceFilesAsync(string workspaceId, WorkspaceSemanticSearchRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(WorkspaceResult);

        public Task<DataSourceResult<SagaSearchResult>> DivineSagaAsync(SagaSearchRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(SagaResult);

    }

    private sealed class FakeWorkspaceExplorerDataSource : IWorkspaceExplorerDataSource
    {

        public Task<DataSourceResult<WorkspaceInfo[]>> ListWorkspacesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<WorkspaceInfo[]>([], true, null, null));

        public Task<DataSourceResult<FileListResult>> ListFilesAsync(string workspaceId, string? relativePath, bool? recursive, string? searchPattern, CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<FileListResult>(null, true, null, null));

        public Task<DataSourceResult<FileEntry>> GetFileInfoAsync(string workspaceId, string? relativePath, CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<FileEntry>(null, true, null, null));

        public Task<DataSourceResult<FileReadResult>> GetFileContentsAsync(string workspaceId, string relativePath, CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<FileReadResult>(null, true, null, null));

        public Task<DataSourceResult<bool>> IndexWorkspaceAsync(string workspaceId, CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<bool>(true, true, null, null));

        public Task<DataSourceResult<WorkspaceSearchResult[]>> DivineWorkspaceFilesAsync(string workspaceId, WorkspaceSemanticSearchRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<WorkspaceSearchResult[]>([], true, null, null));

        public Task<DataSourceResult<FileWriteResult>> WriteFileContentsAsync(string workspaceId, string relativePath, string content, CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<FileWriteResult>(null, true, null, null));

        public Task<DataSourceResult<TextBlockReplaceResult>> ReplaceTextBlockAsync(string workspaceId, string relativePath, string oldString, string newString, int? expectedReplacements, CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<TextBlockReplaceResult>(null, true, null, null));

        public Task<DataSourceResult<FileDeleteResult>> DeleteFileAsync(string workspaceId, string relativePath, bool? recursive, CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<FileDeleteResult>(null, true, null, null));

        public Task<DataSourceResult<DirectoryCreateResult>> CreateDirectoryAsync(string workspaceId, string relativePath, CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<DirectoryCreateResult>(null, true, null, null));

    }

}
