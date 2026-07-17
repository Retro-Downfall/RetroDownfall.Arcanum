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
            new UntrustedWorkspaceStore(),
            new FakeHttpClientFactory(),
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

    // Regression guard: a canceled StartAsync (e.g. the caller's HTTP request aborted mid-handshake)
    // must reset the entry off McpServerState.Starting rather than leaving it stuck there forever,
    // which would otherwise make every future StartAsync call for this entry short-circuit into a
    // false "already starting" success (see the state check at the top of StartAsync) without ever
    // actually starting anything.
    [Fact]
    public async Task StartAsync_CanceledDuringHandshake_ResetsEntryState_NotStuckStarting()
    {

        // /bin/sleep is spawned directly (an absolute path, bypassing PATH resolution, which MCP
        // subprocesses do not inherit by default) and never speaks the MCP JSON-RPC handshake, so
        // InitializeAsync hangs until this test's own cancellation fires.
        McpConfig config = new()
        {
            McpServers = new Dictionary<string, McpServerConfig>
            {
                ["hang-server"] = new() { Command = "/bin/sleep", Args = ["30"] },
            },
        };

        await _manager.RegisterFromConfigAsync(config, scopeWorkingDirectory: null, CancellationToken.None);

        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _manager.StartAsync("hang-server", null, cts.Token));

        McpServerInfo? status = await _manager.GetStatusAsync("hang-server", null, CancellationToken.None);

        Assert.NotNull(status);

        Assert.NotEqual(McpServerState.Starting, status!.State);

        Assert.Equal(McpServerState.Error, status.State);

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
