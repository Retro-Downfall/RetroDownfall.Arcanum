using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Run_bound_Mcp_cast_uses_one_child_coordinator_and_replays_exact_identity(int transport)
    {
        await using Fixture f = await Fixture.CreateAsync(productionOptions: true);

        f.Settings.CurrentValue.Features.Conclave = true;

        f.Settings.CurrentValue.Providers = [new() { Name = "provider", Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://api.example.test/v1", Models = [new() { Name = "model" }] }];

        GuildCallerContext owner = await f.OperatorAsync();

        GuildToolGrant tool = new(GuildToolSourceKind.InternalMcp, "arcanum", "create_agent_task");

        var root = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with { Tools = [tool] } });

        Assert.True(root.IsSuccess, root.Error.Message);

        GuildLocalInferenceExecution execution = await ToolExecutionAsync(f, owner, root.Value);

        var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        ChildQueue queue = new();

        using GuildLocalToolRegistry registry = new(execution, input.Value, new ConclaveArchmage(null!, f.Settings, f.Store, queue));

        GuildToolInvocationContext context = f.Store.CreateToolContext(execution, registry, _ => true);

        await using GuildMcpRoute route = await GuildMcpRoute.CreateAsync(transport != 0, generation: transport == 3);

        using IDisposable ambient = GuildToolInvocationAmbient.Begin(context);

        using IDisposable frame = registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), tool);

        GuildQuestDto first = transport >= 2 ? await route.CastBridgeAsync(context, tool) : await route.CastAsync();

        GuildQuestDto replay = transport >= 2 ? await route.CastBridgeAsync(context, tool) : await route.CastAsync();

        Assert.Equal(first.Id, replay.Id);

        Assert.Equal(first.RunId, replay.RunId);

        Assert.Equal(first.OperationId, replay.OperationId);

        Assert.Equal(root.Value.Quest.Id, first.ParentQuestId);

        Assert.Equal(root.Value.Quest.RootQuestId, first.RootQuestId);

        Assert.NotNull(first.SessionId);

        Assert.NotEqual(root.Value.Quest.SessionId, first.SessionId);

        Assert.Equal(root.Value.Quest.CampaignId, first.CampaignId);

        Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));

        Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM LongRunningOperations"));

        Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM session_campaign_bindings WHERE BindingKindCode=1"));
    }

    [Theory]
    [InlineData("closed-frame")]
    [InlineData("closed-registry")]
    [InlineData("feature-off")]
    [InlineData("revoked")]
    [InlineData("forged-context")]
    [InlineData("wrong-name")]
    [InlineData("duplicate-argument")]
    public async Task Queued_Mcp_cast_rechecks_exact_live_host_frame_before_creating_work(string boundary)
    {
        await using Fixture f = await Fixture.CreateAsync(productionOptions: true);

        f.Settings.CurrentValue.Features.Conclave = true;

        f.Settings.CurrentValue.Providers = [new() { Name = "provider", Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://api.example.test/v1", Models = [new() { Name = "model" }] }];

        GuildCallerContext owner = await f.OperatorAsync();

        GuildToolGrant tool = new(GuildToolSourceKind.InternalMcp, "arcanum", "create_agent_task");

        var root = await f.IssueAsync(owner, f.Request with { Grant = f.Request.Grant with { Tools = [tool] } });

        Assert.True(root.IsSuccess, root.Error.Message);

        GuildLocalInferenceExecution execution = await ToolExecutionAsync(f, owner, root.Value);

        var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        ChildQueue queue = new();

        using GuildLocalToolRegistry registry = new(execution, input.Value, new ConclaveArchmage(null!, f.Settings, f.Store, queue));

        GuildToolInvocationContext context = boundary == "forged-context"
            ? new(execution.Driver.Execution, f.Authority, _ => true)
            : f.Store.CreateToolContext(execution, registry, _ => true);

        await using GuildMcpRoute route = await GuildMcpRoute.CreateAsync(false, deferServer: true);

        using IDisposable ambient = GuildToolInvocationAmbient.Begin(context);

        using IDisposable frame = registry.BeginInvocation(Guid.NewGuid(), Guid.NewGuid(), tool);

        Task<(bool Error, string Text)> pending = route.CallAsync(boundary == "wrong-name" ? "get_sending" : "create_agent_task",
            boundary == "duplicate-argument" ? """{"goal":"First goal","goal":"Second goal","name":"Child"}""" : null);

        await route.RequestWritten;

        if (boundary == "closed-frame") frame.Dispose();

        if (boundary == "closed-registry") registry.Dispose();

        if (boundary == "feature-off") f.Settings.CurrentValue.Features.Guild = false;

        if (boundary == "revoked")
        {
            Guid key = Guid.NewGuid();

            var current = await f.Store.GetAsync(owner, root.Value.Quest.Id, default);

            Assert.True(current.IsSuccess, current.Error.Message);

            var revoked = await f.Authority.RevokeAsync(owner, new(key, new string('A', 64)), current.Value.Quest.SignetId,
                new(key, current.Value.Quest.SignetRevision, GuildCancellationReason.OperatorRequested), default);

            Assert.True(revoked.IsSuccess, revoked.Error.Message);
        }

        route.StartServer();

        var result = await pending;

        Assert.True(result.Error, result.Text);

        Assert.Contains("Guild.", result.Text);

        if (boundary == "duplicate-argument") Assert.StartsWith("Guild.InvalidRequest:", result.Text);

        Assert.False(queue.Pending.Reader.TryRead(out _));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM LongRunningOperations"));
    }

    private sealed class GuildMcpRoute(ServiceProvider services, InProcessMcpTransport? raw,
        SdkMcpClientWrapper? sdk, CancellationTokenSource lifetime) : IAsyncDisposable
    {
        private Task? _serverTask;

        private int _request;

        private McpBridgeTool? _bridge;

        private McpClientGeneration? _generation;

        private readonly TaskCompletionSource _written = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task RequestWritten => _written.Task;

        private Func<Task>? _runServer;

        internal void StartServer() => _serverTask ??= Task.Run(_runServer!);

        internal static async Task<GuildMcpRoute> CreateAsync(bool useSdk, bool deferServer = false, bool generation = false, long outputCapBytes = 1_048_576)
        {
            ServiceProvider services = new ServiceCollection()
                .AddSingleton<IOptionsMonitor<ArcanumSettings>>(new TestOptionsMonitor<ArcanumSettings>(new() { Features = new() { Guild = true, Conclave = true } }))
                .BuildServiceProvider();

            IServiceScopeFactory scopeFactory = services.GetRequiredService<IServiceScopeFactory>();

            CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(20));

            GuildMcpRoute route;

            if (useSdk)
            {
                var (toServer, fromServer, server) = InProcessMcpTransport.CreateServerChannelPair(new HumanPromptRegistry(),
                    scopeFactory, new GuildMcpPacer(), null, 16, new IntelligenceSettings { ToolOutputCapBytes = outputCapBytes }, 1024,
                    true, false, false, false, 1_048_576, NullLogger<ArcanumInternalToolServer>.Instance);

                ChannelClientTransport transport = new(toServer, fromServer, 1_048_576, server.AmbientConnectionKey);

                SdkMcpClientWrapper sdk = new(transport,
                    new McpClientOptions { ClientInfo = new Implementation { Name = "guild-route-tests", Version = "1" } },
                    TimeSpan.FromSeconds(10), 65536, 1_048_576, new McpElicitationSink(), internalServer: server);

                route = new(services, null, sdk, lifetime);

                route._runServer = () => server.RunAsync(lifetime.Token);

                if (!deferServer) route.StartServer();

                await sdk.InitializeAsync(lifetime.Token);

                if (generation) route._generation = new(sdk);

                IReadOnlyList<McpBridgeTool> tools = route._generation is { } owned
                    ? await owned.GetToolsAsync(lifetime.Token) : await sdk.GetToolsAsync(lifetime.Token);

                route._bridge = tools.Single(function => function.Name == "create_agent_task");
            }
            else
            {
                var (transport, server) = InProcessMcpTransport.CreatePair(new HumanPromptRegistry(), scopeFactory,
                    new GuildMcpPacer(), null, 16, new IntelligenceSettings { ToolOutputCapBytes = outputCapBytes }, 1024, true, false, false, false, 1_048_576);

                route = new(services, transport, null, lifetime);

                route._runServer = () => server.RunAsync(lifetime.Token);

                if (!deferServer) route.StartServer();

                await transport.StartAsync(lifetime.Token);
            }

            return route;
        }

        internal async Task<GuildQuestDto> CastBridgeAsync(GuildToolInvocationContext context, GuildToolGrant tool)
        {
            var bridge = _bridge!.WithGuildAuthority(context, tool);

            object? result = await bridge.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments
            {
                ["goal"] = "Produce a contribution", ["name"] = "Child",
            }, lifetime.Token);

            string text = Assert.IsType<string>(result);

            Assert.DoesNotContain("Guild.ToolBoundaryUnavailable", text);

            return JsonSerializer.Deserialize(text, GuildJsonContext.Default.GuildQuestDto)!;
        }

        internal async Task<GuildQuestDto> CastAsync()
        {
            var result = await CallAsync();

            Assert.False(result.Error, result.Text);

            return JsonSerializer.Deserialize(result.Text, GuildJsonContext.Default.GuildQuestDto)!;
        }

        internal async Task<(bool Error, string Text)> CallAsync(string name = "create_agent_task", string? argumentsJson = null)
        {
            if (sdk is not null)
            {
                var result = await sdk.CallToolAsync(name, new Dictionary<string, object?>
                {
                    ["goal"] = "Produce a contribution", ["name"] = "Child",
                }, cancellationToken: lifetime.Token);

                return (result.IsError == true, Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
            }
            else
            {
                using JsonDocument arguments = JsonDocument.Parse(argumentsJson ?? """{"goal":"Produce a contribution","name":"Child"}""");

                JsonElement parameters = JsonSerializer.SerializeToElement(new McpToolsCallParams
                {
                    Name = name, Arguments = arguments.RootElement.Clone(),
                }, McpJsonSerializerContext.Default.McpToolsCallParams);

                await raw!.WriteRequestAsync(new()
                {
                    Id = JsonSerializer.SerializeToElement(++_request, McpJsonSerializerContext.Default.Int32),
                    Method = "tools/call", Params = parameters,
                }, lifetime.Token);

                _written.TrySetResult();

                var envelope = await raw.InboundReader.ReadAsync(lifetime.Token);

                Assert.Null(envelope.Response!.Error);

                JsonElement result = envelope.Response.Result!.Value;

                return (result.TryGetProperty("isError", out var error) && error.GetBoolean(),
                    result.GetProperty("content")[0].GetProperty("text").GetString()!);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync();

            if (_generation is not null) await _generation.DisposeAsync();
            else if (sdk is not null) await sdk.DisposeAsync();

            if (raw is not null) await raw.DisposeAsync();

            try { if (_serverTask is not null) await _serverTask; }
            catch (OperationCanceledException) { }

            lifetime.Dispose();

            await services.DisposeAsync();
        }
    }

    private sealed class GuildMcpPacer : IUnseenServantPacer
    {
        public Task<bool> SetDynamicIntervalAsync(string jobName, int intervalMinutes, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public int GetEffectiveInterval(UnseenServantJob job) => 0;

        public Task HydrateAsync(IReadOnlyList<UnseenServantWatermark> watermarks, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
