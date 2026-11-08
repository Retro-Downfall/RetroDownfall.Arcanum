using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("WorkspacePathPolicy")]
public sealed partial class GuildEndpointTests
{
    [Fact]
    public void Physical_Guild_protection_starts_after_key_bootstrap_before_ordinary_workers()
    {
        ServiceCollection services = new();

        services.AddArcanumApiServices(new ConfigurationBuilder().Build());

        Type?[] hosted = services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
            .Select(descriptor => descriptor.ImplementationType ?? descriptor.ImplementationFactory?.Method.ReturnType)
            .Select(type => type is { IsGenericType: true }
                && type.GetGenericTypeDefinition() == typeof(InstallationResetRecoveryAwareHostedService<>)
                    ? type.GetGenericArguments()[0] : type).ToArray();

        int keyBootstrap = Array.IndexOf(hosted, typeof(FileEncryptionKeyBootstrapHostedService));

        int readiness = Array.IndexOf(hosted, typeof(GuildLocalHostReadiness));

        Assert.True(keyBootstrap >= 0);

        Assert.Equal(keyBootstrap + 1, readiness);

        Assert.True(Array.IndexOf(hosted, typeof(GrimoireDatabaseHostedService)) < readiness);

        Assert.True(Array.IndexOf(hosted, typeof(LongRunningOperationStartupHostedService)) > readiness);

        Assert.True(Array.IndexOf(hosted, typeof(ApprenticeService)) > readiness);

        Assert.True(Array.IndexOf(hosted, typeof(GuildLocalQuestScheduler)) > readiness);
    }

