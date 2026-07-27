using System.Text.Json;

using Microsoft.Extensions.AI;

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
    [InlineData("SEARCH_WORKSPACE")]
    [InlineData("Execute_Command")]
    [InlineData("Workspace_Check")]
    [InlineData("Apply_Patch")]
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
    [InlineData("SEARCH_WORKSPACE")]
    [InlineData("Execute_Command")]
    [InlineData("Workspace_Check")]
    [InlineData("Apply_Patch")]
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
