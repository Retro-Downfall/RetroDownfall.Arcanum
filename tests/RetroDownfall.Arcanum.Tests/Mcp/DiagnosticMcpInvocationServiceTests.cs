using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Mcp;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Support;
using Xunit;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class DiagnosticMcpInvocationServiceTests
{

    [Fact]
    public async Task BlockedTool_ReturnsDiagnosticBlocked_ForEveryForbiddenArt()
    {

        DiagnosticMcpInvocationService service = CreateService();

        foreach (string blocked in DiagnosticMcpInvocationService.BlockedToolNames)
        {

            Result<DiagnosticMcpInvocationOutcome> result = await service
                .InvokeAsync(blocked, default, null, null, CancellationToken.None);

            Assert.True(result.IsFailure);
            Assert.Equal(ErrorCodes.Mcp.DiagnosticBlocked, result.Error.Code);
            Assert.Contains("Forbidden Art", result.Error.Message);

        }

    }

    [Fact]
    public async Task EmptyToolName_ReturnsInvalidBody()
    {

        DiagnosticMcpInvocationService service = CreateService();

        Result<DiagnosticMcpInvocationOutcome> result = await service
            .InvokeAsync("  ", default, null, null, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Validation.InvalidBody, result.Error.Code);

    }

    [Fact]
    public async Task ServerNotRunning_ReturnsServerNotRunning()
    {

        FakeMcpConnectionManager manager = new();
        manager.AddServer("stopped-srv", "stopped", ["echo"]);
        DiagnosticMcpInvocationService service = CreateService(manager);

        Result<DiagnosticMcpInvocationOutcome> result = await service
            .InvokeAsync("echo", default, "stopped-srv", null, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Mcp.ServerNotRunning, result.Error.Code);

    }

    [Fact]
    public async Task InternalServer_IsFilteredOut_EvenWhenNamed()
    {

        FakeMcpConnectionManager manager = new();
        manager.AddServer(DiagnosticMcpInvocationService.InternalServerName, "running", ["execute_command"]);
        DiagnosticMcpInvocationService service = CreateService(manager);

        Result<DiagnosticMcpInvocationOutcome> result = await service
            .InvokeAsync("execute_command", default, DiagnosticMcpInvocationService.InternalServerName, null, CancellationToken.None);

        // execute_command is a Forbidden Art -> blocked before the server lookup even runs.
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Mcp.DiagnosticBlocked, result.Error.Code);

    }

    [Fact]
    public async Task UntrustedWorkspace_HidesWorkspaceServer_TreatedAsServerNotFound()
    {

        // GetServerStatusesAsync simulates the real manager: untrusted workspace-local servers are
        // hidden, so an explicit serverName that is not visible resolves to ServerNotFound.
        FakeMcpConnectionManager manager = new();
        manager.AddServer("external-global", "running", ["echo"]);
        manager.WorkspaceVisibleServers = new HashSet<string> { "external-global" }; // workspace-local hidden
        DiagnosticMcpInvocationService service = CreateService(manager);

        Result<DiagnosticMcpInvocationOutcome> result = await service
            .InvokeAsync("echo", default, "workspace-local-srv", "/untrusted/ws", CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Mcp.ServerNotFound, result.Error.Code);

    }

    [Fact]
    public async Task AmbiguousTool_WithoutServerName_ReturnsAmbiguousTool()
    {

        FakeMcpConnectionManager manager = new();
        manager.AddServer("srv-a", "running", ["echo"]);
        manager.AddServer("srv-b", "running", ["echo"]);
        DiagnosticMcpInvocationService service = CreateService(manager);

        Result<DiagnosticMcpInvocationOutcome> result = await service
            .InvokeAsync("echo", default, null, null, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Mcp.AmbiguousTool, result.Error.Code);
        Assert.Contains("srv-a", result.Error.Message);
        Assert.Contains("srv-b", result.Error.Message);

    }

    [Fact]
    public async Task ToolNotFound_WhenNoVisibleServerExposesIt()
    {

        FakeMcpConnectionManager manager = new();
        manager.AddServer("srv-a", "running", ["other"]);
        DiagnosticMcpInvocationService service = CreateService(manager);

        Result<DiagnosticMcpInvocationOutcome> result = await service
            .InvokeAsync("echo", default, null, null, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Mcp.ToolNotFound, result.Error.Code);

    }

    [Fact]
    public async Task NamedServerMissingTool_ReturnsToolNotFound()
    {

        FakeMcpConnectionManager manager = new();
        manager.AddServer("srv-a", "running", ["other"]);
        DiagnosticMcpInvocationService service = CreateService(manager);

        Result<DiagnosticMcpInvocationOutcome> result = await service
            .InvokeAsync("echo", default, "srv-a", null, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Mcp.ToolNotFound, result.Error.Code);

    }

    [Fact]
    public async Task HappyPath_InvokesExternalTool_AndReturnsResult()
    {

        FakeMcpConnectionManager manager = new();
        manager.AddServer("srv-a", "running", ["echo"]);
        manager.AddTool("echo", "{\"ok\":true}");
        DiagnosticMcpInvocationService service = CreateService(manager);

        Result<DiagnosticMcpInvocationOutcome> result = await service
            .InvokeAsync("echo", default, "srv-a", null, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("srv-a", result.Value.ServerName);
        Assert.Equal("echo", result.Value.ToolName);
        Assert.True(result.Value.DurationMs >= 0);
        Assert.False(result.Value.Truncated);
        Assert.Equal(JsonValueKind.Object, result.Value.Result.ValueKind);
        Assert.True(result.Value.Result.GetProperty("ok").GetBoolean());

    }

    [Fact]
    public async Task TruncationMarker_SetsTruncatedFlag()
    {

        FakeMcpConnectionManager manager = new();
        manager.AddServer("srv-a", "running", ["echo"]);
        manager.AddTool("echo", "partial output [truncated: exceeded 1048576 bytes]");
        DiagnosticMcpInvocationService service = CreateService(manager);

        Result<DiagnosticMcpInvocationOutcome> result = await service
            .InvokeAsync("echo", default, "srv-a", null, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.Truncated);

    }

    [Fact]
    public async Task ToolError_ReturnsToolErrorCode()
    {

        FakeMcpConnectionManager manager = new();
        manager.AddServer("srv-a", "running", ["boom"]);
        manager.AddTool("boom", throws: new InvalidOperationException("the tool reported isError: true"));
        DiagnosticMcpInvocationService service = CreateService(manager);

        Result<DiagnosticMcpInvocationOutcome> result = await service
            .InvokeAsync("boom", default, "srv-a", null, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Mcp.ToolError, result.Error.Code);

    }

    [Fact]
    public async Task Timeout_ReturnsDiagnosticTimeout()
    {

        FakeMcpConnectionManager manager = new();
        manager.AddServer("srv-a", "running", ["slow"]);
        manager.AddTool("slow", throws: new OperationCanceledException(), delay: TimeSpan.FromSeconds(2));
        ArcanumSettings settings = new() { Mcp = new McpSettings { RequestTimeoutSeconds = 1 } };
        DiagnosticMcpInvocationService service = CreateService(manager, settings);

        Result<DiagnosticMcpInvocationOutcome> result = await service
            .InvokeAsync("slow", default, "srv-a", null, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Mcp.DiagnosticTimeout, result.Error.Code);

    }

    [Fact]
    public async Task NonJsonOutput_IsWrappedAsString()
    {

        FakeMcpConnectionManager manager = new();
        manager.AddServer("srv-a", "running", ["echo"]);
        manager.AddTool("echo", "plain text not json");
        DiagnosticMcpInvocationService service = CreateService(manager);

        Result<DiagnosticMcpInvocationOutcome> result = await service
            .InvokeAsync("echo", default, "srv-a", null, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(JsonValueKind.String, result.Value.Result.ValueKind);
        Assert.Equal("plain text not json", result.Value.Result.GetString());

    }

    private static DiagnosticMcpInvocationService CreateService(FakeMcpConnectionManager? manager = null, ArcanumSettings? settings = null) =>
        new(
            manager ?? new FakeMcpConnectionManager(),
            new TestOptionsMonitor<ArcanumSettings>(settings ?? new ArcanumSettings()),
            NullLogger<DiagnosticMcpInvocationService>.Instance);

    private sealed class FakeMcpConnectionManager : IMcpConnectionManager
    {

        private readonly Dictionary<string, (string Status, List<string> Tools)> _servers = new(StringComparer.Ordinal);

        private readonly Dictionary<string, FakeAIFunction> _tools = new(StringComparer.Ordinal);

        /// <summary>When set, only these server names are returned by GetServerStatusesAsync (simulates untrusted workspace hiding locals).</summary>
        public ISet<string> WorkspaceVisibleServers { get; set; } = new HashSet<string>(StringComparer.Ordinal);

        public void AddServer(string name, string status, IEnumerable<string> tools) =>
            _servers[name] = (status, tools.ToList());

        public void AddTool(string name, string? output = null, Exception? throws = null, TimeSpan delay = default) =>
            _tools[name] = new FakeAIFunction(name, output, throws, delay);

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<Result> StartAsync(string name, string? workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> StopAsync(string name, string? workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> RestartAsync(string name, string? workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task<McpServerInfo?> GetStatusAsync(string name, string? workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult<McpServerInfo?>(null);

        public Task<McpServerInfo[]> GetAllStatusesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Array.Empty<McpServerInfo>());

        public Task<IReadOnlyList<AITool>> GetAvailableToolsAsync(string? workingDirectory, CancellationToken cancellationToken = default)
        {

            List<AITool> tools = _tools.Values.OfType<AITool>().ToList();

            return Task.FromResult<IReadOnlyList<AITool>>(tools);

        }

        public Task<List<McpServerStatusDto>> GetServerStatusesAsync(string workingDirectory, CancellationToken cancellationToken = default)
        {

            IEnumerable<KeyValuePair<string, (string Status, List<string> Tools)>> visible = _servers;

            if (!string.IsNullOrEmpty(workingDirectory) && WorkspaceVisibleServers.Count > 0)
            {

                visible = _servers.Where(kv => WorkspaceVisibleServers.Contains(kv.Key));

            }

            List<McpServerStatusDto> dtos = visible
                .Select(kv => new McpServerStatusDto(kv.Key, kv.Value.Status, kv.Value.Tools.Count, kv.Value.Tools, null))
                .ToList();

            return Task.FromResult(dtos);

        }

        public Task ReloadAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<Result> TrustWorkspaceAsync(string workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

    }

    private sealed class FakeAIFunction : AIFunction
    {

        private readonly string? _output;

        private readonly Exception? _throws;

        private readonly TimeSpan _delay;

        public FakeAIFunction(string name, string? output, Exception? throws, TimeSpan delay)
        {

            Name = name;

            _output = output;

            _throws = throws;

            _delay = delay;

        }

        public override string Name { get; }

        public override string Description => "fake";

        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {

            if (_delay > TimeSpan.Zero)
            {

                await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);

            }

            if (_throws is not null)
            {

                throw _throws;

            }

            return _output;

        }

    }

}
