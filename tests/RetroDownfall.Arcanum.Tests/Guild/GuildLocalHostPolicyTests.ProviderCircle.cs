using System.Net;
using System.Text;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Intelligence;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Distribution_notification_reaches_the_actual_provider_and_is_applied_with_its_effect()
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        await using GuildLocalHostReadiness readiness = new(f.Settings, f.Connections, f.Policy, f.Authority,
            TimeProvider.System, Path.Combine(f.WorkspaceRoot, "host.lock"), f.Gate);

        f.Readiness.Inner = readiness;

        Assert.True((await readiness.EnsureReadyAsync(default)).IsSuccess);

        var (owner, issuer, root, cohorts, cohort, members) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 1);

        var author = await BindCohortRunAsync(f, owner, members[0]);

        FakeTimeProvider future = new();

        future.SetUtcNow(DateTimeOffset.UtcNow.AddDays(1));

        GuildPortalStore portal = new(f.Store, f.Authority, new EventDisclosurePolicy(f.Policy), future);

        var earlier = await portal.PublishAsync(author, members[0].Quest.Id,
            new(Guid.NewGuid(), new(GuildCircleAddressKind.Parent, root.Quest.Id, null, null),
                GuildEnvelopeKind.Progress, "text/plain", "earlier progress canary", null), default);

        Assert.True(earlier.IsSuccess, earlier.Error.Message);

        var current = await cohorts.GetAsync(issuer, cohort.Id, default);

        AppendSharedNoteRequest note = new(Guid.NewGuid(), current.Value.Revision, GuildNoteKind.Observation, null,
            "distribution source canary", null);

        Assert.True((await cohorts.AppendNoteAsync(author, NoteMutation(note), cohort.Id, note, default)).IsSuccess);

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

        LongRunningOperationStore operations = new(db, f.Connections);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        var leased = await operations.TryAcquireLeaseAsync(root.Quest.OperationId, "distribution-provider", now, now.AddMinutes(5));

        Assert.True(leased.Acquired);

        GuildQuestDriver driver = new(issuer.Execution!, "distribution-provider", leased.Operation.Revision, issuer);

        var dispatch = await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(), new string('C', 64), root.Quest.RunId, false, default);

        Assert.True(dispatch.IsSuccess, dispatch.Error.Message);

        var input = await f.Store.ReadLocalInputAsync(driver, default);

        Assert.True(input.IsSuccess, input.Error.Message);

        ArcanumSettings settings = new()
        {
            Features = new() { Guild = true }, DefaultModel = "accepted-model",
            Providers = [new() { Name = "accepted-provider", Type = AiProviderKind.OpenAICompatible,
                Endpoint = "https://example.test/v1", Models = ["accepted-model"] }],
        };

        BudgetReservationService daily = new(db, new TestOptionsMonitor<ArcanumSettings>(settings));

        using CircleInputTransport transport = new();

        ChatClientFactory clients = new(transport, new TestOptionsMonitor<ArcanumSettings>(settings));

        WizardIntelligenceProviderTests wizard = new();

        try
        {
            var runtime = await wizard.CreateGuildRuntimeAsync(daily, settings, clients, new ProviderChildDispatcher(f));

            var result = await runtime.Runtime.ExecuteTurnAsync(new(f.Store, driver, dispatch.Value), input.Value,
                new("Consider the current shared findings.", DisableAllTools: true), default);

            Assert.True(result.IsSuccess, result.Error.Message);

            string payload = Assert.Single(transport.Bodies);

            Assert.Contains("distribution source canary", payload, StringComparison.Ordinal);

            Assert.Contains("UNTRUSTED DATA", payload, StringComparison.Ordinal);

            Assert.Contains("earlier progress canary", payload, StringComparison.Ordinal);

            Assert.True(payload.IndexOf("earlier progress canary", StringComparison.Ordinal)
                < payload.IndexOf("distribution source canary", StringComparison.Ordinal));

            Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_portal_inputs"));

            Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM guild_portal_receipts"));

            Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_budget_effects"));
        }
        finally
        {
            await wizard.DisposeAsync();
        }
    }

    private sealed class CircleInputTransport : HttpMessageHandler, IHttpClientFactory
    {
        internal List<string> Bodies { get; } = [];

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));

            const string response = """{"id":"distribution-response","object":"chat.completion","created":1700000000,"model":"accepted-model","choices":[{"index":0,"message":{"role":"assistant","content":"Shared findings considered."},"finish_reason":"stop"}],"usage":{"prompt_tokens":7,"completion_tokens":3,"total_tokens":10}}""";

            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
