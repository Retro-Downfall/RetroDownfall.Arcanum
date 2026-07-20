using System.ComponentModel;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Core.Models.OpenAi;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.Services.Whispers;
using RetroDownfall.TheForge.Ux.ViewModels;
using RetroDownfall.TheForge.Ux.ViewModels.FilesBatches;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class FilesBatchesViewModelTests
{

    [Fact]
    public async Task RefreshFilesAsync_PopulatesFromFake()
    {

        FakeFilesBatchesDataSource dataSource = new();

        dataSource.Files.Add(new OpenAiFileObject("file-1", 10, 1, "a.jsonl", "batch"));

        ControllableConnection connection = new() { State = ConnectionState.Connected };

        FilesBatchesViewModel viewModel = Create(dataSource, connection);

        await viewModel.RefreshFilesAsync(CancellationToken.None);

        Assert.Single(viewModel.Files);

        Assert.Equal("file-1", viewModel.Files[0].Id);

        Assert.False(viewModel.HasNoFiles);

        viewModel.Dispose();

    }

    [Fact]
    public async Task UploadFileAsync_CallsDataSourceWithPurpose()
    {

        FakeFilesBatchesDataSource dataSource = new();

        ControllableConnection connection = new() { State = ConnectionState.Connected };

        ControllableFileDialog dialog = new("/tmp/upload.jsonl");

        FilesBatchesViewModel viewModel = Create(dataSource, connection, dialog);

        viewModel.UploadPurpose = "batch";

        await viewModel.UploadFileAsync(CancellationToken.None);

        Assert.Equal(("/tmp/upload.jsonl", "batch"), dataSource.LastUpload);

        Assert.Contains(viewModel.Files, f => f.Id == "file-uploaded");

        viewModel.Dispose();

    }

    [Fact]
    public void IsVisible_StartsAndStopsPolling_WhenConnected()
    {

        FakeFilesBatchesDataSource dataSource = new();

        ControllableConnection connection = new() { State = ConnectionState.Connected };

        FilesBatchesViewModel viewModel = Create(dataSource, connection);

        Assert.False(viewModel.IsPolling);

        viewModel.IsVisible = true;

        Assert.True(viewModel.IsPolling);

        viewModel.IsVisible = false;

        Assert.False(viewModel.IsPolling);

        viewModel.Dispose();

        Assert.False(viewModel.IsPolling);

    }

    [Fact]
    public void Disconnect_StopsPollingWhileVisible()
    {

        FakeFilesBatchesDataSource dataSource = new();

        ControllableConnection connection = new() { State = ConnectionState.Connected };

        FilesBatchesViewModel viewModel = Create(dataSource, connection);

        viewModel.IsVisible = true;

        Assert.True(viewModel.IsPolling);

        connection.State = ConnectionState.Disconnected;

        Assert.False(viewModel.IsPolling);

        viewModel.Dispose();

    }

    [Fact]
    public void Dispose_StopsPolling()
    {

        FakeFilesBatchesDataSource dataSource = new();

        ControllableConnection connection = new() { State = ConnectionState.Connected };

        FilesBatchesViewModel viewModel = Create(dataSource, connection);

        viewModel.IsVisible = true;

        Assert.True(viewModel.IsPolling);

        viewModel.Dispose();

        Assert.False(viewModel.IsPolling);

    }

    private static FilesBatchesViewModel Create(
        IFilesBatchesDataSource dataSource,
        IArcanumConnection connection,
        IArtifactFileDialogService? dialog = null)
    {

        FoundryFloorViewModel floor = new(new NullLogService());

        return new FilesBatchesViewModel(
            dataSource,
            connection,
            floor,
            dialog ?? new NullArtifactFileDialogService(),
            new NullConfirmationDialogService(),
            new FakeWhispersService());

    }

    private sealed class FakeFilesBatchesDataSource : IFilesBatchesDataSource
    {

        public List<OpenAiFileObject> Files { get; } = [];

        public List<OpenAiBatchObject> Batches { get; } = [];

        public (string Path, string Purpose)? LastUpload { get; private set; }

        public Task<OpenAiResult<OpenAiFileListResponse>> ListFilesAsync(string? purpose, CancellationToken cancellationToken) =>
            Task.FromResult(OpenAiResult<OpenAiFileListResponse>.Ok(new OpenAiFileListResponse([.. Files])));

        public Task<OpenAiResult<OpenAiFileObject>> UploadFileAsync(string filePath, string purpose, CancellationToken cancellationToken)
        {

            LastUpload = (filePath, purpose);

            OpenAiFileObject file = new("file-uploaded", 1, 1, Path.GetFileName(filePath), purpose);

            Files.Add(file);

            return Task.FromResult(OpenAiResult<OpenAiFileObject>.Ok(file));

        }

        public Task<OpenAiResult<OpenAiFileDeleteResponse>> DeleteFileAsync(string fileId, CancellationToken cancellationToken) =>
            Task.FromResult(OpenAiResult<OpenAiFileDeleteResponse>.Ok(new OpenAiFileDeleteResponse(fileId, true)));

        public Task<OpenAiResult<bool>> DownloadFileContentAsync(string fileId, string destinationPath, CancellationToken cancellationToken) =>
            Task.FromResult(OpenAiResult<bool>.Ok(true));

        public Task<OpenAiResult<JsonlPreviewResult>> PreviewFileJsonlAsync(string fileId, int maxLines, int maxBytes, CancellationToken cancellationToken) =>
            Task.FromResult(OpenAiResult<JsonlPreviewResult>.Ok(new JsonlPreviewResult([], false, 0)));

        public Task<OpenAiResult<OpenAiBatchListResponse>> ListBatchesAsync(string? status, CancellationToken cancellationToken) =>
            Task.FromResult(OpenAiResult<OpenAiBatchListResponse>.Ok(new OpenAiBatchListResponse([.. Batches])));

        public Task<OpenAiResult<OpenAiBatchObject>> GetBatchAsync(string batchId, CancellationToken cancellationToken) =>
            Task.FromResult(OpenAiResult<OpenAiBatchObject>.Fail("not_found", "missing"));

        public Task<OpenAiResult<OpenAiBatchObject>> CreateBatchAsync(string inputFileId, string? endpoint, string? completionWindow, CancellationToken cancellationToken) =>
            Task.FromResult(OpenAiResult<OpenAiBatchObject>.Fail("test", "not used"));

        public Task<OpenAiResult<OpenAiBatchObject>> CancelBatchAsync(string batchId, CancellationToken cancellationToken) =>
            Task.FromResult(OpenAiResult<OpenAiBatchObject>.Fail("test", "not used"));

        public Task<OpenAiResult<OpenAiBatchObject>> ResetBatchAsync(string batchId, CancellationToken cancellationToken) =>
            Task.FromResult(OpenAiResult<OpenAiBatchObject>.Fail("test", "not used"));

    }

    private sealed class ControllableConnection : IArcanumConnection
    {

        private ConnectionState _state = ConnectionState.Disconnected;

        public ConnectionState State
        {
            get => _state;
            set
            {
                if (_state == value)
                {

                    return;

                }

                _state = value;

                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));

            }
        }

        public HealthReportDto? LastReport => null;

        public InstanceMetadataDto? LastMeta => null;

        public string? LastErrorCode => null;

        public string? LastErrorMessage => null;

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Connect() => State = ConnectionState.Connected;

        public void Disconnect() => State = ConnectionState.Disconnected;

    }

    private sealed class ControllableFileDialog(string? path) : IArtifactFileDialogService
    {

        public Task<string?> PickSaveJsonPathAsync(string suggestedFileName, CancellationToken cancellationToken) =>
            Task.FromResult(path);

        public Task<string?> PickOpenJsonPathAsync(CancellationToken cancellationToken) =>
            Task.FromResult(path);

        public Task<string?> PickSaveCsvPathAsync(string suggestedFileName, CancellationToken cancellationToken) =>
            Task.FromResult(path);

        public Task<string?> PickOpenAnyPathAsync(CancellationToken cancellationToken) =>
            Task.FromResult(path);

        public Task<string?> PickSaveAnyPathAsync(string suggestedFileName, string? defaultExtension, CancellationToken cancellationToken) =>
            Task.FromResult(path);

    }

}
