using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Events;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Infrastructure.Platform;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Mcp;

/// <summary>
/// RAG Phase 4 — <c>read_saga</c> MCP tool: gating (advertised/callable only when
/// <c>Features:Saga</c>), semantic search happy path, empty results, and embedding failure.
/// Mirrors <see cref="ArcanumInternalToolServerTests"/>'s session harness, but with its own minimal
/// scope providing <see cref="IWeaveService"/>, <see cref="IDivinationService"/>, and
/// <see cref="ISagaMemoryStore"/> — read_saga needs none of the workspace/Grimoire dependencies that
/// harness wires up for file/lore tools.
/// </summary>
public sealed class ReadSagaToolTests
{

    [Fact]
    public async Task ToolsList_DoesNotAdvertiseReadSaga_WhenDisabled()
    {

        await using TestMcpSession session = await CreateSessionAsync(sagaEnabled: false);

        JsonRpcResponse response = await session.SendRequestAsync("tools/list", null);

        McpToolsListResultWire tools = JsonSerializer.Deserialize(
            response.Result!.Value,
            McpJsonSerializerContext.Default.McpToolsListResultWire)!;

        Assert.DoesNotContain(tools.Tools, static t => t.Name == "read_saga");

    }

    [Fact]
    public async Task ToolsList_AdvertisesReadSaga_WhenEnabled()
    {

        await using TestMcpSession session = await CreateSessionAsync(sagaEnabled: true);

        JsonRpcResponse response = await session.SendRequestAsync("tools/list", null);

        McpToolsListResultWire tools = JsonSerializer.Deserialize(
            response.Result!.Value,
            McpJsonSerializerContext.Default.McpToolsListResultWire)!;

        Assert.Contains(tools.Tools, static t => t.Name == "read_saga");

    }

