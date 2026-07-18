using System.Text.Json;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Ux.ViewModels.Arsenal;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class ArsenalViewModelTests
{

    [Fact]
    public async Task McpServers_Refresh_PopulatesCards()
    {

        FakeArsenalDataSource dataSource = new()
        {

            Servers =
            [

                NewServer("filesystem", McpServerState.Running, ["fs.read"]),

                NewServer("git", McpServerState.Stopped, []),

            ],

        };

        McpServersViewModel viewModel = NewMcpServers(dataSource);

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.Equal(2, viewModel.Servers.Count);

        Assert.Equal("filesystem", viewModel.Servers[0].Name);

        Assert.Equal("Running", viewModel.Servers[0].StateText);

        Assert.Equal("Stopped", viewModel.Servers[1].StateText);

    }

    [Fact]
    public async Task McpServers_Start_CallsDataSourceWithSelectedServer()
    {

        FakeArsenalDataSource dataSource = new()
        {

            Servers = [NewServer("filesystem", McpServerState.Stopped, [])],

        };

        McpServersViewModel viewModel = NewMcpServers(dataSource);

        await viewModel.RefreshAsync(CancellationToken.None);

        viewModel.SelectedServer = viewModel.Servers[0];

        await viewModel.StartAsync(CancellationToken.None);

        Assert.Equal("filesystem", dataSource.LastStartName);

        Assert.Equal(1, dataSource.StartCallCount);

        Assert.Null(viewModel.LastError);

    }

    [Fact]
    public async Task McpServers_StopAndRestart_CallDataSource()
    {

        FakeArsenalDataSource dataSource = new()
        {

            Servers = [NewServer("filesystem", McpServerState.Running, [])],

        };

        McpServersViewModel viewModel = NewMcpServers(dataSource);

        await viewModel.RefreshAsync(CancellationToken.None);

        viewModel.SelectedServer = viewModel.Servers[0];

        await viewModel.StopAsync(CancellationToken.None);

        await viewModel.RestartAsync(CancellationToken.None);

        Assert.Equal("filesystem", dataSource.LastStopName);

        Assert.Equal("filesystem", dataSource.LastRestartName);

    }

    [Fact]
    public async Task McpServers_Reload_SurfacesSuccessAndFailure()
    {

        FakeArsenalDataSource dataSource = new()
        {

            ReloadResult = (true, null),

            Servers = [NewServer("s", McpServerState.Running, [])],

        };

        McpServersViewModel viewModel = NewMcpServers(dataSource);

        await viewModel.ReloadAsync(CancellationToken.None);

        Assert.Equal("MCP configuration reloaded.", viewModel.StatusText);

        dataSource.ReloadResult = (false, "bad config");

        await viewModel.ReloadAsync(CancellationToken.None);

        Assert.Equal("Reload failed.", viewModel.StatusText);

        Assert.Equal("bad config", viewModel.LastError);

    }

    [Fact]
    public async Task McpServers_StartWithoutSelection_SetsStatusTextAndDoesNotCall()
    {

        FakeArsenalDataSource dataSource = new();

        McpServersViewModel viewModel = NewMcpServers(dataSource);

        await viewModel.StartAsync(CancellationToken.None);

        Assert.Equal("Select a server first.", viewModel.StatusText);

        Assert.Equal(0, dataSource.StartCallCount);

    }

    [Fact]
    public async Task McpServers_RefreshWhenListFails_SetsLastErrorNotEmptyMessage()
    {

        FakeArsenalDataSource dataSource = new() { ListError = "upstream 503" };

        McpServersViewModel viewModel = NewMcpServers(dataSource);

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.Equal("upstream 503", viewModel.LastError);

        Assert.Equal("Failed to load MCP servers.", viewModel.StatusText);

        Assert.Empty(viewModel.Servers);

    }

    [Fact]
    public async Task McpServers_RefreshWhenListThrows_SetsLastErrorAndDoesNotThrow()
    {

        FakeArsenalDataSource dataSource = new() { ThrowOnList = true };

        McpServersViewModel viewModel = NewMcpServers(dataSource);

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(viewModel.LastError));

    }

    [Fact]
    public async Task ScryingPool_Refresh_PopulatesNativeTools()
    {

        FakeArsenalDataSource dataSource = new()
        {

            Arsenal = new WorkspaceArsenalDto([], ["fs.read", "fs.write"], [], []),

        };

        ScryingPoolViewModel viewModel = NewScryingPool(dataSource);

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.Equal(2, viewModel.NativeTools.Count);

        Assert.Equal("fs.read", viewModel.NativeTools[0]);

    }

    [Fact]
    public async Task ScryingPool_RefreshWhenArsenalFails_SetsLastErrorNotEmptyToolsMessage()
    {

        FakeArsenalDataSource dataSource = new() { ArsenalError = "arsenal unavailable" };

        ScryingPoolViewModel viewModel = NewScryingPool(dataSource);

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.Equal("arsenal unavailable", viewModel.LastError);

        Assert.Equal("Failed to load built-in tools.", viewModel.StatusText);

        Assert.Empty(viewModel.NativeTools);

    }

    [Fact]
    public async Task ScryingPool_Invoke_SendsToolNameAndArgumentsAndSurfacesResult()
    {

        FakeArsenalDataSource dataSource = new()
        {

            InvokeResult = new ToolInvokeResponse(JsonDocument.Parse("""{"ok":true}""").RootElement.Clone()),

        };

        ScryingPoolViewModel viewModel = NewScryingPool(dataSource);

        viewModel.SelectedTool = "fs.read";

        viewModel.ArgumentsText = """{"path":"/tmp"}""";

        await viewModel.InvokeAsync(CancellationToken.None);

        Assert.NotNull(dataSource.LastInvokeRequest);

        Assert.Equal("fs.read", dataSource.LastInvokeRequest!.ToolName);

        Assert.Equal("/tmp", dataSource.LastInvokeRequest.Arguments.GetProperty("path").GetString());

        Assert.Equal("""{"ok":true}""", viewModel.ResultText);

        Assert.Null(viewModel.LastError);

    }

    [Fact]
    public async Task ScryingPool_InvokeWithoutSelection_SetsStatusText()
    {

        ScryingPoolViewModel viewModel = NewScryingPool(new FakeArsenalDataSource());

        await viewModel.InvokeAsync(CancellationToken.None);

        Assert.Equal("Select a built-in tool first.", viewModel.StatusText);

    }

    [Fact]
    public async Task ScryingPool_InvokeWithInvalidJson_SetsLastError()
    {

        FakeArsenalDataSource dataSource = new();

        ScryingPoolViewModel viewModel = NewScryingPool(dataSource);

        viewModel.SelectedTool = "fs.read";

        viewModel.ArgumentsText = "not-json";

        await viewModel.InvokeAsync(CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(viewModel.LastError));

        Assert.Null(dataSource.LastInvokeRequest);

    }

    [Fact]
    public async Task McpServers_StartFailure_SurfacesDataSourceError()
    {

        FakeArsenalDataSource dataSource = new()
        {

            Servers = [NewServer("filesystem", McpServerState.Stopped, [])],

            StartResult = (false, "[Mcp.ServerNotFound] filesystem is not configured."),

        };

        McpServersViewModel viewModel = NewMcpServers(dataSource);

        await viewModel.RefreshAsync(CancellationToken.None);

        viewModel.SelectedServer = viewModel.Servers[0];

        await viewModel.StartAsync(CancellationToken.None);

        Assert.Equal("[Mcp.ServerNotFound] filesystem is not configured.", viewModel.LastError);

        Assert.Equal("filesystem: start failed.", viewModel.StatusText);

    }

    [Fact]
    public async Task ScryingPool_InvokeFailure_SurfacesApiErrorText()
    {

        FakeArsenalDataSource dataSource = new()
        {

            InvokeError = "[Hub.Error] tool blew up",

        };

        ScryingPoolViewModel viewModel = NewScryingPool(dataSource);

        viewModel.SelectedTool = "fs.read";

        await viewModel.InvokeAsync(CancellationToken.None);

        Assert.Equal("[Hub.Error] tool blew up", viewModel.LastError);

        Assert.Equal("[Hub.Error] tool blew up", viewModel.ResultText);

        Assert.Equal("Invocation failed.", viewModel.StatusText);

    }

    private static McpServersViewModel NewMcpServers(FakeArsenalDataSource dataSource) =>
        new(dataSource, new FoundryFloorViewModel(new NullLogService()));

    private static ScryingPoolViewModel NewScryingPool(FakeArsenalDataSource dataSource) =>
        new(dataSource, new FoundryFloorViewModel(new NullLogService()));

    private static McpServerInfo NewServer(string name, McpServerState state, string[] tools) =>
        new(name, null, McpServerTransport.Stdio, false, "npx", [], null, state, null, tools, null);

    private sealed class FakeArsenalDataSource : IArsenalDataSource
    {

        public IReadOnlyList<McpServerInfo> Servers { get; init; } = [];

        public (bool Ok, string? Error) StartResult { get; init; } = (true, null);

        public (bool Ok, string? Error) StopResult { get; init; } = (true, null);

        public (bool Ok, string? Error) RestartResult { get; init; } = (true, null);

        public (bool Success, string? Error) ReloadResult { get; set; } = (true, null);

        public WorkspaceArsenalDto? Arsenal { get; init; }

        public string? ListError { get; init; }

        public string? ArsenalError { get; init; }

        public ToolInvokeResponse? InvokeResult { get; init; }

        public string? InvokeError { get; init; }

        public bool ThrowOnList { get; init; }

        public string? LastStartName { get; private set; }

        public string? LastStopName { get; private set; }

        public string? LastRestartName { get; private set; }

        public ToolInvokeRequest? LastInvokeRequest { get; private set; }

        public int StartCallCount { get; private set; }

        public Task<(IReadOnlyList<McpServerInfo>? Servers, string? Error)> ListMcpServersAsync(CancellationToken cancellationToken)
        {

            if (ThrowOnList)
            {

                throw new InvalidOperationException("boom");

            }

            if (ListError is not null)
            {

                return Task.FromResult<(IReadOnlyList<McpServerInfo>?, string?)>((null, ListError));

            }

            return Task.FromResult<(IReadOnlyList<McpServerInfo>?, string?)>((Servers, null));

        }

        public Task<(bool Ok, string? Error)> StartServerAsync(string name, CancellationToken cancellationToken)
        {

            LastStartName = name;

            StartCallCount++;

            return Task.FromResult(StartResult);

        }

        public Task<(bool Ok, string? Error)> StopServerAsync(string name, CancellationToken cancellationToken)
        {

            LastStopName = name;

            return Task.FromResult(StopResult);

        }

        public Task<(bool Ok, string? Error)> RestartServerAsync(string name, CancellationToken cancellationToken)
        {

            LastRestartName = name;

            return Task.FromResult(RestartResult);

        }

        public Task<(bool Success, string? Error)> ReloadMcpAsync(string? workingDirectory, CancellationToken cancellationToken) =>
            Task.FromResult(ReloadResult);

        public Task<(WorkspaceArsenalDto? Arsenal, string? Error)> GetArsenalAsync(string? workingDirectory, CancellationToken cancellationToken)
        {

            if (ArsenalError is not null)
            {

                return Task.FromResult<(WorkspaceArsenalDto?, string?)>((null, ArsenalError));

            }

            return Task.FromResult<(WorkspaceArsenalDto?, string?)>((Arsenal, null));

        }

        public Task<(ToolInvokeResponse? Response, string? Error)> InvokeToolAsync(ToolInvokeRequest request, CancellationToken cancellationToken)
        {

            LastInvokeRequest = request;

            if (InvokeError is not null)
            {

                return Task.FromResult<(ToolInvokeResponse?, string?)>((null, InvokeError));

            }

            return Task.FromResult<(ToolInvokeResponse?, string?)>((InvokeResult, null));

        }

        public Task<(McpToolInvokeResponse? Response, string? Error)> InvokeDiagnosticMcpAsync(McpToolInvokeRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<(McpToolInvokeResponse?, string?)>((null, "Not implemented."));

    }

}
