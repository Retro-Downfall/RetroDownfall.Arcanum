using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Infrastructure.Platform;
using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;
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

        Assert.Contains("search_workspace", names);

        Assert.Contains("write_file", names);

        Assert.Contains("execute_command", names);

    }

    [Fact]
    public async Task ToolsList_without_workspace_omits_workspace_filesystem_tools()
    {

        await using TestMcpSession session = await CreateSessionAsync(
            configureWorkspace: false);

        JsonRpcResponse response = await session.SendRequestAsync(
            "tools/list",
            null);
        McpToolsListResultWire tools = JsonSerializer.Deserialize(
            response.Result!.Value,
            McpJsonSerializerContext.Default.McpToolsListResultWire)!;
        string[] names = tools.Tools.Select(
            static tool => tool.Name).ToArray();

        Assert.DoesNotContain("read_file_chunk", names);
        Assert.DoesNotContain("replace_text_block", names);
        Assert.DoesNotContain("write_file", names);
        Assert.DoesNotContain("list_directory", names);
        Assert.DoesNotContain(
            ToolRiskClassifier.SearchWorkspaceToolName,
            names);
        Assert.DoesNotContain(
            ToolRiskClassifier.ApplyPatchToolName,
            names);
        Assert.DoesNotContain(
            ToolRiskClassifier.WorkspaceCheckToolName,
            names);
        Assert.DoesNotContain(
            ToolRiskClassifier.ExecuteCommandToolName,
            names);
        Assert.Contains("ask_human", names);

    }

    [Fact]
    public async Task ToolsList_search_workspace_schema_exposes_bounded_exact_search_contract()
    {

        await using TestMcpSession session = await CreateSessionAsync();

        JsonRpcResponse response = await session.SendRequestAsync("tools/list", null);

        McpToolsListResultWire tools = JsonSerializer.Deserialize(
            response.Result!.Value,
            McpJsonSerializerContext.Default.McpToolsListResultWire)!;

        McpToolDefinitionWire search = Assert.Single(
            tools.Tools,
            static tool => tool.Name == "search_workspace");
        JsonElement schema = search.InputSchema;
        JsonElement properties = schema.GetProperty("properties");

        Assert.Equal("string", properties.GetProperty("pattern").GetProperty("type").GetString());
        Assert.Equal(
            ["literal", "regex"],
            properties.GetProperty("mode").GetProperty("enum")
                .EnumerateArray()
                .Select(static item => item.GetString()));
        Assert.Equal("boolean", properties.GetProperty("caseSensitive").GetProperty("type").GetString());
        Assert.Equal(
            ["pattern", "mode", "caseSensitive"],
            schema.GetProperty("required")
                .EnumerateArray()
                .Select(static item => item.GetString()));

    }

    [Fact]
    public async Task ToolsList_apply_patch_exposes_canonical_bounded_schema_and_AOT_contract()
    {

        await using TestMcpSession session = await CreateSessionAsync();

        JsonRpcResponse response = await session.SendRequestAsync(
            "tools/list",
            null);
        McpToolsListResultWire tools = JsonSerializer.Deserialize(
            response.Result!.Value,
            McpJsonSerializerContext.Default.McpToolsListResultWire)!;
        McpToolDefinitionWire patch = Assert.Single(
            tools.Tools,
            static tool =>
                tool.Name == ToolRiskClassifier.ApplyPatchToolName);
        JsonElement properties = patch.InputSchema.GetProperty("properties");

        Assert.Equal(
            ["patch", "dryRun"],
            properties.EnumerateObject().Select(static property => property.Name));
        Assert.Equal(
            "string",
            properties.GetProperty("patch").GetProperty("type").GetString());
        Assert.Equal(
            "boolean",
            properties.GetProperty("dryRun").GetProperty("type").GetString());
        Assert.Equal(
            ["patch"],
            patch.InputSchema.GetProperty("required")
                .EnumerateArray()
                .Select(static value => value.GetString()));
        Assert.False(
            patch.InputSchema.GetProperty("additionalProperties").GetBoolean());
        Assert.NotNull(McpJsonSerializerContext.Default.ApplyPatchParams);
        Assert.NotNull(
            McpJsonSerializerContext.Default.WorkspacePatchToolResultEnvelope);

    }

    [Fact]
    public async Task ToolsCall_apply_patch_requires_bound_persisted_invocation_before_planning()
    {

        _workspace.WriteFile("binary-target.txt", "before\0binary");
        await using TestMcpSession session = await CreateSessionAsync();

        McpToolsCallResultWire result = await session.CallToolAsync(
            ToolRiskClassifier.ApplyPatchToolName,
            JsonSerializer.SerializeToElement(
                new ApplyPatchParams(
                    """
                    --- a/binary-target.txt
                    +++ b/binary-target.txt
                    @@ -1 +1 @@
                    -before
                    +after
                    """),
                McpJsonSerializerContext.Default.ApplyPatchParams));

        Assert.False(result.IsError);
        using JsonDocument payload = JsonDocument.Parse(
            Assert.Single(result.Content).Text);
        Assert.Equal(
            "session_required",
            payload.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            "before\0binary",
            await File.ReadAllTextAsync(
                Path.Combine(_workspace.Root, "binary-target.txt")));
        Assert.Empty(
            Directory.GetFiles(
                _workspace.Root,
                "*.arcanum-*",
                SearchOption.AllDirectories));

    }

    [Fact]
    public async Task ToolsCall_apply_patch_binds_pending_receipt_and_returns_the_exact_result()
    {

        _workspace.WriteFile("bound-patch.txt", "before\n");
        await using TestMcpSession session = await CreateSessionAsync();
        RecordingPatchReceiptSink sink = new();
        ApplyPatchParams request = new(
            """
            --- a/bound-patch.txt
            +++ b/bound-patch.txt
            @@ -1 +1 @@
            -before
            +after
            """);
        JsonElement exactArguments = JsonSerializer.SerializeToElement(
            request,
            McpJsonSerializerContext.Default.ApplyPatchParams);
        ApplyPatchInvocationContext context = new(
            SessionId: Guid.Parse("01ffb5fc-fc66-44b3-81a3-cb2914498615"),
            AssistantEntryId: Guid.Parse("df59f2cb-c4b7-451f-aa67-d3103e28bca3"),
            Identity: new ToolInvocationIdentity(
                "turn-bound-patch",
                "provider-call",
                ToolRoundOrdinal: 2,
                CallOrdinal: 1,
                ToolRiskClassifier.ApplyPatchToolName),
            SerializedArguments: exactArguments.GetRawText(),
            ModelUsed: "test-model",
            CreatedAt: DateTimeOffset.Parse(
                "2026-07-26T12:00:00Z",
                System.Globalization.CultureInfo.InvariantCulture),
            Sink: sink);

        using IDisposable binding =
            ApplyPatchInvocationAmbient.Begin(context);

        McpToolsCallResultWire result = await session.CallToolAsync(
            ToolRiskClassifier.ApplyPatchToolName,
            exactArguments);

        Assert.False(result.IsError);
        string exactResult = Assert.Single(result.Content).Text;
        PendingApplyPatchReceipt pending = Assert.Single(sink.Receipts);

        Assert.Equal(exactResult, pending.SerializedResult);
        Assert.Equal(exactArguments.GetRawText(), pending.SerializedArguments);
        Assert.Equal("after\n", await File.ReadAllTextAsync(
            Path.Combine(_workspace.Root, "bound-patch.txt")));

        WorkspaceRollbackResult rollback =
            await pending.RollbackAsync(CancellationToken.None);

        Assert.True(rollback.Complete);
        Assert.Equal("before\n", await File.ReadAllTextAsync(
            Path.Combine(_workspace.Root, "bound-patch.txt")));

    }

    [Fact]
    public async Task ToolsList_workspace_check_is_capability_gated_and_has_no_open_execution_surface()
    {

        FakeWorkspaceCheckRuntime runtime = new(
            new WorkspaceCheckExecutionStatus(
                true,
                false,
                "available"));
        await using TestMcpSession session = await CreateSessionAsync(
            workspaceCheckRuntime: runtime);

        JsonRpcResponse response = await session.SendRequestAsync(
            "tools/list",
            null);
        McpToolsListResultWire tools = JsonSerializer.Deserialize(
            response.Result!.Value,
            McpJsonSerializerContext.Default.McpToolsListResultWire)!;
        McpToolDefinitionWire check = Assert.Single(
            tools.Tools,
            static tool => tool.Name == ToolRiskClassifier.WorkspaceCheckToolName);
        Assert.False(
            check.InputSchema.TryGetProperty(
                "x-profileOptions",
                out _));
        Assert.False(check.InputSchema.GetProperty("additionalProperties").GetBoolean());
        JsonElement[] branches = check.InputSchema
            .GetProperty("oneOf")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(3, branches.Length);

        foreach (JsonElement branch in branches)
        {
            JsonElement properties = branch.GetProperty("properties");
            Assert.Equal(
                ["profile", "options"],
                properties.EnumerateObject()
                    .Select(static property => property.Name));
            Assert.DoesNotContain(
                properties.EnumerateObject(),
                static property => property.Name is
                    "command" or "arguments" or "argumentList" or "argv"
                    or "shell" or "interpreter" or "script");
            Assert.False(
                branch.GetProperty("additionalProperties")
                    .GetBoolean());
            Assert.Equal(
                ["profile"],
                branch.GetProperty("required")
                    .EnumerateArray()
                    .Select(static value => value.GetString()));
            Assert.False(
                properties.GetProperty("options")
                    .GetProperty("additionalProperties")
                    .GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(
                properties.GetProperty("profile")
                    .GetProperty("const")
                    .GetString()));
        }

        JsonElement buildBranch = branches.Single(
            static branch =>
                branch.GetProperty("properties")
                    .GetProperty("profile")
                    .GetProperty("const")
                    .GetString()
                == WorkspaceCheckCatalogDefaults.DotNetBuildProfileId);
        JsonElement buildOptions = buildBranch
            .GetProperty("properties")
            .GetProperty("options")
            .GetProperty("properties");
        Assert.Equal(
            ["configuration", "verbosity"],
            buildOptions.EnumerateObject()
                .Select(static property => property.Name));
        Assert.Equal(
            ["debug", "release"],
            buildOptions.GetProperty("configuration")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(static value => value.GetString()));
        Assert.NotNull(McpJsonSerializerContext.Default.WorkspaceCheckParams);
    }

    [Fact]
    public async Task ToolsList_workspace_check_omits_unavailable_platform_capability()
    {

        FakeWorkspaceCheckRuntime runtime = new(
            new WorkspaceCheckExecutionStatus(
                false,
                true,
                WorkspaceCheckExecutionPolicy.LinuxUnavailableReason));
        await using TestMcpSession session = await CreateSessionAsync(
            workspaceCheckRuntime: runtime);

        JsonRpcResponse response = await session.SendRequestAsync(
            "tools/list",
            null);
        McpToolsListResultWire tools = JsonSerializer.Deserialize(
            response.Result!.Value,
            McpJsonSerializerContext.Default.McpToolsListResultWire)!;

        Assert.DoesNotContain(
            tools.Tools,
            static tool => tool.Name == ToolRiskClassifier.WorkspaceCheckToolName);
    }

    [Fact]
    public async Task ToolsCall_workspace_check_returns_structured_normal_outcome()
    {

        FakeWorkspaceCheckRuntime runtime = new(
            new WorkspaceCheckExecutionStatus(true, false, "available"))
        {
            Result = new WorkspaceCheckToolResultEnvelope
            {
                Status = "failed",
                Code = "check_failed",
                ProfileId = WorkspaceCheckCatalogDefaults.DotNetBuildProfileId,
                SelectedSdkVersion = "10.0.302",
                ExitCode = 1,
            },
        };
        await using TestMcpSession session = await CreateSessionAsync(
            workspaceCheckRuntime: runtime);

        McpToolsCallResultWire result = await session.CallToolAsync(
            ToolRiskClassifier.WorkspaceCheckToolName,
            JsonSerializer.SerializeToElement(
                new WorkspaceCheckParams
                {
                    Profile = WorkspaceCheckCatalogDefaults.DotNetBuildProfileId,
                    Options = new Dictionary<string, string>
                    {
                        ["configuration"] = "release",
                    },
                },
                McpJsonSerializerContext.Default.WorkspaceCheckParams));

        Assert.False(result.IsError);
        using JsonDocument payload = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal(
            "failed",
            payload.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            "10.0.302",
            payload.RootElement.GetProperty("selectedSdkVersion").GetString());
        Assert.Equal(
            WorkspaceCheckCatalogDefaults.DotNetBuildProfileId,
            runtime.LastRequest!.ProfileId);
        Assert.Equal(
            "release",
            runtime.LastRequest.Options["configuration"]);
    }

    [Fact]
    public async Task Stale_workspace_check_invocation_rechecks_capability_and_fails_closed()
    {

        FakeWorkspaceCheckRuntime runtime = new(
            new WorkspaceCheckExecutionStatus(true, false, "available"));
        await using TestMcpSession session = await CreateSessionAsync(
            workspaceCheckRuntime: runtime);
        _ = await session.SendRequestAsync("tools/list", null);
        runtime.Status = new WorkspaceCheckExecutionStatus(
            false,
            true,
            "The pinned executable changed.");

        McpToolsCallResultWire result = await session.CallToolAsync(
            ToolRiskClassifier.WorkspaceCheckToolName,
            JsonSerializer.SerializeToElement(
                new WorkspaceCheckParams
                {
                    Profile = WorkspaceCheckCatalogDefaults.DotNetBuildProfileId,
                },
                McpJsonSerializerContext.Default.WorkspaceCheckParams));

        Assert.False(result.IsError);
        using JsonDocument payload = JsonDocument.Parse(result.Content[0].Text);
        Assert.Equal(
            "unavailable",
            payload.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            "capability_unavailable",
            payload.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, runtime.RunCount);
    }

    [Fact]
    public async Task ToolsCall_workspace_check_receives_host_bound_monotonic_inference_deadline()
    {

        FakeWorkspaceCheckRuntime runtime = new(
            new WorkspaceCheckExecutionStatus(true, false, "available"));
        await using TestMcpSession session = await CreateSessionAsync(
            workspaceCheckRuntime: runtime);
        long before = TimeProvider.System.GetTimestamp();

        using IDisposable deadline =
            WorkspaceCheckInferenceDeadlineAmbient.Begin(
                TimeProvider.System,
                TimeSpan.FromMinutes(10));
        _ = await session.CallToolAsync(
            ToolRiskClassifier.WorkspaceCheckToolName,
            JsonSerializer.SerializeToElement(
                new WorkspaceCheckParams
                {
                    Profile = WorkspaceCheckCatalogDefaults.DotNetBuildProfileId,
                },
                McpJsonSerializerContext.Default.WorkspaceCheckParams));

        Assert.NotNull(runtime.LastRequest);
        Assert.True(
            runtime.LastRequest.InferenceDeadlineTimestamp > before);
    }

    [Fact]
    public async Task ToolsCall_search_workspace_returns_normal_structured_no_match_and_invalid_outcomes()
    {

        await using TestMcpSession session = await CreateSessionAsync();

        McpToolsCallResultWire noMatch = await session.CallToolAsync(
            "search_workspace",
            JsonSerializer.SerializeToElement(
                new SearchWorkspaceParams
                {
                    Pattern = "absent",
                    Mode = "literal",
                    CaseSensitive = true,
                },
                McpJsonSerializerContext.Default.SearchWorkspaceParams));

        McpToolsCallResultWire invalidPattern = await session.CallToolAsync(
            "search_workspace",
            JsonSerializer.SerializeToElement(
                new SearchWorkspaceParams
                {
                    Pattern = "[",
                    Mode = "regex",
                    CaseSensitive = true,
                },
                McpJsonSerializerContext.Default.SearchWorkspaceParams));

        McpToolsCallResultWire invalidMode = await session.CallToolAsync(
            "search_workspace",
            JsonSerializer.SerializeToElement(
                new SearchWorkspaceParams
                {
                    Pattern = "sample",
                    Mode = "fuzzy",
                    CaseSensitive = true,
                },
                McpJsonSerializerContext.Default.SearchWorkspaceParams));

        McpToolsCallResultWire invalidRoot = await session.CallToolAsync(
            "search_workspace",
            JsonSerializer.SerializeToElement(
                new SearchWorkspaceParams
                {
                    Pattern = "sample",
                    Mode = "literal",
                    CaseSensitive = true,
                    Root = "../outside",
                },
                McpJsonSerializerContext.Default.SearchWorkspaceParams));

        Assert.False(noMatch.IsError);
        Assert.False(invalidPattern.IsError);
        Assert.False(invalidMode.IsError);
        Assert.False(invalidRoot.IsError);

        using JsonDocument noMatchJson = JsonDocument.Parse(noMatch.Content[0].Text);
        using JsonDocument invalidPatternJson = JsonDocument.Parse(invalidPattern.Content[0].Text);
        using JsonDocument invalidModeJson = JsonDocument.Parse(invalidMode.Content[0].Text);
        using JsonDocument invalidRootJson = JsonDocument.Parse(invalidRoot.Content[0].Text);

        Assert.Equal("no_match", noMatchJson.RootElement.GetProperty("status").GetString());
        Assert.Equal("invalid_pattern", invalidPatternJson.RootElement.GetProperty("status").GetString());
        Assert.Equal("invalid_request", invalidModeJson.RootElement.GetProperty("status").GetString());
        Assert.Equal("invalid_mode", invalidModeJson.RootElement.GetProperty("code").GetString());
        Assert.Equal("invalid_request", invalidRootJson.RootElement.GetProperty("status").GetString());
        Assert.Equal("invalid_root", invalidRootJson.RootElement.GetProperty("code").GetString());

    }

    [Fact]
    public async Task NotificationsCancelled_cancels_in_flight_search_workspace_regex()
    {
        _workspace.WriteFile(
            "expensive-search.txt",
            new string('a', 100_000) + "!");
        CodingToolsSettings codingTools = ArcanumRuntimeDefaults.CodingTools with
        {
            Search = ArcanumRuntimeDefaults.CodingTools.Search with
            {
                RegexTimeoutMilliseconds = 1_000,
                MaxElapsedMilliseconds = 5_000,
            },
        };

        await using TestMcpSession session = await CreateSessionAsync(
            codingToolsSettings: codingTools);

        JsonElement arguments = JsonSerializer.SerializeToElement(
            new SearchWorkspaceParams
            {
                Pattern = @"^(a+)+(?=b)",
                Mode = "regex",
                CaseSensitive = true,
            },
            McpJsonSerializerContext.Default.SearchWorkspaceParams);
        JsonElement callParams = JsonSerializer.SerializeToElement(
            new McpToolsCallParams
            {
                Name = ToolRiskClassifier.SearchWorkspaceToolName,
                Arguments = arguments,
            },
            McpJsonSerializerContext.Default.McpToolsCallParams);

        (int requestId, Task<JsonRpcResponse> responseTask) = await session
            .SendRequestFireAndForgetAsync("tools/call", callParams);

        await Task.Delay(50);
        await session.SendCancelNotificationAsync(requestId);

        JsonRpcResponse response = await responseTask.WaitAsync(
            TimeSpan.FromSeconds(2));

        Assert.Null(response.Error);

        McpToolsCallResultWire result = JsonSerializer.Deserialize(
            response.Result!.Value,
            McpJsonSerializerContext.Default.McpToolsCallResultWire)!;

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task Search_workspace_wire_and_model_budgets_preserve_valid_cumulative_truncation()
    {
        _workspace.WriteFile(
            "many-search-results.txt",
            string.Join(
                '\n',
                Enumerable.Range(1, 500)
                    .Select(static index =>
                        $"needle {index:D3} {new string('x', 96)}")));
        IntelligenceSettings intelligenceSettings = ArcanumRuntimeDefaults.Intelligence with
        {
            EnableLexiconSystem = false,
            EnableArchiveSearch = false,
            ToolOutputCapBytes = 4_096,
        };
        CodingToolsSettings codingTools = ArcanumRuntimeDefaults.CodingTools with
        {
            Search = ArcanumRuntimeDefaults.CodingTools.Search with
            {
                MaxMatches = 1_000,
                MaxPreviewChars = 128,
            },
        };

        await using TestMcpSession session = await CreateSessionAsync(
            intelligenceSettings: intelligenceSettings,
            maxJsonRpcLineBytes: 16_384,
            codingToolsSettings: codingTools);

        McpToolsCallResultWire result = await session.CallToolAsync(
            ToolRiskClassifier.SearchWorkspaceToolName,
            JsonSerializer.SerializeToElement(
                new SearchWorkspaceParams
                {
                    Pattern = "needle",
                    Mode = "literal",
                    CaseSensitive = true,
                },
                McpJsonSerializerContext.Default.SearchWorkspaceParams));
        string wireText = Assert.Single(result.Content).Text;

        using JsonDocument wireJson = JsonDocument.Parse(wireText);
        int wireRetained = wireJson.RootElement.GetProperty("matches").GetArrayLength();
        int total = wireJson.RootElement.GetProperty("totalMatchCount").GetInt32();

        Assert.True(wireJson.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(
            total - wireRetained,
            wireJson.RootElement.GetProperty("omittedMatchCount").GetInt32());

        string modelText = ToolExecutionPipeline.MaterializeToolResultForModel(
            ToolRiskClassifier.SearchWorkspaceToolName,
            new TrustedStructuredToolResult(
                TrustedStructuredToolResultKind.WorkspaceSearch,
                wireText),
            new ToolResultMaterializer(),
            new ToolResultMaterializerOptions(
                MaxTokens: 10_000,
                MaxUtf8Bytes: 1_024));

        using JsonDocument modelJson = JsonDocument.Parse(modelText);
        int modelRetained = modelJson.RootElement.GetProperty("matches").GetArrayLength();

        Assert.InRange(modelRetained, 1, wireRetained - 1);
        Assert.Equal(total, modelJson.RootElement.GetProperty("totalMatchCount").GetInt32());
        Assert.Equal(
            total - modelRetained,
            modelJson.RootElement.GetProperty("omittedMatchCount").GetInt32());
        Assert.True(modelJson.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task ToolsList_names_match_registered_handlers_when_all_features_enabled()
    {

        IntelligenceSettings allFeatures = ArcanumRuntimeDefaults.Intelligence with
        {

            EnableLexiconSystem = true,

            EnableArchiveSearch = true,

        };

        await using TestMcpSession session = await CreateSessionAsync(
            intelligenceSettings: allFeatures,
            conclaveEnabled: true,
            sagaEnabled: true,
            a2aClientEnabled: true,
            attachmentsToolEnabled: true,
            workspaceCheckRuntime: new FakeWorkspaceCheckRuntime(
                new WorkspaceCheckExecutionStatus(true, false, "available")));

        JsonRpcResponse response = await session.SendRequestAsync("tools/list", null);

        McpToolsListResultWire tools = JsonSerializer.Deserialize(
            response.Result!.Value,
            McpJsonSerializerContext.Default.McpToolsListResultWire)!;

        HashSet<string> listedNames = tools.Tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        HashSet<string> registeredNames = session.Server.RegisteredToolHandlerNamesForTests
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("send_commlink_alert", listedNames);

        Assert.Equal(registeredNames, listedNames);

    }

    [Fact]
    public async Task Client_channel_completion_cancels_and_classifies_in_flight_apply_patch()
    {

        const string relativePath = "channel-completion-patch.txt";
        _workspace.WriteFile(relativePath, "before\n");
        await using TestMcpSession session = await CreateSessionAsync();
        CancellationObservingPatchReceiptSink sink = new();
        ApplyPatchParams request = new(
            $"""
             --- a/{relativePath}
             +++ b/{relativePath}
             @@ -1 +1 @@
             -before
             +after
             """);
        JsonElement exactArguments = JsonSerializer.SerializeToElement(
            request,
            McpJsonSerializerContext.Default.ApplyPatchParams);
        ApplyPatchInvocationContext context = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new ToolInvocationIdentity(
                "channel-completion-turn",
                "channel-completion-call",
                ToolRoundOrdinal: 0,
                CallOrdinal: 0,
                ToolRiskClassifier.ApplyPatchToolName),
            exactArguments.GetRawText(),
            "test-model",
            DateTimeOffset.UtcNow,
            sink);
        JsonElement callParams = JsonSerializer.SerializeToElement(
            new McpToolsCallParams
            {
                Name = ToolRiskClassifier.ApplyPatchToolName,
                Arguments = exactArguments,
            },
            McpJsonSerializerContext.Default.McpToolsCallParams);

        Task<JsonRpcResponse> response;
        using (ApplyPatchInvocationAmbient.Begin(context))
        {
            (_, response) =
                await session.SendRequestFireAndForgetAsync(
                    "tools/call",
                    callParams);
        }

        _ = response.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
        await sink.HandoffStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        await session.CloseClientChannelAsync();
        await sink.CancellationObserved.Task.WaitAsync(
            TimeSpan.FromSeconds(2));
        await session.ServerCompletion.WaitAsync(
            TimeSpan.FromSeconds(2));

        Assert.True(context.CancellationClassified);
        Assert.True(context.RequiresTurnFailure);
        Assert.Equal(
            MandatoryToolInteractionAppendOutcome.Ambiguous,
            context.HandoffOutcome);
        Assert.Equal(
            "after\n",
            await File.ReadAllTextAsync(
                Path.Combine(_workspace.Root, relativePath)));

    }

    [Fact]
    public async Task Client_channel_completion_before_patch_commit_cancels_without_ghost_mutation()
    {

        const string relativePath = "channel-precommit-patch.txt";
        _workspace.WriteFile(relativePath, "before\n");
        await using TestMcpSession session = await CreateSessionAsync();
        CancellationBeforeCommitPatchReceiptSink sink = new();
        ApplyPatchParams request = new(
            $"""
             --- a/{relativePath}
             +++ b/{relativePath}
             @@ -1 +1 @@
             -before
             +after
             """);
        JsonElement exactArguments = JsonSerializer.SerializeToElement(
            request,
            McpJsonSerializerContext.Default.ApplyPatchParams);
        ApplyPatchInvocationContext context = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new ToolInvocationIdentity(
                "channel-precommit-turn",
                "channel-precommit-call",
                ToolRoundOrdinal: 0,
                CallOrdinal: 0,
                ToolRiskClassifier.ApplyPatchToolName),
            exactArguments.GetRawText(),
            "test-model",
            DateTimeOffset.UtcNow,
            sink);
        JsonElement callParams = JsonSerializer.SerializeToElement(
            new McpToolsCallParams
            {
                Name = ToolRiskClassifier.ApplyPatchToolName,
                Arguments = exactArguments,
            },
            McpJsonSerializerContext.Default.McpToolsCallParams);

        Task<JsonRpcResponse> response;
        using (ApplyPatchInvocationAmbient.Begin(context))
        {
            (_, response) =
                await session.SendRequestFireAndForgetAsync(
                    "tools/call",
                    callParams);
        }

        _ = response.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
        await sink.ProbeStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        await session.CloseClientChannelAsync();
        await sink.CancellationObserved.Task.WaitAsync(
            TimeSpan.FromSeconds(2));
        await session.ServerCompletion.WaitAsync(
            TimeSpan.FromSeconds(2));

        Assert.True(context.WasDispatched);
        Assert.True(context.RequiresTurnFailure);
        Assert.Null(context.HandoffOutcome);
        Assert.Equal(
            "before\n",
            await File.ReadAllTextAsync(
                Path.Combine(_workspace.Root, relativePath)));
        Assert.Empty(
            Directory.GetFiles(
                _workspace.Root,
                "*.arcanum-*",
                SearchOption.AllDirectories));

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

        IntelligenceSettings intelligenceSettings = ArcanumRuntimeDefaults.Intelligence with
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
        using IDisposable persistedTurn = BeginPersistedTurn();

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
    public async Task ToolsCall_write_file_requires_bound_persisted_turn_context()
    {

        await using TestMcpSession session = await CreateSessionAsync();
        const string relativePath = "unbound-write.txt";

        McpToolsCallResultWire result = await session.CallToolAsync(
            "write_file",
            JsonSerializer.SerializeToElement(
                new WriteFileParams(relativePath, "must not be written"),
                McpJsonSerializerContext.Default.WriteFileParams));

        Assert.True(result.IsError);
        Assert.Contains(
            "persisted",
            Assert.Single(result.Content).Text,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(
            File.Exists(
                Path.Combine(_workspace.Root, relativePath)));

    }

    [Fact]
    public async Task ToolsCall_replace_text_block_replaces_verbatim_block()
    {

        await using TestMcpSession session = await CreateSessionAsync();
        using IDisposable persistedTurn = BeginPersistedTurn();

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
    public async Task ToolsCall_mixed_case_policy_name_does_not_change_exact_wire_routing()
    {

        await using TestMcpSession session = await CreateSessionAsync();
        using IDisposable persistedTurn = BeginPersistedTurn();
        const string relativePath = "wire-routing.txt";

        McpToolsCallResultWire result = await session.CallToolAsync(
            "WRITE_FILE",
            JsonSerializer.SerializeToElement(
                new WriteFileParams(relativePath, "must not route"),
                McpJsonSerializerContext.Default.WriteFileParams));

        Assert.True(result.IsError);
        Assert.Contains(
            "Unknown tool",
            Assert.Single(result.Content).Text,
            StringComparison.Ordinal);
        Assert.False(
            File.Exists(
                Path.Combine(_workspace.Root, relativePath)));

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

        IntelligenceSettings settings = ArcanumRuntimeDefaults.Intelligence with
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

        // Oversized lines still carry a parseable JSON-RPC id when present; echo it (Wave 2 MCP error rule).
        Assert.Equal(JsonValueKind.Number, response.Id.ValueKind);

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
    public async Task Duplicate_in_flight_request_id_is_rejected_with_json_rpc_error()
    {

        await using TestMcpSession session = await CreateSessionAsync();

        string sentinelPath = Path.Combine(_workspace.Root, "dup-id-sentinel.txt");

        (string command, string[] argumentList) = ResolveDelayedWriteCommand(sentinelPath);

        JsonElement arguments = JsonSerializer.SerializeToElement(
            new ExecuteCommandParams { Command = command, ArgumentList = argumentList },
            McpJsonSerializerContext.Default.ExecuteCommandParams);

        JsonElement callParams = JsonSerializer.SerializeToElement(
            new McpToolsCallParams { Name = "execute_command", Arguments = arguments },
            McpJsonSerializerContext.Default.McpToolsCallParams);

        const int sharedId = 4242;

        await session.WriteRequestWithFixedIdAsync(sharedId, "tools/call", callParams);

        // Give the first call time to register in _inFlightToolCalls before the duplicate arrives.
        await Task.Delay(300);

        await session.WriteRequestWithFixedIdAsync(sharedId, "tools/call", callParams);

        // Duplicate is rejected immediately (JSON-RPC error); the first call is still sleeping.
        JsonRpcResponse duplicate = await session.ReadNextResponseAsync()
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(duplicate.Error);
        Assert.Equal(-32600, duplicate.Error!.Code);
        Assert.Contains("Duplicate", duplicate.Error.Message, StringComparison.OrdinalIgnoreCase);

        await session.SendCancelNotificationAsync(sharedId);

        JsonRpcResponse first = await session.ReadNextResponseAsync()
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(first.Error);

        McpToolsCallResultWire result = JsonSerializer.Deserialize(
            first.Result!.Value,
            McpJsonSerializerContext.Default.McpToolsCallResultWire)!;

        Assert.True(result.IsError);

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
        using IDisposable persistedTurn = BeginPersistedTurn();

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
        using IDisposable persistedTurn = BeginPersistedTurn();

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

        IntelligenceSettings settings = ArcanumRuntimeDefaults.Intelligence with
        {
            EnableLexiconSystem = true,
            EnableArchiveSearch = false,
        };

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

        IntelligenceSettings settings = ArcanumRuntimeDefaults.Intelligence with
        {
            EnableLexiconSystem = false,
            EnableArchiveSearch = false,
        };

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

        IntelligenceSettings settings = ArcanumRuntimeDefaults.Intelligence with
        {
            EnableLexiconSystem = true,
            EnableArchiveSearch = false,
        };

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

        IntelligenceSettings settings = ArcanumRuntimeDefaults.Intelligence with
        {
            EnableLexiconSystem = true,
            EnableArchiveSearch = false,
        };

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

    [Fact]
    public async Task LineHandler_exception_with_request_id_returns_sanitized_internal_error()
    {

        await using TestMcpSession session = await CreateSessionAsync();

        const string secret = "super-secret-exception-detail";

        session.Server.LineHandlerFaultForTesting = _ => new InvalidOperationException(secret);

        JsonRpcResponse response = await session.SendRequestAsync("tools/list", parameters: null);

        Assert.NotNull(response.Error);

        Assert.Equal(-32603, response.Error!.Code);

        Assert.Equal("Internal error.", response.Error.Message);

        Assert.DoesNotContain(secret, response.Error.Message, StringComparison.Ordinal);

        Assert.Null(response.Error.Data);

        string wire = JsonSerializer.Serialize(response, McpJsonSerializerContext.Default.JsonRpcResponse);

        Assert.DoesNotContain(secret, wire, StringComparison.Ordinal);

    }

    [Fact]
    public async Task LineHandler_exception_on_notification_writes_no_response()
    {

        await using TestMcpSession session = await CreateSessionAsync();

        session.Server.LineHandlerFaultForTesting = _ => new InvalidOperationException("should-not-leak");

        string notification =
            """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":1}}""";

        JsonRpcResponse? leaked = await session.SendRawLineWithTimeoutAsync(
            notification,
            TimeSpan.FromMilliseconds(300));

        Assert.Null(leaked);

        session.Server.LineHandlerFaultForTesting = null;

        JsonRpcResponse response = await session.SendRequestAsync("tools/list", parameters: null);

        Assert.Null(response.Error);

    }

    private async Task<TestMcpSession> CreateSessionAsync(
        bool configureWorkspace = true,
        IntelligenceSettings? intelligenceSettings = null,
        long maxFileReadSizeBytes = 1024 * 1024,
        int maxJsonRpcLineBytes = 2_097_152,
        bool conclaveEnabled = false,
        bool sagaEnabled = false,
        bool a2aClientEnabled = false,
        bool attachmentsToolEnabled = false,
        IA2AClientService? a2aClientService = null,
        CodingToolsSettings? codingToolsSettings = null,
        IWorkspaceCheckRuntime? workspaceCheckRuntime = null)
    {

        string? normalizedRoot = configureWorkspace
            ? Path.GetFullPath(_workspace.Root)
            : null;

        IntelligenceSettings settings = intelligenceSettings
            ?? (ArcanumRuntimeDefaults.Intelligence with
            {
                EnableLexiconSystem = false,
                EnableArchiveSearch = false,
            });

        ServiceCollection services = new();

        services.AddSingleton<ISanctumGuard, PermissiveSanctumGuard>();

        services.AddSingleton<RetroDownfall.Arcanum.Core.Platform.IProcessResourceLimiter, ProcessResourceLimiter>();

        // Existing MCP tool tests exercise command plumbing, not the OS FS jail. Opt into the escape
        // hatch so nested CI/agent sandboxes (where sandbox-exec cannot apply) do not fail-closed.
        services.AddSingleton<IOptionsMonitor<ArcanumSettings>>(
            new TestOptionsMonitor<ArcanumSettings>(
                new ArcanumSettings
                {
                    Security = new SecuritySettings { AllowUnsandboxedToolChildren = true },
                }));

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
            attachmentsToolEnabled: attachmentsToolEnabled,
            maxJsonRpcLineBytes: maxJsonRpcLineBytes,
            logger: NullLogger<ArcanumInternalToolServer>.Instance,
            codingToolsSettings: codingToolsSettings,
            workspaceCheckRuntime: workspaceCheckRuntime);

        CancellationTokenSource cts = new();

        Task serverTask = server.RunAsync(cts.Token);

        await transport.StartAsync();

        return new TestMcpSession(transport, server, serverTask, cts);

    }

    private static IDisposable BeginPersistedTurn() =>
        PersistedToolInvocationAmbient.Begin(
            new PersistedToolInvocationContext(
                Guid.NewGuid(),
                Guid.NewGuid()));

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

        public Task ServerCompletion => serverTask;

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

        public ValueTask CloseClientChannelAsync() =>
            transport.DisposeAsync();

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

        public Task WriteRequestWithFixedIdAsync(int id, string method, JsonElement? parameters)
        {

            JsonRpcRequest request = new()
            {
                Method = method,
                Params = parameters,
                Id = JsonSerializer.SerializeToElement(id, McpJsonSerializerContext.Default.Int32),
            };

            return transport.WriteRequestAsync(request);

        }

        public Task<JsonRpcResponse> ReadNextResponseAsync() => ReadResponseAsync();

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

    private sealed class FakeWorkspaceCheckRuntime(
        WorkspaceCheckExecutionStatus status) : IWorkspaceCheckRuntime
    {

        public WorkspaceCheckExecutionStatus Status { get; set; } = status;

        public WorkspaceCheckToolResultEnvelope Result { get; init; } =
            new()
            {
                Status = "ok",
                ProfileId = WorkspaceCheckCatalogDefaults.DotNetBuildProfileId,
                SelectedSdkVersion = "10.0.302",
            };

        public WorkspaceCheckRuntimeRequest? LastRequest { get; private set; }

        public int RunCount { get; private set; }

        public WorkspaceCheckExecutionStatus GetStatus(string workspaceRoot) =>
            Status;

        public Task<WorkspaceCheckToolResultEnvelope> RunAsync(
            WorkspaceCheckRuntimeRequest request,
            CancellationToken cancellationToken)
        {

            cancellationToken.ThrowIfCancellationRequested();
            RunCount++;
            LastRequest = request;
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingPatchReceiptSink
        : IApplyPatchPendingReceiptSink
    {
        internal List<PendingApplyPatchReceipt> Receipts { get; } = [];

        public ValueTask<ApplyPatchReceiptProbeResult> ProbeAsync(
            ApplyPatchReceiptProbe probe,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                new ApplyPatchReceiptProbeResult(
                    ApplyPatchReceiptProbeOutcome.NotFound,
                    SerializedResult: null));

        public ValueTask<ApplyPatchReceiptPreflightResult> PreflightAsync(
            ApplyPatchReceiptPreflight preflight,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                new ApplyPatchReceiptPreflightResult(
                    ApplyPatchReceiptPreflightOutcome.Admitted,
                    SerializedResult: null));

        public ValueTask<MandatoryToolInteractionAppendOutcome>
            PersistRecoveryReceiptAsync(
                ApplyPatchRecoveryReceipt receipt,
                CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                MandatoryToolInteractionAppendOutcome.NewlyCommitted);

        public ValueTask<ApplyPatchPendingReceiptHandoffResult> HandoffAsync(
            PendingApplyPatchReceipt receipt,
            CancellationToken cancellationToken)
        {

            cancellationToken.ThrowIfCancellationRequested();
            Receipts.Add(receipt);

            return ValueTask.FromResult(
                new ApplyPatchPendingReceiptHandoffResult(
                    MandatoryToolInteractionAppendOutcome.NewlyCommitted,
                    Cleanup: null,
                    Rollback: null));

        }
    }

    private sealed class CancellationObservingPatchReceiptSink
        : IApplyPatchPendingReceiptSink
    {
        internal TaskCompletionSource HandoffStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<ApplyPatchReceiptProbeResult> ProbeAsync(
            ApplyPatchReceiptProbe probe,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                new ApplyPatchReceiptProbeResult(
                    ApplyPatchReceiptProbeOutcome.NotFound,
                    SerializedResult: null));

        public ValueTask<ApplyPatchReceiptPreflightResult> PreflightAsync(
            ApplyPatchReceiptPreflight preflight,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                new ApplyPatchReceiptPreflightResult(
                    ApplyPatchReceiptPreflightOutcome.Admitted,
                    SerializedResult: null));

        public ValueTask<MandatoryToolInteractionAppendOutcome>
            PersistRecoveryReceiptAsync(
                ApplyPatchRecoveryReceipt receipt,
                CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                MandatoryToolInteractionAppendOutcome.Ambiguous);

        public async ValueTask<ApplyPatchPendingReceiptHandoffResult>
            HandoffAsync(
                PendingApplyPatchReceipt receipt,
                CancellationToken cancellationToken)
        {

            HandoffStarted.TrySetResult();

            try
            {
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                throw;
            }

            throw new InvalidOperationException(
                "The cancellation-observing handoff unexpectedly completed.");
        }
    }

    private sealed class CancellationBeforeCommitPatchReceiptSink
        : IApplyPatchPendingReceiptSink
    {
        internal TaskCompletionSource ProbeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ApplyPatchReceiptProbeResult>
            ProbeAsync(
                ApplyPatchReceiptProbe probe,
                CancellationToken cancellationToken)
        {
            ProbeStarted.TrySetResult();

            try
            {
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                throw;
            }

            throw new InvalidOperationException(
                "The pre-commit cancellation probe unexpectedly completed.");
        }

        public ValueTask<ApplyPatchReceiptPreflightResult> PreflightAsync(
            ApplyPatchReceiptPreflight preflight,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "A canceled receipt probe must not reach preflight.");

        public ValueTask<MandatoryToolInteractionAppendOutcome>
            PersistRecoveryReceiptAsync(
                ApplyPatchRecoveryReceipt receipt,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "A canceled receipt probe must not reach recovery persistence.");

        public ValueTask<ApplyPatchPendingReceiptHandoffResult> HandoffAsync(
            PendingApplyPatchReceipt receipt,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "A canceled receipt probe must not reach handoff.");
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
