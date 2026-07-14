using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;

namespace RetroDownfall.TheForge.Ux.ViewModels.Arsenal;

/// <summary>MCP server dashboard tab of The Arsenal: lists servers and runs start/stop/restart/reload.</summary>
public sealed partial class McpServersViewModel : ViewModelBase
{

    private readonly IArsenalDataSource _dataSource;

    private readonly FoundryFloorViewModel _foundryFloor;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _lastError;

    [ObservableProperty]
    private string? _statusText;

    public ObservableCollection<McpServerCardViewModel> Servers { get; } = [];

    [ObservableProperty]
    private McpServerCardViewModel? _selectedServer;

    public McpServersViewModel(IArsenalDataSource dataSource, FoundryFloorViewModel foundryFloor)
    {

        _dataSource = dataSource;

        _foundryFloor = foundryFloor;

        Title = "MCP Servers";

    }

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {

        IsBusy = true;

        LastError = null;

        try
        {

            (IReadOnlyList<McpServerInfo>? servers, string? error) = await _dataSource
                .ListMcpServersAsync(cancellationToken)
                .ConfigureAwait(true);

            if (error is not null)
            {

                LastError = error;

                StatusText = "Failed to load MCP servers.";

                _foundryFloor.AppendLine($"Arsenal MCP refresh error: {error}");

                return;

            }

            Servers.Clear();

            foreach (McpServerInfo server in servers ?? [])
            {

                Servers.Add(new McpServerCardViewModel(server));

            }

            StatusText = Servers.Count == 0 ? "No MCP servers configured." : $"{Servers.Count} server(s).";

        }

        catch (Exception ex)
        {

            LastError = ex.Message;

            StatusText = "Failed to load MCP servers.";

            _foundryFloor.AppendLine($"Arsenal MCP refresh error: {ex.Message}");

        }

        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    public async Task StartAsync(CancellationToken cancellationToken)
    {

        await RunServerActionAsync((name, ct) => _dataSource.StartServerAsync(name, ct), "start", cancellationToken).ConfigureAwait(true);

    }

    [RelayCommand]
    public async Task StopAsync(CancellationToken cancellationToken)
    {

        await RunServerActionAsync((name, ct) => _dataSource.StopServerAsync(name, ct), "stop", cancellationToken).ConfigureAwait(true);

    }

    [RelayCommand]
    public async Task RestartAsync(CancellationToken cancellationToken)
    {

        await RunServerActionAsync((name, ct) => _dataSource.RestartServerAsync(name, ct), "restart", cancellationToken).ConfigureAwait(true);

    }

    [RelayCommand]
    public async Task ReloadAsync(CancellationToken cancellationToken)
    {

        IsBusy = true;

        LastError = null;

        try
        {

            (bool success, string? error) = await _dataSource.ReloadMcpAsync(null, cancellationToken).ConfigureAwait(true);

            await RefreshAsync(cancellationToken).ConfigureAwait(true);

            StatusText = success ? "MCP configuration reloaded." : "Reload failed.";

            if (!success)
            {

                LastError = error ?? "Failed to reload MCP configuration.";

                _foundryFloor.AppendLine($"Arsenal MCP reload error: {LastError}");

            }

        }

        catch (Exception ex)
        {

            LastError = ex.Message;

            _foundryFloor.AppendLine($"Arsenal MCP reload error: {ex.Message}");

        }

        finally
        {

            IsBusy = false;

        }

    }

    private async Task RunServerActionAsync(Func<string, CancellationToken, Task<bool>> action, string verb, CancellationToken cancellationToken)
    {

        if (SelectedServer is not { } server)
        {

            StatusText = "Select a server first.";

            return;

        }

        IsBusy = true;

        LastError = null;

        try
        {

            bool ok = await action(server.Name, cancellationToken).ConfigureAwait(true);

            await RefreshAsync(cancellationToken).ConfigureAwait(true);

            StatusText = ok ? $"{server.Name}: {verb} sent." : $"{server.Name}: {verb} failed.";

            if (!ok)
            {

                LastError = $"{verb} failed for '{server.Name}'.";

                _foundryFloor.AppendLine($"Arsenal MCP {verb} failed: {server.Name}");

            }

        }

        catch (Exception ex)
        {

            LastError = ex.Message;

            _foundryFloor.AppendLine($"Arsenal MCP {verb} error: {ex.Message}");

        }

        finally
        {

            IsBusy = false;

        }

    }

}
