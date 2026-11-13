using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Core.Operations;
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
