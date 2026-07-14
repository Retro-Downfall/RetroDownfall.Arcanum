using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.LlamaCpp;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;

namespace RetroDownfall.TheForge.Ux.ViewModels.Reliquary;

/// <summary>
/// The Reliquary — local LlamaCpp management: cached GGUF models, llama-server status, and NDJSON
/// model pull. Pull progress is appended here and to the Foundry Floor; Stop cancels an in-flight pull.
/// </summary>
public sealed partial class ReliquaryViewModel : ViewModelBase, IDisposable
{

    private readonly IReliquaryDataSource _dataSource;

    private readonly FoundryFloorViewModel _foundryFloor;

    private readonly CancellationTokenSource _lifetimeCts = new();

    private CancellationTokenSource? _pullCts;

    private bool _disposed;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isPulling;

    [ObservableProperty]
    private string? _lastError;

    [ObservableProperty]
    private string? _statusText;

    public ObservableCollection<CachedModelInfo> CachedModels { get; } = [];

    public ObservableCollection<LlamaServerInfo> Servers { get; } = [];

    [ObservableProperty]
    private CachedModelInfo? _selectedModel;

    [ObservableProperty]
    private string _pullSourceUrl = string.Empty;

    public ObservableCollection<string> PullLines { get; } = [];

    public ReliquaryViewModel(IReliquaryDataSource dataSource, FoundryFloorViewModel foundryFloor)
    {

        _dataSource = dataSource;

        _foundryFloor = foundryFloor;

        Title = "The Reliquary";

    }

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {

        IsBusy = true;

        LastError = null;

        try
        {

            IReadOnlyList<CachedModelInfo> models = await _dataSource.ListCachedModelsAsync(cancellationToken).ConfigureAwait(true);

            IReadOnlyList<LlamaServerInfo> servers = await _dataSource.ListServersAsync(cancellationToken).ConfigureAwait(true);

            CachedModels.Clear();

            foreach (CachedModelInfo model in models)
            {

                CachedModels.Add(model);

            }

            Servers.Clear();

            foreach (LlamaServerInfo server in servers)
            {

                Servers.Add(server);

            }

            StatusText = $"{models.Count} cached model(s), {servers.Count} server(s).";

        }

        catch (Exception ex)
        {

            LastError = ex.Message;

            _foundryFloor.AppendLine($"Reliquary refresh error: {ex.Message}");

        }

        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    public async Task PullAsync(CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(PullSourceUrl))
        {

            StatusText = "Enter a model source URL.";

            return;

        }

        _pullCts?.Cancel();

        _pullCts?.Dispose();

        _pullCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCts.Token);

        CancellationToken pullToken = _pullCts.Token;

        IsPulling = true;

        LastError = null;

        PullLines.Clear();

        try
        {

            PullModelRequestDto request = new() { SourceUrl = PullSourceUrl };

            await foreach (LlamaPullProgress progress in _dataSource.PullModelAsync(request, pullToken).ConfigureAwait(true))
            {

                string line = FormatProgressLine(progress);

                PullLines.Add(line);

                _foundryFloor.AppendLine($"Reliquary: {line}");

            }

            if (!pullToken.IsCancellationRequested)
            {

                await RefreshAsync(CancellationToken.None).ConfigureAwait(true);

            }

            StatusText = pullToken.IsCancellationRequested ? "Pull cancelled." : "Pull complete.";

        }

        catch (OperationCanceledException) when (pullToken.IsCancellationRequested)
        {

            StatusText = "Pull cancelled.";

        }

        catch (Exception ex)
        {

            LastError = ex.Message;

            _foundryFloor.AppendLine($"Reliquary pull error: {ex.Message}");

        }

        finally
        {

            IsPulling = false;

        }

    }

    [RelayCommand]
    private void CancelPull()
    {

        _pullCts?.Cancel();

    }

    [RelayCommand]
    public async Task StartServerAsync(CancellationToken cancellationToken)
    {

        if (SelectedModel is not { } model)
        {

            StatusText = "Select a cached model first.";

            return;

        }

        IsBusy = true;

        LastError = null;

        try
        {

            LlamaServerInfo? server = await _dataSource.StartServerAsync(model.CacheKey, cancellationToken).ConfigureAwait(true);

            StatusText = server is not null ? $"{model.CacheKey}: start sent." : $"{model.CacheKey}: start failed.";

            if (server is null)
            {

                LastError = $"Start failed for '{model.CacheKey}'.";

                _foundryFloor.AppendLine($"Reliquary start failed: {model.CacheKey}");

            }

            await RefreshAsync(cancellationToken).ConfigureAwait(true);

        }

        catch (Exception ex)
        {

            LastError = ex.Message;

            _foundryFloor.AppendLine($"Reliquary start error: {ex.Message}");

        }

        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    public async Task StopServerAsync(CancellationToken cancellationToken)
    {

        if (SelectedModel is not { } model)
        {

            StatusText = "Select a cached model first.";

            return;

        }

        IsBusy = true;

        LastError = null;

        try
        {

            bool ok = await _dataSource.StopServerAsync(model.CacheKey, cancellationToken).ConfigureAwait(true);

            StatusText = ok ? $"{model.CacheKey}: stop sent." : $"{model.CacheKey}: stop failed.";

            if (!ok)
            {

                LastError = $"Stop failed for '{model.CacheKey}'.";

                _foundryFloor.AppendLine($"Reliquary stop failed: {model.CacheKey}");

            }

            await RefreshAsync(cancellationToken).ConfigureAwait(true);

        }

        catch (Exception ex)
        {

            LastError = ex.Message;

            _foundryFloor.AppendLine($"Reliquary stop error: {ex.Message}");

        }

        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    public async Task WarmupServerAsync(CancellationToken cancellationToken)
    {

        if (SelectedModel is not { } model)
        {

            StatusText = "Select a cached model first.";

            return;

        }

        IsBusy = true;

        LastError = null;

        try
        {

            WarmupResultDto? result = await _dataSource.WarmupServerAsync(model.CacheKey, cancellationToken).ConfigureAwait(true);

            StatusText = result is { Success: true }
                ? $"{model.CacheKey}: warmup ok ({result.LatencyMs} ms)."
                : $"{model.CacheKey}: warmup failed.";

            if (result is not { Success: true })
            {

                LastError = $"Warmup failed for '{model.CacheKey}'.";

                _foundryFloor.AppendLine($"Reliquary warmup failed: {model.CacheKey}");

            }

        }

        catch (Exception ex)
        {

            LastError = ex.Message;

            _foundryFloor.AppendLine($"Reliquary warmup error: {ex.Message}");

        }

        finally
        {

            IsBusy = false;

        }

    }

    public void Dispose()
    {

        if (_disposed)
        {

            return;

        }

        _disposed = true;

        _pullCts?.Cancel();

        _pullCts?.Dispose();

        _lifetimeCts.Dispose();

        GC.SuppressFinalize(this);

    }

    private static string FormatProgressLine(LlamaPullProgress progress)
    {

        if (!string.IsNullOrWhiteSpace(progress.Error))
        {

            return $"error: {progress.Error}";

        }

        if (!string.IsNullOrWhiteSpace(progress.Warning))
        {

            return $"warning: {progress.Warning}";

        }

        if (progress.Completed)
        {

            return $"completed: {progress.CacheKey}";

        }

        return progress.Percent is { } percent
            ? $"pulling {progress.CacheKey}: {percent:0.#}%"
            : $"pulling {progress.CacheKey}: {progress.BytesDownloaded} bytes";

    }

}