    [Fact]
    public async Task ToolsCall_ReadSaga_WhenDisabled_ReturnsError()
    {

        await using TestMcpSession session = await CreateSessionAsync(sagaEnabled: false);

        JsonElement arguments = JsonSerializer.SerializeToElement(
            new ReadSagaParams("what theme do I like?"),
            McpJsonSerializerContext.Default.ReadSagaParams);

        McpToolsCallResultWire result = await session.CallToolAsync("read_saga", arguments);

        Assert.True(result.IsError);

        Assert.Contains("Saga is disabled", result.Content![0].Text!, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task ToolsCall_ReadSaga_HappyPath_ReturnsMemoriesWithSimilarity()
    {

        FakeWeaveService weave = new();

        FakeSagaMemoryStore store = new();

        store.Memories["mem-1"] = new SagaMemoryDto(
            "mem-1",
            "The operator prefers dark mode.",
            new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero),
            SessionId: null,
            Tags: null,
            Source: "extraction");

        FakeDivinationService divination = new()
        {
            Results = [new DivinationResult("mem-1", 0.87f, EmptyMetadata)],
        };

        await using TestMcpSession session = await CreateSessionAsync(sagaEnabled: true, weave: weave, divination: divination, store: store);

        JsonElement arguments = JsonSerializer.SerializeToElement(
            new ReadSagaParams("what theme do I like?"),
            McpJsonSerializerContext.Default.ReadSagaParams);

        McpToolsCallResultWire result = await session.CallToolAsync("read_saga", arguments);

        Assert.False(result.IsError);

        string text = result.Content![0].Text!;

        Assert.Contains("The operator prefers dark mode.", text, StringComparison.Ordinal);

        Assert.Contains("0.87", text, StringComparison.Ordinal);

    }

    [Fact]
    public async Task ToolsCall_ReadSaga_EmptyResults_ReturnsFriendlyMessage()
    {

        FakeWeaveService weave = new();

        FakeSagaMemoryStore store = new();

        FakeDivinationService divination = new() { Results = [] };

        await using TestMcpSession session = await CreateSessionAsync(sagaEnabled: true, weave: weave, divination: divination, store: store);

        JsonElement arguments = JsonSerializer.SerializeToElement(
            new ReadSagaParams("unrelated query"),
            McpJsonSerializerContext.Default.ReadSagaParams);

        McpToolsCallResultWire result = await session.CallToolAsync("read_saga", arguments);

        Assert.False(result.IsError);

        Assert.Contains("No Saga memories matched", result.Content![0].Text!, StringComparison.Ordinal);

    }

    [Fact]
    public async Task ToolsCall_ReadSaga_EmbeddingFailure_ReturnsErrorGracefully()
    {

        FakeWeaveService weave = new() { FailEmbed = true };

        await using TestMcpSession session = await CreateSessionAsync(sagaEnabled: true, weave: weave);

        JsonElement arguments = JsonSerializer.SerializeToElement(
            new ReadSagaParams("what theme do I like?"),
            McpJsonSerializerContext.Default.ReadSagaParams);

        McpToolsCallResultWire result = await session.CallToolAsync("read_saga", arguments);

        Assert.True(result.IsError);

        Assert.Contains("Failed to embed", result.Content![0].Text!, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task ToolsCall_ReadSaga_ProviderUnavailable_ReturnsErrorGracefully()
    {

        FakeWeaveService weave = new() { Available = false };

        await using TestMcpSession session = await CreateSessionAsync(sagaEnabled: true, weave: weave);

        JsonElement arguments = JsonSerializer.SerializeToElement(
            new ReadSagaParams("what theme do I like?"),
            McpJsonSerializerContext.Default.ReadSagaParams);

        McpToolsCallResultWire result = await session.CallToolAsync("read_saga", arguments);

        Assert.True(result.IsError);

        Assert.Contains("embedding provider is unavailable", result.Content![0].Text!, StringComparison.OrdinalIgnoreCase);

    }

    /// <summary>
    /// The tool reads inside the turn's Campaign, and takes that Campaign from the Session the host
    /// bound to the call rather than from anything the model could say.
    /// </summary>
    /// <remarks>
    /// <c>read_saga</c> is read-only precisely so a model cannot steer its own memory. Letting it steer
    /// which Campaign's memory it reads would hand that back, so the Session identity the scope is
    /// resolved from is asserted here alongside the scope itself.
    /// </remarks>
    [Fact]
    public async Task ToolsCall_ReadSaga_WhenScopingIsOn_SearchesTheAmbientSessionsCampaign()
    {

        Guid campaign = new("5E2A9C11-7B34-4D80-9F16-2C7E0A3B4D59");

        Guid session = new("11111111-2222-4333-8444-555555555555");

        FakeWeaveService weave = new() { Available = true };

        FakeSagaMemoryStore store = new();

        store.Memories["mem-1"] = new SagaMemoryDto(
            "mem-1",
            "a conclusion",
            DateTimeOffset.UtcNow,
            SessionId: null,
            Tags: null,
            Source: "extraction");

        FakeDivinationService divination = new()
        {
            Results = [new DivinationResult("mem-1", 0.87f, EmptyMetadata)],
        };

        FakeMemoryScopeResolver scopeResolver = new(
            new MemoryScope(MemoryScopeKind.Campaign, campaign));

        await using TestMcpSession mcp = await CreateSessionAsync(
            sagaEnabled: true,
            weave: weave,
            divination: divination,
            store: store,
            scopeResolver: scopeResolver);

        Guid? previousSession = SessionAttachmentToolAmbient.CurrentSessionId;

        try
        {

            SessionAttachmentToolAmbient.CurrentSessionId = session;

            McpToolsCallResultWire result = await mcp.CallToolAsync(
                "read_saga",
                JsonSerializer.SerializeToElement(
                    new ReadSagaParams("what did we decide?"),
                    McpJsonSerializerContext.Default.ReadSagaParams));

            Assert.False(result.IsError);

        }
        finally
        {

            SessionAttachmentToolAmbient.CurrentSessionId = previousSession;

        }

        Assert.NotNull(divination.LastCampaignScope);

        Assert.Equal(campaign, divination.LastCampaignScope!.CampaignId);

        Assert.Equal(session, scopeResolver.LastSessionId);

    }

    /// <summary>
    /// With the gate off the tool asks the unscoped question it always asked.
    /// </summary>
    [Fact]
    public async Task ToolsCall_ReadSaga_WhenScopingIsOff_UsesTheUnscopedSearch()
    {

        FakeWeaveService weave = new() { Available = true };

        FakeSagaMemoryStore store = new();

        store.Memories["mem-1"] = new SagaMemoryDto(
            "mem-1",
            "a conclusion",
            DateTimeOffset.UtcNow,
            SessionId: null,
            Tags: null,
            Source: "extraction");

        FakeDivinationService divination = new()
        {
            Results = [new DivinationResult("mem-1", 0.87f, EmptyMetadata)],
        };

        await using TestMcpSession mcp = await CreateSessionAsync(
            sagaEnabled: true,
            weave: weave,
            divination: divination,
            store: store);

        McpToolsCallResultWire result = await mcp.CallToolAsync(
            "read_saga",
            JsonSerializer.SerializeToElement(
                new ReadSagaParams("what did we decide?"),
                McpJsonSerializerContext.Default.ReadSagaParams));

        Assert.False(result.IsError);

        Assert.Null(divination.LastCampaignScope);

    }

    private static readonly IReadOnlyDictionary<string, string> EmptyMetadata = new Dictionary<string, string>(0);

    private static async Task<TestMcpSession> CreateSessionAsync(
        bool sagaEnabled,
        FakeWeaveService? weave = null,
        FakeDivinationService? divination = null,
        FakeSagaMemoryStore? store = null,
        FakeMemoryScopeResolver? scopeResolver = null)
    {

        ServiceCollection services = new();

        services.AddSingleton<IMemoryScopeResolver>(scopeResolver ?? new FakeMemoryScopeResolver());

        services.AddSingleton<IWeaveService>(weave ?? new FakeWeaveService());

        services.AddSingleton<IDivinationService>(divination ?? new FakeDivinationService());

        services.AddSingleton<ISagaMemoryStore>(store ?? new FakeSagaMemoryStore());

        services.AddSingleton<IOptionsMonitor<ArcanumSettings>>(
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings
            {
                Features = new FeatureSettings
                {
                    Embeddings = true,
                    Saga = sagaEnabled,
                },
            }));

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
            logger: NullLogger<ArcanumInternalToolServer>.Instance);

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

    private sealed class FakeWeaveService : IWeaveService
    {

        public bool Available { get; set; } = true;

        public bool FailEmbed { get; set; }

        public float[] QueryVector { get; set; } = [1f, 0f, 0f];

        public bool IsAvailable => Available;

        public Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken)
        {

            if (FailEmbed)
            {

                return Task.FromResult(Result<Embedding<float>>.Failure(
                    new Error(ErrorCodes.Embeddings.ProviderUnavailable, "Simulated embedding failure.")));

            }

            return Task.FromResult(Result<Embedding<float>>.Success(new Embedding<float>(QueryVector)));

        }

        public Task<Result<Embedding<float>[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by read_saga.");

        public Task<Result<(string Chunk, int Offset)[]>> ChunkAsync(string text, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by read_saga.");

    }

    private sealed class FakeDivinationService : IDivinationService
    {

        public DivinationResult[] Results { get; set; } = [];

        public bool Fail { get; set; }

        public Task<Result<DivinationResult[]>> SearchAsync(
            string tableName,
            string primaryKeyColumn,
            string embeddingColumn,
            Embedding<float> queryEmbedding,
            int maxResults,
            float similarityThreshold,
            CancellationToken cancellationToken)
        {

            if (Fail)
            {

                return Task.FromResult(Result<DivinationResult[]>.Failure(
                    new Error(ErrorCodes.Embeddings.ProviderUnavailable, "Simulated search failure.")));

            }

            return Task.FromResult(Result<DivinationResult[]>.Success(Results));

        }

        public Task<Result<DivinationResult[]>> SearchScopedAsync(
            string tableName,
            string primaryKeyColumn,
            string embeddingColumn,
            string scopeTableName,
            string scopeJoinColumn,
            string scopeFilterColumn,
            string scopeFilterValue,
            Embedding<float> queryEmbedding,
            int maxResults,
            float similarityThreshold,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by read_saga.");

        /// <summary>The scope the tool asked for, or null when it used the unscoped search.</summary>
        public DivinationCampaignScope? LastCampaignScope { get; private set; }

        public Task<Result<DivinationResult[]>> SearchCampaignScopedAsync(
            string tableName,
            string primaryKeyColumn,
            string embeddingColumn,
            DivinationCampaignScope scope,
            Embedding<float> queryEmbedding,
            int maxResults,
            float similarityThreshold,
            CancellationToken cancellationToken)
        {

            LastCampaignScope = scope;

            return SearchAsync(tableName, primaryKeyColumn, embeddingColumn, queryEmbedding, maxResults, similarityThreshold, cancellationToken);

        }

    }

    private sealed class FakeSagaMemoryStore : ISagaMemoryStore
    {

        public Dictionary<string, SagaMemoryDto> Memories { get; } = new(StringComparer.Ordinal);

        public Task<SagaMemoryWriteOutcome> InsertAsync(
            string id,
            string content,
            DateTimeOffset createdAt,
            Guid? sessionId,
            string? tags,
            string? source,
            float[] embedding,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("read_saga is read-only.");

        public Task<int> CountAsync(CancellationToken cancellationToken) => Task.FromResult(Memories.Count);

        public Task<int> CountBySessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by read_saga.");

        public Task<SagaMemoryDto[]> ListAsync(string? query, Guid? sessionId, MemoryScope scope, int limit, int offset, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by read_saga.");

        public Task<IReadOnlyDictionary<string, SagaMemoryDto>> GetByIdsAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken)
        {

            Dictionary<string, SagaMemoryDto> result = new(StringComparer.Ordinal);

            foreach (string id in ids)
            {

                if (Memories.TryGetValue(id, out SagaMemoryDto? memory))
                {

                    result[id] = memory;

                }

            }

            return Task.FromResult((IReadOnlyDictionary<string, SagaMemoryDto>)result);

        }

        public Task<SagaMemoryCurationRow?> ReadCurationRowAsync(string id, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by read_saga.");

        public Task<SagaCurationOutcome> RetireAsync(
            string id, byte[] expectedContentDigest, DateTimeOffset retiredAt, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by read_saga.");

        public Task<SagaCurationOutcome> ReinstateAsync(
            string id,
            byte[] expectedContentDigest,
            float[] embedding,
            DateTimeOffset reinstatedAt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by read_saga.");

        public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by read_saga.");

        public Task DeleteAllAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by read_saga.");

        public Task<SagaStats> GetStatsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by read_saga.");

        public Task<DateTimeOffset?> GetWatermarkAsync(Guid sessionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by read_saga.");

        public Task SetWatermarkAsync(Guid sessionId, DateTimeOffset lastExtractedEntryCreatedAt, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by read_saga.");

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
