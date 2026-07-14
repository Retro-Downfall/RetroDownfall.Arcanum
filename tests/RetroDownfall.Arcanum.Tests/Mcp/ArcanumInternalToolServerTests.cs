using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Infrastructure.Platform;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class ArcanumInternalToolServerTests : IAsyncLifetime
{

    private const string SentinelToken = "ARCANUM_TEST_SENTINEL";

    private TempWorkspace _workspace = null!;

    public async Task InitializeAsync()
    {

        _workspace = new TempWorkspace();

        await _workspace.InitializeAsync();

        _workspace.WriteFile("notes/alpha.txt", "line one\nline two\nline three");

        _workspace.CreateSubdir("folder");

    }

    public async Task DisposeAsync()
    {

        await _workspace.DisposeAsync();

    }

    [Fact]
    public async Task Initialize_returns_protocol_version_and_server_info()
    {

        await using TestMcpSession session = await CreateSessionAsync();

        JsonRpcResponse response = await session.SendRequestAsync(
            "initialize",
            JsonSerializer.SerializeToElement(
                new McpInitializeParams
                {
                    ProtocolVersion = "2024-11-05",
                    Capabilities = new McpClientCapabilities(),
                    ClientInfo = new McpClientInfo { Name = "tests", Version = "1.0" },
                },
                McpJsonSerializerContext.Default.McpInitializeParams));

        Assert.Null(response.Error);

        McpInitializeServerResult body = JsonSerializer.Deserialize(
            response.Result!.Value,
            McpJsonSerializerContext.Default.McpInitializeServerResult)!;

        Assert.Equal("2024-11-05", body.ProtocolVersion);

        Assert.Equal("ArcanumInternal", body.ServerInfo.Name);

    }

    [Fact]
    public async Task ToolsList_includes_safe_core_tools()
    {

        await using TestMcpSession session = await CreateSessionAsync();

        JsonRpcResponse response = await session.SendRequestAsync("tools/list", null);

        McpToolsListResultWire tools = JsonSerializer.Deserialize(
            response.Result!.Value,
            McpJsonSerializerContext.Default.McpToolsListResultWire)!;

        string[] names = tools.Tools.Select(t => t.Name).ToArray();

        Assert.Contains("read_file_chunk", names);

        Assert.Contains("list_directory", names);

        Assert.Contains("write_file", names);

        Assert.Contains("execute_command", names);

    }

    [Fact]
    public async Task ToolsList_names_match_registered_handlers_when_all_features_enabled()
    {

        IntelligenceSettings allFeatures = new()
        {

            EnableLexiconSystem = true,

            EnableArchiveSearch = true,

        };

        await using TestMcpSession session = await CreateSessionAsync(
            intelligenceSettings: allFeatures,
            conclaveEnabled: true,
            sagaEnabled: true,
            a2aClientEnabled: true);

        JsonRpcResponse response = await session.SendRequestAsync("tools/list", null);

        McpToolsListResultWire tools = JsonSerializer.Deserialize(
            response.Result!.Value,
            McpJsonSerializerContext.Default.McpToolsListResultWire)!;

        HashSet<string> listedNames = tools.Tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        HashSet<string> registeredNames = session.Server.RegisteredToolHandlerNamesForTests
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(registeredNames, listedNames);

    }

    [Fact]
    public async Task ToolsCall_unknown_tool_returns_expected_error()
    {

        await using TestMcpSession session = await CreateSessionAsync();

        JsonElement arguments = JsonDocument.Parse("{}").RootElement;

        McpToolsCallResultWire result = await session.CallToolAsync("nonexistent_tool_xyz", arguments);

        Assert.True(result.IsError);

        Assert.Equal("Unknown tool: nonexistent_tool_xyz", result.Content![0].Text);

    }

    [Fact]
    public async Task ToolsCall_list_directory_lists_workspace_entries()
    {

        await using TestMcpSession session = await CreateSessionAsync();

        JsonElement arguments = JsonSerializer.SerializeToElement(
            new ListDirectoryParams { RelativePath = ".", Recursive = false },
            McpJsonSerializerContext.Default.ListDirectoryParams);

        McpToolsCallResultWire result = await session.CallToolAsync("list_directory", arguments);

        Assert.False(result.IsError);

        string text = result.Content![0].Text!;

        Assert.Contains("notes", text, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("folder", text, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task ToolsCall_read_file_chunk_returns_error_when_output_exceeds_cap()
    {

        string largeLine = new('x', 16_384);

        await File.WriteAllTextAsync(
            Path.Combine(_workspace.Root, "notes", "huge.txt"),
            largeLine + "\nsecond line");

        IntelligenceSettings intelligenceSettings = new()
        {
            EnableLexiconSystem = false,
            EnableArchiveSearch = false,
            ToolOutputCapBytes = 4_096,
        };

        await using TestMcpSession session = await CreateSessionAsync(
            configureWorkspace: true,
            intelligenceSettings: intelligenceSettings,
            maxJsonRpcLineBytes: 16_384);

        JsonElement arguments = JsonSerializer.SerializeToElement(
            new ReadFileChunkParams
            {
                RelativePath = "notes/huge.txt",
                StartLine = 1,
                EndLine = 1,
            },
            McpJsonSerializerContext.Default.ReadFileChunkParams);

        McpToolsCallResultWire result = await session.CallToolAsync("read_file_chunk", arguments);

        Assert.True(result.IsError);

        Assert.Contains("too large", result.Content![0].Text!, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task ToolsCall_read_file_chunk_returns_requested_lines()
    {

        await using TestMcpSession session = await CreateSessionAsync();

        JsonElement arguments = JsonSerializer.SerializeToElement(
            new ReadFileChunkParams
            {
                RelativePath = "notes/alpha.txt",
                StartLine = 2,
                EndLine = 2,
            },
            McpJsonSerializerContext.Default.ReadFileChunkParams);

        McpToolsCallResultWire result = await session.CallToolAsync("read_file_chunk", arguments);

        Assert.False(result.IsError);

        Assert.Equal("line two", result.Content![0].Text);

    }

    [Fact]
    public async Task ToolsCall_write_file_writes_inside_workspace_sandbox()
    {

        await using TestMcpSession session = await CreateSessionAsync();

        JsonElement arguments = JsonSerializer.SerializeToElement(
            new WriteFileParams("created.txt", "written by test"),
            McpJsonSerializerContext.Default.WriteFileParams);

        McpToolsCallResultWire result = await session.CallToolAsync("write_file", arguments);

        Assert.False(result.IsError);

        string fullPath = Path.Combine(_workspace.Root, "created.txt");

        Assert.True(File.Exists(fullPath));

        Assert.Equal("written by test", await File.ReadAllTextAsync(fullPath));

    }

    [Fact]
    public async Task ToolsCall_replace_text_block_replaces_verbatim_block()
    {

        await using TestMcpSession session = await CreateSessionAsync();

        JsonElement arguments = JsonSerializer.SerializeToElement(
            new ReplaceTextBlockParams
            {
                RelativePath = "notes/alpha.txt",
                ExactSearchText = "line two",
                ReplacementText = "line replaced",
            },
            McpJsonSerializerContext.Default.ReplaceTextBlockParams);

        McpToolsCallResultWire result = await session.CallToolAsync("replace_text_block", arguments);

        Assert.False(result.IsError);

        string updated = await File.ReadAllTextAsync(Path.Combine(_workspace.Root, "notes/alpha.txt"));

        Assert.Contains("line replaced", updated, StringComparison.Ordinal);

        Assert.DoesNotContain("line two", updated, StringComparison.Ordinal);

    }

    [Fact]
    public async Task ToolsCall_read_file_chunk_rejects_path_outside_workspace()
    {

        await using TestMcpSession session = await CreateSessionAsync();

        JsonElement arguments = JsonSerializer.SerializeToElement(
            new ReadFileChunkParams
            {
                RelativePath = "../outside.txt",
                StartLine = 1,
                EndLine = 1,
            },
            McpJsonSerializerContext.Default.ReadFileChunkParams);

        McpToolsCallResultWire result = await session.CallToolAsync("read_file_chunk", arguments);

        Assert.True(result.IsError);

        Assert.Contains("sandbox", result.Content![0].Text!, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task ToolsCall_unknown_tool_returns_error_result()
    {

        await using TestMcpSession session = await CreateSessionAsync();

        McpToolsCallResultWire result = await session.CallToolAsync(
            "not_a_real_tool",
            JsonSerializer.SerializeToElement(new { }));

        Assert.True(result.IsError);

        Assert.Contains("not_a_real_tool", result.Content![0].Text!, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task Notifications_initialized_does_not_return_error_response()
    {

        await using TestMcpSession session = await CreateSessionAsync();

        JsonRpcResponse? response = await session.SendNotificationAsync("notifications/initialized", null);

        Assert.Null(response);

    }

    [Fact]
    public async Task Unknown_method_returns_method_not_found_error()
    {

        await using TestMcpSession session = await CreateSessionAsync();

        JsonRpcResponse response = await session.SendRequestAsync("nope/method", null);

        Assert.NotNull(response.Error);

        Assert.Equal(-32601, response.Error!.Code);

        Assert.Contains("nope/method", response.Error.Message, StringComparison.Ordinal);

    }

    [Fact]
    public async Task Oversized_inbound_line_produces_jsonrpc_error_response_with_null_id()
    {

        string longClientName = new('x', 200);

        IntelligenceSettings settings = new()
        {
            EnableLexiconSystem = false,
            EnableArchiveSearch = false,
        };

        await using TestMcpSession session = await CreateSessionAsync(
            configureWorkspace: true,
            intelligenceSettings: settings,
            maxJsonRpcLineBytes: 128);

        JsonElement parameters = JsonSerializer.SerializeToElement(
            new McpInitializeParams
            {
                ProtocolVersion = "2024-11-05",
                Capabilities = new McpClientCapabilities(),
                ClientInfo = new McpClientInfo { Name = longClientName, Version = "1.0" },
            },
            McpJsonSerializerContext.Default.McpInitializeParams);

        // W3.4 Group C #4: the client's outbound cap now rejects oversized lines before they
        // are written (see McpOutboundLineGuardTests). To exercise the SERVER's inbound cap
        // (a separate defense), serialize the request and write the raw line directly to the
        // server channel, bypassing the client's outbound guard.
        JsonRpcRequest request = new()
        {
            Method = "initialize",
            Params = parameters,
            Id = JsonSerializer.SerializeToElement(0, McpJsonSerializerContext.Default.Int32),
        };

        string rawLine = JsonSerializer.Serialize(request, McpJsonSerializerContext.Default.JsonRpcRequest);

        JsonRpcResponse? response = await session.SendRawLineWithTimeoutAsync(rawLine, TimeSpan.FromSeconds(5));

        Assert.NotNull(response);

        Assert.NotNull(response!.Error);

        Assert.Equal(-32600, response.Error!.Code);

        Assert.Contains("exceeds maximum UTF-8 byte budget", response.Error.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(JsonValueKind.Null, response.Id.ValueKind);

    }

    // Net-new coverage for the ModelContextProtocol SDK migration: ArcanumInternalToolServer now
    // reads inbound notifications/cancelled directly off the wire (replacing the pre-migration
    // McpRequestCancellationBroker, which correlated ids on the client side before the SDK existed).
    // This verifies (1) the notification actually cancels the in-flight tool's CancellationToken —
    // observed via the child process being killed before it can write its sentinel file — (2) the
    // server returns a graceful error response rather than letting OperationCanceledException
    // propagate out of RunAsync's read loop, and (3) the server keeps servicing requests afterward.
    [Fact]
    public async Task NotificationsCancelled_cancels_in_flight_execute_command_and_server_keeps_running()
    {

        await using TestMcpSession session = await CreateSessionAsync();

        string sentinelPath = Path.Combine(_workspace.Root, "cancel-sentinel.txt");

        (string command, string[] argumentList) = ResolveDelayedWriteCommand(sentinelPath);

        JsonElement arguments = JsonSerializer.SerializeToElement(
            new ExecuteCommandParams { Command = command, ArgumentList = argumentList },
            McpJsonSerializerContext.Default.ExecuteCommandParams);

        JsonElement callParams = JsonSerializer.SerializeToElement(
            new McpToolsCallParams { Name = "execute_command", Arguments = arguments },
            McpJsonSerializerContext.Default.McpToolsCallParams);

        (int requestId, Task<JsonRpcResponse> responseTask) = await session
            .SendRequestFireAndForgetAsync("tools/call", callParams);

        // Give the handler time to register the in-flight call and spawn the child process before
        // cancelling, so the notification actually races a live call rather than a not-yet-started one.
        await Task.Delay(300);

        await session.SendCancelNotificationAsync(requestId);

        JsonRpcResponse response = await responseTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(response.Error);

        McpToolsCallResultWire result = JsonSerializer.Deserialize(
            response.Result!.Value,
            McpJsonSerializerContext.Default.McpToolsCallResultWire)!;

        Assert.True(result.IsError);

        Assert.False(File.Exists(sentinelPath), "The child process was not killed before it wrote its sentinel file.");

        JsonRpcResponse followUp = await session.SendRequestAsync("tools/list", null);

        Assert.Null(followUp.Error);

    }

    [Fact]
    public async Task ExecuteCommand_without_workspace_is_blocked_before_spawn()
    {

        await using TestMcpSession session = await CreateSessionAsync(configureWorkspace: false);

        JsonElement arguments = JsonSerializer.SerializeToElement(
            new ExecuteCommandParams { Command = ResolveHarmlessEchoCommand().Command },
            McpJsonSerializerContext.Default.ExecuteCommandParams);

        McpToolsCallResultWire result = await session.CallToolAsync("execute_command", arguments);

        Assert.True(result.IsError);

        Assert.Contains("Workspace not configured", result.Content![0].Text!, StringComparison.Ordinal);

    }

    [Fact]
    public async Task ExecuteCommand_harmless_echo_returns_sentinel_in_sandbox()
    {

        await using TestMcpSession session = await CreateSessionAsync();

        (string command, string[] argumentList) = ResolveHarmlessEchoCommand();

        JsonElement arguments = JsonSerializer.SerializeToElement(
            new ExecuteCommandParams
            {
                Command = command,
                ArgumentList = argumentList,
            },
            McpJsonSerializerContext.Default.ExecuteCommandParams);

        McpToolsCallResultWire result = await session.CallToolAsync("execute_command", arguments);

        Assert.False(result.IsError);

        string output = result.Content![0].Text!;

        Assert.Contains(SentinelToken, output, StringComparison.Ordinal);

        Assert.Contains("--- exit code ---", output, StringComparison.Ordinal);

        Assert.Contains("0", output, StringComparison.Ordinal);

    }

    [Fact]
    public async Task ToolsCall_list_directory_recursive_lists_nested_entries()
    {

        _workspace.WriteFile("nested/child.txt", "nested");

        await using TestMcpSession session = await CreateSessionAsync();

        JsonElement arguments = JsonSerializer.SerializeToElement(
            new ListDirectoryParams { RelativePath = ".", Recursive = true },
            McpJsonSerializerContext.Default.ListDirectoryParams);

        McpToolsCallResultWire result = await session.CallToolAsync("list_directory", arguments);

        Assert.False(result.IsError);

        string text = result.Content![0].Text!;

        Assert.Contains("child.txt", text, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task ToolsCall_list_directory_rejects_file_path()
    {

        await using TestMcpSession session = await CreateSessionAsync();

        JsonElement arguments = JsonSerializer.SerializeToElement(
            new ListDirectoryParams { RelativePath = "notes/alpha.txt", Recursive = false },
            McpJsonSerializerContext.Default.ListDirectoryParams);

        McpToolsCallResultWire result = await session.CallToolAsync("list_directory", arguments);

        Assert.True(result.IsError);

        Assert.Contains("not a directory", result.Content![0].Text!, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task ToolsCall_list_directory_rejects_path_outside_workspace()
    {

        await using TestMcpSession session = await CreateSessionAsync();

        JsonElement arguments = JsonSerializer.SerializeToElement(
            new ListDirectoryParams { RelativePath = "../outside", Recursive = false },
            McpJsonSerializerContext.Default.ListDirectoryParams);

        McpToolsCallResultWire result = await session.CallToolAsync("list_directory", arguments);

        Assert.True(result.IsError);

        Assert.Contains("sandbox", result.Content![0].Text!, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task ToolsCall_write_file_rejects_path_outside_workspace()
    {

        await using TestMcpSession session = await CreateSessionAsync();

        JsonElement arguments = JsonSerializer.SerializeToElement(
            new WriteFileParams("../escape.txt", "nope"),
            McpJsonSerializerContext.Default.WriteFileParams);

        McpToolsCallResultWire result = await session.CallToolAsync("write_file", arguments);

        Assert.True(result.IsError);

        Assert.Contains("sandbox", result.Content![0].Text!, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task ToolsCall_write_file_creates_nested_directories()
    {

        await using TestMcpSession session = await CreateSessionAsync();

        JsonElement arguments = JsonSerializer.SerializeToElement(
            new WriteFileParams("deep/nested/file.txt", "nested write"),
            McpJsonSerializerContext.Default.WriteFileParams);

        McpToolsCallResultWire result = await session.CallToolAsync("write_file", arguments);

        Assert.False(result.IsError);

        string fullPath = Path.Combine(_workspace.Root, "deep", "nested", "file.txt");

        Assert.True(File.Exists(fullPath));

        Assert.Equal("nested write", await File.ReadAllTextAsync(fullPath));

    }

    [Fact]
    public async Task ToolsCall_execute_command_with_relative_working_directory()
    {

        await using TestMcpSession session = await CreateSessionAsync();

        (string command, string[] argumentList) = ResolveHarmlessEchoCommand();

        JsonElement arguments = JsonSerializer.SerializeToElement(
            new ExecuteCommandParams
            {
                Command = command,
                ArgumentList = argumentList,
                WorkingDirectory = "notes",
            },
            McpJsonSerializerContext.Default.ExecuteCommandParams);

        McpToolsCallResultWire result = await session.CallToolAsync("execute_command", arguments);

        Assert.False(result.IsError);

        Assert.Contains(SentinelToken, result.Content![0].Text!, StringComparison.Ordinal);

    }

    [Fact]
    public async Task ToolsCall_execute_command_rejects_absolute_working_directory()
    {

        await using TestMcpSession session = await CreateSessionAsync();

        JsonElement arguments = JsonSerializer.SerializeToElement(
            new ExecuteCommandParams
            {
                Command = ResolveHarmlessEchoCommand().Command,
                WorkingDirectory = "/tmp",
            },
            McpJsonSerializerContext.Default.ExecuteCommandParams);

        McpToolsCallResultWire result = await session.CallToolAsync("execute_command", arguments);

        Assert.True(result.IsError);

        Assert.Contains("relative to the workspace root", result.Content![0].Text!, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task ToolsCall_scribe_lexicon_when_disabled_returns_error()
    {

        await using TestMcpSession session = await CreateSessionAsync();

        JsonElement arguments = JsonSerializer.SerializeToElement(
            new ScribeLexiconParams("Test", null, ["fact"]),
            McpJsonSerializerContext.Default.ScribeLexiconParams);

        McpToolsCallResultWire result = await session.CallToolAsync("scribe_lexicon", arguments);

        Assert.True(result.IsError);

        Assert.Contains("Lexicon system is disabled", result.Content![0].Text!, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task ToolsList_advertises_lexicon_tools_when_enabled()
    {

        IntelligenceSettings settings = new() { EnableLexiconSystem = true, EnableArchiveSearch = false };

        await using TestMcpSession session = await CreateSessionAsync(intelligenceSettings: settings);

        JsonRpcResponse response = await session.SendRequestAsync("tools/list", null);

        McpToolsListResultWire tools = JsonSerializer.Deserialize(
            response.Result!.Value,
            McpJsonSerializerContext.Default.McpToolsListResultWire)!;

        Assert.Contains(tools.Tools, static t => t.Name == "scribe_lexicon");

        Assert.Contains(tools.Tools, static t => t.Name == "delete_lexicon");

        Assert.DoesNotContain(tools.Tools, static t => t.Name is "read_lore" or "scribe_lore" or "delete_lore");

    }

    [Fact]
    public async Task ToolsList_omits_lexicon_tools_when_disabled()
    {

        IntelligenceSettings settings = new() { EnableLexiconSystem = false, EnableArchiveSearch = false };

        await using TestMcpSession session = await CreateSessionAsync(intelligenceSettings: settings);

        JsonRpcResponse response = await session.SendRequestAsync("tools/list", null);

        McpToolsListResultWire tools = JsonSerializer.Deserialize(
            response.Result!.Value,
            McpJsonSerializerContext.Default.McpToolsListResultWire)!;

        Assert.DoesNotContain(tools.Tools, static t => t.Name is "scribe_lexicon" or "delete_lexicon");

    }

    [Fact]
    public async Task ToolsCall_scribe_lexicon_creates_entry()
    {

        IntelligenceSettings settings = new() { EnableLexiconSystem = true, EnableArchiveSearch = false };

        await using TestMcpSession session = await CreateSessionAsync(intelligenceSettings: settings);

        JsonElement arguments = JsonSerializer.SerializeToElement(
            new ScribeLexiconParams("Alice", "Person", ["Prefers concise answers."]),
            McpJsonSerializerContext.Default.ScribeLexiconParams);

        McpToolsCallResultWire result = await session.CallToolAsync("scribe_lexicon", arguments);

        Assert.False(result.IsError);

        Assert.Contains("Alice", result.Content![0].Text!, StringComparison.Ordinal);

    }

    [Fact]
    public async Task ToolsCall_delete_lexicon_removes_entry()
    {

        IntelligenceSettings settings = new() { EnableLexiconSystem = true, EnableArchiveSearch = false };

        await using TestMcpSession session = await CreateSessionAsync(intelligenceSettings: settings);

        JsonElement scribeArgs = JsonSerializer.SerializeToElement(
            new ScribeLexiconParams("Bob", "Person", ["Initial."]),
            McpJsonSerializerContext.Default.ScribeLexiconParams);

        _ = await session.CallToolAsync("scribe_lexicon", scribeArgs);

        JsonElement deleteArgs = JsonSerializer.SerializeToElement(
            new DeleteLexiconParams("Bob"),
            McpJsonSerializerContext.Default.DeleteLexiconParams);

        McpToolsCallResultWire result = await session.CallToolAsync("delete_lexicon", deleteArgs);

        Assert.False(result.IsError);

        Assert.Contains("Bob", result.Content![0].Text!, StringComparison.Ordinal);

    }

    [Fact]
    public async Task ToolsCall_adjust_initiative_clamps_interval()
    {

        await using TestMcpSession session = await CreateSessionAsync();

        JsonElement arguments = JsonSerializer.SerializeToElement(
            new AdjustInitiativeArgs { JobName = "summarize", IntervalMinutes = 9999 },
            McpJsonSerializerContext.Default.AdjustInitiativeArgs);

        McpToolsCallResultWire result = await session.CallToolAsync("adjust_initiative", arguments);

        Assert.False(result.IsError);

        Assert.Contains("summarize", result.Content![0].Text!, StringComparison.Ordinal);

        Assert.Contains("minutes", result.Content![0].Text!, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task ToolsList_DoesNotAdvertiseDispatchSending_WhenDisabled()
    {

        await using TestMcpSession session = await CreateSessionAsync(a2aClientEnabled: false);

        JsonRpcResponse response = await session.SendRequestAsync("tools/list", null);

        McpToolsListResultWire tools = JsonSerializer.Deserialize(
            response.Result!.Value,
            McpJsonSerializerContext.Default.McpToolsListResultWire)!;

        Assert.DoesNotContain(tools.Tools, static t => t.Name == "dispatch_sending");

    }

    [Fact]
    public async Task ToolsList_AdvertisesDispatchSending_WhenEnabled()
    {

        await using TestMcpSession session = await CreateSessionAsync(a2aClientEnabled: true);

        JsonRpcResponse response = await session.SendRequestAsync("tools/list", null);

        McpToolsListResultWire tools = JsonSerializer.Deserialize(
            response.Result!.Value,
            McpJsonSerializerContext.Default.McpToolsListResultWire)!;

        Assert.Contains(tools.Tools, static t => t.Name == "dispatch_sending");

    }

    [Fact]
    public async Task ToolsCall_DispatchSending_WhenDisabled_ReturnsToolError()
    {

        await using TestMcpSession session = await CreateSessionAsync(a2aClientEnabled: false);

        JsonElement arguments = JsonSerializer.SerializeToElement(
            new DispatchSendingParams { Goal = "do the thing", AgentUrl = "https://agent.example.test/" },
            McpJsonSerializerContext.Default.DispatchSendingParams);

        McpToolsCallResultWire result = await session.CallToolAsync("dispatch_sending", arguments);

        Assert.True(result.IsError);

        Assert.Contains("A2A is disabled", result.Content![0].Text!, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task ToolsCall_DispatchSending_Success_ReturnsStructuredJsonWithResponseText()
    {

        FakeA2AClientService fake = new(static (goal, _, agentUrl) =>
            Result<A2ADispatchResult>.Success(new A2ADispatchResult("remote-task-1", $"answered '{goal}' via {agentUrl}")));

        await using TestMcpSession session = await CreateSessionAsync(a2aClientEnabled: true, a2aClientService: fake);

        JsonElement arguments = JsonSerializer.SerializeToElement(
            new DispatchSendingParams { Goal = "do the thing", AgentUrl = "https://agent.example.test/" },
            McpJsonSerializerContext.Default.DispatchSendingParams);

        McpToolsCallResultWire result = await session.CallToolAsync("dispatch_sending", arguments);

        Assert.False(result.IsError);

        DispatchSendingResultWire payload = JsonSerializer.Deserialize(
            result.Content![0].Text!,
            McpJsonSerializerContext.Default.DispatchSendingResultWire)!;

        Assert.True(payload.Succeeded);

        Assert.Equal("remote-task-1", payload.TaskId);

        Assert.Contains("do the thing", payload.Response, StringComparison.Ordinal);

    }

    [Fact]
    public async Task ToolsCall_DispatchSending_PreflightFailure_ReturnsPlainToolError()
    {

        FakeA2AClientService fake = new(static (_, _, _) =>
            Result<A2ADispatchResult>.Failure(new Error(ErrorCodes.Sending.MaxTasksReached, "too many in flight")));

        await using TestMcpSession session = await CreateSessionAsync(a2aClientEnabled: true, a2aClientService: fake);

        JsonElement arguments = JsonSerializer.SerializeToElement(
            new DispatchSendingParams { Goal = "do the thing", AgentUrl = "https://agent.example.test/" },
            McpJsonSerializerContext.Default.DispatchSendingParams);

        McpToolsCallResultWire result = await session.CallToolAsync("dispatch_sending", arguments);

        Assert.True(result.IsError);

        Assert.Contains("too many in flight", result.Content![0].Text!, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task ToolsCall_DispatchSending_PostDispatchFailure_ReturnsStructuredJsonNotToolError()
    {

        FakeA2AClientService fake = new(static (_, _, _) =>
            Result<A2ADispatchResult>.Failure(new Error(ErrorCodes.Sending.AgentUnreachable, "could not connect")));

        await using TestMcpSession session = await CreateSessionAsync(a2aClientEnabled: true, a2aClientService: fake);

        JsonElement arguments = JsonSerializer.SerializeToElement(
            new DispatchSendingParams { Goal = "do the thing", AgentUrl = "https://agent.example.test/" },
            McpJsonSerializerContext.Default.DispatchSendingParams);

        McpToolsCallResultWire result = await session.CallToolAsync("dispatch_sending", arguments);

        // A dispatch was genuinely attempted (the remote agent just could not be reached), so
        // ApprenticeService's Chronicle interception still needs a parseable payload here — this must
        // NOT be a plain IsError=true ToolError like the preflight-rejection case above.
        Assert.False(result.IsError);

        DispatchSendingResultWire payload = JsonSerializer.Deserialize(
            result.Content![0].Text!,
            McpJsonSerializerContext.Default.DispatchSendingResultWire)!;

        Assert.False(payload.Succeeded);

        Assert.Contains("could not connect", payload.Error, StringComparison.OrdinalIgnoreCase);

    }

    private sealed class FakeA2AClientService(Func<string, string?, string, Result<A2ADispatchResult>> respond) : IA2AClientService
    {

        public Task<Result<A2ADispatchResult>> DispatchSendingAsync(
            string goal,
            string? name,
            string agentUrl,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(respond(goal, name, agentUrl));

    }

    [Fact]
    public async Task ToolsCall_read_file_chunk_rejects_invalid_line_range()
    {

        await using TestMcpSession session = await CreateSessionAsync();

        JsonElement arguments = JsonSerializer.SerializeToElement(
            new ReadFileChunkParams
            {
                RelativePath = "notes/alpha.txt",
                StartLine = 3,
                EndLine = 1,
            },
            McpJsonSerializerContext.Default.ReadFileChunkParams);

        McpToolsCallResultWire result = await session.CallToolAsync("read_file_chunk", arguments);

        Assert.True(result.IsError);

        Assert.Contains("startLine", result.Content![0].Text!, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task ToolsCall_read_file_chunk_rejects_symlink_to_outside_workspace()
    {

        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {

            return;

        }

        string outsidePath = Path.Combine(Path.GetTempPath(), $"arcanum-outside-{Guid.NewGuid():N}.txt");

        await File.WriteAllTextAsync(outsidePath, "outside");

        try
        {

            string linkPath = Path.Combine(_workspace.Root, "notes", "escape-link.txt");

            if (File.Exists(linkPath))
            {

                File.Delete(linkPath);

            }

            File.CreateSymbolicLink(linkPath, outsidePath);

            await using TestMcpSession session = await CreateSessionAsync();

            JsonElement arguments = JsonSerializer.SerializeToElement(
                new ReadFileChunkParams
                {
                    RelativePath = "notes/escape-link.txt",
                    StartLine = 1,
                    EndLine = 1,
                },
                McpJsonSerializerContext.Default.ReadFileChunkParams);

            McpToolsCallResultWire result = await session.CallToolAsync("read_file_chunk", arguments);

            Assert.True(result.IsError);

            Assert.Contains("sandbox", result.Content![0].Text!, StringComparison.OrdinalIgnoreCase);

        }
        finally
        {

            if (File.Exists(outsidePath))
            {

                File.Delete(outsidePath);

            }

        }

    }

    private async Task<TestMcpSession> CreateSessionAsync(
        bool configureWorkspace = true,
        IntelligenceSettings? intelligenceSettings = null,
        long maxFileReadSizeBytes = 1024 * 1024,
        int maxJsonRpcLineBytes = 2_097_152,
        bool conclaveEnabled = false,
        bool sagaEnabled = false,
        bool a2aClientEnabled = false,
        IA2AClientService? a2aClientService = null)
    {

        string? normalizedRoot = configureWorkspace
            ? Path.GetFullPath(_workspace.Root)
            : null;

        IntelligenceSettings settings = intelligenceSettings ?? new IntelligenceSettings
        {
            EnableLexiconSystem = false,
            EnableArchiveSearch = false,
        };

        ServiceCollection services = new();

        services.AddSingleton<ISanctumGuard, PermissiveSanctumGuard>();

        services.AddSingleton<RetroDownfall.Arcanum.Core.Platform.IProcessResourceLimiter, ProcessResourceLimiter>();

        services.AddSingleton<ILexiconService, FakeLexiconService>();

        if (a2aClientService is not null)
        {

            services.AddSingleton(a2aClientService);

        }

        IServiceScopeFactory scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        IHumanPromptRegistry humanPrompts = new HumanPromptRegistry();

        IUnseenServantPacer pacer = new UnseenServantPacer(
            new FakeEventBus(),
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()),
            scopeFactory,
            NullLogger<UnseenServantPacer>.Instance);

        (InProcessMcpTransport transport, ArcanumInternalToolServer server) = InProcessMcpTransport.CreatePair(
            humanPrompts,
            scopeFactory,
            pacer,
            normalizedRoot,
            executeCommandTimeout: TimeSpan.FromSeconds(30),
            executeCommandTimeoutSecondsForDisplay: 30,
            listDirectoryMaxPaths: 64,
            intelligenceSettings: settings,
            maxFileReadSizeBytes: maxFileReadSizeBytes,
            conclaveEnabled: conclaveEnabled,
            sagaEnabled: sagaEnabled,
            a2aClientEnabled: a2aClientEnabled,
            maxJsonRpcLineBytes: maxJsonRpcLineBytes,
            logger: NullLogger<ArcanumInternalToolServer>.Instance);

        CancellationTokenSource cts = new();

        Task serverTask = server.RunAsync(cts.Token);

        await transport.StartAsync();

        return new TestMcpSession(transport, server, serverTask, cts);

    }

    private static (string Command, string[] ArgumentList) ResolveHarmlessEchoCommand()
    {

        if (OperatingSystem.IsWindows())
        {

            return ("powershell.exe", ["-NoProfile", "-Command", $"Write-Output {SentinelToken}"]);

        }

        return ("/bin/echo", [SentinelToken]);

    }

    private static (string Command, string[] ArgumentList) ResolveDelayedWriteCommand(string sentinelPath)
    {

        if (OperatingSystem.IsWindows())
        {

            return ("powershell.exe", ["-NoProfile", "-Command", $"Start-Sleep -Seconds 5; Set-Content -Path '{sentinelPath}' -Value done"]);

        }

        return ("/bin/sh", ["-c", $"sleep 5 && echo done > '{sentinelPath}'"]);

    }

    private sealed class TestMcpSession(
        InProcessMcpTransport transport,
        ArcanumInternalToolServer server,
        Task serverTask,
        CancellationTokenSource lifetime) : IAsyncDisposable
    {

        public ArcanumInternalToolServer Server => server;

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

        // Writes a request and returns its id immediately without waiting for the response, so the
        // caller can send a notifications/cancelled for that id while the tool call is still in flight.
        public async Task<(int Id, Task<JsonRpcResponse> Response)> SendRequestFireAndForgetAsync(
            string method,
            JsonElement? parameters)
        {

            int id = Interlocked.Increment(ref _nextId);

            JsonRpcRequest request = new()
            {
                Method = method,
                Params = parameters,
                Id = JsonSerializer.SerializeToElement(id, McpJsonSerializerContext.Default.Int32),
            };

            await transport.WriteRequestAsync(request).ConfigureAwait(false);

            return (id, ReadResponseAsync());

        }

        private async Task<JsonRpcResponse> ReadResponseAsync()
        {

            McpInboundEnvelope envelope = await transport.InboundReader.ReadAsync().ConfigureAwait(false);

            Assert.Equal(McpInboundKind.Response, envelope.Kind);

            return envelope.Response!;

        }

        public Task SendCancelNotificationAsync(int requestId)
        {

            JsonElement cancelParams = JsonSerializer.SerializeToElement(new { requestId });

            JsonRpcRequest notification = new()
            {
                Method = "notifications/cancelled",
                Params = cancelParams,
                Id = null,
            };

            return transport.WriteRequestAsync(notification);

        }

        public async Task<JsonRpcResponse?> SendRequestWithTimeoutAsync(
            string method,
            JsonElement? parameters,
            TimeSpan timeout)
        {

            int id = Interlocked.Increment(ref _nextId);

            JsonRpcRequest request = new()
            {
                Method = method,
                Params = parameters,
                Id = JsonSerializer.SerializeToElement(id, McpJsonSerializerContext.Default.Int32),
            };

            await transport.WriteRequestAsync(request).ConfigureAwait(false);

            using CancellationTokenSource cts = new(timeout);

            try
            {

                McpInboundEnvelope envelope = await transport.InboundReader
                    .ReadAsync(cts.Token)
                    .ConfigureAwait(false);

                Assert.Equal(McpInboundKind.Response, envelope.Kind);

                return envelope.Response;

            }

            catch (OperationCanceledException)
            {

                return null;

            }

        }

        // W3.4 Group C #4: writes a pre-serialized line directly to the server channel,
        // bypassing the client's outbound line-size guard so the server's INBOUND cap can be
        // exercised. The server reads the raw line and applies its own size check.
        public async Task<JsonRpcResponse?> SendRawLineWithTimeoutAsync(string rawLine, TimeSpan timeout)
        {

            await transport.WriteRawLineForTestsAsync(rawLine).ConfigureAwait(false);

            using CancellationTokenSource cts = new(timeout);

            try
            {

                McpInboundEnvelope envelope = await transport.InboundReader
                    .ReadAsync(cts.Token)
                    .ConfigureAwait(false);

                Assert.Equal(McpInboundKind.Response, envelope.Kind);

                return envelope.Response;

            }

            catch (OperationCanceledException)
            {

                return null;

            }

        }

        public async Task<JsonRpcResponse?> SendNotificationAsync(string method, JsonElement? parameters)
        {

            JsonRpcRequest request = new()
            {
                Method = method,
                Params = parameters,
                Id = null,
            };

            await transport.WriteRequestAsync(request).ConfigureAwait(false);

            using CancellationTokenSource wait = new(TimeSpan.FromMilliseconds(250));

            try
            {

                McpInboundEnvelope envelope = await transport.InboundReader.ReadAsync(wait.Token).ConfigureAwait(false);

                Assert.Equal(McpInboundKind.Response, envelope.Kind);

                return envelope.Response;

            }
            catch (OperationCanceledException)
            {

                return null;

            }

        }

        public async Task<McpToolsCallResultWire> CallToolAsync(string toolName, JsonElement arguments)
        {

            JsonElement callParams = JsonSerializer.SerializeToElement(
                new McpToolsCallParams { Name = toolName, Arguments = arguments },
                McpJsonSerializerContext.Default.McpToolsCallParams);

            JsonRpcResponse response = await SendRequestAsync("tools/call", callParams).ConfigureAwait(false);

            Assert.Null(response.Error);

            return JsonSerializer.Deserialize(
                response.Result!.Value,
                McpJsonSerializerContext.Default.McpToolsCallResultWire)!;

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

        public Task<ResourceLimits> GetEffectiveResourceLimitsForWorkspaceAsync(string? workspaceRoot, CancellationToken ct = default) =>
            Task.FromResult(new ResourceLimits());

        public Task RecordResourceLimitBreachAsync(
            string? workspaceRoot,
            string toolName,
            Core.Platform.ResourceLimitKind resource,
            string limitValue,
            string? actualValue,
            CancellationToken ct = default) =>
            Task.CompletedTask;

    }

    private sealed class FakeEventBus : Core.Events.IEventBus
    {

        public void Publish<T>(T @event) where T : notnull
        {
        }

        public IAsyncEnumerable<T> Subscribe<T>(CancellationToken cancellationToken) where T : notnull =>
            AsyncEnumerable.Empty<T>();

    }

}
