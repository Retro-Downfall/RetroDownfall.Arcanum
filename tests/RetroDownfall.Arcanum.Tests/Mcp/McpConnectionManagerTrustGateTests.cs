using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Events;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class McpConnectionManagerTrustGateTests : IAsyncLifetime
{

    private TempWorkspace _workspace = null!;

    private McpConnectionManager _manager = null!;

    public async Task InitializeAsync()
    {

        _workspace = new TempWorkspace();

        await _workspace.InitializeAsync();

        _workspace.WriteFile(
            "mcp.json",
            """
            {
              "mcpServers": {
                "untrusted-local": {
                  "command": "echo",
                  "args": ["trusted-gate-test"]
                }
              }
            }
            """);

        ServiceCollection services = new();

        services.AddSingleton<ISanctumGuard, PermissiveSanctumGuard>();

        IServiceScopeFactory scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        IHumanPromptRegistry humanPrompts = new HumanPromptRegistry();

        IUnseenServantPacer pacer = new UnseenServantPacer(
            new FakeEventBus(),
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

        _manager = new McpConnectionManager(
            NullLogger<McpConnectionManager>.Instance,
            humanPrompts,
            scopeFactory,
            pacer,
            new FakeEventBus(),
            new UntrustedWorkspaceStore(),
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

    }

    public async Task DisposeAsync()
    {

        await _manager.DisposeAsync();

        await _workspace.DisposeAsync();

    }

    [Fact]
    public async Task GetAvailableToolsAsync_untrusted_workspace_does_not_register_local_servers()
    {

        await _manager.GetAvailableToolsAsync(_workspace.Root);

        McpServerInfo? status = await _manager.GetStatusAsync("untrusted-local", _workspace.Root);

        Assert.Null(status);

    }

    private sealed class UntrustedWorkspaceStore : ITrustedMcpWorkspaceStore
    {

        public Task<bool> IsTrustedAsync(string workspaceRootPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task TrustAsync(string workspaceRootPath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

    }

    private sealed class PermissiveSanctumGuard : ISanctumGuard
    {

        public Task<SanctumResult> ValidatePathAsync(
            string campaignId,
            string requestedPath,
            string operationType,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<SanctumResult> ValidateNetworkAsync(
            string campaignId,
            string url,
            string toolName,
            CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<SanctumResult> ValidateToolAsync(string campaignId, string toolName, CancellationToken ct = default) =>
            Task.FromResult(new SanctumResult { Allowed = true });

        public Task<IReadOnlyList<SanctumBreach>> GetBreachesAsync(
            string campaignId,
            int limit = 100,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SanctumBreach>>([]);

        public Task<ResourceLimits> GetEffectiveResourceLimitsForWorkspaceAsync(
            string? workspaceRoot,
            CancellationToken ct = default) =>
            Task.FromResult(new ResourceLimits());

    }

    private sealed class FakeEventBus : IEventBus
    {

        public void Publish<T>(T @event) where T : notnull
        {
        }

        public IAsyncEnumerable<T> Subscribe<T>(CancellationToken cancellationToken) where T : notnull =>
            AsyncEnumerable.Empty<T>();

    }

}
