using System.Data;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

/// <summary>Every ordinary lifecycle path one survival row runs.</summary>
public enum MemoryErasureLifecyclePath
{
    RetentionPrune = 1,

    SagaDeleteOne = 2,

    SagaDeleteAll = 3,

    LexiconDelete = 4,

    AgentLexiconDelete = 5,

    ResetEntry = 6,

    ResetAttachments = 7,

    ResetWorkspace = 8,

    ResetSaga = 9,

    ResetLexicon = 10,

    ResetCovenant = 11,

    ResetSagaCampaign = 12,

    ResetLexiconCampaign = 13,

    EmbeddingsResetSaga = 14,

    EmbeddingsResetAll = 15,

    CampaignDeletion = 16,

    SessionDeletion = 17,

    FactoryReset = 18,
}

/// <summary>
/// Erasure evidence and the erasure key survive every ordinary lifecycle path, and the erased identity
/// stays refused afterwards.
/// </summary>
/// <remarks>
/// <para>The design keeps fingerprints, receipts and receipt subjects through everything short of a full
/// installation reset, which removes the Grimoire and the key together. Each row here erases one item in
/// each store through the routes, runs one production lifecycle path through its own entry point, and then
/// requires three things: the path really removed its own victim, every evidence row and the key's stored
/// secret are byte-identical to what they were before, and the Saga chokepoint still refuses the erased
/// text. A path that reported success while deleting nothing would prove nothing, which is why each row
/// seeds a victim through a production writer and asserts it is gone.</para>
///
/// <para>The evidence is read on a fresh read-only lease from the host's ordinary connection factory, the
/// host's own view of its Grimoire, and the key from the test's in-memory credential store.</para>
/// </remarks>
[Collection("ApiHost")]
public sealed class MemoryErasureLifecycleSurvivalTests
{
    private const string T = "The toll bridge is closed on feast days.";

    private const string Victim = "The ferry leaves the east quay at noon.";

    private const string ErasedLexiconName = "Toll Keeper";

    private const string VictimLexiconName = "Ferry Master";

    private const string ErasedCovenantKey = "survival.key";

    private const string VictimCovenantKey = "survival.second";

    private const string EvidenceStore =
        "memory_erasure_fingerprints + memory_erasure_receipts + memory_erasure_receipt_subjects";

    private static readonly TimeSpan IndexingDeadline = TimeSpan.FromSeconds(60);

    private static CancellationToken Token => CancellationToken.None;

    public static TheoryData<MemoryErasureLifecyclePath> Paths()
    {
        TheoryData<MemoryErasureLifecyclePath> paths = [];

        foreach (MemoryErasureLifecyclePath path in Enum.GetValues<MemoryErasureLifecyclePath>())
        {
            paths.Add(path);
        }

        return paths;
    }

    [SkippableTheory]
    [MemberData(nameof(Paths))]
    public async Task Erasure_evidence_and_the_key_survive(MemoryErasureLifecyclePath path)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        // Attachment and workspace index rows are written only by their background indexers, and only
        // while that retrieval feature is on; every other row runs on exactly the host the route driver
        // composes.
        await using ArcanumWebApplicationFactory factory = path switch
        {
            // The prune row enables every retention rule, so it needs every producer whose rows a rule
            // can select: the workspace indexer and both dated audit logs.
            MemoryErasureLifecyclePath.RetentionPrune => MemoryErasureRouteDriver.Host(
                credentials,
                covenant: true,
                configure: static settings =>
                {
                    settings.Features.CodebaseRetrieval = true;

                    settings.Host.AuditLog.Enabled = true;

                    settings.Security.Guardrails.AuditLog.Enabled = true;
                }),

            MemoryErasureLifecyclePath.ResetAttachments => MemoryErasureRouteDriver.Host(
                credentials,
                covenant: true,
                configure: static settings => settings.Features.AttachmentRetrieval = true),

            MemoryErasureLifecyclePath.ResetWorkspace => MemoryErasureRouteDriver.Host(
                credentials,
                covenant: true,
                configure: static settings => settings.Features.CodebaseRetrieval = true),

            _ => MemoryErasureRouteDriver.CreateFactory(credentials),
        };

        SurvivalHost host = await SurvivalHost.EraseOneOfEachAsync(factory, credentials);

        MemoryErasureRetainedSnapshot before = await host.CaptureAsync();

        Assert.Equal((3, 3, 3), (before.Fingerprints.Count, before.Receipts.Count, before.Subjects.Count));

        Assert.NotNull(before.KeySecret);

        LifecycleVictim victim = await host.SeedVictimAsync(path);

        // Every probe sees its victim before the path runs, so a probe that stopped seeing it could not
        // let a path pass without deleting anything.
        Assert.All(
            await host.VictimStatesAsync(path, victim),
            static state => Assert.True(state.Present, $"{state.Name} was absent before the path ran."));

        await host.RunAsync(path, victim);

        Assert.All(
            await host.VictimStatesAsync(path, victim),
            static state => Assert.False(state.Present, $"{state.Name} survived the path."));

        await host.AssertRetainedAsync(before);

