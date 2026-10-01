using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Tower;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Memory;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.NativeSqlCipher;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api.Tower;

/// <summary>
/// The Lexicon erase routes, prepare then apply, driven through the mapped HTTP surface against a host
/// whose entries were written by the service the agent tools and endpoints hold.
/// </summary>
/// <remarks>
/// <para>Every target comes from <c>POST /api/memory/lexicon/show</c>, which is where the CLI takes it
/// from, and every lifecycle change from the curation routes. A label is written only through the
/// sensitivity ledger, the one production writer of labels.</para>
///
/// <para>Every test that erases ends by asking the database whether any Annals claim outlived its
/// row.</para>
/// </remarks>
[Collection("ApiHost")]
public sealed class LexiconErasureEndpointTests
{
    private const string Name = "Mill Warden";

    private const string OperatorManaged = "This Lexicon entry is managed by the operator in this scope, so nothing was recorded.";

    [SkippableFact]
    public async Task Prepare_then_apply_erases_the_entry_its_annals_and_provenance()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore());

        (HttpClient client, MemoryErasureRouteDriver driver) = Connect(factory);

        LexiconEntryDto entry = await ScribeAsync(factory, Name, null, ["guards the mill", "keeps the ledger"], Provenance());

        // A correction keeps the unchanged fact's current provenance and records historical source
        // coordinates on the claim, so both provenance tables hold rows for the erase to take.
        using (HttpResponseMessage corrected = await client.PostAsync(
            "/api/memory/lexicon/correct",
            JsonContent.Create(
                new LexiconCorrectRequest(
                    (await ShowAsync(driver, Name, null)).Target,
                    new LexiconReplacementContent("Place", ["guards the mill", "watches the gate"])),
                ArcanumJsonContext.Default.LexiconCorrectRequest)))
        {
            Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        }

        Assert.True(await ScalarAsync(factory, "SELECT count(*) FROM lexicon_fact_attachment_provenance WHERE EntryId = $id;", Id(entry)) > 0);

        Assert.True(await ScalarAsync(factory, LexiconAnnalProvenance, Id(entry)) > 0);

        Assert.Equal(1, await ScalarAsync(factory, "SELECT count(*) FROM lexicon_fts WHERE lexicon_fts MATCH 'warden';"));

        long before = await PlanRowsAsync(factory, entry.Id);

        LexiconCurationTarget target = (await ShowAsync(driver, Name, null)).Target;

        LexiconErasePrepareRequest prepare = new(target, Guid.NewGuid());

        MemoryErasurePreflightDto preflight = await PrepareOkAsync(driver, prepare);

        Assert.Equal(MemoryReviewStore.Lexicon, preflight.Store);

        Assert.Equal(prepare.MutationId, preflight.MutationId);

        Assert.Equal(1, preflight.Plan.ErasedItemCount);

        Assert.Equal(before, preflight.Plan.RowsToRemove);

        Assert.Equal(0, preflight.Plan.LabelsToRemove);

        Assert.Equal(0, preflight.Plan.RetirementSuppressionsToRemove);

        Assert.False(preflight.Plan.Pinned);

        Assert.False(preflight.Plan.Lexicon!.GlobalEntryResurfaces);

        Assert.Null(preflight.Plan.Covenant);

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
                MemoryExternalEvidence.NotApplicable,
                MemoryExternalEvidence.NotRecorded,
                MemoryExternalEvidence.NotRecorded,
            ],
            preflight.External.Channels.Select(static channel => channel.Evidence));

        Assert.Equal([MemoryErasureNote.OtherScopesUnaffected], preflight.Notes);

        Assert.Equal(TimeSpan.FromMinutes(5), preflight.ExpiresAtUtc - preflight.IssuedAtUtc);

        MemoryErasureResultDto result = await driver.ApplyLexiconAsync(new(target, prepare.MutationId, preflight.PreflightToken));

        Assert.Equal(MemoryReviewStore.Lexicon, result.Store);

        Assert.False(result.Replayed);

        Assert.Equal(preflight.EffectDigest, result.EffectDigest);

        Assert.Equal(1, result.Local.ErasedItemCount);

        Assert.Equal(preflight.Plan.RowsToRemove, result.Local.RemovedRowCount);

        Assert.DoesNotContain(MemoryErasureScrubPendingReason.FullTextSecureDeleteUnverified, result.Local.PendingReasons);

        Assert.True(result.Local.SuppressionFingerprintRecorded);

        Assert.Equal([MemoryErasureNote.OtherScopesUnaffected], result.Notes);

        await AssertShowRefusedAsync(driver, Name, null, HttpStatusCode.NotFound, ErrorCodes.Lexicon.NotFound);

        Assert.Equal(0, await ScalarAsync(factory, "SELECT count(*) FROM annal_claims WHERE SubjectStoreCode = 2 AND SubjectId = $id;", Id(entry)));

        Assert.Equal(0, await PlanRowsAsync(factory, entry.Id, requireRows: false));

        Assert.Equal(0, await ScalarAsync(factory, "SELECT count(*) FROM lexicon_fts WHERE lexicon_fts MATCH 'warden';"));

        Assert.Equal(1, await ReceiptSubjectsAsync(factory, prepare.MutationId));

        Assert.Equal(1, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Lexicon));

        await AssertNoOrphanClaimsAsync(factory);
    }

    [SkippableFact]
    public async Task A_campaign_erase_reports_and_produces_global_resurfacing()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore());

        (HttpClient client, MemoryErasureRouteDriver driver) = Connect(factory);

        Guid campaign = Guid.NewGuid();

        LexiconEntryDto global = await ScribeAsync(factory, Name, null, ["the installation's warden"]);

        LexiconEntryDto scoped = await ScribeAsync(factory, Name, campaign, ["the campaign's warden"]);

        Assert.Equal(scoped.Id, (await EffectiveAsync(client, campaign)).Id);

        MemoryErasureRoundTrip<LexiconEraseRequest> erased = await driver.EraseLexiconAsync(Name, campaign);

        Assert.True(erased.Preflight.Plan.Lexicon!.GlobalEntryResurfaces);

        LexiconEntryDto effective = await EffectiveAsync(client, campaign);

        Assert.Equal(global.Id, effective.Id);

        Assert.Null(effective.ScopeCampaignId);

        Assert.Equal(["the installation's warden"], effective.Facts);

        await AssertShowRefusedAsync(driver, Name, campaign, HttpStatusCode.NotFound, ErrorCodes.Lexicon.NotFound);

        await AssertNoOrphanClaimsAsync(factory);
    }

    /// <summary>
    /// A labelled entry is erased under the write lease over its own scope: the lease is taken before
    /// the transaction, the label is removed in it, and no label is left for the entry.
    /// </summary>
    /// <remarks>
    /// Annals capture is off, so the entry carries no claim whose sensitivity would have to agree with
    /// the label: labelling a claimed entry after the fact is not a state any production writer leaves.
    /// </remarks>
    [SkippableFact]
    public async Task A_labelled_entry_is_erased_under_the_exact_scope_write_lease()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(
            new InMemoryOsCredentialStore(),
            covenant: true,
            configure: static settings => settings.Features.Annals = false);

        (_, MemoryErasureRouteDriver driver) = Connect(factory);

        LexiconEntryDto entry = await ScribeAsync(factory, Name, null, ["guards the mill"]);

        await LabelAsync(factory, entry);

        Assert.Equal(1, await LabelCountAsync(factory, entry.Id));

        MemoryErasureRoundTrip<LexiconEraseRequest> erased = await driver.EraseLexiconAsync(Name, null);

        Assert.True(erased.Apply.Target.SensitivityLabel.IsPresent);

        Assert.Equal(1, erased.Preflight.Plan.LabelsToRemove);

        Assert.Equal(1, erased.Result.Local.RemovedLabelCount);

        Assert.Equal(0, await LabelCountAsync(factory, entry.Id));

        await AssertShowRefusedAsync(driver, Name, null, HttpStatusCode.NotFound, ErrorCodes.Lexicon.NotFound);

        await AssertNoOrphanClaimsAsync(factory);
    }

    /// <summary>
    /// A labelled Campaign entry is erased under the write lease over that Campaign: the lease, its
    /// exact-scope check and the label's owner are all the Campaign's, not the installation's.
    /// </summary>
    [SkippableFact]
    public async Task A_labelled_campaign_entry_is_erased_under_that_campaigns_write_lease()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(
            new InMemoryOsCredentialStore(),
            covenant: true,
            configure: static settings => settings.Features.Annals = false);

        (HttpClient client, MemoryErasureRouteDriver driver) = Connect(factory);

        Guid campaign = await RegisterCampaignAsync(factory, client);

        LexiconEntryDto global = await ScribeAsync(factory, Name, null, ["the installation's warden"]);

        LexiconEntryDto entry = await ScribeAsync(factory, Name, campaign, ["the campaign's warden"]);

        await LabelAsync(factory, entry);

        MemoryErasureRoundTrip<LexiconEraseRequest> erased = await driver.EraseLexiconAsync(Name, campaign);

        Assert.True(erased.Apply.Target.SensitivityLabel.IsPresent);

        Assert.Equal(LexiconScopeKind.Campaign, erased.Apply.Target.Scope.Kind);

        Assert.Equal(1, erased.Preflight.Plan.LabelsToRemove);

        Assert.Equal(1, erased.Result.Local.RemovedLabelCount);

        Assert.Equal(0, await LabelCountAsync(factory, entry.Id));

        await AssertShowRefusedAsync(driver, Name, campaign, HttpStatusCode.NotFound, ErrorCodes.Lexicon.NotFound);

        Assert.Equal(global.Id, (await ShowAsync(driver, Name, null)).Entry.Id);

        await AssertNoOrphanClaimsAsync(factory);
    }

    /// <summary>
    /// The full-text row is a planned row like any other: an index that still holds the entry after its
    /// row is deleted fails the absence proof, and the erase removes nothing.
    /// </summary>
    /// <remarks>
    /// Dropping the content table's delete trigger stands in for an index that has fallen out of step
    /// with its rows. No production writer leaves one, which is why the proof has to be exercised this
    /// way. The trigger is put back from the shipped schema file afterwards.
    /// </remarks>
    [SkippableFact]
    public async Task An_index_that_still_holds_the_entry_fails_the_absence_proof()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore());

        (_, MemoryErasureRouteDriver driver) = Connect(factory);

        LexiconEntryDto entry = await ScribeAsync(factory, Name, null, ["guards the mill"]);

        LexiconCurationTarget target = (await ShowAsync(driver, Name, null)).Target;

        LexiconErasePrepareRequest prepare = new(target, Guid.NewGuid());

        MemoryErasurePreflightDto preflight = await PrepareOkAsync(driver, prepare);

        await ExecuteAsync(factory, "DROP TRIGGER lexicon_entries_ad;");

        await AssertApplyRefusedAsync(
            driver,
            new(target, prepare.MutationId, preflight.PreflightToken),
            HttpStatusCode.InternalServerError,
            ErrorCodes.MemoryErasure.ErasureIncomplete);

        await ExecuteAsync(factory, File.ReadAllText(Path.Combine(
            NativeSqlCipherTestPaths.RepositoryRoot(),
            "src",
            "RetroDownfall.Arcanum.Infrastructure",
            "Data",
            "Schema",
            "Triggers",
            "lexicon_entries_ad.sql")));

        Assert.Equal(0, await ReceiptsAsync(factory, prepare.MutationId));

        Assert.Equal(0, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Lexicon));

        Assert.Equal(target, (await ShowAsync(driver, Name, null)).Target);

        Assert.Equal(1, await ScalarAsync(factory, "SELECT count(*) FROM lexicon_fts WHERE lexicon_fts MATCH 'warden';"));

        Assert.Equal(1, await ScalarAsync(factory, "SELECT count(*) FROM annal_claims WHERE SubjectStoreCode = 2 AND SubjectId = $id;", Id(entry)));

        await AssertNoOrphanClaimsAsync(factory);
    }

    /// <summary>
    /// A labelled entry whose erase another mutation already applied takes no lease: the transaction
    /// answers that its subject was erased, as it does for an unlabelled one.
    /// </summary>
    [SkippableFact]
    public async Task A_second_mutation_on_a_labelled_entry_answers_410_at_apply()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(
            new InMemoryOsCredentialStore(),
            covenant: true,
            configure: static settings => settings.Features.Annals = false);

        (_, MemoryErasureRouteDriver driver) = Connect(factory);

        LexiconEntryDto entry = await ScribeAsync(factory, Name, null, ["guards the mill"]);

        await LabelAsync(factory, entry);

        LexiconCurationTarget target = (await ShowAsync(driver, Name, null)).Target;

        Assert.True(target.SensitivityLabel.IsPresent);

        LexiconErasePrepareRequest first = new(target, Guid.NewGuid());

        LexiconErasePrepareRequest second = first with { MutationId = Guid.NewGuid() };

        MemoryErasurePreflightDto firstPlan = await PrepareOkAsync(driver, first);

        MemoryErasurePreflightDto secondPlan = await PrepareOkAsync(driver, second);

        MemoryErasureResultDto applied = await driver.ApplyLexiconAsync(new(target, first.MutationId, firstPlan.PreflightToken));

        Assert.False(applied.Replayed);

        await AssertApplyRefusedAsync(
            driver,
            new(target, second.MutationId, secondPlan.PreflightToken),
            HttpStatusCode.Gone,
            ErrorCodes.MemoryErasure.SubjectErased);

        Assert.Equal(0, await ReceiptSubjectsAsync(factory, second.MutationId));

        await AssertNoOrphanClaimsAsync(factory);
    }

    [SkippableFact]
    public async Task A_pinned_or_retired_entry_is_erasable()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore());

        (HttpClient client, MemoryErasureRouteDriver driver) = Connect(factory);

        _ = await ScribeAsync(factory, "Pinned Warden", null, ["keeps the pin"]);

        _ = await ScribeAsync(factory, "Retired Warden", null, ["kept the gate"]);

        using (HttpResponseMessage pinned = await client.PostAsync(
            "/api/memory/lexicon/pin",
            JsonContent.Create(
                new LexiconPinRequest((await ShowAsync(driver, "Pinned Warden", null)).Target),
                ArcanumJsonContext.Default.LexiconPinRequest)))
        {
            Assert.Equal(HttpStatusCode.OK, pinned.StatusCode);
        }

        using (HttpResponseMessage retired = await client.PostAsync(
            "/api/memory/lexicon/retire",
            JsonContent.Create(
                new LexiconRetireRequest((await ShowAsync(driver, "Retired Warden", null)).Target),
                ArcanumJsonContext.Default.LexiconRetireRequest)))
        {
            Assert.Equal(HttpStatusCode.OK, retired.StatusCode);
        }

        MemoryErasureRoundTrip<LexiconEraseRequest> pinnedErase = await driver.EraseLexiconAsync("Pinned Warden", null);

        Assert.True(pinnedErase.Preflight.Plan.Pinned);

        Assert.False(pinnedErase.Result.Replayed);

        MemoryErasureRoundTrip<LexiconEraseRequest> retiredErase = await driver.EraseLexiconAsync("Retired Warden", null);

        Assert.False(retiredErase.Preflight.Plan.Pinned);

        Assert.False(retiredErase.Result.Replayed);

        await AssertShowRefusedAsync(driver, "Pinned Warden", null, HttpStatusCode.NotFound, ErrorCodes.Lexicon.NotFound);

        await AssertShowRefusedAsync(driver, "Retired Warden", null, HttpStatusCode.NotFound, ErrorCodes.Lexicon.NotFound);

        await AssertNoOrphanClaimsAsync(factory);
    }

    [SkippableFact]
    public async Task A_repeated_apply_replays()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore());

        (_, MemoryErasureRouteDriver driver) = Connect(factory);

        _ = await ScribeAsync(factory, Name, null, ["guards the mill"]);

        MemoryErasureRoundTrip<LexiconEraseRequest> roundTrip = await driver.EraseLexiconAsync(Name, null);

        MemoryErasureResultDto replayed = await driver.ApplyLexiconAsync(roundTrip.Apply with { PreflightToken = "x" });

        Assert.True(replayed.Replayed);

        Assert.Equal(roundTrip.Result.EffectDigest, replayed.EffectDigest);

        Assert.Equal(roundTrip.Result.MutationId, replayed.MutationId);

        Assert.Equal(roundTrip.Result.Local.RemovedRowCount, replayed.Local.RemovedRowCount);

        Assert.Equal(roundTrip.Result.Notes, replayed.Notes);

        Assert.Equal(1, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Lexicon));

        await AssertNoOrphanClaimsAsync(factory);
    }

    [SkippableFact]
    public async Task Prepare_for_an_erased_entry_answers_410()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore());

        (_, MemoryErasureRouteDriver driver) = Connect(factory);

        _ = await ScribeAsync(factory, Name, null, ["guards the mill"]);

        MemoryErasureRoundTrip<LexiconEraseRequest> erased = await driver.EraseLexiconAsync(Name, null);

        LexiconErasePrepareRequest again = new(erased.Apply.Target, Guid.NewGuid());

        using (HttpResponseMessage gone = await PostPrepareAsync(driver, again))
        {
            await AssertRefusalAsync(gone, HttpStatusCode.Gone, ErrorCodes.MemoryErasure.SubjectErased);
        }

        using HttpResponseMessage unknown = await PostPrepareAsync(
            driver,
            again with { Target = again.Target with { EntryId = Guid.NewGuid() } });

        await AssertRefusalAsync(unknown, HttpStatusCode.NotFound, ErrorCodes.Lexicon.NotFound);

        await AssertNoOrphanClaimsAsync(factory);
    }

    /// <summary>
    /// A Global entry of the same name created after a Campaign erase was prepared changes what the
    /// erase reports, while the Campaign entry's own curation target is untouched, so only the effect
    /// recompute can refuse it.
    /// </summary>
    [SkippableFact]
    public async Task A_global_entry_created_between_prepare_and_apply_is_a_stale_plan()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore());

        (_, MemoryErasureRouteDriver driver) = Connect(factory);

        Guid campaign = Guid.NewGuid();

        _ = await ScribeAsync(factory, Name, campaign, ["the campaign's warden"]);

        LexiconCurationTarget target = (await ShowAsync(driver, Name, campaign)).Target;

        LexiconErasePrepareRequest prepare = new(target, Guid.NewGuid());

        MemoryErasurePreflightDto preflight = await PrepareOkAsync(driver, prepare);

        Assert.False(preflight.Plan.Lexicon!.GlobalEntryResurfaces);

        _ = await ScribeAsync(factory, Name, null, ["the installation's warden"]);

        await AssertApplyRefusedAsync(
            driver,
            new(target, prepare.MutationId, preflight.PreflightToken),
            HttpStatusCode.Conflict,
            ErrorCodes.MemoryErasure.StalePlan);

        Assert.Equal(target, (await ShowAsync(driver, Name, campaign)).Target);

        Assert.Equal(0, await ReceiptsAsync(factory, prepare.MutationId));

        Assert.Equal(0, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Lexicon));

        await AssertNoOrphanClaimsAsync(factory);
    }

    /// <summary>
    /// After an erase through the route, the agent's own <c>scribe_lexicon</c> tool, reached through
    /// the host's MCP bridge exactly as a turn reaches it, is refused with the operator-managed text and
    /// records nothing.
    /// </summary>
    /// <remarks>
    /// The diagnostic invocation route refuses every tool of the internal server by design, so the
    /// production entry point here is the bridge function the turn pipeline is handed. A tool call made
    /// outside a turn has no Session, so it writes to the Global tier.
    /// </remarks>
    [SkippableFact]
    public async Task A_scribe_lexicon_tool_call_after_an_erase_is_refused_with_the_operator_managed_text()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(
            new InMemoryOsCredentialStore(),
            configure: static settings => settings.Features.Lexicon = true);

        (_, MemoryErasureRouteDriver driver) = Connect(factory);

        _ = await ScribeAsync(factory, Name, null, ["guards the mill"]);

        _ = await driver.EraseLexiconAsync(Name, null);

        IReadOnlyList<AITool> tools = await factory.Services
            .GetRequiredService<IMcpConnectionManager>()
            .GetAvailableToolsAsync(null);

        AIFunction scribe = Assert.IsAssignableFrom<AIFunction>(
            Assert.Single(tools, static tool => string.Equals(tool.Name, "scribe_lexicon", StringComparison.Ordinal)));

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await scribe.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["name"] = Name,
                ["type"] = "Place",
                ["facts"] = new[] { "returns to the mill" },
            })));

        Assert.Equal(OperatorManaged, refused.Message);

        Assert.Equal(0, await ScalarAsync(
            factory,
            "SELECT count(*) FROM lexicon_entries WHERE NameNormalized = $id AND ScopeCampaignId = '';",
            Name.ToUpperInvariant()));

        await AssertNoOrphanClaimsAsync(factory);
    }

    /// <summary>
    /// The agent's <c>delete_lexicon</c> still refuses a daemon's own entry by name, through the same
    /// recognizer the erase refuses it with, and leaves the entry in place.
    /// </summary>
    [SkippableFact]
    public async Task A_delete_lexicon_tool_call_still_refuses_a_daemon_state_entry()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(
            new InMemoryOsCredentialStore(),
            configure: static settings => settings.Features.Lexicon = true);

        (_, MemoryErasureRouteDriver driver) = Connect(factory);

        _ = await ScribeAsync(factory, "daemon_state:Watcher:abc", null, ["the trend rose"]);

        IReadOnlyList<AITool> tools = await factory.Services
            .GetRequiredService<IMcpConnectionManager>()
            .GetAvailableToolsAsync(null);

        AIFunction delete = Assert.IsAssignableFrom<AIFunction>(
            Assert.Single(tools, static tool => string.Equals(tool.Name, "delete_lexicon", StringComparison.Ordinal)));

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await delete.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["name"] = "DAEMON_STATE:Watcher:abc",
            })));

        Assert.Contains("daemon_state", refused.Message, StringComparison.Ordinal);

        _ = await ShowAsync(driver, "daemon_state:Watcher:abc", null);
    }

    /// <summary>
    /// A rolled-back erase reports its failure and nothing else: no receipt, no fingerprint, no result,
    /// and no post-commit scrub, because the scrub runs only for an erase that committed.
    /// </summary>
    /// <remarks>
    /// A trigger stands in for a writer that re-creates the erased entry inside the erase's own
    /// transaction, after its deletes and before its commit. No production path can do that, which is
    /// exactly why the in-transaction absence proof has to be exercised this way.
    /// </remarks>
    [SkippableFact]
    public async Task An_erase_that_rolls_back_reports_no_result_and_never_scrubs()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        TestCapturingLogger<MemoryErasureScrubber> scrubLog = new();

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore());

        factory.ServiceOverrides += services =>
            services.AddSingleton<ILogger<MemoryErasureScrubber>>(scrubLog);

        (_, MemoryErasureRouteDriver driver) = Connect(factory);

        LexiconEntryDto entry = await ScribeAsync(factory, Name, null, ["guards the mill"]);

        LexiconCurationTarget target = (await ShowAsync(driver, Name, null)).Target;

        LexiconErasePrepareRequest prepare = new(target, Guid.NewGuid());

        MemoryErasurePreflightDto preflight = await PrepareOkAsync(driver, prepare);

        await ExecuteAsync(
            factory,
            $"""
            CREATE TRIGGER test_erasure_resurrects_the_entry AFTER INSERT ON memory_erasure_receipts
            BEGIN
                INSERT INTO lexicon_entries (Id, Name, NameNormalized, ScopeCampaignId, Type, FactsJson, FactsText, UpdatedAt)
                VALUES ('{entry.Id:N}', 'Resurrected', 'RESURRECTED', '', 'Place', '[]', '', '2026-09-30T00:00:00.0000000Z');
            END;
            """);

        await AssertApplyRefusedAsync(
            driver,
            new(target, prepare.MutationId, preflight.PreflightToken),
            HttpStatusCode.InternalServerError,
            ErrorCodes.MemoryErasure.ErasureIncomplete);

        Assert.Empty(scrubLog.Entries);

        Assert.Equal(0, await ReceiptsAsync(factory, prepare.MutationId));

        Assert.Equal(0, await MemoryErasureRouteDriver.FingerprintCountAsync(factory, MemoryReviewStore.Lexicon));

        await ExecuteAsync(factory, "DROP TRIGGER test_erasure_resurrects_the_entry;");

        Assert.Equal(target, (await ShowAsync(driver, Name, null)).Target);

        await AssertNoOrphanClaimsAsync(factory);
    }

    private const string LexiconAnnalProvenance =
        """
        SELECT count(*) FROM lexicon_annal_fact_provenance
        WHERE AnnalVersionId IN (
            SELECT VersionId FROM annal_versions
            WHERE ClaimId IN (SELECT ClaimId FROM annal_claims WHERE SubjectStoreCode = 2 AND SubjectId = $id));
        """;

    private static (HttpClient Client, MemoryErasureRouteDriver Driver) Connect(ArcanumWebApplicationFactory factory)
    {
        HttpClient client = factory.CreateAuthenticatedClient();

        return (client, new MemoryErasureRouteDriver(client));
    }

    /// <summary>Registers one Campaign through its route, so the Covenant gate knows the scope it leases.</summary>
    private static async Task<Guid> RegisterCampaignAsync(ArcanumWebApplicationFactory factory, HttpClient client)
    {
        string path = Path.Combine(factory.TempHome, "erasure-campaign");

        Directory.CreateDirectory(path);

        using HttpResponseMessage registered = await client.PostAsync(
            "/api/campaigns",
            JsonContent.Create(
                new RegisterCampaignRequest("Lexicon erasure", path, WorkspaceType.Campaign, null),
                ArcanumJsonContext.Default.RegisterCampaignRequest));

        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);

        return (await MemoryErasureRouteDriver.ReadDataAsync(registered, ArcanumJsonContext.Default.ApiResponseCampaignDto)).Id;
    }

    /// <summary>One attachment source, so a scribe records current fact provenance for its facts.</summary>
    private static AttachmentMemoryProvenance Provenance() =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "mill-notes",
            1,
            "attachment-hash",
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            "WorkspaceFile",
            AttachmentSourceAvailability.Available);

    /// <summary>Scribes one entry through the host's own Lexicon service, the one every writer holds.</summary>
    private static async Task<LexiconEntryDto> ScribeAsync(
        ArcanumWebApplicationFactory factory,
        string name,
        Guid? campaignId,
        string[] facts,
        AttachmentMemoryProvenance? provenance = null)
    {
        using IServiceScope scope = factory.Services.CreateScope();

        ILexiconService lexicon = scope.ServiceProvider.GetRequiredService<ILexiconService>();

        LexiconScope tier = LexiconScope.ForResolvedCampaign(campaignId);

        Result<LexiconEntryDto> scribed = provenance is null
            ? await lexicon.UpsertAsync(name, "Place", facts, tier, CancellationToken.None)
            : await lexicon.UpsertAsync(name, "Place", facts, provenance, tier, CancellationToken.None);

        Assert.True(scribed.IsSuccess, scribed.IsFailure ? scribed.Error.Message : null);

        return scribed.Value;
    }

    /// <summary>Labels one entry through the one production writer of sensitivity labels.</summary>
    private static async Task LabelAsync(ArcanumWebApplicationFactory factory, LexiconEntryDto entry)
    {
        LexiconCanonicalValue canonical = LexiconValueNormalizer.NormalizeCorrection(entry.Name, entry.Type, entry.Facts).Value;

        using IServiceScope scope = factory.Services.CreateScope();

        Result<LabeledArtifactWriteReceipt> labelled = await scope.ServiceProvider
            .GetRequiredService<IArtifactSensitivityLedger>()
            .LabelAsync(
                new DerivedArtifactWrite(
                    SensitiveArtifactKind.Lexicon,
                    entry.Id,
                    sessionId: null,
                    entry.ScopeCampaignId,
                    turnId: null,
                    artifactRevision: 1,
                    DerivedArtifactContentDigest.ForBytes(LexiconSnapshotDigest.Encode(canonical)),
                    ContentSensitivity.CovenantDerived,
                    GenerationProvenance.CreateExact([Guid.NewGuid()])),
                CancellationToken.None);

        Assert.True(labelled.IsSuccess, labelled.IsFailure ? labelled.Error.Message : null);
    }

    private static async Task<LexiconEntryDetail> ShowAsync(MemoryErasureRouteDriver driver, string name, Guid? campaignId)
    {
        using HttpResponseMessage shown = await driver.PostAsync(
            "/api/memory/lexicon/show",
            new LexiconShowRequest(name, ScopeOf(campaignId)),
            ArcanumJsonContext.Default.LexiconShowRequest);

        Assert.Equal(HttpStatusCode.OK, shown.StatusCode);

        return await MemoryErasureRouteDriver.ReadDataAsync(shown, ArcanumJsonContext.Default.ApiResponseLexiconEntryDetail);
    }

    private static async Task AssertShowRefusedAsync(
        MemoryErasureRouteDriver driver,
        string name,
        Guid? campaignId,
        HttpStatusCode status,
        string code)
    {
        using HttpResponseMessage shown = await driver.PostAsync(
            "/api/memory/lexicon/show",
            new LexiconShowRequest(name, ScopeOf(campaignId)),
            ArcanumJsonContext.Default.LexiconShowRequest);

        Assert.Equal(status, shown.StatusCode);

        Assert.Equal(code, await MemoryErasureRouteDriver.ReadErrorCodeAsync(shown));
    }

    /// <summary>The effective active lookup a Campaign turn would see.</summary>
    private static async Task<LexiconEntryDto> EffectiveAsync(HttpClient client, Guid campaignId)
    {
        using HttpResponseMessage found = await client.GetAsync(
            $"/api/memory/lexicon/{Uri.EscapeDataString(Name)}?campaignId={campaignId}");

        Assert.Equal(HttpStatusCode.OK, found.StatusCode);

        return await MemoryErasureRouteDriver.ReadDataAsync(found, ArcanumJsonContext.Default.ApiResponseLexiconEntryDto);
    }

    private static LexiconCurationScope ScopeOf(Guid? campaignId) =>
        campaignId is null ? new(LexiconScopeKind.Global, null) : new(LexiconScopeKind.Campaign, campaignId);

    private static Task<HttpResponseMessage> PostPrepareAsync(MemoryErasureRouteDriver driver, LexiconErasePrepareRequest request) =>
        driver.PostAsync("/api/memory/lexicon/erase/prepare", request, ArcanumJsonContext.Default.LexiconErasePrepareRequest);

    private static async Task<MemoryErasurePreflightDto> PrepareOkAsync(MemoryErasureRouteDriver driver, LexiconErasePrepareRequest request)
    {
        using HttpResponseMessage response = await PostPrepareAsync(driver, request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await MemoryErasureRouteDriver.ReadDataAsync(response, ArcanumJsonContext.Default.ApiResponseMemoryErasurePreflightDto);
    }

    private static async Task AssertApplyRefusedAsync(
        MemoryErasureRouteDriver driver,
        LexiconEraseRequest request,
        HttpStatusCode status,
        string code)
    {
        using HttpResponseMessage response = await driver.PostAsync(
            "/api/memory/lexicon/erase",
            request,
            ArcanumJsonContext.Default.LexiconEraseRequest);

        await AssertRefusalAsync(response, status, code);
    }

    /// <summary>A refusal carries its code and no result.</summary>
    private static async Task AssertRefusalAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);

        Assert.Equal(code, await MemoryErasureRouteDriver.ReadErrorCodeAsync(response));

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // The envelope omits an absent result rather than writing a null one; either way there is none.
        Assert.False(
            body.RootElement.TryGetProperty("data", out JsonElement data)
            && data.ValueKind != JsonValueKind.Null);
    }

    private static string Id(LexiconEntryDto entry) => entry.Id.ToString("N");

    /// <summary>
    /// Every row the Lexicon plan owns for one entry, counted table by table with the relations spelled
    /// out here rather than borrowed from the plan. Assertion-only.
    /// </summary>
    private static async Task<long> PlanRowsAsync(ArcanumWebApplicationFactory factory, Guid entryId, bool requireRows = true)
    {
        const string claims = "SELECT ClaimId FROM annal_claims WHERE SubjectStoreCode = 2 AND SubjectId = $id";

        const string versions = "SELECT VersionId FROM annal_versions WHERE ClaimId IN (" + claims + ")";

        const string events = "SELECT Sequence FROM annal_review_events WHERE VersionId IN (" + versions + ")";

        string[] counts =
        [
            "SELECT count(*) FROM annal_review_decision_receipts WHERE ReviewEventSequence IN (" + events + ")",
            "SELECT count(*) FROM annal_review_events WHERE VersionId IN (" + versions + ")",
            "SELECT count(*) FROM annal_dependencies WHERE DependentVersionId IN (" + versions + ") OR DependencyVersionId IN (" + versions + ")",
            "SELECT count(*) FROM lexicon_annal_fact_provenance WHERE AnnalVersionId IN (" + versions + ")",
            "SELECT count(*) FROM annal_heads WHERE ClaimId IN (" + claims + ")",
            "SELECT count(*) FROM annal_versions WHERE ClaimId IN (" + claims + ")",
            claims.Replace("SELECT ClaimId", "SELECT count(*)", StringComparison.Ordinal),
            "SELECT count(*) FROM lexicon_fact_attachment_provenance WHERE EntryId = $id",
            "SELECT count(*) FROM lexicon_entries WHERE Id = $id",
        ];

        long total = 0;

        foreach (string count in counts)
        {
            total += await ScalarAsync(factory, count, entryId.ToString("N"));
        }

        if (requireRows)
        {
            Assert.True(total >= 4);
        }

        return total;
    }

    private static Task<long> ReceiptsAsync(ArcanumWebApplicationFactory factory, Guid mutationId) =>
        ScalarAsync(
            factory,
            "SELECT count(*) FROM memory_erasure_receipts WHERE MutationId = $id;",
            mutationId.ToString("D").ToUpperInvariant());

    private static Task<long> ReceiptSubjectsAsync(ArcanumWebApplicationFactory factory, Guid mutationId) =>
        ScalarAsync(
            factory,
            "SELECT count(*) FROM memory_erasure_receipt_subjects WHERE MutationId = $id;",
            mutationId.ToString("D").ToUpperInvariant());

    private static Task<long> LabelCountAsync(ArcanumWebApplicationFactory factory, Guid entryId) =>
        ScalarAsync(
            factory,
            "SELECT count(*) FROM artifact_sensitivity WHERE ArtifactKindCode = 7 AND ArtifactId = $id;",
            entryId.ToString("D").ToUpperInvariant());

    private static async Task<long> ScalarAsync(ArcanumWebApplicationFactory factory, string sql, string? id = null)
    {
        using IServiceScope scope = factory.Services.CreateScope();

        SqliteConnection connection = await OpenAsync(scope);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        if (id is not null)
        {
            _ = command.Parameters.AddWithValue("$id", id);
        }

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(ArcanumWebApplicationFactory factory, string sql)
    {
        using IServiceScope scope = factory.Services.CreateScope();

        SqliteConnection connection = await OpenAsync(scope);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

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
}
