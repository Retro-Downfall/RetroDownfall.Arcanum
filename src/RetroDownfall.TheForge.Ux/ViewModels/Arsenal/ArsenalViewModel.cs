using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;

namespace RetroDownfall.TheForge.Ux.ViewModels.Arsenal;

/// <summary>
/// The Arsenal — operational panel for MCP servers, built-in tool invocation (The Scrying Pool),
/// and models &amp; providers. A tab container that refreshes its children when Arcanum connects.
/// </summary>
public sealed partial class ArsenalViewModel : ViewModelBase, IDisposable
{

    private readonly IArcanumConnection _connection;

    private readonly McpServersViewModel _mcpServers;

    private readonly ScryingPoolViewModel _scryingPool;

    private readonly DiagnosticMcpInvocationViewModel _diagnosticMcp;

    private readonly ModelsProvidersViewModel _modelsProviders;

    private bool _disposed;

    public ArsenalViewModel(
        IArcanumConnection connection,
        McpServersViewModel mcpServers,
        ScryingPoolViewModel scryingPool,
        DiagnosticMcpInvocationViewModel diagnosticMcp,
        ModelsProvidersViewModel modelsProviders)
    {

        _connection = connection;

        _mcpServers = mcpServers;

        _scryingPool = scryingPool;

        _diagnosticMcp = diagnosticMcp;

        _modelsProviders = modelsProviders;

        Title = "The Arsenal";

        _connection.PropertyChanged += OnConnectionPropertyChanged;

        if (_connection.State == ConnectionState.Connected)
        {

            TaskUtilities.FireAndForget(RefreshAsync(CancellationToken.None));

        }

    }

    public McpServersViewModel McpServers => _mcpServers;

    public ScryingPoolViewModel ScryingPool => _scryingPool;

    public DiagnosticMcpInvocationViewModel DiagnosticMcp => _diagnosticMcp;

    public ModelsProvidersViewModel ModelsProviders => _modelsProviders;

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {

        await Task.WhenAll(
            _mcpServers.RefreshAsync(cancellationToken),
            _scryingPool.RefreshAsync(cancellationToken),
            _diagnosticMcp.RefreshAsync(cancellationToken),
            _modelsProviders.RefreshAsync(cancellationToken)).ConfigureAwait(true);

    }

    public void Dispose()
    {

        if (_disposed)
        {

            return;

        }

        _disposed = true;

        _connection.PropertyChanged -= OnConnectionPropertyChanged;

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
