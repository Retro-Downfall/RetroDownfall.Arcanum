using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Tower;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api.Tower;

/// <summary>
/// Spec §5.6: an operator write that makes an erased identity live again in its own scope deletes the
/// fingerprint in its own transaction and says so, and nothing else does.
/// </summary>
/// <remarks>
/// <para>An operator Covenant <c>set</c> is the set prepare route then the commit route; a Saga
/// correction is the correct route; a bulk correction is the Saga review routes. Every fingerprint comes
/// from an actual erase, and every precondition from a production writer.</para>
///
/// <para>The release reads only the process's latch. When fingerprints exist that the latched key cannot
/// check, because there is no key or it is not the one they were recorded under, the operator write
/// still succeeds and reports <see langword="null"/> rather than guessing.</para>
/// </remarks>
[Collection("ApiHost")]
public sealed class MemoryErasureOperatorRecreationTests
{
    private const string Key = "preference.vault";

    private const string Content = "Keep the vault key offline.";

    private const string Service = ArcanumCredentialIdentity.Service;

    private const string Account = ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount;

    /// <summary>The live half of spec §19.2 #6; a backup restored after it is Task 24's half.</summary>
    [SkippableFact]
    public async Task Operator_set_of_an_erased_key_discloses_then_releases_the_fingerprint()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(credentials, covenant: true);

        MemoryErasureRouteDriver driver = new(factory.CreateClient());

        Guid campaign = await RegisterCampaignAsync(factory, "c");

        await EraseCovenantKeyAsync(driver, CovenantScope.Campaign, campaign, Key);

        CovenantSetPrepareRequest prepare = new(CovenantScope.Campaign, campaign, Key, Content, 0, Guid.NewGuid(), Reactivate: false);

        CovenantMutationPreflightDto preflight = await PrepareSetAsync(driver, prepare);

        Assert.True(preflight.Effect.ReleasesErasureFingerprint);

