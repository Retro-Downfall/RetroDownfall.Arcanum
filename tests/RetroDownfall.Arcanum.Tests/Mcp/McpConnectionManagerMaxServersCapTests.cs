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

public sealed class McpConnectionManagerMaxServersCapTests : IAsyncLifetime
{

    private McpConnectionManager _manager = null!;

    public Task InitializeAsync()
    {

        ArcanumSettings settings = new()
        {

            Mcp = new McpSettings { MaxServers = 4 },

        };

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
            new TestOptionsMonitor<ArcanumSettings>(settings));

        return Task.CompletedTask;

    }

    public async Task DisposeAsync()
    {

        await _manager.DisposeAsync();

    }

    // W3.3 Fix 2: the MaxServers count-check + registry add must be serialized.
    // The workspace-build path registered without holding _registryLock, so N
    // parallel registrations could all pass the count check and overshoot the cap.
    // The fix wraps the register+count-check in _registryLock. A Barrier releases
    // all registrations onto the seam at the same instant so the check-then-add
    // window is actually contested; with MaxServers=K < N, at most K servers may
    // be registered.
    [Fact]
    public async Task RegisterFromConfigAsync_ConcurrentRegistrations_NeverOvershootsMaxServers()
    {

        const int maxServers = 4;

        const int registrationCount = 16;

        using Barrier barrier = new(registrationCount);

        McpConfig[] configs = Enumerable.Range(0, registrationCount)
            .Select(i => new McpConfig
            {

                McpServers = new Dictionary<string, McpServerConfig>
                {

                    [$"server-{i}"] = new() { Command = "echo", Args = [$"arg-{i}"] },

                },

            })
            .ToArray();

        Task[] tasks = Enumerable.Range(0, registrationCount)
            .Select(i => Task.Run(async () =>
            {

                barrier.SignalAndWait();

                await _manager.RegisterFromConfigAsync(configs[i], scopeWorkingDirectory: null, CancellationToken.None);

            }))
            .ToArray();

        await Task.WhenAll(tasks);

        McpServerInfo[] statuses = await _manager.GetAllStatusesAsync(CancellationToken.None);

        Assert.True(statuses.Length <= maxServers, $"Overshot MaxServers: {statuses.Length} registered > {maxServers}.");

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