        Assert.Equal(SagaMemoryWriteOutcome.Suppressed, await MemoryErasureRouteDriver.InsertSagaOutcomeAsync(factory, T));
    }

    [SkippableFact]
    public async Task Status_reports_erasure_evidence_under_a_never_aged_class()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.CreateFactory(credentials);

        SurvivalHost host = await SurvivalHost.EraseOneOfEachAsync(factory, credentials);

        using HttpResponseMessage response = await host.Client.GetAsync("/api/data/status", Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        DataRetentionStatus status = await MemoryErasureRouteDriver.ReadDataAsync(
            response,
            ArcanumJsonContext.Default.ApiResponseDataRetentionStatus);

        DataRetentionStatusItem evidence = Assert.Single(
            status.Items,
            static item => item.DataClass == RetentionDataClass.MemoryErasureEvidence);

        Assert.Equal(9, evidence.Rows);

        Assert.False(evidence.PolicyEnabled);

        Assert.Null(evidence.RetentionDays);

        Assert.Equal(EvidenceStore, evidence.Store);

        Assert.Equal(new DataRetentionMemoryErasureInventory(3, 3, 3), status.MemoryErasure);
    }

    /// <summary>
    /// The factory preview states how much evidence stays in force, and only as a report: it is not a
    /// plan item and it is not part of the plan's identity.
    /// </summary>
    [SkippableFact]
    public async Task The_factory_plan_states_the_fingerprints_that_remain_in_force()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.CreateFactory(credentials);

        SurvivalHost host = await SurvivalHost.EraseOneOfEachAsync(factory, credentials);

        DataRetentionPlan first = await host.PlanFactoryResetAsync();

        Assert.Equal(new DataRetentionMemoryErasureInventory(3, 3, 3), first.MemoryErasure);

        // A release deletes one fingerprint and nothing the plan selects.
        MemoryErasureReleaseResultDto released = await host.Driver.ReleaseSagaAsync(
            new SagaErasureReleaseRequest(SagaMemoryScopeKind.Global, null, T),
            Token);

        Assert.Equal((MemoryErasureReleaseOutcome.Released, 1), (released.Outcome, released.ReleasedCount));

        DataRetentionPlan second = await host.PlanFactoryResetAsync();

        Assert.Equal(new DataRetentionMemoryErasureInventory(2, 3, 3), second.MemoryErasure);

        Assert.Equal(first.PlanId, second.PlanId);

        Assert.DoesNotContain(first.Items, static item => item.DataClass == RetentionDataClass.MemoryErasureEvidence);

        Assert.DoesNotContain(second.Items, static item => item.DataClass == RetentionDataClass.MemoryErasureEvidence);
    }

    [SkippableFact]
    public async Task A_rule_update_for_erasure_evidence_is_refused_with_its_own_reason()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        InMemoryOsCredentialStore credentials = new();

        await using ArcanumWebApplicationFactory factory = MemoryErasureRouteDriver.CreateFactory(credentials);

        using HttpClient client = factory.CreateAuthenticatedClient();

        string before = await RetentionSettingsJsonAsync(client);

        using HttpResponseMessage refused = await client.PutAsync(
            "/api/data/retention",
            JsonContent.Create(
                new RetentionRuleUpdateRequest("memory-erasure-evidence", true, 1),
                ArcanumJsonContext.Default.RetentionRuleUpdateRequest),
            Token);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        using JsonDocument body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(Token));

        JsonElement error = body.RootElement.GetProperty("error");

        Assert.Equal(ErrorCodes.Data.InvalidRequest, error.GetProperty("code").GetString());

        string message = error.GetProperty("message").GetString()!;

        Assert.StartsWith(
            "Erasure evidence has no time-based retention rule",
            message,
            StringComparison.Ordinal);

        // The refusal names the closed list of ways evidence is removed, the same five every document
        // names: a release, an operator re-creation, reset-key, a restore's join, a full installation reset.
        foreach (string removal in (string[])
                 [
                     "a release",
                     "operator re-creation",
                     "'memory erasure reset-key'",
                     "restore's destination-authoritative join",
                     "a full installation reset",
                 ])
        {
            Assert.Contains(removal, message, StringComparison.Ordinal);
        }

        Assert.Equal(before, await RetentionSettingsJsonAsync(client));
    }

    private static async Task<string> RetentionSettingsJsonAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync("/api/data/retention", Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));

        return body.RootElement.GetProperty("data").GetRawText();
    }

    /// <summary>What one row seeded for its lifecycle path to remove, and how to find it again.</summary>
    private sealed record LifecycleVictim(string Id, string? Generation = null, IReadOnlyList<PruneVictim>? Prune = null);

    /// <summary>One aged row or file a retention rule selects, and the check that it is still there.</summary>
    private sealed record PruneVictim(string Name, string Sql, string Id, string? FilePath = null);

    /// <summary>Whether one victim of a row is still present.</summary>
    private sealed record VictimState(string Name, bool Present);

    /// <summary>
    /// One host with one item erased in each store, and the routes and services a lifecycle row drives.
    /// </summary>
    private sealed class SurvivalHost
    {
        private SurvivalHost(
            ArcanumWebApplicationFactory factory,
            InMemoryOsCredentialStore credentials,
            HttpClient client,
            Guid campaign)
        {
            Factory = factory;

            Credentials = credentials;

            Client = client;

            Driver = new MemoryErasureRouteDriver(client);

            Campaign = campaign;
        }

        internal ArcanumWebApplicationFactory Factory { get; }

        internal InMemoryOsCredentialStore Credentials { get; }

        internal HttpClient Client { get; }

        internal MemoryErasureRouteDriver Driver { get; }

        /// <summary>Campaign C, whose Lexicon entry was erased.</summary>
        internal Guid Campaign { get; }

        /// <summary>
        /// Erases a Global Saga memory, a Campaign-C Lexicon entry and a Global Covenant key, each through
        /// its own erase routes, leaving three fingerprints, three receipts and three subjects.
        /// </summary>
        internal static async Task<SurvivalHost> EraseOneOfEachAsync(
            ArcanumWebApplicationFactory factory,
            InMemoryOsCredentialStore credentials)
        {
            HttpClient client = factory.CreateAuthenticatedClient();

            Guid campaign = await RegisterCampaignAsync(factory, client, "survival-c");

            SurvivalHost host = new(factory, credentials, client, campaign);

            string memory = await MemoryErasureRouteDriver.InsertSagaAsync(factory, T, ct: Token);

            _ = await host.Driver.EraseSagaAsync(memory, ct: Token);

            await host.ScribeAsync(ErasedLexiconName, LexiconScope.ForCampaign(campaign));

            _ = await host.Driver.EraseLexiconAsync(ErasedLexiconName, campaign, ct: Token);

            _ = await host.Driver.SetCovenantAsync(
                CovenantScope.Global,
                null,
                ErasedCovenantKey,
                "The toll is two coppers.",
                Token);

            _ = await host.Driver.EraseCovenantAsync(CovenantScope.Global, null, ErasedCovenantKey, ct: Token);

            return host;
        }

        internal Task<MemoryErasureRetainedSnapshot> CaptureAsync() =>
            WithReadOnlyLeaseAsync(connection =>
                MemoryErasureRetainedEvidence.CaptureAsync(connection, Credentials, Token));

        internal Task AssertRetainedAsync(MemoryErasureRetainedSnapshot before) =>
            WithReadOnlyLeaseAsync(async connection =>
            {
                await MemoryErasureRetainedEvidence.AssertRetainedAsync(before, connection, Credentials, Token);

                return true;
            });

        internal async Task<DataRetentionPlan> PlanFactoryResetAsync()
        {
            await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();

            return await scope.ServiceProvider
                .GetRequiredService<IDataRetentionService>()
                .PlanAsync(new DataRetentionRequest(DataRetentionOperation.FactoryReset), Token);
        }

        internal async Task<LifecycleVictim> SeedVictimAsync(MemoryErasureLifecyclePath path)
        {
            switch (path)
            {
                case MemoryErasureLifecyclePath.RetentionPrune:
                    return new(string.Empty, Prune: await SeedPruneVictimsAsync());

                case MemoryErasureLifecyclePath.SagaDeleteOne
                    or MemoryErasureLifecyclePath.SagaDeleteAll
                    or MemoryErasureLifecyclePath.ResetSaga
                    or MemoryErasureLifecyclePath.EmbeddingsResetSaga
                    or MemoryErasureLifecyclePath.EmbeddingsResetAll:
                    return new(await MemoryErasureRouteDriver.InsertSagaAsync(Factory, Victim, ct: Token));

                case MemoryErasureLifecyclePath.LexiconDelete
                    or MemoryErasureLifecyclePath.AgentLexiconDelete
                    or MemoryErasureLifecyclePath.ResetLexicon:
                    await ScribeAsync(VictimLexiconName, LexiconScope.Global);

                    return new(VictimLexiconName);

                case MemoryErasureLifecyclePath.ResetEntry:
                    return new(await SeedEntryEmbeddingAsync());

                case MemoryErasureLifecyclePath.ResetAttachments:
                    return await SeedIndexedAttachmentAsync();

                case MemoryErasureLifecyclePath.ResetWorkspace:
                    return new(await SeedIndexedWorkspaceAsync());

                case MemoryErasureLifecyclePath.ResetCovenant:
                    _ = await Driver.SetCovenantAsync(
                        CovenantScope.Global,
                        null,
                        VictimCovenantKey,
                        "The ferry toll is one copper.",
                        Token);

                    return new(VictimCovenantKey);

                case MemoryErasureLifecyclePath.ResetSagaCampaign:
                    return new(await SeedCampaignSagaAsync());

                case MemoryErasureLifecyclePath.ResetLexiconCampaign:
                    await ScribeAsync(VictimLexiconName, LexiconScope.ForCampaign(Campaign));

                    return new(VictimLexiconName);

                case MemoryErasureLifecyclePath.CampaignDeletion:
                    return new(Campaign.ToString("D"));

                case MemoryErasureLifecyclePath.SessionDeletion
                    or MemoryErasureLifecyclePath.FactoryReset:
                    return new((await CreateSessionAsync()).ToString("D"));

                default:
                    throw new ArgumentOutOfRangeException(nameof(path), path, null);
            }
        }

        internal async Task RunAsync(MemoryErasureLifecyclePath path, LifecycleVictim victim)
        {
            switch (path)
            {
                case MemoryErasureLifecyclePath.RetentionPrune:
                    await PruneEveryRuleAsync();

                    return;

                case MemoryErasureLifecyclePath.SagaDeleteOne:
                    await RequireNoContentAsync(await Client.DeleteAsync($"/api/saga/{victim.Id}", Token));

                    return;

                case MemoryErasureLifecyclePath.SagaDeleteAll:
                    await RequireNoContentAsync(await Client.DeleteAsync("/api/saga?confirm=true", Token));

                    return;

                case MemoryErasureLifecyclePath.LexiconDelete:
                    await RequireNoContentAsync(
                        await Client.DeleteAsync($"/api/memory/lexicon/{Uri.EscapeDataString(victim.Id)}", Token));

                    return;

                case MemoryErasureLifecyclePath.AgentLexiconDelete:
                {
                    await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();

                    Result<bool> deleted = await scope.ServiceProvider
                        .GetRequiredService<ILexiconService>()
                        .DeleteByNameAsync(victim.Id, LexiconScope.Global, LexiconDeletionOrigin.Agent, Token);

                    Assert.True(deleted.IsSuccess, deleted.IsFailure ? deleted.Error.Message : null);

                    Assert.True(deleted.Value);

                    return;
                }

                case MemoryErasureLifecyclePath.ResetEntry:
                    RequireRemoved(await ResetMemoryAsync(new MemoryResetRequest(MemoryResetScope.Entry)));

                    return;

                case MemoryErasureLifecyclePath.ResetAttachments:
                    RequireRemoved(await ResetMemoryAsync(new MemoryResetRequest(MemoryResetScope.Attachments)));

                    return;

                case MemoryErasureLifecyclePath.ResetWorkspace:
                    RequireRemoved(await ResetMemoryAsync(new MemoryResetRequest(MemoryResetScope.Workspace)));

                    return;

                case MemoryErasureLifecyclePath.ResetSaga:
                    RequireRemoved(await ResetMemoryAsync(new MemoryResetRequest(MemoryResetScope.Saga)));

                    return;

                case MemoryErasureLifecyclePath.ResetLexicon:
                    RequireRemoved(await ResetMemoryAsync(new MemoryResetRequest(MemoryResetScope.Lexicon)));

                    return;

                case MemoryErasureLifecyclePath.ResetCovenant:
                    // The Covenant reset reports zero deletion counters by contract: its retained
                    // disclosure evidence is part of the preview, so the victim check is what proves it ran.
                    _ = await ResetMemoryAsync(new MemoryResetRequest(MemoryResetScope.Covenant));

                    return;

                case MemoryErasureLifecyclePath.ResetSagaCampaign:
                    RequireRemoved(await ResetMemoryAsync(
                        new MemoryResetRequest(MemoryResetScope.Saga, CampaignId: Campaign)));

                    return;

                case MemoryErasureLifecyclePath.ResetLexiconCampaign:
                    RequireRemoved(await ResetMemoryAsync(
                        new MemoryResetRequest(MemoryResetScope.Lexicon, CampaignId: Campaign)));

                    return;

                case MemoryErasureLifecyclePath.EmbeddingsResetSaga:
                    await RequireOkAsync(await Client.PostAsync("/api/embeddings/reset?confirm=true&scope=saga", null, Token));

                    return;

                case MemoryErasureLifecyclePath.EmbeddingsResetAll:
                    await RequireOkAsync(await Client.PostAsync("/api/embeddings/reset?confirm=true&scope=all", null, Token));

                    return;

                case MemoryErasureLifecyclePath.CampaignDeletion:
                    await RequireNoContentAsync(await Client.DeleteAsync($"/api/campaigns/{victim.Id}", Token));

                    return;

                case MemoryErasureLifecyclePath.SessionDeletion:
                {
                    using HttpResponseMessage deleted = await Client.DeleteAsync($"/api/data/sessions/{victim.Id}", Token);

                    Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

                    RequireApplied(await MemoryErasureRouteDriver.ReadDataAsync(
                        deleted,
                        ArcanumJsonContext.Default.ApiResponseDataRetentionApplyResult));

                    return;
                }

                case MemoryErasureLifecyclePath.FactoryReset:
                    RequireApplied(await FactoryResetAsync());

                    return;

                default:
                    throw new ArgumentOutOfRangeException(nameof(path), path, null);
            }
        }

        /// <summary>Each victim the row seeded, and whether it is still present.</summary>
        internal async Task<IReadOnlyList<VictimState>> VictimStatesAsync(
            MemoryErasureLifecyclePath path,
            LifecycleVictim victim)
        {
            if (victim.Prune is { } prune)
            {
                List<VictimState> states = [];

                foreach (PruneVictim aged in prune)
                {
                    bool present = aged.FilePath is { } file
                        ? File.Exists(file)
                        : await CountAsync(aged.Sql, aged.Id) > 0;

                    states.Add(new(aged.Name, present));
                }

                return states;
            }

            return [new(path.ToString(), !await VictimGoneAsync(path, victim))];
        }

        private async Task<bool> VictimGoneAsync(MemoryErasureLifecyclePath path, LifecycleVictim victim)
        {
            switch (path)
            {
                case MemoryErasureLifecyclePath.SagaDeleteOne
                    or MemoryErasureLifecyclePath.SagaDeleteAll
                    or MemoryErasureLifecyclePath.ResetSaga
                    or MemoryErasureLifecyclePath.ResetSagaCampaign
                    or MemoryErasureLifecyclePath.EmbeddingsResetSaga
                    or MemoryErasureLifecyclePath.EmbeddingsResetAll:
                    return await CountAsync("SELECT count(*) FROM saga_memories WHERE Id = $id;", victim.Id) == 0;

                case MemoryErasureLifecyclePath.LexiconDelete
                    or MemoryErasureLifecyclePath.AgentLexiconDelete
                    or MemoryErasureLifecyclePath.ResetLexicon
                    or MemoryErasureLifecyclePath.ResetLexiconCampaign:
                    return await CountAsync(
                        "SELECT count(*) FROM lexicon_entries WHERE upper(trim(Name)) = upper($id);",
                        victim.Id) == 0;

                case MemoryErasureLifecyclePath.ResetEntry:
                    return await CountAsync("SELECT count(*) FROM entry_embeddings WHERE EntryId = $id;", victim.Id) == 0;

                case MemoryErasureLifecyclePath.ResetAttachments:
                    return await CountAsync(
                            "SELECT count(*) FROM session_attachment_chunks WHERE GenerationId = $id;",
                            victim.Generation!) == 0
                        && await CountAsync(
                            "SELECT count(*) FROM session_attachment_index_state WHERE PublishedGenerationId = $id;",
                            victim.Generation!) == 0;

                case MemoryErasureLifecyclePath.ResetWorkspace:
                    return await CountAsync(
                        "SELECT count(*) FROM workspace_file_chunks WHERE WorkspacePath = $id;",
                        victim.Id) == 0;

                case MemoryErasureLifecyclePath.ResetCovenant:
                    return await CovenantCountAsync(
                        "SELECT count(*) FROM covenant_entries WHERE NormalizedKey = $id;",
                        victim.Id) == 0;

                case MemoryErasureLifecyclePath.CampaignDeletion:
                {
                    using HttpResponseMessage shown = await Client.GetAsync($"/api/campaigns/{victim.Id}", Token);

                    return shown.StatusCode == HttpStatusCode.NotFound;
                }

                case MemoryErasureLifecyclePath.SessionDeletion
                    or MemoryErasureLifecyclePath.FactoryReset:
                    return await CountAsync(
                        "SELECT count(*) FROM Sessions WHERE lower(replace(Id, '-', '')) = $id;",
                        Guid.Parse(victim.Id).ToString("N")) == 0;

                default:
                    throw new ArgumentOutOfRangeException(nameof(path), path, null);
            }
        }

        internal async Task ScribeAsync(string name, LexiconScope scope)
        {
            await using AsyncServiceScope services = Factory.Services.CreateAsyncScope();

            Result<LexiconEntryDto> scribed = await services.ServiceProvider
                .GetRequiredService<ILexiconService>()
                .UpsertAsync(name, "Person", ["keeps the river crossing"], scope, Token);

            Assert.True(scribed.IsSuccess, scribed.IsFailure ? scribed.Error.Message : null);
        }

        /// <summary>
        /// The prune route with every retention rule enabled, applied at the plan it previewed.
        /// </summary>
        /// <remarks>
        /// Every rule the policy store accepts is enabled, so every prune executor that has a candidate
        /// runs once with erasure evidence present. Entries keep ten days rather than one, so the entry
        /// aged three days for the embedding rule is selected by that rule alone; accounting ages by its
        /// own floor whatever its rule says.
        /// </remarks>
        private async Task PruneEveryRuleAsync()
        {
            foreach ((string dataClass, int days) in PruneRules)
            {
                using HttpResponseMessage enabled = await Client.PutAsync(
                    "/api/data/retention",
                    JsonContent.Create(
                        new RetentionRuleUpdateRequest(dataClass, true, days),
                        ArcanumJsonContext.Default.RetentionRuleUpdateRequest),
                    Token);

                Assert.True(enabled.StatusCode == HttpStatusCode.OK, $"{dataClass}: {(int)enabled.StatusCode}");
            }

            DataRetentionRequest request = new(DataRetentionOperation.Prune);

            DataRetentionPlan plan;

            using (HttpResponseMessage planned = await Client.PostAsync(
                "/api/data/prune/plan",
                JsonContent.Create(request, ArcanumJsonContext.Default.DataRetentionRequest),
                Token))
            {
                Assert.Equal(HttpStatusCode.OK, planned.StatusCode);

                await RequireNoErasureInventoryAsync(planned);

                plan = await MemoryErasureRouteDriver.ReadDataAsync(
                    planned,
                    ArcanumJsonContext.Default.ApiResponseDataRetentionPlan);
            }

            Assert.Empty(plan.Blockers);

            Assert.Empty(plan.Conflicts);

            using HttpResponseMessage pruned = await Client.PostAsync(
                "/api/data/prune",
                JsonContent.Create(
                    new DataRetentionApplyRequest(request, plan.PlanId),
                    ArcanumJsonContext.Default.DataRetentionApplyRequest),
                Token);

            Assert.Equal(HttpStatusCode.OK, pruned.StatusCode);

            DataRetentionApplyResult result = await MemoryErasureRouteDriver.ReadDataAsync(
                pruned,
                ArcanumJsonContext.Default.ApiResponseDataRetentionApplyResult);

            RequireApplied(result);

            Assert.True(result.Reconciled);
        }

        /// <summary>
        /// One aged victim for every prune executor this harness can feed, each written by a production
        /// writer and then aged.
        /// </summary>
        /// <remarks>
        /// <para>Where a writer takes its own timestamp the victim is written already old. Elsewhere the
        /// writer stamps "now", and only the timestamp the rule reads is moved back afterwards; for a
        /// dated audit file that is its last-write time.</para>
        ///
        /// <para>Nothing in production writes a standalone cost adjustment, so that executor's victim is
        /// the one row here seeded directly; its executor still runs its own <c>DELETE FROM
        /// CostAdjustments</c> on the Grimoire with the evidence present. Daemon history alone has no
        /// victim: it is process-local, stamped by its repository's own clock, and its executor touches no
        /// Grimoire table. The compiled-statement pin covers both executors' source.</para>
        /// </remarks>
        private async Task<IReadOnlyList<PruneVictim>> SeedPruneVictimsAsync()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;

            DateTimeOffset threeDaysAgo = now.AddDays(-3);

            DateTimeOffset accountingAge = now.AddDays(-400);

            List<PruneVictim> victims = [];

            await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();

            IServiceProvider services = scope.ServiceProvider;

            // Saga: the store takes the timestamp.
            string memory = Guid.NewGuid().ToString();

            Assert.Equal(
                SagaMemoryWriteOutcome.Written,
                await services.GetRequiredService<ISagaMemoryStore>().InsertAsync(
                    memory,
                    Victim,
                    threeDaysAgo,
                    null,
                    null,
                    "extraction",
                    Vector(),
                    Token));

            victims.Add(new("saga memory", "SELECT count(*) FROM saga_memories WHERE Id = $id;", memory));

            // Lexicon: written now, aged.
            await ScribeAsync(VictimLexiconName, LexiconScope.Global);

            await AgeAsync(
                "UPDATE lexicon_entries SET UpdatedAt = $at WHERE upper(trim(Name)) = upper($id);",
                VictimLexiconName,
                threeDaysAgo);

            victims.Add(new("lexicon entry", "SELECT count(*) FROM lexicon_entries WHERE upper(trim(Name)) = upper($id);", VictimLexiconName));

            // One fresh Session holds an old entry, an entry whose embedding alone is old, and an old
            // attachment; two more Sessions are old themselves, one of them archived.
            Guid holder = await CreateSessionAsync();

            string oldEntry = await AppendEntryAsync(holder, "Which quay does the ferry leave from?");

            string embeddedEntry = await AppendEntryAsync(holder, "When does the ferry leave?");

            EmbeddingSettings embeddings = Factory.Services
                .GetRequiredService<IOptionsMonitor<ArcanumSettings>>()
                .CurrentValue
                .ResolveEmbeddings();

            Assert.Equal(
                EntryWeavingTickOutcome.Woven,
                await Factory.Services.GetRequiredService<EntryWeavingService>().RunTickAsync(embeddings, Token));

            await AgeAsync("UPDATE Entries SET CreatedAt = $at WHERE Id = $id;", oldEntry, now.AddDays(-20));

            await AgeAsync("UPDATE Entries SET CreatedAt = $at WHERE Id = $id;", embeddedEntry, threeDaysAgo);

            victims.Add(new("entry", "SELECT count(*) FROM Entries WHERE Id = $id;", oldEntry));

            victims.Add(new("entry embedding", "SELECT count(*) FROM entry_embeddings WHERE EntryId = $id;", embeddedEntry));

            string attachment = (await UploadTextAttachmentAsync(holder)).ToString("N");

            await AgeAsync(
                "UPDATE SessionAttachments SET CreatedAt = $at WHERE lower(replace(Id, '-', '')) = $id;",
                attachment,
                threeDaysAgo);

            victims.Add(new("attachment", "SELECT count(*) FROM SessionAttachments WHERE lower(replace(Id, '-', '')) = $id;", attachment));

            string active = (await CreateSessionAsync()).ToString("N");

            await AgeAsync("UPDATE Sessions SET UpdatedAt = $at WHERE lower(replace(Id, '-', '')) = $id;", active, threeDaysAgo);

            victims.Add(new("active session", "SELECT count(*) FROM Sessions WHERE lower(replace(Id, '-', '')) = $id;", active));

            Guid archivedSession = await CreateSessionAsync();

            using (HttpResponseMessage archived = await Client.PatchAsync(
                $"/api/sessions/{archivedSession:D}",
                JsonContent.Create(
                    new UpdateSessionRequest(null, "archived"),
                    ArcanumJsonContext.Default.UpdateSessionRequest),
                Token))
            {
                Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
            }

            string archivedId = archivedSession.ToString("N");

            await AgeAsync("UPDATE Sessions SET UpdatedAt = $at WHERE lower(replace(Id, '-', '')) = $id;", archivedId, threeDaysAgo);

            victims.Add(new("archived session", "SELECT count(*) FROM Sessions WHERE lower(replace(Id, '-', '')) = $id AND Status = 'archived';", archivedId));

            // An uploaded file through the OpenAI-compatible route.
            string file = await UploadFileAsync();

            await AgeAsync("UPDATE UploadedFiles SET CreatedAt = $at WHERE lower(replace(Id, '-', '')) = $id;", file, threeDaysAgo);

            victims.Add(new("uploaded file", "SELECT count(*) FROM UploadedFiles WHERE lower(replace(Id, '-', '')) = $id;", file));

            // A terminal batch: the repository takes both timestamps. Its input is a fresh upload of
            // its own, which no rule selects.
            Guid batch = Guid.NewGuid();

            Guid batchInput = Guid.Parse(await UploadFileAsync());

            await services.GetRequiredService<IBatchRepository>().CreateAsync(
                new BatchRecord(
                    batch,
                    batchInput,
                    "/v1/chat/completions",
                    BatchStatuses.Completed,
                    threeDaysAgo,
                    threeDaysAgo,
                    null,
                    null),
                Token);

            victims.Add(new("completed batch", "SELECT count(*) FROM Batches WHERE lower(replace(Id, '-', '')) = $id;", batch.ToString("N")));

            // Workspace chunks from the host's own indexer, aged.
            string workspace = await SeedIndexedWorkspaceAsync();

            await AgeAsync("UPDATE workspace_file_chunks SET IndexedAt = $at WHERE WorkspacePath = $id;", workspace, threeDaysAgo);

            victims.Add(new("workspace chunks", "SELECT count(*) FROM workspace_file_chunks WHERE WorkspacePath = $id;", workspace));

            // A terminal idempotency claim, aged, and a legacy completed-response key written old.
            IIdempotencyClaimStore claims = services.GetRequiredService<IIdempotencyClaimStore>();

            IdempotencyClaimAcquireResult acquired = await claims.TryAcquireAsync(
                new IdempotencyClaimAcquireRequest(
                    Convert.ToHexString(Guid.NewGuid().ToByteArray()),
                    Convert.ToHexString(Guid.NewGuid().ToByteArray()),
                    "survival-owner",
                    now.AddMinutes(5),
                    now),
                Token);

            Assert.True(acquired.Acquired);

            await claims.CompleteAsync(acquired.Claim.Id, "survival-owner", 200, "application/json", "{}", true, null, Token);

            string claim = acquired.Claim.Id.ToString("N");

            await AgeAsync(
                "UPDATE IdempotencyClaims SET CreatedAt = $at, UpdatedAt = $at, LeaseExpiresAt = $at WHERE lower(replace(Id, '-', '')) = $id;",
                claim,
                threeDaysAgo);

            victims.Add(new("idempotency claim", "SELECT count(*) FROM IdempotencyClaims WHERE lower(replace(Id, '-', '')) = $id;", claim));

            string keyHash = Convert.ToHexString(Guid.NewGuid().ToByteArray());

            await services.GetRequiredService<IIdempotencyStore>().SaveAsync(keyHash, 200, "application/json", "{}", threeDaysAgo, Token);

            victims.Add(new("idempotency key", "SELECT count(*) FROM IdempotencyKeys WHERE KeyHash = $id;", keyHash));

            // Accounting: a finished run with no Session, and a budget alert, both past the floor.
            ITurnRunWriter runs = services.GetRequiredService<ITurnRunWriter>();

            Guid run = await runs.StartRunAsync(
                new InferenceRunStart("survival-" + Guid.NewGuid().ToString("N"), null, "test", "survival", null, accountingAge),
                Token);

            await runs.CompleteRunAsync(run, InferenceRunStatus.Completed, Token);

            await AgeAsync(
                "UPDATE InferenceRuns SET StartedAt = $at, CompletedAt = $at WHERE lower(replace(Id, '-', '')) = $id;",
                run.ToString("N"),
                accountingAge);

            victims.Add(new("inference run", "SELECT count(*) FROM InferenceRuns WHERE lower(replace(Id, '-', '')) = $id;", run.ToString("N")));

            Assert.True(await services.GetRequiredService<IBudgetAlertRepository>().RecordAlertAsync(80, 8m, 10m, Token));

            string alert = await ScalarTextAsync("SELECT Id FROM BudgetAlerts ORDER BY AlertedAt DESC LIMIT 1;", string.Empty);

            await AgeAsync("UPDATE BudgetAlerts SET AlertedAt = $at WHERE Id = $id;", alert, accountingAge);

            victims.Add(new("budget alert", "SELECT count(*) FROM BudgetAlerts WHERE Id = $id;", alert));

            // A standalone cost adjustment. No production code writes one, so this row is seeded directly.
            string adjustment = Guid.NewGuid().ToString("D");

            await AgeAsync(
                "INSERT INTO CostAdjustments (Id, BillableOperationId, RunId, Reason, CreatedAt, AmountUsd) VALUES ($id, NULL, NULL, 'survival', $at, '0');",
                adjustment,
                accountingAge);

            victims.Add(new("standalone cost adjustment", "SELECT count(*) FROM CostAdjustments WHERE Id = $id;", adjustment));

            // A terminal long-running operation, written with its own old timestamps.
            ILongRunningOperationStore operations = services.GetRequiredService<ILongRunningOperationStore>();

            LongRunningOperation operation = await operations.CreateAsync(
                new LongRunningOperationCreateRequest(
                    LongRunningOperationKinds.DataRetentionMutation,
                    LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
                    "Survival history.",
                    threeDaysAgo),
                Token);

            LongRunningOperationLeaseResult lease = await operations.TryAcquireLeaseAsync(
                operation.Id,
                "survival-owner",
                threeDaysAgo,
                threeDaysAgo.AddMinutes(5),
                Token);

            Assert.True(lease.Acquired);

            Assert.True(await operations.TryTransitionAsync(
                operation.Id,
                lease.Operation.Revision,
                "survival-owner",
                LongRunningOperationState.Completed,
                threeDaysAgo.AddMinutes(1),
                cancellationToken: Token));

            victims.Add(new("long-running operation", "SELECT count(*) FROM LongRunningOperations WHERE lower(replace(Id, '-', '')) = $id;", operation.Id.ToString("N")));

            // A Sanctum breach, written old. The repository assigns its own identity.
            await services.GetRequiredService<ISanctumBreachRepository>().RecordAsync(
                new SanctumBreachRecord(string.Empty, Campaign.ToString("D"), threeDaysAgo, "survival_tool", "PathEscape", "Survival breach.", null),
                100,
                Token);

            string breach = await ScalarTextAsync("SELECT Id FROM SanctumBreaches WHERE ToolName = $id;", "survival_tool");

            victims.Add(new("sanctum breach", "SELECT count(*) FROM SanctumBreaches WHERE Id = $id;", breach));

            // Both dated audit logs, written today by their own loggers, then aged.
            await services.GetRequiredService<IInferenceAuditLogger>().LogAsync(
                new InferenceAuditRecord(now.ToString("O"), null, "survival", null, null, 0, 0, 0, 0, 0, [], null, null, null, null, null),
                Token);

            victims.Add(new("audit log", string.Empty, string.Empty, BackdateLog("audit", threeDaysAgo)));

            await services.GetRequiredService<IGuardrailAuditLogger>().LogAsync(
                new GuardrailAuditRecord(now.ToString("O"), null, "input", "survival", null, null),
                Token);

            victims.Add(new("guardrail log", string.Empty, string.Empty, BackdateLog("guardrails", threeDaysAgo)));

            return victims;
        }

        /// <summary>The rules the prune row enables, with their days.</summary>
        private static readonly (string DataClass, int Days)[] PruneRules =
        [
            ("active-sessions", 1),
            ("archived-sessions", 1),
            ("entries", 10),
            ("attachments", 1),
            ("uploaded-files", 1),
            ("completed-batches", 1),
            ("saga-memories", 1),
            ("lexicon-entries", 1),
            ("workspace-indexes", 1),
            ("session-entry-embeddings", 1),
            ("audit-logs", 1),
            ("guardrail-logs", 1),
            ("idempotency-claims", 1),
            ("accounting", 1),
            ("long-running-operations", 1),
            ("sanctum-breaches", 1),
            ("daemon-history", 1),
        ];

        /// <summary>
        /// Ages today's dated log of one family: renamed to an older date, so its logger never appends to
        /// it again, and given that date as its last write, which is the age the prune reads.
        /// </summary>
        private string BackdateLog(string stem, DateTimeOffset at)
        {
            string directory = Path.Combine(Factory.TempHome, ".config", "arcanum");

            string today = Path.Combine(directory, $"{stem}-{DateTimeOffset.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.jsonl");

            Assert.True(File.Exists(today), $"the {stem} logger wrote no file for today");

            string aged = Path.Combine(directory, $"{stem}-{at.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.jsonl");

            File.Move(today, aged);

            File.SetLastWriteTimeUtc(aged, at.UtcDateTime);

            return aged;
        }

        /// <summary>
        /// Moves one timestamp back on rows a production writer just wrote, or seeds the one row no
        /// production code writes. Setup-only.
        /// </summary>
        private async Task AgeAsync(string sql, string id, DateTimeOffset at)
        {
            await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();

            ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

            SqliteConnection connection = (SqliteConnection)db.Database.GetDbConnection();

            if (connection.State != ConnectionState.Open)
            {
                await db.Database.OpenConnectionAsync(Token);
            }

            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = sql;

            _ = command.Parameters.AddWithValue("$id", id);

            _ = command.Parameters.AddWithValue("$at", UtcInstantText.Format(at));

            Assert.True(await command.ExecuteNonQueryAsync(Token) >= 1, $"Nothing to age: {sql}");
        }

        private async Task<string> AppendEntryAsync(Guid session, string content)
        {
            using (HttpResponseMessage appended = await Client.PostAsync(
                $"/api/sessions/{session:D}/entries",
                JsonContent.Create(
                    new AppendEntryRequest(MessageRole.User, content),
                    ArcanumJsonContext.Default.AppendEntryRequest),
                Token))
            {
                Assert.Equal(HttpStatusCode.OK, appended.StatusCode);
            }

            return await ScalarTextAsync(
                "SELECT Id FROM Entries WHERE lower(replace(SessionId, '-', '')) = $id ORDER BY Sequence DESC LIMIT 1;",
                session.ToString("N"));
        }

        private async Task<Guid> UploadTextAttachmentAsync(Guid session)
        {
            using MultipartFormDataContent form = new();

            using ByteArrayContent content = new(Encoding.UTF8.GetBytes("The ferry leaves the east quay at noon on market days."));

            content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");

            form.Add(content, "file", "ferry-notes.txt");

            using HttpResponseMessage uploaded = await Client.PostAsync($"/api/sessions/{session:D}/attachments", form, Token);

            Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);

            return (await MemoryErasureRouteDriver.ReadDataAsync(
                uploaded,
                ArcanumJsonContext.Default.ApiResponseSessionAttachmentDto)).Id;
        }

        /// <summary>One file through <c>POST /v1/files</c>; returns its stored identity.</summary>
        private async Task<string> UploadFileAsync()
        {
            using MultipartFormDataContent form = new();

            using ByteArrayContent content = new([1, 2, 3, 4]);

            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            form.Add(content, "file", "ferry-ledger.bin");

            form.Add(new StringContent("assistants"), "purpose");

            using HttpResponseMessage uploaded = await Client.PostAsync("/v1/files", form, Token);

            Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);

            using JsonDocument body = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync(Token));

            string id = body.RootElement.GetProperty("id").GetString()!;

            Assert.StartsWith("file-", id, StringComparison.Ordinal);

            return id["file-".Length..];
        }

        /// <summary>A plan that is not a factory plan carries no erasure inventory on the wire.</summary>
        private static async Task RequireNoErasureInventoryAsync(HttpResponseMessage response)
        {
            using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));

            Assert.False(
                body.RootElement.GetProperty("data").TryGetProperty("memoryErasure", out _),
                "A plan other than the factory plan carried a memoryErasure member.");
        }

        /// <summary>The reset-plan route, then the reset route at the plan it previewed.</summary>
        private async Task<DataRetentionApplyResult> ResetMemoryAsync(MemoryResetRequest request)
        {
            DataRetentionPlan plan;

            using (HttpResponseMessage planned = await Client.PostAsync(
                "/api/data/memory/reset/plan",
                JsonContent.Create(request, ArcanumJsonContext.Default.MemoryResetRequest),
                Token))
            {
                Assert.Equal(HttpStatusCode.OK, planned.StatusCode);

                await RequireNoErasureInventoryAsync(planned);

                plan = await MemoryErasureRouteDriver.ReadDataAsync(
                    planned,
                    ArcanumJsonContext.Default.ApiResponseDataRetentionPlan);
            }

            Assert.Empty(plan.Blockers);

            Assert.Empty(plan.Conflicts);

            using HttpResponseMessage reset = await Client.PostAsync(
                "/api/data/memory/reset",
                JsonContent.Create(
                    request with { ExpectedPlanId = plan.PlanId },
                    ArcanumJsonContext.Default.MemoryResetRequest),
                Token);

            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

            DataRetentionApplyResult result = await MemoryErasureRouteDriver.ReadDataAsync(
                reset,
                ArcanumJsonContext.Default.ApiResponseDataRetentionApplyResult);

            Assert.Empty(result.Blockers);

            Assert.Empty(result.Conflicts);

            Assert.True(result.Reconciled);

            return result;
        }

        /// <summary>
        /// The host's factory plan and apply, the apply taken inside a request admission lease the way the
        /// admission middleware takes one before an endpoint runs.
        /// </summary>
        private async Task<DataRetentionApplyResult> FactoryResetAsync()
        {
            DataRetentionPlan plan = await PlanFactoryResetAsync();

            await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();

            Assert.True(
                scope.ServiceProvider
                    .GetRequiredService<GrimoireRequestAdmissionScope>()
                    .TryAdmit(GrimoireRequestKind.Finite),
                "the test could not take the request lease the middleware would have taken");

            Result<DataRetentionApplyResult> applied = await scope.ServiceProvider
                .GetRequiredService<IDataRetentionService>()
                .ApplyAsync(
                    new DataRetentionApplyRequest(
                        new DataRetentionRequest(DataRetentionOperation.FactoryReset),
                        plan.PlanId,
                        null),
                    Token);

            Assert.True(applied.IsSuccess, applied.IsFailure ? $"{applied.Error.Code}: {applied.Error.Message}" : null);

            return applied.Value;
        }

        /// <summary>
        /// A Session with one appended entry, and that entry's embedding written by one tick of the host's
        /// own entry weaving service.
        /// </summary>
        /// <remarks>
        /// The entry reset clears entry embeddings, not entries, so the embedding is the victim. Session
        /// search is off on this host, so the weaving loop idles and the one tick here is the only writer.
        /// </remarks>
        private async Task<string> SeedEntryEmbeddingAsync()
        {
            Guid session = await CreateSessionAsync();

            using (HttpResponseMessage appended = await Client.PostAsync(
                $"/api/sessions/{session:D}/entries",
                JsonContent.Create(
                    new AppendEntryRequest(MessageRole.User, "Which quay does the ferry leave from?"),
                    ArcanumJsonContext.Default.AppendEntryRequest),
                Token))
            {
                Assert.Equal(HttpStatusCode.OK, appended.StatusCode);
            }

            string entry = await ScalarTextAsync(
                "SELECT Id FROM Entries WHERE lower(replace(SessionId, '-', '')) = $id;",
                session.ToString("N"));

            EmbeddingSettings embeddings = Factory.Services
                .GetRequiredService<IOptionsMonitor<ArcanumSettings>>()
                .CurrentValue
                .ResolveEmbeddings();

            Assert.False(embeddings.SessionSearchEnabled);

            Assert.Equal(
                EntryWeavingTickOutcome.Woven,
                await Factory.Services.GetRequiredService<EntryWeavingService>().RunTickAsync(embeddings, Token));

            Assert.Equal(1, await CountAsync("SELECT count(*) FROM entry_embeddings WHERE EntryId = $id;", entry));

            return entry;
        }

        /// <summary>
        /// One text attachment uploaded through the route and indexed by the host's own background
        /// indexer; the victim is the index generation it published.
        /// </summary>
        /// <remarks>
        /// Naming the generation rather than the attachment keeps the check exact even if a later
        /// reconciliation pass indexes the attachment again, because every index pass publishes a new one.
        /// </remarks>
        private async Task<LifecycleVictim> SeedIndexedAttachmentAsync()
        {
            Guid session = await CreateSessionAsync();

            using MultipartFormDataContent form = new();

            using ByteArrayContent file = new(Encoding.UTF8.GetBytes("The ferry leaves the east quay at noon on market days."));

            file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");

            form.Add(file, "file", "ferry-notes.txt");

            SessionAttachmentDto attachment;

            using (HttpResponseMessage uploaded = await Client.PostAsync($"/api/sessions/{session:D}/attachments", form, Token))
            {
                Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);

                attachment = await MemoryErasureRouteDriver.ReadDataAsync(
                    uploaded,
                    ArcanumJsonContext.Default.ApiResponseSessionAttachmentDto);
            }

            string id = attachment.Id.ToString("N");

            DateTime deadline = DateTime.UtcNow + IndexingDeadline;

            while (await ScalarTextOrNullAsync(
                    "SELECT Status FROM session_attachment_index_state WHERE lower(replace(AttachmentId, '-', '')) = $id;",
                    id) != "Indexed")
            {
                Assert.True(DateTime.UtcNow < deadline, "the attachment was never indexed");

                await Task.Delay(TimeSpan.FromMilliseconds(50), Token);
            }

            string generation = await ScalarTextAsync(
                "SELECT PublishedGenerationId FROM session_attachment_index_state WHERE lower(replace(AttachmentId, '-', '')) = $id;",
                id);

            Assert.True(
                await CountAsync("SELECT count(*) FROM session_attachment_chunks WHERE GenerationId = $id;", generation) > 0);

            return new(attachment.Id.ToString("D"), generation);
        }

        /// <summary>
        /// One workspace with one file, registered through the route and indexed by the host's own
        /// background indexer after the re-index route queued it; the victim is the workspace's chunks.
        /// </summary>
        /// <remarks>
        /// No synchronous writer of workspace chunks is reachable, so the row waits, bounded, for the
        /// queued reconciliation to persist them. Nothing re-indexes the workspace afterwards: its file
        /// does not change again, and the periodic reconciliation is an hour away.
        /// </remarks>
        private async Task<string> SeedIndexedWorkspaceAsync()
        {
            string root = Path.Combine(Factory.TempHome, "survival-workspace");

            Directory.CreateDirectory(root);

            await File.WriteAllTextAsync(
                Path.Combine(root, "ferry.md"),
                "# Ferry\n\nThe ferry leaves the east quay at noon on market days.",
                Token);

            WorkspaceInfo workspace;

            using (HttpResponseMessage registered = await Client.PostAsync(
                "/api/workspaces",
                JsonContent.Create(
                    new CreateWorkspaceRequest("survival-workspace", root, WorkspaceType.Custom),
                    ArcanumJsonContext.Default.CreateWorkspaceRequest),
                Token))
            {
                Assert.Equal(HttpStatusCode.Created, registered.StatusCode);

                workspace = await MemoryErasureRouteDriver.ReadDataAsync(
                    registered,
                    ArcanumJsonContext.Default.ApiResponseWorkspaceInfo);
            }

            using (HttpResponseMessage queued = await Client.PostAsync(
                $"/api/workspaces/{Uri.EscapeDataString(workspace.Id)}/files/index",
                null,
                Token))
            {
                Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
            }

            DateTime deadline = DateTime.UtcNow + IndexingDeadline;

            while (await CountAsync("SELECT count(*) FROM workspace_file_chunks WHERE WorkspacePath = $id;", workspace.Path) == 0)
            {
                Assert.True(DateTime.UtcNow < deadline, "the workspace was never indexed");

                await Task.Delay(TimeSpan.FromMilliseconds(50), Token);
            }

            return workspace.Path;
        }

        /// <summary>A memory written from a Session bound to Campaign C, so the store scopes it to C.</summary>
        private async Task<string> SeedCampaignSagaAsync()
        {
            Guid session;

            await using (AsyncServiceScope scope = Factory.Services.CreateAsyncScope())
            {
                Result<Guid> created = await scope.ServiceProvider.GetRequiredService<ISessionTurnBeginStore>().CreateBoundSessionAsync(
                    CanonicalCampaignContext.Create(
                        SessionCampaignBinding.ForCampaign(Campaign),
                        campaignAvailabilityGeneration: 1,
                        pathIdentityPolicyVersion: 1,
                        pathIdentityRevision: null,
                        rootIdentityDigest: null),
                    "Survival campaign session",
                    Token);

                Assert.True(created.IsSuccess, created.IsFailure ? created.Error.Message : null);

                session = created.Value;
            }

            string id = await MemoryErasureRouteDriver.InsertSagaAsync(Factory, Victim, session, Token);

            Assert.Equal(
                $"{(int)SagaMemoryScopeKind.Campaign}|{Campaign.ToString("D").ToUpperInvariant()}",
                await ScalarTextAsync("SELECT ScopeKindCode || '|' || upper(CampaignId) FROM saga_memories WHERE Id = $id;", id));

            return id;
        }

        private async Task<Guid> CreateSessionAsync()
        {
            using HttpResponseMessage created = await Client.PostAsync(
                "/api/sessions",
                JsonContent.Create(
                    new CreateSessionRequest(null, "Survival session"),
                    ArcanumJsonContext.Default.CreateSessionRequest),
                Token);

            Assert.Equal(HttpStatusCode.Created, created.StatusCode);

            SessionDetailDto session = await MemoryErasureRouteDriver.ReadDataAsync(
                created,
                ArcanumJsonContext.Default.ApiResponseSessionDetailDto);

            return session.Id;
        }

        private static async Task<Guid> RegisterCampaignAsync(
            ArcanumWebApplicationFactory factory,
            HttpClient client,
            string name)
        {
            string path = Path.Combine(factory.TempHome, name);

            Directory.CreateDirectory(path);

            using HttpResponseMessage registered = await client.PostAsync(
                "/api/campaigns",
                JsonContent.Create(
                    new RegisterCampaignRequest(name, path, WorkspaceType.Campaign, null),
                    ArcanumJsonContext.Default.RegisterCampaignRequest),
                Token);

            Assert.Equal(HttpStatusCode.Created, registered.StatusCode);

            CampaignDto campaign = await MemoryErasureRouteDriver.ReadDataAsync(
                registered,
                ArcanumJsonContext.Default.ApiResponseCampaignDto);

            return campaign.Id;
        }

        private async Task<T> WithReadOnlyLeaseAsync<T>(Func<SqliteConnection, Task<T>> read)
        {
            Result<IGrimoireOrdinaryConnectionLease> opened = await Factory.Services
                .GetRequiredService<IGrimoireOrdinaryConnectionFactory>()
                .OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly, Token);

            Assert.True(opened.IsSuccess, opened.IsFailure ? opened.Error.Message : null);

            await using IGrimoireOrdinaryConnectionLease lease = opened.Value;

            return await read(lease.Connection);
        }

        /// <summary>One count from the host's Grimoire. Assertion-only.</summary>
        private Task<long> CountAsync(string sql, string id) =>
            WithReadOnlyLeaseAsync(async connection =>
            {
                await using SqliteCommand command = connection.CreateCommand();

                command.CommandText = sql;

                _ = command.Parameters.AddWithValue("$id", id);

                return Convert.ToInt64(await command.ExecuteScalarAsync(Token), CultureInfo.InvariantCulture);
            });

        private async Task<string> ScalarTextAsync(string sql, string id) =>
            await ScalarTextOrNullAsync(sql, id) ?? throw new InvalidOperationException($"No row answered: {sql}");

        private Task<string?> ScalarTextOrNullAsync(string sql, string id) =>
            WithReadOnlyLeaseAsync(async connection =>
            {
                await using SqliteCommand command = connection.CreateCommand();

                command.CommandText = sql;

                _ = command.Parameters.AddWithValue("$id", id);

                object? value = await command.ExecuteScalarAsync(Token);

                return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
            });

        /// <summary>One count from the Covenant tier, through the host's own Covenant connection. Assertion-only.</summary>
        private async Task<long> CovenantCountAsync(string sql, string id)
        {
            await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();

            SqliteConnection connection = await scope.ServiceProvider
                .GetRequiredService<ICovenantConnectionSource>()
                .GetOpenConnectionAsync(Token);

            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = sql;

            _ = command.Parameters.AddWithValue("$id", id);

            return Convert.ToInt64(await command.ExecuteScalarAsync(Token), CultureInfo.InvariantCulture);
        }

        private static float[] Vector()
        {
            float[] vector = new float[MemoryErasureRouteDriver.Dimensions];

            vector[0] = 1f;

            return vector;
        }

        /// <summary>A reset or prune that applied cleanly and removed at least one row.</summary>
        private static void RequireApplied(DataRetentionApplyResult result)
        {
            Assert.Empty(result.Blockers);

            Assert.Empty(result.Conflicts);

            Assert.True(result.RowsDeleted >= 1, $"RowsDeleted was {result.RowsDeleted}.");
        }

        /// <summary>
        /// A memory reset that removed at least one row. A memory reset reports what it removed as derived
        /// records and never as rows, because every row it selects is a store row rather than a Session root.
        /// </summary>
        private static void RequireRemoved(DataRetentionApplyResult result) =>
            Assert.True(
                result.DerivedRecordsDeleted >= 1,
                $"DerivedRecordsDeleted was {result.DerivedRecordsDeleted}.");

        private static async Task RequireNoContentAsync(HttpResponseMessage response)
        {
            using (response)
            {
                Assert.True(
                    response.StatusCode == HttpStatusCode.NoContent,
                    $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Token)}");
            }
        }

        private static async Task RequireOkAsync(HttpResponseMessage response)
        {
            using (response)
            {
                Assert.True(
                    response.StatusCode == HttpStatusCode.OK,
                    $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Token)}");
            }
        }
    }
}
