using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Events;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class McpConnectionManagerTrustGateTests : IAsyncLifetime
{

    private TempWorkspace _workspace = null!;

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

    }

    public async Task DisposeAsync()
    {

        await _workspace.DisposeAsync();

    }

    [Fact]
    public async Task GetAvailableToolsAsync_untrusted_workspace_does_not_register_local_servers()
    {

        await using McpConnectionManager manager = CreateManager(new UntrustedWorkspaceStore());

        await manager.GetAvailableToolsAsync(_workspace.Root);

        McpServerInfo? status = await manager.GetStatusAsync("untrusted-local", _workspace.Root);

        Assert.Null(status);

    }

    [Fact]
    public async Task RestartAsync_after_trust_revoke_returns_WorkspaceNotTrusted()
    {

        ToggleableTrustStore trust = new() { Trusted = true };

        await using McpConnectionManager manager = CreateManager(trust);

        await manager.RegisterFromConfigAsync(
            new McpConfig
            {
                McpServers = new Dictionary<string, McpServerConfig>
                {
                    ["local-restart"] = new McpServerConfig
                    {
                        Command = "arcanum-nonexistent-binary-zzz",
                    },
                },
            },
            scopeWorkingDirectory: _workspace.Root,
            CancellationToken.None);

        Result start = await manager.StartAsync("local-restart", _workspace.Root);

        Assert.True(start.IsFailure);

        Assert.Equal("Mcp.StartFailed", start.Error.Code);

        McpServerInfo? afterStart = await manager.GetStatusAsync("local-restart", _workspace.Root);

        Assert.NotNull(afterStart);

        Assert.Equal(McpServerState.Error, afterStart!.State);

        trust.Trusted = false;

        Result restart = await manager.RestartAsync("local-restart", _workspace.Root);

        Assert.True(restart.IsFailure);

        Assert.Equal("Mcp.WorkspaceNotTrusted", restart.Error.Code);

        Assert.Contains("trust-workspace", restart.Error.Message, StringComparison.Ordinal);

    }

    private McpConnectionManager CreateManager(ITrustedMcpWorkspaceStore trustStore)
    {

        ServiceCollection services = new();

        services.AddSingleton<ISanctumGuard, PermissiveSanctumGuard>();

        services.AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<ArcanumSettings>>(
            new TestOptionsMonitor<ArcanumSettings>(
                new ArcanumSettings
                {
                    Security = new SecuritySettings { AllowUnsandboxedToolChildren = true },
                }));

        IServiceScopeFactory scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        IHumanPromptRegistry humanPrompts = new HumanPromptRegistry();

        IUnseenServantPacer pacer = new UnseenServantPacer(
            new FakeEventBus(),
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()),
            scopeFactory,
            NullLogger<UnseenServantPacer>.Instance);

        return new McpConnectionManager(
            NullLogger<McpConnectionManager>.Instance,
            humanPrompts,
            scopeFactory,
            pacer,
            new FakeEventBus(),
            trustStore,
            new FakeHttpClientFactory(),
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

    }

    private sealed class UntrustedWorkspaceStore : ITrustedMcpWorkspaceStore
    {

        public Task<bool> IsTrustedAsync(string workspaceRootPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task TrustAsync(string workspaceRootPath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

    }

    private sealed class ToggleableTrustStore : ITrustedMcpWorkspaceStore
    {

        public bool Trusted { get; set; }

        public Task<bool> IsTrustedAsync(string workspaceRootPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(Trusted);

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

        public Task<ResourceLimits> GetEffectiveResourceLimitsForWorkspaceAsync(
            string? workspaceRoot,
            CancellationToken ct = default) =>
            Task.FromResult(new ResourceLimits());

        public Task<SanctumChildProcessBoundary?> GetChildProcessBoundaryForWorkspaceAsync(
            string? workspaceRoot,
            CancellationToken ct = default) =>
            Task.FromResult<SanctumChildProcessBoundary?>(null);

        public Task RecordResourceLimitBreachAsync(
            string? workspaceRoot,
            string toolName,
            Core.Platform.ResourceLimitKind resource,
            string limitValue,
            string? actualValue,
            CancellationToken ct = default) =>
            Task.CompletedTask;

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
