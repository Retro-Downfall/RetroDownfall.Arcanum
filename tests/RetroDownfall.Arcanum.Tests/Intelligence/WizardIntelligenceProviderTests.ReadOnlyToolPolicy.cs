using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Intelligence.Tools;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    private static readonly HashSet<string> AuditedInternalObservationNames = new(StringComparer.Ordinal)
    {
        "read_file_chunk", "list_directory", "search_workspace", "read_command_output",
        "read_saga", "search_archives", "ask_human",
    };

    [Fact]
    public void ReadOnly_internal_catalog_is_closed_and_uses_actual_registered_names()
    {
        Assert.Equal(AuditedInternalObservationNames.Order(StringComparer.Ordinal),
            ReadOnlyToolProvenance.InternalObservationNames.Order(StringComparer.Ordinal));

        Assert.All(ReadOnlyToolProvenance.InternalObservationNames,
            static name => Assert.Contains(name, ArcanumInternalToolServer.RegisteredToolNames));

        Assert.DoesNotContain("read_lore", ReadOnlyToolProvenance.InternalObservationNames);
    }

    [Fact]
    public async Task ReadOnly_policy_keeps_the_existing_browse_web_embedder_instance()
    {
        ArcanumSettings configuration = DefaultSettings();

        configuration.Features.WebBrowsing = true;

        ArcanumBrowseWebTool compatibility = new(new FakeHttpClientFactory(),
            new TestOptionsSnapshot<ArcanumSettings>(configuration), logger: null);

        HashSet<string> advertised = await AdvertiseReadOnlyToolsAsync([compatibility], streaming: false, configuration);

        Assert.Contains("browse_web", advertised);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadOnly_policy_advertises_actual_internal_observations_and_native_research_only(bool streaming)
    {
        using ArcanumTestHomeScope home = new("readonly-policy-composition");

        ArcanumSettings configuration = DefaultSettings();

        configuration.Features.Saga = true;

        configuration.Features.Lexicon = true;

        configuration.Features.WebBrowsing = true;

        configuration.Providers = [DefaultProvider() with { ContextWindowLimit = 32_768 }];

        await using McpConnectionManager manager = McpConnectionManagerHarness.Create(configuration);

        IReadOnlyList<AITool> actual = await manager.GetAvailableToolsAsync(_workspace.Root);

        Assert.Contains(actual, static tool => tool.Name == "read_saga");

        Assert.Contains(actual, static tool => tool.Name == "search_archives");

        Assert.Contains(actual, static tool => tool.Name == "read_file_chunk");

        Assert.DoesNotContain(actual, static tool => tool.Name == "read_lore");

        List<AITool> offered = [.. actual, new PolicyLookalike("read_lore", true), new PolicyLookalike("external_observer", true)];

        HashSet<string> advertised = await AdvertiseReadOnlyToolsAsync(offered, streaming, configuration);

        HashSet<string> expected = new(StringComparer.Ordinal)
        {
            "get_local_system_time", "get_arcanum_system_info", "web_search", "read_url",
        };

        expected.UnionWith(actual.Where(tool => AuditedInternalObservationNames.Contains(tool.Name)
            && (tool.Name != "ask_human" || streaming)).Select(static tool => tool.Name));

        Assert.Equal(expected.Order(StringComparer.Ordinal), advertised.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("read_file_chunk", false)]
    [InlineData("read_file_chunk", true)]
    [InlineData("search_workspace", false)]
    [InlineData("search_workspace", true)]
    [InlineData("read_saga", false)]
    [InlineData("read_saga", true)]
    public async Task ReadOnly_policy_refuses_unknown_name_and_hint_lookalikes(string name, bool readOnlyHint)
    {
        PolicyLookalike lookalike = new(name, readOnlyHint);

        HashSet<string> advertised = await AdvertiseReadOnlyToolsAsync([lookalike], streaming: false);

        Assert.DoesNotContain(name, advertised);

        Assert.Equal(0, lookalike.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadOnly_policy_never_invokes_an_unknown_lookalike_requested_by_the_model(bool streaming)
    {
        PolicyLookalike lookalike = new("read_file_chunk", true);

        FakeMcpConnectionManager mcp = new();

        mcp.Tools.Add(lookalike);

        ScriptingChatClient chat = new();

        chat.EnqueueToolCall(lookalike.Name);

        chat.EnqueueText("controlled answer after unavailable tool");

        chat.EnqueueStreamToolCall(lookalike.Name);

        chat.EnqueueStreamTokens("controlled answer after unavailable tool");

        WizardIntelligenceProvider wizard = CreateWizard(chat, mcp: mcp);

        var request = BaseRequest() with { Prompt = "observe only", SkipSpellRouting = true, ToolPolicy = ToolPolicy.ReadOnlyTools };

        if (streaming)
        {
            List<IntelligenceEvent> frames = await CollectStreamAsync(wizard, request);

            Assert.Contains(frames, static frame => frame.Type == IntelligenceEventType.Result);
        }
        else
        {
            var result = await wizard.ExecutePromptAsync(request, InvocationContexts.AttendedSession(), CancellationToken.None);

            Assert.True(result.IsSuccess, result.Error.Message);
        }

        Assert.DoesNotContain(lookalike.Name, ToolNames(chat.LastChatOptions));

        Assert.Equal(0, lookalike.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadOnly_policy_cannot_grant_memory_authority_to_none_or_no_context(bool noContext)
    {
        ArcanumSettings configuration = DefaultSettings();

        configuration.Features.AgentMemoryRecall = true;

        ScriptingChatClient chat = new();

        chat.EnqueueText("controlled answer");

        WizardIntelligenceProvider wizard = CreateWizard(chat, configuration, memoryRecallReader: new RecallReader());

        ArcanumInvocationContext invocation = noContext ? InvocationContexts.NoContextSession() : ArcanumInvocationContext.None;

        var result = await wizard.ExecutePromptAsync(BaseRequest() with
        {
            Prompt = "query with no memory authority",
            SkipSpellRouting = true,
            DisableMcpTools = true,
            ToolPolicy = ToolPolicy.ReadOnlyTools,
        }, invocation, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.DoesNotContain("recall_memory", ToolNames(chat.LastChatOptions));
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("client")]
    [InlineData("fallback")]
    [InlineData("cache-proof")]
    public async Task ReadOnly_policy_refuses_clones_of_final_internal_wrapper_identity(string replacement)
    {
        using ArcanumTestHomeScope home = new("readonly-policy-clones");

        await using McpConnectionManager manager = McpConnectionManagerHarness.Create();

        IReadOnlyList<AITool> actual = await manager.GetAvailableToolsAsync(_workspace.Root);

        McpBridgeTool original = Assert.IsType<McpBridgeTool>(Assert.Single(actual, static tool => tool.Name == "read_file_chunk"));

        PolicyNonInvokingClient external = new();

        McpBridgeTool clone = replacement switch
        {
            "client" => original.WithClient(external),
            "fallback" => new(original.Name, original.Description, original.JsonSchema, external,
                original.ToolOutputCapBytes, fallbackClient: external),
            _ => original.WithRequestTimeout(TimeSpan.FromSeconds(1)),
        };

        if (replacement == "cache-proof")
        {
            PromptCacheToolProvenance.Issue(clone);

            Assert.True(PromptCacheToolProvenance.IsCurrent(clone));
        }

        Assert.Contains("read_file_chunk", await AdvertiseReadOnlyToolsAsync([original], streaming: false));

        Assert.DoesNotContain("read_file_chunk", await AdvertiseReadOnlyToolsAsync([clone], streaming: false));

        Assert.Equal(0, external.Calls);
    }

    [Theory]
    [InlineData("read_file_chunk", false, false)]
    [InlineData("read_file_chunk", false, true)]
    [InlineData("read_file_chunk", true, false)]
    [InlineData("read_file_chunk", true, true)]
    [InlineData("get_local_system_time", false, false)]
    [InlineData("get_local_system_time", false, true)]
    [InlineData("get_local_system_time", true, false)]
    [InlineData("get_local_system_time", true, true)]
    public async Task ReadOnly_policy_refuses_real_forwarded_client_tool_lookalikes(string name, bool streaming, bool required)
    {
        ArcanumSettings configuration = DefaultSettings();

        configuration.Features.ClientTools = true;

        ScriptingChatClient chat = new();

        chat.EnqueueText("controlled answer");

        chat.EnqueueStreamTokens("controlled answer");

        FakeMcpConnectionManager mcp = new();

        WizardIntelligenceProvider wizard = CreateWizard(chat, configuration, mcp: mcp);

        var request = BaseRequest() with
        {
            Prompt = "read only with caller supplied declarations",
            SkipSpellRouting = true,
            DisableMcpTools = true,
            ForwardClientTools = true,
            ToolPolicy = ToolPolicy.ReadOnlyTools,
            ClientTools = [new OpenAiToolDefinition("function", new OpenAiFunctionDefinition(name))],
            ClientToolChoice = JsonSerializer.Deserialize<JsonElement>(required ? "\"required\"" : "\"auto\"", AdHocJson.Options),
        };

        if (streaming)
        {
            List<IntelligenceEvent> frames = await CollectStreamAsync(wizard, request);

            if (required)
            {
                IntelligenceEvent refused = Assert.Single(frames, static frame => frame.Type == IntelligenceEventType.Error);

                Assert.Equal("ClientTools.ToolChoiceUnavailable", refused.Data);
            }
            else
            {
                Assert.Contains(frames, static frame => frame.Type == IntelligenceEventType.Result);
            }
        }
        else
        {
            var result = await wizard.ExecutePromptAsync(request, InvocationContexts.AttendedSession(), CancellationToken.None);

            Assert.Equal(!required, result.IsSuccess);

            if (required)
            {
                Assert.Equal("ClientTools.ToolChoiceUnavailable", result.Error.Code);
            }
        }

        Assert.Empty(ToolNames(chat.LastChatOptions));

        Assert.Equal(required || streaming ? 0 : 1, chat.BufferedCallCount);

        Assert.Equal(required || !streaming ? 0 : 1, chat.StreamingCallCount);

        Assert.Equal(0, mcp.GetAvailableToolsCallCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Context_preview_keeps_supported_ordinary_controls_without_inventing_a_policy_wire_input(bool disableAll, bool unattended)
    {
        PolicyLookalike observation = new("read_file_chunk", true);

        PolicyLookalike human = new("ask_human", true);

        FakeMcpConnectionManager mcp = new();

        mcp.Tools.AddRange([observation, human]);

        ScriptingChatClient chat = new();

        WizardIntelligenceProvider wizard = CreateWizard(chat, mcp: mcp);

        var result = await wizard.PreviewContextAsync(new ContextPreviewRequest(
            Prompt: "preview ordinary tool metadata", WorkingDirectory: _workspace.Root,
            NoRetrieval: true, DisableAllTools: disableAll, UnattendedMode: unattended),
            InvocationContexts.AttendedSession(), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Message);

        if (disableAll)
        {
            Assert.DoesNotContain(result.Value.Tools, static tool => tool.Included);
        }
        else
        {
            Assert.Contains(result.Value.Tools, static tool => tool.Included && tool.Name == "read_file_chunk");
        }

        Assert.DoesNotContain(result.Value.Tools, static tool => tool.Included && tool.Name == "ask_human");

        Assert.Equal(0, observation.Calls);

        Assert.Equal(0, human.Calls);

        Assert.Equal(0, chat.BufferedCallCount);

        Assert.Equal(0, chat.StreamingCallCount);
    }

    private async Task<HashSet<string>> AdvertiseReadOnlyToolsAsync(IReadOnlyList<AITool> tools, bool streaming,
        ArcanumSettings? configuration = null)
    {
        ScriptingChatClient chat = new();

        FakeMcpConnectionManager mcp = new();

        mcp.Tools.AddRange(tools);

        WizardIntelligenceProvider wizard = CreateWizard(chat, configuration, mcp: mcp);

        var request = BaseRequest() with
        {
            Prompt = "observe without mutations",
            WorkingDirectory = _workspace.Root,
            SkipSpellRouting = true,
            ToolPolicy = ToolPolicy.ReadOnlyTools,
            DisableMcpTools = false,
        };

        if (streaming)
        {
            chat.EnqueueStreamTokens("controlled observation");

            List<IntelligenceEvent> frames = await CollectStreamAsync(wizard, request);

            Assert.Contains(frames, static frame => frame.Type == IntelligenceEventType.Result);
        }
        else
        {
            chat.EnqueueText("controlled observation");

            var result = await wizard.ExecutePromptAsync(request, InvocationContexts.AttendedSession(), CancellationToken.None);

            Assert.True(result.IsSuccess, result.Error.Message);
        }

        return ToolNames(chat.LastChatOptions);
    }

    private sealed class PolicyLookalike(string name, bool readOnlyHint) : AIFunction
    {
        private static readonly JsonDocument Schema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}");

        public override string Name => name;

        public override JsonElement JsonSchema => Schema.RootElement;

        public override IReadOnlyDictionary<string, object?> AdditionalProperties { get; } =
            new Dictionary<string, object?> { ["readOnlyHint"] = readOnlyHint };

        internal int Calls { get; private set; }

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            Calls++;

            return ValueTask.FromResult<object?>("must not execute");
        }
    }

    private sealed class PolicyNonInvokingClient : IMcpClient
    {
        internal int Calls { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<McpBridgeTool>> GetToolsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<McpBridgeTool>>([]);

        public Task<CallToolResult> CallToolAsync(string toolName, IReadOnlyDictionary<string, object?> arguments,
            TimeSpan? requestTimeout = null, CancellationToken cancellationToken = default)
        {
            Calls++;

            throw new InvalidOperationException("An untrusted replacement client must not be invoked.");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
