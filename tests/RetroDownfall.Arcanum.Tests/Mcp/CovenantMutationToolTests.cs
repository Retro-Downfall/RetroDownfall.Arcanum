using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Events;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Mcp;

/// <summary>
/// The two Covenant MCP mutation tools: what they advertise, what they refuse, and what a staged
/// mutation is allowed to say back to the model (§10.14).
/// </summary>
public sealed class CovenantMutationToolTests
{

    [Fact]
    public async Task Neither_tool_is_advertised_while_the_feature_is_off()
    {
        await using CovenantToolSession session = await CovenantToolSession.CreateAsync(featureEnabled: false);

        McpToolsListResultWire tools = await session.ListToolsAsync();

        Assert.DoesNotContain(tools.Tools, static tool => tool.Name == CovenantToolNames.ProposeCovenant);
        Assert.DoesNotContain(tools.Tools, static tool => tool.Name == CovenantToolNames.RetireCovenant);
    }

    [Fact]
    public async Task Neither_tool_is_advertised_while_the_canonical_tier_is_unhealthy()
    {
        await using CovenantToolSession session = await CovenantToolSession.CreateAsync(
            canonical: CovenantCapabilityState.Degraded);

        McpToolsListResultWire tools = await session.ListToolsAsync();

        Assert.DoesNotContain(tools.Tools, static tool => tool.Name == CovenantToolNames.ProposeCovenant);
        Assert.DoesNotContain(tools.Tools, static tool => tool.Name == CovenantToolNames.RetireCovenant);
    }

    [Fact]
    public async Task The_proposal_tool_advertises_a_hand_authored_input_and_output_schema()
    {
        await using CovenantToolSession session = await CovenantToolSession.CreateAsync();

        McpToolsListResultWire tools = await session.ListToolsAsync();

        McpToolDefinitionWire propose = Assert.Single(
            tools.Tools,
            static tool => tool.Name == CovenantToolNames.ProposeCovenant);

        JsonElement retire = ArcanumInternalToolServer.BuildRetireCovenantSchema();

        Assert.Equal(["content", "key"], PropertyNames(propose.InputSchema));
        Assert.Equal(["key", "lane"], PropertyNames(retire));
        Assert.NotNull(propose.OutputSchema);

        // Everything an agent could use to widen its own reach is absent from the wire, not merely
        // rejected by the server.
        foreach (string forbidden in (string[])["scope", "campaignId", "origin", "lifecycle", "revision", "attachment_id"])
        {
            Assert.DoesNotContain(forbidden, PropertyNames(propose.InputSchema));
            Assert.DoesNotContain(forbidden, PropertyNames(retire));
        }
    }

    [Fact]
    public async Task Retirement_is_withheld_from_the_advertised_set_it_cannot_be_granted_from()
    {
        await using CovenantToolSession session = await CovenantToolSession.CreateAsync();

        McpToolsListResultWire tools = await session.ListToolsAsync();

        // A healthy Covenant tier advertises exactly the tools a turn can mint a capability for.
        // Minting a retirement capability needs the preflight disclosure and Ward receipt no
        // production caller builds, so every retirement would refuse; advertising it anyway teaches a
        // model that the capability is broken rather than absent.
        Assert.Contains(tools.Tools, static tool => tool.Name == CovenantToolNames.ProposeCovenant);
        Assert.DoesNotContain(tools.Tools, static tool => tool.Name == CovenantToolNames.RetireCovenant);

        // The handler stays registered regardless, so a stale or direct invocation still fails closed
        // rather than reaching an unregistered name.
        McpToolsCallResultWire refused = await session.CallRetireAsync(
            "campaign.a",
            nameof(CovenantLane.Proposed));

        Assert.Equal(ErrorCodes.Covenant.IneligibleTurn, Failure(refused).Code);
    }

