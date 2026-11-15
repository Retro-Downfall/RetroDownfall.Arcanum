using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Sanctum;
using ModelContextProtocol.Protocol;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    [SkippableTheory]
    [InlineData("absolute")]
    [InlineData("parent")]
    [InlineData("symlink")]
    public async Task Workspace_tool_outcomes_preserve_denial_and_containment_through_native_ndjson(string spelling)
    {
        Skip.If(spelling == "symlink" && OperatingSystem.IsWindows(), "Creating a symbolic link requires host privilege on Windows.");

        using ArcanumTestHomeScope home = new("native-workspace-denial");

        string workspace = _workspace.CreateSubdir("authorized");

        string canary = "outside-" + Guid.NewGuid().ToString("N");

        string outsidePath = _workspace.WriteFile("outside/secret.txt", canary);

        _workspace.WriteFile("authorized/present.txt", "contained answer\n");

        string linkPath = Path.Combine(workspace, "outside-link.txt");

        if (spelling == "symlink")
        {
            File.CreateSymbolicLink(linkPath, outsidePath);
        }

        string requestedPath = spelling switch
        {
            "absolute" => outsidePath,
            "parent" => "../outside/secret.txt",
            _ => "outside-link.txt",
        };

        await using McpConnectionManager manager = McpConnectionManagerHarness.Create();

        FakeMcpConnectionManager mcp = await WorkspaceOutcomeToolsAsync(manager, workspace);

        ScriptingChatClient chat = new();

        chat.EnqueueStreamToolCall("read_file", "refused-read", WorkspaceReadArguments(requestedPath));

        chat.EnqueueStreamToolCall("read_file", "contained-read", WorkspaceReadArguments("present.txt"));

        chat.EnqueueStreamTokens("recovered after refusal");

        List<IntelligenceEvent> frames = await CollectStreamAsync(
            CreateWizard(chat, mcp: mcp),
            WorkspaceOutcomeRequest(workspace));

        IntelligenceEvent refused = Assert.Single(frames, static frame => frame.Type == IntelligenceEventType.ToolResult && frame.ToolCall?.CallId == "refused-read");

        AssertWorkspaceOutcome(refused, "workspacePolicyDenied");

        Assert.True(refused.ToolDenied);

        Assert.Contains("workspace", refused.Data!, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(outsidePath, refused.Data!, StringComparison.Ordinal);

        Assert.DoesNotContain(frames, static frame => frame.Type == IntelligenceEventType.ToolError);

        IntelligenceEvent call = Assert.Single(frames, static frame => frame.Type == IntelligenceEventType.ToolCall && frame.ToolCall?.CallId == "refused-read");

        Assert.Equal(call.ToolCall!.ArgumentsJson, refused.ToolCall!.ArgumentsJson);

        IntelligenceEvent contained = Assert.Single(frames, static frame => frame.Type == IntelligenceEventType.ToolResult && frame.ToolCall?.CallId == "contained-read");

        Assert.Contains("contained answer", contained.Data!, StringComparison.Ordinal);

        Assert.False(contained.ToolDenied);

        string ndjson = string.Join('\n', frames.Select(static frame => JsonSerializer.Serialize(frame, ArcanumJsonContext.Default.IntelligenceEvent)));

        Assert.DoesNotContain(canary, ndjson, StringComparison.Ordinal);

        Assert.Equal(canary, await File.ReadAllTextAsync(outsidePath));

        if (spelling == "symlink")
        {
            Assert.Equal(outsidePath, new FileInfo(linkPath).LinkTarget);
        }

        Assert.Equal("recovered after refusal", Assert.Single(frames, static frame => frame.Type == IntelligenceEventType.Result).Message);
    }

    [Theory]
    [InlineData("missing-file", "notFound")]
    [InlineData("missing-parent", "notFound")]
    [InlineData("missing-directory", "notFound")]
    [InlineData("file-as-directory", "notADirectory")]
    [InlineData("zero-start", "invalidRange")]
    [InlineData("reversed-range", "invalidRange")]
    [InlineData("excessive-range", "invalidRange")]
    public async Task Workspace_tool_outcomes_keep_expected_failures_actionable_through_native_ndjson(string scenario, string expectedCode)
    {
        using ArcanumTestHomeScope home = new("native-workspace-miss");

        _workspace.WriteFile("present.txt", "contained answer\n");

        (string toolName, Dictionary<string, object?> arguments) = WorkspaceFailureArguments(scenario);

        await using McpConnectionManager manager = McpConnectionManagerHarness.Create();

        FakeMcpConnectionManager mcp = await WorkspaceOutcomeToolsAsync(manager, _workspace.Root);

        ScriptingChatClient chat = new();

        chat.EnqueueStreamToolCall(toolName, "expected-miss", arguments);

        chat.EnqueueStreamToolCall("read_file", "subsequent-read", WorkspaceReadArguments("present.txt"));

        chat.EnqueueStreamTokens("recovered after expected failure");

        List<IntelligenceEvent> frames = await CollectStreamAsync(CreateWizard(chat, mcp: mcp), WorkspaceOutcomeRequest(_workspace.Root));

        IntelligenceEvent missed = Assert.Single(frames, static frame => frame.Type == IntelligenceEventType.ToolResult && frame.ToolCall?.CallId == "expected-miss");

        AssertWorkspaceOutcome(missed, expectedCode);

        Assert.False(missed.ToolDenied);

        Assert.DoesNotContain("internal error", missed.Data!, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("leave the workspace", missed.Data!, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(_workspace.Root, missed.Data!, StringComparison.Ordinal);

        Assert.DoesNotContain(frames, static frame => frame.Type == IntelligenceEventType.ToolError);

        IntelligenceEvent call = Assert.Single(frames, static frame => frame.Type == IntelligenceEventType.ToolCall && frame.ToolCall?.CallId == "expected-miss");

        Assert.Equal(call.ToolCall!.ArgumentsJson, missed.ToolCall!.ArgumentsJson);

        IntelligenceEvent contained = Assert.Single(frames, static frame => frame.Type == IntelligenceEventType.ToolResult && frame.ToolCall?.CallId == "subsequent-read");

        Assert.Contains("contained answer", contained.Data!, StringComparison.Ordinal);

        Assert.Equal("recovered after expected failure", Assert.Single(frames, static frame => frame.Type == IntelligenceEventType.Result).Message);
    }

    [Theory]
    [InlineData("missing-file")]
    [InlineData("missing-directory")]
    [InlineData("zero-start")]
    public async Task Workspace_tool_outcomes_remain_recoverable_on_buffered_turns(string scenario)
    {
        using ArcanumTestHomeScope home = new("buffered-workspace-miss");

        (string toolName, Dictionary<string, object?> arguments) = WorkspaceFailureArguments(scenario);

        await using McpConnectionManager manager = McpConnectionManagerHarness.Create();

        FakeMcpConnectionManager mcp = await WorkspaceOutcomeToolsAsync(manager, _workspace.Root);

        ScriptingChatClient chat = new();

        chat.EnqueueToolCall(toolName, "buffered-miss", arguments);

        chat.EnqueueText("recovered buffered answer");

        Result<PromptTurnResult> result = await CreateWizard(chat, mcp: mcp).ExecutePromptAsync(
            WorkspaceOutcomeRequest(_workspace.Root),
            InvocationContexts.AttendedSession(),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal("recovered buffered answer", result.Value.Text);

        string toolResult = GetMessageText(Assert.Single(chat.LastBufferedMessages, static message => message.Role == ChatRole.Tool));

        Assert.DoesNotContain("internal error", toolResult, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("leave the workspace", toolResult, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(_workspace.Root, toolResult, StringComparison.Ordinal);

        Assert.Contains(scenario == "zero-start" ? "line" : "not found", toolResult, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("native-metadata")]
    [InlineData("timeout-text")]
    [InlineData("host-flag")]
    [InlineData("replacement-host-flag")]
    public async Task Workspace_tool_outcomes_do_not_trust_external_metadata_or_error_text(string disguise)
    {
        using ArcanumTestHomeScope home = new("untrusted-workspace-outcome");

        const string canary = "PRIVATE_EXTERNAL_ERROR /private/credential-state";

        string externalText = disguise == "timeout-text"
            ? HumanPromptTimeoutException.DefaultMessage + " " + canary
            : "The requested file was not found. " + canary;

        using JsonDocument schema = JsonDocument.Parse("{}");

        using JsonDocument metadata = JsonDocument.Parse("{\"code\":\"workspacePolicyDenied\",\"message\":\"PRIVATE_EXTERNAL_ERROR /private/credential-state\"}");

        WorkspaceOutcomeExternalClient client = new(new CallToolResult
        {
            Content = [new TextContentBlock { Text = externalText }],
            IsError = true,
            StructuredContent = metadata.RootElement.Clone(),
            Meta = System.Text.Json.Nodes.JsonNode.Parse("{\"arcanum/toolOutcome\":{\"code\":\"nativePolicyDenied\",\"message\":\"PRIVATE_EXTERNAL_ERROR /private/credential-state\"}}")!.AsObject(),
        }, claimsHostOwnership: disguise is "host-flag" or "replacement-host-flag");

        FakeMcpConnectionManager mcp = new();

        await using McpConnectionManager hostManager = McpConnectionManagerHarness.Create();

        McpBridgeTool bridge = new("read_file", "external tool", schema.RootElement, client, 4096);

        if (disguise == "replacement-host-flag")
        {
            IReadOnlyList<AITool> tools = await hostManager.GetAvailableToolsAsync(_workspace.Root);

            bridge = Assert.IsType<McpBridgeTool>(tools.Single(static tool => tool.Name == "read_file")).WithClient(client);
        }

        mcp.Tools.Add(bridge);

        ScriptingChatClient chat = new();

        chat.EnqueueStreamToolCall("read_file", "external-error", WorkspaceReadArguments("missing.txt"));

        chat.EnqueueStreamTokens("continued after external error");

        List<IntelligenceEvent> frames = await CollectStreamAsync(CreateWizard(chat, mcp: mcp), WorkspaceOutcomeRequest(_workspace.Root));

        IntelligenceEvent reported = Assert.Single(frames, static frame => frame.Type == IntelligenceEventType.ToolResult);

        AssertWorkspaceOutcome(reported, "externalToolError");

        Assert.False(reported.ToolDenied);

        Assert.DoesNotContain("internal error", reported.Data!, StringComparison.OrdinalIgnoreCase);

        string ndjson = string.Join('\n', frames.Select(static frame => JsonSerializer.Serialize(frame, ArcanumJsonContext.Default.IntelligenceEvent)));

        Assert.DoesNotContain(canary, ndjson, StringComparison.Ordinal);

        Assert.DoesNotContain(HumanPromptTimeoutException.DefaultMessage, reported.Data!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Workspace_tool_outcomes_remain_failed_operations_without_throwing(bool denied, bool tolerateUnexpectedFailures)
    {
        using ArcanumTestHomeScope home = new("workspace-outcome-pipeline");

        await using McpConnectionManager manager = McpConnectionManagerHarness.Create();

        IReadOnlyList<AITool> tools = await manager.GetAvailableToolsAsync(_workspace.Root);

        AITool read = tools.Single(static tool => tool.Name == "read_file");

        string path = denied ? Path.Combine(_workspace.Root, "rooted.txt") : "missing.txt";

        ToolExecutionPipeline.ProcessedToolCall result = await ToolExecutionPipelineTests.CreatePipeline().ProcessSingleToolCallAsync(
            new FunctionCallContent("expected-failure", "read_file", WorkspaceReadArguments(path)),
            WorkspaceOutcomeRequest(_workspace.Root),
            new ChatOptions { Tools = [read] },
            null,
            null,
            new ToolExecutionPipeline.TurnContext { WorkspaceRoot = _workspace.Root },
            tolerateUnexpectedFailures,
            CancellationToken.None);

        Assert.True(result.Failed);

        Assert.Equal(denied, result.Denied);

        Assert.DoesNotContain("internal error", result.ResultText, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("operator has been notified", result.ResultText, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Workspace_tool_outcomes_distinguish_absence_from_an_unverifiable_existing_file(bool exists)
    {
        string path = Path.Combine(_workspace.Root, "metadata-probe.txt");

        if (exists)
        {
            File.WriteAllText(path, "must not be read");
        }

        Func<string, FileHandleMetadata?>? previous = FileHandleIdentityInterop.TryGetPathMetadataForTests;

        try
        {
            FileHandleIdentityInterop.TryGetPathMetadataForTests = _ => null;

            Assert.False(SandboxedFileIo.TryOpenForRead(_workspace.Root, path, out FileStream? stream, out McpToolsCallResultWire? error));

            Assert.Null(stream);

            Assert.NotNull(error);

            if (exists)
            {
                Assert.Null(error.StructuredContent);

                Assert.Null(error.Meta);
            }
            else
            {
                Assert.True(error.StructuredContent.HasValue);

                Assert.Equal("notFound", error.StructuredContent.Value.GetProperty("code").GetString());
            }

            Assert.Equal(exists, File.Exists(path));
        }
        finally
        {
            FileHandleIdentityInterop.TryGetPathMetadataForTests = previous;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_reported_errors_keep_actionable_copy_and_failure_status(bool denied)
    {
        using ArcanumTestHomeScope home = new("native-reported-errors");

        _workspace.WriteFile("retry.txt", "original text");

        using IDisposable persisted = PersistedToolInvocationAmbient.Begin(new(Guid.NewGuid(), Guid.NewGuid()));

        await using McpConnectionManager manager = McpConnectionManagerHarness.Create(configureServices: static services => services.AddSingleton<ISanctumGuard>(new ConfigurableSanctumGuard()));

        IReadOnlyList<AITool> tools = await manager.GetAvailableToolsAsync(_workspace.Root);

        string toolName = denied ? "delete_memory" : "edit_file";

        Dictionary<string, object?> arguments = denied
            ? new() { ["name"] = "daemon_state:Watcher:abc" }
            : new() { ["relativePath"] = "retry.txt", ["exactSearchText"] = "missing block", ["replacementText"] = "changed" };

        AITool tool = tools.Single(t => t.Name == toolName);

        ToolExecutionPipeline.ProcessedToolCall result = await ToolExecutionPipelineTests.CreatePipeline().ProcessSingleToolCallAsync(
            new FunctionCallContent("reported-failure", toolName, arguments),
            WorkspaceOutcomeRequest(_workspace.Root),
            new ChatOptions { Tools = [tool] },
            null,
            null,
            new ToolExecutionPipeline.TurnContext { WorkspaceRoot = _workspace.Root },
            true,
            CancellationToken.None);

        Assert.True(result.Failed);

        Assert.Equal(denied, result.Denied);

        Assert.Contains(denied ? "daemon_state" : "Re-read the file", result.ResultText, StringComparison.Ordinal);

        Assert.Equal("original text", File.ReadAllText(Path.Combine(_workspace.Root, "retry.txt")));

        FakeMcpConnectionManager mcp = new();

        mcp.Tools.Add(tool);

        ScriptingChatClient chat = new();

        chat.EnqueueStreamToolCall(toolName, "reported-failure", arguments);

        chat.EnqueueStreamTokens("continued after reported failure");

        List<IntelligenceEvent> frames = await CollectStreamAsync(CreateWizard(chat, mcp: mcp), WorkspaceOutcomeRequest(_workspace.Root));

        IntelligenceEvent reported = Assert.Single(frames, static f => f.Type == IntelligenceEventType.ToolResult);

        AssertWorkspaceOutcome(reported, denied ? "nativePolicyDenied" : "nativeOperationFailed");

        Assert.Equal(denied, reported.ToolDenied);

        Assert.Contains(denied ? "daemon_state" : "Re-read the file", reported.Data!, StringComparison.Ordinal);

        Assert.DoesNotContain(frames, static f => f.Type == IntelligenceEventType.ToolError);
    }

    [Fact]
    public async Task Native_attachment_selection_error_remains_actionable_through_bridge()
    {
        using ArcanumTestHomeScope home = new("native-attachment-selection");

        await using McpConnectionManager manager = McpConnectionManagerHarness.Create(
            settings: new() { Features = new() { Attachments = true } });

        IReadOnlyList<AITool> tools = await manager.GetAvailableToolsAsync(_workspace.Root);

        AIFunction attachment = Assert.IsAssignableFrom<AIFunction>(tools.Single(static t => t.Name == "attach_session_file"));

        object? result = await attachment.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["logicalName"] = "missing.txt" }));

        McpToolFailure failure = Assert.IsType<McpToolFailure>(result);

        Assert.False(failure.Outcome.Denied);

        Assert.Equal("No current session; cannot attach a session file.", failure.ToString());
    }

    [Fact]
    public async Task Native_persisted_turn_refusal_is_a_failed_policy_denial()
    {
        using ArcanumTestHomeScope home = new("native-persisted-turn-denial");

        _workspace.WriteFile("unchanged.txt", "original text");

        await using McpConnectionManager manager = McpConnectionManagerHarness.Create();

        IReadOnlyList<AITool> tools = await manager.GetAvailableToolsAsync(_workspace.Root);

        AIFunction edit = Assert.IsAssignableFrom<AIFunction>(tools.Single(static t => t.Name == "edit_file"));

        McpToolFailure result = Assert.IsType<McpToolFailure>(await edit.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["relativePath"] = "unchanged.txt", ["exactSearchText"] = "original text", ["replacementText"] = "changed",
        })));

        Assert.True(result.Outcome.Denied);

        Assert.Equal("This mutating filesystem tool requires a bound persisted assistant-turn context.", result.ResultText);

        Assert.Equal("original text", File.ReadAllText(Path.Combine(_workspace.Root, "unchanged.txt")));
    }

    [Fact]
    public async Task Native_unexpected_catch_is_still_an_infrastructure_fault()
    {
        using ArcanumTestHomeScope home = new("native-unexpected-fault");

        await using McpConnectionManager manager = McpConnectionManagerHarness.Create();

        IReadOnlyList<AITool> tools = await manager.GetAvailableToolsAsync(_workspace.Root);

        AIFunction save = Assert.IsAssignableFrom<AIFunction>(tools.Single(static t => t.Name == "save_memory"));

        InvalidOperationException fault = await Assert.ThrowsAsync<InvalidOperationException>(() => save.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["name"] = "test", ["type"] = "Place", ["facts"] = new[] { "a fact" },
        })).AsTask());

        Assert.Equal("The internal MCP tool returned an unclassified error.", fault.Message);
    }

    private static PingRequest WorkspaceOutcomeRequest(string workspace) => BaseRequest() with
    {
        Prompt = "Perform the supplied tool calls without trying any alternative path.",
        WorkingDirectory = workspace,
        SkipSpellRouting = true,
    };

    private static Dictionary<string, object?> WorkspaceReadArguments(string path, int startLine = 1, int endLine = 1) => new()
    {
        ["relativePath"] = path,
        ["startLine"] = startLine,
        ["endLine"] = endLine,
    };

    private static (string ToolName, Dictionary<string, object?> Arguments) WorkspaceFailureArguments(string scenario) => scenario switch
    {
        "missing-file" => ("read_file", WorkspaceReadArguments("missing.txt")),
        "missing-parent" => ("read_file", WorkspaceReadArguments("missing-parent/missing.txt")),
        "missing-directory" => ("list_directory", new() { ["relativePath"] = "missing-directory" }),
        "file-as-directory" => ("list_directory", new() { ["relativePath"] = "present.txt" }),
        "zero-start" => ("read_file", WorkspaceReadArguments("present.txt", 0, 1)),
        "reversed-range" => ("read_file", WorkspaceReadArguments("present.txt", 3, 2)),
        "excessive-range" => ("read_file", WorkspaceReadArguments("present.txt", 1, 100_000)),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
    };

    private static async Task<FakeMcpConnectionManager> WorkspaceOutcomeToolsAsync(McpConnectionManager manager, string workspace)
    {
        IReadOnlyList<AITool> tools = await manager.GetAvailableToolsAsync(workspace);

        FakeMcpConnectionManager selected = new();

        selected.Tools.AddRange(tools.Where(static tool => tool.Name is "read_file" or "list_directory"));

        return selected;
    }

    private static void AssertWorkspaceOutcome(IntelligenceEvent frame, string expectedCode)
    {
        using JsonDocument wire = JsonDocument.Parse(JsonSerializer.Serialize(frame, ArcanumJsonContext.Default.IntelligenceEvent));

        Assert.True(wire.RootElement.TryGetProperty("toolOutcome", out JsonElement outcome), "The native tool result must carry its typed outcome.");

        Assert.Equal(expectedCode, outcome.GetProperty("code").GetString());
    }

    private sealed class WorkspaceOutcomeExternalClient(CallToolResult response, bool claimsHostOwnership = false) : IMcpClient
    {
        public bool IsHostOwned => claimsHostOwnership;

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<McpBridgeTool>> GetToolsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CallToolResult> CallToolAsync(string toolName, IReadOnlyDictionary<string, object?> arguments, TimeSpan? requestTimeout = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(response);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
