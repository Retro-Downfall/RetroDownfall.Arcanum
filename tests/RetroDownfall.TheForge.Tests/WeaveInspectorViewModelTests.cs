using System.ComponentModel;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.Services.Whispers;
using RetroDownfall.TheForge.Ux.ViewModels;
using RetroDownfall.TheForge.Ux.ViewModels.Archive;
using RetroDownfall.TheForge.Ux.ViewModels.Divination;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;
using RetroDownfall.TheForge.Ux.ViewModels.WeaveInspector;
using RetroDownfall.TheForge.Ux.ViewModels.WorkspaceExplorer;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class WeaveInspectorViewModelTests
{

    private static readonly Guid SessionId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static readonly WorkspaceInfo Workspace = new("ws-1", "My Workspace", "/tmp/ws-1", WorkspaceType.Custom, DateTimeOffset.UtcNow);

    [Fact]
    public async Task Refresh_LoadsWorkspacesStatusAndChunks()
    {

        WorkspaceIndexStatusDto status = NewStatus(totalFiles: 3, totalChunks: 12, indexingEnabled: true);

        WorkspaceFileChunkDto chunk = NewChunk("src/A.cs", "chunk-a-0", 0, 2);

        WorkspaceFileChunkPage page = new([chunk], Total: 12, Limit: 50, Offset: 0, HasMore: true, RelativePathFilter: null);

        FakeWeaveInspectorDataSource inspector = new()
        {
            StatusResult = new DataSourceResult<WorkspaceIndexStatusDto>(status, true, null, null),

            ChunksResult = new DataSourceResult<WorkspaceFileChunkPage>(page, true, null, null),
        };

        FakeWorkspaceExplorerDataSource workspace = new() { Workspaces = [Workspace] };

        WeaveInspectorViewModel viewModel = NewViewModel(inspector, workspace);

        viewModel.IsVisible = true;

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.Single(viewModel.Workspaces);

        Assert.Equal(Workspace.Id, viewModel.SelectedWorkspace!.Id);

        Assert.NotNull(viewModel.Status);

        Assert.Equal(3, viewModel.Status!.TotalIndexedFiles);

        Assert.Equal(12, viewModel.Status.TotalChunks);

        Assert.True(viewModel.IndexingEnabled);

        Assert.Single(viewModel.Chunks);

        Assert.Equal(12, viewModel.ChunkTotal);

        Assert.True(viewModel.ChunkHasMore);

    }

    [Fact]
    public async Task LoadChunks_AppliesFilterAndNextPageAdvancesOffset()
    {

        WorkspaceFileChunkPage firstPage = new([NewChunk("src/A.cs", "c0", 0, 1)], 2, 50, 0, true, null);

        WorkspaceFileChunkPage secondPage = new([NewChunk("src/A.cs", "c1", 0, 1)], 2, 50, 50, false, null);

        FakeWeaveInspectorDataSource inspector = new()
        {
            ChunksResult = new DataSourceResult<WorkspaceFileChunkPage>(firstPage, true, null, null),
        };

        FakeWorkspaceExplorerDataSource workspace = new() { Workspaces = [Workspace] };

        WeaveInspectorViewModel viewModel = NewViewModel(inspector, workspace);

        viewModel.SelectedWorkspace = Workspace;

        viewModel.ChunkFilterRelativePath = "src/A.cs";

        await viewModel.LoadChunksAsync(CancellationToken.None);

        Assert.Equal("src/A.cs", inspector.LastChunksRelativePath);

        Assert.Equal(0, inspector.LastChunksOffset);

        Assert.True(viewModel.ChunkHasMore);

        inspector.ChunksResult = new DataSourceResult<WorkspaceFileChunkPage>(secondPage, true, null, null);

        await viewModel.NextChunkPageAsync(CancellationToken.None);

        Assert.Equal(50, inspector.LastChunksOffset);

        Assert.False(viewModel.ChunkHasMore);

    }

    [Fact]
    public async Task LoadChunksForFile_SetsFilterResetsOffsetAndFocusesIndexTab()
    {

        WorkspaceFileChunkPage page = new([NewChunk("src/Found.cs", "c0", 0, 1)], 1, 50, 0, false, "src/Found.cs");

        FakeWeaveInspectorDataSource inspector = new()
        {
            ChunksResult = new DataSourceResult<WorkspaceFileChunkPage>(page, true, null, null),
        };

        FakeWorkspaceExplorerDataSource workspace = new() { Workspaces = [Workspace] };

        WeaveInspectorViewModel viewModel = NewViewModel(inspector, workspace);

        viewModel.SelectedWorkspace = Workspace;

        viewModel.ActiveTabIndex = 1;

        viewModel.ChunkOffset = 100;

        await viewModel.LoadChunksForFileAsync("src/Found.cs");

        Assert.Equal("src/Found.cs", viewModel.ChunkFilterRelativePath);

        Assert.Equal(0, viewModel.ChunkOffset);

        Assert.Equal(0, viewModel.ActiveTabIndex);

        Assert.Equal("src/Found.cs", inspector.LastChunksRelativePath);

    }

    [Fact]
    public async Task Reindex_WhenSuccessful_SurfacesTriggeredAndWhispersSuccess()
    {

        FakeWorkspaceExplorerDataSource workspace = new()
        {
            Workspaces = [Workspace],

            IndexResult = new DataSourceResult<bool>(true, true, null, null),
        };

        FakeWhispersService whispers = new();

        WeaveInspectorViewModel viewModel = NewViewModel(new FakeWeaveInspectorDataSource(), workspace, whispers: whispers);

        viewModel.SelectedWorkspace = Workspace;

        await viewModel.ReindexAsync(CancellationToken.None);

        Assert.Equal(Workspace.Id, workspace.LastIndexWorkspaceId);

        Assert.Contains("Re-index triggered", viewModel.StatusText, StringComparison.Ordinal);

        Assert.Contains((WhisperSeverity.Success, "Re-index triggered.", (string?)null), whispers.Calls);

    }

    [Fact]
    public async Task Reindex_FeatureDisabled_SetsWorkspaceFeatureDisabled()
    {

        FakeWorkspaceExplorerDataSource workspace = new()
        {
            Workspaces = [Workspace],

            IndexResult = new DataSourceResult<bool>(false, false, ErrorCodes.Embeddings.FeatureDisabled, "disabled"),
        };

        WeaveInspectorViewModel viewModel = NewViewModel(new FakeWeaveInspectorDataSource(), workspace);

        viewModel.SelectedWorkspace = Workspace;

        await viewModel.ReindexAsync(CancellationToken.None);

        Assert.True(viewModel.WorkspaceFeatureDisabled);

    }

    [Fact]
    public async Task ResetEmbeddings_WhenConfirmed_CallsDataSourceWithWorkspaceFileScopeAndRefreshes()
    {

        EmbeddingsResetResult reset = new(new Dictionary<string, int>
        {
            ["workspace_file_chunks"] = 12,

            ["workspace_file_embeddings"] = 12,
        });

        FakeWeaveInspectorDataSource inspector = new()
        {
            StatusResult = new DataSourceResult<WorkspaceIndexStatusDto>(NewStatus(0, 0, true), true, null, null),

            ChunksResult = new DataSourceResult<WorkspaceFileChunkPage>(new([], 0, 50, 0, false, null), true, null, null),

            ResetResult = new DataSourceResult<EmbeddingsResetResult>(reset, true, null, null),
        };

        FakeWorkspaceExplorerDataSource workspace = new() { Workspaces = [Workspace] };

        ControllableConfirmationDialog confirmation = new(accept: true);

        FakeWhispersService whispers = new();

        WeaveInspectorViewModel viewModel = NewViewModel(inspector, workspace, confirmation: confirmation, whispers: whispers);

        viewModel.SelectedWorkspace = Workspace;

        await viewModel.ResetEmbeddingsAsync(CancellationToken.None);

        Assert.Equal(1, inspector.ResetCallCount);

        Assert.Equal(WeaveInspectorViewModel.DefaultResetScope, inspector.LastResetScope);

        Assert.Contains("workspace_file_chunks=12", viewModel.StatusText, StringComparison.Ordinal);

        Assert.Contains((WhisperSeverity.Success, "Embeddings reset.", (string?)null), whispers.Calls);

        // Confirmation must default to NOT accepting (destructive).
        Assert.False(confirmation.LastConfirmIsDefault);

        // Refresh after reset: status + chunks reloaded.
        Assert.Equal(1, inspector.StatusCallCount);

        Assert.Empty(viewModel.Chunks);

    }

    [Fact]
    public async Task ResetEmbeddings_WhenCancelled_DoesNotCallDataSource()
    {

        FakeWeaveInspectorDataSource inspector = new();

        FakeWorkspaceExplorerDataSource workspace = new() { Workspaces = [Workspace] };

        ControllableConfirmationDialog confirmation = new(accept: false);

        WeaveInspectorViewModel viewModel = NewViewModel(inspector, workspace, confirmation: confirmation);

        viewModel.SelectedWorkspace = Workspace;

        await viewModel.ResetEmbeddingsAsync(CancellationToken.None);

        Assert.Equal(0, inspector.ResetCallCount);

        Assert.Equal("Reset cancelled.", viewModel.StatusText);

    }

    [Fact]
    public async Task SearchWorkspace_PopulatesResults()
    {

        WorkspaceSearchResult hit = new("src/A.cs", 0, 2, 0.9f, "preview");

        FakeWorkspaceExplorerDataSource workspace = new()
        {
            Workspaces = [Workspace],

            WorkspaceDivineResult = new DataSourceResult<WorkspaceSearchResult[]>([hit], true, null, null),
        };

        WeaveInspectorViewModel viewModel = NewViewModel(new FakeWeaveInspectorDataSource(), workspace);

        viewModel.SelectedWorkspace = Workspace;

        viewModel.WorkspaceQuery = "find me";

        await viewModel.SearchWorkspaceAsync(CancellationToken.None);

        Assert.Single(viewModel.WorkspaceResults);

        Assert.Equal("src/A.cs", viewModel.WorkspaceResults[0].RelativePath);

        Assert.Equal("find me", workspace.LastWorkspaceDivineQuery);

    }

    [Fact]
    public async Task SearchSaga_DisplaysSimilaritiesPerMemory()
    {

        SagaMemoryDto m1 = new("m1", "alpha", DateTimeOffset.UtcNow, null, null, null);

        SagaMemoryDto m2 = new("m2", "beta", DateTimeOffset.UtcNow, null, null, null);

        FakeSagaArchiveDataSource saga = new()
        {
            DivineResult = new DataSourceResult<SagaSearchResult>(new SagaSearchResult([m1, m2], [0.91f, 0.42f]), true, null, null),
        };

        WeaveInspectorViewModel viewModel = NewViewModel(new FakeWeaveInspectorDataSource(), new FakeWorkspaceExplorerDataSource(), saga: saga);

        viewModel.SagaQuery = "alpha";

        await viewModel.SearchSagaAsync(CancellationToken.None);

        Assert.Equal(2, viewModel.SagaResults.Count);

        Assert.Equal(0.91f, viewModel.SagaResults[0].Similarity);

        Assert.Equal(0.42f, viewModel.SagaResults[1].Similarity);

    }

    [Fact]
    public async Task SearchSessions_PopulatesResults()
    {

        SemanticSessionSearchResult hit = new(SessionId, "Forge chat", Guid.NewGuid(), "user", "hi", 0.7f, DateTimeOffset.UtcNow);

        FakeDivinationDataSource divination = new()
        {
            SessionsResult = new DataSourceResult<SemanticSearchResult>(new SemanticSearchResult([hit], false, null), true, null, null),
        };

        WeaveInspectorViewModel viewModel = NewViewModel(new FakeWeaveInspectorDataSource(), new FakeWorkspaceExplorerDataSource(), divination: divination);

        viewModel.SessionQuery = "hi";

        await viewModel.SearchSessionsAsync(CancellationToken.None);

        Assert.Single(viewModel.SessionResults);

        Assert.Equal(SessionId, viewModel.SessionResults[0].SessionId);

        Assert.Equal("hi", divination.LastSessionQuery);

    }

    [Fact]
    public void OpenSessionResult_OpensDocumentInTome()
    {

        SemanticSessionSearchResult hit = new(SessionId, "Forge chat", Guid.NewGuid(), "user", "preview", 0.75f, DateTimeOffset.UtcNow);

        NavigationService navigation = new();

        (DocumentKind Kind, string Id)? opened = null;

        navigation.DocumentOpenRequested += (kind, id, _) => opened = (kind, id);

        WeaveInspectorViewModel viewModel = NewViewModel(new FakeWeaveInspectorDataSource(), new FakeWorkspaceExplorerDataSource(), navigation: navigation);

        viewModel.OpenSessionResultCommand.Execute(hit);

        Assert.Equal((DocumentKind.Session, SessionId.ToString("D")), opened);

    }

    [Fact]
    public void VectorMode_ReflectsConnectionMetaAndShowsManagedBanner()
    {

        FakeArcanumConnection connection = new()
        {
            LastMeta = NewMeta(embeddingsEnabled: true, vectorMode: "managed"),
        };

        WeaveInspectorViewModel viewModel = NewViewModel(new FakeWeaveInspectorDataSource(), new FakeWorkspaceExplorerDataSource(), connection: connection);

        Assert.Equal("managed", viewModel.VectorModeText, StringComparer.Ordinal);

        Assert.True(viewModel.ShowManagedWeaveBanner);

        Assert.True(viewModel.EmbeddingsEnabled);

    }

    [Fact]
    public async Task RefreshSagaStats_PopulatesStats()
    {

        SagaStats stats = new(7, 3, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        FakeSagaArchiveDataSource saga = new()
        {
            StatsResult = new DataSourceResult<SagaStats>(stats, true, null, null),
        };

        WeaveInspectorViewModel viewModel = NewViewModel(new FakeWeaveInspectorDataSource(), new FakeWorkspaceExplorerDataSource(), saga: saga);

        await viewModel.RefreshSagaStatsAsync(CancellationToken.None);

        Assert.NotNull(viewModel.SagaStats);

        Assert.Equal(7, viewModel.SagaStats!.TotalCount);

        Assert.Contains("7 memories", viewModel.SagaStatsText, StringComparison.Ordinal);

    }

    private static WeaveInspectorViewModel NewViewModel(
        FakeWeaveInspectorDataSource inspector,
        FakeWorkspaceExplorerDataSource workspace,
        FakeDivinationDataSource? divination = null,
        FakeSagaArchiveDataSource? saga = null,
        NavigationService? navigation = null,
        ControllableConfirmationDialog? confirmation = null,
        FakeClipboardService? clipboard = null,
        FakeWhispersService? whispers = null,
        FakeArcanumConnection? connection = null) =>
        new(
            inspector,
            workspace,
            divination ?? new FakeDivinationDataSource(),
            saga ?? new FakeSagaArchiveDataSource(),
            navigation ?? new NavigationService(),
            new FoundryFloorViewModel(new NullLogService()),
            confirmation ?? new ControllableConfirmationDialog(accept: false),
            clipboard ?? new FakeClipboardService(),
            whispers ?? new FakeWhispersService(),
            connection ?? new FakeArcanumConnection());

    private static WorkspaceIndexStatusDto NewStatus(int totalFiles, int totalChunks, bool indexingEnabled) =>
        new(
            Workspace.Id,
            Workspace.Name,
            Workspace.Path,
            "managed",
            "managed SIMD fallback",
            indexingEnabled,
            totalFiles,
            totalChunks,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            4,
            "Skipped-file reasons are not currently persisted by Arcanum.");

    private static WorkspaceFileChunkDto NewChunk(string relativePath, string chunkId, int chunkIndex, int totalChunksForFile) =>
        new(chunkId, relativePath, chunkIndex, totalChunksForFile, "content preview", 0, 15, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static InstanceMetadataDto NewMeta(bool embeddingsEnabled, string vectorMode) =>
        new(
            Version: "1.0.0",
            OsDescription: "test",
            RuntimeIdentifier: "test",
            ProcessId: 1,
            StartTime: DateTimeOffset.UtcNow,
            Uptime: TimeSpan.Zero,
            NativeAot: false,
            GrimoireDirectory: "/tmp",
            ConfigPath: "/tmp/arcanum.json",
            Port: 5000,
            ListenAny: false,
            LoreSystemEnabled: true,
            ArchiveSearchEnabled: true,
            ContextCompressionEnabled: true,
            TokenTrackingEnabled: true,
            HttpsEnabled: false,
            HttpsPort: 0,
            HttpsUrl: null,
            HttpUrl: null,
            EmbeddingsEnabled: embeddingsEnabled,
            EmbeddingsVectorMode: vectorMode,
            EmbeddingsVectorDiagnostic: "diag",
            EmbeddingsManagedSearchRowBudget: 50000,
            Edition: "local",
            HostProcessToolsAllowed: false,
            ConclaveEnabled: false,
            A2AServerEnabled: false,
            A2AClientEnabled: false,
            ConclaveA2AState: "disabled",
            A2AServerPath: null,
            A2AAgentCardPath: null,
            A2AAllowedRemoteAgentCount: 0);

    private sealed class FakeWeaveInspectorDataSource : IWeaveInspectorDataSource
    {

        public DataSourceResult<WorkspaceIndexStatusDto> StatusResult { get; set; } =
            new(null, true, null, null);

        public DataSourceResult<WorkspaceFileChunkPage> ChunksResult { get; set; } =
            new(new([], 0, 50, 0, false, null), true, null, null);

        public DataSourceResult<EmbeddingsResetResult> ResetResult { get; set; } =
            new(new(new Dictionary<string, int>()), true, null, null);

        public string? LastChunksRelativePath { get; private set; }

        public int LastChunksOffset { get; private set; }

        public int LastChunksLimit { get; private set; }

        public string? LastResetScope { get; private set; }

        public int ResetCallCount { get; private set; }

        public int StatusCallCount { get; private set; }

        public Task<DataSourceResult<WorkspaceIndexStatusDto>> GetIndexStatusAsync(string workspaceId, CancellationToken cancellationToken)
        {

            StatusCallCount++;

            return Task.FromResult(StatusResult);

        }

        public Task<DataSourceResult<WorkspaceFileChunkPage>> GetChunksAsync(string workspaceId, string? relativePath, int limit, int offset, CancellationToken cancellationToken)
        {

            LastChunksRelativePath = relativePath;

            LastChunksLimit = limit;

            LastChunksOffset = offset;

            return Task.FromResult(ChunksResult);

        }

        public Task<DataSourceResult<EmbeddingsResetResult>> ResetEmbeddingsAsync(string scope, CancellationToken cancellationToken)
        {

            LastResetScope = scope;

            ResetCallCount++;

            return Task.FromResult(ResetResult);

        }

    }

    private sealed class FakeWorkspaceExplorerDataSource : IWorkspaceExplorerDataSource
    {

        public WorkspaceInfo[] Workspaces { get; set; } = [];

        public DataSourceResult<bool> IndexResult { get; set; } = new(true, true, null, null);

        public DataSourceResult<WorkspaceSearchResult[]> WorkspaceDivineResult { get; set; } =
            new([], true, null, null);

        public string? LastIndexWorkspaceId { get; private set; }

        public string? LastWorkspaceDivineQuery { get; private set; }

        public Task<DataSourceResult<WorkspaceInfo[]>> ListWorkspacesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<WorkspaceInfo[]>(Workspaces, true, null, null));

        public Task<DataSourceResult<bool>> IndexWorkspaceAsync(string workspaceId, CancellationToken cancellationToken)
        {

            LastIndexWorkspaceId = workspaceId;

            return Task.FromResult(IndexResult);

        }

        public Task<DataSourceResult<WorkspaceSearchResult[]>> DivineWorkspaceFilesAsync(string workspaceId, WorkspaceSemanticSearchRequest request, CancellationToken cancellationToken)
        {

            LastWorkspaceDivineQuery = request.Query;

            return Task.FromResult(WorkspaceDivineResult);

        }

        public Task<DataSourceResult<FileListResult>> ListFilesAsync(string workspaceId, string? relativePath, bool? recursive, string? searchPattern, CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<FileListResult>(null, true, null, null));

        public Task<DataSourceResult<FileEntry>> GetFileInfoAsync(string workspaceId, string? relativePath, CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<FileEntry>(null, true, null, null));

        public Task<DataSourceResult<FileReadResult>> GetFileContentsAsync(string workspaceId, string relativePath, CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<FileReadResult>(null, true, null, null));

        public Task<DataSourceResult<FileWriteResult>> WriteFileContentsAsync(string workspaceId, string relativePath, string content, CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<FileWriteResult>(null, true, null, null));

        public Task<DataSourceResult<TextBlockReplaceResult>> ReplaceTextBlockAsync(string workspaceId, string relativePath, string oldString, string newString, int? expectedReplacements, CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<TextBlockReplaceResult>(null, true, null, null));

        public Task<DataSourceResult<FileDeleteResult>> DeleteFileAsync(string workspaceId, string relativePath, bool? recursive, CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<FileDeleteResult>(null, true, null, null));

        public Task<DataSourceResult<DirectoryCreateResult>> CreateDirectoryAsync(string workspaceId, string relativePath, CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<DirectoryCreateResult>(null, true, null, null));

    }

    private sealed class FakeDivinationDataSource : IDivinationDataSource
    {

        public DataSourceResult<SemanticSearchResult> SessionsResult { get; set; } =
            new(null, true, null, null);

        public string? LastSessionQuery { get; private set; }

        public Task<DataSourceResult<SemanticSearchResult>> DivineSessionsAsync(SemanticSearchRequest request, CancellationToken cancellationToken)
        {

            LastSessionQuery = request.Query;

            return Task.FromResult(SessionsResult);

        }

        public Task<DataSourceResult<WorkspaceSearchResult[]>> DivineWorkspaceFilesAsync(string workspaceId, WorkspaceSemanticSearchRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<WorkspaceSearchResult[]>([], true, null, null));

        public Task<DataSourceResult<SagaSearchResult>> DivineSagaAsync(SagaSearchRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<SagaSearchResult>(null, true, null, null));

    }

    private sealed class FakeSagaArchiveDataSource : ISagaArchiveDataSource
    {

        public DataSourceResult<SagaSearchResult> DivineResult { get; set; } =
            new(null, true, null, null);

        public DataSourceResult<SagaStats> StatsResult { get; set; } =
            new(null, true, null, null);

        public Task<DataSourceResult<SagaMemoryDto[]>> ListAsync(string? query, Guid? sessionId, int? limit, int? offset, CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<SagaMemoryDto[]>([], true, null, null));

        public Task<DataSourceResult<SagaSearchResult>> DivineAsync(string query, int? limit, CancellationToken cancellationToken) =>
            Task.FromResult(DivineResult);

        public Task<DataSourceResult<bool>> DeleteAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<bool>(true, true, null, null));

        public Task<DataSourceResult<bool>> DeleteAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new DataSourceResult<bool>(true, true, null, null));

        public Task<DataSourceResult<SagaStats>> GetStatsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(StatsResult);

    }

    private sealed class ControllableConfirmationDialog(bool accept) : IConfirmationDialogService
    {

        public int CallCount { get; private set; }

        public bool LastConfirmIsDefault { get; private set; } = true;

        public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken, bool confirmIsDefault = true)
        {

            CallCount++;

            LastConfirmIsDefault = confirmIsDefault;

            return Task.FromResult(accept);

        }

    }

    private sealed class FakeArcanumConnection : IArcanumConnection
    {

        public ConnectionState State { get; set; } = ConnectionState.Disconnected;

        public HealthReportDto? LastReport { get; set; }

        private InstanceMetadataDto? _lastMeta;

        public InstanceMetadataDto? LastMeta
        {

            get => _lastMeta;

            set
            {

                _lastMeta = value;

                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastMeta)));

            }

        }

        public string? LastErrorCode { get; set; }

        public string? LastErrorMessage { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Connect()
        {
        }

        public void Disconnect()
        {
        }

    }

}
