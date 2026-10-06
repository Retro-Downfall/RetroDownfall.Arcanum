using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Tower;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Memory;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.NativeSqlCipher;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api.Tower;

/// <summary>
/// The Saga erase routes, prepare then apply, driven through the mapped HTTP surface against a host
/// whose preconditions were written by production writers.
/// </summary>
/// <remarks>
/// <para>Campaigns are registered through their route. A route-created Session carries no Campaign
/// binding until a turn begins, so each Session here is created through the turn-begin store, the
/// production writer that binds a Session to its Campaign. Memories are written through the store's
/// own insert and read back through the detail route, which is where the CLI takes its target from.</para>
///
/// <para>Every test that erases ends by asking the database whether any Annals claim outlived its
/// row.</para>
/// </remarks>
[Collection("ApiHost")]
public sealed class SagaErasureEndpointTests
{
    private const string T = "The ward-stone lies under the mill.";

    private const string Other = "The miller keeps a second key above the door.";

    private static readonly MemoryRetainedLocalCopy[] RetainedWithoutAudit =
    [
        MemoryRetainedLocalCopy.SessionTranscripts,
        MemoryRetainedLocalCopy.SearchAndSummaryDerivatives,
        MemoryRetainedLocalCopy.Attachments,
        MemoryRetainedLocalCopy.ResponseCaches,
        MemoryRetainedLocalCopy.ApplicationLogs,
        MemoryRetainedLocalCopy.BackupArchives,
        MemoryRetainedLocalCopy.OtherLocalState,
    ];

