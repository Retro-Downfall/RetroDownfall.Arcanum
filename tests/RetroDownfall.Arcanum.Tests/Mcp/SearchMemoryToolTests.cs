using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Intelligence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Events;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Platform;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class SearchMemoryToolTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Advertisement_depends_on_shared_search_service_independently_of_Saga(bool available)
    {
        await using TestMcpSession session = await CreateSessionAsync(available ? new SearchService() : null, sagaEnabled: !available);

        JsonRpcResponse response = await session.SendRequestAsync("tools/list", null);

        McpToolsListResultWire tools = JsonSerializer.Deserialize(response.Result!.Value, McpJsonSerializerContext.Default.McpToolsListResultWire)!;

        Assert.Equal(available, tools.Tools.Any(tool => tool.Name == "search_memory"));

        Assert.DoesNotContain(tools.Tools, tool => tool.Name == "read_saga");

        if (!available)
        {
            McpToolsCallResultWire refused = await CallAsync(session, new("needle"));

            Assert.True(refused.IsError);

            Assert.Equal("Memory search is unavailable on this host.", refused.Content[0].Text);
        }
    }

    [Fact]
    public async Task Model_search_uses_shared_invocation_lane_and_retains_store_identity_and_continuation()
    {
        SearchService search = new()
        {
            Response = new("retained fact", MemorySearchScope.All,
                [new(MemorySearchScope.Lexicon, "Fact", "retained fact", "source", "retained", "identity")],
                HasMore: true, Continuation: "next-page", TruncationReason: MemorySearchTruncationReason.ResultLimit),
        };

        await using TestMcpSession session = await CreateSessionAsync(search);

        McpToolsCallResultWire result = await CallAsync(session, new("retained fact", 2, Continuation: "previous-page"));

        Assert.False(result.IsError);

        using JsonDocument document = JsonDocument.Parse(result.Content[0].Text!);

        Assert.Equal("Lexicon", Assert.Single(document.RootElement.GetProperty("results").EnumerateArray()).GetProperty("scope").GetString());

        Assert.Equal("next-page", document.RootElement.GetProperty("continuation").GetString());

        Assert.NotNull(search.Request);

        Assert.Null(search.Request.SessionId);

        Assert.Null(search.Request.WorkspaceId);

        Assert.Equal("previous-page", search.Request.Continuation);

        Assert.False(search.OperatorInspectionCalled);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_query_is_refused_before_source_work(string query)
    {
        SearchService search = new();

        await using TestMcpSession session = await CreateSessionAsync(search);

        McpToolsCallResultWire result = await CallAsync(session, new(query));

        Assert.True(result.IsError);

        Assert.Null(search.Request);
    }

    [Theory]
    [InlineData("not-a-scope")]
    [InlineData("99")]
    public async Task Unknown_scope_is_refused_before_source_work(string scope)
    {
        SearchService search = new();

        await using TestMcpSession session = await CreateSessionAsync(search);

        Assert.True((await CallAsync(session, new("fact", Scope: scope))).IsError);

        Assert.Null(search.Request);
    }

    [Fact]
    public async Task Oversized_content_is_explicitly_excerpted_inside_one_response_without_losing_continuation()
    {
        SearchService search = new()
        {
            Response = new("fact", MemorySearchScope.All,
                [new(MemorySearchScope.Session, "title", string.Concat(Enumerable.Repeat("🧙", 40_000)), "source", "retained", "entry")],
                HasMore: true, Continuation: "next"),
        };

        await using TestMcpSession session = await CreateSessionAsync(search);

        McpToolsCallResultWire result = await CallAsync(session, new("fact", Limit: 1));

        Assert.False(result.IsError);

        Assert.Contains("TRUNCATED excerpt", result.Content[0].Text);

        Assert.InRange(Encoding.UTF8.GetByteCount(result.Content[0].Text!), 1, 8192);

        using JsonDocument document = JsonDocument.Parse(result.Content[0].Text!);

        Assert.Equal("next", document.RootElement.GetProperty("continuation").GetString());
    }

    private static Task<McpToolsCallResultWire> CallAsync(TestMcpSession session, SearchMemoryParams args) =>
        session.CallToolAsync("search_memory", JsonSerializer.SerializeToElement(args, McpJsonSerializerContext.Default.SearchMemoryParams));

    private static async Task<TestMcpSession> CreateSessionAsync(SearchService? search, bool sagaEnabled = false)
    {
        ServiceCollection services = new();

        services.AddSingleton<IMemoryScopeResolver>(new ScopeResolver());

        if (search is not null) services.AddSingleton<IMemorySearchService>(search);

        IServiceScopeFactory scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        IHumanPromptRegistry humanPrompts = new HumanPromptRegistry();

        IUnseenServantPacer pacer = new UnseenServantPacer(
            new FakeEventBus(),
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()),
            scopeFactory,
            NullLogger<UnseenServantPacer>.Instance);

        IntelligenceSettings intelligenceSettings = ArcanumRuntimeDefaults.Intelligence with
        {
            EnableLexiconSystem = false,
            EnableArchiveSearch = false,
            ToolOutputCapBytes = 8192,
        };

        (InProcessMcpTransport transport, ArcanumInternalToolServer server) = InProcessMcpTransport.CreatePair(
            humanPrompts,
            scopeFactory,
            pacer,
            workspaceRootNormalizedOrNull: null,
            listDirectoryMaxPaths: 64,
            intelligenceSettings: intelligenceSettings,
            maxFileReadSizeBytes: 1024 * 1024,
            conclaveEnabled: false,
            sagaEnabled: sagaEnabled,
            a2aClientEnabled: false,
            attachmentsToolEnabled: false,
            maxJsonRpcLineBytes: 2_097_152,
            logger: NullLogger<ArcanumInternalToolServer>.Instance,
            allowHostProcessTools: true);

        CancellationTokenSource cts = new();

        Task serverTask = server.RunAsync(cts.Token);

        await transport.StartAsync();

        return new TestMcpSession(transport, serverTask, cts);
    }

    private sealed class FakeEventBus : IEventBus
    {
        public void Publish<T>(T @event) where T : notnull
        {
        }

        public async IAsyncEnumerable<T> Subscribe<T>([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken) where T : notnull
        {
            await Task.CompletedTask;

            yield break;
        }
    }

    private sealed class ScopeResolver : IMemoryScopeResolver
    {
        public bool IsCampaignScopingEnabled => false;

        public MemoryScope ForResolvedCampaign(Guid? campaignId) => MemoryScope.Installation;

        public ValueTask<MemoryScope> ResolveForSessionAsync(Guid? sessionId, CancellationToken cancellationToken) => ValueTask.FromResult(MemoryScope.Installation);
    }

    private sealed class SearchService : IMemorySearchService
    {
        internal MemorySearchRequest? Request;

        internal bool OperatorInspectionCalled;

        internal MemorySearchResponse Response = new("fact", MemorySearchScope.All, []);

        public Task<Result<MemorySearchResponse>> SearchForInvocationAsync(MemorySearchRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;

            return Task.FromResult(Result<MemorySearchResponse>.Success(Response));
        }

        public Task<Result<LexiconInspectionResult<MemorySearchResponse>>> SearchAsync(MemorySearchRequest? request,
            ICovenantSnapshotReadLease? readLease, CancellationToken cancellationToken = default)
        {
            OperatorInspectionCalled = true;

            throw new InvalidOperationException("The model must not borrow operator inspection authority.");
        }
    }

    private sealed class TestMcpSession(
        InProcessMcpTransport transport,
        Task serverTask,
        CancellationTokenSource lifetime) : IAsyncDisposable
    {
        private int _nextId;

        public async ValueTask DisposeAsync()
        {
            lifetime.Cancel();

            try
            {
                await serverTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            await transport.DisposeAsync().ConfigureAwait(false);

            lifetime.Dispose();
        }

        public async Task<JsonRpcResponse> SendRequestAsync(string method, JsonElement? parameters)
        {
            int id = Interlocked.Increment(ref _nextId);

            JsonRpcRequest request = new()
            {
                Method = method,
                Params = parameters,
                Id = JsonSerializer.SerializeToElement(id, McpJsonSerializerContext.Default.Int32),
            };

            await transport.WriteRequestAsync(request).ConfigureAwait(false);

            McpInboundEnvelope envelope = await transport.InboundReader.ReadAsync().ConfigureAwait(false);

            Assert.Equal(McpInboundKind.Response, envelope.Kind);

            return envelope.Response!;
        }

        public async Task<McpToolsCallResultWire> CallToolAsync(string name, JsonElement arguments)
        {
            McpToolsCallParams callParams = new() { Name = name, Arguments = arguments };

            JsonElement paramsElement = JsonSerializer.SerializeToElement(callParams, McpJsonSerializerContext.Default.McpToolsCallParams);

            JsonRpcResponse response = await SendRequestAsync("tools/call", paramsElement).ConfigureAwait(false);

            return JsonSerializer.Deserialize(response.Result!.Value, McpJsonSerializerContext.Default.McpToolsCallResultWire)!;
        }
    }
}