    [Fact]
    public async Task An_ineligible_turn_gets_a_typed_failure_rather_than_a_staged_mutation()
    {
        await using CovenantToolSession session = await CovenantToolSession.CreateAsync();

        McpToolsCallResultWire result = await session.CallProposeAsync("response.style", "concise");

        Assert.True(result.IsError);
        Assert.Equal(ErrorCodes.Covenant.IneligibleTurn, Failure(result).Code);
    }

    [Fact]
    public async Task A_call_made_while_the_feature_is_off_refuses_even_with_a_capability()
    {
        await using CovenantToolSession session = await CovenantToolSession.CreateAsync(featureEnabled: false);

        session.RegisterProposalCapability();

        McpToolsCallResultWire result = await session.CallProposeAsync("response.style", "concise");

        Assert.Equal(ErrorCodes.Covenant.Unavailable, Failure(result).Code);
    }

    [Fact]
    public async Task A_proposal_stages_one_campaign_scoped_proposed_mutation()
    {
        await using CovenantToolSession session = await CovenantToolSession.CreateAsync();

        session.RegisterProposalCapability();

        McpToolsCallResultWire result = await session.CallProposeAsync(
            "response.style",
            "concise and direct");

        CovenantMutationStagedResultWire staged = Staged(result);

        Assert.False(result.IsError);
        Assert.Equal("staged", staged.Status);
        Assert.Equal(nameof(CovenantScope.Campaign), staged.Scope);
        Assert.Equal(nameof(CovenantLane.Proposed), staged.Lane);
        Assert.Equal(nameof(CovenantOperation.Set), staged.Operation);
        Assert.Equal(0, staged.ExpectedRevision);
        Assert.NotNull(staged.RenderedHash);
        Assert.Equal(1, session.Collector.StagedCount);
    }