    [SkippableFact]
    public async Task Prepare_then_apply_erases_the_twin_class_in_scope_and_leaves_other_scopes_and_content()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore());

        (HttpClient client, MemoryErasureRouteDriver driver) = Connect(factory);

        (Guid campaignA, Guid sessionA) = await BoundSessionAsync(factory, client, "a");

        (_, Guid sessionB) = await BoundSessionAsync(factory, client, "b");

        string target = await MemoryErasureRouteDriver.InsertSagaAsync(factory, T, sessionA);

        string firstTwin = await InsertTwinAsync(factory, campaignA, T);

        string secondTwin = await InsertTwinAsync(factory, campaignA, T);

        string otherText = await MemoryErasureRouteDriver.InsertSagaAsync(factory, Other, sessionA);

        string otherScope = await MemoryErasureRouteDriver.InsertSagaAsync(factory, T, sessionB);

        string[] erased = [target, firstTwin, secondTwin];

        long before = await PlanRowsAsync(factory, erased);

        SagaErasePrepareRequest prepare = await PrepareRequestAsync(client, target, Guid.NewGuid());

        MemoryErasurePreflightDto preflight = await PrepareOkAsync(driver, prepare);

        Assert.Equal(MemoryReviewStore.Saga, preflight.Store);

        Assert.Equal(prepare.MutationId, preflight.MutationId);

        Assert.Equal(3, preflight.Plan.ErasedItemCount);

        Assert.Equal(before, preflight.Plan.RowsToRemove);

        Assert.Equal(0, preflight.Plan.LabelsToRemove);

        Assert.Equal(0, preflight.Plan.RetirementSuppressionsToRemove);

        Assert.False(preflight.Plan.Pinned);

        Assert.Null(preflight.Plan.Lexicon);

        Assert.Null(preflight.Plan.Covenant);

        Assert.Equal([MemoryErasureNote.OtherScopesUnaffected], preflight.Notes);

        Assert.Equal(MemoryExternalRevocation.NotPerformed, preflight.External.Revocation);

        Assert.Equal(
            [
                MemoryExternalChannel.InferenceProviderAuthorship,
                MemoryExternalChannel.InferenceProviderContext,
                MemoryExternalChannel.EmbeddingProvider,
                MemoryExternalChannel.EncryptedBackup,
                MemoryExternalChannel.OtherExternal,
            ],
            preflight.External.Channels.Select(static channel => channel.Channel));

        Assert.Equal(
            [
                MemoryExternalEvidence.Known,
                MemoryExternalEvidence.NotRecorded,
                MemoryExternalEvidence.Known,
                MemoryExternalEvidence.NotRecorded,
                MemoryExternalEvidence.NotRecorded,
            ],
            preflight.External.Channels.Select(static channel => channel.Evidence));

        Assert.Equal(RetainedWithoutAudit, preflight.RetainedLocalCopies);

        Assert.Equal(TimeSpan.FromMinutes(5), preflight.ExpiresAtUtc - preflight.IssuedAtUtc);

        Assert.Equal(64, preflight.RequestDigest.Length);

        Assert.Equal(64, preflight.EffectDigest.Length);

        MemoryErasureResultDto result = await driver.ApplySagaAsync(Apply(prepare, preflight));

        Assert.Equal(MemoryReviewStore.Saga, result.Store);

        Assert.Equal(prepare.MutationId, result.MutationId);

        Assert.False(result.Replayed);

        Assert.Equal(preflight.EffectDigest, result.EffectDigest);

        Assert.Equal(3, result.Local.ErasedItemCount);

        Assert.Equal(preflight.Plan.RowsToRemove, result.Local.RemovedRowCount);

        Assert.Equal(0, result.Local.RemovedLabelCount);

        Assert.Equal(0, result.Local.RemovedRetirementSuppressionCount);

        Assert.True(result.Local.SuppressionFingerprintRecorded);

        Assert.Equal(preflight.External, result.External, ExposureComparer.Instance);

        Assert.Equal(RetainedWithoutAudit, result.RetainedLocalCopies);

        Assert.Equal([MemoryErasureNote.OtherScopesUnaffected], result.Notes);

        foreach (string id in erased)
        {
            await AssertShowRefusedAsync(client, id, HttpStatusCode.NotFound, ErrorCodes.Saga.NotFound);
        }

        await AssertShownAsync(client, otherScope, T);

        await AssertShownAsync(client, otherText, Other);

        Assert.Equal(0, await CountForIdsAsync(factory, "SELECT count(*) FROM saga_memory_embeddings WHERE MemoryId IN ({0})", erased));

        Assert.Equal(3, await ReceiptSubjectsAsync(factory, prepare.MutationId));

        Assert.Equal(1, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Saga));

        await AssertNoOrphanClaimsAsync(factory);
    }

    [SkippableFact]
    public async Task A_retired_labelled_memory_is_erased_with_its_label_and_retirement_suppression_pair()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(
            new InMemoryOsCredentialStore(),
            covenant: true);

        (HttpClient client, MemoryErasureRouteDriver driver) = Connect(factory);

        (Guid campaignA, Guid sessionA) = await BoundSessionAsync(factory, client, "a");

        string target = await MemoryErasureRouteDriver.InsertSagaAsync(factory, T, sessionA);

        using (HttpResponseMessage retired = await client.PostAsync(
            $"/api/memory/saga/{target}/retire",
            JsonContent.Create(new SagaRetireRequest(Hash(T)), ArcanumJsonContext.Default.SagaRetireRequest)))
        {
            Assert.Equal(HttpStatusCode.OK, retired.StatusCode);
        }

        await LabelAsync(factory, target, sessionA, campaignA);

        Assert.Equal(1, await ScalarAsync(factory, "SELECT count(*) FROM saga_retirement_suppressions;"));

        MemoryErasureRoundTrip<SagaEraseRequest> erased = await driver.EraseSagaAsync(target);

        Assert.Equal(1, erased.Preflight.Plan.LabelsToRemove);

        Assert.Equal(1, erased.Preflight.Plan.RetirementSuppressionsToRemove);

        Assert.Equal(1, erased.Result.Local.RemovedLabelCount);

        Assert.Equal(1, erased.Result.Local.RemovedRetirementSuppressionCount);

        Assert.Equal(0, await ScalarAsync(factory, "SELECT count(*) FROM artifact_sensitivity;"));

        Assert.Equal(0, await ScalarAsync(factory, "SELECT count(*) FROM saga_retirement_suppressions;"));

        // The retirement key is read, never created, and never removed: other retirements still need it.
        Assert.Equal(1, await ScalarAsync(factory, "SELECT count(*) FROM saga_suppression_key;"));

        await AssertShowRefusedAsync(client, target, HttpStatusCode.NotFound, ErrorCodes.Saga.NotFound);

        await AssertNoOrphanClaimsAsync(factory);
    }

    [SkippableFact]
    public async Task A_label_that_appeared_after_prepare_is_a_stale_plan()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(
            new InMemoryOsCredentialStore(),
            covenant: true);

        (HttpClient client, MemoryErasureRouteDriver driver) = Connect(factory);

        (Guid campaignA, Guid sessionA) = await BoundSessionAsync(factory, client, "a");

        string target = await MemoryErasureRouteDriver.InsertSagaAsync(factory, T, sessionA);

        SagaErasePrepareRequest prepare = await PrepareRequestAsync(client, target, Guid.NewGuid());

        MemoryErasurePreflightDto preflight = await PrepareOkAsync(driver, prepare);

        Assert.Equal(0, preflight.Plan.LabelsToRemove);

        await LabelAsync(factory, target, sessionA, campaignA);

        await AssertApplyRefusedAsync(driver, Apply(prepare, preflight), HttpStatusCode.Conflict, ErrorCodes.MemoryErasure.StalePlan);

        await AssertShownAsync(client, target, T);

        Assert.Equal(1, await ScalarAsync(factory, "SELECT count(*) FROM artifact_sensitivity;"));

        Assert.Equal(1, await CountForIdsAsync(factory, "SELECT count(*) FROM saga_memory_embeddings WHERE MemoryId IN ({0})", [target]));

        Assert.Equal(0, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Saga));

        await AssertNoOrphanClaimsAsync(factory);
    }

    [SkippableFact]
    public async Task A_new_twin_between_prepare_and_apply_is_a_stale_plan()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore());

        (HttpClient client, MemoryErasureRouteDriver driver) = Connect(factory);

        (Guid campaignA, Guid sessionA) = await BoundSessionAsync(factory, client, "a");

        string target = await MemoryErasureRouteDriver.InsertSagaAsync(factory, T, sessionA);

        _ = await InsertTwinAsync(factory, campaignA, T);

        _ = await InsertTwinAsync(factory, campaignA, T);

        SagaErasePrepareRequest prepare = await PrepareRequestAsync(client, target, Guid.NewGuid());

        MemoryErasurePreflightDto preflight = await PrepareOkAsync(driver, prepare);

        Assert.Equal(3, preflight.Plan.ErasedItemCount);

        _ = await InsertTwinAsync(factory, campaignA, T);

        await AssertApplyRefusedAsync(driver, Apply(prepare, preflight), HttpStatusCode.Conflict, ErrorCodes.MemoryErasure.StalePlan);

        Assert.Equal(4, await ContentRowsAsync(factory, T));

        Assert.Equal(0, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Saga));

        await AssertNoOrphanClaimsAsync(factory);
    }

    /// <summary>
    /// A memory written with the Annals off has no claim, so nothing but its content binding can tell
    /// the erase that the text it planned against was corrected. The caller even supplies the corrected
    /// text's hash, and the plan is still stale.
    /// </summary>
    [SkippableFact]
    public async Task A_claimless_correction_between_prepare_and_apply_is_a_stale_plan_even_with_the_new_hash()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        const string corrected = "The ward-stone lies under the old mill.";

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(
            new InMemoryOsCredentialStore(),
            configure: static settings => settings.Features.Annals = false);

        (HttpClient client, MemoryErasureRouteDriver driver) = Connect(factory);

        (_, Guid sessionA) = await BoundSessionAsync(factory, client, "a");

        string target = await MemoryErasureRouteDriver.InsertSagaAsync(factory, T, sessionA);

        SagaErasePrepareRequest prepare = await PrepareRequestAsync(client, target, Guid.NewGuid());

        Assert.Null(prepare.ExpectedClaimVersionId);

        MemoryErasurePreflightDto preflight = await PrepareOkAsync(driver, prepare);

        using (HttpResponseMessage correction = await client.PostAsync(
            $"/api/memory/saga/{target}/correct",
            JsonContent.Create(new SagaCorrectRequest(Hash(T), corrected), ArcanumJsonContext.Default.SagaCorrectRequest)))
        {
            Assert.Equal(HttpStatusCode.OK, correction.StatusCode);
        }

        SagaEraseRequest apply = Apply(prepare, preflight) with { ExpectedContentHash = Hash(corrected) };

        await AssertApplyRefusedAsync(driver, apply, HttpStatusCode.Conflict, ErrorCodes.MemoryErasure.StalePlan);

        await AssertShownAsync(client, target, corrected);

        Assert.Equal(0, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Saga));

        await AssertNoOrphanClaimsAsync(factory);
    }

    [SkippableFact]
    public async Task A_stale_content_hash_is_refused_before_anything_is_measured()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore());

        (HttpClient client, MemoryErasureRouteDriver driver) = Connect(factory);

        string target = await MemoryErasureRouteDriver.InsertSagaAsync(factory, T);

        SagaErasePrepareRequest prepare = await PrepareRequestAsync(client, target, Guid.NewGuid());

        using HttpResponseMessage response = await PostPrepareAsync(driver, prepare with { ExpectedContentHash = Hash(Other) });

        await AssertRefusalAsync(response, HttpStatusCode.Conflict, ErrorCodes.Saga.StaleContent);

        await AssertShownAsync(client, target, T);
    }

    [SkippableFact]
    public async Task Prepare_for_an_erased_memory_answers_410_and_an_unknown_memory_answers_404()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore());

        (_, MemoryErasureRouteDriver driver) = Connect(factory);

        string target = await MemoryErasureRouteDriver.InsertSagaAsync(factory, T);

        MemoryErasureRoundTrip<SagaEraseRequest> erased = await driver.EraseSagaAsync(target);

        SagaErasePrepareRequest again = new(
            target,
            erased.Apply.ExpectedContentHash,
            erased.Apply.ExpectedClaimVersionId,
            Guid.NewGuid());

        using (HttpResponseMessage gone = await PostPrepareAsync(driver, again))
        {
            await AssertRefusalAsync(gone, HttpStatusCode.Gone, ErrorCodes.MemoryErasure.SubjectErased);
        }

        using HttpResponseMessage unknown = await PostPrepareAsync(
            driver,
            again with { MemoryId = Guid.NewGuid().ToString() });

        await AssertRefusalAsync(unknown, HttpStatusCode.NotFound, ErrorCodes.Saga.NotFound);

        await AssertNoOrphanClaimsAsync(factory);
    }

    [SkippableFact]
    public async Task A_second_mutation_prepared_before_the_first_applied_answers_410_at_apply()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore());

        (HttpClient client, MemoryErasureRouteDriver driver) = Connect(factory);

        string target = await MemoryErasureRouteDriver.InsertSagaAsync(factory, T);

        SagaErasePrepareRequest first = await PrepareRequestAsync(client, target, Guid.NewGuid());

        SagaErasePrepareRequest second = first with { MutationId = Guid.NewGuid() };

        MemoryErasurePreflightDto firstPlan = await PrepareOkAsync(driver, first);

        MemoryErasurePreflightDto secondPlan = await PrepareOkAsync(driver, second);

        MemoryErasureResultDto applied = await driver.ApplySagaAsync(Apply(first, firstPlan));

        Assert.False(applied.Replayed);

        await AssertApplyRefusedAsync(driver, Apply(second, secondPlan), HttpStatusCode.Gone, ErrorCodes.MemoryErasure.SubjectErased);

        Assert.Equal(0, await ReceiptSubjectsAsync(factory, second.MutationId));

        await AssertNoOrphanClaimsAsync(factory);
    }

    [SkippableFact]
    public async Task A_repeated_apply_replays_the_receipt_without_a_valid_token()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore());

        (_, MemoryErasureRouteDriver driver) = Connect(factory);

        string target = await MemoryErasureRouteDriver.InsertSagaAsync(factory, T);

        MemoryErasureRoundTrip<SagaEraseRequest> erased = await driver.EraseSagaAsync(target);

        MemoryErasureResultDto replayed = await driver.ApplySagaAsync(erased.Apply with { PreflightToken = "x" });

        Assert.True(replayed.Replayed);

        AssertSameErase(erased.Result, replayed);

        Assert.Equal(1, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Saga));

        await AssertNoOrphanClaimsAsync(factory);
    }

    /// <summary>
    /// Replay needs no <em>valid</em> token but still needs a present one: the handler refuses an empty
    /// <c>preflightToken</c> as an invalid body before the service can look the receipt up, and the API
    /// document says exactly that.
    /// </summary>
    [SkippableTheory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_repeated_apply_with_an_empty_token_is_refused_even_though_the_receipt_exists(string token)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore());

        (_, MemoryErasureRouteDriver driver) = Connect(factory);

        string target = await MemoryErasureRouteDriver.InsertSagaAsync(factory, T);

        MemoryErasureRoundTrip<SagaEraseRequest> erased = await driver.EraseSagaAsync(target);

        await AssertApplyRefusedAsync(
            driver,
            erased.Apply with { PreflightToken = token },
            HttpStatusCode.BadRequest,
            ErrorCodes.Validation.InvalidBody);

        Assert.Equal(1, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Saga));
    }

    [SkippableFact]
    public async Task Concurrent_identical_applies_commit_once_and_replay_once()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore());

        (HttpClient client, MemoryErasureRouteDriver driver) = Connect(factory);

        string target = await MemoryErasureRouteDriver.InsertSagaAsync(factory, T);

        SagaErasePrepareRequest prepare = await PrepareRequestAsync(client, target, Guid.NewGuid());

        SagaEraseRequest apply = Apply(prepare, await PrepareOkAsync(driver, prepare));

        MemoryErasureResultDto[] results = await Task.WhenAll(
            driver.ApplySagaAsync(apply),
            driver.ApplySagaAsync(apply));

        Assert.Single(results, static result => !result.Replayed);

        Assert.Single(results, static result => result.Replayed);

        AssertSameErase(results[0], results[1]);

        Assert.Equal(1, await ReceiptsAsync(factory, prepare.MutationId));

        await AssertNoOrphanClaimsAsync(factory);
    }

    [SkippableFact]
    public async Task The_same_mutation_id_for_a_different_request_is_an_idempotency_conflict()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore());

        (HttpClient client, MemoryErasureRouteDriver driver) = Connect(factory);

        string first = await MemoryErasureRouteDriver.InsertSagaAsync(factory, T);

        string second = await MemoryErasureRouteDriver.InsertSagaAsync(factory, Other);

        Guid mutationId = Guid.NewGuid();

        _ = await driver.EraseSagaAsync(first, mutationId);

        SagaErasePrepareRequest reused = await PrepareRequestAsync(client, second, mutationId);

        await AssertApplyRefusedAsync(
            driver,
            Apply(reused, await PrepareOkAsync(driver, reused)),
            HttpStatusCode.Conflict,
            ErrorCodes.Security.IdempotencyConflict);

        await AssertShownAsync(client, second, Other);

        await AssertNoOrphanClaimsAsync(factory);
    }

    [SkippableFact]
    public async Task A_token_prepared_for_another_request_is_an_invalid_preflight()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore());

        (HttpClient client, MemoryErasureRouteDriver driver) = Connect(factory);

        string target = await MemoryErasureRouteDriver.InsertSagaAsync(factory, T);

        SagaErasePrepareRequest prepared = await PrepareRequestAsync(client, target, Guid.NewGuid());

        MemoryErasurePreflightDto preflight = await PrepareOkAsync(driver, prepared);

        SagaEraseRequest borrowed = Apply(prepared, preflight) with { MutationId = Guid.NewGuid() };

        await AssertApplyRefusedAsync(driver, borrowed, HttpStatusCode.BadRequest, ErrorCodes.MemoryErasure.InvalidPreflight);

        await AssertShownAsync(client, target, T);

        Assert.Equal(0, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Saga));
    }

    [SkippableFact]
    public async Task A_pinned_memory_is_erased_and_the_plan_says_it_was_pinned()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore());

        (HttpClient client, MemoryErasureRouteDriver driver) = Connect(factory);

        string target = await MemoryErasureRouteDriver.InsertSagaAsync(factory, T);

        using (HttpResponseMessage pinned = await client.PostAsync($"/api/memory/saga/{target}/pin", content: null))
        {
            Assert.Equal(HttpStatusCode.OK, pinned.StatusCode);
        }

        MemoryErasureRoundTrip<SagaEraseRequest> erased = await driver.EraseSagaAsync(target);

        Assert.True(erased.Preflight.Plan.Pinned);

        Assert.False(erased.Result.Replayed);

        await AssertShowRefusedAsync(client, target, HttpStatusCode.NotFound, ErrorCodes.Saga.NotFound);

        await AssertNoOrphanClaimsAsync(factory);
    }

    [SkippableFact]
    public async Task A_non_guid_memory_id_is_an_invalid_body()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore());

        (_, MemoryErasureRouteDriver driver) = Connect(factory);

        using HttpResponseMessage response = await PostPrepareAsync(
            driver,
            new SagaErasePrepareRequest("mem-not-a-guid", Hash(T), null, Guid.NewGuid()));

        await AssertRefusalAsync(response, HttpStatusCode.BadRequest, ErrorCodes.Validation.InvalidBody);
    }

    /// <summary>
    /// A legacy <c>vec0</c> mirror cannot be opened, so the erase skips it and records the residue it
    /// could not reach on its receipt, where it stays: the reason never upgrades, whatever the log does.
    /// </summary>
    [SkippableFact]
    public async Task A_legacy_vector_mirror_is_skipped_and_its_receipt_stays_unverified()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore());

        (_, MemoryErasureRouteDriver driver) = Connect(factory);

        string target = await MemoryErasureRouteDriver.InsertSagaAsync(factory, T);

        // The shipping runtime has no vec0 module, so an FTS5 table stands in for the legacy mirror:
        // it records the same CREATE VIRTUAL TABLE text, which is all that classifying a mirror reads.
        await ExecuteAsync(factory, "CREATE VIRTUAL TABLE saga_memory_embeddings_vec USING fts5(MemoryId, Embedding);");

        MemoryErasureRoundTrip<SagaEraseRequest> erased = await driver.EraseSagaAsync(target);

        Assert.Equal(MemoryLocalErasureOutcome.RowsRemovedScrubPending, erased.Result.Local.Outcome);

        Assert.Contains(MemoryErasureScrubPendingReason.VectorIndexScrubUnverified, erased.Result.Local.PendingReasons);

        MemoryErasureResultDto replayed = await driver.ApplySagaAsync(erased.Apply with { PreflightToken = "x" });

        Assert.True(replayed.Replayed);

        Assert.Equal(MemoryLocalErasureOutcome.RowsRemovedScrubPending, replayed.Local.Outcome);

        Assert.Contains(MemoryErasureScrubPendingReason.VectorIndexScrubUnverified, replayed.Local.PendingReasons);

        await AssertNoOrphanClaimsAsync(factory);
    }

    /// <summary>
    /// A rolled-back erase reports its failure and nothing else: no receipt, no fingerprint, no result,
    /// and no post-commit scrub, because the scrub runs only for an erase that committed.
    /// </summary>
    /// <remarks>
    /// A trigger stands in for a writer that re-creates an erased memory's embedding inside the erase's
    /// own transaction, after its deletes and before its commit. No production path can do that, which
    /// is exactly why the in-transaction absence proof has to be exercised this way.
    /// </remarks>
    [SkippableFact]
    public async Task An_erase_that_rolls_back_reports_no_result_and_never_scrubs()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        TestCapturingLogger<MemoryErasureScrubber> scrubLog = new();

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore());

        factory.ServiceOverrides += services =>
            services.AddSingleton<ILogger<MemoryErasureScrubber>>(scrubLog);

        (HttpClient client, MemoryErasureRouteDriver driver) = Connect(factory);

        string target = await MemoryErasureRouteDriver.InsertSagaAsync(factory, T);

        SagaErasePrepareRequest prepare = await PrepareRequestAsync(client, target, Guid.NewGuid());

        MemoryErasurePreflightDto preflight = await PrepareOkAsync(driver, prepare);

        await ExecuteAsync(
            factory,
            $"""
            CREATE TRIGGER test_erasure_resurrects_an_embedding AFTER INSERT ON memory_erasure_receipts
            BEGIN
                INSERT INTO saga_memory_embeddings (MemoryId, Embedding, Dim) VALUES ('{target}', zeroblob(8), 2);
            END;
            """);

        using HttpResponseMessage response = await driver.PostAsync(
            "/api/memory/saga/erase",
            Apply(prepare, preflight),
            ArcanumJsonContext.Default.SagaEraseRequest);

        await AssertRefusalAsync(response, HttpStatusCode.InternalServerError, ErrorCodes.MemoryErasure.ErasureIncomplete);

        Assert.Empty(scrubLog.Entries);

        Assert.Equal(0, await ReceiptsAsync(factory, prepare.MutationId));

        Assert.Equal(0, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Saga));

        await AssertShownAsync(client, target, T);

        await AssertNoOrphanClaimsAsync(factory);
    }

    [SkippableFact]
    public async Task Apply_after_token_expiry_replays_when_committed_and_refuses_when_not()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FakeTimeProvider clock = new();

        MemoryReviewTokenCodec codec = new(clock);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore());

        // Only the codec reads the fake clock. The host's own TimeProvider stays the system clock, so no
        // timer-driven hosted service ever sees time jump.
        factory.ServiceOverrides += services =>
        {
            services.RemoveAll<MemoryReviewTokenCodec>();

            services.RemoveAll<IMemoryReviewTokenCodec>();

            services.RemoveAll<IMemoryErasureTokenCodec>();

            services.AddSingleton(codec);

            services.AddSingleton<IMemoryReviewTokenCodec>(codec);

            services.AddSingleton<IMemoryErasureTokenCodec>(codec);
        };

        (HttpClient client, MemoryErasureRouteDriver driver) = Connect(factory);

        string uncommitted = await MemoryErasureRouteDriver.InsertSagaAsync(factory, T);

        SagaErasePrepareRequest prepare = await PrepareRequestAsync(client, uncommitted, Guid.NewGuid());

        MemoryErasurePreflightDto preflight = await PrepareOkAsync(driver, prepare);

        clock.Advance(TimeSpan.FromMinutes(5));

        await AssertApplyRefusedAsync(driver, Apply(prepare, preflight), HttpStatusCode.BadRequest, ErrorCodes.MemoryErasure.InvalidPreflight);

        await AssertShownAsync(client, uncommitted, T);

        string committed = await MemoryErasureRouteDriver.InsertSagaAsync(factory, Other);

        MemoryErasureRoundTrip<SagaEraseRequest> erased = await driver.EraseSagaAsync(committed);

        clock.Advance(TimeSpan.FromMinutes(6));

        MemoryErasureResultDto replayed = await driver.ApplySagaAsync(erased.Apply);

        Assert.True(replayed.Replayed);

        AssertSameErase(erased.Result, replayed);

        await AssertNoOrphanClaimsAsync(factory);
    }

    /// <summary>
    /// A committed erase answers its replay by receipt on a restarted host.
    /// </summary>
    /// <remarks>
    /// The token codec's key is process-static, so an in-process restart cannot invalidate a token; the
    /// replay deliberately sends none. The uncommitted half of the rule is the expiry test above and the
    /// borrowed-token test.
    /// </remarks>
    [SkippableFact]
    public async Task Apply_after_a_host_restart_replays_by_receipt()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        await using RestartableArcanumProfileFixture profile = new();

        string target;

        MemoryErasureRoundTrip<SagaEraseRequest> erased;

        await using (ArcanumWebApplicationFactory first = MemoryErasureRouteDriver.Host(credentials, profile))
        {
            (_, MemoryErasureRouteDriver driver) = Connect(first);

            target = await MemoryErasureRouteDriver.InsertSagaAsync(first, T);

            erased = await driver.EraseSagaAsync(target);
        }

        await using ArcanumWebApplicationFactory second = MemoryErasureRouteDriver.Host(credentials, profile);

        (HttpClient client, MemoryErasureRouteDriver restarted) = Connect(second);

        MemoryErasureResultDto replayed = await restarted.ApplySagaAsync(erased.Apply with { PreflightToken = "x" });

        Assert.True(replayed.Replayed);

        AssertSameErase(erased.Result, replayed);

        await AssertShowRefusedAsync(client, target, HttpStatusCode.NotFound, ErrorCodes.Saga.NotFound);

        Assert.Equal(1, await MemoryErasureRouteDriver.FingerprintCountAsync(second, MemoryReviewStore.Saga));

        await AssertNoOrphanClaimsAsync(second);
    }

    [SkippableFact]
    public async Task A_busy_checkpoint_leaves_the_receipt_pending_and_a_replay_verifies_it()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore());

        (HttpClient client, MemoryErasureRouteDriver driver) = Connect(factory);

        string target = await MemoryErasureRouteDriver.InsertSagaAsync(factory, T);

        SagaErasePrepareRequest prepare = await PrepareRequestAsync(client, target, Guid.NewGuid());

        SagaEraseRequest apply = Apply(prepare, await PrepareOkAsync(driver, prepare));

        MemoryErasureResultDto pending;

        await using (IAsyncDisposable reader = await MemoryErasureRouteDriver.HoldReaderAsync(factory))
        {
            pending = await driver.ApplySagaAsync(apply);
        }

        Assert.False(pending.Replayed);

        Assert.Equal(MemoryLocalErasureOutcome.RowsRemovedScrubPending, pending.Local.Outcome);

        Assert.Equal([MemoryErasureScrubPendingReason.WalCheckpointPending], pending.Local.PendingReasons);

        Assert.Equal(MemoryErasureWalCheckpointAttempt.Busy, pending.Local.WalCheckpointAttempt);

        MemoryErasureResultDto verified = await driver.ApplySagaAsync(apply);

        Assert.True(verified.Replayed);

        Assert.Equal(MemoryLocalErasureOutcome.Verified, verified.Local.Outcome);

        Assert.Empty(verified.Local.PendingReasons);

        Assert.Equal(MemoryErasureWalCheckpointAttempt.Truncated, verified.Local.WalCheckpointAttempt);

        await AssertNoOrphanClaimsAsync(factory);
    }

    [SkippableFact]
    public async Task Hundreds_of_twins_are_erased_in_one_receipt_with_exact_counts()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        const int twins = 300;

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore());

        (HttpClient client, MemoryErasureRouteDriver driver) = Connect(factory);

        (Guid campaign, Guid session) = await BoundSessionAsync(factory, client, "twins");

        List<string> ids = [];

        for (int index = 0; index < twins; index++)
        {
            ids.Add(await InsertTwinAsync(factory, campaign, T));
        }

        string lone = await MemoryErasureRouteDriver.InsertSagaAsync(factory, Other, session);

        MemoryErasurePreflightDto lonePlan = await PrepareOkAsync(
            driver,
            await PrepareRequestAsync(client, lone, Guid.NewGuid()));

        long perMemory = lonePlan.Plan.RowsToRemove;

        Assert.True(perMemory > 0);

        long fingerprintsBefore = await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Saga);

        SagaErasePrepareRequest prepare = await PrepareRequestAsync(client, ids[twins / 2], Guid.NewGuid());

        MemoryErasurePreflightDto preflight = await PrepareOkAsync(driver, prepare);

        Assert.Equal(twins, preflight.Plan.ErasedItemCount);

        Assert.Equal(twins * perMemory, preflight.Plan.RowsToRemove);

        MemoryErasureResultDto result = await driver.ApplySagaAsync(Apply(prepare, preflight));

        Assert.Equal(twins, result.Local.ErasedItemCount);

        Assert.Equal(twins * perMemory, result.Local.RemovedRowCount);

        Assert.Equal(twins, await ReceiptSubjectsAsync(factory, prepare.MutationId));

        Assert.Equal(1, await ReceiptsAsync(factory, prepare.MutationId));

        Assert.Equal(fingerprintsBefore + 1, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Saga));

        Assert.Equal(0, await ContentRowsAsync(factory, T));

        await AssertShownAsync(client, lone, Other);

        await AssertNoOrphanClaimsAsync(factory);
    }

    /// <summary>
    /// A labelled memory takes the owner's write lease before its transaction, and an erase that finds
    /// its subject already erased by another mutation must still answer as the transaction would: 410,
    /// not a stale plan.
    /// </summary>
    [SkippableFact]
    public async Task A_second_mutation_on_a_labelled_memory_answers_410_at_apply()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(
            new InMemoryOsCredentialStore(),
            covenant: true);

        (HttpClient client, MemoryErasureRouteDriver driver) = Connect(factory);

        (Guid campaignA, Guid sessionA) = await BoundSessionAsync(factory, client, "a");

        string target = await MemoryErasureRouteDriver.InsertSagaAsync(factory, T, sessionA);

        await LabelAsync(factory, target, sessionA, campaignA);

        SagaErasePrepareRequest first = await PrepareRequestAsync(client, target, Guid.NewGuid());

        SagaErasePrepareRequest second = first with { MutationId = Guid.NewGuid() };

        MemoryErasurePreflightDto firstPlan = await PrepareOkAsync(driver, first);

        MemoryErasurePreflightDto secondPlan = await PrepareOkAsync(driver, second);

        Assert.Equal(1, firstPlan.Plan.LabelsToRemove);

        MemoryErasureResultDto applied = await driver.ApplySagaAsync(Apply(first, firstPlan));

        Assert.False(applied.Replayed);

        await AssertApplyRefusedAsync(driver, Apply(second, secondPlan), HttpStatusCode.Gone, ErrorCodes.MemoryErasure.SubjectErased);

        Assert.Equal(0, await ReceiptSubjectsAsync(factory, second.MutationId));

        await AssertNoOrphanClaimsAsync(factory);
    }

    /// <summary>
    /// Two applies of one labelled mutation race: the retry finds no receipt before the original
    /// commits, and must replay the original's receipt rather than refuse the erase it asked for.
    /// </summary>
    /// <remarks>
    /// The seam runs the original apply to completion between the retry's receipt probe and everything
    /// after it, which is the one interleaving that matters and the one a plain concurrent pair reaches
    /// only by chance.
    /// </remarks>
    [SkippableFact]
    public async Task A_labelled_retry_that_misses_the_receipt_replays_the_committed_erase()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        MemoryErasureRouteDriver? inner = null;

        SagaEraseRequest? original = null;

        MemoryErasureResultDto? committed = null;

        int fired = 0;

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(
            new InMemoryOsCredentialStore(),
            covenant: true);

        UseSeamedService(
            factory,
            afterReceiptProbe: async cancellationToken =>
            {
                if (Interlocked.Exchange(ref fired, 1) == 0)
                {
                    committed = await inner!.ApplySagaAsync(original!, cancellationToken);
                }
            });

        (HttpClient client, MemoryErasureRouteDriver driver) = Connect(factory);

        inner = new MemoryErasureRouteDriver(factory.CreateAuthenticatedClient());

        (Guid campaignA, Guid sessionA) = await BoundSessionAsync(factory, client, "a");

        string target = await MemoryErasureRouteDriver.InsertSagaAsync(factory, T, sessionA);

        await LabelAsync(factory, target, sessionA, campaignA);

        SagaErasePrepareRequest prepare = await PrepareRequestAsync(client, target, Guid.NewGuid());

        original = Apply(prepare, await PrepareOkAsync(driver, prepare));

        MemoryErasureResultDto retried = await driver.ApplySagaAsync(original);

        Assert.NotNull(committed);

        Assert.False(committed!.Replayed);

        Assert.True(retried.Replayed);

        AssertSameErase(committed, retried);

        Assert.Equal(1, await ReceiptsAsync(factory, prepare.MutationId));

        await AssertNoOrphanClaimsAsync(factory);
    }

    /// <summary>
    /// A <c>COMMIT</c> that fails after its frame persisted is an erase that happened: the receipt is
    /// read back on a connection of its own and the committed erase is reported.
    /// </summary>
    [SkippableFact]
    public async Task A_commit_that_fails_after_persisting_reports_the_committed_erase()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore());

        UseSeamedService(
            factory,
            commit: static async (transaction, cancellationToken) =>
            {
                await transaction.CommitAsync(cancellationToken);

                throw new SqliteException("disk I/O error", 10);
            });

        (HttpClient client, MemoryErasureRouteDriver driver) = Connect(factory);

        string target = await MemoryErasureRouteDriver.InsertSagaAsync(factory, T);

        SagaErasePrepareRequest prepare = await PrepareRequestAsync(client, target, Guid.NewGuid());

        MemoryErasurePreflightDto preflight = await PrepareOkAsync(driver, prepare);

        MemoryErasureResultDto result = await driver.ApplySagaAsync(Apply(prepare, preflight));

        Assert.False(result.Replayed);

        Assert.Equal(prepare.MutationId, result.MutationId);

        Assert.Equal(preflight.EffectDigest, result.EffectDigest);

        Assert.Equal(preflight.Plan.RowsToRemove, result.Local.RemovedRowCount);

        await AssertShowRefusedAsync(client, target, HttpStatusCode.NotFound, ErrorCodes.Saga.NotFound);

        Assert.Equal(1, await ReceiptsAsync(factory, prepare.MutationId));

        Assert.Equal(1, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Saga));

        await AssertNoOrphanClaimsAsync(factory);
    }

    /// <summary>
    /// A <c>COMMIT</c> that persisted through a statement of its own and then reported a failure leaves the
    /// transaction object believing it is still open, so its rollback on disposal fails too. That failure
    /// must not replace the commit's: the receipt is still read back on a connection of its own and the
    /// committed erase is reported.
    /// </summary>
    [SkippableFact]
    public async Task A_commit_that_persisted_while_the_transaction_still_believes_it_is_open_reports_the_committed_erase()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore());

        UseSeamedService(
            factory,
            commit: static async (transaction, cancellationToken) =>
            {
                await using (SqliteCommand commit = transaction.Connection!.CreateCommand())
                {
                    commit.Transaction = transaction;

                    commit.CommandText = "COMMIT;";

                    _ = await commit.ExecuteNonQueryAsync(cancellationToken);
                }

                throw new SqliteException("disk I/O error", 10);
            });

        (HttpClient client, MemoryErasureRouteDriver driver) = Connect(factory);

        string target = await MemoryErasureRouteDriver.InsertSagaAsync(factory, T);

        SagaErasePrepareRequest prepare = await PrepareRequestAsync(client, target, Guid.NewGuid());

        MemoryErasurePreflightDto preflight = await PrepareOkAsync(driver, prepare);

        MemoryErasureResultDto result = await driver.ApplySagaAsync(Apply(prepare, preflight));

        Assert.False(result.Replayed);

        Assert.Equal(prepare.MutationId, result.MutationId);

        Assert.Equal(preflight.EffectDigest, result.EffectDigest);

        Assert.Equal(preflight.Plan.RowsToRemove, result.Local.RemovedRowCount);

        await AssertShowRefusedAsync(client, target, HttpStatusCode.NotFound, ErrorCodes.Saga.NotFound);

        Assert.Equal(1, await ReceiptsAsync(factory, prepare.MutationId));

        Assert.Equal(1, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Saga));

        await AssertNoOrphanClaimsAsync(factory);
    }

    /// <summary>
    /// A <c>COMMIT</c> that fails before anything persisted reports the failure and no result: the
    /// receipt is absent on a fresh connection, so nothing is said to have been erased.
    /// </summary>
    [SkippableFact]
    public async Task A_commit_that_fails_without_persisting_reports_the_failure_and_no_result()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        TestCapturingLogger<MemoryErasureScrubber> scrubLog = new();

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore());

        factory.ServiceOverrides += services =>
            services.AddSingleton<ILogger<MemoryErasureScrubber>>(scrubLog);

        UseSeamedService(
            factory,
            commit: static (_, _) => throw new SqliteException("disk I/O error", 10));

        (HttpClient client, MemoryErasureRouteDriver driver) = Connect(factory);

        string target = await MemoryErasureRouteDriver.InsertSagaAsync(factory, T);

        SagaErasePrepareRequest prepare = await PrepareRequestAsync(client, target, Guid.NewGuid());

        MemoryErasurePreflightDto preflight = await PrepareOkAsync(driver, prepare);

        using HttpResponseMessage response = await driver.PostAsync(
            "/api/memory/saga/erase",
            Apply(prepare, preflight),
            ArcanumJsonContext.Default.SagaEraseRequest);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        await AssertNoResultAsync(response);

        Assert.Empty(scrubLog.Entries);

        await AssertShownAsync(client, target, T);

        Assert.Equal(0, await ReceiptsAsync(factory, prepare.MutationId));

        Assert.Equal(0, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Saga));

        await AssertNoOrphanClaimsAsync(factory);
    }

    /// <summary>
    /// A <c>COMMIT</c> that SQLite answers busy left nothing persisted and is not an uncertain outcome:
    /// the busy retry rolls the attempt back and runs the erase again, which commits once.
    /// </summary>
    [SkippableFact]
    public async Task A_busy_commit_is_retried_and_commits_the_erase_once()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore());

        int commits = 0;

        UseSeamedService(
            factory,
            commit: async (transaction, cancellationToken) =>
            {
                if (Interlocked.Increment(ref commits) == 1)
                {
                    throw new SqliteException("database is locked", 5);
                }

                await transaction.CommitAsync(cancellationToken);
            });

        (HttpClient client, MemoryErasureRouteDriver driver) = Connect(factory);

        string target = await MemoryErasureRouteDriver.InsertSagaAsync(factory, T);

        SagaErasePrepareRequest prepare = await PrepareRequestAsync(client, target, Guid.NewGuid());

        MemoryErasurePreflightDto preflight = await PrepareOkAsync(driver, prepare);

        MemoryErasureResultDto result = await driver.ApplySagaAsync(Apply(prepare, preflight));

        Assert.Equal(2, Volatile.Read(ref commits));

        Assert.False(result.Replayed);

        Assert.Equal(prepare.MutationId, result.MutationId);

        Assert.Equal(preflight.EffectDigest, result.EffectDigest);

        Assert.Equal(preflight.Plan.RowsToRemove, result.Local.RemovedRowCount);

        await AssertShowRefusedAsync(client, target, HttpStatusCode.NotFound, ErrorCodes.Saga.NotFound);

        Assert.Equal(1, await ReceiptsAsync(factory, prepare.MutationId));

        Assert.Equal(1, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Saga));

        await AssertNoOrphanClaimsAsync(factory);
    }

    /// <summary>
    /// The twin seek binds the Campaign in every spelling a writer has produced, and each one is erased.
    /// </summary>
    /// <remarks>
    /// The version-5 guard now admits only the canonical spelling, so the three legacy spellings can
    /// exist only on rows written before its sweep reached them. The guard is lifted for the one
    /// rewrite that recreates each, and put back from the shipped schema file.
    /// </remarks>
    [SkippableFact]
    public async Task Every_campaign_spelling_a_writer_has_produced_is_in_the_twin_class()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore());

        (HttpClient client, MemoryErasureRouteDriver driver) = Connect(factory);

        (Guid campaignA, Guid sessionA) = await BoundSessionAsync(factory, client, "a");

        string target = await MemoryErasureRouteDriver.InsertSagaAsync(factory, T, sessionA);

        string lowerDashed = await InsertTwinAsync(factory, campaignA, T);

        string upperUndashed = await InsertTwinAsync(factory, campaignA, T);

        string lowerUndashed = await InsertTwinAsync(factory, campaignA, T);

        await RewriteCampaignSpellingAsync(factory, lowerDashed, campaignA.ToString("D"));

        await RewriteCampaignSpellingAsync(factory, upperUndashed, campaignA.ToString("N").ToUpperInvariant());

        await RewriteCampaignSpellingAsync(factory, lowerUndashed, campaignA.ToString("N"));

        MemoryErasureRoundTrip<SagaEraseRequest> erased = await driver.EraseSagaAsync(target);

        Assert.Equal(4, erased.Preflight.Plan.ErasedItemCount);

        Assert.Equal(4, erased.Result.Local.ErasedItemCount);

        foreach (string id in (string[])[target, lowerDashed, upperUndashed, lowerUndashed])
        {
            await AssertShowRefusedAsync(client, id, HttpStatusCode.NotFound, ErrorCodes.Saga.NotFound);
        }

        await AssertNoOrphanClaimsAsync(factory);
    }

    /// <summary>
    /// A twin stored under a Campaign spelling the seek does not bind would be suppressed by the
    /// fingerprint and left live by the delete, so the erase refuses and removes nothing.
    /// </summary>
    /// <remarks>
    /// No writer has ever produced a mixed-case Campaign, and the version-5 guard refuses one, so the
    /// guard is lifted to plant it. The cross-check that catches it counts the scope with the
    /// normalised comparison, which reads every spelling. The planted spelling is asserted to be none
    /// of the four the seek binds, so the test cannot pass or fail because the seek found the twin.
    /// </remarks>
    [SkippableFact]
    public async Task A_twin_under_a_campaign_spelling_the_seek_misses_fails_closed()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore());

        (HttpClient client, MemoryErasureRouteDriver driver) = Connect(factory);

        (Guid campaignA, Guid sessionA, string mixedCase) = await MixedCaseCampaignAsync(factory, client);

        string target = await MemoryErasureRouteDriver.InsertSagaAsync(factory, T, sessionA);

        string hidden = await InsertTwinAsync(factory, campaignA, T);

        Assert.Equal(campaignA, Guid.Parse(mixedCase));

        Assert.DoesNotContain(
            mixedCase,
            (string[])
            [
                campaignA.ToString("D").ToUpperInvariant(),
                campaignA.ToString("D"),
                campaignA.ToString("N").ToUpperInvariant(),
                campaignA.ToString("N"),
            ]);

        await RewriteCampaignSpellingAsync(factory, hidden, mixedCase);

        SagaErasePrepareRequest prepare = await PrepareRequestAsync(client, target, Guid.NewGuid());

        using HttpResponseMessage response = await PostPrepareAsync(driver, prepare);

        await AssertRefusalAsync(response, HttpStatusCode.InternalServerError, ErrorCodes.MemoryErasure.ErasureIncomplete);

        Assert.Equal(2, await ContentRowsAsync(factory, T));

        Assert.Equal(0, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Saga));
    }

    /// <summary>
    /// A member whose normalised id another row shares would take that row with it, so the erase
    /// refuses and removes nothing.
    /// </summary>
    /// <remarks>
    /// Every Saga id is minted by <c>Guid.NewGuid().ToString()</c>, so no writer stores two ids that
    /// differ only in case; the colliding row is planted directly to stand in for one.
    /// </remarks>
    [SkippableFact]
    public async Task A_twin_whose_id_another_row_shares_fails_closed()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore());

        (HttpClient client, MemoryErasureRouteDriver driver) = Connect(factory);

        (Guid campaignA, Guid sessionA) = await BoundSessionAsync(factory, client, "a");

        string target = await MemoryErasureRouteDriver.InsertSagaAsync(factory, T, sessionA);

        string twin = await InsertTwinAsync(factory, campaignA, T);

        await ExecuteAsync(
            factory,
            "INSERT INTO saga_memories (Id, Content, CreatedAt, ScopeKindCode) VALUES ($id, $content, $createdAt, 1);",
            [("$id", twin.ToUpperInvariant()), ("$content", Other), ("$createdAt", UtcInstantText.Format(DateTimeOffset.UtcNow))]);

        SagaErasePrepareRequest prepare = await PrepareRequestAsync(client, target, Guid.NewGuid());

        using HttpResponseMessage response = await PostPrepareAsync(driver, prepare);

        await AssertRefusalAsync(response, HttpStatusCode.InternalServerError, ErrorCodes.MemoryErasure.ErasureIncomplete);

        Assert.Equal(2, await ContentRowsAsync(factory, T));

        Assert.Equal(1, await ContentRowsAsync(factory, Other));
    }

    /// <summary>
    /// The plan's mirror, attachment-provenance and review rows are measured, removed, and counted
    /// exactly, each through its own production writer.
    /// </summary>
    [SkippableFact]
    public async Task An_erase_removes_the_mirror_provenance_and_review_rows_it_measured()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore());

        (HttpClient client, MemoryErasureRouteDriver driver) = Connect(factory);

        (Guid campaignA, Guid sessionA) = await BoundSessionAsync(factory, client, "a");

        // No schema file installs the mirror; an accelerator builds it. A plain table stands in for a
        // mirror this runtime can write, and The Weave is told it is there so the insert fills it.
        await ExecuteAsync(
            factory,
            """CREATE TABLE "saga_memory_embeddings_vec" ("MemoryId" TEXT PRIMARY KEY, "Embedding" BLOB NOT NULL);""");

        factory.Services.GetRequiredService<WeaveIndexAvailability>().SetAvailable(true, "Test mirror present.");

        string target = Guid.NewGuid().ToString();

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            SagaMemoryWriteOutcome written = await scope.ServiceProvider.GetRequiredService<ISagaMemoryStore>().InsertAsync(
                target,
                T,
                DateTimeOffset.UtcNow,
                sessionA,
                tags: null,
                source: "attachment-extraction",
                Vector(),
                new AttachmentMemoryProvenance(
                    sessionA,
                    Guid.NewGuid(),
                    "logical-key",
                    1,
                    "attachment-hash",
                    DateTimeOffset.UtcNow,
                    "note",
                    AttachmentSourceAvailability.Available),
                CancellationToken.None);

            Assert.Equal(SagaMemoryWriteOutcome.Written, written);
        }

        await ConfirmReviewAsync(client, campaignA, target);

        string[] ids = [target];

        Assert.Equal(1, await CountForIdsAsync(factory, "SELECT count(*) FROM saga_memory_embeddings_vec WHERE MemoryId IN ({0})", ids));

        Assert.Equal(1, await CountForIdsAsync(factory, "SELECT count(*) FROM saga_memory_attachment_provenance WHERE MemoryId IN ({0})", ids));

        Assert.True(await CountForIdsAsync(factory, ReviewEvents, ids) > 0);

        Assert.True(await CountForIdsAsync(factory, DecisionReceipts, ids) > 0);

        long before = await PlanRowsAsync(factory, ids);

        MemoryErasureRoundTrip<SagaEraseRequest> erased = await driver.EraseSagaAsync(target);

        Assert.Equal(before, erased.Preflight.Plan.RowsToRemove);

        Assert.Equal(before, erased.Result.Local.RemovedRowCount);

        Assert.Equal(0, await CountForIdsAsync(factory, "SELECT count(*) FROM saga_memory_embeddings_vec WHERE MemoryId IN ({0})", ids));

        Assert.Equal(0, await CountForIdsAsync(factory, "SELECT count(*) FROM saga_memory_attachment_provenance WHERE MemoryId IN ({0})", ids));

        Assert.Equal(0, await CountForIdsAsync(factory, ReviewEvents, ids));

        Assert.Equal(0, await CountForIdsAsync(factory, DecisionReceipts, ids));

        Assert.Equal(0, await PlanRowsAsync(factory, ids, requireRows: false));

        await AssertNoOrphanClaimsAsync(factory);
    }

    /// <summary>
    /// Every Session without a resolved binding shares one unresolved scope, so the erased class spans
    /// them, and the preflight says the fingerprint stops matching once a binding is resolved.
    /// </summary>
    [SkippableFact]
    public async Task A_legacy_unresolved_memory_is_erased_across_every_unbound_session()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore());

        (HttpClient client, MemoryErasureRouteDriver driver) = Connect(factory);

        Guid firstSession = await UnboundSessionAsync(client, "first");

        Guid secondSession = await UnboundSessionAsync(client, "second");

        string target = await MemoryErasureRouteDriver.InsertSagaAsync(factory, T, firstSession);

        string twin = await MemoryErasureRouteDriver.InsertSagaAsync(factory, T, secondSession);

        MemoryErasureRoundTrip<SagaEraseRequest> erased = await driver.EraseSagaAsync(target);

        Assert.Equal(2, erased.Preflight.Plan.ErasedItemCount);

        Assert.Equal(
            [MemoryErasureNote.UnresolvedScopeStopsMatchingOnResolution, MemoryErasureNote.OtherScopesUnaffected],
            erased.Preflight.Notes);

        Assert.Equal([MemoryErasureNote.OtherScopesUnaffected], erased.Result.Notes);

        await AssertShowRefusedAsync(client, target, HttpStatusCode.NotFound, ErrorCodes.Saga.NotFound);

        await AssertShowRefusedAsync(client, twin, HttpStatusCode.NotFound, ErrorCodes.Saga.NotFound);

        await AssertNoOrphanClaimsAsync(factory);
    }

    /// <summary>
    /// The fingerprint refuses the erased text in the erased scope and nowhere else: not in another
    /// Campaign, not in Global, and not with a byte of difference.
    /// </summary>
    [SkippableFact]
    public async Task The_fingerprint_refuses_the_text_only_in_the_erased_scope()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore());

        (HttpClient client, MemoryErasureRouteDriver driver) = Connect(factory);

        (_, Guid sessionA) = await BoundSessionAsync(factory, client, "a");

        (_, Guid sessionB) = await BoundSessionAsync(factory, client, "b");

        string target = await MemoryErasureRouteDriver.InsertSagaAsync(factory, T, sessionA);

        _ = await driver.EraseSagaAsync(target);

        Assert.Equal(SagaMemoryWriteOutcome.Suppressed, await MemoryErasureRouteDriver.InsertSagaOutcomeAsync(factory, T, sessionA));

        Assert.Equal(SagaMemoryWriteOutcome.Written, await MemoryErasureRouteDriver.InsertSagaOutcomeAsync(factory, T, sessionB));

        Assert.Equal(SagaMemoryWriteOutcome.Written, await MemoryErasureRouteDriver.InsertSagaOutcomeAsync(factory, T));

        Assert.Equal(SagaMemoryWriteOutcome.Written, await MemoryErasureRouteDriver.InsertSagaOutcomeAsync(factory, T + " ", sessionA));

        await AssertNoOrphanClaimsAsync(factory);
    }

    private const string ReviewEvents =
        "SELECT count(*) FROM annal_review_events WHERE SubjectStoreCode = 1 AND SubjectId IN ({0})";

    private const string DecisionReceipts =
        "SELECT count(*) FROM annal_review_decision_receipts WHERE ReviewEventSequence IN "
        + "(SELECT Sequence FROM annal_review_events WHERE SubjectStoreCode = 1 AND SubjectId IN ({0}))";

    /// <summary>
    /// A corrected memory's claim is a chain: its later version names the first as its predecessor, and
    /// goes with it by cascade. The erase still reports every row it removed, the receipt records the
    /// same count, and a replay reports it again.
    /// </summary>
    [SkippableFact]
    public async Task A_corrected_memory_reports_every_removed_row()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore());

        (HttpClient client, MemoryErasureRouteDriver driver) = Connect(factory);

        string target = await MemoryErasureRouteDriver.InsertSagaAsync(factory, T);

        using (HttpResponseMessage corrected = await client.PostAsync(
            $"/api/memory/saga/{target}/correct",
            JsonContent.Create(new SagaCorrectRequest(Hash(T), Other), ArcanumJsonContext.Default.SagaCorrectRequest)))
        {
            Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        }

        Assert.Equal(
            2,
            await CountForIdsAsync(
                factory,
                "SELECT count(*) FROM annal_versions WHERE ClaimId IN (SELECT ClaimId FROM annal_claims WHERE SubjectStoreCode = 1 AND SubjectId IN ({0}))",
                [target]));

        long before = await PlanRowsAsync(factory, [target]);

        MemoryErasureRoundTrip<SagaEraseRequest> erased = await driver.EraseSagaAsync(target);

        Assert.Equal(before, erased.Preflight.Plan.RowsToRemove);

        Assert.Equal(erased.Preflight.Plan.RowsToRemove, erased.Result.Local.RemovedRowCount);

        Assert.Equal(
            erased.Result.Local.RemovedRowCount,
            await ScalarAsync(
                factory,
                "SELECT RemovedRowCount FROM memory_erasure_receipts WHERE MutationId = $mutation;",
                [("$mutation", erased.Apply.MutationId.ToString("D").ToUpperInvariant())]));

        MemoryErasureResultDto replayed = await driver.ApplySagaAsync(erased.Apply with { PreflightToken = "x" });

        Assert.True(replayed.Replayed);

        Assert.Equal(erased.Result.Local.RemovedRowCount, replayed.Local.RemovedRowCount);

        Assert.Equal(0, await PlanRowsAsync(factory, [target], requireRows: false));

        await AssertNoOrphanClaimsAsync(factory);
    }

    /// <summary>
    /// Replaces the host's erase service with the production one carrying test seams, built from the
    /// host's own registrations.
    /// </summary>
    private static void UseSeamedService(
        ArcanumWebApplicationFactory factory,
        Func<SqliteTransaction, CancellationToken, Task>? commit = null,
        Func<CancellationToken, Task>? afterReceiptProbe = null) =>
        factory.ServiceOverrides += services => services.AddScoped<ISagaMemoryErasureService>(provider =>
            new SagaMemoryErasureService(
                provider.GetRequiredService<ArcanumDbContext>(),
                provider.GetRequiredService<IMemoryErasureKeyCreator>(),
                provider.GetRequiredService<IMemoryErasureKeyProvider>(),
                provider.GetRequiredService<IMemoryErasureTokenCodec>(),
                provider.GetRequiredService<MemoryErasureScrubber>(),
                provider.GetRequiredService<ICovenantOperationGate>(),
                provider.GetRequiredService<IOperatorAuthorityContextIssuer>(),
                provider.GetRequiredService<ICovenantSqliteConnectionInitializer>(),
                provider.GetRequiredService<IOptionsMonitor<ArcanumSettings>>())
            {
                CommitForTesting = commit,
                AfterReceiptProbeForTesting = afterReceiptProbe,
            });

    /// <summary>A Session created through its route, which binds it to no Campaign until a turn begins.</summary>
    private static async Task<Guid> UnboundSessionAsync(HttpClient client, string suffix)
    {
        using HttpResponseMessage created = await client.PostAsync(
            "/api/sessions",
            JsonContent.Create(new CreateSessionRequest(null, $"Unbound {suffix}"), ArcanumJsonContext.Default.CreateSessionRequest));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        return (await MemoryErasureRouteDriver.ReadDataAsync(created, ArcanumJsonContext.Default.ApiResponseSessionDetailDto)).Id;
    }

    /// <summary>Confirms one memory's current version through the Saga review routes, which records a decision.</summary>
    private static async Task ConfirmReviewAsync(HttpClient client, Guid campaignId, string memoryId)
    {
        using HttpResponseMessage listed = await client.PostAsync(
            "/api/memory/saga/review/list",
            JsonContent.Create(
                new SagaReviewListRequest(SagaMemoryScopeKind.Campaign, campaignId, 50, null),
                ArcanumJsonContext.Default.SagaReviewListRequest));

        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);

        SagaReviewPageDto page = await MemoryErasureRouteDriver.ReadDataAsync(listed, ArcanumJsonContext.Default.ApiResponseSagaReviewPageDto);

        SagaReviewItemDto item = Assert.Single(
            page.Items,
            candidate => string.Equals(candidate.SubjectId, memoryId, StringComparison.OrdinalIgnoreCase));

        SagaReviewBulkPrepareRequest prepare = new(
            Guid.NewGuid(),
            SagaMemoryScopeKind.Campaign,
            campaignId,
            MemoryReviewAction.Confirm,
            [new SagaReviewDecision(item.ObservationToken, null)]);

        using HttpResponseMessage prepared = await client.PostAsync(
            "/api/memory/saga/review/prepare",
            JsonContent.Create(prepare, ArcanumJsonContext.Default.SagaReviewBulkPrepareRequest));

        Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);

        MemoryReviewBulkPlanDto plan = await MemoryErasureRouteDriver.ReadDataAsync(
            prepared,
            ArcanumJsonContext.Default.ApiResponseMemoryReviewBulkPlanDto);

        using HttpResponseMessage applied = await client.PostAsync(
            "/api/memory/saga/review/apply",
            JsonContent.Create(
                new SagaReviewBulkApplyRequest(prepare, plan.PreparedPlanToken),
                ArcanumJsonContext.Default.SagaReviewBulkApplyRequest));

        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
    }

    /// <summary>
    /// Rewrites one memory's stored Campaign to a spelling the version-5 guard would refuse, standing
    /// in for a row written before that guard's sweep reached it.
    /// </summary>
    private static async Task RewriteCampaignSpellingAsync(ArcanumWebApplicationFactory factory, string memoryId, string spelling)
    {
        const string guard = "saga_memories_CampaignId_guard_identity_update";

        await ExecuteAsync(factory, $"DROP TRIGGER {guard};");

        await ExecuteAsync(
            factory,
            "UPDATE saga_memories SET CampaignId = $campaign WHERE Id = $id;",
            [("$campaign", spelling), ("$id", memoryId)]);

        await ExecuteAsync(
            factory,
            File.ReadAllText(Path.Combine(
                NativeSqlCipherTestPaths.RepositoryRoot(),
                "src",
                "RetroDownfall.Arcanum.Infrastructure",
                "Data",
                "Schema",
                "Triggers",
                guard + ".sql")));

        Assert.Equal(1, await ScalarAsync(factory, "SELECT count(*) FROM sqlite_master WHERE type = 'trigger' AND name = $name;", [("$name", guard)]));
    }

    private static float[] Vector()
    {
        float[] vector = new float[MemoryErasureRouteDriver.Dimensions];

        vector[0] = 1f;

        return vector;
    }

    private static (HttpClient Client, MemoryErasureRouteDriver Driver) Connect(ArcanumWebApplicationFactory factory)
    {
        HttpClient client = factory.CreateAuthenticatedClient();

        return (client, new MemoryErasureRouteDriver(client));
    }

    /// <summary>
    /// Registers one Campaign through its route, then creates one Session bound to it through the
    /// turn-begin store, the writer that records a Session's Campaign binding.
    /// </summary>
    private static async Task<(Guid Campaign, Guid Session)> BoundSessionAsync(
        ArcanumWebApplicationFactory factory,
        HttpClient client,
        string suffix)
    {
        string path = Path.Combine(factory.TempHome, $"erasure-campaign-{suffix}");

        Directory.CreateDirectory(path);

        using HttpResponseMessage registered = await client.PostAsync(
            "/api/campaigns",
            JsonContent.Create(
                new RegisterCampaignRequest($"Erasure {suffix}", path, WorkspaceType.Campaign, null),
                ArcanumJsonContext.Default.RegisterCampaignRequest));

        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);

        CampaignDto campaign = await MemoryErasureRouteDriver.ReadDataAsync(
            registered,
            ArcanumJsonContext.Default.ApiResponseCampaignDto);

        return (campaign.Id, await SessionBoundToAsync(factory, campaign.Id, $"Erasure session {suffix}"));
    }

    private static async Task<Guid> SessionBoundToAsync(
        ArcanumWebApplicationFactory factory,
        Guid campaignId,
        string title)
    {
        using IServiceScope scope = factory.Services.CreateScope();

        Result<Guid> session = await scope.ServiceProvider.GetRequiredService<ISessionTurnBeginStore>().CreateBoundSessionAsync(
            CanonicalCampaignContext.Create(
                SessionCampaignBinding.ForCampaign(campaignId),
                campaignAvailabilityGeneration: 1,
                pathIdentityPolicyVersion: 1,
                pathIdentityRevision: null,
                rootIdentityDigest: null),
            title,
            CancellationToken.None);

        Assert.True(session.IsSuccess, session.IsFailure ? session.Error.Message : null);

        return session.Value;
    }

    /// <summary>
    /// Writes one more memory of <paramref name="content"/> into the Campaign's scope through the store's
    /// own insert, from a Session of its own bound to that Campaign.
    /// </summary>
    /// <remarks>
    /// The insert answers <c>AlreadyPresent</c> for content its owning Session already holds in that scope,
    /// so a Session cannot hold a twin of its own memory; the same conclusion reached in another Session of
    /// the Campaign is the twin an erase has to find. Every twin comes from a fresh Session for that reason.
    /// </remarks>
    private static async Task<string> InsertTwinAsync(
        ArcanumWebApplicationFactory factory,
        Guid campaignId,
        string content)
    {
        Guid session = await SessionBoundToAsync(factory, campaignId, $"Erasure twin {Guid.NewGuid():N}");

        return await MemoryErasureRouteDriver.InsertSagaAsync(factory, content, session);
    }

    /// <summary>
    /// A bound Campaign whose id has a <see cref="MixedCaseSpelling"/>, registering another when a
    /// random id has none.
    /// </summary>
    private static async Task<(Guid Campaign, Guid Session, string MixedCase)> MixedCaseCampaignAsync(
        ArcanumWebApplicationFactory factory,
        HttpClient client)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            (Guid campaign, Guid session) = await BoundSessionAsync(factory, client, $"mixed-{attempt}");

            if (MixedCaseSpelling(campaign) is { } mixedCase)
            {
                return (campaign, session, mixedCase);
            }
        }

        throw new InvalidOperationException("Eight Campaign ids in a row had fewer than two hex letters.");
    }

    /// <summary>
    /// An id spelled in lower case except for its first hex letter: a dashed spelling that is neither
    /// of the two the seek binds, or null for an id with fewer than two letters.
    /// </summary>
    /// <remarks>
    /// With no letter that spelling would be the lower-case one, and with one it would be the canonical
    /// upper-case one. A random id has fewer than two letters about once in a hundred thousand.
    /// </remarks>
    private static string? MixedCaseSpelling(Guid id)
    {
        char[] letters = ['a', 'b', 'c', 'd', 'e', 'f'];

        string lower = id.ToString("D");

        int first = lower.IndexOfAny(letters);

        return first >= 0 && lower.IndexOfAny(letters, first + 1) >= 0
            ? lower[..first] + char.ToUpperInvariant(lower[first]) + lower[(first + 1)..]
            : null;
    }

    /// <summary>Labels one memory through the one production writer of sensitivity labels.</summary>
    private static async Task LabelAsync(
        ArcanumWebApplicationFactory factory,
        string memoryId,
        Guid sessionId,
        Guid campaignId)
    {
        using IServiceScope scope = factory.Services.CreateScope();

        Result<LabeledArtifactWriteReceipt> labelled = await scope.ServiceProvider
            .GetRequiredService<IArtifactSensitivityLedger>()
            .LabelAsync(
                new DerivedArtifactWrite(
                    SensitiveArtifactKind.Saga,
                    Guid.Parse(memoryId),
                    sessionId,
                    campaignId,
                    turnId: null,
                    artifactRevision: 1,
                    DerivedArtifactContentDigest.ForText(T),
                    ContentSensitivity.CovenantDerived,
                    GenerationProvenance.CreateExact([Guid.NewGuid()])),
                CancellationToken.None);

        Assert.True(labelled.IsSuccess, labelled.IsFailure ? labelled.Error.Message : null);
    }

    /// <summary>The prepare request the CLI builds from the detail route.</summary>
    private static async Task<SagaErasePrepareRequest> PrepareRequestAsync(HttpClient client, string memoryId, Guid mutationId)
    {
        using HttpResponseMessage shown = await client.GetAsync($"/api/memory/saga/{memoryId}");

        Assert.Equal(HttpStatusCode.OK, shown.StatusCode);

        SagaMemoryDetail detail = await MemoryErasureRouteDriver.ReadDataAsync(
            shown,
            ArcanumJsonContext.Default.ApiResponseSagaMemoryDetail);

        return new SagaErasePrepareRequest(memoryId, detail.ContentHash, detail.Claim?.CurrentVersionId, mutationId);
    }

    private static Task<HttpResponseMessage> PostPrepareAsync(MemoryErasureRouteDriver driver, SagaErasePrepareRequest request) =>
        driver.PostAsync("/api/memory/saga/erase/prepare", request, ArcanumJsonContext.Default.SagaErasePrepareRequest);

    private static async Task<MemoryErasurePreflightDto> PrepareOkAsync(MemoryErasureRouteDriver driver, SagaErasePrepareRequest request)
    {
        using HttpResponseMessage response = await PostPrepareAsync(driver, request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await MemoryErasureRouteDriver.ReadDataAsync(
            response,
            ArcanumJsonContext.Default.ApiResponseMemoryErasurePreflightDto);
    }

    private static SagaEraseRequest Apply(SagaErasePrepareRequest prepare, MemoryErasurePreflightDto preflight) =>
        new(prepare.MemoryId, prepare.ExpectedContentHash, prepare.ExpectedClaimVersionId, prepare.MutationId, preflight.PreflightToken);

    private static async Task AssertApplyRefusedAsync(
        MemoryErasureRouteDriver driver,
        SagaEraseRequest request,
        HttpStatusCode status,
        string code)
    {
        using HttpResponseMessage response = await driver.PostAsync(
            "/api/memory/saga/erase",
            request,
            ArcanumJsonContext.Default.SagaEraseRequest);

        await AssertRefusalAsync(response, status, code);
    }

    /// <summary>A refusal carries its code and no result.</summary>
    private static async Task AssertRefusalAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);

        Assert.Equal(code, await MemoryErasureRouteDriver.ReadErrorCodeAsync(response));

        await AssertNoResultAsync(response);
    }

    /// <summary>A failure envelope that carries no result.</summary>
    private static async Task AssertNoResultAsync(HttpResponseMessage response)
    {
        using System.Text.Json.JsonDocument body = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.False(body.RootElement.GetProperty("isSuccess").GetBoolean());

        // The envelope omits an absent result rather than writing a null one; either way there is none.
        Assert.False(
            body.RootElement.TryGetProperty("data", out System.Text.Json.JsonElement data)
            && data.ValueKind != System.Text.Json.JsonValueKind.Null);
    }

    private static async Task AssertShownAsync(HttpClient client, string memoryId, string content)
    {
        using HttpResponseMessage shown = await client.GetAsync($"/api/memory/saga/{memoryId}");

        Assert.Equal(HttpStatusCode.OK, shown.StatusCode);

        SagaMemoryDetail detail = await MemoryErasureRouteDriver.ReadDataAsync(
            shown,
            ArcanumJsonContext.Default.ApiResponseSagaMemoryDetail);

        Assert.Equal(content, detail.Memory.Content);
    }

    private static async Task AssertShowRefusedAsync(HttpClient client, string memoryId, HttpStatusCode status, string code)
    {
        using HttpResponseMessage shown = await client.GetAsync($"/api/memory/saga/{memoryId}");

        Assert.Equal(status, shown.StatusCode);

        Assert.Equal(code, await MemoryErasureRouteDriver.ReadErrorCodeAsync(shown));
    }

    /// <summary>A replay reports the erase the first apply committed, count for count.</summary>
    private static void AssertSameErase(MemoryErasureResultDto first, MemoryErasureResultDto replay)
    {
        Assert.Equal(first.Store, replay.Store);

        Assert.Equal(first.MutationId, replay.MutationId);

        Assert.Equal(first.EffectDigest, replay.EffectDigest);

        Assert.Equal(first.Local.ErasedItemCount, replay.Local.ErasedItemCount);

        Assert.Equal(first.Local.RemovedRowCount, replay.Local.RemovedRowCount);

        Assert.Equal(first.Local.RemovedLabelCount, replay.Local.RemovedLabelCount);

        Assert.Equal(first.Local.RemovedRetirementSuppressionCount, replay.Local.RemovedRetirementSuppressionCount);

        Assert.Equal(first.Local.SuppressionFingerprintRecorded, replay.Local.SuppressionFingerprintRecorded);

        Assert.Equal(first.External, replay.External, ExposureComparer.Instance);

        Assert.Equal(first.RetainedLocalCopies, replay.RetainedLocalCopies);

        Assert.Equal(first.Notes, replay.Notes);
    }

    private static string Hash(string content) => Convert.ToHexString(AnnalContentDigest.ForSagaMemory(content));

    /// <summary>
    /// Every row the Saga plan owns for these memories, counted table by table with the relations
    /// spelled out here rather than borrowed from the plan. Assertion-only.
    /// </summary>
    private static async Task<long> PlanRowsAsync(
        ArcanumWebApplicationFactory factory,
        IReadOnlyList<string> ids,
        bool requireRows = true)
    {
        const string claims = "SELECT ClaimId FROM annal_claims WHERE SubjectStoreCode = 1 AND SubjectId IN ({0})";

        const string versions = "SELECT VersionId FROM annal_versions WHERE ClaimId IN (" + claims + ")";

        const string events = "SELECT Sequence FROM annal_review_events WHERE VersionId IN (" + versions + ")";

        string[] counts =
        [
            "SELECT count(*) FROM annal_review_decision_receipts WHERE ReviewEventSequence IN (" + events + ")",
            "SELECT count(*) FROM annal_review_events WHERE VersionId IN (" + versions + ")",
            "SELECT count(*) FROM annal_dependencies WHERE DependentVersionId IN (" + versions + ") OR DependencyVersionId IN (" + versions + ")",
            "SELECT count(*) FROM annal_heads WHERE ClaimId IN (" + claims + ")",
            "SELECT count(*) FROM annal_versions WHERE ClaimId IN (" + claims + ")",
            "SELECT count(*) FROM annal_claims WHERE SubjectStoreCode = 1 AND SubjectId IN ({0})",
            "SELECT count(*) FROM saga_memory_embeddings WHERE MemoryId IN ({0})",
            "SELECT count(*) FROM saga_memory_attachment_provenance WHERE MemoryId IN ({0})",
            "SELECT count(*) FROM saga_memories WHERE Id IN ({0})",
        ];

        long total = 0;

        foreach (string count in counts)
        {
            total += await CountForIdsAsync(factory, count, ids);
        }

        // A mirror exists only where a test built one in place of an accelerator, and only as a plain
        // table; a legacy virtual mirror is never counted, because the plan skips it.
        if (await ScalarAsync(
                factory,
                "SELECT count(*) FROM sqlite_master WHERE name = 'saga_memory_embeddings_vec' AND sql NOT LIKE 'CREATE VIRTUAL TABLE%';") == 1)
        {
            total += await CountForIdsAsync(factory, "SELECT count(*) FROM saga_memory_embeddings_vec WHERE MemoryId IN ({0})", ids);
        }

        if (requireRows)
        {
            Assert.True(total >= ids.Count * 4L);
        }

        return total;
    }

    private static async Task<long> CountForIdsAsync(ArcanumWebApplicationFactory factory, string template, IReadOnlyList<string> ids)
    {
        string list = string.Join(", ", ids.Select(static (_, index) => $"$id{index}"));

        return await ScalarAsync(
            factory,
            string.Format(System.Globalization.CultureInfo.InvariantCulture, template, list),
            [.. ids.Select(static (id, index) => ($"$id{index}", (object)id))]);
    }

    private static Task<long> ContentRowsAsync(ArcanumWebApplicationFactory factory, string content) =>
        ScalarAsync(factory, "SELECT count(*) FROM saga_memories WHERE Content = $content;", [("$content", content)]);

    private static Task<long> ReceiptsAsync(ArcanumWebApplicationFactory factory, Guid mutationId) =>
        ScalarAsync(
            factory,
            "SELECT count(*) FROM memory_erasure_receipts WHERE MutationId = $mutation;",
            [("$mutation", mutationId.ToString("D").ToUpperInvariant())]);

    private static Task<long> ReceiptSubjectsAsync(ArcanumWebApplicationFactory factory, Guid mutationId) =>
        ScalarAsync(
            factory,
            "SELECT count(*) FROM memory_erasure_receipt_subjects WHERE MutationId = $mutation;",
            [("$mutation", mutationId.ToString("D").ToUpperInvariant())]);

    private static async Task<long> ScalarAsync(
        ArcanumWebApplicationFactory factory,
        string sql,
        IReadOnlyList<(string Name, object Value)>? parameters = null)
    {
        using IServiceScope scope = factory.Services.CreateScope();

        SqliteConnection connection = await OpenAsync(scope);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object value) in parameters ?? [])
        {
            _ = command.Parameters.AddWithValue(name, value);
        }

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(
        ArcanumWebApplicationFactory factory,
        string sql,
        IReadOnlyList<(string Name, object Value)>? parameters = null)
    {
        using IServiceScope scope = factory.Services.CreateScope();

        SqliteConnection connection = await OpenAsync(scope);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object value) in parameters ?? [])
        {
            _ = command.Parameters.AddWithValue(name, value);
        }

        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertNoOrphanClaimsAsync(ArcanumWebApplicationFactory factory)
    {
        using IServiceScope scope = factory.Services.CreateScope();

        await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(await OpenAsync(scope));
    }

    private static async Task<SqliteConnection> OpenAsync(IServiceScope scope)
    {
        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        SqliteConnection connection = (SqliteConnection)db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync();
        }

        return connection;
    }

    /// <summary>Compares two exposure reports by revocation and by each channel's evidence, in order.</summary>
    private sealed class ExposureComparer : IEqualityComparer<MemoryErasureExternalExposureDto>
    {
        internal static ExposureComparer Instance { get; } = new();

        public bool Equals(MemoryErasureExternalExposureDto? x, MemoryErasureExternalExposureDto? y) =>
            x is not null
            && y is not null
            && x.Revocation == y.Revocation
            && x.Channels.SequenceEqual(y.Channels);

        public int GetHashCode(MemoryErasureExternalExposureDto obj) => obj.Revocation.GetHashCode();
    }
}
