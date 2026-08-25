using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// What an operator actually does with the Covenant, on an installation that starts holding nothing.
/// </summary>
/// <remarks>
/// Every rendering assertion here is made against the system message a provider was handed, never
/// against a turn plan. A plan is what a turn intends; four consecutive adversarial passes over this
/// feature found defects that lived entirely in the distance between an intended plan and a
/// dispatched prompt, and every one of them had a green test standing over it that stopped at the
/// plan.
///
/// <para>Storage starts genuinely empty. The Campaign rows and the Session-to-Campaign binding are
/// the only seeds, because a live turn refuses to proceed without them. Every Covenant row is
/// written by the operator's real prepare-and-commit pair or staged by the agent's real tool call
/// over the real in-process MCP transport, and every value handed to production is one a production
/// caller supplies.</para>
/// </remarks>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class CovenantOperatorJourneyTests : IAsyncLifetime
{

    private const string ModelName = "covenant-journey-test-model";

    private const string GlobalKey = "preference.builds";

    private const string GlobalPreference = "Run build commands from the repository root.";

    private const string CampaignKey = "preference.migrations";

    private const string CampaignPreference = "This Campaign ships its migrations by hand.";

    private const string ProposedKey = "tests.reply.style";

    private const string ProposedContent = "answer with the failing assertion first";

    private const string OperatorPrompt = "how should I run the build";

    private const string AssistantAnswer = "From the repository root.";

    /// <summary>The page size the operator's own list and version commands send.</summary>
    /// <remarks>
    /// Taken from the CLI rather than left at the wire default. <c>CovenantListRequest.EffectiveLimit</c>
    /// documents zero as "use the default", but the service digests and queries the raw
    /// <c>Limit</c>, so a zero throws out of the read instead of paging — a separate defect, and not
    /// one these journeys are about.
    /// </remarks>
    private const int OperatorPageSize = 50;

    private static readonly Guid CampaignA = CovenantOperationGateFixture.CampaignOne;

    private static readonly Guid CampaignB = CovenantOperationGateFixture.CampaignTwo;

    private readonly GrimoireFixture _fixture;

    private readonly FakeCovenantAvailability _availability = new();

    private readonly FakeCovenantAuthorityProvider _authority = new();

    private readonly FakeCovenantCampaignScopeProbe _campaigns = new();

    private readonly AcceptingDisclosureJournal _journal = new();

    private readonly PassthroughEnvelopeCodec _codec = new();

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    private SqliteConnection? _connection;

    private CovenantOperationGate? _operationGate;

    public CovenantOperatorJourneyTests(GrimoireFixture fixture) =>
        _fixture = fixture;

    public Task InitializeAsync()
    {

        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        return Task.CompletedTask;

    }

    public async Task DisposeAsync()
    {

        if (_db is not null)
        {

            await _db.DisposeAsync();

        }

        if (File.Exists(_dbPath))
        {

            File.Delete(_dbPath);

        }

    }

    [SkippableFact]
    public async Task A_global_preference_the_operator_stated_is_in_the_prompt_of_a_later_turn_in_another_campaign()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedCampaignAsync(CampaignA, "journey-a");

        await SeedCampaignAsync(CampaignB, "journey-b");

        Result<CovenantMutationResultDto> stated = await SetGlobalAsync(GlobalPreference, expectedRevision: 0);

        Assert.True(stated.IsSuccess, stated.IsFailure ? $"{stated.Error.Code}: {stated.Error.Message}" : null);

        // A different Campaign, and a Session that did not exist when the preference was written. A
        // Global arm that had quietly acquired a Campaign predicate is correct for exactly one
        // Campaign, so asserting inside the author's own would let that defect through.
        string prompt = await RunTurnAsync(CampaignB);

        Assert.Contains(GlobalPreference, prompt, StringComparison.Ordinal);

    }

    [SkippableFact]
    public async Task A_campaign_preference_is_in_the_prompt_of_its_own_campaign_and_of_no_other()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedCampaignAsync(CampaignA, "journey-a");

        await SeedCampaignAsync(CampaignB, "journey-b");

        Result<CovenantMutationResultDto> stated = await SetCampaignAsync(
            CampaignA,
            CampaignKey,
            CampaignPreference,
            expectedRevision: 0);

        Assert.True(stated.IsSuccess, stated.IsFailure ? $"{stated.Error.Code}: {stated.Error.Message}" : null);

        string own = await RunTurnAsync(CampaignA);

        Assert.Contains(CampaignPreference, own, StringComparison.Ordinal);

        // The containment half of the sentence. A Campaign preference that followed the operator into
        // their other work would be a Global preference nobody agreed to.
        string other = await RunTurnAsync(CampaignB);

        Assert.DoesNotContain(CampaignPreference, other, StringComparison.Ordinal);

    }

    [SkippableFact]
    public async Task A_retired_preference_reaches_no_later_prompt_and_cannot_return_without_an_explicit_reactivation()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedCampaignAsync(CampaignA, "journey-a");

        Result<CovenantMutationResultDto> stated = await SetGlobalAsync(GlobalPreference, expectedRevision: 0);

        Assert.True(stated.IsSuccess, stated.IsFailure ? $"{stated.Error.Code}: {stated.Error.Message}" : null);

        Assert.Contains(GlobalPreference, await RunTurnAsync(CampaignA), StringComparison.Ordinal);

        Result<CovenantMutationResultDto> retired = await RetireGlobalAsync(expectedRevision: 1);

        Assert.True(retired.IsSuccess, retired.IsFailure ? $"{retired.Error.Code}: {retired.Error.Message}" : null);

        // The rendering is the whole of the promise. A retired head that still reached a prompt would
        // mean the operator withdrew a statement the model is still being shown.
        Assert.DoesNotContain(GlobalPreference, await RunTurnAsync(CampaignA), StringComparison.Ordinal);

        // Retirement is the operator saying "stop honoring this". A later write that silently revived
        // the key would make the withdrawal a suggestion.
        Result<CovenantMutationResultDto> revived = await SetGlobalAsync("Quietly back again.", expectedRevision: 2);

        Assert.True(revived.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.LifecycleConflict, revived.Error.Code);

        Assert.DoesNotContain("Quietly back again.", await RunTurnAsync(CampaignA), StringComparison.Ordinal);

        // The refusal has to be a gate rather than a wall, or retiring one key would be a decision the
        // operator could never undo.
        Result<CovenantMutationResultDto> reactivated = await SetGlobalAsync(
            "Deliberately back again.",
            expectedRevision: 2,
            reactivate: true);

        Assert.True(
            reactivated.IsSuccess,
            reactivated.IsFailure ? $"{reactivated.Error.Code}: {reactivated.Error.Message}" : null);

        Assert.Contains("Deliberately back again.", await RunTurnAsync(CampaignA), StringComparison.Ordinal);

    }

    [SkippableFact]
    public async Task An_operator_can_discover_that_the_agent_left_a_proposal_waiting_for_them()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedCampaignAsync(CampaignA, "journey-a");

        ProposalTurn proposed = await ProposeAsync(CampaignA, [(ProposedKey, ProposedContent)]);

        Assert.True(proposed.Turn.IsSuccess, proposed.Turn.IsFailure ? proposed.Turn.Error.Message : null);

        CovenantPageDto page = await ListAsync(CampaignA);

        CovenantHeadDto proposal = Assert.Single(
            page.Items,
            static item => item.Lane is CovenantLane.Proposed);

        Assert.Equal(ProposedKey, proposal.Key);

        Assert.Equal(CovenantScope.Campaign, proposal.Scope);

        Assert.Equal(CampaignA, proposal.CampaignId);

        // The origin is what tells an operator this is a suggestion rather than something they wrote
        // and forgot. A proposal indistinguishable from their own statement is one they cannot judge.
        Assert.Equal(CovenantOrigin.AgentProposed, proposal.Origin);

    }

    [SkippableFact]
    public async Task An_operator_can_read_what_the_agent_proposed_before_deciding_what_to_do_about_it()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedCampaignAsync(CampaignA, "journey-a");

        ProposalTurn proposed = await ProposeAsync(CampaignA, [(ProposedKey, ProposedContent)]);

        Assert.True(proposed.Turn.IsSuccess, proposed.Turn.IsFailure ? proposed.Turn.Error.Message : null);

        // Every operator read surface this build maps, asked the only question that decides what the
        // operator does next: what does the proposal say. Knowing a key exists is not enough to accept
        // or retire it, and a rendered hash is not a sentence anyone can judge. Each surface is
        // rendered whole, so a content field anywhere in its payload counts.
        string[] surfaces = await OperatorReadableSurfacesAsync(CampaignA, ProposedKey);

        Assert.Contains(surfaces, surface => surface.Contains(ProposedContent, StringComparison.Ordinal));

    }

    [SkippableFact]
    public async Task A_full_proposed_lane_refuses_a_further_proposal_without_costing_the_operator_their_answer()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedCampaignAsync(CampaignA, "journey-a");

        // Filled the way the lane fills in service: whole turns, each staging what one turn is allowed
        // to stage. Seeding thirty-two heads directly would prove the ceiling arithmetic and nothing
        // about the path an agent actually takes to reach it.
        int filled = 0;

        while (filled < CovenantLimits.MaxCampaignProposedEntries)
        {

            int batch = Math.Min(
                CovenantLimits.MaxStagedMutationsPerTurn,
                CovenantLimits.MaxCampaignProposedEntries - filled);

            (string Key, string Content)[] proposals =
            [
                .. Enumerable.Range(filled, batch).Select(static ordinal =>
                    ($"journey.proposal.{ordinal:D2}", $"proposal {ordinal:D2} body")),
            ];

            ProposalTurn round = await ProposeAsync(CampaignA, proposals);

            Assert.True(round.Turn.IsSuccess, round.Turn.IsFailure ? round.Turn.Error.Message : null);

            Assert.All(round.Failures, static failure => Assert.Null(failure));

            filled += batch;

        }

        Assert.Equal(
            CovenantLimits.MaxCampaignProposedEntries,
            (await ListAsync(CampaignA)).Items.Count(static item => item.Lane is CovenantLane.Proposed));

        ProposalTurn overflow = await ProposeAsync(
            CampaignA,
            [("journey.proposal.overflow", "one proposal past the ceiling")]);

        // Where the refusal lands is deliberately left open: refusing at the staging tool, so the
        // model learns the lane is full, and accepting nothing at publication are both honest. What
        // is asserted is the part an operator experiences.

        // The operator asked a question and an answer was produced for them. Whatever the platform
        // decides about the proposal, discarding the reply makes a full lane silently cost the
        // operator the turn they paid for, and nothing in the conversation says why.
        Assert.True(
            overflow.Turn.IsSuccess,
            overflow.Turn.IsFailure ? $"{overflow.Turn.Error.Code}: {overflow.Turn.Error.Message}" : null);

        Assert.Equal(AssistantAnswer, await ReadLastAssistantContentAsync(overflow.SessionId));

        // And the lane is still exactly full, because the refusal has to be a refusal.
        Assert.Equal(
            CovenantLimits.MaxCampaignProposedEntries,
            (await ListAsync(CampaignA)).Items.Count(static item => item.Lane is CovenantLane.Proposed));

    }

    [SkippableFact]
    public async Task A_turn_with_the_feature_off_sends_the_bytes_an_installation_that_never_had_a_covenant_sends()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedCampaignAsync(CampaignA, "journey-a");

        // The baseline is taken first, against storage holding no Covenant row and a host that composed
        // no Covenant arm at all. Taking it afterwards would let a Covenant write that had touched some
        // shared prompt input pass unnoticed, in both directions.
        string never = await RunTurnAsync(CampaignA, withCovenantArm: false);

        Result<CovenantMutationResultDto> global = await SetGlobalAsync(GlobalPreference, expectedRevision: 0);

        Assert.True(global.IsSuccess, global.IsFailure ? $"{global.Error.Code}: {global.Error.Message}" : null);

        Result<CovenantMutationResultDto> campaign = await SetCampaignAsync(
            CampaignA,
            CampaignKey,
            CampaignPreference,
            expectedRevision: 0);

        Assert.True(campaign.IsSuccess, campaign.IsFailure ? $"{campaign.Error.Code}: {campaign.Error.Message}" : null);

        // The operator turns the feature off with content already in storage, which is the
        // configuration flip a host republishes availability for rather than a fresh installation that
        // never had any.
        _availability.Mutate(static current => current with { FeatureEnabled = false });

        string off = await RunTurnAsync(CampaignA);

        // Byte-identical, not merely free of the two statements. "Injects nothing" has to mean the
        // prompt is the one the operator would have had, framing, headings and all.
        Assert.Equal(never, off);

    }

    [SkippableFact]
    public async Task An_operator_whose_canonical_tier_goes_down_is_told_the_count_was_not_taken()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedCampaignAsync(CampaignA, "journey-a");

        Result<CovenantMutationResultDto> stated = await SetGlobalAsync(GlobalPreference, expectedRevision: 0);

        Assert.True(stated.IsSuccess, stated.IsFailure ? $"{stated.Error.Code}: {stated.Error.Message}" : null);

        Result<CovenantStatusDto> healthy = await ManagementService().StatusAsync(CancellationToken.None);

        Assert.True(healthy.IsSuccess, healthy.IsFailure ? healthy.Error.Message : null);

        Assert.Equal(CovenantCensusReadState.Read, healthy.Value.Census);

        Assert.NotEmpty(healthy.Value.Counts);

        // The tier goes down under an operator who already holds an entry. This is the moment a zero is
        // at its most dangerous: it is indistinguishable from the installation they started with.
        _availability.Mutate(static current => current with
        {
            Canonical = CovenantCapabilityState.Unavailable,
            CanonicalDiagnosticCode = "canonical-unavailable",
        });

        Result<CovenantStatusDto> degraded = await ManagementService().StatusAsync(CancellationToken.None);

        Assert.True(degraded.IsSuccess, degraded.IsFailure ? degraded.Error.Message : null);

        Assert.False(degraded.Value.Available);

        // Not Read. A census reporting Read beside an empty row set is the platform stating, as a
        // measurement, that the operator holds nothing.
        Assert.NotEqual(CovenantCensusReadState.Read, degraded.Value.Census);

        Assert.Empty(degraded.Value.Counts);

        Assert.Equal("canonical-unavailable", degraded.Value.DegradationCode);

    }

    /// <summary>
    /// Drives one whole turn and returns the system message the provider was handed.
    /// </summary>
    /// <remarks>
    /// Entry is <c>ExecutePromptAsync</c>, so the Covenant wrapper, the admission decision, the
    /// envelope freeze and the prompt rebuilds all run. The captured string is the transcript's own
    /// first message, which the dispatch gate compares the frozen envelope against — so it is the same
    /// bytes the provider receives and the receipt describes.
    /// </remarks>
    private async Task<string> RunTurnAsync(Guid campaignId, bool withCovenantArm = true)
    {

        Guid sessionId = await SeedSessionAsync(campaignId);

        RecordingChatClient chat = new(AssistantAnswer);

        CovenantToolCapabilityRegistry registry = new();

        Result<PromptTurnResult> turn = await Wizard(
                chat,

                // A host that composed no Covenant arm hands the provider exactly these nulls; the
                // shared factory only declares them non-null for its other callers.
                withCovenantArm ? Gate() : null!,
                withCovenantArm ? registry : null!)
            .ExecutePromptAsync(
                new PingRequest(
                    Prompt: OperatorPrompt,
                    Model: ModelName,
                    WorkingDirectory: string.Empty,
                    SessionId: sessionId,
                    DisableMcpTools: true,
                    SkipSpellRouting: true),
                Invocation(campaignId),
                CancellationToken.None);

        Assert.True(turn.IsSuccess, turn.IsFailure ? $"{turn.Error.Code}: {turn.Error.Message}" : null);

        Assert.NotNull(chat.SystemPrompt);

        return chat.SystemPrompt!;

    }

    /// <summary>
    /// Drives one whole turn whose model round stages the given proposals through the real tool.
    /// </summary>
    private async Task<ProposalTurn> ProposeAsync(
        Guid campaignId,
        IReadOnlyList<(string Key, string Content)> proposals)
    {

        Guid sessionId = await SeedSessionAsync(campaignId);

        CovenantToolCapabilityRegistry registry = new();

        await using CovenantToolCall toolCall = await CovenantToolCall.CreateAsync(registry, _availability);

        StagingChatClient chat = new(toolCall, proposals, AssistantAnswer);

        Result<PromptTurnResult> turn = await Wizard(chat, Gate(), registry).ExecutePromptAsync(
            new PingRequest(
                Prompt: OperatorPrompt,
                Model: ModelName,
                WorkingDirectory: string.Empty,
                SessionId: sessionId,
                DisableMcpTools: true,
                SkipSpellRouting: true),
            Invocation(campaignId),
            CancellationToken.None);

        return new ProposalTurn(sessionId, turn, chat.Failures, chat.SawStagingCapability);

    }

    /// <summary>One turn's outcome, and what its scripted tool round observed while inside it.</summary>
    private sealed record ProposalTurn(
        Guid SessionId,
        Result<PromptTurnResult> Turn,
        IReadOnlyList<CovenantMutationFailureResultWire?> Failures,
        bool SawStagingCapability);

    /// <summary>
    /// Every mapped operator read surface for one key, rendered whole.
    /// </summary>
    /// <remarks>
    /// Rendered rather than probed field by field so the question stays "can the operator read this
    /// anywhere" rather than "does this particular property exist". Free-text query is not among them:
    /// it is deliberately unmapped on this build and refuses every call.
    /// </remarks>
    private async Task<string[]> OperatorReadableSurfacesAsync(Guid campaignId, string key)
    {

        CovenantManagementService management = ManagementService();

        List<string> surfaces = [];

        await using (CovenantInstallationReadLease read = await InstallationReadAsync())
        {

            Result<CovenantPageDto> page = await management.ListAsync(
                new CovenantListRequest(
                    CovenantCursorScopeSelection.Campaign,
                    campaignId,
                    Lane: null,
                    CovenantLifecycle.Set,
                    campaignId,
                    Limit: OperatorPageSize,
                    Cursor: null),
                read,
                CancellationToken.None);

            Assert.True(page.IsSuccess, page.IsFailure ? page.Error.Message : null);

            surfaces.Add(page.Value.ToString());

            Result<CovenantDetailDto> detail = await management.DetailAsync(
                new CovenantDetailRequest(CovenantScope.Campaign, campaignId, key),
                read,
                CancellationToken.None);

            Assert.True(detail.IsSuccess, detail.IsFailure ? detail.Error.Message : null);

            surfaces.Add(detail.Value.ToString());

            CovenantHeadDto proposed = Assert.IsType<CovenantHeadDto>(detail.Value.Proposed);

            Result<CovenantVersionPageDto> versions = await management.VersionsAsync(
                new CovenantVersionsRequest(proposed.EntryId, CovenantLane.Proposed, Limit: OperatorPageSize, Cursor: null),
                read,
                CancellationToken.None);

            Assert.True(versions.IsSuccess, versions.IsFailure ? versions.Error.Message : null);

            surfaces.Add(versions.Value.ToString());

            Result<CovenantSourcesDto> sources = await management.SourcesAsync(
                new CovenantSourcesRequest(proposed.VersionId),
                read,
                CancellationToken.None);

            Assert.True(sources.IsSuccess, sources.IsFailure ? sources.Error.Message : null);

            surfaces.Add(sources.Value.ToString());

        }

        // The one surface that is documented to return content, asked with the privacy gate open and
        // the Campaign the proposal lives in named.
        CovenantLeasedServiceResult<CovenantExplainDto> explained = await management.ExplainAsync(
            new CovenantExplainRequest(campaignId, ShowContent: true),
            CancellationToken.None);

        (Result<CovenantExplainDto> payload, ICovenantOperationLease explainLease) = explained.Take();

        await using (explainLease)
        {

            Assert.True(payload.IsSuccess, payload.IsFailure ? payload.Error.Message : null);

            surfaces.Add(payload.Value.ToString());

            surfaces.AddRange(payload.Value.Sections
                .Select(static section => section.Content)
                .OfType<string>());

        }

        return [.. surfaces];

    }

    private async Task<CovenantPageDto> ListAsync(Guid campaignId)
    {

        await using CovenantInstallationReadLease read = await InstallationReadAsync();

        Result<CovenantPageDto> page = await ManagementService().ListAsync(
            new CovenantListRequest(
                CovenantCursorScopeSelection.Campaign,
                campaignId,
                Lane: null,
                CovenantLifecycle.Set,
                campaignId,
                Limit: OperatorPageSize,
                Cursor: null),
            read,
            CancellationToken.None);

        Assert.True(page.IsSuccess, page.IsFailure ? $"{page.Error.Code}: {page.Error.Message}" : null);

        return page.Value;

    }

    private async Task<CovenantInstallationReadLease> InstallationReadAsync()
    {

        Result<CovenantInstallationReadLease> read = await OperationGate()
            .AcquireInstallationReadAsync(CancellationToken.None);

        Assert.True(read.IsSuccess, read.IsFailure ? $"{read.Error.Code}: {read.Error.Message}" : null);

        return read.Value;

    }

    private Task<Result<CovenantMutationResultDto>> SetGlobalAsync(
        string content,
        long expectedRevision,
        bool reactivate = false) =>
        SetAsync(CovenantScope.Global, null, GlobalKey, content, expectedRevision, reactivate);

    private Task<Result<CovenantMutationResultDto>> SetCampaignAsync(
        Guid campaignId,
        string key,
        string content,
        long expectedRevision,
        bool reactivate = false) =>
        SetAsync(CovenantScope.Campaign, campaignId, key, content, expectedRevision, reactivate);

    /// <summary>
    /// The operator's write, both halves of it, through the pair the route pair calls.
    /// </summary>
    /// <remarks>
    /// Prepare and commit are separate acquisitions because they are separate requests in service: an
    /// installation read capability measures the effect, and a scoped write capability commits exactly
    /// the effect that measurement described.
    /// </remarks>
    private async Task<Result<CovenantMutationResultDto>> SetAsync(
        CovenantScope scope,
        Guid? campaignId,
        string key,
        string content,
        long expectedRevision,
        bool reactivate)
    {

        CovenantMutationService service = MutationService();

        Guid mutationId = Guid.CreateVersion7();

        string preflight;

        await using (CovenantInstallationReadLease read = await InstallationReadAsync())
        {

            Result<CovenantMutationPreflightDto> prepared = await service.PrepareSetAsync(
                new CovenantSetPrepareRequest(scope, campaignId, key, content, expectedRevision, mutationId, reactivate),
                read,
                CancellationToken.None);

            if (prepared.IsFailure)
            {

                return prepared.Error;

            }

            preflight = prepared.Value.PreflightToken;

        }

        await using CovenantWriteLease write = await WriteAsync(scope, campaignId);

        return await service.SetAsync(
            new CovenantSetRequest(scope, campaignId, key, content, expectedRevision, mutationId, reactivate, preflight),
            write,
            CancellationToken.None);

    }

    private async Task<Result<CovenantMutationResultDto>> RetireGlobalAsync(long expectedRevision)
    {

        CovenantMutationService service = MutationService();

        Guid mutationId = Guid.CreateVersion7();

        string preflight;

        await using (CovenantInstallationReadLease read = await InstallationReadAsync())
        {

            Result<CovenantMutationPreflightDto> prepared = await service.PrepareRetireAsync(
                new CovenantRetirePrepareRequest(
                    CovenantScope.Global,
                    null,
                    GlobalKey,
                    CovenantLane.Confirmed,
                    expectedRevision,
                    mutationId),
                read,
                CancellationToken.None);

            if (prepared.IsFailure)
            {

                return prepared.Error;

            }

            preflight = prepared.Value.PreflightToken;

        }

        await using CovenantWriteLease write = await WriteAsync(CovenantScope.Global, null);

        return await service.RetireAsync(
            new CovenantRetireRequest(
                CovenantScope.Global,
                null,
                GlobalKey,
                CovenantLane.Confirmed,
                expectedRevision,
                mutationId,
                preflight),
            write,
            CancellationToken.None);

    }

    private async Task<CovenantWriteLease> WriteAsync(CovenantScope scope, Guid? campaignId)
    {

        CovenantOperationScope operationScope = scope is CovenantScope.Campaign && campaignId is { } id
            ? CovenantOperationScope.ForCampaign(id)
            : CovenantOperationScope.Global;

        Result<CovenantWriteLease> write = await OperationGate()
            .AcquireWriteAsync(operationScope, CancellationToken.None);

        Assert.True(write.IsSuccess, write.IsFailure ? $"{write.Error.Code}: {write.Error.Message}" : null);

        return write.Value;

    }

    private WizardIntelligenceProvider Wizard(
        IChatClient chat,
        CovenantDispatchGate gate,
        CovenantToolCapabilityRegistry registry)
    {

        ProviderSettings provider = new()
        {
            Name = "provider-covenant-journey",
            Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://example.test/v1",
            Models = [ModelName],
            ContextWindowLimit = 32_768,
        };

        GrimoireRepository repository = Repository();

        return WizardIntelligenceProviderFallbackTests.CreateCovenantStagingWizard(
            new SingleLeaseChatClientFactory(chat, provider, ModelName),
            gate,
            registry,
            repository,
            repository,
            provider);

    }

    /// <summary>
    /// The composed dispatch gate over the real store, provider, linker, and operation gate.
    /// </summary>
    /// <remarks>
    /// One operation gate for the whole test, because a second would mint its own authority and every
    /// turn built against the first would be refused as stale — the same shape as a real failure, and
    /// it would hide one.
    /// </remarks>
    private CovenantDispatchGate Gate() =>
        new(
            new CovenantContextProvider(
                _availability,
                OperationGate(),
                Store(),
                new CovenantLinker()),
            _journal,
            new ArtifactSensitivityLedger(new FixedCovenantConnectionSource(Connection())),
            _authority,
            TimeProvider.System,
            NullLogger<CovenantDispatchGate>.Instance);

    private CovenantManagementService ManagementService() =>
        new(
            Store(),
            new CovenantLinker(),
            OperationGate(),
            _availability,
            _codec,
            new CampaignAvailabilityReader(new FixedCovenantConnectionSource(Connection())));

    private CovenantMutationService MutationService() =>
        new(
            Store(),
            new CovenantCompiler(),
            _codec,
            new FixedCovenantConnectionSource(Connection()),
            new CovenantMutationKernel(),
            _authority,
            TimeProvider.System);

    private CovenantStore Store() => new(new FixedCovenantConnectionSource(Connection()));

    private CovenantOperationGate OperationGate()
    {

        if (_operationGate is not null)
        {

            return _operationGate;

        }

        _campaigns.Set(CampaignA, CovenantCampaignScopeState.Live);

        _campaigns.Set(CampaignB, CovenantCampaignScopeState.Live);

        _operationGate = CovenantOperationGateFixture.CreateGate(_availability, _authority, _campaigns);

        return _operationGate;

    }

    /// <summary>
    /// An attended Campaign-bound turn whose read epoch matches the live authority.
    /// </summary>
    /// <remarks>
    /// Built from the authority the gate actually publishes rather than from a constant. The provider
    /// refuses a turn whose epoch does not match the lease it just took, and a hard-coded epoch would
    /// degrade every turn here to <see cref="CovenantTurnAbsence.CapabilityUnavailable"/> — a silent
    /// absence under which "nothing was injected" would read as a passing assertion.
    /// </remarks>
    private ArcanumInvocationContext Invocation(Guid campaignId)
    {

        _ = OperationGate();

        CovenantAuthoritySnapshot authority = _authority.Current!;

        return ArcanumInvocationContext.Create(
            ArcanumExecutionSurface.SessionBackedOperatorTurn,
            CovenantOperationGateFixture.CampaignContext(campaignId),
            InvocationAttendance.Attended,
            CovenantContextPolicy.Default,
            ToolPolicy.AllTools,
            CovenantReadAuthorityEpoch.CreateForTests(
                Guid.Parse(authority.InstallationIdentity),
                authority.RuntimeAuthorityGeneration,
                authority.AuthorityEpoch)).Value;

    }

    private GrimoireRepository Repository() =>
        new(
            _db!,
            new NoOpSessionAttachmentStore(),
            NullLogger<GrimoireRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()),
            attachmentIndex: null,
            new CovenantMutationKernel());

    private SqliteConnection Connection()
    {

        if (_connection is not null)
        {

            return _connection;

        }

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {

            connection.Open();

        }

        _connection = connection;

        return connection;

    }

    /// <summary>
    /// Seeds one Campaign through EF rather than raw SQL.
    /// </summary>
    /// <remarks>
    /// EF's SQLite mapping stores a <see cref="Guid"/> key as uppercase <c>D</c>-format text, and the
    /// canonical store's Campaign predicates compare against that column. A hand-written lowercase row
    /// would seed a Campaign no canonical query could ever match.
    /// </remarks>
    private async Task SeedCampaignAsync(Guid campaignId, string name)
    {

        DateTimeOffset now = DateTimeOffset.UtcNow;

        _ = _db!.Campaigns.Add(new Campaign
        {
            Id = campaignId,
            Name = name,
            NameLower = name,
            Path = Path.Combine(Path.GetTempPath(), name),
            Type = WorkspaceType.Campaign,
            CreatedAt = now,
            UpdatedAt = now,
        });

        _ = await _db.SaveChangesAsync(CancellationToken.None);

    }

    /// <summary>
    /// An empty Session with the immutable Campaign binding a live turn refuses to proceed without.
    /// </summary>
    /// <remarks>
    /// The binding row is written the way the turn path writes and reads one — the same table, the same
    /// authorization scope its guard trigger demands, and the same lowercase identity text
    /// <c>GrimoireRepository</c> binds and queries with. Its foreign key is stood down for that one
    /// statement because EF stores the Session key as uppercase text and the binding writer does not,
    /// so the reference can never hold; that mismatch is a defect of its own and not one this file can
    /// assert around.
    ///
    /// <para>Nothing else is seeded. The turn writes its own user entry and assistant placeholder, and
    /// there are no Covenant rows and no sensitivity labels.</para>
    /// </remarks>
    private async Task<Guid> SeedSessionAsync(Guid campaignId)
    {

        Guid sessionId = Guid.NewGuid();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        _ = _db!.Sessions.Add(new Session
        {
            Id = sessionId,
            CampaignId = campaignId,
            CreatedAt = now,
            UpdatedAt = now,
            Status = "active",
            Title = "covenant journey",
            UnsummarizedEntryCount = 0,
        });

        _ = await _db.SaveChangesAsync(CancellationToken.None);

        await SetForeignKeyEnforcementAsync(enabled: false);

        try
        {

            using CovenantSqliteAuthorizationScope authorized = CovenantSqliteConnectionInitializer.Instance.Authorize(
                Connection(),
                CovenantSqliteAuthorizationKind.SessionBindingWrite);

            await using SqliteCommand command = Connection().CreateCommand();

            command.CommandText = """
                INSERT INTO session_campaign_bindings (SessionId, BindingKindCode, CampaignId, BoundAtUtc)
                VALUES ($sessionId, $kindCode, $campaignId, $boundAtUtc);
                """;

            _ = command.Parameters.AddWithValue("$sessionId", sessionId.ToString());

            _ = command.Parameters.AddWithValue(
                "$kindCode",
                (long)SessionCampaignBinding.ForCampaign(campaignId).Kind);

            _ = command.Parameters.AddWithValue("$campaignId", campaignId.ToString());

            _ = command.Parameters.AddWithValue(
                "$boundAtUtc",
                now.ToString("o", System.Globalization.CultureInfo.InvariantCulture));

            _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

        }
        finally
        {

            await SetForeignKeyEnforcementAsync(enabled: true);

        }

        return sessionId;

    }

    private async Task SetForeignKeyEnforcementAsync(bool enabled)
    {

        await using SqliteCommand pragma = Connection().CreateCommand();

        pragma.CommandText = enabled ? "PRAGMA foreign_keys = ON;" : "PRAGMA foreign_keys = OFF;";

        _ = await pragma.ExecuteNonQueryAsync(CancellationToken.None);

    }

    private async Task<string?> ReadLastAssistantContentAsync(Guid sessionId) =>
        await _db!.Entries
            .AsNoTracking()
            .Where(entry => entry.SessionId == sessionId && entry.Role == MessageRole.Assistant)
            .OrderByDescending(entry => entry.Sequence)
            .Select(entry => entry.Content)
            .FirstOrDefaultAsync(CancellationToken.None);

    /// <summary>A scripted provider that keeps the system message it was handed.</summary>
    /// <remarks>
    /// The first message of the transcript, taken inside the turn's own async flow. The dispatch gate
    /// refuses any dispatch whose frozen envelope disagrees with this same message, so what is captured
    /// here is what the provider receives and what the admission receipt describes.
    /// </remarks>
    private sealed class RecordingChatClient(string answer) : IChatClient
    {

        public string? SystemPrompt { get; private set; }

        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<MeAiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {

            MeAiChatMessage first = messages.First();

            Assert.Equal(ChatRole.System, first.Role);

            SystemPrompt = first.Text;

            return Task.FromResult(new ChatResponse(new MeAiChatMessage(ChatRole.Assistant, answer)));

        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<MeAiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

    }

    /// <summary>
    /// A scripted provider that spends the turn's staging capability the way a tool round does.
    /// </summary>
    /// <remarks>
    /// It runs inside the turn's async flow, which is the whole point: the staging ambient is an
    /// <c>AsyncLocal</c> the turn loop pushes around the provider call, so a client that cannot see it
    /// here is a turn in which no tool call could have seen it either. Nothing is constructed by hand —
    /// the ambient is read, handed to the production binder, and the tool is then called over the wire.
    /// </remarks>
    private sealed class StagingChatClient(
        CovenantToolCall toolCall,
        IReadOnlyList<(string Key, string Content)> proposals,
        string answer) : IChatClient
    {

        private readonly List<CovenantMutationFailureResultWire?> _failures = [];

        public bool SawStagingCapability { get; private set; }

        public IReadOnlyList<CovenantMutationFailureResultWire?> Failures => _failures;

        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<MeAiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {

            SawStagingCapability = CovenantToolStagingAmbient.Current is not null;

            foreach ((string key, string content) in proposals)
            {

                McpToolsCallResultWire result = await toolCall.ProposeAsync(key, content).ConfigureAwait(false);

                _failures.Add(result.IsError
                    ? JsonSerializer.Deserialize(
                        result.StructuredContent!.Value,
                        McpJsonSerializerContext.Default.CovenantMutationFailureResultWire)
                    : null);

            }

            return new ChatResponse(new MeAiChatMessage(ChatRole.Assistant, answer));

        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<MeAiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

    }

    /// <summary>
    /// One live in-process MCP server, driven over its real transport.
    /// </summary>
    /// <remarks>
    /// The binder and the server are both production. <c>ApplyAmbientBinding</c> is what mints the
    /// capability from the staging ambient and registers it under the connection and request identity;
    /// the server is what takes it back out and publishes it to the handler. Registering a capability
    /// by hand would skip precisely the handover the whole capability model exists to enforce.
    /// </remarks>
    private sealed class CovenantToolCall : IAsyncDisposable
    {

        private readonly InProcessMcpTransport _transport;

        private readonly Task _serverTask;

        private readonly CancellationTokenSource _lifetime;

        private readonly string _connectionKey;

        private int _nextId;

        private CovenantToolCall(
            InProcessMcpTransport transport,
            Task serverTask,
            CancellationTokenSource lifetime,
            string connectionKey)
        {

            _transport = transport;

            _serverTask = serverTask;

            _lifetime = lifetime;

            _connectionKey = connectionKey;

        }

        public static async Task<CovenantToolCall> CreateAsync(
            CovenantToolCapabilityRegistry registry,
            ICovenantAvailability availability)
        {

            ServiceCollection services = [];

            services.AddSingleton<ICovenantCompiler, CovenantCompiler>();

            services.AddSingleton(registry);

            services.AddSingleton(availability);

            services.AddSingleton<IOptionsMonitor<ArcanumSettings>>(
                new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

            ServiceProvider provider = services.BuildServiceProvider();

            IServiceScopeFactory scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

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
                    EnableLexiconSystem = false,
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

            return new CovenantToolCall(transport, serverTask, lifetime, server.AmbientConnectionKey);

        }

        public async Task<McpToolsCallResultWire> ProposeAsync(string key, string content)
        {

            int id = Interlocked.Increment(ref _nextId);

            JsonElement arguments = JsonSerializer.SerializeToElement(
                new ProposeCovenantParams(key, content),
                McpJsonSerializerContext.Default.ProposeCovenantParams);

            JsonRpcRequest request = new()
            {
                Method = "tools/call",
                Params = JsonSerializer.SerializeToElement(
                    new McpToolsCallParams
                    {
                        Name = CovenantToolNames.ProposeCovenant,
                        Arguments = arguments,
                    },
                    McpJsonSerializerContext.Default.McpToolsCallParams),
                Id = JsonSerializer.SerializeToElement(id, McpJsonSerializerContext.Default.Int32),
            };

            // The production binding site. It reads the staging ambient this turn published and mints
            // the single-use capability from it, or mints nothing at all.
            JsonRpcRequest bound = SessionAttachmentAmbientSend.ApplyAmbientBinding(_connectionKey, request);

            await _transport.WriteRequestAsync(bound).ConfigureAwait(false);

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

    private sealed class SingleLeaseChatClientFactory(
        IChatClient client,
        ProviderSettings provider,
        string model) : IChatClientFactory
    {

        public Task<ChatClientLease> ResolveClientAsync(string? targetModel, CancellationToken cancellationToken) =>
            Task.FromResult(new ChatClientLease(client, provider, model, ownedHttpClient: null));

        public Task<ChatClientLease> ResolveClientAsync(
            ProviderSettings candidate,
            string resolvedModel,
            CancellationToken cancellationToken) =>
            ResolveClientAsync(resolvedModel, cancellationToken);

    }

    /// <summary>A journal that accepts every disclosure it is handed.</summary>
    /// <remarks>
    /// Disclosure accounting has its own suite. What matters to these journeys is only that a tainted
    /// dispatch is never blocked by a journal that was never composed for it.
    /// </remarks>
    private sealed class AcceptingDisclosureJournal : ICovenantDisclosureJournal
    {

        private long _sequence;

        public ValueTask<Result<CovenantDisclosureReceipt>> AcknowledgeAsync(
            CovenantDisclosureDraft draft,
            CovenantDisclosureEffectCategory category,
            ProviderCallSensitivity sensitivity,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<CovenantDisclosureReceipt>.Success(
                new CovenantDisclosureReceipt(draft, (ulong)Interlocked.Increment(ref _sequence))));

    }

    /// <summary>
    /// A codec that authenticates by construction rather than by key material.
    /// </summary>
    /// <remarks>
    /// The envelope protocol has its own vectors and its own suite. What these journeys are about is
    /// what an operator can state, read and withdraw, so the stand-in keeps the exact shape — purpose,
    /// timestamps, payload — and skips only the cryptography.
    /// </remarks>
    private sealed class PassthroughEnvelopeCodec : ICovenantEnvelopeCodec
    {

        private readonly Dictionary<string, CovenantEnvelopeBody> _issued = new(StringComparer.Ordinal);

        public CovenantEnvelopeKeySnapshot KeySnapshot { get; } =
            new(1, 1, 1, Guid.NewGuid().ToString("D"), Guid.NewGuid());

        public Result<string> Encode(
            CovenantEnvelopePurpose purpose,
            ReadOnlySpan<byte> payload,
            TimeSpan lifetime,
            DateTimeOffset? issuedAtUtc = null)
        {

            string token = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());

            // Honoured, not ignored: a stand-in that stamped its own clock would let the body and the
            // header disagree, and the suite would rediscover that as a flake rather than a bug.
            DateTimeOffset now = issuedAtUtc ?? DateTimeOffset.UtcNow;

            _issued[token] = new CovenantEnvelopeBody(
                purpose,
                1,
                1,
                (ulong)_issued.Count + 1,
                DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds()),
                DateTimeOffset.FromUnixTimeMilliseconds((now + lifetime).ToUnixTimeMilliseconds()),
                payload.ToArray());

            return Result<string>.Success(token);

        }

        public Result<CovenantEnvelopeBody> Decode(CovenantEnvelopePurpose expectedPurpose, string? token) =>
            token is not null && _issued.TryGetValue(token, out CovenantEnvelopeBody? body)
                && body.Purpose == expectedPurpose
                ? Result<CovenantEnvelopeBody>.Success(body)
                : Result<CovenantEnvelopeBody>.Failure(new Error(
                    ErrorCodes.Covenant.ForbiddenAuthority,
                    "This Covenant token is not valid for this purpose."));

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

}
