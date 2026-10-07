using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Events;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Memory;
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
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Intelligence;
using RetroDownfall.Arcanum.Tests.Support;

using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Tests.Api.Tower;

/// <summary>
/// The Covenant erase routes, prepare then apply, driven through the mapped HTTP surface against a host
/// whose entries were written through the Covenant set routes.
/// </summary>
/// <remarks>
/// <para>Every target comes from <c>POST /api/memory/covenant/detail</c>, which is where the CLI takes
/// it from, and every curation change from the curation routes. A later agent proposal is staged by a
/// real turn: the production turn path, the real Covenant dispatch gate over the host's own gate and
/// store, and the real in-process MCP transport.</para>
///
/// <para>Every test that erases ends by asking the database whether any Annals claim outlived its
/// row.</para>
/// </remarks>
[Collection("ApiHost")]
public sealed class CovenantErasureEndpointTests
{
    private const string Key = "erasure.endpoint";

    private const string OperatorManaged = "This Covenant key is managed by the operator in this scope.";

    private const string ModelName = "covenant-erasure-test-model";

    private const string AssistantAnswer = "The answer the operator asked for.";

    private static CancellationToken Token => CancellationToken.None;

    /// <summary>Polls until the condition holds, failing if the awaited drain never starts.</summary>
    private static async Task WaitForDrainAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 1000; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10, Token);
        }

        Assert.Fail("The awaited drain never started.");
    }

    /// <summary>
    /// The drain is real: the erase waits for the Campaign turn that holds the entry's scope, and once it
    /// has gone a later turn that proposes the same key in that Campaign is refused at staging with the
    /// operator-managed answer, and stages nothing.
    /// </summary>
    [SkippableFact]
    public async Task An_erase_waits_for_an_in_flight_turn_and_a_later_agent_proposal_is_refused_at_staging()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory host = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore(), covenant: true);

        (HttpClient client, MemoryErasureRouteDriver driver) = Connect(host);

        Guid campaign = await RegisterCampaignAsync(host, client, "drain");

        _ = await driver.SetCovenantAsync(CovenantScope.Campaign, campaign, Key, "Campaign text.");

        CovenantDetailDto detail = await DetailAsync(driver, CovenantScope.Campaign, campaign, Key);

        ICovenantOperationGate gate = host.Services.GetRequiredService<ICovenantOperationGate>();

        CovenantTurnLease turn = (await gate.AcquireTurnAsync(CampaignContext(campaign), Token)).Value;

        CovenantErasePrepareRequest prepare = Prepare(CovenantScope.Campaign, campaign, detail);

        MemoryErasurePreflightDto preflight = await PrepareOkAsync(driver, prepare);

        Assert.Contains(MemoryErasureNote.CovenantDrainsInFlightTurns, preflight.Notes);

        Task<HttpResponseMessage> erase = client.PostAsync(
            "/api/memory/covenant/erase",
            JsonContent.Create(Apply(prepare, preflight.PreflightToken), ArcanumJsonContext.Default.CovenantEraseRequest));

        // The gate revokes a covered turn the moment the erase starts draining it, so the turn's own
        // revocation is the proof that the drain began; only then is "not completed" a statement about it.
        await WaitForDrainAsync(() => turn.Revocation.IsCancellationRequested);

        // Draining the covered turn.
        Assert.False(erase.IsCompleted);

        await turn.DisposeAsync();

        using (HttpResponseMessage erased = await erase)
        {
            Assert.Equal(HttpStatusCode.OK, erased.StatusCode);
        }

        CovenantDetailDto after = await DetailAsync(driver, CovenantScope.Campaign, campaign, Key);

        Assert.Null(after.EntryId);

        Assert.Null(after.Confirmed);

        Assert.Null(after.Proposed);

        AgentTurnOutcome proposed = await ProposeInRealTurnAsync(host, campaign, Key, "The agent proposes the erased key again.");

        Assert.True(proposed.SawStagingCapability);

        CovenantMutationFailureResultWire refusal = Assert.IsType<CovenantMutationFailureResultWire>(Assert.Single(proposed.Failures));

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, refusal.Code);

        Assert.Equal(OperatorManaged, refusal.Message);

        Assert.Equal(0, await ScalarAsync(host, $"SELECT count(*) FROM covenant_entries WHERE NormalizedKey = '{Key}';"));

        Assert.Equal(0, await ScalarAsync(host, "SELECT count(*) FROM covenant_versions WHERE LaneCode = 2;"));

        await AssertNoOrphanClaimsAsync(host);
    }

    /// <summary>
    /// A real erase fingerprints the key, and a real agent turn that then proposes the key in the same
    /// Campaign is refused at staging with the operator-managed text, publishes nothing for the key, and
    /// still saves the answer the operator asked for.
    /// </summary>
    [SkippableFact]
    public async Task An_erased_key_proposed_in_a_real_turn_is_refused_at_staging_and_the_answer_is_still_saved()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory host = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore(), covenant: true);

        (HttpClient client, MemoryErasureRouteDriver driver) = Connect(host);

        Guid campaign = await RegisterCampaignAsync(host, client, "agent-turn");

        _ = await driver.SetCovenantAsync(CovenantScope.Campaign, campaign, Key, "Campaign text.");

        MemoryErasureRoundTrip<CovenantEraseRequest> erased = await driver.EraseCovenantAsync(CovenantScope.Campaign, campaign, Key);

        Assert.False(erased.Result.Replayed);

        Assert.Equal(1, await MemoryErasureRouteDriver.FingerprintCountAsync(host, MemoryReviewStore.Covenant));

        AgentTurnOutcome proposed = await ProposeInRealTurnAsync(host, campaign, Key, "The agent proposes the erased key again.");

        Assert.True(proposed.Turn.IsSuccess, proposed.Turn.IsFailure ? $"{proposed.Turn.Error.Code}: {proposed.Turn.Error.Message}" : null);

        Assert.True(proposed.SawStagingCapability);

        CovenantMutationFailureResultWire refusal = Assert.IsType<CovenantMutationFailureResultWire>(Assert.Single(proposed.Failures));

        Assert.Equal(OperatorManaged, refusal.Message);

        Assert.Equal(0, await ScalarAsync(host, $"SELECT count(*) FROM covenant_entries WHERE NormalizedKey = '{Key}';"));

        Assert.Equal(0, await ScalarAsync(host, $"SELECT count(*) FROM covenant_heads WHERE NormalizedKey = '{Key}';"));

        Assert.Equal(AssistantAnswer, await LastAssistantContentAsync(host, proposed.SessionId));

        await AssertNoOrphanClaimsAsync(host);
    }

    /// <summary>
    /// A real turn whose proposal publishes at turn commit republishes the canonical position that commit
    /// advanced. It sits here for this class's real-turn harness; the host leaves its background maintenance
    /// pass out so nothing else publishes between the commit and the assertion.
    /// </summary>
    [SkippableFact]
    public async Task A_turn_that_publishes_a_proposal_republishes_the_canonical_position_its_commit_advanced()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory host = CovenantAvailabilityRepublicationTests.Host(new InMemoryOsCredentialStore());

        (HttpClient client, _) = Connect(host);

        Guid campaign = await RegisterCampaignAsync(host, client, "republication");

        long canonical = await ScalarAsync(host, "SELECT CanonicalSearchSequence FROM covenant_state WHERE StateKey = 1;");

        AgentTurnOutcome proposed = await ProposeInRealTurnAsync(host, campaign, Key, "The agent proposes a fresh key.");

        Assert.True(proposed.Turn.IsSuccess, proposed.Turn.IsFailure ? $"{proposed.Turn.Error.Code}: {proposed.Turn.Error.Message}" : null);

        Assert.Null(Assert.Single(proposed.Failures));

        Assert.Equal(1, await ScalarAsync(host, $"SELECT count(*) FROM covenant_heads WHERE NormalizedKey = '{Key}' AND LaneCode = 2;"));

        CovenantAvailabilitySnapshot live = await CovenantAvailabilityRepublicationTests.AssertRepublishedAsync(
            host,
            CovenantHealthTransition.CanonicalMutation);

        Assert.Equal(canonical + 1, live.CanonicalSequence);

        Assert.Equal(CovenantFtsSynchronizationState.Dirty, live.FtsSynchronization);
    }

    [SkippableFact]
    public async Task Prepare_holds_its_installation_read_lease_through_the_protected_response()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        RecordingReadLease lease = new();

        FakePreparer preparer = new(lease);

        await using ArcanumWebApplicationFactory host = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore(), covenant: true);

        host.ServiceOverrides += services =>
        {
            services.RemoveAll<ICovenantEntryErasurePreparer>();

            services.AddScoped<ICovenantEntryErasurePreparer>(_ => preparer);
        };

        (_, MemoryErasureRouteDriver driver) = Connect(host);

        CovenantErasePrepareRequest prepare = new(
            CovenantScope.Global,
            null,
            Key,
            Guid.NewGuid(),
            new CovenantEraseHeadExpectation(Guid.NewGuid(), 1),
            null,
            Guid.NewGuid());

        using HttpResponseMessage response = await driver.PostAsync(
            "/api/memory/covenant/erase/prepare",
            prepare,
            ArcanumJsonContext.Default.CovenantErasePrepareRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        AssertProtectedTuple(response);

        MemoryErasurePreflightDto preflight = await MemoryErasureRouteDriver.ReadDataAsync(
            response,
            ArcanumJsonContext.Default.ApiResponseMemoryErasurePreflightDto);

        Assert.Equal(prepare.MutationId, preflight.MutationId);

        Assert.Equal(1, preparer.Calls);

        Assert.Equal(1, lease.Revalidations);

        Assert.Equal(1, lease.Disposals);
    }

    [SkippableFact]
    public async Task Apply_holds_no_lease_after_the_response_and_emits_the_protected_tuple_on_success_and_refusal()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory host = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore(), covenant: true);

        (_, MemoryErasureRouteDriver driver) = Connect(host);

        ICovenantOperationGate gate = host.Services.GetRequiredService<ICovenantOperationGate>();

        _ = await driver.SetCovenantAsync(CovenantScope.Global, null, Key, "Erased first.");

        _ = await driver.SetCovenantAsync(CovenantScope.Global, null, "erasure.moved", "Moved before its erase.");

        CovenantErasePrepareRequest first = Prepare(CovenantScope.Global, null, await DetailAsync(driver, CovenantScope.Global, null, Key));

        MemoryErasurePreflightDto firstPreflight = await PrepareOkAsync(driver, first);

        using (HttpResponseMessage applied = await driver.PostAsync(
            "/api/memory/covenant/erase",
            Apply(first, firstPreflight.PreflightToken),
            ArcanumJsonContext.Default.CovenantEraseRequest))
        {
            Assert.Equal(HttpStatusCode.OK, applied.StatusCode);

            AssertProtectedTuple(applied);
        }

        await using (CovenantInstallationReadLease open = (await gate.AcquireInstallationReadAsync(Token)).Value)
        {
            Assert.Equal(CovenantLeaseKind.InstallationRead, open.Snapshot.Kind);
        }

        CovenantErasePrepareRequest second = Prepare(
            CovenantScope.Global,
            null,
            await DetailAsync(driver, CovenantScope.Global, null, "erasure.moved"));

        MemoryErasurePreflightDto secondPreflight = await PrepareOkAsync(driver, second);

        _ = await driver.SetCovenantAsync(CovenantScope.Global, null, "erasure.moved", "Moved after its erase was prepared.");

        using (HttpResponseMessage refused = await driver.PostAsync(
            "/api/memory/covenant/erase",
            Apply(second, secondPreflight.PreflightToken),
            ArcanumJsonContext.Default.CovenantEraseRequest))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

            Assert.Equal(ErrorCodes.Covenant.RevisionConflict, await MemoryErasureRouteDriver.ReadErrorCodeAsync(refused));

            AssertProtectedTuple(refused);
        }

        await using (CovenantInstallationReadLease reopened = (await gate.AcquireInstallationReadAsync(Token)).Value)
        {
            Assert.Equal(CovenantLeaseKind.InstallationRead, reopened.Snapshot.Kind);
        }

        await AssertNoOrphanClaimsAsync(host);
    }

    /// <summary>
    /// A Campaign entry whose key also has a live Global entry: the plan counts its two Confirmed
    /// versions, its pending outbox delta, its one search document and its pin, keeps the key, and says
    /// the Global entry will apply in that Campaign once its own is gone.
    /// </summary>
    [SkippableFact]
    public async Task The_preflight_reports_versions_curation_outbox_and_scope_facts()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory host = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore(), covenant: true);

        (HttpClient client, MemoryErasureRouteDriver driver) = Connect(host);

        Guid campaign = await RegisterCampaignAsync(host, client, "facts");

        _ = await driver.SetCovenantAsync(CovenantScope.Global, null, Key, "Global text.");

        _ = await driver.SetCovenantAsync(CovenantScope.Campaign, campaign, Key, "First Campaign text.");

        // The search projection catches up exactly as the maintenance pass would, so the second write
        // below is the one delta still pending when the erase is measured.
        await SynchronizeSearchAsync(host);

        _ = await driver.SetCovenantAsync(CovenantScope.Campaign, campaign, Key, "Second Campaign text.");

        await CurateAsync(driver, CovenantCurationKind.Pin, CovenantScope.Campaign, campaign, Key);

        CovenantDetailDto pinned = await DetailAsync(driver, CovenantScope.Campaign, campaign, Key);

        Assert.Equal(new CovenantCurationStateDto(true, false, 1), pinned.ConfirmedCuration);

        CovenantErasePrepareRequest prepare = Prepare(CovenantScope.Campaign, campaign, pinned);

        MemoryErasurePreflightDto preflight = await PrepareOkAsync(driver, prepare);

        Assert.Equal(MemoryReviewStore.Covenant, preflight.Store);

        Assert.Equal(prepare.MutationId, preflight.MutationId);

        Assert.Equal(
            new CovenantErasurePlanFacts(
                ConfirmedVersions: 2,
                ProposedVersions: 0,
                ProvenanceLeaves: 0,
                MutationReceipts: 2,
                CurationRows: 3,
                OutboxRows: 1,
                SearchDocuments: 1,
                ReclaimsKey: false,
                RetainsCampaignMask: false,
                GlobalConfirmedResurfaces: true,
                IsPinned: true,
                AffectedCampaigns: 1),
            preflight.Plan.Covenant);

        Assert.Equal(1, preflight.Plan.ErasedItemCount);

        Assert.True(preflight.Plan.Pinned);

        Assert.Equal(0, preflight.Plan.LabelsToRemove);

        Assert.Equal(0, preflight.Plan.RetirementSuppressionsToRemove);

        Assert.Null(preflight.Plan.Lexicon);

        Assert.Equal(
            [
                MemoryExternalEvidence.NotRecorded,
                MemoryExternalEvidence.NotRecorded,
                MemoryExternalEvidence.NotApplicable,
                MemoryExternalEvidence.NotRecorded,
                MemoryExternalEvidence.NotRecorded,
            ],
            preflight.External.Channels.Select(static channel => channel.Evidence));

        Assert.Equal([MemoryErasureNote.OtherScopesUnaffected, MemoryErasureNote.CovenantDrainsInFlightTurns], preflight.Notes);

        Assert.Equal(TimeSpan.FromMinutes(5), preflight.ExpiresAtUtc - preflight.IssuedAtUtc);

        MemoryErasureResultDto result = await driver.ApplyCovenantAsync(Apply(prepare, preflight.PreflightToken));

        Assert.Equal(preflight.EffectDigest, result.EffectDigest);

        Assert.Equal(preflight.Plan.RowsToRemove, result.Local.RemovedRowCount);

        Assert.Equal([MemoryErasureNote.OtherScopesUnaffected, MemoryErasureNote.CovenantDrainsInFlightTurns], result.Notes);

        Assert.NotNull((await DetailAsync(driver, CovenantScope.Global, null, Key)).EntryId);

        // The erased subject's curation reads exactly as a key never written in that scope reads, so the
        // detail route's curation state says nothing about an erasure having happened.
        CovenantDetailDto erased = await DetailAsync(driver, CovenantScope.Campaign, campaign, Key);

        CovenantDetailDto neverWritten = await DetailAsync(driver, CovenantScope.Campaign, campaign, "erasure.never-written");

        Assert.Null(erased.EntryId);

        Assert.Equal(CovenantCurationStateDto.None, erased.ConfirmedCuration);

        Assert.Equal(neverWritten.ConfirmedCuration, erased.ConfirmedCuration);

        Assert.Equal(neverWritten.ProposedCuration, erased.ProposedCuration);

        await AssertNoOrphanClaimsAsync(host);
    }

    [SkippableFact]
    public async Task A_head_moved_after_prepare_is_a_revision_conflict()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory host = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore(), covenant: true);

        (_, MemoryErasureRouteDriver driver) = Connect(host);

        _ = await driver.SetCovenantAsync(CovenantScope.Global, null, Key, "First text.");

        CovenantErasePrepareRequest prepare = Prepare(CovenantScope.Global, null, await DetailAsync(driver, CovenantScope.Global, null, Key));

        MemoryErasurePreflightDto preflight = await PrepareOkAsync(driver, prepare);

        _ = await driver.SetCovenantAsync(CovenantScope.Global, null, Key, "Second text.");

        using HttpResponseMessage refused = await driver.PostAsync(
            "/api/memory/covenant/erase",
            Apply(prepare, preflight.PreflightToken),
            ArcanumJsonContext.Default.CovenantEraseRequest);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        Assert.Equal(ErrorCodes.Covenant.RevisionConflict, await MemoryErasureRouteDriver.ReadErrorCodeAsync(refused));

        Assert.Equal(0, await ReceiptsAsync(host, prepare.MutationId));

        Assert.NotNull((await DetailAsync(driver, CovenantScope.Global, null, Key)).EntryId);

        await AssertNoOrphanClaimsAsync(host);
    }

    [SkippableFact]
    public async Task A_global_entry_with_a_proposed_expectation_is_an_invalid_scope()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory host = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore(), covenant: true);

        (_, MemoryErasureRouteDriver driver) = Connect(host);

        _ = await driver.SetCovenantAsync(CovenantScope.Global, null, Key, "Global text.");

        CovenantErasePrepareRequest prepare = Prepare(CovenantScope.Global, null, await DetailAsync(driver, CovenantScope.Global, null, Key)) with
        {
            Proposed = new CovenantEraseHeadExpectation(Guid.NewGuid(), 1),
        };

        using HttpResponseMessage refused = await driver.PostAsync(
            "/api/memory/covenant/erase/prepare",
            prepare,
            ArcanumJsonContext.Default.CovenantErasePrepareRequest);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        Assert.Equal(ErrorCodes.Covenant.InvalidScope, await MemoryErasureRouteDriver.ReadErrorCodeAsync(refused));

        AssertProtectedTuple(refused);

        await AssertNoOrphanClaimsAsync(host);
    }

    /// <summary>
    /// Erasing a key's only entry reclaims the key, which moves the installation's key-reclamation
    /// epoch, so a set prepared on any other key before it is refused at commit as stale.
    /// </summary>
    [SkippableFact]
    public async Task A_reclaiming_erase_makes_an_outstanding_set_preflight_stale()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory host = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore(), covenant: true);

        (HttpClient client, MemoryErasureRouteDriver driver) = Connect(host);

        Guid campaign = await RegisterCampaignAsync(host, client, "reclaim");

        _ = await driver.SetCovenantAsync(CovenantScope.Campaign, campaign, Key, "The key's only entry.");

        CovenantSetPrepareRequest outstanding = new(CovenantScope.Global, null, "erasure.other", "Another key.", 0, Guid.NewGuid(), Reactivate: false);

        CovenantMutationPreflightDto setPreflight;

        using (HttpResponseMessage prepared = await driver.PostAsync(
            "/api/memory/covenant/set/prepare",
            outstanding,
            ArcanumJsonContext.Default.CovenantSetPrepareRequest))
        {
            setPreflight = await MemoryErasureRouteDriver.ReadDataAsync(prepared, ArcanumJsonContext.Default.ApiResponseCovenantMutationPreflightDto);
        }

        MemoryErasureRoundTrip<CovenantEraseRequest> erased = await driver.EraseCovenantAsync(CovenantScope.Campaign, campaign, Key);

        Assert.True(erased.Preflight.Plan.Covenant!.ReclaimsKey);

        using HttpResponseMessage committed = await client.SendAsync(new HttpRequestMessage(HttpMethod.Put, "/api/memory/covenant")
        {
            Content = JsonContent.Create(
                new CovenantSetRequest(
                    outstanding.Scope,
                    outstanding.CampaignId,
                    outstanding.Key,
                    outstanding.Content,
                    outstanding.ExpectedRevision,
                    outstanding.MutationId,
                    outstanding.Reactivate,
                    setPreflight.PreflightToken),
                ArcanumJsonContext.Default.CovenantSetRequest),
        });

        Assert.Equal(HttpStatusCode.Conflict, committed.StatusCode);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, await MemoryErasureRouteDriver.ReadErrorCodeAsync(committed));

        await AssertNoOrphanClaimsAsync(host);
    }

    /// <summary>
    /// A repeated apply answers from the receipt with the same effect, whatever token it carries. The
    /// removed row count is the plan's measured count, review events removed by the versions' cascade
    /// included.
    /// </summary>
    [SkippableFact]
    public async Task A_repeated_apply_replays()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory host = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore(), covenant: true);

        (_, MemoryErasureRouteDriver driver) = Connect(host);

        _ = await driver.SetCovenantAsync(CovenantScope.Global, null, Key, "First text.");

        _ = await driver.SetCovenantAsync(CovenantScope.Global, null, Key, "Second text.");

        Assert.Equal(2, await ScalarAsync(host, "SELECT count(*) FROM covenant_review_events;"));

        MemoryErasureRoundTrip<CovenantEraseRequest> roundTrip = await driver.EraseCovenantAsync(CovenantScope.Global, null, Key);

        Assert.Equal(roundTrip.Preflight.Plan.RowsToRemove, roundTrip.Result.Local.RemovedRowCount);

        Assert.Equal(0, await ScalarAsync(host, "SELECT count(*) FROM covenant_review_events;"));

        MemoryErasureResultDto replayed = await driver.ApplyCovenantAsync(roundTrip.Apply with { PreflightToken = "x" });

        Assert.True(replayed.Replayed);

        Assert.Equal(roundTrip.Result.EffectDigest, replayed.EffectDigest);

        Assert.Equal(roundTrip.Result.Local.RemovedRowCount, replayed.Local.RemovedRowCount);

        Assert.Equal(1, await ReceiptsAsync(host, roundTrip.Apply.MutationId));

        await AssertNoOrphanClaimsAsync(host);
    }

    /// <summary>
    /// A pin moves no head, no key epoch and no dataset generation, so only the effect recompute inside
    /// the erase transaction can see it.
    /// </summary>
    [SkippableFact]
    public async Task A_pin_recorded_between_prepare_and_apply_is_a_stale_plan()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory host = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore(), covenant: true);

        (HttpClient client, MemoryErasureRouteDriver driver) = Connect(host);

        Guid campaign = await RegisterCampaignAsync(host, client, "pin");

        _ = await driver.SetCovenantAsync(CovenantScope.Campaign, campaign, Key, "Campaign text.");

        CovenantErasePrepareRequest prepare = Prepare(CovenantScope.Campaign, campaign, await DetailAsync(driver, CovenantScope.Campaign, campaign, Key));

        MemoryErasurePreflightDto preflight = await PrepareOkAsync(driver, prepare);

        await CurateAsync(driver, CovenantCurationKind.Pin, CovenantScope.Campaign, campaign, Key);

        using HttpResponseMessage refused = await driver.PostAsync(
            "/api/memory/covenant/erase",
            Apply(prepare, preflight.PreflightToken),
            ArcanumJsonContext.Default.CovenantEraseRequest);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        Assert.Equal(ErrorCodes.MemoryErasure.StalePlan, await MemoryErasureRouteDriver.ReadErrorCodeAsync(refused));

        Assert.Equal(prepare.EntryId, (await DetailAsync(driver, CovenantScope.Campaign, campaign, Key)).EntryId);

        Assert.Equal(0, await ReceiptsAsync(host, prepare.MutationId));

        Assert.Equal(0, await MemoryErasureRouteDriver.FingerprintCountAsync(host, MemoryReviewStore.Covenant));

        await AssertNoOrphanClaimsAsync(host);
    }

    /// <summary>
    /// Two mutations prepared against one entry: the first erases it, and the second, already gone,
    /// takes no closure and is answered by the receipt that names it.
    /// </summary>
    [SkippableFact]
    public async Task A_second_mutation_prepared_before_the_first_applied_answers_410_at_apply()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory host = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore(), covenant: true);

        (_, MemoryErasureRouteDriver driver) = Connect(host);

        _ = await driver.SetCovenantAsync(CovenantScope.Global, null, Key, "Global text.");

        CovenantDetailDto detail = await DetailAsync(driver, CovenantScope.Global, null, Key);

        CovenantErasePrepareRequest first = Prepare(CovenantScope.Global, null, detail);

        CovenantErasePrepareRequest second = Prepare(CovenantScope.Global, null, detail);

        MemoryErasurePreflightDto firstPreflight = await PrepareOkAsync(driver, first);

        MemoryErasurePreflightDto secondPreflight = await PrepareOkAsync(driver, second);

        _ = await driver.ApplyCovenantAsync(Apply(first, firstPreflight.PreflightToken));

        using HttpResponseMessage gone = await driver.PostAsync(
            "/api/memory/covenant/erase",
            Apply(second, secondPreflight.PreflightToken),
            ArcanumJsonContext.Default.CovenantEraseRequest);

        Assert.Equal(HttpStatusCode.Gone, gone.StatusCode);

        Assert.Equal(ErrorCodes.MemoryErasure.SubjectErased, await MemoryErasureRouteDriver.ReadErrorCodeAsync(gone));

        AssertProtectedTuple(gone);

        Assert.Equal(0, await ReceiptsAsync(host, second.MutationId));

        await AssertNoOrphanClaimsAsync(host);
    }

    private static (HttpClient Client, MemoryErasureRouteDriver Driver) Connect(ArcanumWebApplicationFactory host)
    {
        HttpClient client = host.CreateAuthenticatedClient();

        return (client, new MemoryErasureRouteDriver(client));
    }

    private static async Task<Guid> RegisterCampaignAsync(ArcanumWebApplicationFactory host, HttpClient client, string name)
    {
        string path = Path.Combine(host.TempHome, $"covenant-erasure-{name}");

        Directory.CreateDirectory(path);

        using HttpResponseMessage registered = await client.PostAsync(
            "/api/campaigns",
            JsonContent.Create(
                new RegisterCampaignRequest($"Covenant erasure {name}", path, WorkspaceType.Campaign, null),
                ArcanumJsonContext.Default.RegisterCampaignRequest));

        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);

        return (await MemoryErasureRouteDriver.ReadDataAsync(registered, ArcanumJsonContext.Default.ApiResponseCampaignDto)).Id;
    }

    private static CanonicalCampaignContext CampaignContext(Guid campaignId) =>
        CanonicalCampaignContext.Create(
            SessionCampaignBinding.ForCampaign(campaignId),
            campaignAvailabilityGeneration: 1,
            pathIdentityPolicyVersion: 1,
            pathIdentityRevision: null,
            rootIdentityDigest: null);

    private static async Task<CovenantDetailDto> DetailAsync(
        MemoryErasureRouteDriver driver,
        CovenantScope scope,
        Guid? campaignId,
        string key)
    {
        using HttpResponseMessage shown = await driver.PostAsync(
            "/api/memory/covenant/detail",
            new CovenantDetailRequest(scope, campaignId, key),
            ArcanumJsonContext.Default.CovenantDetailRequest);

        Assert.Equal(HttpStatusCode.OK, shown.StatusCode);

        return await MemoryErasureRouteDriver.ReadDataAsync(shown, ArcanumJsonContext.Default.ApiResponseCovenantDetailDto);
    }

    private static CovenantErasePrepareRequest Prepare(CovenantScope scope, Guid? campaignId, CovenantDetailDto detail)
    {
        Assert.NotNull(detail.EntryId);

        return new CovenantErasePrepareRequest(
            scope,
            campaignId,
            detail.Key,
            detail.EntryId!.Value,
            detail.Confirmed is { } confirmed ? new(confirmed.VersionId, confirmed.LaneRevision) : null,
            detail.Proposed is { } proposed ? new(proposed.VersionId, proposed.LaneRevision) : null,
            Guid.NewGuid());
    }

    private static CovenantEraseRequest Apply(CovenantErasePrepareRequest prepare, string token) =>
        new(prepare.Scope, prepare.CampaignId, prepare.Key, prepare.EntryId, prepare.Confirmed, prepare.Proposed, prepare.MutationId, token);

    private static async Task<MemoryErasurePreflightDto> PrepareOkAsync(MemoryErasureRouteDriver driver, CovenantErasePrepareRequest prepare)
    {
        using HttpResponseMessage prepared = await driver.PostAsync(
            "/api/memory/covenant/erase/prepare",
            prepare,
            ArcanumJsonContext.Default.CovenantErasePrepareRequest);

        Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);

        AssertProtectedTuple(prepared);

        return await MemoryErasureRouteDriver.ReadDataAsync(prepared, ArcanumJsonContext.Default.ApiResponseMemoryErasurePreflightDto);
    }

    /// <summary>Prepares and commits one curation change through the curation routes.</summary>
    private static async Task CurateAsync(
        MemoryErasureRouteDriver driver,
        CovenantCurationKind kind,
        CovenantScope scope,
        Guid? campaignId,
        string key)
    {
        CovenantCurationPrepareRequest prepare = new(kind, scope, campaignId, key, CovenantLane.Confirmed, 0, Guid.NewGuid());

        CovenantCurationPreflightDto preflight;

        using (HttpResponseMessage prepared = await driver.PostAsync(
            "/api/memory/covenant/curate/prepare",
            prepare,
            ArcanumJsonContext.Default.CovenantCurationPrepareRequest))
        {
            preflight = await MemoryErasureRouteDriver.ReadDataAsync(prepared, ArcanumJsonContext.Default.ApiResponseCovenantCurationPreflightDto);
        }

        using HttpResponseMessage curated = await driver.PostAsync(
            "/api/memory/covenant/curate",
            new CovenantCurationRequest(
                prepare.Kind,
                prepare.Scope,
                prepare.CampaignId,
                prepare.Key,
                prepare.Lane,
                prepare.ExpectedRevision,
                prepare.MutationId,
                preflight.PreflightToken),
            ArcanumJsonContext.Default.CovenantCurationRequest);

        Assert.Equal(HttpStatusCode.OK, curated.StatusCode);
    }

    /// <summary>Runs one search-outbox synchronization the way the maintenance pass runs it.</summary>
    private static async Task SynchronizeSearchAsync(ArcanumWebApplicationFactory host)
    {
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        if (db.Database.GetDbConnection().State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(Token);
        }

        Result<CovenantOutboxSyncOutcome> synchronized = await scope.ServiceProvider
            .GetRequiredService<CovenantSearchOutboxCoordinator>()
            .SynchronizeAsync(CovenantSearchOutboxWorker.DefaultBatchRows, Token);

        Assert.True(synchronized.IsSuccess, synchronized.IsFailure ? synchronized.Error.Message : null);
    }

    /// <summary>
    /// One agent turn on the production turn path, in a Session bound to the Campaign, whose scripted
    /// provider proposes one key through the real staging tool before answering.
    /// </summary>
    private static async Task<AgentTurnOutcome> ProposeInRealTurnAsync(
        ArcanumWebApplicationFactory host,
        Guid campaignId,
        string key,
        string content)
    {
        CanonicalCampaignContext campaign = CampaignContext(campaignId);

        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();

        Result<Guid> session = await scope.ServiceProvider
            .GetRequiredService<ISessionTurnBeginStore>()
            .CreateBoundSessionAsync(campaign, "Covenant erasure turn", Token);

        Assert.True(session.IsSuccess, session.IsFailure ? session.Error.Message : null);

        ICovenantAvailability availability = host.Services.GetRequiredService<ICovenantAvailability>();

        ICovenantAuthoritySnapshotProvider authority = host.Services.GetRequiredService<ICovenantAuthoritySnapshotProvider>();

        CovenantToolCapabilityRegistry registry = new();

        await using CovenantToolCall toolCall = await CovenantToolCall.CreateAsync(registry, availability);

        StagingChatClient chat = new(toolCall, [(key, content)], AssistantAnswer);

        GrimoireRepository repository = (GrimoireRepository)scope.ServiceProvider.GetRequiredService<IGrimoireRepository>();

        CovenantDispatchGate dispatch = new(
            new CovenantContextProvider(
                availability,
                host.Services.GetRequiredService<ICovenantOperationGate>(),
                scope.ServiceProvider.GetRequiredService<ICovenantStore>(),
                new CovenantLinker()),
            new AcceptingDisclosureJournal(),
            scope.ServiceProvider.GetRequiredService<IArtifactSensitivityLedger>(),
            authority,
            TimeProvider.System,
            NullLogger<CovenantDispatchGate>.Instance);

        ProviderSettings provider = new()
        {
            Name = "provider-covenant-erasure",
            Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://example.test/v1",
            Models = [ModelName],
            ContextWindowLimit = 32_768,
        };

        WizardIntelligenceProvider wizard = WizardIntelligenceProviderFallbackTests.CreateCovenantStagingWizard(
            new SingleLeaseChatClientFactory(chat, provider, ModelName),
            dispatch,
            registry,
            repository,
            repository,
            provider);

        CovenantAuthoritySnapshot current = authority.Current!;

        ArcanumInvocationContext invocation = ArcanumInvocationContext.Create(
            ArcanumExecutionSurface.SessionBackedOperatorTurn,
            campaign,
            InvocationAttendance.Attended,
            CovenantContextPolicy.Default,
            ToolPolicy.AllTools,
            CovenantReadAuthorityEpoch.CreateForTests(
                Guid.Parse(current.InstallationIdentity),
                current.RuntimeAuthorityGeneration,
                current.AuthorityEpoch)).Value;

        Result<PromptTurnResult> turn = await wizard.ExecutePromptAsync(
            new PingRequest(
                Prompt: "Remember how I like my answers.",
                Model: ModelName,
                WorkingDirectory: string.Empty,
                SessionId: session.Value,
                DisableMcpTools: true,
                SkipSpellRouting: true),
            invocation,
            Token);

        return new AgentTurnOutcome(session.Value, turn, chat.Failures, chat.SawStagingCapability);
    }

    private static async Task<long> ScalarAsync(ArcanumWebApplicationFactory host, string sql) =>
        Convert.ToInt64(await ScalarObjectAsync(host, sql), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The Session's latest assistant entry, as the turn saved it.</summary>
    private static async Task<string?> LastAssistantContentAsync(ArcanumWebApplicationFactory host, Guid sessionId)
    {
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<ArcanumDbContext>().Entries
            .AsNoTracking()
            .Where(entry => entry.SessionId == sessionId && entry.Role == MessageRole.Assistant)
            .OrderByDescending(static entry => entry.Sequence)
            .Select(static entry => entry.Content)
            .FirstOrDefaultAsync(Token);
    }

    private static async Task<object?> ScalarObjectAsync(ArcanumWebApplicationFactory host, string sql)
    {
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();

        SqliteConnection connection = await scope.ServiceProvider
            .GetRequiredService<ICovenantConnectionSource>()
            .GetOpenConnectionAsync(Token);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return await command.ExecuteScalarAsync(Token);
    }

    private static Task<long> ReceiptsAsync(ArcanumWebApplicationFactory host, Guid mutationId) =>
        ScalarAsync(host, $"SELECT count(*) FROM memory_erasure_receipts WHERE MutationId = '{mutationId.ToString("D").ToUpperInvariant()}';");

    private static async Task AssertNoOrphanClaimsAsync(ArcanumWebApplicationFactory host)
    {
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();

        SqliteConnection connection = await scope.ServiceProvider
            .GetRequiredService<ICovenantConnectionSource>()
            .GetOpenConnectionAsync(Token);

        await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(connection, Token);
    }

    /// <summary>The tuple every erasure response carries whatever its status (API §8.29, §8.35).</summary>
    private static void AssertProtectedTuple(HttpResponseMessage response)
    {
        Assert.Equal("no-store, private", response.Headers.CacheControl!.ToString());

        Assert.Equal("no-cache", Assert.Single(response.Headers.GetValues("Pragma")));

        Assert.Equal("0", Assert.Single(response.Content.Headers.GetValues("Expires")));

        Assert.Null(response.Headers.ETag);

        Assert.False(response.Content.Headers.Contains("Last-Modified"));
    }

    /// <summary>One agent turn's outcome, and what its scripted tool round observed while inside it.</summary>
    private sealed record AgentTurnOutcome(
        Guid SessionId,
        Result<PromptTurnResult> Turn,
        IReadOnlyList<CovenantMutationFailureResultWire?> Failures,
        bool SawStagingCapability);

    /// <summary>A preparer that hands the route a fixed preflight and a lease that counts what is done to it.</summary>
    private sealed class FakePreparer(RecordingReadLease lease) : ICovenantEntryErasurePreparer
    {
        private int _calls;

        internal int Calls => Volatile.Read(ref _calls);

        public Task<Result<CovenantEntryErasurePrepared>> PrepareHeldAsync(
            CovenantErasePrepareRequest request,
            OperatorAuthorityContext authority,
            CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _calls);

            DateTimeOffset issued = DateTimeOffset.UtcNow;

            MemoryErasurePreflightDto preflight = new(
                MemoryReviewStore.Covenant,
                request.MutationId,
                new string('a', 64),
                new string('b', 64),
                new MemoryErasurePlanDto(
                    1,
                    1,
                    0,
                    0,
                    false,
                    Lexicon: null,
                    new CovenantErasurePlanFacts(1, 0, 0, 1, 0, 0, 0, false, false, false, false, 0)),
                new MemoryErasureExternalExposureDto(MemoryExternalRevocation.NotPerformed, []),
                [],
                [MemoryErasureNote.OtherScopesUnaffected],
                issued,
                issued + TimeSpan.FromMinutes(5),
                "token");

            return Task.FromResult(Result<CovenantEntryErasurePrepared>.Success(new CovenantEntryErasurePrepared(preflight, lease)));
        }
    }

    /// <summary>A read lease that counts its revalidations and disposals.</summary>
    private sealed class RecordingReadLease : ICovenantSnapshotReadLease
    {
        private int _revalidations;

        private int _disposals;

        internal int Revalidations => Volatile.Read(ref _revalidations);

        internal int Disposals => Volatile.Read(ref _disposals);

        public CovenantOperationLeaseSnapshot Snapshot { get; } = new(
            Guid.NewGuid(),
            RuntimeAuthorityGeneration: 1,
            CovenantLeaseKind.InstallationRead,
            CovenantLeaseCoverage.Installation,
            Scope: null,
            DatasetGeneration: Guid.NewGuid(),
            CapabilityGeneration: 1,
            AuthorityEpoch: 1,
            CanonicalSequence: 0,
            CampaignAvailabilityGeneration: null,
            CampaignPathRevision: null,
            AcceleratorEpoch: null,
            AppliedCampaignDeletionSequence: null,
            RecoveryOwner: null,
            CleanupOnlyHistoricalCampaign: false);

        public CancellationToken Revocation => CancellationToken.None;

        public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _revalidations);

            return ValueTask.FromResult(Result.Success());
        }

        public ValueTask DisposeAsync()
        {
            _ = Interlocked.Increment(ref _disposals);

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// A scripted provider that spends the turn's staging capability the way a tool round does, then
    /// answers.
    /// </summary>
    /// <remarks>
    /// It runs inside the turn's async flow, so it sees the staging ambient the turn loop pushes around
    /// the provider call, and hands it to the production binder before calling the tool over the wire.
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

    /// <summary>One live in-process MCP server, driven over its real transport.</summary>
    private sealed class CovenantToolCall : IAsyncDisposable
    {
        private readonly InProcessMcpTransport _transport;

        private readonly Task _serverTask;

        private readonly CancellationTokenSource _lifetime;

        private readonly string _connectionKey;

        private int _nextId;

        private CovenantToolCall(InProcessMcpTransport transport, Task serverTask, CancellationTokenSource lifetime, string connectionKey)
        {
            _transport = transport;

            _serverTask = serverTask;

            _lifetime = lifetime;

            _connectionKey = connectionKey;
        }

        public static async Task<CovenantToolCall> CreateAsync(CovenantToolCapabilityRegistry registry, ICovenantAvailability availability)
        {
            ServiceCollection services = [];

            services.AddSingleton<ICovenantCompiler, CovenantCompiler>();

            services.AddSingleton(registry);

            services.AddSingleton(availability);

            services.AddSingleton<IOptionsMonitor<ArcanumSettings>>(new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

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
                logger: NullLogger<ArcanumInternalToolServer>.Instance,
                allowHostProcessTools: true);

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

            // The production binding site: it reads the staging ambient this turn published and mints the
            // single-use capability from it, or mints nothing at all.
            JsonRpcRequest bound = SessionAttachmentAmbientSend.ApplyAmbientBinding(_connectionKey, request);

            await _transport.WriteRequestAsync(bound).ConfigureAwait(false);

            McpInboundEnvelope envelope = await _transport.InboundReader.ReadAsync().ConfigureAwait(false);

            Assert.Equal(McpInboundKind.Response, envelope.Kind);

            return JsonSerializer.Deserialize(envelope.Response!.Result!.Value, McpJsonSerializerContext.Default.McpToolsCallResultWire)!;
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

    private sealed class SingleLeaseChatClientFactory(IChatClient client, ProviderSettings provider, string model) : IChatClientFactory
    {
        public Task<ChatClientLease> ResolveClientAsync(string? targetModel, CancellationToken cancellationToken) =>
            Task.FromResult(new ChatClientLease(client, provider, model, ownedHttpClient: null));

        public Task<ChatClientLease> ResolveClientAsync(ProviderSettings candidate, string resolvedModel, CancellationToken cancellationToken) =>
            ResolveClientAsync(resolvedModel, cancellationToken);
    }

    /// <summary>A journal that accepts every disclosure it is handed; disclosure accounting has its own suite.</summary>
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

    private sealed class SilentEventBus : IEventBus
    {
        public void Publish<T>(T @event)
            where T : notnull
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
