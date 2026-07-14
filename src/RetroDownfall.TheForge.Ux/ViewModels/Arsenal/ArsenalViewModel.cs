using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.ViewModels.Reliquary;

namespace RetroDownfall.TheForge.Ux.ViewModels.Arsenal;

/// <summary>
/// The Arsenal — operational panel for MCP servers, built-in tool invocation (The Scrying Pool),
/// models &amp; providers, and local LlamaCpp management (The Reliquary). A tab container that
/// refreshes its children when Arcanum connects.
/// </summary>
public sealed partial class ArsenalViewModel : ViewModelBase, IDisposable
{

    private readonly IArcanumConnection _connection;

    private readonly McpServersViewModel _mcpServers;

    private readonly ScryingPoolViewModel _scryingPool;

    private readonly ModelsProvidersViewModel _modelsProviders;

    private readonly ReliquaryViewModel _reliquary;

    private bool _disposed;

    public ArsenalViewModel(
        IArcanumConnection connection,
        McpServersViewModel mcpServers,
        ScryingPoolViewModel scryingPool,
        ModelsProvidersViewModel modelsProviders,
        ReliquaryViewModel reliquary)
    {

        _connection = connection;

        _mcpServers = mcpServers;

        _scryingPool = scryingPool;

        _modelsProviders = modelsProviders;

        _reliquary = reliquary;

        Title = "The Arsenal";

        _connection.PropertyChanged += OnConnectionPropertyChanged;

        if (_connection.State == ConnectionState.Connected)
        {

            TaskUtilities.FireAndForget(RefreshAsync(CancellationToken.None));

        }

    }

    public McpServersViewModel McpServers => _mcpServers;

    public ScryingPoolViewModel ScryingPool => _scryingPool;

    public ModelsProvidersViewModel ModelsProviders => _modelsProviders;

    public ReliquaryViewModel Reliquary => _reliquary;

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {

        await Task.WhenAll(
            _mcpServers.RefreshAsync(cancellationToken),
            _scryingPool.RefreshAsync(cancellationToken),
            _modelsProviders.RefreshAsync(cancellationToken),
            _reliquary.RefreshAsync(cancellationToken)).ConfigureAwait(true);

    }

    public void Dispose()
    {

        if (_disposed)
        {

            return;

        }

        _disposed = true;

        _connection.PropertyChanged -= OnConnectionPropertyChanged;

        _reliquary.Dispose();

        GC.SuppressFinalize(this);

    }

    private void OnConnectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {

        if (e.PropertyName == nameof(IArcanumConnection.State) && _connection.State == ConnectionState.Connected)
        {

            TaskUtilities.FireAndForget(RefreshAsync(CancellationToken.None));

        }

    }

}
