using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.TheForge.Core.Models.OpenAi;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.Services.Services;
using RetroDownfall.TheForge.Ux.Services.Whispers;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;

namespace RetroDownfall.TheForge.Ux.ViewModels.FilesBatches;

/// <summary>
/// Files &amp; Batches (Phase 9) — dock tool over OpenAI-compatible <c>/v1/files</c> and
/// <c>/v1/batches</c>. Upload / list / filter / download / delete files; create / list / detail /
/// cancel / reset batches; bounded JSONL preview with download/export for full data. Polls batch
/// list on a bounded interval while visible and connected; disposes the poll CTS when hidden or
/// when Arcanum disconnects. Suite-from-batch is deferred (not practical this pass).
/// </summary>
public sealed partial class FilesBatchesViewModel : ViewModelBase, IDisposable
{

    public const int PollIntervalSeconds = 5;

    public const string DefaultUploadPurpose = "batch";

    public const string SuiteFromBatchDeferredNoteText =
        "Creating a Proving Grounds suite from a completed batch is deferred — not practical in this pass.";

    private readonly IFilesBatchesDataSource _dataSource;

    private readonly IArcanumConnection _connection;

    private readonly FoundryFloorViewModel _foundryFloor;

    private readonly IArtifactFileDialogService _fileDialog;

    private readonly IConfirmationDialogService _confirmation;

    private readonly IWhispersService _whispers;

    private CancellationTokenSource? _pollCts;

    private bool _disposed;

    private bool _loaded;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _lastError;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private int _activeTabIndex;

    [ObservableProperty]
    private string _filePurposeFilter = string.Empty;

    [ObservableProperty]
    private string _uploadPurpose = DefaultUploadPurpose;

    [ObservableProperty]
    private OpenAiFileObject? _selectedFile;

    [ObservableProperty]
    private string _jsonlPreviewText = string.Empty;

    [ObservableProperty]
    private bool _jsonlPreviewTruncated;

    [ObservableProperty]
    private string _batchStatusFilter = string.Empty;

    [ObservableProperty]
    private string _createBatchInputFileId = string.Empty;

    [ObservableProperty]
    private OpenAiBatchObject? _selectedBatch;

    public FilesBatchesViewModel(
        IFilesBatchesDataSource dataSource,
        IArcanumConnection connection,
        FoundryFloorViewModel foundryFloor,
        IArtifactFileDialogService fileDialog,
        IConfirmationDialogService confirmation,
        IWhispersService whispers)
    {

        _dataSource = dataSource;

        _connection = connection;

        _foundryFloor = foundryFloor;

        _fileDialog = fileDialog;

        _confirmation = confirmation;

        _whispers = whispers;

        Title = "Files & Batches";

        StatusText = "Files & Batches ready.";

        _connection.PropertyChanged += OnConnectionPropertyChanged;

    }

    public ObservableCollection<OpenAiFileObject> Files { get; } = [];

    public ObservableCollection<OpenAiBatchObject> Batches { get; } = [];

    public bool HasNoFiles => Files.Count == 0;

    public bool HasNoBatches => Batches.Count == 0;

    public string SuiteFromBatchDeferredNote => SuiteFromBatchDeferredNoteText;

    public bool IsConnected => _connection.State == ConnectionState.Connected;

    partial void OnIsVisibleChanged(bool value)
    {

        SyncPolling();

        if (value && !_loaded && IsConnected)
        {

            _loaded = true;

            _ = RefreshFilesAsync(CancellationToken.None);

            _ = RefreshBatchesAsync(CancellationToken.None);

        }

    }

