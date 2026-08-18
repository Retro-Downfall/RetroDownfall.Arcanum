using System.Text.Json;
using System.Threading.Channels;
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
    public async Task GetToolsAsync_does_not_truncate_a_server_tool_catalog()
    {
        await using (SdkMcpClientWrapper client = await CreateInitializedClientAsync())
        {
            IReadOnlyList<McpBridgeTool> tools = await client.GetToolsAsync();

            Assert.True(tools.Count > 2, $"Expected the complete tool catalog, got {tools.Count} tools.");
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

    [Fact]
    public async Task GetToolsAsync_stops_paging_once_the_tool_metadata_budget_is_exhausted()
    {
        PagingToolsListServer server = new(descriptionBytes: 2000, maxPages: 400);

        using CancellationTokenSource serverLifetime = new();

        await using SdkMcpClientWrapper client = await CreatePagingClientAsync(
            server,
            maxToolsTotalBytes: 8192,
            serverLifetime.Token);

        _ = await Assert.ThrowsAsync<InvalidDataException>(() => client.GetToolsAsync());

        await serverLifetime.CancelAsync();

        // Each page carries ~2 KiB of tool metadata against an 8 KiB budget, so the fifth page is the
        // one that blows it. Anything materially beyond that means the byte cap is being applied after
        // the whole catalog has already been accumulated instead of bounding the allocation.
        Assert.InRange(server.PagesServed, 1, 8);
    }

    [Fact]
    public async Task GetToolsAsync_stops_paging_once_a_server_keeps_handing_back_pages_that_carry_no_tools()
    {
        PagingToolsListServer server = new(descriptionBytes: 2000, maxPages: 5000, toolsPerPage: 0);

        using CancellationTokenSource serverLifetime = new();

        await using SdkMcpClientWrapper client = await CreatePagingClientAsync(
            server,
            maxToolsTotalBytes: 8192,
            serverLifetime.Token);

        // An empty page advances no byte budget and presents a brand new cursor, so neither the
        // metadata cap nor the cursor-cycle guard can ever end the walk.
        _ = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.GetToolsAsync().WaitAsync(TimeSpan.FromSeconds(60)));

        await serverLifetime.CancelAsync();

        Assert.InRange(server.PagesServed, 1, 256);
    }

    [Fact]
    public async Task GetToolsAsync_stops_paging_once_a_server_keeps_handing_back_pages_of_unnamed_tools()
    {
        PagingToolsListServer server = new(
            descriptionBytes: 2000,
            maxPages: 5000,
            toolsPerPage: 1,
            blankToolNames: true);

        using CancellationTokenSource serverLifetime = new();

        await using SdkMcpClientWrapper client = await CreatePagingClientAsync(
            server,
            maxToolsTotalBytes: 8192,
            serverLifetime.Token);

        // A page whose tools are all skipped for a blank name is byte-for-byte as free as an empty
        // page, so the same unbounded walk applies.
        _ = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.GetToolsAsync().WaitAsync(TimeSpan.FromSeconds(60)));

        await serverLifetime.CancelAsync();

        Assert.InRange(server.PagesServed, 1, 256);
    }

    private async Task<SdkMcpClientWrapper> CreatePagingClientAsync(
        PagingToolsListServer server,
        int maxToolsTotalBytes,
        CancellationToken serverLifetime)
    {
        Channel<string> toServer = Channel.CreateUnbounded<string>();

        Channel<string> fromServer = Channel.CreateUnbounded<string>();

        _ = Task.Run(
            () => server.RunAsync(toServer.Reader, fromServer.Writer, serverLifetime),
            CancellationToken.None);

        ChannelClientTransport clientTransport = new(
            toServer.Writer,
            fromServer.Reader,
            maxJsonRpcLineBytes: 2_097_152);

        SdkMcpClientWrapper client = new(
            clientTransport,
            new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "arcanum-tests", Version = "1.0.0" },
            },
            initializationTimeout: TimeSpan.FromSeconds(10),
            toolOutputCapBytes: 65536,
            maxToolsTotalBytes: maxToolsTotalBytes);

        await client.InitializeAsync();

        return client;
    }

    /// <summary>
    /// A minimal JSON-RPC peer that answers every <c>tools/list</c> with one fresh page and a brand new
    /// cursor, so the cursor-cycle guard never fires and only a byte budget can end the walk.
    /// <paramref name="toolsPerPage"/> of zero (or <paramref name="blankToolNames"/>) serves pages that
    /// carry no accountable tool metadata at all, which is the shape no budget can terminate.
    /// </summary>
    private sealed class PagingToolsListServer(
        int descriptionBytes,
        int maxPages,
        int toolsPerPage = 1,
        bool blankToolNames = false)
    {
        private readonly string _description = new('d', descriptionBytes);

        private int _pagesServed;

        public int PagesServed => Volatile.Read(ref _pagesServed);

        public async Task RunAsync(
            ChannelReader<string> fromClient,
            ChannelWriter<string> toClient,
            CancellationToken cancellationToken)
        {
            await foreach (string line in fromClient.ReadAllAsync(cancellationToken))
            {
                using JsonDocument document = JsonDocument.Parse(line);

                JsonElement root = document.RootElement;

                if (!root.TryGetProperty("method", out JsonElement methodElement)
                    || !root.TryGetProperty("id", out JsonElement idElement))
                {
                    continue;
                }

                string id = idElement.GetRawText();

                string method = methodElement.GetString() ?? string.Empty;

                if (string.Equals(method, "initialize", StringComparison.Ordinal))
                {
                    string protocolVersion =
                        root.TryGetProperty("params", out JsonElement initParams)
                        && initParams.TryGetProperty("protocolVersion", out JsonElement versionElement)
                            ? versionElement.GetString() ?? "2024-11-05"
                            : "2024-11-05";

                    await toClient.WriteAsync(
                        "{\"jsonrpc\":\"2.0\",\"id\":" + id
                        + ",\"result\":{\"protocolVersion\":\"" + protocolVersion
                        + "\",\"capabilities\":{\"tools\":{}},\"serverInfo\":{\"name\":\"paging-fake\",\"version\":\"1.0.0\"}}}\n",
                        cancellationToken);

                    continue;
                }

                if (!string.Equals(method, "tools/list", StringComparison.Ordinal))
                {
                    continue;
                }

                int page = Interlocked.Increment(ref _pagesServed);

                string nextCursor = page >= maxPages ? string.Empty : $",\"nextCursor\":\"p{page}\"";

                string[] tools = new string[toolsPerPage];

                for (int index = 0; index < toolsPerPage; index++)
                {
                    string name = blankToolNames ? string.Empty : $"paged_tool_{page}_{index}";

                    tools[index] =
                        "{\"name\":\"" + name
                        + "\",\"description\":\"" + _description
                        + "\",\"inputSchema\":{\"type\":\"object\"}}";
                }

                await toClient.WriteAsync(
                    "{\"jsonrpc\":\"2.0\",\"id\":" + id
                    + ",\"result\":{\"tools\":[" + string.Join(',', tools) + "]" + nextCursor + "}}\n",
                    cancellationToken);
            }
        }
    }

    private async Task<SdkMcpClientWrapper> CreateInitializedClientAsync()
    {
        string normalizedRoot = Path.GetFullPath(_workspace.Root);

        IntelligenceSettings settings = ArcanumRuntimeDefaults.Intelligence with
        {
            EnableLexiconSystem = false,
            EnableArchiveSearch = false,
        };

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
            initializationTimeout: TimeSpan.FromSeconds(10),
            toolOutputCapBytes: 65536,
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
