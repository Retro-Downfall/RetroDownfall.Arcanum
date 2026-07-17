using Microsoft.Extensions.AI;
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

public sealed class McpConnectionManagerBootstrapIdempotencyTests : IAsyncLifetime
{

    private McpConnectionManager _manager = null!;

    public Task InitializeAsync()
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

        _manager = new McpConnectionManager(
            NullLogger<McpConnectionManager>.Instance,
            humanPrompts,
            scopeFactory,
            pacer,
            new FakeEventBus(),
            new AlwaysTrustedWorkspaceStore(),
            new FakeHttpClientFactory(),
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

        return Task.CompletedTask;

    }

    public async Task DisposeAsync()
    {

        await _manager.DisposeAsync();

    }

    [Fact]
    public async Task ConcurrentInitializeAsync_CompletesOnce_WithoutThrowing()
    {

        Task first = _manager.InitializeAsync();

        Task second = _manager.InitializeAsync();

        await Task.WhenAll(first, second);

        await _manager.InitializeAsync();

        IReadOnlyList<AITool> tools = await _manager.GetAvailableToolsAsync(workingDirectory: null);

        Assert.NotNull(tools);

    }

    private sealed class AlwaysTrustedWorkspaceStore : ITrustedMcpWorkspaceStore
    {

        public Task<bool> IsTrustedAsync(string workspaceRootPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

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
