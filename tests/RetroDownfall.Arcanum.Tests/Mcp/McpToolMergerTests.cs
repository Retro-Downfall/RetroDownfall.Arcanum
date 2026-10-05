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

using RetroDownfall.Arcanum.Tests.Support;

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

    /// <summary>
    /// One theory case per name the in-process server registers a handler for, generated from the handler
    /// registry so a newly registered tool is covered the moment it exists. A failure names the tool.
    /// </summary>
    public static TheoryData<string> RegisteredToolNameCases()
    {
        // Guards the generator itself: an empty or truncated registry would make every theory vacuous.
        IReadOnlyCollection<string> registeredNames = InternalToolHandlerNames.Registered();

        Assert.Contains("write_file", registeredNames);

        Assert.Contains("ask_human", registeredNames);

        Assert.Contains("propose_covenant", registeredNames);

        TheoryData<string> cases = [];

        foreach (string name in registeredNames.Order(StringComparer.Ordinal))
        {
            cases.Add(name);
        }

        return cases;
    }

    /// <summary>The registered names a global server may not claim: all but the workspace-root ones.</summary>
    public static TheoryData<string> GloballyReservedToolNameCases()
    {
        TheoryData<string> cases = [];

        foreach (string name in InternalToolHandlerNames.Registered().Order(StringComparer.Ordinal))
        {
            if (!ArcanumInternalToolServer.WorkspaceRootToolNames.Contains(name))
            {
                cases.Add(name);
            }
        }

        return cases;
    }

    public static TheoryData<string> WorkspaceRootToolNameCases()
    {
        TheoryData<string> cases = [];

        foreach (string name in ArcanumInternalToolServer.WorkspaceRootToolNames.Order(StringComparer.Ordinal))
        {
            cases.Add(name);
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(RegisteredToolNameCases))]
    public void MergeWorkspaceSurface_a_registered_internal_tool_name_is_unshadowable_by_a_workspace_local_server(
        string name)
    {
        LoadedMcpToolRow internalRow = Row(name, "internal");

        LoadedMcpToolRow localCollision = Row(name, "workspace-local");

        IReadOnlyList<AITool> merged = McpToolMerger.MergeWorkspaceSurface(
            [internalRow],
            new Dictionary<string, LoadedMcpToolRow>(),
            [localCollision]);

        Assert.Same(internalRow.Tool, Assert.Single(merged));
    }

    [Theory]
    [InlineData("ask_human")]
    [InlineData("scribe_lexicon")]
    [InlineData("delete_lexicon")]
    [InlineData("search_archives")]
    [InlineData("cast_sending")]
    [InlineData("dispatch_sending")]
    [InlineData("continue_sending")]
    [InlineData("read_saga")]
    [InlineData("attach_session_file")]
    [InlineData("refresh_session_file")]
    [InlineData("write_file")]
    [InlineData("Scribe_Lexicon")]
    public void MergeWorkspaceSurface_workspace_local_server_cannot_claim_an_internal_tool_the_session_does_not_advertise(
        string internalName)
    {
        // The built-in is gated off for this session (ask_human on a non-streaming turn, scribe_lexicon
        // with the Lexicon feature off, no workspace root for the file tools), so no internal row exists
        // to collide with. The name is still the built-in's: handing it to an approved workspace
        // mcp.json would let that file answer to a name the model and the name-keyed policy attribute to
        // the sandboxed built-in.
        LoadedMcpToolRow localClaim = Row(internalName, "workspace-local");

        IReadOnlyList<AITool> merged = McpToolMerger.MergeWorkspaceSurface(
            [],

            new Dictionary<string, LoadedMcpToolRow>(),

            [localClaim]);

        Assert.Empty(merged);
    }

    [Theory]
    [MemberData(nameof(RegisteredToolNameCases))]
    public void MergeWorkspaceSurface_a_registered_internal_tool_name_is_reserved_against_a_workspace_local_server_when_the_built_in_is_not_advertised(
        string name)
    {
        IReadOnlyList<AITool> merged = McpToolMerger.MergeWorkspaceSurface(
            [],

            new Dictionary<string, LoadedMcpToolRow>(),

            [Row(name, "workspace-local")]);

        Assert.Empty(merged);
    }

    [Theory]
    [MemberData(nameof(GloballyReservedToolNameCases))]
    public void MergeWorkspaceSurface_a_global_server_cannot_claim_an_internal_tool_name_that_is_not_workspace_root_gated(
        string name)
    {
        // The built-in is gated off for this session (ask_human on a non-streaming turn, scribe_lexicon
        // with the Lexicon feature off, the Conclave, Saga, A2A and attachment tools behind their flags),
        // so no internal row exists to collide with. The operator's own global configuration is trusted,
        // but not so far that a tool could answer to the name the model and the name-keyed Ward policy
        // attribute to the built-in exactly while the built-in is unavailable.
        Dictionary<string, LoadedMcpToolRow> globalMap = new(StringComparer.Ordinal)
        {
            [name] = Row(name, "global"),
        };

        Assert.Empty(McpToolMerger.MergeWorkspaceSurface([], globalMap, []));

        McpToolMerger.GlobalDedupResult deduped = McpToolMerger.DedupeGlobalTaggedTools([Row(name, "global")]);

        Assert.Empty(deduped.FirstByToolName);

        Assert.Empty(deduped.SurfaceTools);
    }

    [Theory]
    [MemberData(nameof(WorkspaceRootToolNameCases))]
    public void MergeWorkspaceSurface_a_global_server_keeps_a_workspace_root_gated_tool_name_when_the_session_has_no_root(
        string name)
    {
        // A filesystem server's write_file or list_directory in a session with no workspace root is
        // ordinary: the built-in is not advertised there, and the global server is the only file tooling.
        LoadedMcpToolRow globalRow = Row(name, "global");

        Dictionary<string, LoadedMcpToolRow> globalMap = new(StringComparer.Ordinal)
        {
            [name] = globalRow,
        };

        IReadOnlyList<AITool> merged = McpToolMerger.MergeWorkspaceSurface([], globalMap, []);

        Assert.Same(globalRow.Tool, Assert.Single(merged));

        Assert.Single(McpToolMerger.DedupeGlobalTaggedTools([Row(name, "global")]).SurfaceTools);
    }

    [Fact]
    public async Task The_workspace_root_gated_names_are_registered_and_not_advertised_without_a_workspace_root()
    {
        // WorkspaceRootToolNames is what keeps those names open to global servers, on the strength of the
        // built-in being absent in a session with no root. Compare it with what a root-less server really
        // advertises so the claim cannot drift.
        HashSet<string> registered = [.. InternalToolHandlerNames.Registered()];

        Assert.All(
            ArcanumInternalToolServer.WorkspaceRootToolNames,
            name => Assert.Contains(name, registered));

        string[] advertised = await AdvertisedToolNamesAsync(workspaceRoot: null);

        // Guards the probe: a root-less server still advertises the always-on tools.
        Assert.Contains("ask_human", advertised);

        Assert.Empty(advertised.Intersect(ArcanumInternalToolServer.WorkspaceRootToolNames, StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_workspace_root_gated_names_are_advertised_to_every_session_that_has_a_workspace_root()
    {
        // The other half of the same claim: a global server is let off these names only because, in a
        // session that does have a root, the internal row is added first and wins the name. A name the
        // server advertises only under a further condition (read_command_output also needs host-process
        // tools, which production allows only in the Development edition) is absent from a production
        // session that has a root, so a global server could answer to it there. Probe with host-process
        // tools off, the shape every non-Development session has.
        TempWorkspace workspace = new();

        await workspace.InitializeAsync();

        try
        {
            string[] advertised = await AdvertisedToolNamesAsync(
                workspaceRoot: workspace.Root,
                allowHostProcessTools: false);

            // Guards the probe: the file tools really are advertised with a root, and the host-process
            // tools really are absent when they are gated off.
            Assert.Contains("read_file_chunk", advertised);

            Assert.DoesNotContain("execute_command", advertised);

            string[] missing =
            [
                .. ArcanumInternalToolServer.WorkspaceRootToolNames
                    .Where(name => !advertised.Contains(name, StringComparer.Ordinal))
                    .Order(StringComparer.Ordinal),
            ];

            Assert.Empty(missing);
        }
        finally
        {
            await workspace.DisposeAsync();
        }
    }

    [Fact]
    public void MergeWorkspaceSurface_a_global_server_cannot_claim_read_command_output_in_a_session_that_does_not_advertise_it()
    {
        // read_command_output is advertised only with a workspace root and host-process tools both, so in
        // a production session with a root the built-in row is absent. A global server answering to it
        // there would be attributed to the built-in by the model and the name-keyed Ward policy.
        LoadedMcpToolRow globalRow = Row("read_command_output", "global");

        Dictionary<string, LoadedMcpToolRow> globalMap = new(StringComparer.Ordinal)
        {
            ["read_command_output"] = globalRow,
        };

        Assert.Empty(McpToolMerger.MergeWorkspaceSurface([], globalMap, []));

        Assert.Empty(McpToolMerger.DedupeGlobalTaggedTools([Row("read_command_output", "global")]).SurfaceTools);
    }

    [Fact]
    public void The_static_internal_tool_name_set_is_exactly_the_registered_handler_names()
    {
        // The static set is what keeps a gated-off built-in's name reserved. A handler registered
        // without being added to it (or a name left in it after its handler is removed) would silently
        // reopen, or pointlessly widen, that reservation, so the two are compared one-for-one.
        string[] registered = [.. InternalToolHandlerNames.Registered().Order(StringComparer.Ordinal)];

        string[] reserved = [.. ArcanumInternalToolServer.RegisteredToolNames.Order(StringComparer.Ordinal)];

        Assert.Equal(registered, reserved);
    }

    [Fact]
    public void MergeWorkspaceSurface_global_server_keeps_a_tool_named_like_an_internal_tool_the_session_does_not_advertise()
    {
        // The reservation against a gated-off built-in is for approved workspace mcp.json files. A global
        // server is the operator's own configuration (a filesystem server's write_file or list_directory
        // in a session with no workspace root is ordinary), so it is not swept up in it.
        LoadedMcpToolRow globalRow = Row("list_directory", "global");

        Dictionary<string, LoadedMcpToolRow> globalMap = new(StringComparer.Ordinal)
        {
            ["list_directory"] = globalRow,
        };

        IReadOnlyList<AITool> merged = McpToolMerger.MergeWorkspaceSurface(
            [],

            globalMap,

            [Row("local_only", "workspace-local")]);

        Assert.Equal(["list_directory", "local_only"], merged.Select(static t => t.Name).ToArray());

        Assert.Same(globalRow.Tool, merged[0]);
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

    private static async Task<string[]> AdvertisedToolNamesAsync(
        string? workspaceRoot,
        bool allowHostProcessTools = true)
    {
        IServiceScopeFactory scopeFactory = new ServiceCollection()
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

        (InProcessMcpTransport transport, ArcanumInternalToolServer server) = InProcessMcpTransport.CreatePair(
            new HumanPromptRegistry(),
            scopeFactory,
            new InertPacer(),
            workspaceRootNormalizedOrNull: workspaceRoot,
            listDirectoryMaxPaths: 16,
            intelligenceSettings: ArcanumRuntimeDefaults.Intelligence,
            maxFileReadSizeBytes: 1024,
            conclaveEnabled: true,
            sagaEnabled: true,
            a2aClientEnabled: true,
            attachmentsToolEnabled: true,
            maxJsonRpcLineBytes: 1_048_576,
            logger: NullLogger<ArcanumInternalToolServer>.Instance,
            allowHostProcessTools: allowHostProcessTools);

        using CancellationTokenSource lifetime = new();

        Task serverTask = server.RunAsync(lifetime.Token);

        await transport.StartAsync();

        try
        {
            await transport.WriteRequestAsync(new JsonRpcRequest
            {
                Method = "tools/list",
                Id = JsonSerializer.SerializeToElement(1, McpJsonSerializerContext.Default.Int32),
            });

            McpInboundEnvelope envelope = await transport.InboundReader.ReadAsync();

            McpToolsListResultWire tools = JsonSerializer.Deserialize(
                envelope.Response!.Result!.Value,
                McpJsonSerializerContext.Default.McpToolsListResultWire)!;

            return [.. tools.Tools.Select(static tool => tool.Name)];
        }
        finally
        {
            await lifetime.CancelAsync();

            try
            {
                await serverTask;
            }
            catch (OperationCanceledException)
            {
            }

            await transport.DisposeAsync();
        }
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
