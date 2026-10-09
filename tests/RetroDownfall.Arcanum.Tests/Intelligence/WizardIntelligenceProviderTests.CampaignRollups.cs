using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Tests.Support;
using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Short_Session_B_dispatches_Campaign_decisions_without_loading_Session_A_history(bool streaming)
    {
        CampaignWizardHarness harness = CreateCampaignWizardHarness();

        harness.Chat.EnqueueText("continue the SQLite design");

        harness.Chat.EnqueueStreamTokens("continue the SQLite design");

        if (streaming)
        {
            List<IntelligenceEvent> events = [];

            await foreach (IntelligenceEvent frame in harness.Wizard.StreamPromptAsync(harness.Request, CampaignInvocation(), CancellationToken.None))
            {
                events.Add(frame);
            }

            Assert.DoesNotContain(events, frame => frame.Type is IntelligenceEventType.Error);

            Assert.Contains(events, frame => frame.Type is IntelligenceEventType.Result);
        }
        else
        {
            Result<PromptTurnResult> result = await harness.Wizard.ExecutePromptAsync(harness.Request, CampaignInvocation(), CancellationToken.None);

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        }

        IReadOnlyList<ChatMessage> sent = Assert.Single(streaming ? harness.Chat.AllStreamingCalls : harness.Chat.AllBufferedCalls);

        string system = sent.Single(message => message.Role == ChatRole.System).Text!;

        Assert.Contains("Project convention from Session A: use SQLite.", system);

        Assert.DoesNotContain("### Session Summary (compressed context)", system);

        Assert.Equal(1, harness.Store.Reads);

        Assert.Equal(1, harness.Maintenance.SessionCalls);

        Assert.Equal(1, harness.Maintenance.CampaignCalls);

        Assert.Equal(1, harness.Claims.Begins);

        Assert.Single(harness.Claims.Commits);

        Assert.Equal(SessionTurnClaimState.Committed, Assert.Single(harness.Claims.Outcomes).State);

        Assert.Equal(0, harness.Grimoire.FinalizeCallCount);
    }

    [Fact]
    public async Task A_failed_Campaign_stream_retains_partial_history_but_its_retry_returns_the_terminal_error_without_dispatch()
    {
        CampaignWizardHarness harness = CreateCampaignWizardHarness();

        harness.Chat.EnqueueStreamFailure(new InvalidOperationException("stream broke after paid output"));

        try
        {
            TurnIdempotencyAmbient.PublishIdentity(new string('a', 64), new string('b', 64));

            List<IntelligenceEvent> first = [];

            await foreach (IntelligenceEvent frame in harness.Wizard.StreamPromptAsync(harness.Request, CampaignInvocation(), CancellationToken.None))
            {
                first.Add(frame);
            }

            IntelligenceEvent originalError = Assert.Single(first, frame => frame.Type is IntelligenceEventType.Error);

            Assert.Equal("partial", Assert.Single(harness.Claims.Commits).FinalText);

            List<IntelligenceEvent> retry = [];

            await foreach (IntelligenceEvent frame in harness.Wizard.StreamPromptAsync(harness.Request, CampaignInvocation(), CancellationToken.None))
            {
                retry.Add(frame);
            }

            IntelligenceEvent replayedError = Assert.Single(retry, frame => frame.Type is IntelligenceEventType.Error);

            Assert.Equal(originalError.Data, replayedError.Data);

            Assert.DoesNotContain(retry, frame => frame.Type is IntelligenceEventType.Result);

            Assert.Equal(1, harness.Chat.StreamingCallCount);

            Assert.Equal(1, harness.Claims.Begins);

            Assert.Equal(1, harness.Maintenance.SessionCalls);

            Assert.Equal(1, harness.Maintenance.CampaignCalls);

            Assert.Equal(SessionTurnClaimState.RestoredInterrupted, Assert.Single(harness.Claims.Outcomes).State);

            Assert.Equal(500, harness.Claims.Outcomes.Single().TerminalHttpStatus);
        }
        finally
        {
            TurnIdempotencyAmbient.Clear();
        }
    }

    [Fact]
    public async Task Campaign_preview_reports_its_own_cost_without_claiming_a_turn_or_refreshing_a_summary()
    {
        CampaignWizardHarness harness = CreateCampaignWizardHarness();

        Result<ContextPreviewResult> preview = await harness.Wizard.PreviewContextAsync(
            new ContextPreviewRequest(Prompt: "inspect", Model: ModelName, SessionId: harness.Request.SessionId,
                CampaignId: CampaignRollupTurnSnapshotTests.CampaignId, WorkingDirectory: _workspace.Root, NoRetrieval: true),
            CampaignInvocation(), CancellationToken.None);

        Assert.True(preview.IsSuccess, preview.IsFailure ? preview.Error.Message : null);

        ContextPreviewSource campaign = Assert.Single(preview.Value.Sources, source => source.Source is ContextTokenSource.CampaignRollup);

        Assert.True(campaign.Included && campaign.TokenCount > 0);

        Assert.Empty(harness.Claims.Requests);

        Assert.Equal(0, harness.Claims.Begins);

        Assert.Equal(0, harness.Maintenance.SessionCalls + harness.Maintenance.CampaignCalls);

        Assert.Equal(0, harness.Chat.BufferedCallCount + harness.Chat.StreamingCallCount);

        Assert.Equal(1, harness.Store.Reads);
    }

    [Fact]
    public async Task Compression_rebuild_keeps_Campaign_context_and_costs_the_Session_summary_separately()
    {
        Guid sessionId = Guid.NewGuid();

        CampaignWizardHarness harness = CreateCampaignWizardHarness(BuildHeavySession(sessionId), contextWindow: 30000);

        harness.Chat.EnqueueText("continued");

        Result<PromptTurnResult> result = await harness.Wizard.ExecutePromptAsync(harness.Request, CampaignInvocation(), CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        string system = harness.Chat.LastBufferedMessages.Single(message => message.Role == ChatRole.System).Text!;

        Assert.Contains("### Campaign Summary (cross-session context)", system);

        Assert.Contains("Project convention from Session A: use SQLite.", system);

        Assert.Contains("### Session Summary (compressed context)", system);

        Assert.Equal(1, harness.Store.Reads);

        Assert.True(harness.Store.LastValidated?.ArtifactId == harness.Store.Current?.ArtifactId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_summary_publication_keeps_the_bound_revision_while_invalidation_refuses_a_structured_retry(bool invalidate)
    {
        CampaignWizardHarness harness = CreateCampaignWizardHarness();

        CampaignRollupArtifact original = harness.Store.Current!;

        harness.Chat.EnqueueText("{}");

        harness.Chat.EnqueueText("{\"name\":\"accepted\"}");

        harness.Chat.BeforeCall = () =>
        {
            harness.Store.Current = CampaignRollupTurnSnapshotTests.Artifact("Later publication must not replace the bound context.", ContentSensitivity.None);

            if (invalidate)
            {
                harness.Store.Validation = new Error(ErrorCodes.Covenant.StaleSnapshot, "The consumed source was deleted.");
            }
        };

        JsonElement schema = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"name":{"type":"string"}},"required":["name"],"additionalProperties":false}""", AdHocJson.Options);

        Result<PromptTurnResult> result = await harness.Wizard.ExecutePromptAsync(harness.Request with
        {
            ResponseFormat = "json_schema", ResponseFormatJsonSchema = schema,
        }, CampaignInvocation(), CancellationToken.None);

        Assert.Equal(!invalidate, result.IsSuccess);

        Assert.Equal(invalidate ? 1 : 2, harness.Chat.BufferedCallCount);

        Assert.Equal(1, harness.Store.Reads);

        Assert.Same(original, harness.Store.LastValidated);

        Assert.All(harness.Chat.AllBufferedCalls, messages =>
        {
            string system = messages.First(message => message.Role == ChatRole.System).Text!;

            Assert.Contains(original.Content, system);

            Assert.DoesNotContain("Later publication must not replace", system);
        });
    }

    [Fact]
    public async Task Disabling_rollups_cannot_bypass_an_already_owned_accepted_turn_and_pay_again()
    {
        CampaignWizardHarness harness = CreateCampaignWizardHarness();

        harness.Settings.Features.CampaignRollups = false;

        harness.Chat.EnqueueText("must never be paid for");

        try
        {
            TurnIdempotencyAmbient.PublishIdentity(new string('a', 64), new string('b', 64));

            harness.Lookup.Existing = (await new CampaignRollupTurnSnapshotTests.AcceptingClaims().AcquireAsync(
                new(Guid.Parse(new CampaignRollupTurnSnapshotTests.Authority().Current!.InstallationIdentity), 0,
                    TurnIdempotencyAmbient.RequestIdentity!.ClientTurnId, harness.Request.SessionId!.Value,
                    SessionTurnSurface.Intelligence, Digest(1), Digest(2), null, 0, 0), CancellationToken.None)).Value.Claim;

            Result<PromptTurnResult> result = await harness.Wizard.ExecutePromptAsync(harness.Request, CampaignInvocation(), CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(0, harness.Chat.BufferedCallCount);

            Assert.Equal(0, harness.Store.Reads);
        }
        finally
        {
            TurnIdempotencyAmbient.Clear();
        }
    }

    [Fact]
    public async Task Changing_the_configuration_refuses_a_structured_retry_under_the_old_Campaign_claim()
    {
        CampaignWizardHarness harness = CreateCampaignWizardHarness();

        harness.Chat.EnqueueText("{}");

        harness.Chat.EnqueueText("{\"name\":\"must not be paid for\"}");

        harness.Chat.BeforeCall = () => harness.Settings.Providers[0].ContextWindowLimit++;

        JsonElement schema = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"name":{"type":"string"}},"required":["name"],"additionalProperties":false}""", AdHocJson.Options);

        Result<PromptTurnResult> result = await harness.Wizard.ExecutePromptAsync(harness.Request with
        {
            ResponseFormat = "json_schema", ResponseFormatJsonSchema = schema,
        }, CampaignInvocation(), CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(1, harness.Chat.BufferedCallCount);
    }

    [Fact]
    public async Task A_configuration_change_during_preparation_cannot_rebind_the_acquired_claim_or_dispatch()
    {
        CampaignWizardHarness harness = CreateCampaignWizardHarness();

        harness.Maintenance.OnSession = () => harness.Settings.Providers[0].ContextWindowLimit++;

        harness.Chat.EnqueueText("must never be paid for");

        Result<PromptTurnResult> result = await harness.Wizard.ExecutePromptAsync(
            harness.Request, CampaignInvocation(), CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(0, harness.Chat.BufferedCallCount);

        Assert.Equal(0, harness.Store.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_first_Campaign_turn_creates_a_claimed_Session_and_consumes_cross_session_context(bool streaming)
    {
        CampaignWizardHarness harness = CreateCampaignWizardHarness();

        PingRequest request = harness.Request with { SessionId = null, CampaignId = CampaignRollupTurnSnapshotTests.CampaignId };

        using ServiceProvider services = new ServiceCollection().AddSingleton<IOptionsMonitor<ArcanumSettings>>(
            new TestOptionsMonitor<ArcanumSettings>(harness.Settings)).BuildServiceProvider();

        ArcanumInvocationContext invocation = ArcanumInvocationContexts.ForTurn(
            new DefaultHttpContext { RequestServices = services }, request, CampaignRollupTurnSnapshotTests.Campaign);

        harness.Chat.EnqueueText("continued");

        harness.Chat.EnqueueStreamTokens("continued");

        if (streaming)
        {
            List<IntelligenceEvent> frames = [];

            await foreach (IntelligenceEvent frame in harness.Wizard.StreamPromptAsync(request, invocation, CancellationToken.None))
            {
                frames.Add(frame);
            }

            Assert.DoesNotContain(frames, frame => frame.Type is IntelligenceEventType.Error);
        }
        else
        {
            Result<PromptTurnResult> result = await harness.Wizard.ExecutePromptAsync(request, invocation, CancellationToken.None);

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        }

        IReadOnlyList<ChatMessage> sent = Assert.Single(streaming ? harness.Chat.AllStreamingCalls : harness.Chat.AllBufferedCalls);

        Assert.Contains("Project convention from Session A: use SQLite.", sent.Single(message => message.Role == ChatRole.System).Text!);

        Assert.Equal(1, harness.Claims.Begins);

        Assert.NotEqual(Guid.Empty, Assert.Single(harness.Claims.Requests).SessionId);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Invalidation_during_durable_disclosure_refuses_the_physical_provider_send(bool streaming, bool disableFeature)
    {
        CampaignWizardHarness harness = CreateCampaignWizardHarness(onDisclosure: h =>
        {
            if (disableFeature)
            {
                h.Settings.Features.CampaignRollups = false;
            }
            else
            {
                h.Store.Validation = new Error(ErrorCodes.Covenant.StaleSnapshot, "The consumed source was erased during disclosure.");
            }
        });

        harness.Chat.EnqueueText("must never be paid for");

        harness.Chat.EnqueueStreamTokens("must never be paid for");

        if (streaming)
        {
            List<IntelligenceEvent> frames = [];

            await foreach (IntelligenceEvent frame in harness.Wizard.StreamPromptAsync(harness.Request, CampaignInvocation(), CancellationToken.None))
            {
                frames.Add(frame);
            }

            Assert.Contains(frames, frame => frame.Type is IntelligenceEventType.Error);
        }
        else
        {
            Result<PromptTurnResult> result = await harness.Wizard.ExecutePromptAsync(harness.Request, CampaignInvocation(), CancellationToken.None);

            Assert.True(result.IsFailure);
        }

        Assert.Equal(0, harness.Chat.BufferedCallCount + harness.Chat.StreamingCallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_configuration_reload_before_claiming_cannot_bind_new_settings_to_an_old_request_snapshot(bool streaming)
    {
        CampaignWizardHarness harness = CreateCampaignWizardHarness(newerMonitorConfiguration: true);

        harness.Chat.EnqueueText("must never be paid for");

        harness.Chat.EnqueueStreamTokens("must never be paid for");

        if (streaming)
        {
            List<IntelligenceEvent> frames = [];

            await foreach (IntelligenceEvent frame in harness.Wizard.StreamPromptAsync(harness.Request, CampaignInvocation(), CancellationToken.None))
            {
                frames.Add(frame);
            }

            Assert.Contains(frames, frame => frame.Type is IntelligenceEventType.Error);
        }
        else
        {
            Result<PromptTurnResult> result = await harness.Wizard.ExecutePromptAsync(harness.Request, CampaignInvocation(), CancellationToken.None);

            Assert.True(result.IsFailure);
        }

        Assert.Equal(0, harness.Chat.BufferedCallCount + harness.Chat.StreamingCallCount);

        Assert.Empty(harness.Claims.Requests);

        Assert.Equal(0, harness.Maintenance.SessionCalls + harness.Maintenance.CampaignCalls);

        Assert.Equal(0, harness.Store.Reads);
    }

    private CampaignWizardHarness CreateCampaignWizardHarness(Session? session = null, int contextWindow = 128000,
        Action<CampaignWizardHarness>? onDisclosure = null, bool newerMonitorConfiguration = false)
    {
        ArcanumSettings settings = DefaultSettings();

        settings.Features.CampaignRollups = true;

        settings.Providers[0].ContextWindowLimit = contextWindow;

        session ??= new Session { Id = Guid.NewGuid(), CampaignId = CampaignRollupTurnSnapshotTests.CampaignId };

        session.CampaignId = CampaignRollupTurnSnapshotTests.CampaignId;

        FakeGrimoireRepository grimoire = new() { Session = session };

        CampaignRollupTurnSnapshotTests.ProbeStore store = new()
        {
            Current = CampaignRollupTurnSnapshotTests.Artifact("Project convention from Session A: use SQLite.", ContentSensitivity.None),
        };

        CampaignWizardClaims claims = new() { HistoryRevision = session.Entries.Select(entry => entry.Sequence).DefaultIfEmpty().Max() };

        CampaignWizardMaintenance maintenance = new();

        CampaignWizardLookup lookup = new();

        ArcanumSettings monitorSettings = newerMonitorConfiguration
            ? settings with { Providers = settings.Providers.Select(provider => provider with { ContextWindowLimit = provider.ContextWindowLimit + 1 }).ToArray() }
            : settings;

        CampaignRollupTurnPreparer preparer = new(store, new TestOptionsMonitor<ArcanumSettings>(monitorSettings),
            new CampaignRollupTurnSnapshotTests.Resolver(), null!, maintenance, claims, grimoire, claims,
            new CampaignRollupTurnSnapshotTests.Authority(), lookup, replayStore: claims);

        ScriptingChatClient chat = new();

        CampaignWizardHarness? harness = null;

        CovenantDispatchGate? gate = onDisclosure is null ? null : new(
            new StagingPlanContextProvider(CovenantCompositionFixture.Plan(confirmed: 1, proposed: 0)),
            new CampaignMutatingJournal(() => onDisclosure(harness!)), new UntaintedSensitivityLedger(),
            new StagingAuthority(), TimeProvider.System, NullLogger<CovenantDispatchGate>.Instance);

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings, grimoire, campaignRollups: preparer,
            turnCommitter: claims, claimedBeginStore: claims, claims: claims, covenantDispatch: gate);

        PingRequest request = BaseRequest() with { Prompt = "Continue this project.", SessionId = session.Id, SkipSpellRouting = true, DisableAllTools = true };

        harness = new(wizard, chat, grimoire, settings, store, maintenance, claims, lookup, request);

        return harness;
    }

    private sealed record CampaignWizardHarness(WizardIntelligenceProvider Wizard, ScriptingChatClient Chat,
        FakeGrimoireRepository Grimoire, ArcanumSettings Settings, CampaignRollupTurnSnapshotTests.ProbeStore Store,
        CampaignWizardMaintenance Maintenance, CampaignWizardClaims Claims, CampaignWizardLookup Lookup, PingRequest Request);

    private sealed class CampaignMutatingJournal(Action mutate) : ICovenantDisclosureJournal
    {
        public ValueTask<Result<CovenantDisclosureReceipt>> AcknowledgeAsync(CovenantDisclosureDraft draft,
            CovenantDisclosureEffectCategory category, ProviderCallSensitivity sensitivity, CancellationToken cancellationToken)
        {
            mutate();

            return ValueTask.FromResult(Result<CovenantDisclosureReceipt>.Success(new(draft, 1)));
        }
    }

    private sealed class CampaignWizardLookup : ISessionTurnClaimLookup
    {
        public SessionTurnClaim? Existing { get; set; }

        public ValueTask<Result<bool>> HasClaimAsync(Guid clientTurnId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<bool>.Success(Existing is not null));

        public ValueTask<Result<SessionTurnClaim?>> FindAsync(Guid installationId, Guid clientTurnId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<SessionTurnClaim?>.Success(Existing));
    }

    private sealed class CampaignWizardMaintenance : ICampaignRollupMaintenance
    {
        public Action? OnSession { get; set; }

        public int SessionCalls { get; private set; }

        public int CampaignCalls { get; private set; }

        public Task<Result<CampaignRollupMaintenanceCapability>> CreateCapabilityAsync(ArcanumInvocationContext invocation, SessionTurnClaimLease lease, CanonicalCampaignContext campaign, CovenantTurnLease authority, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<CampaignRollupMaintenanceResult>> ProcessSessionAsync(Guid sessionId, long? through, CampaignRollupMaintenanceCapability? capability, CancellationToken cancellationToken)
        {
            SessionCalls++;

            OnSession?.Invoke();

            return Task.FromResult(Result<CampaignRollupMaintenanceResult>.Success(new(0, false, null)));
        }

        public Task<Result<CampaignRollupMaintenanceResult>> ProcessCampaignAsync(Guid campaignId, CampaignRollupMaintenanceCapability? capability, CancellationToken cancellationToken)
        {
            CampaignCalls++;

            return Task.FromResult(Result<CampaignRollupMaintenanceResult>.Success(new(0, false, null)));
        }
    }

    private sealed class CampaignWizardClaims : ISessionTurnClaimCoordinator, ISessionTurnClaimBeginStore, IGrimoireTurnCommitter, ISessionTurnClaimReplayStore
    {
        private readonly Dictionary<Guid, SessionTurnClaimLease> _leases = [];

        public long HistoryRevision { get; init; }
        public List<SessionTurnRequestIdentity> Requests { get; } = [];

        public int Begins { get; private set; }

        public List<TurnCommitRequest> Commits { get; } = [];

        public List<SessionTurnClaimOutcome> Outcomes { get; } = [];

        public async ValueTask<Result<SessionTurnClaimLease>> AcquireAsync(SessionTurnRequestIdentity request, CancellationToken cancellationToken)
        {
            Requests.Add(request);

            if (_leases.TryGetValue(request.ClientTurnId, out SessionTurnClaimLease? existing) && existing.Claim.IsTerminal)
            {
                return existing with { Disposition = SessionTurnClaimDisposition.Replayed, ExecutorId = null, LeaseDeadlineUtc = null };
            }

            SessionTurnClaimLease created = (await new CampaignRollupTurnSnapshotTests.AcceptingClaims().AcquireAsync(request, cancellationToken)).Value;

            _leases[request.ClientTurnId] = created;

            return created;
        }

        public ValueTask<Result<SessionTurnClaimInputSnapshot>> ReadClaimInputAsync(Guid sessionId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<SessionTurnClaimInputSnapshot>.Success(new(sessionId, HistoryRevision, null, 0)));

        public ValueTask<Result<AssistantReplyBeginReceipt>> BeginClaimedAssistantReplyAsync(SessionTurnClaimLease lease, CanonicalCampaignContext campaign, string prompt, string model, CancellationToken cancellationToken)
        {
            Begins++;

            AssistantReplyBeginReceipt begin = new(lease.Claim.SessionId, Guid.NewGuid(), lease.FutureAssistantEntryId,
                new(lease.Claim.SessionId, campaign.Binding, lease.Claim.PreRequestHistoryRevision, 0));

            _leases[lease.Claim.ClientTurnId] = lease with
            {
                Claim = lease.Claim with { State = SessionTurnClaimState.Begun, UserEntryId = begin.UserEntryId, AssistantEntryId = begin.AssistantEntryId },
            };

            return ValueTask.FromResult(Result<AssistantReplyBeginReceipt>.Success(begin));
        }

        public Task<Result<TurnCommitReceipt>> CommitTurnAsync(TurnCommitRequest request, CancellationToken cancellationToken)
        {
            Commits.Add(request);

            return Task.FromResult(Result<TurnCommitReceipt>.Success(new(request.AssistantEntryId, request.Outcome, false, [], 1)));
        }

        public ValueTask<Result<SessionTurnClaim>> MarkBegunAsync(SessionTurnClaimLease lease, AssistantReplyBeginReceipt begin, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<Result<SessionTurnClaim>> CompleteAsync(SessionTurnClaimLease lease, SessionTurnClaimOutcome outcome, CancellationToken cancellationToken)
        {
            Outcomes.Add(outcome);

            SessionTurnClaimLease recorded = _leases[lease.Claim.ClientTurnId];

            SessionTurnClaim terminal = recorded.Claim with { State = outcome.State, Outcome = outcome };

            _leases[lease.Claim.ClientTurnId] = recorded with { Claim = terminal };

            return ValueTask.FromResult(Result<SessionTurnClaim>.Success(terminal));
        }

        public ValueTask<Result<GrimoireEntryDto>> ReadCleanCommittedReplyAsync(SessionTurnClaimLease lease, CancellationToken cancellationToken)
        {
            TurnCommitRequest committed = Commits.Single(request => request.AssistantEntryId == lease.FutureAssistantEntryId);

            return ValueTask.FromResult(Result<GrimoireEntryDto>.Success(
                new(lease.FutureAssistantEntryId, MessageRole.Assistant, committed.FinalText, ModelName, DateTimeOffset.UtcNow, false)));
        }
    }

    [Fact]
    public async Task Campaign_terminal_replay_without_authority_refuses_when_the_atomic_reply_reader_is_unavailable()
    {
        CampaignRollupTurnSnapshotTests.ProbeStore store = new();

        CampaignClaimProbe claims = new() { Replay = true };

        Result<CampaignRollupTurnSnapshot?> prepared = await CampaignPreparer(store, claims).PrepareLiveAsync(
            BaseRequest() with { SessionId = Guid.NewGuid() }, CampaignInvocation(), false, CancellationToken.None);

        Assert.True(prepared.IsSuccess, prepared.IsFailure ? prepared.Error.Message : null);

        await using CampaignRollupTurnSnapshot snapshot = prepared.Value!;

        Result<PromptTurnResult> replay = Assert.IsType<Result<PromptTurnResult>>(snapshot.Replay);

        Assert.True(replay.IsFailure);

        Assert.Equal(0, store.Reads);
    }

    [Fact]
    public async Task Campaign_root_changes_dependency_evidence_but_not_the_accepted_request_identity()
    {
        CampaignClaimProbe claims = new();

        CampaignRollupTurnPreparer preparer = CampaignPreparer(new(), claims);

        PingRequest request = BaseRequest() with { SessionId = Guid.NewGuid() };

        CanonicalCampaignContext first = CanonicalCampaignContext.Create(
            SessionCampaignBinding.ForCampaign(CampaignRollupTurnSnapshotTests.CampaignId), 1, 1, 1, Digest(1));

        CanonicalCampaignContext moved = CanonicalCampaignContext.Create(first.Binding, 1, 1, 2, Digest(2));

        try
        {
            TurnIdempotencyAmbient.PublishIdentity(new string('a', 64), new string('b', 64));

            _ = await preparer.PrepareLiveAsync(request, CampaignInvocation(first), false, CancellationToken.None);

            _ = await preparer.PrepareLiveAsync(request, CampaignInvocation(moved), false, CancellationToken.None);

            Assert.Equal(2, claims.Requests.Count);

            Assert.Equal(claims.Requests[0].RequestDigest, claims.Requests[1].RequestDigest);

            Assert.NotEqual(claims.Requests[0].DependencyDigest, claims.Requests[1].DependencyDigest);
        }
        finally
        {
            TurnIdempotencyAmbient.Clear();
        }
    }

    [Fact]
    public async Task Resolved_prompt_revision_changes_dependency_evidence_under_one_accepted_request()
    {
        CampaignClaimProbe claims = new();

        CampaignRollupTurnPreparer preparer = CampaignPreparer(new(), claims);

        PingRequest request = BaseRequest() with { SessionId = Guid.NewGuid(), Prompt = "stored Prompt revision one" };

        try
        {
            TurnIdempotencyAmbient.PublishIdentity(new string('a', 64), new string('b', 64));

            _ = await preparer.PrepareLiveAsync(request, CampaignInvocation(), false, CancellationToken.None);

            _ = await preparer.PrepareLiveAsync(request with { Prompt = "stored Prompt revision two" }, CampaignInvocation(), false, CancellationToken.None);

            Assert.Equal(2, claims.Requests.Count);

            Assert.Equal(claims.Requests[0].RequestDigest, claims.Requests[1].RequestDigest);

            Assert.NotEqual(claims.Requests[0].DependencyDigest, claims.Requests[1].DependencyDigest);
        }
        finally
        {
            TurnIdempotencyAmbient.Clear();
        }
    }

    private static CovenantDigest Digest(byte value) => new(Enumerable.Repeat(value, 32).ToArray());

    private static ArcanumInvocationContext CampaignInvocation(CanonicalCampaignContext? campaign = null) =>
        ArcanumInvocationContext.Create(ArcanumExecutionSurface.SessionBackedOperatorTurn,
            campaign ?? CampaignRollupTurnSnapshotTests.Campaign, InvocationAttendance.Attended,
            CovenantContextPolicy.Default, ToolPolicy.NoTools, null).Value;

    private static CampaignRollupTurnPreparer CampaignPreparer(
        CampaignRollupTurnSnapshotTests.ProbeStore store,
        CampaignClaimProbe claims)
    {
        Core.Configuration.ArcanumSettings settings = DefaultSettings();

        settings.Features.CampaignRollups = true;

        return new(store, new TestOptionsMonitor<Core.Configuration.ArcanumSettings>(settings),
            new CampaignRollupTurnSnapshotTests.Resolver(), null!,
            new CampaignRollupTurnSnapshotTests.RefusingMaintenance(), new CampaignRollupTurnSnapshotTests.InputStore(),
            null, claims, new CampaignRollupTurnSnapshotTests.Authority());
    }

    private sealed class CampaignClaimProbe : ISessionTurnClaimCoordinator
    {
        public bool Replay { get; init; }

        public List<SessionTurnRequestIdentity> Requests { get; } = [];

        public async ValueTask<Result<SessionTurnClaimLease>> AcquireAsync(SessionTurnRequestIdentity request, CancellationToken cancellationToken)
        {
            Requests.Add(request);

            if (!Replay)
            {
                return new Error(ErrorCodes.Hub.SessionTurnBusy, "The existing claim owns the turn.");
            }

            SessionTurnClaimLease created = (await new CampaignRollupTurnSnapshotTests.AcceptingClaims().AcquireAsync(request, cancellationToken)).Value;

            return created with
            {
                Disposition = SessionTurnClaimDisposition.Replayed,
                ExecutorId = null,
                LeaseDeadlineUtc = null,
                Claim = created.Claim with { State = SessionTurnClaimState.Committed, AssistantEntryId = created.FutureAssistantEntryId },
            };
        }

        public ValueTask<Result<SessionTurnClaim>> MarkBegunAsync(SessionTurnClaimLease lease, AssistantReplyBeginReceipt begin, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<Result<SessionTurnClaim>> CompleteAsync(SessionTurnClaimLease lease, SessionTurnClaimOutcome outcome, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

}