    [Fact]
    public async Task A_staged_result_repeats_neither_the_key_nor_the_content()
    {
        await using CovenantToolSession session = await CovenantToolSession.CreateAsync();

        session.RegisterProposalCapability();

        McpToolsCallResultWire result = await session.CallProposeAsync(
            "personal.detail",
            "the operator's home address is 12 Wizard Lane");

        string rendered = JsonSerializer.Serialize(
            result,
            McpJsonSerializerContext.Default.McpToolsCallResultWire);

        Assert.DoesNotContain("Wizard Lane", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("personal.detail", rendered, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_proposal_answers_the_model_that_the_change_is_staged_and_not_written()
    {
        await using CovenantToolSession session = await CovenantToolSession.CreateAsync();

        session.RegisterProposalCapability();

        McpToolsCallResultWire result = await session.CallProposeAsync("response.style", "concise");

        string text = Assert.Single(result.Content).Text;

        Assert.Contains("staged", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("saved to", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_proposal_for_a_key_the_plan_already_holds_uses_its_revision_without_probing()
    {
        await using CovenantToolSession session = await CovenantToolSession.CreateAsync();

        session.RegisterProposalCapability();

        McpToolsCallResultWire result = await session.CallProposeAsync("proposed.a", "an updated preference");

        Assert.Equal(0, session.HeadProbe.ProbeCount);
        Assert.Equal(3, Staged(result).ExpectedRevision);
    }

    [Fact]
    public async Task A_proposal_for_an_unplanned_key_probes_exactly_once()
    {
        await using CovenantToolSession session = await CovenantToolSession.CreateAsync();

        session.HeadProbe.SetPresent("unplanned.key", revision: 7);

        session.RegisterProposalCapability();

        McpToolsCallResultWire result = await session.CallProposeAsync("unplanned.key", "a preference");

        Assert.Equal(1, session.HeadProbe.ProbeCount);
        Assert.Equal(7, Staged(result).ExpectedRevision);
    }

    [Fact]
    public async Task An_agent_cannot_reactivate_a_key_the_operator_retired()
    {
        await using CovenantToolSession session = await CovenantToolSession.CreateAsync();

        session.HeadProbe.SetRetired("retired.key", revision: 4);

        session.RegisterProposalCapability();

        McpToolsCallResultWire result = await session.CallProposeAsync("retired.key", "please remember this again");

        Assert.Equal(ErrorCodes.Covenant.LifecycleConflict, Failure(result).Code);
        Assert.Equal(0, session.Collector.StagedCount);
    }

    [Fact]
    public async Task A_provider_call_with_unattributable_content_cannot_author_memory()
    {
        await using CovenantToolSession session = await CovenantToolSession.CreateAsync();

        session.RegisterProposalCapability(
            materialization: CovenantCapabilityFixtures.Materialization(unprovenanced: true));

        McpToolsCallResultWire result = await session.CallProposeAsync("response.style", "concise");

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, Failure(result).Code);
        Assert.Equal(0, session.Collector.StagedCount);
    }

    [Fact]
    public async Task A_proposal_capability_cannot_be_spent_on_a_retirement()
    {
        await using CovenantToolSession session = await CovenantToolSession.CreateAsync();

        session.RegisterProposalCapability();

        McpToolsCallResultWire result = await session.CallRetireAsync("campaign.a", nameof(CovenantLane.Proposed));

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, Failure(result).Code);
    }

    [Fact]
    public async Task A_retirement_stages_the_exact_target_its_ward_was_shown()
    {
        await using CovenantToolSession session = await CovenantToolSession.CreateAsync();

        session.RegisterRetirementCapability();

        McpToolsCallResultWire result = await session.CallRetireAsync("campaign.a", nameof(CovenantLane.Proposed));

        CovenantMutationStagedResultWire staged = Staged(result);

        Assert.False(result.IsError);
        Assert.Equal(nameof(CovenantOperation.Retire), staged.Operation);
        Assert.Equal(4, staged.ExpectedRevision);
        Assert.Null(staged.RenderedHash);
        Assert.Equal(1, session.Collector.StagedCount);
    }

    [Fact]
    public async Task A_retirement_that_names_another_target_is_refused()
    {
        await using CovenantToolSession session = await CovenantToolSession.CreateAsync();

        session.RegisterRetirementCapability();

        McpToolsCallResultWire result = await session.CallRetireAsync("some.other.key", nameof(CovenantLane.Proposed));

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, Failure(result).Code);
        Assert.Equal(0, session.Collector.StagedCount);
    }

    [Fact]
    public async Task A_retirement_the_operator_declined_stages_nothing()
    {
        await using CovenantToolSession session = await CovenantToolSession.CreateAsync();

        session.RegisterRetirementCapability(
            ward: CovenantCapabilityFixtures.WardReceipt(CovenantWardDecision.Denied));

        McpToolsCallResultWire result = await session.CallRetireAsync("campaign.a", nameof(CovenantLane.Proposed));

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, Failure(result).Code);
        Assert.Equal(0, session.Collector.StagedCount);
    }

    [Fact]
    public async Task A_retirement_naming_a_malformed_key_is_refused_as_invalid_rather_than_stale()
    {
        await using CovenantToolSession session = await CovenantToolSession.CreateAsync();

        session.RegisterRetirementCapability();

        McpToolsCallResultWire result = await session.CallRetireAsync("Campaign.A", nameof(CovenantLane.Proposed));

        Assert.Equal(ErrorCodes.Covenant.InvalidScope, Failure(result).Code);
        Assert.Equal(0, session.Collector.StagedCount);
    }

    [Fact]
    public async Task A_retirement_with_an_unparsable_lane_is_refused_before_anything_else()
    {
        await using CovenantToolSession session = await CovenantToolSession.CreateAsync();

        session.RegisterRetirementCapability();

        McpToolsCallResultWire result = await session.CallRetireAsync("campaign.a", "proposed");

        Assert.Equal(ErrorCodes.Covenant.InvalidScope, Failure(result).Code);
    }

    [Fact]
    public async Task The_capability_is_drained_and_its_request_id_freed_once_the_handler_returns()
    {
        await using CovenantToolSession session = await CovenantToolSession.CreateAsync();

        CovenantToolInvocationContext capability = session.RegisterProposalCapability();

        _ = await session.CallProposeAsync("response.style", "concise");

        Assert.Equal(CovenantToolCapabilityState.Disposed, capability.State);
        Assert.Equal(0, session.Registry.CountForTests);
    }

    [Fact]
    public async Task An_exact_tool_replay_returns_the_original_receipt_and_consumes_no_slot()
    {
        await using CovenantToolSession session = await CovenantToolSession.CreateAsync();

        session.RegisterProposalCapability(toolCallId: "call-1");

        CovenantMutationStagedResultWire first = Staged(
            await session.CallProposeAsync("response.style", "concise"));

        session.RegisterProposalCapability(toolCallId: "call-1");

        CovenantMutationStagedResultWire replay = Staged(
            await session.CallProposeAsync("response.style", "concise"));

        Assert.Equal(first.MutationId, replay.MutationId);
        Assert.Equal(first.TargetId, replay.TargetId);
        Assert.Equal(1, session.Collector.StagedCount);
    }

    [Fact]
    public async Task Reusing_one_tool_call_identity_with_different_input_fails_closed()
    {
        await using CovenantToolSession session = await CovenantToolSession.CreateAsync();

        session.RegisterProposalCapability(toolCallId: "call-1");

        _ = await session.CallProposeAsync("response.style", "concise");

        session.RegisterProposalCapability(toolCallId: "call-1");

        McpToolsCallResultWire conflict = await session.CallProposeAsync("response.style", "verbose");

        Assert.Equal(ErrorCodes.Security.IdempotencyConflict, Failure(conflict).Code);
    }

    private static CovenantMutationStagedResultWire Staged(McpToolsCallResultWire result) =>
        JsonSerializer.Deserialize(
            result.StructuredContent!.Value,
            McpJsonSerializerContext.Default.CovenantMutationStagedResultWire)!;

    private static CovenantMutationFailureResultWire Failure(McpToolsCallResultWire result) =>
        JsonSerializer.Deserialize(
            result.StructuredContent!.Value,
            McpJsonSerializerContext.Default.CovenantMutationFailureResultWire)!;

    private static string[] PropertyNames(JsonElement schema) =>
        [.. schema.GetProperty("properties")
            .EnumerateObject()
            .Select(static property => property.Name)
            .Order(StringComparer.Ordinal)];

    private sealed class StubAvailability(CovenantAvailabilitySnapshot snapshot) : ICovenantAvailability
    {

        public CovenantAvailabilitySnapshot Current { get; } = snapshot;

    }

    private sealed class FakeEventBus : IEventBus
    {

        public void Publish<T>(T @event) where T : notnull
        {
        }

        public async IAsyncEnumerable<T> Subscribe<T>(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
            where T : notnull
        {
            await Task.CompletedTask;

            yield break;
        }

    }

    private sealed class CovenantToolSession : IAsyncDisposable
    {

        private readonly InProcessMcpTransport _transport;

        private readonly Task _serverTask;

        private readonly CancellationTokenSource _lifetime;

        private readonly string _connectionKey;

        private int _nextId;

        private CovenantToolSession(
            InProcessMcpTransport transport,
            Task serverTask,
            CancellationTokenSource lifetime,
            string connectionKey,
            CovenantToolCapabilityRegistry registry,
            CovenantTurnPlan plan,
            CovenantMutationCollector collector,
            CovenantCapabilityFixtures.StubHeadProbe headProbe)
        {
            _transport = transport;

            _serverTask = serverTask;

            _lifetime = lifetime;

            _connectionKey = connectionKey;

            Registry = registry;

            Plan = plan;

            Collector = collector;

            HeadProbe = headProbe;
        }

        public CovenantToolCapabilityRegistry Registry { get; }

        public CovenantTurnPlan Plan { get; }

        public CovenantMutationCollector Collector { get; }

        public CovenantCapabilityFixtures.StubHeadProbe HeadProbe { get; }

        public static async Task<CovenantToolSession> CreateAsync(
            bool featureEnabled = true,
            CovenantCapabilityState canonical = CovenantCapabilityState.Healthy)
        {
            ServiceCollection services = [];

            services.AddSingleton<ICovenantCompiler, CovenantCompiler>();

            services.AddSingleton<CovenantToolCapabilityRegistry>();

            services.AddSingleton<ICovenantAvailability>(
                new StubAvailability(Snapshot(featureEnabled, canonical)));

            services.AddSingleton<IOptionsMonitor<ArcanumSettings>>(
                new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

            ServiceProvider provider = services.BuildServiceProvider();

            IServiceScopeFactory scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            IntelligenceSettings intelligenceSettings = ArcanumRuntimeDefaults.Intelligence with
            {
                EnableLexiconSystem = false,
                EnableArchiveSearch = false,
            };

            (InProcessMcpTransport transport, ArcanumInternalToolServer server) = InProcessMcpTransport.CreatePair(
                new HumanPromptRegistry(),
                scopeFactory,
                new UnseenServantPacer(
                    new FakeEventBus(),
                    new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()),
                    scopeFactory,
                    NullLogger<UnseenServantPacer>.Instance),
                workspaceRootNormalizedOrNull: null,
                listDirectoryMaxPaths: 64,
                intelligenceSettings: intelligenceSettings,
                maxFileReadSizeBytes: 1024 * 1024,
                conclaveEnabled: false,
                sagaEnabled: false,
                a2aClientEnabled: false,
                attachmentsToolEnabled: false,
                maxJsonRpcLineBytes: 2_097_152,
                logger: NullLogger<ArcanumInternalToolServer>.Instance);

            CancellationTokenSource lifetime = new();

            Task serverTask = server.RunAsync(lifetime.Token);

            await transport.StartAsync();

            CovenantTurnPlan plan = CovenantTask6Fixture.IntegrationPlan();

            return new CovenantToolSession(
                transport,
                serverTask,
                lifetime,
                server.AmbientConnectionKey,
                provider.GetRequiredService<CovenantToolCapabilityRegistry>(),
                plan,
                new CovenantMutationCollector(Guid.NewGuid(), plan.Digest, CovenantTask6Fixture.BranchId),
                new CovenantCapabilityFixtures.StubHeadProbe());
        }

        public CovenantToolInvocationContext RegisterProposalCapability(
            string toolCallId = "call-1",
            ProviderCallMaterializationSnapshot? materialization = null) =>
            Register(
                CovenantToolNames.ProposeCovenant,
                toolCallId,
                materialization,
                retirementPreflight: null,
                wardReceipt: null);

        public CovenantToolInvocationContext RegisterRetirementCapability(
            string toolCallId = "call-1",
            CovenantToolWardReceipt? ward = null) =>
            Register(
                CovenantToolNames.RetireCovenant,
                toolCallId,
                materialization: null,
                CovenantCapabilityFixtures.RetirementPreflight(),
                ward ?? CovenantCapabilityFixtures.WardReceipt(CovenantWardDecision.Approved));

        public async Task<McpToolsListResultWire> ListToolsAsync()
        {
            JsonRpcResponse response = await SendAsync("tools/list", null).ConfigureAwait(false);

            return JsonSerializer.Deserialize(
                response.Result!.Value,
                McpJsonSerializerContext.Default.McpToolsListResultWire)!;
        }

        public Task<McpToolsCallResultWire> CallProposeAsync(string key, string content) =>
            CallToolAsync(
                CovenantToolNames.ProposeCovenant,
                JsonSerializer.SerializeToElement(
                    new ProposeCovenantParams(key, content),
                    McpJsonSerializerContext.Default.ProposeCovenantParams));

        public Task<McpToolsCallResultWire> CallRetireAsync(string key, string lane) =>
            CallToolAsync(
                CovenantToolNames.RetireCovenant,
                JsonSerializer.SerializeToElement(
                    new RetireCovenantParams(key, lane),
                    McpJsonSerializerContext.Default.RetireCovenantParams));

        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel();

            try
            {
                await _serverTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            await _transport.DisposeAsync().ConfigureAwait(false);

            _lifetime.Dispose();
        }

        private CovenantToolInvocationContext Register(
            string toolName,
            string toolCallId,
            ProviderCallMaterializationSnapshot? materialization,
            CovenantRetirementPreflight? retirementPreflight,
            CovenantToolWardReceipt? wardReceipt)
        {
            CovenantToolCapabilityNonce nonce = CovenantToolCapabilityNonce.Create();

            CovenantToolInvocationContext capability = new(
                Collector,
                CovenantCapabilityFixtures.Campaign(),
                CovenantCapabilityFixtures.Admission(Plan),
                materialization ?? CovenantCapabilityFixtures.Materialization(),
                HeadProbe,
                nonce,
                toolName,
                toolCallId,
                retirementPreflight,
                wardReceipt,
                CancellationToken.None);

            Assert.True(Registry.TryRegister(
                _connectionKey,
                (_nextId + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                capability,
                nonce));

            return capability;
        }

        private async Task<McpToolsCallResultWire> CallToolAsync(string name, JsonElement arguments)
        {
            JsonElement paramsElement = JsonSerializer.SerializeToElement(
                new McpToolsCallParams { Name = name, Arguments = arguments },
                McpJsonSerializerContext.Default.McpToolsCallParams);

            JsonRpcResponse response = await SendAsync("tools/call", paramsElement).ConfigureAwait(false);

            return JsonSerializer.Deserialize(
                response.Result!.Value,
                McpJsonSerializerContext.Default.McpToolsCallResultWire)!;
        }

        private async Task<JsonRpcResponse> SendAsync(string method, JsonElement? parameters)
        {
            int id = Interlocked.Increment(ref _nextId);

            JsonRpcRequest request = new()
            {
                Method = method,
                Params = parameters,
                Id = JsonSerializer.SerializeToElement(id, McpJsonSerializerContext.Default.Int32),
            };

            await _transport.WriteRequestAsync(request).ConfigureAwait(false);

            McpInboundEnvelope envelope = await _transport.InboundReader.ReadAsync().ConfigureAwait(false);

            Assert.Equal(McpInboundKind.Response, envelope.Kind);

            return envelope.Response!;
        }

        private static CovenantAvailabilitySnapshot Snapshot(
            bool featureEnabled,
            CovenantCapabilityState canonical) =>
            new(
                Generation: 1,
                featureEnabled,
                canonical,
                CanonicalSchemaVersion: 1,
                CanonicalInstalledFingerprint: "fingerprint",
                CovenantCapabilityState.Healthy,
                AcceleratorSchemaVersion: 1,
                AcceleratorInstalledFingerprint: "fingerprint",
                CovenantTask6Fixture.DatasetGeneration,
                CanonicalSequence: 1,
                CoreCampaignDeletionSequence: 0,
                AppliedDatasetGeneration: CovenantTask6Fixture.DatasetGeneration,
                AppliedSequence: 1,
                AppliedCampaignDeletionSequence: 0,
                AcceleratorEpoch: 1,
                CovenantFtsSynchronizationState.Synchronized,
                RebuildRequired: false,
                CovenantHealthTransition.Bootstrap,
                CanonicalDiagnosticCode: null,
                AcceleratorDiagnosticCode: null);

    }

}