    partial void OnSelectedFileChanged(OpenAiFileObject? value)
    {

        JsonlPreviewText = string.Empty;

        JsonlPreviewTruncated = false;

        if (value is not null && value.Filename.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
        {

            _ = PreviewSelectedFileAsync(CancellationToken.None);

        }

    }

    partial void OnSelectedBatchChanged(OpenAiBatchObject? value)
    {

        if (value is not null && string.IsNullOrWhiteSpace(CreateBatchInputFileId))
        {

            CreateBatchInputFileId = value.InputFileId;

        }

    }

    [RelayCommand]
    public async Task RefreshFilesAsync(CancellationToken cancellationToken)
    {

        if (!IsConnected)
        {

            LastError = "Arcanum is disconnected.";

            StatusText = "Connect to Arcanum to list files.";

            return;

        }

        IsBusy = true;

        LastError = null;

        try
        {

            string? purpose = NullIfWhiteSpace(FilePurposeFilter);

            OpenAiResult<OpenAiFileListResponse> result = await _dataSource
                .ListFilesAsync(purpose, cancellationToken)
                .ConfigureAwait(true);

            Files.Clear();

            SelectedFile = null;

            if (result.Success && result.Data is { } list)
            {

                foreach (OpenAiFileObject file in list.Data)
                {

                    Files.Add(file);

                }

                StatusText = Files.Count == 0
                    ? "No uploaded files."
                    : $"{Files.Count} file(s).";

            }
            else
            {

                LastError = result.ErrorMessage ?? "Failed to list files.";

                StatusText = "Files unavailable.";

                _foundryFloor.AppendLine($"Files & Batches list files failed: {LastError}");

            }

            OnPropertyChanged(nameof(HasNoFiles));

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            // Poll / visibility stop.

        }
        catch (Exception ex)
        {

            LastError = ex.Message;

            _foundryFloor.AppendLine($"Files & Batches list files error: {ex.Message}");

        }
        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    public async Task RefreshBatchesAsync(CancellationToken cancellationToken)
    {

        if (!IsConnected)
        {

            LastError = "Arcanum is disconnected.";

            StatusText = "Connect to Arcanum to list batches.";

            return;

        }

        IsBusy = true;

        LastError = null;

        try
        {

            string? status = NullIfWhiteSpace(BatchStatusFilter);

            string? selectedId = SelectedBatch?.Id;

            OpenAiResult<OpenAiBatchListResponse> result = await _dataSource
                .ListBatchesAsync(status, cancellationToken)
                .ConfigureAwait(true);

            Batches.Clear();

            if (result.Success && result.Data is { } list)
            {

                OpenAiBatchObject? reselect = null;

                foreach (OpenAiBatchObject batch in list.Data)
                {

                    Batches.Add(batch);

                    if (selectedId is not null && string.Equals(batch.Id, selectedId, StringComparison.Ordinal))
                    {

                        reselect = batch;

                    }

                }

                SelectedBatch = reselect;

                StatusText = Batches.Count == 0
                    ? "No batches."
                    : $"{Batches.Count} batch(es).";

            }
            else
            {

                SelectedBatch = null;

                LastError = result.ErrorMessage ?? "Failed to list batches.";

                StatusText = "Batches unavailable.";

                _foundryFloor.AppendLine($"Files & Batches list batches failed: {LastError}");

            }

            OnPropertyChanged(nameof(HasNoBatches));

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            // Poll / visibility stop.

        }
        catch (Exception ex)
        {

            LastError = ex.Message;

            _foundryFloor.AppendLine($"Files & Batches list batches error: {ex.Message}");

        }
        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    public async Task UploadFileAsync(CancellationToken cancellationToken)
    {

        if (!IsConnected)
        {

            LastError = "Arcanum is disconnected.";

            return;

        }

        string? path = await _fileDialog.PickOpenAnyPathAsync(cancellationToken).ConfigureAwait(true);

        if (path is null)
        {

            return;

        }

        string purpose = string.IsNullOrWhiteSpace(UploadPurpose) ? DefaultUploadPurpose : UploadPurpose.Trim();

        IsBusy = true;

        LastError = null;

        try
        {

            OpenAiResult<OpenAiFileObject> result = await _dataSource
                .UploadFileAsync(path, purpose, cancellationToken)
                .ConfigureAwait(true);

            if (result.Success && result.Data is { } file)
            {

                StatusText = $"Uploaded {file.Filename} ({file.Id}).";

                _whispers.Show(WhisperSeverity.Success, "File uploaded.");

                await RefreshFilesAsync(cancellationToken).ConfigureAwait(true);

                SelectedFile = Files.FirstOrDefault(f => string.Equals(f.Id, file.Id, StringComparison.Ordinal));

            }
            else
            {

                LastError = result.ErrorMessage ?? "Upload failed.";

                _whispers.Show(WhisperSeverity.Error, "Upload failed.");

                _foundryFloor.AppendLine($"Files & Batches upload failed: {LastError}");

            }

        }
        catch (Exception ex)
        {

            LastError = ex.Message;

            _whispers.Show(WhisperSeverity.Error, "Upload failed.");

        }
        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    public async Task DownloadSelectedFileAsync(CancellationToken cancellationToken)
    {

        if (SelectedFile is null)
        {

            return;

        }

        string suggested = string.IsNullOrWhiteSpace(SelectedFile.Filename)
            ? SelectedFile.Id
            : SelectedFile.Filename;

        string? path = await _fileDialog
            .PickSaveAnyPathAsync(suggested, Path.GetExtension(suggested).TrimStart('.'), cancellationToken)
            .ConfigureAwait(true);

        if (path is null)
        {

            return;

        }

        IsBusy = true;

        LastError = null;

        try
        {

            OpenAiResult<bool> result = await _dataSource
                .DownloadFileContentAsync(SelectedFile.Id, path, cancellationToken)
                .ConfigureAwait(true);

            if (result.Success)
            {

                StatusText = $"Downloaded {SelectedFile.Id}.";

                _whispers.Show(WhisperSeverity.Success, "File downloaded.");

            }
            else
            {

                LastError = result.ErrorMessage ?? "Download failed.";

                _whispers.Show(WhisperSeverity.Error, "Download failed.");

            }

        }
        catch (Exception ex)
        {

            LastError = ex.Message;

            _whispers.Show(WhisperSeverity.Error, "Download failed.");

        }
        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    public async Task DeleteSelectedFileAsync(CancellationToken cancellationToken)
    {

        if (SelectedFile is null)
        {

            return;

        }

        bool confirmed = await _confirmation
            .ConfirmAsync(
                "Delete file",
                $"Delete uploaded file {SelectedFile.Id} ({SelectedFile.Filename})? This removes metadata and on-disk bytes on Arcanum.",
                cancellationToken,
                confirmIsDefault: false)
            .ConfigureAwait(true);

        if (!confirmed)
        {

            return;

        }

        IsBusy = true;

        LastError = null;

        try
        {

            OpenAiResult<OpenAiFileDeleteResponse> result = await _dataSource
                .DeleteFileAsync(SelectedFile.Id, cancellationToken)
                .ConfigureAwait(true);

            if (result.Success)
            {

                StatusText = $"Deleted {SelectedFile.Id}.";

                _whispers.Show(WhisperSeverity.Success, "File deleted.");

                await RefreshFilesAsync(cancellationToken).ConfigureAwait(true);

            }
            else
            {

                LastError = result.ErrorMessage ?? "Delete failed.";

                _whispers.Show(WhisperSeverity.Error, "Delete failed.");

            }

        }
        catch (Exception ex)
        {

            LastError = ex.Message;

            _whispers.Show(WhisperSeverity.Error, "Delete failed.");

        }
        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    public async Task PreviewSelectedFileAsync(CancellationToken cancellationToken)
    {

        if (SelectedFile is null)
        {

            return;

        }

        IsBusy = true;

        LastError = null;

        try
        {

            OpenAiResult<JsonlPreviewResult> result = await _dataSource
                .PreviewFileJsonlAsync(
                    SelectedFile.Id,
                    JsonlBoundedPreview.DefaultMaxLines,
                    JsonlBoundedPreview.DefaultMaxBytes,
                    cancellationToken)
                .ConfigureAwait(true);

            if (result.Success && result.Data is { } preview)
            {

                StringBuilder text = new();

                foreach (string line in preview.Lines)
                {

                    text.AppendLine(line);

                }

                JsonlPreviewText = text.ToString();

                JsonlPreviewTruncated = preview.Truncated;

                StatusText = preview.Truncated
                    ? $"Preview truncated to {preview.Lines.Count} line(s) / {preview.BytesRead} byte(s). Download for full data."
                    : $"Preview {preview.Lines.Count} line(s).";

            }
            else
            {

                JsonlPreviewText = string.Empty;

                JsonlPreviewTruncated = false;

                LastError = result.ErrorMessage ?? "Preview failed.";

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
    public async Task CreateBatchAsync(CancellationToken cancellationToken)
    {

        if (!IsConnected)
        {

            LastError = "Arcanum is disconnected.";

            return;

        }

        string inputFileId = CreateBatchInputFileId.Trim();

        if (string.IsNullOrEmpty(inputFileId))
        {

            LastError = "input_file_id is required.";

            return;

        }

        IsBusy = true;

        LastError = null;

        try
        {

            OpenAiResult<OpenAiBatchObject> result = await _dataSource
                .CreateBatchAsync(
                    inputFileId,
                    FilesBatchesService.DefaultBatchEndpoint,
                    FilesBatchesService.DefaultCompletionWindow,
                    cancellationToken)
                .ConfigureAwait(true);

            if (result.Success && result.Data is { } batch)
            {

                StatusText = $"Created batch {batch.Id} ({batch.Status}).";

                _whispers.Show(WhisperSeverity.Success, "Batch created.");

                await RefreshBatchesAsync(cancellationToken).ConfigureAwait(true);

                SelectedBatch = Batches.FirstOrDefault(b => string.Equals(b.Id, batch.Id, StringComparison.Ordinal));

            }
            else
            {

                LastError = result.ErrorMessage ?? "Create batch failed.";

                _whispers.Show(WhisperSeverity.Error, "Create batch failed.");

            }

        }
        catch (Exception ex)
        {

            LastError = ex.Message;

            _whispers.Show(WhisperSeverity.Error, "Create batch failed.");

        }
        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    public async Task RefreshSelectedBatchAsync(CancellationToken cancellationToken)
    {

        if (SelectedBatch is null)
        {

            return;

        }

        IsBusy = true;

        LastError = null;

        try
        {

            OpenAiResult<OpenAiBatchObject> result = await _dataSource
                .GetBatchAsync(SelectedBatch.Id, cancellationToken)
                .ConfigureAwait(true);

            if (result.Success && result.Data is { } batch)
            {

                ReplaceBatch(batch);

                SelectedBatch = batch;

                StatusText = $"Batch {batch.Id}: {batch.Status}.";

            }
            else
            {

                LastError = result.ErrorMessage ?? "Get batch failed.";

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
    public async Task CancelSelectedBatchAsync(CancellationToken cancellationToken)
    {

        if (SelectedBatch is null)
        {

            return;

        }

        bool confirmed = await _confirmation
            .ConfirmAsync(
                "Cancel batch",
                $"Cancel batch {SelectedBatch.Id}? Cancellation is idempotent on Arcanum.",
                cancellationToken,
                confirmIsDefault: false)
            .ConfigureAwait(true);

        if (!confirmed)
        {

            return;

        }

        IsBusy = true;

        LastError = null;

        try
        {

            OpenAiResult<OpenAiBatchObject> result = await _dataSource
                .CancelBatchAsync(SelectedBatch.Id, cancellationToken)
                .ConfigureAwait(true);

            if (result.Success && result.Data is { } batch)
            {

                ReplaceBatch(batch);

                SelectedBatch = batch;

                StatusText = $"Batch {batch.Id}: {batch.Status}.";

                _whispers.Show(WhisperSeverity.Success, "Batch cancelled.");

            }
            else
            {

                LastError = result.ErrorMessage ?? "Cancel failed.";

                _whispers.Show(WhisperSeverity.Error, "Cancel failed.");

            }

        }
        catch (Exception ex)
        {

            LastError = ex.Message;

            _whispers.Show(WhisperSeverity.Error, "Cancel failed.");

        }
        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    public async Task ResetSelectedBatchAsync(CancellationToken cancellationToken)
    {

        if (SelectedBatch is null)
        {

            return;

        }

        bool confirmed = await _confirmation
            .ConfirmAsync(
                "Reset batch",
                $"Reset stuck batch {SelectedBatch.Id} back to validating? This is an Arcanum extension (not OpenAI standard).",
                cancellationToken,
                confirmIsDefault: false)
            .ConfigureAwait(true);

        if (!confirmed)
        {

            return;

        }

        IsBusy = true;

        LastError = null;

        try
        {

            OpenAiResult<OpenAiBatchObject> result = await _dataSource
                .ResetBatchAsync(SelectedBatch.Id, cancellationToken)
                .ConfigureAwait(true);

            if (result.Success && result.Data is { } batch)
            {

                ReplaceBatch(batch);

                SelectedBatch = batch;

                StatusText = $"Batch {batch.Id}: {batch.Status}.";

                _whispers.Show(WhisperSeverity.Success, "Batch reset.");

            }
            else
            {

                LastError = result.ErrorMessage ?? "Reset failed.";

                _whispers.Show(WhisperSeverity.Error, "Reset failed.");

            }

        }
        catch (Exception ex)
        {

            LastError = ex.Message;

            _whispers.Show(WhisperSeverity.Error, "Reset failed.");

        }
        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    public async Task DownloadBatchOutputAsync(CancellationToken cancellationToken)
    {

        if (SelectedBatch?.OutputFileId is not { Length: > 0 } outputId)
        {

            LastError = "Selected batch has no output_file_id yet.";

            return;

        }

        await DownloadByFileIdAsync(outputId, $"{SelectedBatch.Id}-output.jsonl", cancellationToken)
            .ConfigureAwait(true);

    }

    [RelayCommand]
    public async Task DownloadBatchErrorAsync(CancellationToken cancellationToken)
    {

        if (SelectedBatch?.ErrorFileId is not { Length: > 0 } errorId)
        {

            LastError = "Selected batch has no error_file_id.";

            return;

        }

        await DownloadByFileIdAsync(errorId, $"{SelectedBatch.Id}-error.jsonl", cancellationToken)
            .ConfigureAwait(true);

    }

    public void Dispose()
    {

        if (_disposed)
        {

            return;

        }

        _disposed = true;

        _connection.PropertyChanged -= OnConnectionPropertyChanged;

        StopPolling();

        GC.SuppressFinalize(this);

    }

    private async Task DownloadByFileIdAsync(string fileId, string suggestedName, CancellationToken cancellationToken)
    {

        string? path = await _fileDialog
            .PickSaveAnyPathAsync(suggestedName, "jsonl", cancellationToken)
            .ConfigureAwait(true);

        if (path is null)
        {

            return;

        }

        IsBusy = true;

        LastError = null;

        try
        {

            OpenAiResult<bool> result = await _dataSource
                .DownloadFileContentAsync(fileId, path, cancellationToken)
                .ConfigureAwait(true);

            if (result.Success)
            {

                StatusText = $"Downloaded {fileId}.";

                _whispers.Show(WhisperSeverity.Success, "Download complete.");

            }
            else
            {

                LastError = result.ErrorMessage ?? "Download failed.";

                _whispers.Show(WhisperSeverity.Error, "Download failed.");

            }

        }
        catch (Exception ex)
        {

            LastError = ex.Message;

            _whispers.Show(WhisperSeverity.Error, "Download failed.");

        }
        finally
        {

            IsBusy = false;

        }

    }

    private void ReplaceBatch(OpenAiBatchObject batch)
    {

        for (int index = 0; index < Batches.Count; index++)
        {

            if (string.Equals(Batches[index].Id, batch.Id, StringComparison.Ordinal))
            {

                Batches[index] = batch;

                return;

            }

        }

        Batches.Insert(0, batch);

        OnPropertyChanged(nameof(HasNoBatches));

    }

    private void OnConnectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {

        if (e.PropertyName is not (nameof(IArcanumConnection.State)))
        {

            return;

        }

        OnPropertyChanged(nameof(IsConnected));

        SyncPolling();

        if (!IsConnected)
        {

            StatusText = "Arcanum disconnected — polling stopped.";

        }

    }

    private void SyncPolling()
    {

        if (IsVisible && IsConnected)
        {

            StartPolling();

        }
        else
        {

            StopPolling();

        }

    }

    private void StartPolling()
    {

        StopPolling();

        _pollCts = new CancellationTokenSource();

        CancellationToken token = _pollCts.Token;

        _ = PollLoopAsync(token);

    }

    private void StopPolling()
    {

        if (_pollCts is null)
        {

            return;

        }

        _pollCts.Cancel();

        _pollCts.Dispose();

        _pollCts = null;

    }

    /// <summary>True when a poll CTS is active (tests assert dispose/stop behavior).</summary>
    internal bool IsPolling => _pollCts is not null;

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {

        try
        {

            while (!cancellationToken.IsCancellationRequested)
            {

                await RefreshBatchesAsync(cancellationToken).ConfigureAwait(true);

                await Task.Delay(TimeSpan.FromSeconds(PollIntervalSeconds), cancellationToken).ConfigureAwait(true);

            }

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            // Visibility / disconnect gated stop.

        }

    }

    private static string? NullIfWhiteSpace(string text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();

}
