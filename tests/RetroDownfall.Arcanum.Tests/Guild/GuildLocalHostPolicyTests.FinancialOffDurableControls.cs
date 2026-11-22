using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Intelligence;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Durable_local_port_retains_post_response_fault_after_reconstruction()
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using Fixture f = await Fixture.CreateAsync(productionOptions: true);

        f.Settings.CurrentValue.Features.Conclave = true;

        f.Settings.CurrentValue.Features.Apprentices = true;

        f.Settings.CurrentValue.DefaultModel = "gpt-5.4-mini-2026-03-17";

        f.Settings.CurrentValue.Providers = [new() { Name = "accepted-provider", Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://api.openai.com/v1", ContextWindowLimit = 400000, Models = ["gpt-5.4-mini-2026-03-17"] }];



        await using GuildLocalHostReadiness readiness = new(f.Settings, f.Connections, f.Policy, f.Authority,
            TimeProvider.System, Path.Combine(f.WorkspaceRoot, "host.lock"), f.Gate);

        f.Readiness.Inner = readiness;

        Assert.True((await readiness.EnsureReadyAsync(default)).IsSuccess);

        GuildCallerContext owner = await f.OperatorAsync();

        var root = await f.IssueAsync(owner);

        Assert.True(root.IsSuccess, root.Error.Message);

        var recovery = await f.Policy.CreateRecoveryAsync(owner, root.Value.Quest.Id, root.Value.Quest.OperationId,
            root.Value.Quest.SignetRevision, default);

        Assert.True(recovery.IsSuccess, recovery.Error.Message);

        var handle = await f.Authority.RebindAsync(recovery.Value, default);

        Assert.True(handle.IsSuccess, handle.Error.Message);

        var pending = await f.Store.ReadLocalPendingAsync(owner, root.Value.Quest.Id, default);

        Assert.True(pending.IsSuccess, pending.Error.Message);

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

        ArcanumSettings settings = new()
        {
            Features = new() { Guild = true }, DefaultModel = "gpt-5.4-mini-2026-03-17",
            Cost = new(),
            Providers = [new() { Name = "accepted-provider", Type = AiProviderKind.OpenAICompatible,
                Endpoint = "https://api.openai.com/v1", ContextWindowLimit = 400000,
                Models = ["gpt-5.4-mini-2026-03-17"] }],
        };

        BudgetReservationService daily = new(db);

        GuildQuestStore store = new(new(f.Connections, f.Gate, TimeProvider.System), f.Authority, f.Policy,
            new GuildBudgetLedger(daily), TimeProvider.System, new GuildLocalExecutionAdmission(f.Settings), f.Readiness);

        using DurableFinancialTransport transport = new();

        ChatClientFactory clients = new(transport, new TestOptionsMonitor<ArcanumSettings>(settings));

        ChildQueue queue = new();

        ConclaveArchmage coordinator = new(null!, f.Settings, store, queue);

        WizardIntelligenceProviderTests wizard = new();

        try
        {
            var runtime = await wizard.CreateGuildRuntimeAsync(daily, settings, clients, coordinator);

            using ServiceProvider services = new ServiceCollection().BuildServiceProvider();

            using ApprenticeService engine = new(services.GetRequiredService<IServiceScopeFactory>(), f.Settings, new ChronicleHub(),
                NullLogger<ApprenticeService>.Instance, f.Gate);

            GuildLocalPort port = new(store, f.Policy, f.Authority, new LongRunningOperationStore(db, f.Connections),
                engine, runtime.Runtime, queue, new(), TimeProvider.System);

            using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(2));

            await f.ExecuteAsync("CREATE TRIGGER fixture_durable_abort_receipt BEFORE INSERT ON guild_spend_receipts BEGIN SELECT RAISE(ABORT,'injected'); END");

            var first = await port.DispatchAsync(handle.Value, pending.Value.Request!, deadline.Token);

            Assert.True(first.IsSuccess, first.Error.Message);

            Assert.Equal(QuestState.Working, first.Value.State);

            Assert.True(first.Value.ReconciliationRequired);

            var current = await store.GetAsync(owner, root.Value.Quest.Id, deadline.Token);

            Assert.True(current.IsSuccess, current.Error.Message);

            Assert.Null(current.Value.Quest.ResultReceiptId);

            Assert.NotEqual(GuildAccountingState.Settled, current.Value.Quest.AccountingState);

            Assert.Equal(1, transport.Posts);

            Assert.Equal(0, transport.MetadataGets);

            Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_result_receipts WHERE NoDispatchProven=1"));

            Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_budget_effects WHERE StateCode=1"));

            Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM BudgetReservations"));

            Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM BudgetReservations WHERE Status=4"));

            string physicalSnapshot = await ReadDurablePhysicalSnapshotAsync(connection.Connection);

            string runKey = root.Value.Quest.RunId.ToString("N");

            var before = await GuildPhysicalRetentionProof.ReadPageAsync(connection.Connection, null, [runKey], deadline.Token);

            Assert.False(before[runKey]);

            long priorPosts = transport.Posts;

            GuildLocalHostPolicy restartedPolicy = f.NewPolicy();

            using SignetAuthority restartedAuthority = new(new(f.Connections), f.Gate, restartedPolicy, TimeProvider.System);

            var restartedOwner = await restartedPolicy.CreateNativeOperatorAsync(Guid.NewGuid(), deadline.Token);

            Assert.True(restartedOwner.IsSuccess, restartedOwner.Error.Message);

            var reopened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, deadline.Token);

            Assert.True(reopened.IsSuccess, reopened.Error.Message);

            await using var restartedConnection = reopened.Value;

            await using ArcanumDbContext restartedDb = GrimoireFixture.CreateContext(restartedConnection.Connection);

            BudgetReservationService restartedDaily = new(restartedDb);

            GuildQuestStore restartedStore = new(new(f.Connections, f.Gate, TimeProvider.System), restartedAuthority,
                restartedPolicy, new GuildBudgetLedger(restartedDaily), TimeProvider.System, new GuildLocalExecutionAdmission(f.Settings), f.Readiness);

            var retained = await restartedStore.GetAsync(restartedOwner.Value, root.Value.Quest.Id, deadline.Token);

            Assert.True(retained.IsSuccess, retained.Error.Message);

            Assert.Null(retained.Value.Quest.ResultReceiptId);

            Assert.NotEqual(GuildAccountingState.Settled, retained.Value.Quest.AccountingState);

            var restartRecovery = await restartedPolicy.CreateRecoveryAsync(restartedOwner.Value, retained.Value.Quest.Id,
                retained.Value.Quest.OperationId, retained.Value.Quest.SignetRevision, deadline.Token);

            Assert.True(restartRecovery.IsSuccess, restartRecovery.Error.Message);

            var rebound = await restartedAuthority.RebindAsync(restartRecovery.Value, deadline.Token);

            Assert.True(rebound.IsSuccess, rebound.Error.Message);

            GuildLocalPort restartedPort = new(restartedStore, restartedPolicy, restartedAuthority,
                new LongRunningOperationStore(restartedDb, f.Connections), engine, runtime.Runtime, queue, new(), TimeProvider.System);

            var replay = await restartedPort.DispatchAsync(rebound.Value, pending.Value.Request!, deadline.Token);

            Assert.True(replay.IsSuccess, replay.Error.Message);

            Assert.Equal(QuestState.Working, replay.Value.State);

            Assert.True(replay.Value.ReconciliationRequired);

            Assert.Equal(priorPosts, transport.Posts);

            Assert.Equal(physicalSnapshot, await ReadDurablePhysicalSnapshotAsync(restartedConnection.Connection));

            Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_result_receipts WHERE NoDispatchProven=1"));

            var after = await GuildPhysicalRetentionProof.ReadPageAsync(restartedConnection.Connection, null, [runKey], deadline.Token);

            Assert.False(after[runKey]);
        }
        finally
        {
            await wizard.DisposeAsync();
        }
    }

    private static async Task<string> ReadDurablePhysicalSnapshotAsync(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT coalesce((SELECT group_concat(Id || ':' || RunId || ':' || Status || ':' || ReservedUsd || ':' || ReconciledUsd, '|')
                FROM (SELECT * FROM BudgetReservations ORDER BY Id)), '') || ';' ||
                coalesce((SELECT group_concat(EffectId || ':' || coalesce(ReservationId, '') || ':' || StateCode || ':' || UnknownUsd || ':' || UnknownTokens, '|')
                FROM (SELECT * FROM guild_budget_effects ORDER BY EffectId)), '') || ';' ||
                (SELECT count(*) FROM guild_spend_receipts) || ':' || (SELECT count(*) FROM guild_result_receipts)
            """;

        return (string)(await command.ExecuteScalarAsync())!;
    }

    private sealed class DurableFinancialTransport : HttpMessageHandler, IHttpClientFactory
    {
        internal int Posts;

        internal int MetadataGets;

        public HttpClient CreateClient(string name) => new(this, false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get)
            {
                Interlocked.Increment(ref MetadataGets);

                throw new InvalidOperationException("Unexpected provider metadata GET");
            }

            Assert.Equal(HttpMethod.Post, request.Method);

            int ordinal = Interlocked.Increment(ref Posts);

            string message = ordinal == 1
                ? """{"role":"assistant","content":"[{\"index\":1,\"description\":\"Answer task\"}]"}"""
                : """{"role":"assistant","content":"actual durable answer"}""";

            string json = $$$"""{"id":"durable-{{{ordinal}}}","object":"chat.completion","created":1700000000,"model":"gpt-5.4-mini-2026-03-17","choices":[{"index":0,"message":{{{message}}},"finish_reason":"stop"}]}""";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
