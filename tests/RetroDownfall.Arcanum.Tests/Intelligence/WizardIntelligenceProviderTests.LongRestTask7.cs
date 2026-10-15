using System.Text;

using System.Text.Json;

using Microsoft.Extensions.AI;

using RetroDownfall.Arcanum.Api.Intelligence;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.LongRest;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Storage.Entities;

using RetroDownfall.Arcanum.Core.Tower;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LongRestTurnPreparation_claims_exact_scope_before_assistant_send_without_rollup_work(bool campaign, bool streaming)
    {
        CampaignWizardHarness h = CreateLongRestTask7Harness(campaign);

        List<string> sendOrder = [];

        h.Chat.BeforeCall = () => sendOrder.Add($"claims:{h.Claims.Requests.Count};begins:{h.Claims.Begins}");

        h.Chat.EnqueueText("continued");

        h.Chat.EnqueueStreamTokens("continued");

        ArcanumInvocationContext invocation = LongRestTask7Invocation(campaign);

        if (streaming)
        {
            List<IntelligenceEvent> frames = [];

            await foreach (IntelligenceEvent frame in h.Wizard.StreamPromptAsync(h.Request, invocation, CancellationToken.None))
            {
                frames.Add(frame);
            }

            Assert.DoesNotContain(frames, frame => frame.Type is IntelligenceEventType.Error);

            Assert.Contains(frames, frame => frame.Type is IntelligenceEventType.Result);
        }
        else
        {
            Result<PromptTurnResult> result = await h.Wizard.ExecutePromptAsync(h.Request, invocation, CancellationToken.None);

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        }

        Assert.Equal(new[] { "claims:1;begins:1" }, sendOrder);

        Assert.Equal(h.Request.SessionId, Assert.Single(h.Claims.Requests).SessionId);

        Assert.Equal(1, h.Claims.Begins);

        Assert.Equal(0, h.Maintenance.SessionCalls + h.Maintenance.CampaignCalls);

        Assert.Equal(0, h.Store.Reads);

        Assert.Equal(0, h.Grimoire.FinalizeCallCount);

        Assert.Single(h.Claims.Commits);

        Assert.Equal(SessionTurnClaimState.Committed, Assert.Single(h.Claims.Outcomes).State);

        AssertLongRestTask7NoRollupMaterial(Assert.Single(streaming ? h.Chat.AllStreamingCalls : h.Chat.AllBufferedCalls));
    }

    [Fact]
    public async Task LongRestTurnPreparation_structured_correction_reuses_one_pending_claim_and_assistant_reservation()
    {
        CampaignWizardHarness h = CreateLongRestTask7Harness(campaign: true);

        h.Chat.EnqueueText("{}");

        h.Chat.EnqueueText("{\"name\":\"accepted\"}");

        JsonElement schema = JsonSerializer.Deserialize<JsonElement>(
            """{"type":"object","properties":{"name":{"type":"string"}},"required":["name"],"additionalProperties":false}""", AdHocJson.Options);

        try
        {
            TurnIdempotencyAmbient.PublishIdentity(new string('a', 64), new string('b', 64));

            Result<PromptTurnResult> result = await h.Wizard.ExecutePromptAsync(h.Request with
            {
                ResponseFormat = "json_schema", ResponseFormatJsonSchema = schema,
            }, LongRestTask7Invocation(campaign: true), CancellationToken.None);

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

            Assert.Equal(2, h.Chat.BufferedCallCount);

            Assert.Single(h.Claims.Requests);

            Assert.Equal(TurnIdempotencyAmbient.RequestIdentity!.ClientTurnId, h.Claims.Requests[0].ClientTurnId);

            Assert.Equal(1, h.Claims.Begins);

            Assert.Single(h.Claims.Commits);

            Assert.Equal(0, h.Maintenance.SessionCalls + h.Maintenance.CampaignCalls);

            Assert.Equal(0, h.Store.Reads);

            Assert.All(h.Chat.AllBufferedCalls, AssertLongRestTask7NoRollupMaterial);
        }
        finally
        {
            TurnIdempotencyAmbient.Clear();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LongRestTurnPreparation_tool_compatibility_fallback_reuses_one_preparation(bool streaming)
    {
        CampaignWizardHarness h = CreateLongRestTask7Harness(campaign: true);

        if (streaming)
        {
            h.Chat.EnqueueImmediateStreamFailure(new InvalidOperationException("model does not support tools"));

            h.Chat.EnqueueStreamTokens("retried");
        }
        else
        {
            h.Chat.EnqueueException(new InvalidOperationException("model does not support tools"));

            h.Chat.EnqueueText("retried");
        }

        PingRequest request = h.Request with { DisableAllTools = false, DisableMcpTools = true };

        ArcanumInvocationContext invocation = ArcanumInvocationContext.Create(
            ArcanumExecutionSurface.SessionBackedOperatorTurn, CampaignRollupTurnSnapshotTests.Campaign,
            InvocationAttendance.Attended, CovenantContextPolicy.Default, ToolPolicy.AllTools, null).Value;

        if (streaming)
        {
            List<IntelligenceEvent> frames = [];

            await foreach (IntelligenceEvent frame in h.Wizard.StreamPromptAsync(request, invocation, CancellationToken.None))
            {
                frames.Add(frame);
            }

            Assert.DoesNotContain(frames, frame => frame.Type is IntelligenceEventType.Error);

            Assert.Contains(frames, frame => frame.Type is IntelligenceEventType.Result);
        }
        else
        {
            Result<PromptTurnResult> result = await h.Wizard.ExecutePromptAsync(request, invocation, CancellationToken.None);

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        }

        Assert.Equal(2, h.Chat.BufferedCallCount + h.Chat.StreamingCallCount);

        Assert.Single(h.Claims.Requests);

        Assert.Equal(1, h.Claims.Begins);

        Assert.Single(h.Claims.Commits);

        Assert.Equal(0, h.Maintenance.SessionCalls + h.Maintenance.CampaignCalls);

        Assert.Equal(0, h.Store.Reads);
    }

    [Fact]
    public async Task LongRestTurnPreparation_completed_accepted_request_replays_native_bytes_without_a_second_provider_send()
    {
        CampaignWizardHarness h = CreateLongRestTask7Harness(campaign: true);

        h.Chat.EnqueueText("persisted reply with an exact newline\n");

        h.Chat.EnqueueText("must not be sent");

        try
        {
            TurnIdempotencyAmbient.PublishIdentity(new string('a', 64), new string('b', 64));

            Result<PromptTurnResult> first = await h.Wizard.ExecutePromptAsync(h.Request, LongRestTask7Invocation(true), CancellationToken.None);

            Assert.True(first.IsSuccess, first.IsFailure ? first.Error.Message : null);

            Result<PromptTurnResult> replay = await h.Wizard.ExecutePromptAsync(h.Request, LongRestTask7Invocation(true), CancellationToken.None);

            Assert.True(replay.IsSuccess, replay.IsFailure ? replay.Error.Message : null);

            Assert.Equal(Encoding.UTF8.GetBytes("persisted reply with an exact newline\n"), Encoding.UTF8.GetBytes(replay.Value.Text));

            Assert.Equal(1, h.Chat.BufferedCallCount);

            Assert.Equal(1, h.Claims.Begins);

            Assert.Equal(2, h.Claims.Requests.Count);

            Assert.Single(h.Claims.Requests.Select(request => request.ClientTurnId).Distinct());

            Assert.Single(h.Claims.Commits);
        }
        finally
        {
            TurnIdempotencyAmbient.Clear();
        }
    }

    [Fact]
    public async Task LongRestTurnPreparation_preview_with_rollups_disabled_dispatches_nothing_and_creates_no_claim()
    {
        CampaignWizardHarness h = CreateLongRestTask7Harness(campaign: true);

        Result<ContextPreviewResult> result = await h.Wizard.PreviewContextAsync(
            new ContextPreviewRequest(Prompt: "inspect", Model: ModelName, SessionId: h.Request.SessionId,
                CampaignId: CampaignRollupTurnSnapshotTests.CampaignId, WorkingDirectory: _workspace.Root, NoRetrieval: true),
            LongRestTask7Invocation(true), CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.Empty(h.Claims.Requests);

        Assert.Equal(0, h.Claims.Begins);

        Assert.Equal(0, h.Maintenance.SessionCalls + h.Maintenance.CampaignCalls);

        Assert.Equal(0, h.Store.Reads);

        Assert.Equal(0, h.Chat.BufferedCallCount + h.Chat.StreamingCallCount);

        Assert.DoesNotContain(result.Value.Sources, source => source.Source is ContextTokenSource.CampaignRollup && source.Included);
    }

    [Fact]
    public async Task LongRestTurnPreparation_stateless_transcript_has_no_maintenance_claim()
    {
        CampaignWizardHarness h = CreateLongRestTask7Harness(campaign: true);

        h.Chat.EnqueueText("stateless reply");

        PingRequest request = h.Request with { SessionId = null, StatelessMessages = [new("user", "stateless question")] };

        ArcanumInvocationContext invocation = ArcanumInvocationContext.Create(
            ArcanumExecutionSurface.StatelessOperatorTurn, CampaignRollupTurnSnapshotTests.Campaign,
            InvocationAttendance.Attended, CovenantContextPolicy.Default, ToolPolicy.NoTools, null).Value;

        Result<PromptTurnResult> result = await h.Wizard.ExecutePromptAsync(request, invocation, CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.Empty(h.Claims.Requests);

        Assert.Equal(0, h.Claims.Begins);

        Assert.Equal(0, h.Maintenance.SessionCalls + h.Maintenance.CampaignCalls);

        Assert.Equal(0, h.Store.Reads);

        AssertLongRestTask7NoRollupMaterial(Assert.Single(h.Chat.AllBufferedCalls));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LongRestTurnPreparation_owned_accepted_key_cannot_become_unclaimed_after_disablement_or_no_context(bool noContext)
    {
        CampaignWizardHarness h = CreateLongRestTask7Harness(campaign: true);

        h.Chat.EnqueueText("must not be sent");

        try
        {
            TurnIdempotencyAmbient.PublishIdentity(new string('a', 64), new string('b', 64));

            h.Lookup.Existing = (await new CampaignRollupTurnSnapshotTests.AcceptingClaims().AcquireAsync(
                new(Guid.Parse(new CampaignRollupTurnSnapshotTests.Authority().Current!.InstallationIdentity), 0,
                    TurnIdempotencyAmbient.RequestIdentity!.ClientTurnId, h.Request.SessionId!.Value,
                    SessionTurnSurface.Intelligence, Digest(1), Digest(2), null, 0, 0), CancellationToken.None)).Value.Claim;

            h.Settings.Features.LongRest = noContext;

            ArcanumInvocationContext invocation = noContext ? ArcanumInvocationContext.Create(
                ArcanumExecutionSurface.SessionBackedOperatorTurn, CampaignRollupTurnSnapshotTests.Campaign,
                InvocationAttendance.Attended, CovenantContextPolicy.None, ToolPolicy.NoTools, null).Value
                : LongRestTask7Invocation(true);

            Result<PromptTurnResult> result = await h.Wizard.ExecutePromptAsync(h.Request, invocation, CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, result.Error.Code);

            Assert.Equal(0, h.Chat.BufferedCallCount);

            Assert.Equal(0, h.Claims.Begins);

            Assert.Equal(0, h.Store.Reads);
        }
        finally
        {
            TurnIdempotencyAmbient.Clear();
        }
    }

    [Fact]
    public async Task LongRestTurnPreparation_disabled_prompt_bytes_match_the_existing_context_free_baseline()
    {
        CampaignWizardHarness disabled = CreateLongRestTask7Harness(campaign: true);

        disabled.Settings.Features.LongRest = false;

        CampaignWizardHarness enabled = CreateLongRestTask7Harness(campaign: true);

        // Stateless context-free requests exercise the same prompt path without maintenance authority.
        PingRequest first = disabled.Request with { SessionId = null, StatelessMessages = [new("user", "same transcript")] };

        PingRequest second = enabled.Request with { SessionId = null, StatelessMessages = [new("user", "same transcript")] };

        ArcanumInvocationContext invocation = ArcanumInvocationContext.Create(
            ArcanumExecutionSurface.StatelessOperatorTurn, CanonicalCampaignContext.GlobalOnly,
            InvocationAttendance.Attended, CovenantContextPolicy.None, ToolPolicy.NoTools, null).Value;

        disabled.Chat.EnqueueText("reply");

        enabled.Chat.EnqueueText("reply");

        Assert.True((await disabled.Wizard.ExecutePromptAsync(first, invocation, CancellationToken.None)).IsSuccess);

        Assert.True((await enabled.Wizard.ExecutePromptAsync(second, invocation, CancellationToken.None)).IsSuccess);

        IReadOnlyList<ChatMessage> baseline = Assert.Single(disabled.Chat.AllBufferedCalls);

        IReadOnlyList<ChatMessage> current = Assert.Single(enabled.Chat.AllBufferedCalls);

        Assert.Equal(baseline.Count, current.Count);

        for (int index = 0; index < baseline.Count; index++)
        {
            Assert.Equal(baseline[index].Role, current[index].Role);

            Assert.Equal(Encoding.UTF8.GetBytes(baseline[index].Text!), Encoding.UTF8.GetBytes(current[index].Text!));
        }

        Assert.Empty(disabled.Claims.Requests);

        Assert.Empty(enabled.Claims.Requests);

        Assert.Equal(0, disabled.Store.Reads + enabled.Store.Reads);

        Assert.Equal(0, disabled.Maintenance.SessionCalls + disabled.Maintenance.CampaignCalls
            + enabled.Maintenance.SessionCalls + enabled.Maintenance.CampaignCalls);
    }

    private CampaignWizardHarness CreateLongRestTask7Harness(bool campaign)
    {
        ArcanumSettings settings = DefaultSettings();

        settings.Features.LongRest = true;

        settings.Features.CampaignRollups = false;

        CanonicalCampaignContext scope = campaign ? CampaignRollupTurnSnapshotTests.Campaign : CanonicalCampaignContext.GlobalOnly;

        Session session = new() { Id = Guid.NewGuid(), CampaignId = scope.CampaignId };

        FakeGrimoireRepository grimoire = new() { Session = session };

        CampaignRollupTurnSnapshotTests.ProbeStore store = new()
        {
            Current = CampaignRollupTurnSnapshotTests.Artifact("Project convention from Session A: use SQLite.", ContentSensitivity.None),
        };

        CampaignWizardClaims claims = new();

        CampaignWizardMaintenance maintenance = new();

        CampaignWizardLookup lookup = new();

        CampaignRollupTurnPreparer preparer = new(store, new TestOptionsMonitor<ArcanumSettings>(settings),
            new LongRestTask7ScopeResolver(scope), null!, new LongRestTask7QueuedCoordinator(claims), UnreachedLongRestPorts.Maintenance,
            UnreachedLongRestPorts.Discovery, new LongRestTask7Configuration(settings),
            maintenance, claims, grimoire, claims,
            new CampaignRollupTurnSnapshotTests.Authority(), lookup, replayStore: claims);

        ScriptingChatClient chat = new();

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings, grimoire, campaignRollups: preparer,
            turnCommitter: claims, claimedBeginStore: claims, claims: claims);

        PingRequest request = BaseRequest() with
        {
            Prompt = "Continue this project.", SessionId = session.Id, SkipSpellRouting = true, DisableAllTools = true,
        };

        return new(wizard, chat, grimoire, settings, store, maintenance, claims, lookup, request);
    }

    private static ArcanumInvocationContext LongRestTask7Invocation(bool campaign) => ArcanumInvocationContext.Create(
        ArcanumExecutionSurface.SessionBackedOperatorTurn,
        campaign ? CampaignRollupTurnSnapshotTests.Campaign : CanonicalCampaignContext.GlobalOnly,
        InvocationAttendance.Attended, CovenantContextPolicy.Default, ToolPolicy.NoTools, null).Value;

    private static void AssertLongRestTask7NoRollupMaterial(IReadOnlyList<ChatMessage> messages)
    {
        ChatMessage[] systems = messages.Where(message => message.Role == ChatRole.System).ToArray();

        Assert.NotEmpty(systems);

        Assert.All(systems, message =>
        {
            string system = message.Text!;

            Assert.DoesNotContain("Project convention from Session A: use SQLite.", system, StringComparison.Ordinal);

            Assert.DoesNotContain("### Campaign Summary (cross-session context)", system, StringComparison.Ordinal);

            Assert.DoesNotContain("### Session Summary (compressed context)", system, StringComparison.Ordinal);
        });
    }

    // The ordinary authority-free harness has no protected debt. It consumes the clean
    // scheduling/configuration ports after pending ownership without running the worker itself.
    private sealed class LongRestTask7QueuedCoordinator(CampaignWizardClaims claims) : ILongRestSweepCoordinator
    {
        public Task<Result<LongRunningOperationDto>> RequestAsync(CanonicalCampaignContext scope, CancellationToken ct)
        {
            Assert.Single(claims.Requests);

            LongRunningOperationDto operation = new(Guid.NewGuid(), LongRunningOperationKinds.LongRestSweep,
                LongRunningOperationState.Pending, LongRunningOperationRecoveryPolicy.ResumeFromCheckpoint,
                null, null, null, null, null, null, null, DateTimeOffset.UtcNow,
                null, null, null, null, null, 0, 0, false, "Long Rest queued.", null, 0);

            return Task.FromResult(Result<LongRunningOperationDto>.Success(operation));
        }

        public Task<Result<LongRunningOperationDto>> RequestAsync(CanonicalCampaignContext scope, LongRestSweepScope partition, CancellationToken ct) =>
            throw new InvalidOperationException("An authority-free fixture cannot schedule protected work.");

        public Task<Result<LongRunningOperationDto>> RequestAsync(CanonicalCampaignContext scope, LongRestSweepScope partition, CovenantTurnLease authority, CancellationToken ct) =>
            throw new InvalidOperationException("An authority-free fixture cannot schedule protected work.");

        public Task<Result<LongRestSweepProgress>> ResumeAsync(Guid operation, LongRestMaintenanceCapability? capability, CancellationToken ct) =>
            throw new InvalidOperationException("Clean scheduling must not resume on the live turn.");
    }

    private sealed class LongRestTask7Configuration(ArcanumSettings settings) : ILongRestConfigurationProvider
    {
        private readonly Guid _boot = Guid.NewGuid();

        public LongRestSweepConfiguration Capture() => new(new(_boot, 0, CampaignRollupTurnPreparer.ConfigurationDigest(settings)),
            settings.Features.LongRest, settings.Features.Covenant,
            new Error(ErrorCodes.LongRest.Unavailable, "The fixture has no productive provider debt."));

        public bool IsCurrent(LongRestConfigurationStamp stamp) => stamp == Capture().Stamp;
    }

    private sealed class LongRestTask7ScopeResolver(CanonicalCampaignContext scope) : ICanonicalCampaignContextResolver
    {
        public ValueTask<Result<CanonicalCampaignContext>> ResolveAsync(CanonicalCampaignResolutionRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<CanonicalCampaignContext>.Success(scope));
    }
}
