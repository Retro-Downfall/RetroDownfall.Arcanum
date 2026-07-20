using Microsoft.Extensions.AI;

using Microsoft.Extensions.Logging.Abstractions;

using Microsoft.ML.Tokenizers;

using RetroDownfall.Arcanum.Api.Intelligence;

using RetroDownfall.Arcanum.Api.Intelligence.Tools;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

using RetroDownfall.Arcanum.Core.Mcp;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Tests.Support;

using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// Pure-logic tests for the <c>POST /api/intelligence/mana</c> building blocks:
/// <see cref="InferenceContextBuilder.MapToAiChatMessages"/> and <see cref="ToolSchemaManaEstimator"/>.
/// HTTP-level behavior (validation, envelope shape) is covered by
/// <c>IntelligenceEndpointTests.PostMana_*</c>.
/// </summary>
public sealed class ManaCountingTests
{

    [Fact]
    public void MapToAiChatMessages_MapsRolesAndContent()
    {

        List<CoreChatMessage> messages =
        [
            new CoreChatMessage("system", "You are helpful."),
            new CoreChatMessage("user", "hello world"),
            new CoreChatMessage("assistant", "hi there"),
        ];

        List<MeAiChatMessage> mapped = InferenceContextBuilder.MapToAiChatMessages(messages);

        Assert.Equal(3, mapped.Count);

        Assert.Equal(ChatRole.System, mapped[0].Role);

        Assert.Equal(ChatRole.User, mapped[1].Role);

        Assert.Equal("hello world", mapped[1].Text);

        Assert.Equal(ChatRole.Assistant, mapped[2].Role);

    }

    [Fact]
    public void MapToAiChatMessages_ToolMessage_MapsToFunctionResultContent()
    {

        List<CoreChatMessage> messages =
        [
            new CoreChatMessage("tool", "42", ToolCallId: "call-1"),
        ];

        List<MeAiChatMessage> mapped = InferenceContextBuilder.MapToAiChatMessages(messages);

        Assert.Single(mapped);

        Assert.Equal(ChatRole.Tool, mapped[0].Role);

        FunctionResultContent result = Assert.IsType<FunctionResultContent>(Assert.Single(mapped[0].Contents));

        Assert.Equal("call-1", result.CallId);

    }

    [Fact]
    public async Task ToolSchemaManaEstimator_NoMcpTools_CountsBuiltInsOnly()
    {

        FakeMcpConnectionManager mcp = new();

        Tokenizer tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");

        int estimate = await ToolSchemaManaEstimator.EstimateAsync(mcp, tokenizer, perToolOverheadTokens: 4, workingDirectory: null, browseTool: null, CancellationToken.None);

        // Two built-in tools (get_local_system_time, get_arcanum_system_info), each contributing at
        // least its per-tool overhead plus non-zero name/description/schema tokens.
        Assert.True(estimate >= 8);

    }

    [Fact]
    public async Task ToolSchemaManaEstimator_IncludesMcpTools()
    {

        FakeMcpConnectionManager mcpWithoutTools = new();

        FakeMcpConnectionManager mcpWithTools = new();

        mcpWithTools.Tools.Add(new FakeTool("run_query", "Runs a query against the database."));

        Tokenizer tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");

        int baseline = await ToolSchemaManaEstimator.EstimateAsync(mcpWithoutTools, tokenizer, perToolOverheadTokens: 4, workingDirectory: null, browseTool: null, CancellationToken.None);

        int withMcpTool = await ToolSchemaManaEstimator.EstimateAsync(mcpWithTools, tokenizer, perToolOverheadTokens: 4, workingDirectory: null, browseTool: null, CancellationToken.None);

        Assert.True(withMcpTool > baseline);

    }

    [Fact]
    public async Task ToolSchemaManaEstimator_IncludesBrowseWebToolWhenEnabled()
    {

        FakeMcpConnectionManager mcp = new();

        Tokenizer tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");

        ArcanumBrowseWebTool browseTool = new(
            new FakeHttpClientFactory(),
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()),
            NullLogger.Instance);

        int baseline = await ToolSchemaManaEstimator.EstimateAsync(mcp, tokenizer, perToolOverheadTokens: 4, workingDirectory: null, browseTool: null, CancellationToken.None);

        int withBrowseTool = await ToolSchemaManaEstimator.EstimateAsync(mcp, tokenizer, perToolOverheadTokens: 4, workingDirectory: null, browseTool, CancellationToken.None);

        Assert.True(withBrowseTool > baseline);

    }

    private sealed class FakeTool(string name, string description) : AIFunction
    {

        public override string Name => name;

        public override string Description => description;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            new((object?)null);

    }

    private sealed class FakeMcpConnectionManager : IMcpConnectionManager
    {

        public List<AITool> Tools { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<Result> StartAsync(string name, string? workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> StopAsync(string name, string? workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> RestartAsync(string name, string? workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

        public Task<McpServerInfo?> GetStatusAsync(string name, string? workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult<McpServerInfo?>(null);

        public Task<McpServerInfo[]> GetAllStatusesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Array.Empty<McpServerInfo>());

        public Task<IReadOnlyList<AITool>> GetAvailableToolsAsync(string? workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AITool>>(Tools);

        public Task<AIFunction?> GetToolAsync(
            string serverName,
            string toolName,
            string? workingDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AIFunction?>(null);

        public Task<List<McpServerStatusDto>> GetServerStatusesAsync(string workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<McpServerStatusDto>());

        public Task ReloadAsync(string workingDirectory, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<Result> TrustWorkspaceAsync(string workingDirectory, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success());

    }

}