        // Disclosing it is not doing it: the preflight is a read.
        Assert.Equal(1, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Covenant));

        CovenantMutationResultDto result = await CommitSetAsync(factory, prepare, preflight.PreflightToken);

        Assert.Equal(CovenantMutationOutcome.Applied, result.Outcome);

        Assert.True(result.ReleasedErasureFingerprint);

        Assert.Equal(0, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Covenant));
    }

    /// <summary>
    /// The release runs inside the mutation's own transaction, so a write the kernel refuses rolls the
    /// release back with it.
    /// </summary>
    [SkippableFact]
    public async Task A_set_refused_by_the_kernel_releases_nothing()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(credentials, covenant: true);

        MemoryErasureRouteDriver driver = new(factory.CreateClient());

        Guid campaign = await RegisterCampaignAsync(factory, "c");

        await EraseCovenantKeyAsync(driver, CovenantScope.Campaign, campaign, Key);

        CovenantSetPrepareRequest prepare = new(CovenantScope.Campaign, campaign, Key, Content, 5, Guid.NewGuid(), Reactivate: false);

        CovenantMutationPreflightDto preflight = await PrepareSetAsync(driver, prepare);

        using HttpResponseMessage refused = await PutSetAsync(factory, prepare, preflight.PreflightToken);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        Assert.Equal(ErrorCodes.Covenant.RevisionConflict, await MemoryErasureRouteDriver.ReadErrorCodeAsync(refused));

        Assert.Equal(1, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Covenant));
    }

    /// <summary>A replay answers from the mutation's receipt and released nothing this time.</summary>
    [SkippableFact]
    public async Task A_replayed_set_reports_no_release()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(credentials, covenant: true);

        MemoryErasureRouteDriver driver = new(factory.CreateClient());

        Guid campaign = await RegisterCampaignAsync(factory, "c");

        await EraseCovenantKeyAsync(driver, CovenantScope.Campaign, campaign, Key);

        CovenantSetPrepareRequest prepare = new(CovenantScope.Campaign, campaign, Key, Content, 0, Guid.NewGuid(), Reactivate: false);

        CovenantMutationPreflightDto preflight = await PrepareSetAsync(driver, prepare);

        CovenantMutationResultDto first = await CommitSetAsync(factory, prepare, preflight.PreflightToken);

        Assert.True(first.ReleasedErasureFingerprint);

        CovenantMutationResultDto replayed = await CommitSetAsync(factory, prepare, preflight.PreflightToken);

        Assert.True(replayed.Replayed);

        Assert.False(replayed.ReleasedErasureFingerprint);

        Assert.Equal(0, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Covenant));
    }

    /// <summary>
    /// A write that released a fingerprint, retried after the key was erased again, never lifts the new
    /// fingerprint: the erase removed the write's receipt and moved the key's epochs, so the retry is a
    /// stale write the kernel refuses, and the new erasure stands.
    /// </summary>
    [SkippableFact]
    public async Task A_set_retried_after_a_new_erase_releases_nothing()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(credentials, covenant: true);

        MemoryErasureRouteDriver driver = new(factory.CreateClient());

        Guid campaign = await RegisterCampaignAsync(factory, "c");

        await EraseCovenantKeyAsync(driver, CovenantScope.Campaign, campaign, Key);

        CovenantSetPrepareRequest prepare = new(CovenantScope.Campaign, campaign, Key, Content, 0, Guid.NewGuid(), Reactivate: false);

        CovenantMutationPreflightDto preflight = await PrepareSetAsync(driver, prepare);

        Assert.True((await CommitSetAsync(factory, prepare, preflight.PreflightToken)).ReleasedErasureFingerprint);

        MemoryErasureRoundTrip<CovenantEraseRequest> again = await driver.EraseCovenantAsync(CovenantScope.Campaign, campaign, Key);

        Assert.True(again.Result.Local.SuppressionFingerprintRecorded);

        Assert.Equal(1, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Covenant));

        using HttpResponseMessage retried = await PutSetAsync(factory, prepare, preflight.PreflightToken);

        Assert.Equal(HttpStatusCode.Conflict, retried.StatusCode);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, await MemoryErasureRouteDriver.ReadErrorCodeAsync(retried));

        Assert.Equal(1, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Covenant));
    }

    /// <summary>
    /// A key nobody erased has nothing to release, whether or not the store holds another key's
    /// fingerprint, and the other key's fingerprint is left alone.
    /// </summary>
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_set_of_a_never_erased_key_reports_false_on_both_halves(bool otherKeyErased)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(credentials, covenant: true);

        MemoryErasureRouteDriver driver = new(factory.CreateClient());

        if (otherKeyErased)
        {
            await EraseCovenantKeyAsync(driver, CovenantScope.Global, null, "preference.other");
        }

        CovenantSetPrepareRequest prepare = new(CovenantScope.Global, null, "preference.fresh", Content, 0, Guid.NewGuid(), Reactivate: false);

        CovenantMutationPreflightDto preflight = await PrepareSetAsync(driver, prepare);

        Assert.False(preflight.Effect.ReleasesErasureFingerprint);

        CovenantMutationResultDto result = await CommitSetAsync(factory, prepare, preflight.PreflightToken);

        Assert.Equal(CovenantMutationOutcome.Applied, result.Outcome);

        Assert.False(result.ReleasedErasureFingerprint);

        Assert.Equal(
            otherKeyErased ? 1 : 0,
            await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Covenant));
    }

    /// <summary>
    /// The same key set in another scope is a different identity, in either direction: the fingerprint
    /// of the erased scope stays, and both halves say nothing was released.
    /// </summary>
    /// <remarks>
    /// The Global-erased row matters most: a Global fingerprint is what restore staging uses to purge a
    /// restored Global entry, so a Campaign write that lifted it would bring the entry back.
    /// </remarks>
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_operator_set_in_another_scope_releases_nothing(bool erasedInCampaign)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(credentials, covenant: true);

        MemoryErasureRouteDriver driver = new(factory.CreateClient());

        Guid campaign = await RegisterCampaignAsync(factory, "c");

        if (erasedInCampaign)
        {
            await EraseCovenantKeyAsync(driver, CovenantScope.Campaign, campaign, Key);
        }
        else
        {
            await EraseCovenantKeyAsync(driver, CovenantScope.Global, null, Key);
        }

        CovenantSetPrepareRequest prepare = erasedInCampaign
            ? new(CovenantScope.Global, null, Key, Content, 0, Guid.NewGuid(), Reactivate: false)
            : new(CovenantScope.Campaign, campaign, Key, Content, 0, Guid.NewGuid(), Reactivate: false);

        CovenantMutationPreflightDto preflight = await PrepareSetAsync(driver, prepare);

        Assert.False(preflight.Effect.ReleasesErasureFingerprint);

        CovenantMutationResultDto result = await CommitSetAsync(factory, prepare, preflight.PreflightToken);

        Assert.Equal(CovenantMutationOutcome.Applied, result.Outcome);

        Assert.False(result.ReleasedErasureFingerprint);

        Assert.Equal(1, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Covenant));
    }

    /// <summary>
    /// With fingerprints the latch has no key for, the operator's write is never refused; it cannot
    /// check, so both halves say so, and it neither deletes evidence nor creates a key.
    /// </summary>
    [SkippableFact]
    public async Task Set_with_fingerprints_but_a_lost_key_succeeds_and_reports_null()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        await using RestartableArcanumProfileFixture profile = new();

        await using (ArcanumWebApplicationFactory first = MemoryErasureRouteDriver.Host(credentials, profile, covenant: true))
        {
            await EraseCovenantKeyAsync(new MemoryErasureRouteDriver(first.CreateClient()), CovenantScope.Global, null, Key);
        }

        Assert.Equal(OsCredentialStoreStatus.Ok, credentials.Delete(Service, Account).Status);

        await using ArcanumWebApplicationFactory second = MemoryErasureRouteDriver.Host(credentials, profile, covenant: true);

        MemoryErasureRouteDriver driver = new(second.CreateClient());

        CovenantSetPrepareRequest prepare = new(CovenantScope.Global, null, Key, Content, 0, Guid.NewGuid(), Reactivate: false);

        CovenantMutationPreflightDto preflight = await PrepareSetAsync(driver, prepare);

        Assert.Null(preflight.Effect.ReleasesErasureFingerprint);

        CovenantMutationResultDto result = await CommitSetAsync(second, prepare, preflight.PreflightToken);

        Assert.Equal(CovenantMutationOutcome.Applied, result.Outcome);

        Assert.Null(result.ReleasedErasureFingerprint);

        Assert.Equal(1, await MemoryErasureRouteDriver.FingerprintCountAsync(second, MemoryReviewStore.Covenant));

        Assert.Equal(OsCredentialStoreStatus.NotFound, credentials.ProbePresence(Service, Account));
    }

    [SkippableFact]
    public async Task Saga_correction_to_erased_content_in_the_same_scope_releases_it()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(credentials, covenant: true);

        MemoryErasureRouteDriver driver = new(factory.CreateClient());

        string alpha = await MemoryErasureRouteDriver.InsertSagaAsync(factory, "alpha");

        string beta = await MemoryErasureRouteDriver.InsertSagaAsync(factory, "beta");

        _ = await driver.EraseSagaAsync(beta);

        SagaCurationResult corrected = await CorrectOkAsync(factory, alpha, "beta");

        Assert.True(corrected.ReleasedErasureFingerprint);

        Assert.Equal(0, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Saga));

        Assert.Equal("beta", (await ShowAsync(factory, alpha)).Memory.Content);
    }

    /// <summary>
    /// A correction releases only in the corrected memory's own scope, in either direction, and the
    /// fingerprint it left still refuses the content where it was erased.
    /// </summary>
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Saga_correction_in_another_scope_releases_nothing(bool erasedInCampaign)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(credentials, covenant: true);

        MemoryErasureRouteDriver driver = new(factory.CreateClient());

        (_, Guid session) = await BoundSessionAsync(factory, "x");

        Guid? erasedSession = erasedInCampaign ? session : null;

        Guid? correctedSession = erasedInCampaign ? null : session;

        string beta = await MemoryErasureRouteDriver.InsertSagaAsync(factory, "beta", erasedSession);

        string alpha = await MemoryErasureRouteDriver.InsertSagaAsync(factory, "alpha", correctedSession);

        _ = await driver.EraseSagaAsync(beta);

        SagaCurationResult corrected = await CorrectOkAsync(factory, alpha, "beta");

        Assert.False(corrected.ReleasedErasureFingerprint);

        Assert.Equal(1, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Saga));

        // Agents stay refused where the operator erased it.
        Assert.Equal(
            SagaMemoryWriteOutcome.Suppressed,
            await MemoryErasureRouteDriver.InsertSagaOutcomeAsync(factory, "beta", erasedSession));
    }

    /// <summary>
    /// Evidence recorded under a key the latch no longer holds is evidence this write cannot check, so
    /// the correction succeeds and reports that rather than claiming it released nothing.
    /// </summary>
    [SkippableFact]
    public async Task Saga_correction_with_evidence_under_a_replaced_key_reports_null()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        await using RestartableArcanumProfileFixture profile = new();

        string alpha;

        await using (ArcanumWebApplicationFactory first = MemoryErasureRouteDriver.Host(credentials, profile, covenant: true))
        {
            alpha = await MemoryErasureRouteDriver.InsertSagaAsync(first, "alpha");

            string beta = await MemoryErasureRouteDriver.InsertSagaAsync(first, "beta");

            _ = await new MemoryErasureRouteDriver(first.CreateClient()).EraseSagaAsync(beta);
        }

        Assert.Equal(
            OsCredentialStoreStatus.Ok,
            credentials.Set(Service, Account, Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32))).Status);

        await using ArcanumWebApplicationFactory second = MemoryErasureRouteDriver.Host(credentials, profile, covenant: true);

        SagaCurationResult corrected = await CorrectOkAsync(second, alpha, "beta");

        Assert.Null(corrected.ReleasedErasureFingerprint);

        Assert.Equal(1, await MemoryErasureRouteDriver.FingerprintCountAsync(second, MemoryReviewStore.Saga));
    }

    /// <summary>
    /// A bulk correction under a key that did not record the store's evidence cannot check it, so both
    /// the plan and the result say so rather than claiming nothing would be or was released.
    /// </summary>
    [SkippableFact]
    public async Task Bulk_review_Correct_with_evidence_under_a_replaced_key_reports_null_before_and_after()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        await using RestartableArcanumProfileFixture profile = new();

        string alpha;

        await using (ArcanumWebApplicationFactory first = MemoryErasureRouteDriver.Host(credentials, profile, covenant: true))
        {
            alpha = await MemoryErasureRouteDriver.InsertSagaAsync(first, "alpha");

            string beta = await MemoryErasureRouteDriver.InsertSagaAsync(first, "beta");

            _ = await new MemoryErasureRouteDriver(first.CreateClient()).EraseSagaAsync(beta);
        }

        Assert.Equal(
            OsCredentialStoreStatus.Ok,
            credentials.Set(Service, Account, Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32))).Status);

        await using ArcanumWebApplicationFactory second = MemoryErasureRouteDriver.Host(credentials, profile, covenant: true);

        MemoryReviewBulkResultDto result = await ReviewCorrectAsync(second.CreateAuthenticatedClient(), SagaMemoryScopeKind.Global, null, alpha, "beta");

        Assert.Null(Assert.Single(result.Items).ReleasedErasureFingerprint);

        Assert.Equal(1, await MemoryErasureRouteDriver.FingerprintCountAsync(second, MemoryReviewStore.Saga));

        Assert.Equal("beta", (await ShowAsync(second, alpha)).Memory.Content);
    }

    [SkippableFact]
    public async Task Bulk_review_Correct_to_erased_content_releases_and_reports_it_on_the_item()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(credentials, covenant: true);

        MemoryErasureRouteDriver driver = new(factory.CreateClient());

        HttpClient client = factory.CreateAuthenticatedClient();

        string alpha = await MemoryErasureRouteDriver.InsertSagaAsync(factory, "alpha");

        string beta = await MemoryErasureRouteDriver.InsertSagaAsync(factory, "beta");

        _ = await driver.EraseSagaAsync(beta);

        SagaReviewItemDto item;

        using (HttpResponseMessage listed = await client.PostAsync(
            "/api/memory/saga/review/list",
            JsonContent.Create(
                new SagaReviewListRequest(SagaMemoryScopeKind.Global, null, 50, null),
                ArcanumJsonContext.Default.SagaReviewListRequest)))
        {
            Assert.Equal(HttpStatusCode.OK, listed.StatusCode);

            SagaReviewPageDto page = await MemoryErasureRouteDriver.ReadDataAsync(listed, ArcanumJsonContext.Default.ApiResponseSagaReviewPageDto);

            item = Assert.Single(
                page.Items,
                candidate => string.Equals(candidate.SubjectId, alpha, StringComparison.OrdinalIgnoreCase));
        }

        SagaReviewBulkPrepareRequest prepare = new(
            Guid.NewGuid(),
            SagaMemoryScopeKind.Global,
            null,
            MemoryReviewAction.Correct,
            [new SagaReviewDecision(item.ObservationToken, "beta")]);

        MemoryReviewBulkPlanDto plan;

        using (HttpResponseMessage prepared = await client.PostAsync(
            "/api/memory/saga/review/prepare",
            JsonContent.Create(prepare, ArcanumJsonContext.Default.SagaReviewBulkPrepareRequest)))
        {
            Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);

            plan = await MemoryErasureRouteDriver.ReadDataAsync(prepared, ArcanumJsonContext.Default.ApiResponseMemoryReviewBulkPlanDto);
        }

        // The plan says so before anything is applied, so the operator approves the release knowingly.
        Assert.True(Assert.Single(plan.Items).ReleasesErasureFingerprint);

        MemoryReviewBulkResultDto result = await ApplyReviewAsync(client, new SagaReviewBulkApplyRequest(prepare, plan.PreparedPlanToken));

        Assert.False(result.Replayed);

        Assert.True(Assert.Single(result.Items).ReleasedErasureFingerprint);

        Assert.Equal(0, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Saga));

        Assert.Equal("beta", (await ShowAsync(factory, alpha)).Memory.Content);

        MemoryReviewBulkResultDto replayed = await ApplyReviewAsync(client, new SagaReviewBulkApplyRequest(prepare, plan.PreparedPlanToken));

        Assert.True(replayed.Replayed);

        Assert.False(Assert.Single(replayed.Items).ReleasedErasureFingerprint);
    }

    /// <summary>
    /// A bulk correction releases only in the reviewed memory's own scope, in either direction, and the
    /// fingerprint it left still refuses the content where it was erased.
    /// </summary>
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Bulk_review_Correct_in_another_scope_releases_nothing(bool erasedInCampaign)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(credentials, covenant: true);

        MemoryErasureRouteDriver driver = new(factory.CreateClient());

        HttpClient client = factory.CreateAuthenticatedClient();

        (Guid campaign, Guid session) = await BoundSessionAsync(factory, "x");

        Guid? erasedSession = erasedInCampaign ? session : null;

        Guid? correctedSession = erasedInCampaign ? null : session;

        string beta = await MemoryErasureRouteDriver.InsertSagaAsync(factory, "beta", erasedSession);

        string alpha = await MemoryErasureRouteDriver.InsertSagaAsync(factory, "alpha", correctedSession);

        _ = await driver.EraseSagaAsync(beta);

        MemoryReviewBulkResultDto result = await ReviewCorrectAsync(
            client,
            erasedInCampaign ? SagaMemoryScopeKind.Global : SagaMemoryScopeKind.Campaign,
            erasedInCampaign ? null : campaign,
            alpha,
            "beta");

        Assert.False(Assert.Single(result.Items).ReleasedErasureFingerprint);

        Assert.Equal(1, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Saga));

        Assert.Equal("beta", (await ShowAsync(factory, alpha)).Memory.Content);

        Assert.Equal(
            SagaMemoryWriteOutcome.Suppressed,
            await MemoryErasureRouteDriver.InsertSagaOutcomeAsync(factory, "beta", erasedSession));
    }

    /// <summary>
    /// A Saga correction runs under ordinary API authority, which a host-tools-tainted installation does
    /// not refuse, while release requires authority it does. So on a tainted installation the correction
    /// still lands but lifts nothing, and says so; the same correction on a clean one releases.
    /// </summary>
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_Saga_correction_on_a_tainted_installation_commits_and_releases_nothing(bool tainted)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        TaintSwitch taint = new();

        await using ArcanumWebApplicationFactory factory = TaintableHost(credentials, taint);

        MemoryErasureRouteDriver driver = new(factory.CreateClient());

        string alpha = await MemoryErasureRouteDriver.InsertSagaAsync(factory, "alpha");

        string beta = await MemoryErasureRouteDriver.InsertSagaAsync(factory, "beta");

        _ = await driver.EraseSagaAsync(beta);

        taint.Tainted = tainted;

        SagaCurationResult corrected = await CorrectOkAsync(factory, alpha, "beta");

        Assert.Equal(!tainted, corrected.ReleasedErasureFingerprint);

        Assert.Equal(tainted ? 1 : 0, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Saga));

        Assert.Equal("beta", (await ShowAsync(factory, alpha)).Memory.Content);
    }

    /// <summary>The bulk correction follows the same rule as the single one on a tainted installation.</summary>
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_bulk_review_Correct_on_a_tainted_installation_commits_and_releases_nothing(bool tainted)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        TaintSwitch taint = new();

        await using ArcanumWebApplicationFactory factory = TaintableHost(credentials, taint);

        MemoryErasureRouteDriver driver = new(factory.CreateClient());

        HttpClient client = factory.CreateAuthenticatedClient();

        string alpha = await MemoryErasureRouteDriver.InsertSagaAsync(factory, "alpha");

        string beta = await MemoryErasureRouteDriver.InsertSagaAsync(factory, "beta");

        _ = await driver.EraseSagaAsync(beta);

        taint.Tainted = tainted;

        MemoryReviewBulkResultDto result = await ReviewCorrectAsync(client, SagaMemoryScopeKind.Global, null, alpha, "beta");

        Assert.Equal(!tainted, Assert.Single(result.Items).ReleasedErasureFingerprint);

        Assert.Equal(tainted ? 1 : 0, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Saga));

        Assert.Equal("beta", (await ShowAsync(factory, alpha)).Memory.Content);
    }

    /// <summary>
    /// A Covenant set commits under Covenant management authority, which a tainted installation refuses
    /// before the body is read, so a set prepared while the installation was clean cannot release once it
    /// is tainted.
    /// </summary>
    [SkippableFact]
    public async Task A_Covenant_set_on_a_tainted_installation_is_refused_and_releases_nothing()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        TaintSwitch taint = new();

        await using ArcanumWebApplicationFactory factory = TaintableHost(credentials, taint);

        MemoryErasureRouteDriver driver = new(factory.CreateClient());

        await EraseCovenantKeyAsync(driver, CovenantScope.Global, null, Key);

        CovenantSetPrepareRequest prepare = new(CovenantScope.Global, null, Key, Content, 0, Guid.NewGuid(), Reactivate: false);

        CovenantMutationPreflightDto preflight = await PrepareSetAsync(driver, prepare);

        Assert.True(preflight.Effect.ReleasesErasureFingerprint);

        taint.Tainted = true;

        using HttpResponseMessage refused = await PutSetAsync(factory, prepare, preflight.PreflightToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);

        Assert.Equal(ErrorCodes.Covenant.OperatorAuthorityUnavailable, await MemoryErasureRouteDriver.ReadErrorCodeAsync(refused));

        Assert.Equal(1, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Covenant));
    }

    /// <summary>
    /// An erasure host whose published authority can be reported host-tools tainted after startup, the
    /// way a tainted installation publishes it.
    /// </summary>
    private static ArcanumWebApplicationFactory TaintableHost(InMemoryOsCredentialStore credentials, TaintSwitch taint)
    {
        ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(credentials, covenant: true);

        factory.ServiceOverrides += services =>
        {
            services.RemoveAll<ICovenantAuthoritySnapshotProvider>();

            services.AddSingleton<ICovenantAuthoritySnapshotProvider>(provider =>
            {
                taint.Inner = provider.GetRequiredService<CovenantAuthoritySnapshotProvider>();

                return taint;
            });
        };

        return factory;
    }

    /// <summary>Lists one exact Saga scope, then prepares and applies one correction of one memory.</summary>
    private static async Task<MemoryReviewBulkResultDto> ReviewCorrectAsync(
        HttpClient client,
        SagaMemoryScopeKind scopeKind,
        Guid? campaignId,
        string memoryId,
        string replacement)
    {
        SagaReviewItemDto item;

        using (HttpResponseMessage listed = await client.PostAsync(
            "/api/memory/saga/review/list",
            JsonContent.Create(
                new SagaReviewListRequest(scopeKind, campaignId, 50, null),
                ArcanumJsonContext.Default.SagaReviewListRequest)))
        {
            Assert.Equal(HttpStatusCode.OK, listed.StatusCode);

            SagaReviewPageDto page = await MemoryErasureRouteDriver.ReadDataAsync(listed, ArcanumJsonContext.Default.ApiResponseSagaReviewPageDto);

            item = Assert.Single(
                page.Items,
                candidate => string.Equals(candidate.SubjectId, memoryId, StringComparison.OrdinalIgnoreCase));
        }

        SagaReviewBulkPrepareRequest prepare = new(
            Guid.NewGuid(),
            scopeKind,
            campaignId,
            MemoryReviewAction.Correct,
            [new SagaReviewDecision(item.ObservationToken, replacement)]);

        MemoryReviewBulkPlanDto plan;

        using (HttpResponseMessage prepared = await client.PostAsync(
            "/api/memory/saga/review/prepare",
            JsonContent.Create(prepare, ArcanumJsonContext.Default.SagaReviewBulkPrepareRequest)))
        {
            Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);

            plan = await MemoryErasureRouteDriver.ReadDataAsync(prepared, ArcanumJsonContext.Default.ApiResponseMemoryReviewBulkPlanDto);
        }

        MemoryReviewBulkResultDto result = await ApplyReviewAsync(client, new SagaReviewBulkApplyRequest(prepare, plan.PreparedPlanToken));

        // What the plan disclosed before the question is what the apply did: true, false and null alike.
        Assert.Equal(Assert.Single(result.Items).ReleasedErasureFingerprint, Assert.Single(plan.Items).ReleasesErasureFingerprint);

        return result;
    }

    /// <summary>Writes one Covenant key through the set routes and erases it through the erase routes.</summary>
    private static async Task EraseCovenantKeyAsync(
        MemoryErasureRouteDriver driver,
        CovenantScope scope,
        Guid? campaignId,
        string key)
    {
        _ = await driver.SetCovenantAsync(scope, campaignId, key, Content);

        MemoryErasureRoundTrip<CovenantEraseRequest> erased = await driver.EraseCovenantAsync(scope, campaignId, key);

        Assert.True(erased.Result.Local.SuppressionFingerprintRecorded);
    }

    private static async Task<CovenantMutationPreflightDto> PrepareSetAsync(
        MemoryErasureRouteDriver driver,
        CovenantSetPrepareRequest prepare)
    {
        using HttpResponseMessage prepared = await driver.PostAsync(
            "/api/memory/covenant/set/prepare",
            prepare,
            ArcanumJsonContext.Default.CovenantSetPrepareRequest);

        Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);

        return await MemoryErasureRouteDriver.ReadDataAsync(prepared, ArcanumJsonContext.Default.ApiResponseCovenantMutationPreflightDto);
    }

    private static Task<HttpResponseMessage> PutSetAsync(
        ArcanumWebApplicationFactory factory,
        CovenantSetPrepareRequest prepare,
        string token) =>
        factory.CreateAuthenticatedClient().PutAsync(
            "/api/memory/covenant",
            JsonContent.Create(
                new CovenantSetRequest(
                    prepare.Scope,
                    prepare.CampaignId,
                    prepare.Key,
                    prepare.Content,
                    prepare.ExpectedRevision,
                    prepare.MutationId,
                    prepare.Reactivate,
                    token),
                ArcanumJsonContext.Default.CovenantSetRequest));

    private static async Task<CovenantMutationResultDto> CommitSetAsync(
        ArcanumWebApplicationFactory factory,
        CovenantSetPrepareRequest prepare,
        string token)
    {
        using HttpResponseMessage committed = await PutSetAsync(factory, prepare, token);

        Assert.Equal(HttpStatusCode.OK, committed.StatusCode);

        return await MemoryErasureRouteDriver.ReadDataAsync(committed, ArcanumJsonContext.Default.ApiResponseCovenantMutationResultDto);
    }

    private static async Task<SagaMemoryDetail> ShowAsync(ArcanumWebApplicationFactory factory, string id)
    {
        using HttpResponseMessage shown = await factory.CreateAuthenticatedClient().GetAsync($"/api/memory/saga/{id}");

        Assert.Equal(HttpStatusCode.OK, shown.StatusCode);

        return await MemoryErasureRouteDriver.ReadDataAsync(shown, ArcanumJsonContext.Default.ApiResponseSagaMemoryDetail);
    }

    /// <summary>Corrects one memory through its route, quoting the content hash its show published.</summary>
    private static async Task<SagaCurationResult> CorrectOkAsync(ArcanumWebApplicationFactory factory, string id, string content)
    {
        SagaMemoryDetail detail = await ShowAsync(factory, id);

        using HttpResponseMessage corrected = await factory.CreateAuthenticatedClient().PostAsync(
            $"/api/memory/saga/{id}/correct",
            JsonContent.Create(
                new SagaCorrectRequest(detail.ContentHash, content),
                ArcanumJsonContext.Default.SagaCorrectRequest));

        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);

        SagaCurationResult result = await MemoryErasureRouteDriver.ReadDataAsync(corrected, ArcanumJsonContext.Default.ApiResponseSagaCurationResult);

        Assert.Equal(SagaCurationOutcomeKind.Applied, result.Outcome);

        return result;
    }

    private static async Task<MemoryReviewBulkResultDto> ApplyReviewAsync(HttpClient client, SagaReviewBulkApplyRequest request)
    {
        using HttpResponseMessage applied = await client.PostAsync(
            "/api/memory/saga/review/apply",
            JsonContent.Create(request, ArcanumJsonContext.Default.SagaReviewBulkApplyRequest));

        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);

        return await MemoryErasureRouteDriver.ReadDataAsync(applied, ArcanumJsonContext.Default.ApiResponseMemoryReviewBulkResultDto);
    }

    /// <summary>Registers one Campaign through its route, so the Covenant gate knows the scope it leases.</summary>
    private static async Task<Guid> RegisterCampaignAsync(ArcanumWebApplicationFactory factory, string suffix)
    {
        string path = Path.Combine(factory.TempHome, $"recreation-campaign-{suffix}");

        Directory.CreateDirectory(path);

        using HttpResponseMessage registered = await factory.CreateAuthenticatedClient().PostAsync(
            "/api/campaigns",
            JsonContent.Create(
                new RegisterCampaignRequest($"Re-creation {suffix}", path, WorkspaceType.Campaign, null),
                ArcanumJsonContext.Default.RegisterCampaignRequest));

        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);

        return (await MemoryErasureRouteDriver.ReadDataAsync(registered, ArcanumJsonContext.Default.ApiResponseCampaignDto)).Id;
    }

    /// <summary>
    /// Registers one Campaign and binds a new Session to it through the turn-begin store, the writer that
    /// records a Session's Campaign binding.
    /// </summary>
    private static async Task<(Guid Campaign, Guid Session)> BoundSessionAsync(ArcanumWebApplicationFactory factory, string suffix)
    {
        Guid campaign = await RegisterCampaignAsync(factory, suffix);

        using IServiceScope scope = factory.Services.CreateScope();

        Result<Guid> session = await scope.ServiceProvider.GetRequiredService<ISessionTurnBeginStore>().CreateBoundSessionAsync(
            CanonicalCampaignContext.Create(
                SessionCampaignBinding.ForCampaign(campaign),
                campaignAvailabilityGeneration: 1,
                pathIdentityPolicyVersion: 1,
                pathIdentityRevision: null,
                rootIdentityDigest: null),
            $"Re-creation session {suffix}",
            CancellationToken.None);

        Assert.True(session.IsSuccess, session.IsFailure ? session.Error.Message : null);

        return (campaign, session.Value);
    }

    /// <summary>
    /// The host's published authority, reported as host-tools tainted while <see cref="Tainted"/> is
    /// set, exactly as a tainted installation publishes it.
    /// </summary>
    private sealed class TaintSwitch : ICovenantAuthoritySnapshotProvider
    {
        private int _tainted;

        internal ICovenantAuthoritySnapshotProvider? Inner { get; set; }

        internal bool Tainted
        {
            get => Volatile.Read(ref _tainted) != 0;
            set => Volatile.Write(ref _tainted, value ? 1 : 0);
        }

        public CovenantAuthoritySnapshot? Current =>
            Inner?.Current is { } current && Tainted
                ? current with { HostToolsState = CovenantHostToolsState.HostToolsTainted }
                : Inner?.Current;
    }
}
