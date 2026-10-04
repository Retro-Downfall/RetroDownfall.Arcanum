using System.Text.Json;

using Microsoft.Extensions.AI;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Api.Intelligence;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Hosting;

using RetroDownfall.Arcanum.Infrastructure.Mcp;

using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class McpToolMergerTests
{
    [Fact]
    public void DedupeGlobalTaggedTools_first_seen_wins_and_preserves_order()
    {
        LoadedMcpToolRow first = Row("alpha", "cmd-a");

        LoadedMcpToolRow duplicate = Row("alpha", "cmd-b");

        LoadedMcpToolRow second = Row("beta", "cmd-c");

        McpToolMerger.GlobalDedupResult result = McpToolMerger.DedupeGlobalTaggedTools([first, duplicate, second]);

        Assert.Equal(2, result.FirstByToolName.Count);

        Assert.Same(first.Tool, result.FirstByToolName["alpha"].Tool);

        Assert.Equal(["alpha", "beta"], result.SurfaceTools.Select(static t => t.Name).ToArray());
    }

    [Fact]
    public void DedupeGlobalTaggedTools_empty_input_returns_empty()
    {
        McpToolMerger.GlobalDedupResult result = McpToolMerger.DedupeGlobalTaggedTools([]);

        Assert.Empty(result.FirstByToolName);

        Assert.Empty(result.SurfaceTools);
    }

    [Fact]
    public void MergeWorkspaceSurface_internal_then_global_precedence()
    {
        LoadedMcpToolRow internalRow = Row("shared", "internal");

        LoadedMcpToolRow globalShared = Row("shared", "global-dup");

        LoadedMcpToolRow globalOnly = Row("global_only", "global");

        Dictionary<string, LoadedMcpToolRow> globalMap = new(StringComparer.Ordinal)

        {
            ["shared"] = globalShared,

            ["global_only"] = globalOnly,
        };

        IReadOnlyList<AITool> merged = McpToolMerger.MergeWorkspaceSurface(
            [internalRow],

            globalMap,

            []);

        Assert.Equal(["shared", "global_only"], merged.Select(static t => t.Name).ToArray());

        Assert.Same(internalRow.Tool, merged[0]);
    }

    [Theory]
    [InlineData("search_workspace")]
    [InlineData("execute_command")]
    [InlineData("workspace_check")]
    [InlineData("apply_patch")]
    [InlineData("propose_covenant")]
    [InlineData("retire_covenant")]
    [InlineData("SEARCH_WORKSPACE")]
    [InlineData("Execute_Command")]
    [InlineData("Workspace_Check")]
    [InlineData("Apply_Patch")]
    [InlineData("Propose_Covenant")]
    [InlineData("Retire_Covenant")]
    public void MergeWorkspaceSurface_external_servers_cannot_override_intrinsic_internal_names(
        string reservedName)
    {
        LoadedMcpToolRow internalRow = Row(reservedName, "internal");

        LoadedMcpToolRow globalCollision = Row(reservedName, "global");

        LoadedMcpToolRow localCollision = Row(reservedName, "local");

        Dictionary<string, LoadedMcpToolRow> globalMap = new(StringComparer.Ordinal)

        {
            [reservedName] = globalCollision,
        };

        IReadOnlyList<AITool> merged = McpToolMerger.MergeWorkspaceSurface(
            [internalRow],

            globalMap,

            [localCollision]);

        AITool tool = Assert.Single(merged);

        Assert.Same(internalRow.Tool, tool);
    }

    [Theory]
    [InlineData("search_workspace")]
    [InlineData("execute_command")]
    [InlineData("workspace_check")]
    [InlineData("apply_patch")]
    [InlineData("propose_covenant")]
    [InlineData("retire_covenant")]
    [InlineData("SEARCH_WORKSPACE")]
    [InlineData("Execute_Command")]
    [InlineData("Workspace_Check")]
    [InlineData("Apply_Patch")]
    [InlineData("Propose_Covenant")]
    [InlineData("Retire_Covenant")]
    public void MergeWorkspaceSurface_omits_reserved_external_name_when_internal_tool_is_unavailable(
        string reservedName)
    {
        LoadedMcpToolRow globalCollision = Row(reservedName, "global");

        LoadedMcpToolRow localCollision = Row(reservedName, "local");

        Dictionary<string, LoadedMcpToolRow> globalMap = new(StringComparer.Ordinal)

        {
            [reservedName] = globalCollision,
        };

        IReadOnlyList<AITool> merged = McpToolMerger.MergeWorkspaceSurface(
            [],

            globalMap,

            [localCollision]);

        Assert.Empty(merged);
    }

    [Theory]
    [InlineData("write_file")]
    [InlineData("read_file_chunk")]
    [InlineData("ask_human")]
    [InlineData("scribe_lexicon")]
    [InlineData("replace_text_block")]
    public void MergeWorkspaceSurface_workspace_local_server_cannot_replace_unreserved_internal_tool(
        string internalName)
    {
        LoadedMcpToolRow internalRow = Row(internalName, "internal");

        LoadedMcpToolRow localCollision = Row(internalName, "workspace-local");

        IReadOnlyList<AITool> merged = McpToolMerger.MergeWorkspaceSurface(
            [internalRow],

            new Dictionary<string, LoadedMcpToolRow>(),

            [localCollision]);

        Assert.Same(internalRow.Tool, Assert.Single(merged));
    }

    [Fact]
    public void MergeWorkspaceSurface_workspace_local_case_variant_of_an_internal_tool_is_omitted_too()
    {
        LoadedMcpToolRow internalRow = Row("write_file", "internal");

        LoadedMcpToolRow localVariant = Row("Write_File", "workspace-local");

        IReadOnlyList<AITool> merged = McpToolMerger.MergeWorkspaceSurface(
            [internalRow],

            new Dictionary<string, LoadedMcpToolRow>(),

            [localVariant]);

        Assert.Same(internalRow.Tool, Assert.Single(merged));
    }

    [Fact]
    public void MergeWorkspaceSurface_workspace_local_server_still_overrides_a_global_external_tool()
    {
        LoadedMcpToolRow internalRow = Row("write_file", "internal");

        LoadedMcpToolRow globalRow = Row("tool_a", new McpServerConfig { Command = "global-cmd" });

        LoadedMcpToolRow localRow = Row("tool_a", new McpServerConfig { Command = "local-cmd" });

        Dictionary<string, LoadedMcpToolRow> globalMap = new(StringComparer.Ordinal) { ["tool_a"] = globalRow };

        IReadOnlyList<AITool> merged = McpToolMerger.MergeWorkspaceSurface(
            [internalRow],

            globalMap,

            [localRow]);

        Assert.Equal(["write_file", "tool_a"], merged.Select(static t => t.Name).ToArray());

        Assert.Same(internalRow.Tool, merged[0]);

        Assert.IsType<McpBridgeTool>(merged[1]);

        Assert.NotSame(globalRow.Tool, merged[1]);
    }

    [Theory]
    [InlineData("propose_covenant")]
    [InlineData("retire_covenant")]
    public void MergeWorkspaceSurface_external_servers_cannot_claim_covenant_tool_names(
        string covenantToolName)
    {
        LoadedMcpToolRow globalClaim = Row(covenantToolName, "global");

        LoadedMcpToolRow localClaim = Row(covenantToolName, "workspace-local");

        Dictionary<string, LoadedMcpToolRow> globalMap = new(StringComparer.Ordinal)
        {
            [covenantToolName] = globalClaim,
        };

        // With the built-in advertised, and with it gated off: the names are reserved either way.
        LoadedMcpToolRow internalRow = Row(covenantToolName, "internal");

        IReadOnlyList<AITool> withBuiltIn = McpToolMerger.MergeWorkspaceSurface(
            [internalRow],
            globalMap,
            [localClaim]);

        Assert.Same(internalRow.Tool, Assert.Single(withBuiltIn));

        IReadOnlyList<AITool> withoutBuiltIn = McpToolMerger.MergeWorkspaceSurface(
            [],
            globalMap,
            [localClaim]);

        Assert.Empty(withoutBuiltIn);
    }

    [Fact]
    public void MergeWorkspaceSurface_every_registered_internal_tool_name_is_unshadowable()
    {
        IReadOnlyCollection<string> registeredNames = RegisteredInternalToolNames();

        // Guards the generator itself: an empty registry would make the loop below vacuous.
        Assert.Contains("write_file", registeredNames);

        Assert.Contains("propose_covenant", registeredNames);

        foreach (string name in registeredNames)
        {
            LoadedMcpToolRow internalRow = Row(name, "internal");

            LoadedMcpToolRow localCollision = Row(name, "workspace-local");

            IReadOnlyList<AITool> merged = McpToolMerger.MergeWorkspaceSurface(
                [internalRow],
                new Dictionary<string, LoadedMcpToolRow>(),
                [localCollision]);

            Assert.True(
                ReferenceEquals(internalRow.Tool, Assert.Single(merged)),
                $"A workspace-local server replaced the internal tool '{name}'.");
        }
    }

    [Fact]
    public void MergeWorkspaceSurface_local_wins_same_registration()
    {
        McpServerConfig config = new() { Command = "echo", Args = ["local"] };

        LoadedMcpToolRow globalRow = Row("tool_a", config);

        LoadedMcpToolRow localRow = Row("tool_a", config);

        Dictionary<string, LoadedMcpToolRow> globalMap = new(StringComparer.Ordinal) { ["tool_a"] = globalRow };

        IReadOnlyList<AITool> merged = McpToolMerger.MergeWorkspaceSurface(
            [],

            globalMap,

            [localRow]);

        Assert.Single(merged);

        Assert.Same(localRow.Tool, merged[0]);
    }

    [Fact]
    public void MergeWorkspaceSurface_local_wins_different_registration_creates_bridge_tool()
    {
        LoadedMcpToolRow globalRow = Row("tool_a", new McpServerConfig { Command = "global-cmd" });

        LoadedMcpToolRow localRow = Row("tool_a", new McpServerConfig { Command = "local-cmd" });

        Dictionary<string, LoadedMcpToolRow> globalMap = new(StringComparer.Ordinal) { ["tool_a"] = globalRow };

        IReadOnlyList<AITool> merged = McpToolMerger.MergeWorkspaceSurface(
            [],

            globalMap,

            [localRow]);

        Assert.Single(merged);

        Assert.IsType<McpBridgeTool>(merged[0]);
    }

    [Fact]
    public void MergeWorkspaceSurface_appends_new_local_only_tools()
    {
        LoadedMcpToolRow globalRow = Row("global_tool", new McpServerConfig { Command = "global" });

        LoadedMcpToolRow localOnly = Row("local_only", new McpServerConfig { Command = "local" });

        Dictionary<string, LoadedMcpToolRow> globalMap = new(StringComparer.Ordinal) { ["global_tool"] = globalRow };

        IReadOnlyList<AITool> merged = McpToolMerger.MergeWorkspaceSurface(
            [],

            globalMap,

            [localOnly]);

        Assert.Equal(["global_tool", "local_only"], merged.Select(static t => t.Name).ToArray());
    }

    [Fact]
    public void MergeWorkspaceSurface_empty_inputs_returns_empty()
    {
        IReadOnlyList<AITool> merged = McpToolMerger.MergeWorkspaceSurface([], new Dictionary<string, LoadedMcpToolRow>(), []);

        Assert.Empty(merged);
    }

    [Fact]
    public void MergeWorkspaceSurface_internal_only_skips_global_and_local()
    {
        LoadedMcpToolRow internalRow = Row("internal_tool", new McpServerConfig { Command = "internal" });

        IReadOnlyList<AITool> merged = McpToolMerger.MergeWorkspaceSurface(
            [internalRow],

            new Dictionary<string, LoadedMcpToolRow>(),

            []);

        Assert.Single(merged);

        Assert.Same(internalRow.Tool, merged[0]);
    }

    private static IReadOnlyCollection<string> RegisteredInternalToolNames()
    {
        IServiceScopeFactory scopeFactory = new ServiceCollection()
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

        (_, _, ArcanumInternalToolServer server) = InProcessMcpTransport.CreateServerChannelPair(
            new HumanPromptRegistry(),
            scopeFactory,
            new InertPacer(),
            workspaceRootNormalizedOrNull: null,
            listDirectoryMaxPaths: 16,
            new IntelligenceSettings(),
            maxFileReadSizeBytes: 1024,
            conclaveEnabled: true,
            sagaEnabled: true,
            a2aClientEnabled: true,
            attachmentsToolEnabled: true,
            maxJsonRpcLineBytes: 1_048_576,
            logger: NullLogger<ArcanumInternalToolServer>.Instance);

        return [.. server.RegisteredToolHandlerNamesForTests];
    }

    private sealed class InertPacer : IUnseenServantPacer
    {
        public Task<bool> SetDynamicIntervalAsync(
            string jobName,
            int intervalMinutes,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public int GetEffectiveInterval(UnseenServantJob job) => 0;

        public Task HydrateAsync(
            IReadOnlyList<UnseenServantWatermark> watermarks,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private static LoadedMcpToolRow Row(string name, McpServerConfig config)
    {
        StubMcpClient client = new();

        McpBridgeTool tool = new(
            name,

            $"{name} description",

            JsonSerializer.SerializeToElement(new McpEmptyJsonObject(), McpJsonSerializerContext.Default.McpEmptyJsonObject),

            client,

            toolOutputCapBytes: 4096);

        return new LoadedMcpToolRow(tool, config, client);
    }

    private static LoadedMcpToolRow Row(string name, string command) =>
        Row(name, new McpServerConfig { Command = command });

    private sealed class StubMcpClient : IMcpClient
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ModelContextProtocol.Protocol.CallToolResult> CallToolAsync(
            string toolName,

            IReadOnlyDictionary<string, object?> arguments,

            TimeSpan? requestTimeout = null,

            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<McpBridgeTool>> GetToolsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
