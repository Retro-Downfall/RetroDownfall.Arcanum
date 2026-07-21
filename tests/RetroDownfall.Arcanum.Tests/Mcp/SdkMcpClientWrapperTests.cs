using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Events;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Platform;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Platform;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Mcp;

/// <summary>
/// End-to-end coverage of the ModelContextProtocol SDK migration's production wiring:
/// <see cref="ChannelClientTransport"/> carrying a real SDK <see cref="McpClient"/> session against the
/// unmodified <see cref="ArcanumInternalToolServer"/>, bridged through <see cref="SdkMcpClientWrapper"/>.
/// </summary>
public sealed class SdkMcpClientWrapperTests : IAsyncLifetime
{
    private TempWorkspace _workspace = null!;

    public async Task InitializeAsync()
    {
        _workspace = new TempWorkspace();

        await _workspace.InitializeAsync();

        _workspace.WriteFile("notes/alpha.txt", "line one\nline two\nline three");
    }

    public async Task DisposeAsync()
    {
        await _workspace.DisposeAsync();
    }

    [Fact]
    public async Task GetToolsAsync_lists_core_tools_via_real_sdk_session()
    {
        await using (SdkMcpClientWrapper client = await CreateInitializedClientAsync())
        {
            IReadOnlyList<McpBridgeTool> tools = await client.GetToolsAsync();

            string[] names = tools.Select(static t => t.Name).ToArray();

            Assert.Contains("read_file_chunk", names);

            Assert.Contains("execute_command", names);

            Assert.Contains("ask_human", names);
        }
    }

    [Fact]
    public async Task CallToolAsync_read_file_chunk_returns_expected_text_via_real_sdk_session()
    {
        await using (SdkMcpClientWrapper client = await CreateInitializedClientAsync())
        {
            CallToolResult result = await client.CallToolAsync(
                "read_file_chunk",
                new Dictionary<string, object?>
                {
                    ["relativePath"] = "notes/alpha.txt",
                    ["startLine"] = 1,
                    ["endLine"] = 2,
                });

            Assert.False(result.IsError);

            string text = McpToolResultFormatter.FormatContentText(result);

            Assert.Contains("line one", text, StringComparison.Ordinal);

            Assert.Contains("line two", text, StringComparison.Ordinal);

            Assert.DoesNotContain("line three", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task CallToolAsync_unknown_tool_returns_isError_via_real_sdk_session()
    {
        await using (SdkMcpClientWrapper client = await CreateInitializedClientAsync())
        {
            CallToolResult result = await client.CallToolAsync(
                "nonexistent_tool_xyz",
                new Dictionary<string, object?>());

            Assert.True(result.IsError);
        }
    }

    [Fact]
    public async Task GetToolsAsync_enforces_max_tools_per_server_cap_via_real_sdk_session()
    {
        await using (SdkMcpClientWrapper client = await CreateInitializedClientAsync(maxToolsPerServer: 2))
        {
            IReadOnlyList<McpBridgeTool> tools = await client.GetToolsAsync();

            Assert.True(tools.Count <= 2, $"Expected at most 2 tools, got {tools.Count}.");
        }
    }

    [Fact]
    public async Task CallToolAsync_after_dispose_throws_ObjectDisposedException()
    {
        SdkMcpClientWrapper client = await CreateInitializedClientAsync();

        await client.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => client.CallToolAsync("read_file_chunk", new Dictionary<string, object?>()));
    }

    private async Task<SdkMcpClientWrapper> CreateInitializedClientAsync(int maxToolsPerServer = 256)
    {
        string normalizedRoot = Path.GetFullPath(_workspace.Root);

        IntelligenceSettings settings = new() { EnableLexiconSystem = false, EnableArchiveSearch = false };

        ServiceCollection services = new();

        services.AddSingleton<ISanctumGuard, PermissiveSanctumGuard>();

        services.AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<ArcanumSettings>>(
            new TestOptionsMonitor<ArcanumSettings>(
                new ArcanumSettings
                {
                    Security = new SecuritySettings { AllowUnsandboxedToolChildren = true },
                }));

        services.AddSingleton<IProcessResourceLimiter, ProcessResourceLimiter>();

        IServiceScopeFactory scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        IHumanPromptRegistry humanPrompts = new HumanPromptRegistry();

        IUnseenServantPacer pacer = new UnseenServantPacer(
            new FakeEventBus(),
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()),
            scopeFactory,
            NullLogger<UnseenServantPacer>.Instance);

        (ChannelClientTransport clientTransport, _) = CreateChannelClientTransport(
            humanPrompts,
            scopeFactory,
            pacer,
            normalizedRoot,
            settings);

        SdkMcpClientWrapper client = new(
            clientTransport,
            new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "arcanum-tests", Version = "1.0.0" },
            },
            defaultRequestTimeout: TimeSpan.FromSeconds(10),
            maxToolsListPages: 8,
            toolOutputCapBytes: 65536,
            maxToolsPerServer: maxToolsPerServer,
            maxToolsPerListPage: 64,
            maxToolsTotalBytes: 1_048_576);

        await client.InitializeAsync();

        return client;
    }

    private static (ChannelClientTransport ClientTransport, ArcanumInternalToolServer Server) CreateChannelClientTransport(
        IHumanPromptRegistry humanPromptRegistry,
        IServiceScopeFactory scopeFactory,
        IUnseenServantPacer pacer,
        string workspaceRoot,
        IntelligenceSettings intelligenceSettings)
    {
        (System.Threading.Channels.ChannelWriter<string> toServer, System.Threading.Channels.ChannelReader<string> fromServer, ArcanumInternalToolServer server) =
            InProcessMcpTransport.CreateServerChannelPair(
                humanPromptRegistry,
                scopeFactory,
                pacer,
                workspaceRoot,
                executeCommandTimeout: TimeSpan.FromSeconds(30),
                executeCommandTimeoutSecondsForDisplay: 30,
                listDirectoryMaxPaths: 64,
                intelligenceSettings: intelligenceSettings,
                maxFileReadSizeBytes: 1024 * 1024,
            conclaveEnabled: false,
            sagaEnabled: false,
            a2aClientEnabled: false,
            attachmentsToolEnabled: false,
            maxJsonRpcLineBytes: 2_097_152,
            logger: NullLogger<ArcanumInternalToolServer>.Instance,
            allowHostProcessTools: true);

        _ = Task.Run(() => server.RunAsync(CancellationToken.None));

        ChannelClientTransport clientTransport = new(
            toServer,
            fromServer,
            maxJsonRpcLineBytes: 2_097_152,
            ambientConnectionKey: server.AmbientConnectionKey);

        return (clientTransport, server);
    }

    private sealed class FakeEventBus : IEventBus
    {
        public void Publish<T>(T @event) where T : notnull
        {
        }

        public IAsyncEnumerable<T> Subscribe<T>(CancellationToken cancellationToken) where T : notnull =>
            AsyncEnumerable.Empty<T>();
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

        public Task<ResourceLimits> GetEffectiveResourceLimitsForWorkspaceAsync(string? workspaceRoot, CancellationToken ct = default) =>
            Task.FromResult(new ResourceLimits());

        
        public Task<SanctumChildProcessBoundary?> GetChildProcessBoundaryForWorkspaceAsync(
            string? workspaceRoot,
            CancellationToken ct = default) =>
            Task.FromResult<SanctumChildProcessBoundary?>(null);

public Task RecordResourceLimitBreachAsync(
            string? workspaceRoot,
            string toolName,
            ResourceLimitKind resource,
            string limitValue,
            string? actualValue,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
