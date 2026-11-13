using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed partial class GuildEndpointTests
{
    [SkippableFact]
    public async Task Operator_spend_effect_list_rejects_invalid_paging_and_hides_unknown_quest()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings =>
            {
                settings.Features.Guild = true;

                return settings;
            },
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        Guid questId = Guid.NewGuid();

        using HttpResponseMessage invalid = await client.GetAsync($"/api/guild/quests/{questId:D}/spend/effects?pageSize=0");

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        using HttpResponseMessage malformed = await client.GetAsync($"/api/guild/quests/{questId:D}/spend/effects?pageSize=abc");

        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);

        using JsonDocument malformedBody = JsonDocument.Parse(await malformed.Content.ReadAsStringAsync());

        Assert.False(malformedBody.RootElement.GetProperty("isSuccess").GetBoolean());

        using HttpResponseMessage missing = await client.GetAsync($"/api/guild/quests/{questId:D}/spend/effects");

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [SkippableFact]
    public async Task Operator_spend_ingress_requires_authentication_and_rejects_invalid_attestations()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings =>
            {
                settings.Features.Guild = true;

                settings.Features.Apprentices = true;

                return settings;
            },
            ServiceOverrides = HoldExecution,
        };

        using HttpClient anonymous = factory.CreateClient();

        using HttpClient client = factory.CreateAuthenticatedClient();

        GuildIssueRootRequest issue = new(Guid.NewGuid(), null, "Spend", "Test spend",
            new([], [], [], new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)), [], null));

        using HttpResponseMessage issued = await client.PostAsync("/api/guild/quests", Json(JsonSerializer.Serialize(issue, GuildJsonContext.Default.GuildIssueRootRequest)));

        Assert.True(issued.StatusCode == HttpStatusCode.Created, await issued.Content.ReadAsStringAsync());

        using JsonDocument issueBody = JsonDocument.Parse(await issued.Content.ReadAsStringAsync());

        Guid quest = issueBody.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        Guid effect = Guid.NewGuid();

        string route = $"/api/guild/quests/{quest:D}/spend/effects/{effect:D}/corrections";

        using HttpResponseMessage denied = await anonymous.GetAsync($"/api/guild/quests/{quest:D}/spend/effects");

        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        string refreshRoute = $"/api/guild/quests/{quest:D}/spend/effects/{effect:D}/refreshes";

        using HttpResponseMessage anonymousRefresh = await anonymous.PostAsync(refreshRoute, Json("{}"));

        Assert.Equal(HttpStatusCode.Unauthorized, anonymousRefresh.StatusCode);

        foreach (string invalidRefresh in new[] { "", "{", "null", "{}", "{\"idempotencyKey\":\"00000000-0000-0000-0000-000000000000\"}" })
        {
            using HttpResponseMessage response = await client.PostAsync(refreshRoute, Json(invalidRefresh));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using HttpResponseMessage missingRefresh = await client.PostAsync(refreshRoute, Json("{\"idempotencyKey\":\"" + Guid.NewGuid().ToString("D") + "\"}"));

        Assert.Equal(HttpStatusCode.NotFound, missingRefresh.StatusCode);

        string key = Guid.NewGuid().ToString("D");

        string digest = new('a', 64);

        string Body(string amounts, string origin = "operatorAttested") =>
            $$"""{"idempotencyKey":"{{key}}","expectedRevision":1,"origin":"{{origin}}","amounts":{{amounts}},"evidenceDigest":"{{digest}}"}""";

        string[] invalid =
        [
            "", "{", "null", Body("{}"), Body("{\"inputTokens\":0,\"outputTokens\":0,\"cachedTokens\":0,\"reasoningTokens\":0,\"usd\":1e-29}"),
            Body("{\"inputTokens\":0,\"outputTokens\":0,\"cachedTokens\":0,\"reasoningTokens\":0,\"usd\":79228162514264337593543950336}"),
            Body("{\"inputTokens\":-1,\"outputTokens\":0,\"cachedTokens\":0,\"reasoningTokens\":0,\"usd\":0}"),
            Body("{\"inputTokens\":0,\"outputTokens\":0,\"cachedTokens\":0,\"reasoningTokens\":0,\"usd\":0}", "providerReported"),
        ];

        foreach (string body in invalid)
        {
            using HttpResponseMessage response = await client.PostAsync(route, Json(body));

            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{body}: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");

            using JsonDocument envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            Assert.False(envelope.RootElement.GetProperty("isSuccess").GetBoolean());
        }

        using HttpResponseMessage complete = await client.PostAsync(route,
            Json(Body("{\"inputTokens\":0,\"outputTokens\":0,\"cachedTokens\":0,\"reasoningTokens\":0,\"usd\":0}")));

        Assert.Equal(HttpStatusCode.NotFound, complete.StatusCode);

        using HttpResponseMessage effects = await client.GetAsync($"/api/guild/quests/{quest:D}/spend/effects");

        Assert.Equal(HttpStatusCode.OK, effects.StatusCode);

        using JsonDocument page = JsonDocument.Parse(await effects.Content.ReadAsStringAsync());

        Assert.Empty(page.RootElement.GetProperty("data").GetProperty("items").EnumerateArray());
    }

    [SkippableFact]
    public async Task Native_operator_refreshes_real_bound_source_once_and_replays_receipt()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        RefreshHttpGate gate = new();

        RefreshHttpCredential credential = new();

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings =>
            {
                settings.Features.Guild = true;

                settings.Features.Apprentices = true;

                settings.DefaultModel = "provider/model";

                settings.Features.A2AServer = true;

                settings.Integrations.A2A.InboundPeers = [new() { PeerId = "refresh-peer", Enabled = true,
                    CredentialEnvironmentVariable = "ARCANUM_REFRESH_TEST_PEER", CampaignId = null, Grant = new() { Workspaces = [], Tools = [], Memory = [],
                        Delegation = [], ExpiresAtUtc = null, Budget = new() { Usd = new() { Mode = GuildBudgetMode.Unlimited },
                            Tokens = new() { Mode = GuildBudgetMode.Unlimited } } } }];

                settings.Providers = [new() { Name = "router", Endpoint = "https://openrouter.ai/api/v1", Models = ["provider/model"],
                    GuildUsageReconciliation = GuildUsageReconciliationSource.OpenRouterGeneration }];

                return settings;
            },
            ServiceOverrides = services =>
            {
                HoldExecution(services);

                services.RemoveAll<IProviderApiKeyResolver>();

                services.AddSingleton<IProviderApiKeyResolver>(credential);

                services.RemoveAll<GuildInboundPeerRegistry>();

                services.AddSingleton(sp => new GuildInboundPeerRegistry(sp.GetRequiredService<IOptionsMonitor<ArcanumSettings>>(),
                    sp.GetRequiredService<IApiKeyDigestCache>(), _ => RefreshHttpCredential.Peer));

                services.RemoveAll<GuildOpenRouterSpendReader>();

                services.AddScoped(_ => new GuildOpenRouterSpendReader(new RefreshHttpDns(), () => new RefreshHttpHandler(gate)));
            },
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        GuildIssueRootRequest request = new(Guid.NewGuid(), null, "Physical spend", "Track physical spend",
            new([], [], [], new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)), [], null));

        using HttpResponseMessage issued = await client.PostAsync("/api/guild/quests", Json(JsonSerializer.Serialize(request, GuildJsonContext.Default.GuildIssueRootRequest)));

        Assert.True(issued.StatusCode == HttpStatusCode.Created, await issued.Content.ReadAsStringAsync());

        using JsonDocument accepted = JsonDocument.Parse(await issued.Content.ReadAsStringAsync());

        Guid questId = accepted.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        Guid effectId = Guid.NewGuid();

        Guid secondEffect = Guid.NewGuid();

        Guid lateEffect = Guid.NewGuid();

        string firstPageJson = "";

        string firstCursor = "";

        long pageSnapshot = 0;

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            GuildLocalHostPolicy policy = scope.ServiceProvider.GetRequiredService<GuildLocalHostPolicy>();

            SignetAuthority authority = scope.ServiceProvider.GetRequiredService<SignetAuthority>();

            GuildQuestStore store = scope.ServiceProvider.GetRequiredService<GuildQuestStore>();

            var owner = await policy.CreateNativeOperatorAsync(Guid.NewGuid(), default);

            Assert.True(owner.IsSuccess, owner.Error.Message);

            var quest = await store.GetAsync(owner.Value, questId, default);

            Assert.True(quest.IsSuccess, quest.Error.Message);

            var recovery = await policy.CreateRecoveryAsync(owner.Value, questId, quest.Value.Quest.OperationId, quest.Value.Quest.SignetRevision, default);

            Assert.True(recovery.IsSuccess, recovery.Error.Message);

            var rebound = await authority.RebindAsync(recovery.Value, default);

            Assert.True(rebound.IsSuccess, rebound.Error.Message);

            var caller = await policy.CreateRunCallerAsync(authority, rebound.Value, Guid.NewGuid(), default);

            Assert.True(caller.IsSuccess, caller.Error.Message);

            DateTimeOffset now = DateTimeOffset.UtcNow;

            var lease = await scope.ServiceProvider.GetRequiredService<ILongRunningOperationStore>()
                .TryAcquireLeaseAsync(quest.Value.Quest.OperationId, "spend-fixture", now, now.AddMinutes(5));

            Assert.True(lease.Acquired);

            GuildQuestDriver driver = new(rebound.Value, "spend-fixture", lease.Operation.Revision, caller.Value);

            var local = await store.ReadLocalPortAsync(driver, questId, default);

            Assert.True(local.IsSuccess, local.Error.Message);

            var dispatch = await store.PrepareDispatchAsync(driver, questId, local.Value.Binding.Input.Digest, quest.Value.Quest.RunId, false, default);

            Assert.True(dispatch.IsSuccess, dispatch.Error.Message);

            GuildLocalInferenceExecution execution = new(store, driver, dispatch.Value);

            BudgetReservationService daily = (BudgetReservationService)scope.ServiceProvider.GetRequiredService<IBudgetReservationService>();

            GuildBudgetEffectRequest effect = new(effectId, questId, dispatch.Value.DispatchId, quest.Value.Quest.RunId,
                GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, null, null);

            BillableOperationRecord bill = new(quest.Value.Quest.RunId, BillableOperationType.Chat, "router", "provider/model", "guild",
                now, now, 100, 20, 5, 40, "{}", .25m, BillableOperationStatus.Completed, "gen-test-123");

            ProviderSettings provider = new() { Name = "router", Endpoint = "https://openrouter.ai/api/v1", Models = ["provider/model"],
                GuildUsageReconciliation = GuildUsageReconciliationSource.OpenRouterGeneration };

            var settled = await store.ExecuteAndSettleProviderResponseAsync(execution, daily, effect,
                _ => Task.FromResult(Result<string>.Success("answer")), _ => Result<BillableOperationRecord>.Success(bill), default,
                spendSource: new GuildSpendSourceCredential(provider, "provider/model", RefreshHttpCredential.Secret), spendPricing: "{}");

            Assert.True(settled.IsSuccess, settled.Error.Message);

            GuildBudgetEffectRequest second = effect with { EffectId = secondEffect, SourceId = Guid.NewGuid() };

            var secondSettled = await store.ExecuteAndSettleProviderResponseAsync(execution, daily, second,
                _ => Task.FromResult(Result<string>.Success("answer")), _ => Result<BillableOperationRecord>.Success(bill), default,
                spendSource: new GuildSpendSourceCredential(provider, "provider/model", RefreshHttpCredential.Secret), spendPricing: "{}");

            Assert.True(secondSettled.IsSuccess, secondSettled.Error.Message);

            using HttpResponseMessage firstPage = await client.GetAsync($"/api/guild/quests/{questId:D}/spend/effects?pageSize=1");

            Assert.Equal(HttpStatusCode.OK, firstPage.StatusCode);

            firstPageJson = await firstPage.Content.ReadAsStringAsync();

            using JsonDocument firstPageBody = JsonDocument.Parse(firstPageJson);

            JsonElement firstData = firstPageBody.RootElement.GetProperty("data");

            Assert.True(System.Text.Encoding.UTF8.GetByteCount(firstData.GetRawText()) <= GuildQuestStore.TreePageMaximumBytes);

            Assert.Equal(JsonValueKind.Null, Assert.Single(firstData.GetProperty("items").EnumerateArray()).GetProperty("providerIneligibility").ValueKind);

            firstCursor = firstData.GetProperty("nextCursor").GetString()!;

            pageSnapshot = firstData.GetProperty("snapshotSequence").GetInt64();

            GuildBudgetEffectRequest late = effect with { EffectId = lateEffect, SourceId = Guid.NewGuid() };

            var lateSettled = await store.ExecuteAndSettleProviderResponseAsync(execution, daily, late,
                _ => Task.FromResult(Result<string>.Success("answer")), _ => Result<BillableOperationRecord>.Success(bill), default,
                spendSource: new GuildSpendSourceCredential(provider, "provider/model", RefreshHttpCredential.Secret), spendPricing: "{}");

            Assert.True(lateSettled.IsSuccess, lateSettled.Error.Message);
        }

        string effectRoute = $"/api/guild/quests/{questId:D}/spend/effects/{effectId:D}";

        string effectsRoute = $"/api/guild/quests/{questId:D}/spend/effects";

        (long operations, long reservations) beforeWorkReads = await RefreshWorkRowsAsync(factory);

        using HttpResponseMessage firstReplay = await client.GetAsync(effectsRoute + "?pageSize=1");

        using JsonDocument firstReplayed = JsonDocument.Parse(await firstReplay.Content.ReadAsStringAsync());

        using JsonDocument firstOriginal = JsonDocument.Parse(firstPageJson);

        Assert.Equal(firstOriginal.RootElement.GetProperty("data").GetProperty("items")[0].GetProperty("effectId").GetGuid(),
            firstReplayed.RootElement.GetProperty("data").GetProperty("items")[0].GetProperty("effectId").GetGuid());

        string nextPageRoute = effectsRoute + "?pageSize=1&cursor=" + Uri.EscapeDataString(firstCursor);

        using HttpResponseMessage nextPage = await client.GetAsync(nextPageRoute);

        Assert.Equal(HttpStatusCode.OK, nextPage.StatusCode);

        string nextJson = await nextPage.Content.ReadAsStringAsync();

        using JsonDocument nextBody = JsonDocument.Parse(nextJson);

        JsonElement nextData = nextBody.RootElement.GetProperty("data");

        Assert.Equal(pageSnapshot, nextData.GetProperty("snapshotSequence").GetInt64());

        Guid nextId = Assert.Single(nextData.GetProperty("items").EnumerateArray()).GetProperty("effectId").GetGuid();

        Assert.NotEqual(lateEffect, nextId);

        Assert.Equal(JsonValueKind.Null, nextData.GetProperty("items")[0].GetProperty("providerIneligibility").ValueKind);

        Assert.Equal(JsonValueKind.Null, nextData.GetProperty("nextCursor").ValueKind);

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(nextData.GetRawText()) <= GuildQuestStore.TreePageMaximumBytes);

        using HttpResponseMessage nextReplay = await client.GetAsync(nextPageRoute);

        using JsonDocument nextReplayed = JsonDocument.Parse(await nextReplay.Content.ReadAsStringAsync());

        Assert.Equal(nextId, Assert.Single(nextReplayed.RootElement.GetProperty("data").GetProperty("items").EnumerateArray()).GetProperty("effectId").GetGuid());

        Assert.Equal(pageSnapshot, nextReplayed.RootElement.GetProperty("data").GetProperty("snapshotSequence").GetInt64());

        Assert.DoesNotContain(RefreshHttpCredential.Secret, nextJson, StringComparison.Ordinal);

        Assert.DoesNotContain("gen-test-123", nextJson, StringComparison.Ordinal);

        int pagePeeks = credential.Reads;

        using HttpResponseMessage sharedPage = await client.GetAsync(effectsRoute + "?pageSize=2");

        Assert.Equal(HttpStatusCode.OK, sharedPage.StatusCode);

        using JsonDocument sharedPageBody = JsonDocument.Parse(await sharedPage.Content.ReadAsStringAsync());

        Assert.Equal(2, sharedPageBody.RootElement.GetProperty("data").GetProperty("items").GetArrayLength());

        Assert.Equal(pagePeeks + 1, credential.Reads);

        (long bills, long events, long receipts) beforeReads = await RefreshRowsAsync(factory);

        int beforePeeks = credential.Reads;

        using HttpResponseMessage eligible = await client.GetAsync(effectRoute);

        Assert.Equal(HttpStatusCode.OK, eligible.StatusCode);

        using JsonDocument eligibleBody = JsonDocument.Parse(await eligible.Content.ReadAsStringAsync());

        Assert.Equal(JsonValueKind.Null, eligibleBody.RootElement.GetProperty("data").GetProperty("providerIneligibility").ValueKind);

        Assert.True(credential.Reads > beforePeeks);

        ProviderSettings eligibilityProvider = factory.Services.GetRequiredService<IOptionsMonitor<ArcanumSettings>>().CurrentValue.Providers[0];

        eligibilityProvider.GuildUsageReconciliation = GuildUsageReconciliationSource.None;

        beforePeeks = credential.Reads;

        using HttpResponseMessage disabled = await client.GetAsync(effectRoute);

        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);

        using JsonDocument disabledBody = JsonDocument.Parse(await disabled.Content.ReadAsStringAsync());

        Assert.Equal("reconciliationDisabled", disabledBody.RootElement.GetProperty("data").GetProperty("providerIneligibility").GetString());

        Assert.Equal(beforePeeks, credential.Reads);

        using HttpResponseMessage disabledPage = await client.GetAsync(effectsRoute + "?pageSize=2");

        Assert.Equal(HttpStatusCode.OK, disabledPage.StatusCode);

        using JsonDocument disabledPageBody = JsonDocument.Parse(await disabledPage.Content.ReadAsStringAsync());

        Assert.All(disabledPageBody.RootElement.GetProperty("data").GetProperty("items").EnumerateArray(), item =>
            Assert.Equal("reconciliationDisabled", item.GetProperty("providerIneligibility").GetString()));

        Assert.Equal(beforePeeks, credential.Reads);

        Assert.Equal(beforeWorkReads, await RefreshWorkRowsAsync(factory));

        Assert.Equal(0, gate.Reads);

        Assert.Equal(beforeReads, await RefreshRowsAsync(factory));

        eligibilityProvider.GuildUsageReconciliation = GuildUsageReconciliationSource.OpenRouterGeneration;

        using HttpResponseMessage otherIssued = await client.PostAsync("/api/guild/quests", Json(JsonSerializer.Serialize(
            request with { IdempotencyKey = Guid.NewGuid() }, GuildJsonContext.Default.GuildIssueRootRequest)));

        Assert.Equal(HttpStatusCode.Created, otherIssued.StatusCode);

        using JsonDocument other = JsonDocument.Parse(await otherIssued.Content.ReadAsStringAsync());

        Guid otherQuest = other.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        string root = $"/api/guild/quests/{questId:D}/spend/effects/{effectId:D}/refreshes";

        string body = "{\"idempotencyKey\":\"" + Guid.NewGuid().ToString("D") + "\"}";

        Task<HttpResponseMessage> first = client.PostAsync(root, Json(body));

        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        using HttpResponseMessage concurrent = await client.PostAsync(root, Json(body));

        Assert.Equal(HttpStatusCode.Accepted, concurrent.StatusCode);

        using JsonDocument pending = JsonDocument.Parse(await concurrent.Content.ReadAsStringAsync());

        Guid operation = pending.RootElement.GetProperty("data").GetProperty("operationId").GetGuid();

        Assert.True(pending.RootElement.GetProperty("data").GetProperty("inFlight").GetBoolean());

        gate.Release.TrySetResult();

        using HttpResponseMessage completed = await first;

        Assert.True(completed.StatusCode == HttpStatusCode.OK, await completed.Content.ReadAsStringAsync());

        using JsonDocument completedBody = JsonDocument.Parse(await completed.Content.ReadAsStringAsync());

        Assert.Equal(operation, completedBody.RootElement.GetProperty("data").GetProperty("operationId").GetGuid());

        Guid receipt = completedBody.RootElement.GetProperty("data").GetProperty("receipt").GetProperty("receiptId").GetGuid();

        (long bills, long events, long receipts) beforeReplay = await RefreshRowsAsync(factory);

        int credentialReads = credential.Reads;

        ProviderSettings configured = factory.Services.GetRequiredService<IOptionsMonitor<ArcanumSettings>>().CurrentValue.Providers[0];

        configured.GuildUsageReconciliation = GuildUsageReconciliationSource.None;

        using HttpResponseMessage replay = await client.PostAsync(root, Json(body));

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        using JsonDocument replayed = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());

        Assert.True(replayed.RootElement.GetProperty("data").GetProperty("replayed").GetBoolean());

        Assert.Equal(receipt, replayed.RootElement.GetProperty("data").GetProperty("receipt").GetProperty("receiptId").GetGuid());

        Assert.Equal(credentialReads, credential.Reads);

        Assert.Equal(beforeReplay, await RefreshRowsAsync(factory));

        Assert.DoesNotContain(RefreshHttpCredential.Secret, replayed.RootElement.GetRawText(), StringComparison.Ordinal);

        Assert.DoesNotContain("gen-test-123", replayed.RootElement.GetRawText(), StringComparison.Ordinal);

        configured.GuildUsageReconciliation = GuildUsageReconciliationSource.OpenRouterGeneration;

        using HttpResponseMessage differentBinding = await client.PostAsync($"/api/guild/quests/{questId:D}/spend/effects/{Guid.NewGuid():D}/refreshes", Json(body));

        Assert.Equal(HttpStatusCode.Conflict, differentBinding.StatusCode);

        using HttpResponseMessage wrongQuest = await client.PostAsync($"/api/guild/quests/{otherQuest:D}/spend/effects/{effectId:D}/refreshes", Json("{\"idempotencyKey\":\"" + Guid.NewGuid().ToString("D") + "\"}"));

        Assert.Equal(HttpStatusCode.NotFound, wrongQuest.StatusCode);

        using HttpClient invalidKey = factory.CreateClient();

        Assert.NotNull(factory.Services.GetRequiredService<GuildInboundPeerRegistry>().Resolve(RefreshHttpCredential.Peer));

        invalidKey.DefaultRequestHeaders.Add(RetroDownfall.Arcanum.Api.Security.ArcanumApiHeaders.ApiKey, RefreshHttpCredential.Peer);

        using HttpResponseMessage deniedKey = await invalidKey.PostAsync(root, Json(body));

        Assert.Equal(HttpStatusCode.Unauthorized, deniedKey.StatusCode);

        using HttpResponseMessage peerGet = await invalidKey.GetAsync(effectRoute);

        Assert.Equal(HttpStatusCode.Unauthorized, peerGet.StatusCode);

        using HttpClient anonymousGetClient = factory.CreateClient();

        using HttpResponseMessage anonymousGet = await anonymousGetClient.GetAsync(effectsRoute);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymousGet.StatusCode);

        string distinctBody = "{\"idempotencyKey\":\"" + Guid.NewGuid().ToString("D") + "\"}";

        using HttpResponseMessage sameFacts = await client.PostAsync(root, Json(distinctBody));

        Assert.Equal(HttpStatusCode.OK, sameFacts.StatusCode);

        using JsonDocument converged = JsonDocument.Parse(await sameFacts.Content.ReadAsStringAsync());

        Assert.True(converged.RootElement.GetProperty("data").GetProperty("replayed").GetBoolean());

        Assert.Equal(receipt, converged.RootElement.GetProperty("data").GetProperty("receipt").GetProperty("receiptId").GetGuid());

        Assert.Equal(beforeReplay, await RefreshRowsAsync(factory));

        Assert.Equal(2, gate.Reads);

        gate.Fail = true;

        string failedBody = "{\"idempotencyKey\":\"" + Guid.NewGuid().ToString("D") + "\"}";

        using HttpResponseMessage parked = await client.PostAsync(root, Json(failedBody));

        Assert.Equal(HttpStatusCode.Accepted, parked.StatusCode);

        using JsonDocument attention = JsonDocument.Parse(await parked.Content.ReadAsStringAsync());

        Guid parkedOperation = attention.RootElement.GetProperty("data").GetProperty("operationId").GetGuid();

        Assert.True(attention.RootElement.GetProperty("data").GetProperty("requiresAttention").GetBoolean());

        Assert.False(string.IsNullOrWhiteSpace(attention.RootElement.GetProperty("data").GetProperty("errorCode").GetString()));

        using HttpResponseMessage parkedReplay = await client.PostAsync(root, Json(failedBody));

        Assert.Equal(HttpStatusCode.Accepted, parkedReplay.StatusCode);

        using JsonDocument repeatedPark = JsonDocument.Parse(await parkedReplay.Content.ReadAsStringAsync());

        Assert.Equal(parkedOperation, repeatedPark.RootElement.GetProperty("data").GetProperty("operationId").GetGuid());

        Assert.Equal(3, gate.Reads);

        string otherParkedBody = "{\"idempotencyKey\":\"" + Guid.NewGuid().ToString("D") + "\"}";

        using HttpResponseMessage otherParked = await client.PostAsync(root, Json(otherParkedBody));

        Assert.Equal(HttpStatusCode.Accepted, otherParked.StatusCode);

        using JsonDocument otherParkedState = JsonDocument.Parse(await otherParked.Content.ReadAsStringAsync());

        Guid otherOperation = otherParkedState.RootElement.GetProperty("data").GetProperty("operationId").GetGuid();

        Assert.NotEqual(parkedOperation, otherOperation);

        using HttpClient anonymous = factory.CreateClient();

        foreach (string action in new[] { "retry", "cancel" })
        {
            using HttpResponseMessage anonymousDenied = await anonymous.PostAsync($"/api/operations/{parkedOperation:D}/{action}", null);

            Assert.Equal(HttpStatusCode.Unauthorized, anonymousDenied.StatusCode);

            using HttpResponseMessage peerDenied = await invalidKey.PostAsync($"/api/operations/{parkedOperation:D}/{action}", null);

            Assert.Equal(HttpStatusCode.Unauthorized, peerDenied.StatusCode);
        }

        configured.GuildUsageReconciliation = GuildUsageReconciliationSource.None;

        using HttpResponseMessage refused = await client.PostAsync($"/api/operations/{parkedOperation:D}/retry", null);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        Assert.Equal(4, gate.Reads);

        configured.GuildUsageReconciliation = GuildUsageReconciliationSource.OpenRouterGeneration;

        gate.Fail = false;

        using HttpResponseMessage retried = await client.PostAsync($"/api/operations/{parkedOperation:D}/retry", null);

        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);

        Assert.Equal(5, gate.Reads);

        using HttpResponseMessage untouched = await client.PostAsync(root, Json(otherParkedBody));

        Assert.Equal(HttpStatusCode.Accepted, untouched.StatusCode);

        using JsonDocument untouchedState = JsonDocument.Parse(await untouched.Content.ReadAsStringAsync());

        Assert.Equal(otherOperation, untouchedState.RootElement.GetProperty("data").GetProperty("operationId").GetGuid());

        Assert.True(untouchedState.RootElement.GetProperty("data").GetProperty("requiresAttention").GetBoolean());

        Assert.Equal(5, gate.Reads);

        using HttpResponseMessage retryReplay = await client.PostAsync(root, Json(failedBody));

        Assert.Equal(HttpStatusCode.OK, retryReplay.StatusCode);

        using JsonDocument retryState = JsonDocument.Parse(await retryReplay.Content.ReadAsStringAsync());

        Assert.Equal(parkedOperation, retryState.RootElement.GetProperty("data").GetProperty("operationId").GetGuid());

        Assert.Equal(JsonValueKind.Object, retryState.RootElement.GetProperty("data").GetProperty("receipt").ValueKind);

        gate.Fail = false;

        gate.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        gate.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        gate.Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        string cancelledBody = "{\"idempotencyKey\":\"" + Guid.NewGuid().ToString("D") + "\"}";

        (long bills, long events, long receipts) beforeCancel = await RefreshRowsAsync(factory);

        using CancellationTokenSource disconnect = new();

        Task<HttpResponseMessage> abandoned = client.PostAsync(root, Json(cancelledBody), disconnect.Token);

        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        using HttpResponseMessage observed = await client.PostAsync(root, Json(cancelledBody));

        Assert.Equal(HttpStatusCode.Accepted, observed.StatusCode);

        using JsonDocument inFlight = JsonDocument.Parse(await observed.Content.ReadAsStringAsync());

        Guid cancelledOperation = inFlight.RootElement.GetProperty("data").GetProperty("operationId").GetGuid();

        await disconnect.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await abandoned);

        await gate.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(30));

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));

        while (true)
        {
            using HttpResponseMessage retry = await client.PostAsync(root, Json(cancelledBody), deadline.Token);

            Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);

            using JsonDocument state = JsonDocument.Parse(await retry.Content.ReadAsStringAsync(deadline.Token));

            Assert.Equal(cancelledOperation, state.RootElement.GetProperty("data").GetProperty("operationId").GetGuid());

            if (state.RootElement.GetProperty("data").GetProperty("requiresAttention").GetBoolean()) break;

            await Task.Delay(20, deadline.Token);
        }

        Assert.Equal(6, gate.Reads);

        Assert.Equal(beforeCancel, await RefreshRowsAsync(factory));

        gate.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        gate.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        gate.Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        string signalBody = "{\"idempotencyKey\":\"" + Guid.NewGuid().ToString("D") + "\"}";

        Task<HttpResponseMessage> waiter = client.PostAsync(root, Json(signalBody));

        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        using HttpResponseMessage signalObserved = await client.PostAsync(root, Json(signalBody));

        using JsonDocument signalPending = JsonDocument.Parse(await signalObserved.Content.ReadAsStringAsync());

        Guid signalOperation = signalPending.RootElement.GetProperty("data").GetProperty("operationId").GetGuid();

        using HttpResponseMessage acknowledged = await client.PostAsync($"/api/operations/{signalOperation:D}/cancel", null);

        Assert.Equal(HttpStatusCode.OK, acknowledged.StatusCode);

        await gate.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(30));

        using HttpResponseMessage cancelled = await waiter.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);

        using JsonDocument cancelledResult = JsonDocument.Parse(await cancelled.Content.ReadAsStringAsync());

        Assert.True(cancelledResult.RootElement.GetProperty("data").GetProperty("cancelled").GetBoolean());

        Assert.Equal("Guild.RefreshCancelled", cancelledResult.RootElement.GetProperty("data").GetProperty("errorCode").GetString());

        Assert.Equal(JsonValueKind.Null, cancelledResult.RootElement.GetProperty("data").GetProperty("receipt").ValueKind);

        using HttpResponseMessage cancelledReplay = await client.PostAsync(root, Json(signalBody));

        Assert.Equal(HttpStatusCode.OK, cancelledReplay.StatusCode);

        using JsonDocument cancelledAgain = JsonDocument.Parse(await cancelledReplay.Content.ReadAsStringAsync());

        Assert.True(cancelledAgain.RootElement.GetProperty("data").GetProperty("cancelled").GetBoolean());

        Assert.Equal(signalOperation, cancelledAgain.RootElement.GetProperty("data").GetProperty("operationId").GetGuid());

        Assert.Equal(7, gate.Reads);

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            var opened = await scope.ServiceProvider.GetRequiredService<IGrimoireOrdinaryConnectionFactory>()
                .OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

            Assert.True(opened.IsSuccess, opened.Error.Message);

            await using IGrimoireOrdinaryConnectionLease lease = opened.Value;

            await using var command = lease.Connection.CreateCommand();

            // Fault injection in this scratch database: production foreign keys forbid this deletion.
            command.CommandText = "PRAGMA foreign_keys=OFF";

            await command.ExecuteNonQueryAsync();

            command.CommandText = "DELETE FROM guild_spend_sources WHERE EffectId=$effect";

            command.Parameters.AddWithValue("$effect", effectId.ToString("D").ToUpperInvariant());

            try
            {
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            finally
            {
                command.CommandText = "PRAGMA foreign_keys=ON";

                await command.ExecuteNonQueryAsync();
            }
        }

        using HttpResponseMessage missingSourceRead = await client.GetAsync(effectRoute);

        Assert.Equal(HttpStatusCode.OK, missingSourceRead.StatusCode);

        using JsonDocument missingSourceBody = JsonDocument.Parse(await missingSourceRead.Content.ReadAsStringAsync());

        Assert.Equal("initialEvidenceUnavailable", missingSourceBody.RootElement.GetProperty("data").GetProperty("providerIneligibility").GetString());

        using HttpResponseMessage missingSource = await client.PostAsync($"/api/operations/{otherOperation:D}/retry", null);

        Assert.Equal(HttpStatusCode.Conflict, missingSource.StatusCode);

        using HttpResponseMessage completedAgain = await client.PostAsync(root, Json(failedBody));

        Assert.Equal(HttpStatusCode.OK, completedAgain.StatusCode);

        Assert.Equal(7, gate.Reads);
    }

    private static async Task<(long Operations, long Reservations)> RefreshWorkRowsAsync(ArcanumWebApplicationFactory factory)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        var opened = await scope.ServiceProvider.GetRequiredService<IGrimoireOrdinaryConnectionFactory>()
            .OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using IGrimoireOrdinaryConnectionLease lease = opened.Value;

        await using var command = lease.Connection.CreateCommand();

        command.CommandText = "SELECT (SELECT COUNT(*) FROM LongRunningOperations),(SELECT COUNT(*) FROM BudgetReservations)";

        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());

        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static async Task<(long Bills, long Events, long Receipts)> RefreshRowsAsync(ArcanumWebApplicationFactory factory)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        var opened = await scope.ServiceProvider.GetRequiredService<IGrimoireOrdinaryConnectionFactory>()
            .OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using IGrimoireOrdinaryConnectionLease lease = opened.Value;

        await using var command = lease.Connection.CreateCommand();

        command.CommandText = "SELECT (SELECT COUNT(*) FROM BillableOperations),(SELECT COUNT(*) FROM guild_events),(SELECT COUNT(*) FROM guild_spend_receipts)";

        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());

        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private sealed class RefreshHttpGate
    {
        internal TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int Reads;

        internal bool Fail;
    }

    private sealed class RefreshHttpDns : IDnsResolver
    {
        public Task<IPAddress[]> GetHostAddressesAsync(string host, CancellationToken cancellationToken = default) =>
            Task.FromResult<IPAddress[]>([IPAddress.Parse("1.1.1.1")]);
    }

    private sealed class RefreshHttpCredential : IProviderApiKeyResolver
    {
        internal const string Secret = "private-refresh-test-key";

        internal static readonly string Peer = Convert.ToBase64String(Enumerable.Repeat((byte)72, 32).ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private readonly ProviderCredentialStamp _stamp = new(Guid.NewGuid(), Guid.NewGuid(), 1);

        internal int Reads;

        public Task<string?> ResolveAsync(ProviderSettings provider, CancellationToken cancellationToken = default) => Task.FromResult<string?>(Secret);

        public Task<ResolvedProviderCredential> PeekWithIdentityAsync(ProviderSettings provider, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Reads);

            return Task.FromResult(new ResolvedProviderCredential(Secret, _stamp));
        }

        public ProviderCredentialCurrentness GetObservedLocalCurrentness(ProviderSettings provider, ProviderCredentialStamp stamp) =>
            stamp == _stamp ? ProviderCredentialCurrentness.Current : ProviderCredentialCurrentness.ConfirmedStale;

        public Task<ProviderCredentialCurrentness> GetCurrentnessAsync(ProviderSettings provider, ProviderCredentialStamp stamp, CancellationToken cancellationToken = default) =>
            Task.FromResult(GetObservedLocalCurrentness(provider, stamp));
    }

    private sealed class RefreshHttpHandler(RefreshHttpGate gate) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref gate.Reads);

            gate.Entered.TrySetResult();

            try
            {
                await gate.Release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                gate.Cancelled.TrySetResult();

                throw;
            }

            if (gate.Fail) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
                {"data":{"api_type":"completions","id":"gen-test-123","is_byok":false,"model":"provider/model",
                "native_tokens_cached":40,"native_tokens_completion":20,"native_tokens_completion_images":0,
                "native_tokens_prompt":100,"native_tokens_reasoning":5,"num_input_audio_prompt":0,
                "num_media_completion":0,"num_media_prompt":0,"total_cost":0.30}}
                """) };
        }
    }

    [SkippableFact]
    public async Task Native_operator_corrects_real_physical_bill_and_replays_original_receipt()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings =>
            {
                settings.Features.Guild = true;

                settings.Features.Apprentices = true;

                return settings;
            },
            ServiceOverrides = HoldExecution,
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        GuildIssueRootRequest request = new(Guid.NewGuid(), null, "Physical spend", "Track physical spend",
            new([], [], [], new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)), [], null));

        using HttpResponseMessage issued = await client.PostAsync("/api/guild/quests", Json(JsonSerializer.Serialize(request, GuildJsonContext.Default.GuildIssueRootRequest)));

        Assert.True(issued.StatusCode == HttpStatusCode.Created, await issued.Content.ReadAsStringAsync());

        using JsonDocument accepted = JsonDocument.Parse(await issued.Content.ReadAsStringAsync());

        Guid questId = accepted.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        Guid effectId = Guid.NewGuid();

        long revision;

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            GuildLocalHostPolicy policy = scope.ServiceProvider.GetRequiredService<GuildLocalHostPolicy>();

            SignetAuthority authority = scope.ServiceProvider.GetRequiredService<SignetAuthority>();

            GuildQuestStore store = scope.ServiceProvider.GetRequiredService<GuildQuestStore>();

            var owner = await policy.CreateNativeOperatorAsync(Guid.NewGuid(), default);

            Assert.True(owner.IsSuccess, owner.Error.Message);

            var quest = await store.GetAsync(owner.Value, questId, default);

            Assert.True(quest.IsSuccess, quest.Error.Message);

            var recovery = await policy.CreateRecoveryAsync(owner.Value, questId, quest.Value.Quest.OperationId, quest.Value.Quest.SignetRevision, default);

            Assert.True(recovery.IsSuccess, recovery.Error.Message);

            var rebound = await authority.RebindAsync(recovery.Value, default);

            Assert.True(rebound.IsSuccess, rebound.Error.Message);

            var caller = await policy.CreateRunCallerAsync(authority, rebound.Value, Guid.NewGuid(), default);

            Assert.True(caller.IsSuccess, caller.Error.Message);

            DateTimeOffset now = DateTimeOffset.UtcNow;

            var lease = await scope.ServiceProvider.GetRequiredService<ILongRunningOperationStore>()
                .TryAcquireLeaseAsync(quest.Value.Quest.OperationId, "spend-fixture", now, now.AddMinutes(5));

            Assert.True(lease.Acquired);

            GuildQuestDriver driver = new(rebound.Value, "spend-fixture", lease.Operation.Revision, caller.Value);

            var local = await store.ReadLocalPortAsync(driver, questId, default);

            Assert.True(local.IsSuccess, local.Error.Message);

            var dispatch = await store.PrepareDispatchAsync(driver, questId, local.Value.Binding.Input.Digest, quest.Value.Quest.RunId, false, default);

            Assert.True(dispatch.IsSuccess, dispatch.Error.Message);

            GuildLocalInferenceExecution execution = new(store, driver, dispatch.Value);

            BudgetReservationService daily = (BudgetReservationService)scope.ServiceProvider.GetRequiredService<IBudgetReservationService>();

            GuildBudgetEffectRequest effect = new(effectId, questId, dispatch.Value.DispatchId, quest.Value.Quest.RunId,
                GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, null, null);

            var sent = await store.ExecuteProviderEffectAsync(execution, daily, effect, _ => Task.FromResult(Result<string>.Success("answer")), default);

            Assert.True(sent.IsSuccess, sent.Error.Message);

            BillableOperationRecord bill = new(quest.Value.Quest.RunId, BillableOperationType.Chat, "provider", "model", "guild",
                now, now, 100, 20, 5, 40, "{}", .25m, BillableOperationStatus.Completed, "request-id");

            var settled = await store.SettleProviderEffectAsync(execution, daily, effect, bill, default);

            Assert.True(settled.IsSuccess, settled.Error.Message);

            revision = settled.Value.Revision;
        }

        string root = $"/api/guild/quests/{questId:D}/spend/effects/{effectId:D}";

        GuildSpendCorrectionRequest correction = new(Guid.NewGuid(), revision, GuildSpendOrigin.OperatorAttested,
            new(110, 25, 45, 6, .30m), new string('a', 64));

        string body = JsonSerializer.Serialize(correction, GuildJsonContext.Default.GuildSpendCorrectionRequest);

        using HttpResponseMessage applied = await client.PostAsync(root + "/corrections", Json(body));

        Assert.True(applied.StatusCode == HttpStatusCode.Created, await applied.Content.ReadAsStringAsync());

        using JsonDocument original = JsonDocument.Parse(await applied.Content.ReadAsStringAsync());

        JsonElement receipt = original.RootElement.GetProperty("data").GetProperty("receipt");

        Guid receiptId = receipt.GetProperty("receiptId").GetGuid();

        Assert.Equal(.30m, receipt.GetProperty("facts").GetProperty("knownUsd").GetDecimal());

        GuildSpendCorrectionRequest later = correction with { IdempotencyKey = Guid.NewGuid(), ExpectedRevision = receipt.GetProperty("evidenceRevision").GetInt64(), Amounts = new(120, 30, 50, 7, .40m) };

        using HttpResponseMessage appliedLater = await client.PostAsync(root + "/corrections", Json(JsonSerializer.Serialize(later, GuildJsonContext.Default.GuildSpendCorrectionRequest)));

        Assert.True(appliedLater.StatusCode == HttpStatusCode.Created, await appliedLater.Content.ReadAsStringAsync());

        using HttpResponseMessage replay = await client.PostAsync(root + "/corrections", Json(body));

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        using JsonDocument repeated = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());

        Assert.True(repeated.RootElement.GetProperty("data").GetProperty("replayed").GetBoolean());

        Assert.Equal(receiptId, repeated.RootElement.GetProperty("data").GetProperty("receipt").GetProperty("receiptId").GetGuid());

        using HttpResponseMessage changedKey = await client.PostAsync(root + "/corrections", Json(JsonSerializer.Serialize(
            correction with { Amounts = new(130, 30, 50, 7, .50m) }, GuildJsonContext.Default.GuildSpendCorrectionRequest)));

        Assert.Equal(HttpStatusCode.Conflict, changedKey.StatusCode);

        using HttpResponseMessage stale = await client.PostAsync(root + "/corrections", Json(JsonSerializer.Serialize(
            correction with { IdempotencyKey = Guid.NewGuid() }, GuildJsonContext.Default.GuildSpendCorrectionRequest)));

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        using HttpResponseMessage effectRead = await client.GetAsync(root);

        Assert.Equal(HttpStatusCode.OK, effectRead.StatusCode);

        using JsonDocument current = JsonDocument.Parse(await effectRead.Content.ReadAsStringAsync());

        Assert.Equal(.40m, current.RootElement.GetProperty("data").GetProperty("facts").GetProperty("knownUsd").GetDecimal());

        using HttpResponseMessage historyPage = await client.GetAsync(root + "/receipts?pageSize=1");

        Assert.Equal(HttpStatusCode.OK, historyPage.StatusCode);

        using JsonDocument historyBody = JsonDocument.Parse(await historyPage.Content.ReadAsStringAsync());

        Assert.Single(historyBody.RootElement.GetProperty("data").GetProperty("items").EnumerateArray());

        string cursor = historyBody.RootElement.GetProperty("data").GetProperty("nextCursor").GetString()!;

        GuildIssueRootRequest otherIssue = request with { IdempotencyKey = Guid.NewGuid() };

        using HttpResponseMessage otherCreated = await client.PostAsync("/api/guild/quests", Json(JsonSerializer.Serialize(otherIssue, GuildJsonContext.Default.GuildIssueRootRequest)));

        Assert.True(otherCreated.StatusCode == HttpStatusCode.Created, await otherCreated.Content.ReadAsStringAsync());

        using JsonDocument otherBody = JsonDocument.Parse(await otherCreated.Content.ReadAsStringAsync());

        Guid otherQuest = otherBody.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        using HttpResponseMessage wrongScope = await client.GetAsync($"/api/guild/quests/{otherQuest:D}/spend/effects/{effectId:D}/receipts?cursor={Uri.EscapeDataString(cursor)}");

        Assert.Equal(HttpStatusCode.NotFound, wrongScope.StatusCode);

        using HttpResponseMessage crossQuest = await client.GetAsync($"/api/guild/quests/{otherQuest:D}/spend/effects/{effectId:D}");

        Assert.Equal(HttpStatusCode.NotFound, crossQuest.StatusCode);

        using HttpClient anonymous = factory.CreateClient();

        using HttpResponseMessage anonymousCorrection = await anonymous.PostAsync(root + "/corrections", Json(body));

        Assert.Equal(HttpStatusCode.Unauthorized, anonymousCorrection.StatusCode);

        using HttpResponseMessage read = await client.GetAsync(root + $"/receipts/{receiptId:D}");

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        using JsonDocument saved = JsonDocument.Parse(await read.Content.ReadAsStringAsync());

        Assert.Equal(.30m, saved.RootElement.GetProperty("data").GetProperty("facts").GetProperty("knownUsd").GetDecimal());
    }

    [SkippableFact]
    public async Task Native_operator_reads_real_remote_spend_but_cannot_attest_it()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        using NativeRosterRemoteHandler handler = new();

        RetroDownfall.Arcanum.Tests.Support.FakeDnsResolver dns = new();

        dns.Add("native-roster.example", IPAddress.Parse("93.184.216.34"));

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings =>
            {
                settings.Features.Guild = true;

                settings.Features.Apprentices = true;

                settings.Features.Conclave = true;

                settings.Features.A2AClient = true;

                settings.Integrations.A2A.AllowedRemoteAgents = [NativeRosterRemote];

                return settings;
            },
            ServiceOverrides = services =>
            {
                HoldExecution(services);

                services.RemoveAll<IHttpClientFactory>();

                services.AddSingleton<IHttpClientFactory>(new NativeRosterHttpFactory(handler));

                services.RemoveAll<RetroDownfall.Arcanum.Core.Security.IDnsResolver>();

                services.AddSingleton<RetroDownfall.Arcanum.Core.Security.IDnsResolver>(dns);
            },
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage discovered = await client.PostAsync("/api/guild/agents/remote/resolve", Json("{\"agent\":\"" + NativeRosterRemote + "\"}"));

        Assert.True(discovered.StatusCode == HttpStatusCode.OK, await discovered.Content.ReadAsStringAsync());

        using JsonDocument card = JsonDocument.Parse(await discovered.Content.ReadAsStringAsync());

        Guid agentId = card.RootElement.GetProperty("data").GetProperty("agentId").GetGuid();

        GuildIssueRootRequest issue = new(Guid.NewGuid(), agentId, "Remote spend", "Track remote spend",
            new([], [], [], new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)), [GuildDelegationKind.Remote], null),
            RemoteOptions: new(GuildRemoteDispatchMode.Continuable));

        using HttpResponseMessage issued = await client.PostAsync("/api/guild/quests", Json(JsonSerializer.Serialize(issue, GuildJsonContext.Default.GuildIssueRootRequest)));

        Assert.True(issued.StatusCode == HttpStatusCode.Created, await issued.Content.ReadAsStringAsync());

        using JsonDocument accepted = JsonDocument.Parse(await issued.Content.ReadAsStringAsync());

        Guid questId = accepted.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            GuildLocalHostPolicy policy = scope.ServiceProvider.GetRequiredService<GuildLocalHostPolicy>();

            SignetAuthority authority = scope.ServiceProvider.GetRequiredService<SignetAuthority>();

            GuildQuestStore store = scope.ServiceProvider.GetRequiredService<GuildQuestStore>();

            var owner = await policy.CreateNativeOperatorAsync(Guid.NewGuid(), default);

            Assert.True(owner.IsSuccess, owner.Error.Message);

            var quest = await store.GetAsync(owner.Value, questId, default);

            Assert.True(quest.IsSuccess, quest.Error.Message);

            var recovery = await policy.CreateRecoveryAsync(owner.Value, questId, quest.Value.Quest.OperationId, quest.Value.Quest.SignetRevision, default);

            Assert.True(recovery.IsSuccess, recovery.Error.Message);

            var rebound = await authority.RebindAsync(recovery.Value, default);

            Assert.True(rebound.IsSuccess, rebound.Error.Message);

            var caller = await policy.CreateRunCallerAsync(authority, rebound.Value, Guid.NewGuid(), default);

            Assert.True(caller.IsSuccess, caller.Error.Message);

            DateTimeOffset now = DateTimeOffset.UtcNow;

            var lease = await scope.ServiceProvider.GetRequiredService<ILongRunningOperationStore>()
                .TryAcquireLeaseAsync(quest.Value.Quest.OperationId, "remote-spend-fixture", now, now.AddMinutes(5));

            Assert.True(lease.Acquired);

            GuildQuestDriver driver = new(rebound.Value, "remote-spend-fixture", lease.Operation.Revision, caller.Value);

            var dispatch = await store.PrepareDispatchAsync(driver, questId, quest.Value.Quest.GoalRef.Digest, quest.Value.Quest.RunId, false, default);

            Assert.True(dispatch.IsSuccess, dispatch.Error.Message);

            var sent = await store.SendRemoteEffectAsync(driver, questId, _ => Task.FromResult(new global::A2A.SendMessageResponse()), default);

            Assert.True(sent.IsSuccess, sent.Error.Message);

            var state = await store.ReadRemotePortAsync(driver, questId, default);

            Assert.True(state.IsSuccess, state.Error.Message);

            var confirmed = await store.RecordDispatchEvidenceAsync(driver,
                new(questId, state.Value.Dispatch!.Revision, GuildDispatchEvidence.Confirmed, RemoteTaskId: "peer-task", RemoteContextId: "peer-context"), default);

            Assert.True(confirmed.IsSuccess, confirmed.Error.Message);

            A2AGuildTaskObservation observation = new("peer-task", "peer-context", "input-required", "Continue?",
                new A2ARemoteCost(true, 20, 1m), now);

            var observed = await store.WaitForRemoteInputAsync(driver, questId, observation, default);

            Assert.True(observed.IsSuccess, observed.Error.Message);
        }

        string root = $"/api/guild/quests/{questId:D}/spend/effects/{questId:D}";

        using HttpResponseMessage read = await client.GetAsync(root);

        Assert.True(read.StatusCode == HttpStatusCode.OK, await read.Content.ReadAsStringAsync());

        using JsonDocument effect = JsonDocument.Parse(await read.Content.ReadAsStringAsync());

        JsonElement data = effect.RootElement.GetProperty("data");

        Assert.Equal("remote", data.GetProperty("kind").GetString());

        Assert.Equal("remoteEffect", data.GetProperty("operatorIneligibility").GetString());

        Assert.Equal("remoteEffect", data.GetProperty("providerIneligibility").GetString());

        Assert.Equal(1m, data.GetProperty("facts").GetProperty("knownUsd").GetDecimal());

        Assert.Equal(20, data.GetProperty("facts").GetProperty("knownTokens").GetInt64());

        Assert.Equal(JsonValueKind.Null, data.GetProperty("facts").GetProperty("localTokens").ValueKind);

        using HttpResponseMessage beforeReceipts = await client.GetAsync(root + "/receipts");

        Assert.Equal(HttpStatusCode.OK, beforeReceipts.StatusCode);

        using JsonDocument beforeHistory = JsonDocument.Parse(await beforeReceipts.Content.ReadAsStringAsync());

        string historyJson = beforeHistory.RootElement.GetProperty("data").GetRawText();

        string effectJson = data.GetRawText();

        GuildSpendCorrectionRequest correction = new(Guid.NewGuid(), data.GetProperty("evidenceRevision").GetInt64(), GuildSpendOrigin.OperatorAttested,
            new(20, 0, 0, 0, 1m), new string('a', 64));

        using HttpResponseMessage deniedRefresh = await client.PostAsync(root + "/refreshes", Json("{\"idempotencyKey\":\"" + Guid.NewGuid().ToString("D") + "\"}"));

        Assert.Equal(HttpStatusCode.NotFound, deniedRefresh.StatusCode);

        using HttpResponseMessage denied = await client.PostAsync(root + "/corrections", Json(JsonSerializer.Serialize(correction, GuildJsonContext.Default.GuildSpendCorrectionRequest)));

        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);

        using HttpResponseMessage afterRead = await client.GetAsync(root);

        Assert.Equal(HttpStatusCode.OK, afterRead.StatusCode);

        using JsonDocument afterEffect = JsonDocument.Parse(await afterRead.Content.ReadAsStringAsync());

        Assert.Equal(effectJson, afterEffect.RootElement.GetProperty("data").GetRawText());

        using HttpResponseMessage afterReceipts = await client.GetAsync(root + "/receipts");

        Assert.Equal(HttpStatusCode.OK, afterReceipts.StatusCode);

        using JsonDocument afterHistory = JsonDocument.Parse(await afterReceipts.Content.ReadAsStringAsync());

        Assert.Equal(historyJson, afterHistory.RootElement.GetProperty("data").GetRawText());
    }
}
