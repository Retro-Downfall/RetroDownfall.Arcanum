using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Events;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class McpConnectionManagerBootstrapIdempotencyTests : IAsyncLifetime
{

    private McpConnectionManager _manager = null!;

    private MutableOptionsMonitor _settings = null!;

    public Task InitializeAsync()
    {

        _settings = new MutableOptionsMonitor(
            new ArcanumSettings
            {
                Security = new SecuritySettings
                {
                    AllowUnsandboxedToolChildren = true,
                },
            });

        ServiceCollection services = new();

        services.AddSingleton<ISanctumGuard, PermissiveSanctumGuard>();

        services.AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<ArcanumSettings>>(
            _settings);

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
            _settings);

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

    [Fact]
    public async Task Initial_settings_fingerprint_does_not_retire_bootstrapped_global_partition()
    {

        await _manager.InitializeAsync();
        TrackingMcpClient bootstrappedClient = new();
        AddClientToPrivatePartition(
            _manager,
            "__arcanum_mcp_global__",
            bootstrappedClient);

        _ = await _manager.GetAvailableToolsAsync(
            workingDirectory: null);

        Assert.Equal(0, bootstrappedClient.DisposeCount);

    }

    [Fact]
    public async Task Equivalent_settings_keep_generation_and_workspace_check_change_rebuilds_surface()
    {

        await using TempWorkspace workspace = new();
        await workspace.InitializeAsync();

        AIFunction before = await GetToolAsync(
            workspace.Root,
            ToolRiskClassifier.SearchWorkspaceToolName);

        WorkspaceCheckIntegrationSettings current =
            _settings.CurrentValue.Integrations.WorkspaceChecks;
        _settings.CurrentValue = _settings.CurrentValue with
        {
            Integrations = _settings.CurrentValue.Integrations with
            {
                WorkspaceChecks = current with { },
            },
        };

        AIFunction equivalent = await GetToolAsync(
            workspace.Root,
            ToolRiskClassifier.SearchWorkspaceToolName);
        Assert.Same(before, equivalent);

        _settings.CurrentValue = _settings.CurrentValue with
        {
            Integrations = _settings.CurrentValue.Integrations with
            {
                WorkspaceChecks = current with
                {
                    ExecutableCatalog = current.ExecutableCatalog with
                    {
                        DotNet = current.ExecutableCatalog.DotNet with
                        {
                            Path = "dotnet-test",
                        },
                    },
                },
            },
        };

        AIFunction changed = await GetToolAsync(
            workspace.Root,
            ToolRiskClassifier.SearchWorkspaceToolName);

        Assert.NotSame(equivalent, changed);

    }

    [Fact]
    public async Task Active_apply_patch_survives_settings_generation_reload()
    {

        await using TempWorkspace workspace = new();
        await workspace.InitializeAsync();
        workspace.WriteFile("active-reload.txt", "before\n");

        AIFunction applyPatch = await GetToolAsync(
            workspace.Root,
            ToolRiskClassifier.ApplyPatchToolName);
        BlockingCommittedReceiptSink sink = new();
        ApplyPatchInvocationContext context = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new ToolInvocationIdentity(
                Guid.NewGuid().ToString("N"),
                "active-reload-call",
                ToolRoundOrdinal: 0,
                CallOrdinal: 0,
                ToolRiskClassifier.ApplyPatchToolName),
            "{\"patch\":\"active-reload\"}",
            "test-model",
            DateTimeOffset.UtcNow,
            sink);

        using IDisposable scope =
            ApplyPatchInvocationAmbient.Begin(context);
        Task<object?> invocation = applyPatch.InvokeAsync(
            new AIFunctionArguments(
                new Dictionary<string, object?>
                {
                    ["patch"] =
                        "--- a/active-reload.txt\n"
                        + "+++ b/active-reload.txt\n"
                        + "@@ -1 +1 @@\n"
                        + "-before\n"
                        + "+after\n",
                    ["dryRun"] = false,
                }),
            CancellationToken.None).AsTask();

        await sink.HandoffStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        _settings.CurrentValue = _settings.CurrentValue with
        {
            Features = _settings.CurrentValue.Features with
            {
                WorkspaceChecks = false,
            },
        };

        _ = await _manager.GetAvailableToolsAsync(
            workspace.Root);

        Assert.False(invocation.IsCompleted);

        sink.ReleaseHandoff();
        TrustedStructuredToolResult result =
            Assert.IsType<TrustedStructuredToolResult>(
                await invocation.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains(
            "\"status\":\"ok\"",
            result.Text,
            StringComparison.Ordinal);
        Assert.True(context.ReceiptHandled);
        Assert.Equal(
            "after\n",
            await File.ReadAllTextAsync(
                Path.Combine(
                    workspace.Root,
                    "active-reload.txt")));

    }

    [Fact]
    public async Task Explicit_reload_drains_active_apply_patch_before_generation_disposal()
    {

        await using TempWorkspace workspace = new();
        await workspace.InitializeAsync();
        workspace.WriteFile("explicit-reload.txt", "before\n");

        AIFunction applyPatch = await GetToolAsync(
            workspace.Root,
            ToolRiskClassifier.ApplyPatchToolName);
        BlockingCommittedReceiptSink sink = new();
        ApplyPatchInvocationContext context = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new ToolInvocationIdentity(
                Guid.NewGuid().ToString("N"),
                "explicit-reload-call",
                ToolRoundOrdinal: 0,
                CallOrdinal: 0,
                ToolRiskClassifier.ApplyPatchToolName),
            "{\"patch\":\"explicit-reload\"}",
            "test-model",
            DateTimeOffset.UtcNow,
            sink);

        using IDisposable scope =
            ApplyPatchInvocationAmbient.Begin(context);
        Task<object?> invocation = applyPatch.InvokeAsync(
            new AIFunctionArguments(
                new Dictionary<string, object?>
                {
                    ["patch"] =
                        "--- a/explicit-reload.txt\n"
                        + "+++ b/explicit-reload.txt\n"
                        + "@@ -1 +1 @@\n"
                        + "-before\n"
                        + "+after\n",
                    ["dryRun"] = false,
                }),
            CancellationToken.None).AsTask();

        await sink.HandoffStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        Task reload = _manager.ReloadAsync(
            workspace.Root);
        await Task.Delay(50);

        Assert.False(reload.IsCompleted);
        Assert.False(invocation.IsCompleted);

        sink.ReleaseHandoff();
        TrustedStructuredToolResult result =
            Assert.IsType<TrustedStructuredToolResult>(
                await invocation.WaitAsync(
                    TimeSpan.FromSeconds(5)));
        await reload.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(
            "\"status\":\"ok\"",
            result.Text,
            StringComparison.Ordinal);
        Assert.True(context.ReceiptHandled);
        Assert.Equal(
            "after\n",
            await File.ReadAllTextAsync(
                Path.Combine(
                    workspace.Root,
                    "explicit-reload.txt")));

    }

    private async Task<AIFunction> GetToolAsync(
        string workspaceRoot,
        string toolName)
    {

        IReadOnlyList<AITool> tools =
            await _manager.GetAvailableToolsAsync(workspaceRoot);

        return Assert.IsAssignableFrom<AIFunction>(
            tools.Single(
                tool => string.Equals(
                    tool.Name,
                    toolName,
                    StringComparison.Ordinal)));

    }

    private static void AddClientToPrivatePartition(
        McpConnectionManager manager,
        string partitionKey,
        IMcpClient client)
    {

        System.Reflection.FieldInfo? field =
            typeof(McpConnectionManager).GetField(
                "_partitionClients",
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        object? partitions = field!.GetValue(manager);
        Assert.NotNull(partitions);
        System.Reflection.PropertyInfo? indexer =
            partitions!.GetType().GetProperty("Item");
        Assert.NotNull(indexer);
        object? lazy = indexer!.GetValue(
            partitions,
            [partitionKey]);
        Assert.NotNull(lazy);
        object? partition =
            lazy!.GetType().GetProperty("Value")?.GetValue(lazy);
        Assert.NotNull(partition);
        object? clients =
            partition!.GetType().GetProperty("Clients")?.GetValue(
                partition);
        Assert.NotNull(clients);
        System.Reflection.MethodInfo? add =
            clients!.GetType().GetMethod("Add");
        Assert.NotNull(add);

        _ = add!.Invoke(clients, [client]);

    }

    private sealed class AlwaysTrustedWorkspaceStore : ITrustedMcpWorkspaceStore
    {

        public Task<bool> IsTrustedAsync(string workspaceRootPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task TrustAsync(string workspaceRootPath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

    }

    private sealed class MutableOptionsMonitor(ArcanumSettings current)
        : Microsoft.Extensions.Options.IOptionsMonitor<ArcanumSettings>
    {
        public ArcanumSettings CurrentValue { get; set; } = current;

        public ArcanumSettings Get(string? name) => CurrentValue;

        public IDisposable OnChange(
            Action<ArcanumSettings, string?> listener) =>
            new NoopDisposable();
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class BlockingCommittedReceiptSink
        : IApplyPatchPendingReceiptSink
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource HandoffStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<ApplyPatchReceiptProbeResult> ProbeAsync(
            ApplyPatchReceiptProbe probe,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                new ApplyPatchReceiptProbeResult(
                    ApplyPatchReceiptProbeOutcome.NotFound,
                    SerializedResult: null));

        public ValueTask<ApplyPatchReceiptPreflightResult> PreflightAsync(
            ApplyPatchReceiptPreflight preflight,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                new ApplyPatchReceiptPreflightResult(
                    ApplyPatchReceiptPreflightOutcome.Admitted,
                    SerializedResult: null));

        public ValueTask<MandatoryToolInteractionAppendOutcome>
            PersistRecoveryReceiptAsync(
                ApplyPatchRecoveryReceipt receipt,
                CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                MandatoryToolInteractionAppendOutcome.NewlyCommitted);

        public async ValueTask<ApplyPatchPendingReceiptHandoffResult>
            HandoffAsync(
                PendingApplyPatchReceipt receipt,
                CancellationToken cancellationToken)
        {

            HandoffStarted.TrySetResult();
            await _release.Task.WaitAsync(
                cancellationToken);
            WorkspaceArtifactCleanupResult cleanup =
                await receipt.MarkIrreversibleAsync(
                    CancellationToken.None);

            return new ApplyPatchPendingReceiptHandoffResult(
                MandatoryToolInteractionAppendOutcome.NewlyCommitted,
                cleanup,
                Rollback: null);
        }

        public void ReleaseHandoff() =>
            _release.TrySetResult();
    }

    private sealed class TrackingMcpClient : IMcpClient
    {
        public int DisposeCount { get; private set; }

        public Task InitializeAsync(
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<McpBridgeTool>> GetToolsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<McpBridgeTool>>([]);

        public Task<ModelContextProtocol.Protocol.CallToolResult>
            CallToolAsync(
                string toolName,
                IReadOnlyDictionary<string, object?> arguments,
                TimeSpan? requestTimeout = null,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
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
