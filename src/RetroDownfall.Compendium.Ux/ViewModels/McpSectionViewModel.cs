using CommunityToolkit.Mvvm.ComponentModel;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Compendium.Ux.ViewModels;

public sealed partial class McpSectionViewModel : ObservableObject
{

    [ObservableProperty] private int _requestTimeoutSeconds;

    [ObservableProperty] private int _maxPaginationPages;

    [ObservableProperty] private bool _bootstrapBlocksStartup;

    [ObservableProperty] private int _maxServers;

    [ObservableProperty] private int _maxToolsPerServer;

    [ObservableProperty] private int _maxToolsPerListPage;

    [ObservableProperty] private int _maxToolsTotalBytes;

    [ObservableProperty] private int _maxJsonRpcLineBytes;

    [ObservableProperty] private int _httpRequestTimeoutSeconds;

    [ObservableProperty] private string _allowedHttpHosts = string.Empty;

    private McpSettings _snapshot = new();

    public void LoadFrom(McpSettings settings)
    {

        _snapshot = settings;

        RequestTimeoutSeconds = settings.RequestTimeoutSeconds;

        MaxPaginationPages = settings.MaxPaginationPages;

        BootstrapBlocksStartup = settings.BootstrapBlocksStartup;

        MaxServers = settings.MaxServers;

        MaxToolsPerServer = settings.MaxToolsPerServer;

        MaxToolsPerListPage = settings.MaxToolsPerListPage;

        MaxToolsTotalBytes = settings.MaxToolsTotalBytes;

        MaxJsonRpcLineBytes = settings.MaxJsonRpcLineBytes;

        HttpRequestTimeoutSeconds = settings.HttpRequestTimeoutSeconds;

        AllowedHttpHosts = settings.AllowedHttpHosts.JoinCsv();

    }

    public McpSettings Build() => _snapshot with
    {

        RequestTimeoutSeconds = RequestTimeoutSeconds,

        MaxPaginationPages = MaxPaginationPages,

        BootstrapBlocksStartup = BootstrapBlocksStartup,

        MaxServers = MaxServers,

        MaxToolsPerServer = MaxToolsPerServer,

        MaxToolsPerListPage = MaxToolsPerListPage,

        MaxToolsTotalBytes = MaxToolsTotalBytes,

        MaxJsonRpcLineBytes = MaxJsonRpcLineBytes,

        HttpRequestTimeoutSeconds = HttpRequestTimeoutSeconds,

        AllowedHttpHosts = AllowedHttpHosts.SplitCsv(),

    };

}