    [Fact]
    public void Production_composition_registers_the_real_local_graph_and_one_coordinator()
    {
        ServiceCollection services = new();

        services.AddArcanumApiServices(new ConfigurationBuilder().Build());

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IGuildQuestCoordinator));

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(GuildLocalHostPolicy)
            && descriptor.Lifetime == ServiceLifetime.Singleton);

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IGuildLocalInferenceRuntime));

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IGuildLocalExecutionEngine));

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IGuildLocalQuestScheduler));
    }

    [SkippableFact]
    public async Task Native_status_requires_operator_authentication_and_reports_disabled_execution()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient anonymous = factory.CreateClient();

        using HttpResponseMessage denied = await anonymous.GetAsync("/api/guild/status");

        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.GetAsync("/api/guild/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.False(body.RootElement.GetProperty("data").GetProperty("enabled").GetBoolean());

        Assert.False(body.RootElement.GetProperty("data").GetProperty("available").GetBoolean());
    }

    [SkippableFact]
    public async Task Native_root_replay_read_and_cancel_use_the_same_authenticated_store()
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

        GuildIssueRootRequest request = new(Guid.NewGuid(), null, "Native Quest", "Run locally",
            new([], [], [], new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)), [], null));

        string payload = JsonSerializer.Serialize(request, GuildJsonContext.Default.GuildIssueRootRequest);

        using HttpResponseMessage issued = await client.PostAsync("/api/guild/quests", Json(payload));

        Assert.Equal(HttpStatusCode.Created, issued.StatusCode);

        using JsonDocument accepted = JsonDocument.Parse(await issued.Content.ReadAsStringAsync());

        Guid id = accepted.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        long revision = accepted.RootElement.GetProperty("data").GetProperty("revision").GetInt64();

        using HttpResponseMessage replay = await client.PostAsync("/api/guild/quests", Json(payload));

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        using JsonDocument replayed = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());

        Assert.Equal(id, replayed.RootElement.GetProperty("data").GetProperty("id").GetGuid());

        Assert.True(replayed.RootElement.GetProperty("data").GetProperty("receipt").GetProperty("replayed").GetBoolean());

        using HttpResponseMessage read = await client.GetAsync($"/api/guild/quests/{id:D}");

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        GuildCancelQuestRequest cancel = new(Guid.NewGuid(), revision, GuildCancellationReason.OperatorRequested);

        using HttpResponseMessage canceled = await client.PostAsync($"/api/guild/quests/{id:D}/cancel",
            Json(JsonSerializer.Serialize(cancel, GuildJsonContext.Default.GuildCancelQuestRequest)));

        Assert.Equal(HttpStatusCode.Accepted, canceled.StatusCode);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        Assert.Same(scope.ServiceProvider.GetRequiredService<ConclaveArchmage>(),
            scope.ServiceProvider.GetRequiredService<IGuildQuestCoordinator>());

        Assert.Same(scope.ServiceProvider.GetRequiredService<GuildLocalHostPolicy>(),
            scope.ServiceProvider.GetRequiredService<IGuildSignetHostPolicy>());
    }

    [SkippableTheory]
    [InlineData(false, AiProviderKind.OpenAICompatible)]
    [InlineData(true, AiProviderKind.CodexCli)]
    public async Task Native_status_refuses_unqualified_local_configuration(bool apprentices, AiProviderKind provider)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings =>
            {
                settings.Features.Guild = true;

                settings.Features.Apprentices = apprentices;

                settings.Providers[0].Type = provider;

                if (provider != AiProviderKind.OpenAICompatible) settings.Providers[0].Endpoint = string.Empty;

                return settings;
            },
            ServiceOverrides = HoldExecution,
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.GetAsync("/api/guild/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.True(body.RootElement.GetProperty("data").GetProperty("enabled").GetBoolean());

        Assert.False(body.RootElement.GetProperty("data").GetProperty("available").GetBoolean());
    }

    [SkippableFact]
    public async Task Native_operator_executes_through_real_scheduler_engine_HTTP_adapter_and_durable_result()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        using NativeProviderTransport transport = new();

        using CapturedLogProvider logs = new();

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings =>
            {
                settings.Features.Guild = true;

                settings.Features.Apprentices = true;

                return settings;
            },
            ServiceOverrides = services =>
            {
                services.AddHttpClient("OpenAiCompatibleProvider").ConfigurePrimaryHttpMessageHandler(() => transport);

                services.AddLogging(builder => builder.AddProvider(logs).AddFilter<CapturedLogProvider>(static (_, _) => true));
            },
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        GuildIssueRootRequest request = new(Guid.NewGuid(), null, "Executable native Quest", "Produce a native answer",
            new([], [], [], new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)), [], null));

        using HttpResponseMessage issued = await client.PostAsync("/api/guild/quests",
            Json(JsonSerializer.Serialize(request, GuildJsonContext.Default.GuildIssueRootRequest)));

        string issuedBody = await issued.Content.ReadAsStringAsync();

        Assert.True(issued.StatusCode == HttpStatusCode.Created, issuedBody);

        using JsonDocument accepted = JsonDocument.Parse(issuedBody);

        Guid id = accepted.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));

        try
        {
            while (true)
            {
                using HttpResponseMessage result = await client.GetAsync($"/api/guild/quests/{id:D}/result", timeout.Token);

                if (result.StatusCode == HttpStatusCode.OK) break;

                Assert.True(result.StatusCode == HttpStatusCode.NotFound, await result.Content.ReadAsStringAsync(timeout.Token));

                await Task.Delay(25, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            Assert.Fail($"Native result timed out after {transport.Calls} physical HTTP calls.\n"
                + await NativeSnapshotAsync(factory, id) + "\n"
                + string.Join("\n", logs.Entries.Where(entry => entry.Category.Contains("Guild", StringComparison.Ordinal)
                    || entry.Category.Contains("ModelCall", StringComparison.Ordinal)).Select(entry => entry.Message + " " + entry.Exception)));
        }

        using HttpResponseMessage content = await client.GetAsync($"/api/guild/quests/{id:D}/result/content");

        Assert.Equal(HttpStatusCode.OK, content.StatusCode);

        using JsonDocument body = JsonDocument.Parse(await content.Content.ReadAsStringAsync());

        Assert.Equal("native result", body.RootElement.GetProperty("data").GetProperty("body").GetString());

        Assert.Equal(3, transport.Calls);

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        var opened = await scope.ServiceProvider.GetRequiredService<IGrimoireOrdinaryConnectionFactory>()
            .OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using var command = connection.Connection.CreateCommand();

        command.CommandText = "SELECT count(*) FROM BillableOperations";

        Assert.Equal(3L, await command.ExecuteScalarAsync());

        command.CommandText = "SELECT count(*) FROM InferenceRuns";

        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    [SkippableFact]
    public async Task Native_waiting_metadata_exposes_the_exact_question_for_operator_continuation()
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

        GuildIssueRootRequest request = new(Guid.NewGuid(), null, "Question", "Wait for operator input",
            new([], [], [], new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)), [], null));

        using HttpResponseMessage issued = await client.PostAsync("/api/guild/quests",
            Json(JsonSerializer.Serialize(request, GuildJsonContext.Default.GuildIssueRootRequest)));

        Assert.Equal(HttpStatusCode.Created, issued.StatusCode);

        using JsonDocument accepted = JsonDocument.Parse(await issued.Content.ReadAsStringAsync());

        Guid questId = accepted.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        Guid questionId = Guid.NewGuid(), continuationId = Guid.NewGuid();

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            GuildLocalHostPolicy policy = scope.ServiceProvider.GetRequiredService<GuildLocalHostPolicy>();

            SignetAuthority authority = scope.ServiceProvider.GetRequiredService<SignetAuthority>();

            GuildQuestStore store = scope.ServiceProvider.GetRequiredService<GuildQuestStore>();

            var owner = await policy.CreateNativeOperatorAsync(Guid.NewGuid(), default);

            Assert.True(owner.IsSuccess, owner.Error.Message);

            var found = await store.GetAsync(owner.Value, questId, default);

            Assert.True(found.IsSuccess, found.Error.Message);

            var recovery = await policy.CreateRecoveryAsync(owner.Value, questId, found.Value.Quest.OperationId,
                found.Value.Quest.SignetRevision, default);

            Assert.True(recovery.IsSuccess, recovery.Error.Message);

            var rebound = await authority.RebindAsync(recovery.Value, default);

            Assert.True(rebound.IsSuccess, rebound.Error.Message);

            var run = await policy.CreateRunCallerAsync(authority, rebound.Value, Guid.NewGuid(), default);

            Assert.True(run.IsSuccess, run.Error.Message);

            DateTimeOffset now = DateTimeOffset.UtcNow;

            var lease = await scope.ServiceProvider.GetRequiredService<ILongRunningOperationStore>()
                .TryAcquireLeaseAsync(found.Value.Quest.OperationId, "native-question", now, now.AddMinutes(5));

            Assert.True(lease.Acquired);

            GuildQuestDriver driver = new(rebound.Value, "native-question", lease.Operation.Revision, run.Value);

            var local = await store.ReadLocalPortAsync(driver, questId, default);

            Assert.True(local.IsSuccess, local.Error.Message);

            var prepared = await store.PrepareDispatchAsync(driver, questId, local.Value.Binding.Input.Digest,
                found.Value.Quest.RunId, false, default);

            Assert.True(prepared.IsSuccess, prepared.Error.Message);

            var waiting = await store.WaitForInputAsync(driver, questionId, continuationId, default);

            Assert.True(waiting.IsSuccess, waiting.Error.Message);
        }

        using HttpResponseMessage read = await client.GetAsync($"/api/guild/quests/{questId:D}");

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        using JsonDocument current = JsonDocument.Parse(await read.Content.ReadAsStringAsync());

        JsonElement data = current.RootElement.GetProperty("data");

        Assert.Equal("input-required", data.GetProperty("state").GetString());

        Assert.True(data.TryGetProperty("awaitingInput", out JsonElement input), "A native operator must be able to read the exact current question identity.");

        Assert.Equal(questionId, input.GetProperty("questionId").GetGuid());

        Assert.Equal(continuationId, input.GetProperty("continuationId").GetGuid());

        GuildContinueRequest answer = new(Guid.NewGuid(), data.GetProperty("revision").GetInt64(),
            input.GetProperty("continuationId").GetGuid(), "text/plain", "Operator answer");

        string payload = JsonSerializer.Serialize(answer, GuildJsonContext.Default.GuildContinueRequest);

        using HttpResponseMessage continued = await client.PostAsync($"/api/guild/quests/{questId:D}/continue", Json(payload));

        Assert.Equal(HttpStatusCode.Accepted, continued.StatusCode);

        using JsonDocument resumed = JsonDocument.Parse(await continued.Content.ReadAsStringAsync());

        Assert.Equal(JsonValueKind.Null, resumed.RootElement.GetProperty("data").GetProperty("awaitingInput").ValueKind);

        using HttpResponseMessage noQuestion = await client.GetAsync($"/api/guild/quests/{questId:D}/input");

        Assert.Equal(HttpStatusCode.NotFound, noQuestion.StatusCode);

        using HttpResponseMessage replay = await client.PostAsync($"/api/guild/quests/{questId:D}/continue", Json(payload));

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
    }

    private static void HoldExecution(IServiceCollection services)
    {
        services.RemoveAll<IGuildLocalQuestScheduler>();

        services.AddSingleton<IGuildLocalQuestScheduler, HeldScheduler>();

        ServiceDescriptor hosted = Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationFactory?.Method.ReturnType
                == typeof(InstallationResetRecoveryAwareHostedService<GuildLocalQuestScheduler>));

        services.Remove(hosted);
    }

    private static async Task<string> NativeSnapshotAsync(ArcanumWebApplicationFactory factory, Guid quest)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));

        try
        {
            await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

            var opened = await scope.ServiceProvider.GetRequiredService<IGrimoireOrdinaryConnectionFactory>()
                .OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly, timeout.Token);

            if (opened.IsFailure) return opened.Error.Code;

            await using var connection = opened.Value;

            await using var command = connection.Connection.CreateCommand();

            command.CommandTimeout = 2;

            command.CommandText = """
                SELECT q.Revision,q.LastNonterminalStateCode,o.State,o.Revision,o.TerminalErrorCode,o.PublicSummary,
                    (SELECT count(*) FROM BillableOperations),
                    (SELECT group_concat(StateCode || ':' || Revision,',') FROM guild_budget_effects WHERE QuestId=q.Id),
                    (SELECT count(*) FROM guild_budget_evidence),
                    (SELECT count(*) FROM guild_result_receipts WHERE QuestId=q.Id)
                FROM guild_quests q JOIN LongRunningOperations o ON o.Id=q.OperationId WHERE q.Id=$quest
                """;

            command.Parameters.AddWithValue("$quest", quest.ToString("D").ToUpperInvariant());

            await using var reader = await command.ExecuteReaderAsync(timeout.Token);

            return await reader.ReadAsync(timeout.Token)
                ? string.Join("; ", Enumerable.Range(0, reader.FieldCount).Select(index => reader.GetName(index) + "=" + reader.GetValue(index)))
                : "Missing accepted Quest";
        }
        catch (Exception error)
        {
            return "Snapshot unavailable: " + error.GetType().Name;
        }
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private sealed class HeldScheduler : IGuildLocalQuestScheduler
    {
        public bool IsAvailable => true;

        public void Schedule(GuildQuestDto quest) { }
    }

    private sealed class NativeProviderTransport : HttpMessageHandler
    {
        private int _calls;

        internal int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int ordinal = Interlocked.Increment(ref _calls);

            string answer = ordinal == 1 ? "[{\"index\":1,\"description\":\"Produce the answer\"}]" : "native result";

            string text = JsonSerializer.Serialize(answer, ArcanumJsonContext.Default.String);

            string json = $$$"""{"id":"native-{{{ordinal}}}","object":"chat.completion","created":1700000000,"model":"mistral:latest","choices":[{"index":0,"message":{"role":"assistant","content":{{{text}}}},"finish_reason":"stop"}],"usage":{"prompt_tokens":7,"completion_tokens":3,"total_tokens":10}}""";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(json) });
        }
    }
}
