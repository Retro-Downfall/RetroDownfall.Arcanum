using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Events;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// R-008: every per-turn ambient a tool reads is visible while the tool runs, in both response modes.
/// </summary>
/// <remarks>
/// <c>RunInferenceAttemptAsync</c> is an async iterator, and an <c>AsyncLocal</c> written in one
/// <c>MoveNextAsync</c> segment is rolled back when that segment returns at a <c>yield return</c>.
/// Every tool call runs in the segment after its own ToolCall frame, so an ambient published before
/// that frame reads null inside the tool unless it is re-established immediately before the call.
/// </remarks>
public sealed partial class WizardIntelligenceProviderTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StreamingAndBufferedToolCall_ObservesAllTurnAmbients(bool streaming)
    {
        const string toolName = "record_turn_ambients";

        Guid sessionId = Guid.NewGuid();

        Guid attachmentId = Guid.NewGuid();

        byte[] payload = System.Text.Encoding.UTF8.GetBytes("explicitly attached notes");

        SessionAttachmentRecord record = new(
            attachmentId,
            sessionId,
            EntryId: null,
            PendingTurnId: null,
            SessionAttachmentState.Bound,
            "notes",
            "notes.txt",
            Version: 1,
            RelativePath: $"noop/{attachmentId:N}",
            ContentSha256: attachmentId.ToString("N"),
            MimeType: "text/plain",
            ByteLength: payload.Length,
            SessionAttachmentKind.Text,
            DateTimeOffset.UtcNow);

        NoOpSessionAttachmentStore store = new(
            records: new Dictionary<Guid, SessionAttachmentRecord> { [attachmentId] = record },
            openRead: (_, _) => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)));

        List<ObservedTurnAmbients> observed = [];

        FakeMcpConnectionManager mcp = new();

        mcp.Tools.Add(
            AIFunctionFactory.Create(
                () =>
                {
                    observed.Add(ObservedTurnAmbients.Capture(attachmentId));

                    return "recorded";
                },
                toolName,
                "records the turn ambients a tool can see"));

        ScriptingChatClient chat = new();

        if (streaming)
        {
            chat.EnqueueStreamToolCall(toolName, "call-1");

            chat.EnqueueStreamTokens("done");
        }
        else
        {
            chat.EnqueueToolCall(toolName, "call-1");

            chat.EnqueueText("done");
        }

        ArcanumSettings defaults = DefaultSettings();

        ArcanumSettings settings = defaults with
        {
            Cost = defaults.Cost with
            {
                Pricing = new PricingSettings
                {
                    DefaultPricing = new ModelPricingEntry
                    {
                        InputPer1M = 1m,
                        OutputPer1M = 1m,
                    },
                },
            },
        };

        RecordingBudgetReservationService reservations = new();

        CovenantToolCapabilityRegistry capabilities = new();

        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            settings,
            grimoire: new FakeGrimoireRepository
            {
                Session = new Session { Id = sessionId, Entries = [] },
            },
            mcp: mcp,
            turnRunWriter: new RecordingTurnRunWriter(),
            budgetReservationService: reservations,
            sessionAttachmentStore: store,
            covenantDispatch: StagingCovenantGate(),
            covenantToolCapabilities: capabilities);

        PingRequest request = BaseRequest() with
        {
            Prompt = "record the ambients",
            SessionId = sessionId,
            AttachmentReferences = [attachmentId],
            SkipSpellRouting = true,
        };

        ArcanumInvocationContext invocation = InvocationContexts.AttendedSession(CovenantTask6Fixture.CampaignId);

        if (streaming)
        {
            List<IntelligenceEvent> events = [];

            await foreach (IntelligenceEvent evt in wizard.StreamPromptAsync(request, invocation, CancellationToken.None))
            {
                events.Add(evt);
            }

            Assert.Contains(events, static e => e.Type == IntelligenceEventType.Result);
        }
        else
        {
            Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
                request,
                invocation,
                CancellationToken.None);

            Assert.True(result.IsSuccess, result.IsFailure ? $"{result.Error.Code}: {result.Error.Message}" : null);
        }

        ObservedTurnAmbients seen = Assert.Single(observed);

        Assert.Equal(sessionId, seen.SessionId);

        Assert.NotNull(seen.Ledger);

        Assert.Equal(sessionId, seen.Ledger!.SessionId);

        Assert.True(seen.HasMaterializedAttachmentContent);

        Assert.True(seen.CanResolveAttachment);

        // The inject-once tracker and the provider round are the turn's own, not an empty default:
        // without the tracker an attachment could be injected on every round.
        Assert.True(seen.RepeatedInjectionRefused);

        Assert.Equal(1, seen.ProviderRound);

        Assert.NotNull(reservations.LastRequest);

        Assert.NotNull(seen.BudgetReservationId);

        Assert.NotNull(seen.CovenantStaging);

        // The turn is over, so nothing it published may outlive it: the gate's per-Session state is
        // the bridge server-side tools resolve through, and a stale one would hand the next turn's tool
        // calls this turn's materialized attachments.
        Assert.False(AttachmentMemoryGateAmbient.HasSessionStateForTests(sessionId));
    }

    /// <summary>
    /// R-008 / V-S17-01: a model's <c>scribe_lexicon</c> call in a Campaign-bound Session writes the
    /// Campaign's tier, not the installation's.
    /// </summary>
    /// <remarks>
    /// The in-process server resolves the scope from the Session the client send boundary bound to the
    /// request, and that boundary reads the session ambient of the tool call's own flow. With the ambient
    /// lost at the ToolCall frame, nothing was bound and the write fell to the global tier, visible to
    /// every other Campaign.
    /// </remarks>
    [Fact]
    public async Task StreamingToolCall_ScribeLexicon_InCampaignBoundSession_WritesCampaignScope()
    {
        Guid sessionId = Guid.NewGuid();

        Guid campaignId = Guid.NewGuid();

        SessionBoundMemoryScopeResolver resolver = new(sessionId, campaignId);

        FakeLexiconService lexicon = new();

        await using LexiconToolServer server = await LexiconToolServer.CreateAsync(resolver, lexicon);

        FakeMcpConnectionManager mcp = new();

        mcp.Tools.Add(server.ScribeTool());

        ScriptingChatClient chat = new();

        chat.EnqueueStreamToolCall(
            "scribe_lexicon",
            "call-scribe",
            new Dictionary<string, object?>
            {
                ["name"] = "Alice",
                ["type"] = "Person",
                ["facts"] = new[] { "Prefers concise answers." },
            });

        chat.EnqueueStreamTokens("noted");

        WizardIntelligenceProvider wizard = CreateWizard(
            chat,
            grimoire: new FakeGrimoireRepository
            {
                Session = new Session { Id = sessionId, Entries = [] },
            },
            mcp: mcp);

        List<IntelligenceEvent> events = await CollectStreamAsync(
            wizard,
            BaseRequest() with { Prompt = "remember Alice", SessionId = sessionId, SkipSpellRouting = true });

        Assert.Contains(events, static e => e.Type == IntelligenceEventType.Result);

        McpToolsCallResultWire result = Assert.Single(server.Results);

        Assert.False(result.IsError, result.Content?.FirstOrDefault()?.Text);

        Assert.Equal([sessionId], resolver.Requests);

        Result<LexiconEntryDto?> campaignEntry = await lexicon.GetByNameInScopeAsync(
            "Alice",
            LexiconScope.ForCampaign(campaignId));

        Assert.Equal(campaignId, campaignEntry.Value?.ScopeCampaignId);

        Result<LexiconEntryDto?> globalEntry = await lexicon.GetByNameInScopeAsync("Alice", LexiconScope.Global);

        Assert.Null(globalEntry.Value);
    }

    /// <summary>
    /// With Campaign scoping on, a Lexicon write that arrives with no Session behind it is refused
    /// rather than landing in the installation tier every Campaign reads.
    /// </summary>
    [Fact]
    public async Task ScribeLexicon_WithCampaignScopingOnAndNoSession_FailsClosed()
    {
        SessionBoundMemoryScopeResolver resolver = new(Guid.NewGuid(), Guid.NewGuid());

        FakeLexiconService lexicon = new();

        await using LexiconToolServer server = await LexiconToolServer.CreateAsync(resolver, lexicon);

        Guid? previous = SessionAttachmentToolAmbient.CurrentSessionId;

        SessionAttachmentToolAmbient.CurrentSessionId = null;

        try
        {
            McpToolsCallResultWire result = await server.ScribeAsync("Mallory", "Person", ["Unscoped fact."]);

            Assert.True(result.IsError);

            Assert.Contains("Session", result.Content![0].Text!, StringComparison.Ordinal);
        }
        finally
        {
            SessionAttachmentToolAmbient.CurrentSessionId = previous;
        }

        Result<LexiconEntryDto?> globalEntry = await lexicon.GetByNameInScopeAsync("Mallory", LexiconScope.Global);

        Assert.Null(globalEntry.Value);
    }

    /// <summary>
    /// The refusal is for a call bound to no Session at all. A call that carries a Session id with no
    /// Campaign binding is not refused: it resolves to the installation scope, which for the Lexicon is
    /// the global tier (DESIGN §10.6).
    /// </summary>
    [Fact]
    public async Task ScribeLexicon_WithCampaignScopingOnAndASessionWithNoCampaignBinding_WritesTheGlobalTier()
    {
        Guid unboundSessionId = Guid.NewGuid();

        SessionBoundMemoryScopeResolver resolver = new(Guid.NewGuid(), Guid.NewGuid());

        FakeLexiconService lexicon = new();

        await using LexiconToolServer server = await LexiconToolServer.CreateAsync(resolver, lexicon);

        Guid? previous = SessionAttachmentToolAmbient.CurrentSessionId;

        SessionAttachmentToolAmbient.CurrentSessionId = unboundSessionId;

        try
        {
            McpToolsCallResultWire result = await server.ScribeAsync("Ada", "Person", ["Installation-scoped fact."]);

            Assert.False(result.IsError, result.Content?.FirstOrDefault()?.Text);
        }
        finally
        {
            SessionAttachmentToolAmbient.CurrentSessionId = previous;
        }

        Assert.Equal([unboundSessionId], resolver.Requests);

        Result<LexiconEntryDto?> globalEntry = await lexicon.GetByNameInScopeAsync("Ada", LexiconScope.Global);

        Assert.NotNull(globalEntry.Value);
    }

    /// <summary>Campaign scoping on; one known Session bound to one Campaign.</summary>
    private sealed class SessionBoundMemoryScopeResolver(Guid sessionId, Guid campaignId) : IMemoryScopeResolver
    {
        public List<Guid?> Requests { get; } = [];

        public bool IsCampaignScopingEnabled => true;

        public MemoryScope ForResolvedCampaign(Guid? resolvedCampaignId) =>
            MemoryScope.Resolve(campaignScopingEnabled: true, resolvedCampaignId);

        public ValueTask<MemoryScope> ResolveForSessionAsync(Guid? requestedSessionId, CancellationToken cancellationToken)
        {
            Requests.Add(requestedSessionId);

            return ValueTask.FromResult(ForResolvedCampaign(requestedSessionId == sessionId ? campaignId : null));
        }
    }

    /// <summary>
    /// One live in-process tool server with the Lexicon enabled, reached through its real transport,
    /// whose send boundary is the production session binder.
    /// </summary>
    private sealed class LexiconToolServer : IAsyncDisposable
    {
        private readonly InProcessMcpTransport _transport;

        private readonly Task _serverTask;

        private readonly CancellationTokenSource _lifetime;

        private int _nextId;

        private LexiconToolServer(InProcessMcpTransport transport, Task serverTask, CancellationTokenSource lifetime)
        {
            _transport = transport;

            _serverTask = serverTask;

            _lifetime = lifetime;
        }

        public List<McpToolsCallResultWire> Results { get; } = [];

        public static async Task<LexiconToolServer> CreateAsync(IMemoryScopeResolver resolver, ILexiconService lexicon)
        {
            ServiceCollection services = new();

            services.AddSingleton(resolver);

            services.AddSingleton(lexicon);

            services.AddSingleton<IOptionsMonitor<ArcanumSettings>>(
                new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

            IServiceScopeFactory scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

            (InProcessMcpTransport transport, ArcanumInternalToolServer server) = InProcessMcpTransport.CreatePair(
                new HumanPromptRegistry(),
                scopeFactory,
                new UnseenServantPacer(
                    new SilentEventBus(),
                    new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()),
                    scopeFactory,
                    NullLogger<UnseenServantPacer>.Instance),
                workspaceRootNormalizedOrNull: null,
                listDirectoryMaxPaths: 64,
                intelligenceSettings: ArcanumRuntimeDefaults.Intelligence with
                {
                    EnableLexiconSystem = true,
                    EnableArchiveSearch = false,
                },
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

            return new LexiconToolServer(transport, serverTask, lifetime);
        }

        /// <summary>The model-facing tool: it forwards the call over the transport and reports the result.</summary>
        public AIFunction ScribeTool() =>
            AIFunctionFactory.Create(
                async (string name, string type, string[] facts) =>
                {
                    McpToolsCallResultWire result = await ScribeAsync(name, type, facts).ConfigureAwait(false);

                    Results.Add(result);

                    return result.Content?.FirstOrDefault()?.Text ?? string.Empty;
                },
                "scribe_lexicon",
                "records a Lexicon entry",
                AdHocJson.AIFunctionOptions);

        public async Task<McpToolsCallResultWire> ScribeAsync(string name, string type, string[] facts)
        {
            int id = Interlocked.Increment(ref _nextId);

            JsonRpcRequest request = new()
            {
                Method = "tools/call",
                Params = JsonSerializer.SerializeToElement(
                    new McpToolsCallParams
                    {
                        Name = "scribe_lexicon",
                        Arguments = JsonSerializer.SerializeToElement(
                            new ScribeLexiconParams(name, type, facts),
                            McpJsonSerializerContext.Default.ScribeLexiconParams),
                    },
                    McpJsonSerializerContext.Default.McpToolsCallParams),
                Id = JsonSerializer.SerializeToElement(id, McpJsonSerializerContext.Default.Int32),
            };

            await _transport.WriteRequestAsync(request).ConfigureAwait(false);

            McpInboundEnvelope envelope = await _transport.InboundReader.ReadAsync().ConfigureAwait(false);

            Assert.Equal(McpInboundKind.Response, envelope.Kind);

            return JsonSerializer.Deserialize(
                envelope.Response!.Result!.Value,
                McpJsonSerializerContext.Default.McpToolsCallResultWire)!;
        }

        public async ValueTask DisposeAsync()
        {
            await _lifetime.CancelAsync();

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
    }

    private sealed class SilentEventBus : IEventBus
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

    /// <summary>What one tool invocation could see of its turn.</summary>
    private sealed record ObservedTurnAmbients(
        Guid? SessionId,
        ContextMaterializationLedger? Ledger,
        bool HasMaterializedAttachmentContent,
        bool CanResolveAttachment,
        bool RepeatedInjectionRefused,
        int ProviderRound,
        Guid? BudgetReservationId,
        CovenantToolStagingContext? CovenantStaging)
    {
        public static ObservedTurnAmbients Capture(Guid attachmentId) =>
            new(
                SessionAttachmentToolAmbient.CurrentSessionId,
                ContextMaterializationLedgerAmbient.Ledger,
                AttachmentMemoryGateAmbient.HasMaterializedAttachmentContent,
                AttachmentMemoryGateAmbient.TryResolve(attachmentId, out _),
                ProbeRepeatedInjectionRefused(),
                ContextMaterializationLedgerAmbient.ProviderRound,
                DelegatedSpendAttribution.BudgetReservationId,
                CovenantToolStagingAmbient.Current);

        /// <summary>
        /// Whether the inject-once tracker is the turn's: with no tracker ambient every injection is
        /// allowed, so only a live tracker refuses the second of two identical marks.
        /// </summary>
        private static bool ProbeRepeatedInjectionRefused()
        {
            const string probeKey = "r-008-inject-once-probe";

            return SessionAttachmentTurnBudget.TryMarkInjected(probeKey, 1)
                && !SessionAttachmentTurnBudget.TryMarkInjected(probeKey, 1);
        }
    }

    /// <summary>
    /// A dispatch gate over an in-memory plan whose turn may stage, so the turn loop publishes staging
    /// material the way a staging-eligible production turn does.
    /// </summary>
    private static CovenantDispatchGate StagingCovenantGate() =>
        new(
            new StagingPlanContextProvider(CovenantCompositionFixture.Plan(confirmed: 1, proposed: 0)),
            new AcceptingDisclosureJournal(),
            new UntaintedSensitivityLedger(),
            new StagingAuthority(),
            TimeProvider.System,
            NullLogger<CovenantDispatchGate>.Instance);

    private sealed class StagingPlanContextProvider(CovenantTurnPlan plan) : ICovenantContextProvider
    {
        public ValueTask<Result<CovenantTurnContext>> BeginTurnAsync(
            ArcanumInvocationContext invocation,
            Guid logicalTurnId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<CovenantTurnContext>.Success(
                CovenantTurnContext.ForPlan(
                    plan,
                    new CovenantTurnLease(new InertTurnLeaseRegistration()),
                    new CovenantMutationCollector(logicalTurnId, plan.Digest, Guid.NewGuid()),
                    logicalTurnId,
                    new CovenantCapabilityFixtures.StubHeadProbe())));
    }

    private sealed class InertTurnLeaseRegistration : ICovenantLeaseRegistration
    {
        public CovenantOperationLeaseSnapshot Snapshot { get; } = new(
            RegistrationId: Guid.Parse("21111111-2222-4333-8444-555555555555"),
            RuntimeAuthorityGeneration: 1,
            CovenantLeaseKind.Turn,
            CovenantLeaseCoverage.Scoped,
            CovenantOperationScope.Global,
            CovenantCompositionFixture.DatasetGeneration,
            CapabilityGeneration: 1,
            AuthorityEpoch: 11,
            CanonicalSequence: 0,
            CampaignAvailabilityGeneration: 1,
            CampaignPathRevision: null,
            AcceleratorEpoch: null,
            AppliedCampaignDeletionSequence: null,
            RecoveryOwner: null,
            CleanupOnlyHistoricalCampaign: false);

        public CancellationToken Revocation => CancellationToken.None;

        public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());

        public ValueTask ReleaseAsync() => ValueTask.CompletedTask;
    }

    private sealed class AcceptingDisclosureJournal : ICovenantDisclosureJournal
    {
        private ulong _sequence;

        public ValueTask<Result<CovenantDisclosureReceipt>> AcknowledgeAsync(
            CovenantDisclosureDraft draft,
            CovenantDisclosureEffectCategory category,
            ProviderCallSensitivity sensitivity,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<CovenantDisclosureReceipt>.Success(
                new CovenantDisclosureReceipt(draft, Interlocked.Increment(ref _sequence))));
    }

    private sealed class UntaintedSensitivityLedger : IArtifactSensitivityLedger
    {
        public Task<Result<LabeledArtifactWriteReceipt>> LabelAsync(
            DerivedArtifactWrite write,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<ArtifactSensitivityLabel?>> TryReadLabelAsync(
            SensitiveArtifactKind artifactKind,
            Guid artifactId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<SessionSensitivityProjection>> ReadSessionProjectionAsync(
            Guid sessionId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result<SessionSensitivityProjection>.Success(new SessionSensitivityProjection(
                sessionId,
                0,
                ContentSensitivity.None,
                CovenantTask6Fixture.D(7),
                1)));
    }

    private sealed class StagingAuthority : ICovenantAuthoritySnapshotProvider
    {
        public CovenantAuthoritySnapshot? Current { get; } = new(
            1,
            "11111111-2222-3333-4444-555555555555",
            1,
            1,
            1,
            CovenantHostToolsState.Clean,
            null);
    }
}
