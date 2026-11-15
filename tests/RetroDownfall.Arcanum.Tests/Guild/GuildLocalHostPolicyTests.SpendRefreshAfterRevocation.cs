using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    private const string RevocationSpendSecret = "revocation-proof-synthetic-provider-key";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revoked_signet_refuses_new_inference_but_preserves_authenticated_provider_spend(bool recover)
    {
        using IDisposable history = WorkspaceMutationFence.IsolateProcessHistoryForTests();

        await using Fixture f = await Fixture.CreateAsync(productionOptions: true);

        f.Settings.CurrentValue.Features.Conclave = true;

        f.Settings.CurrentValue.Features.Apprentices = true;

        f.Settings.CurrentValue.DefaultModel = "provider/model";

        f.Settings.CurrentValue.Cost.Budget.Enabled = false;

        f.Settings.CurrentValue.Providers = [new()
        {
            Name = "router", Type = AiProviderKind.OpenAICompatible, Endpoint = "https://openrouter.ai/api/v1",
            Models = ["provider/model"], GuildUsageReconciliation = GuildUsageReconciliationSource.OpenRouterGeneration,
            CredentialEnvironmentVariable = "ARCANUM_REVOCATION_PROOF_" + Guid.NewGuid().ToString("N"),
        }];

        GuildSpendRefreshTracker tracker = new();

        await using GuildLocalHostReadiness readiness = new(f.Settings, f.Connections, f.Policy, f.Authority,
            TimeProvider.System, Path.Combine(f.WorkspaceRoot, "host.lock"), f.Gate, tracker);

        f.Readiness.Inner = readiness;

        Assert.True((await readiness.EnsureReadyAsync(default)).IsSuccess);

        GuildCallerContext owner = await f.OperatorAsync();

        var issued = await f.IssueAsync(owner);

        Assert.True(issued.IsSuccess, issued.Error.Message);

        GuildQuestDto quest = issued.Value.Quest;

        Assert.Null(quest.WorkspaceId);

        Assert.Null(quest.CampaignId);

        GuildLocalInferenceExecution execution = await ToolExecutionAsync(f, owner, issued.Value);

        var input = await f.Store.ReadLocalInputAsync(execution.Driver, default);

        Assert.True(input.IsSuccess, input.Error.Message);

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

        BudgetReservationService daily = new(db, f.Settings);

        InMemoryOsCredentialStore os = new();

        os.Set(ArcanumCredentialIdentity.Service, ArcanumCredentialIdentity.InferenceProviderApiKeyAccount("router"), RevocationSpendSecret);

        using ProviderCredentialStore credentialStore = new(os, new EphemeralDataProtectionProvider());

        ProviderApiKeyResolver resolver = new(credentialStore);

        using RevocationSpendCompletionTransport transport = new();

        ChatClientFactory clients = new(transport, f.Settings, resolver);

        WizardIntelligenceProviderTests wizard = new();

        try
        {
            var runtime = await wizard.CreateGuildRuntimeAsync(daily, f.Settings.CurrentValue, clients);

            using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(2));

            var initialCall = await runtime.Runtime.ExecuteTurnAsync(execution, input.Value,
                new("Answer the question.", DisableAllTools: true), deadline.Token);

            Assert.True(initialCall.IsSuccess, initialCall.Error.Message);

            Assert.Equal(1, transport.Posts);

            RevocationSpendBill original = await ReadRevocationSpendBillAsync(connection.Connection, deadline.Token);

            Assert.Equal(quest.RunId, Guid.Parse(original.RunId));

            Assert.Equal("router", original.Provider);

            Assert.Equal("provider/model", original.Model);

            Assert.Equal("gen-test-123", original.ProviderRequestId);

            Assert.Equal((7L, 3L, 0L, 0L, 0m),
                (original.InputTokens, original.OutputTokens, original.CachedTokens, original.ReasoningTokens, original.Usd));

            var effects = await f.Store.ListOperatorSpendEffectsAsync(owner, quest.Id, null, 100, deadline.Token);

            Assert.True(effects.IsSuccess, effects.Error.Message);

            GuildSpendEffectDto initial = Assert.Single(effects.Value.Items);

            Assert.Equal(Guid.Parse(original.Id), initial.LastReceiptId);

            Assert.Equal(10L, initial.Facts.KnownTokens);

            Assert.True(initial.Facts.UnknownUsd);

            Assert.False(initial.Facts.UnknownTokens);

            Assert.Equal(new GuildSpendLocalTokensDto(7, 3, 0, 0), initial.Facts.LocalTokens);

            var liveInput = await f.Store.MaterializeProviderInputAsync(execution, input.Value, deadline.Token);

            Assert.True(liveInput.IsSuccess, liveInput.Error.Message);

            var current = await f.Store.GetAsync(owner, quest.Id, deadline.Token);

            Assert.True(current.IsSuccess, current.Error.Message);

            Guid revokeKey = Guid.NewGuid();

            var revoked = await f.Authority.RevokeAsync(owner, new(revokeKey, new string('B', 64)), quest.SignetId,
                new(revokeKey, current.Value.Quest.SignetRevision, GuildCancellationReason.OperatorRequested), deadline.Token);

            Assert.True(revoked.IsSuccess, revoked.Error.Message);

            var refused = await runtime.Runtime.ExecuteTurnAsync(execution, input.Value,
                new("A fresh inference after revocation.", DisableAllTools: true), deadline.Token);

            Assert.True(refused.IsFailure);

            Assert.Equal(1, transport.Posts);

            Assert.Equal(original, await ReadRevocationSpendBillAsync(connection.Connection, deadline.Token));

            GuildPortalStore portal = new(f.Store, f.Authority, f.Policy, TimeProvider.System);

            var snapshot = await portal.CaptureTreeSnapshotAsync(owner, quest.Id, deadline.Token);

            Assert.True(snapshot.IsSuccess, snapshot.Error.Message);

            long evidenceBefore = await f.ScalarAsync("SELECT COUNT(*) FROM guild_budget_evidence");

            long receiptsBefore = await f.ScalarAsync("SELECT COUNT(*) FROM guild_spend_receipts");

            long eventsBefore = await f.ScalarAsync("SELECT COUNT(*) FROM guild_events WHERE SpendEffectId IS NOT NULL");

            using LongRunningOperationStore operations = new(db, f.Connections);

            using GuildSpendRefreshConfiguration configuration = new(f.Settings);

            int metadataGets = 0;

            GuildOpenRouterSpendReader reader = new(new RefreshRecoveryDns(),
                () => new RevocationSpendMetadataTransport(() => Interlocked.Increment(ref metadataGets)));

            LongRunningOperationOwnership ownership = new();

            GuildSpendRefreshService service = new(f.Store, configuration, resolver, reader, readiness, f.Gate,
                operations, ownership, TimeProvider.System, tracker);

            Guid refreshId = Guid.NewGuid();

            Guid? recoveredOperation = null;

            if (recover)
            {
                var source = await GuildSpendRefreshCurrentness.CaptureAsync(configuration, resolver, "router", "provider/model", deadline.Token);

                Assert.True(source.IsSuccess, source.Error.Message);

                var admitted = await f.Store.AdmitProviderSpendRefreshAsync(owner, quest.Id, initial.EffectId,
                    refreshId, source.Value, deadline.Token);

                Assert.True(admitted.IsSuccess, admitted.Error.Message);

                recoveredOperation = admitted.Value.OperationId!.Value;

                Assert.Equal(0, metadataGets);

                Assert.Equal(0, (await operations.GetAsync(recoveredOperation.Value, deadline.Token))!.AttemptCount);

                GuildSpendRefreshRecoveryHandler handler = new(f.Policy, service);

                LongRunningOperationReconciler reconciler = new(operations, [handler], TimeProvider.System,
                    NullLogger<LongRunningOperationReconciler>.Instance, ownership);

                await reconciler.ReconcileNowAsync("revocation-proof-recovery", maxConcurrency: 1, cancellationToken: deadline.Token);
            }

            var accepted = await service.RefreshAsync(owner, quest.Id, initial.EffectId, refreshId, deadline.Token);

            Assert.True(accepted.IsSuccess, accepted.Error.Message);

            Assert.False(accepted.Value.InFlight);

            Assert.False(accepted.Value.RequiresAttention);

            Assert.False(accepted.Value.Cancelled);

            Assert.Equal(recover, accepted.Value.Replayed);

            if (recoveredOperation is { } expectedOperation) Assert.Equal(expectedOperation, accepted.Value.OperationId);

            GuildSpendReceiptDto receipt = Assert.IsType<GuildSpendReceiptDto>(accepted.Value.Receipt);

            Assert.Equal(quest.Id, receipt.QuestId);

            Assert.Equal(initial.EffectId, receipt.EffectId);

            Assert.Equal(GuildSpendOrigin.ProviderReported, receipt.Origin);

            Assert.Equal(initial.EvidenceRevision + 1, receipt.EvidenceRevision);

            Assert.Equal(.25m, receipt.Facts.KnownUsd);

            Assert.Equal(120L, receipt.Facts.KnownTokens);

            Assert.Equal(new GuildSpendLocalTokensDto(100, 20, 40, 5), receipt.Facts.LocalTokens);

            Assert.Equal(initial.Facts.UnknownUsd, receipt.Facts.UnknownUsd);

            Assert.Equal(initial.Facts.UnknownTokens, receipt.Facts.UnknownTokens);

            Assert.Equal(initial.Facts.Complete, receipt.Facts.Complete);

            Assert.Equal(original with { InputTokens = 100, OutputTokens = 20, CachedTokens = 40, ReasoningTokens = 5, Usd = .25m },
                await ReadRevocationSpendBillAsync(connection.Connection, deadline.Token));

            Assert.Equal(evidenceBefore + 1, await f.ScalarAsync("SELECT COUNT(*) FROM guild_budget_evidence"));

            Assert.Equal(receiptsBefore + 1, await f.ScalarAsync("SELECT COUNT(*) FROM guild_spend_receipts"));

            Assert.Equal(eventsBefore + 1, await f.ScalarAsync("SELECT COUNT(*) FROM guild_events WHERE SpendEffectId IS NOT NULL"));

            var events = await portal.ReadScopedReplayAsync(owner, quest.Id, quest.Id, null, snapshot.Value.ResumeCursor, deadline.Token);

            Assert.True(events.IsSuccess, events.Error.Message);

            GuildChangeEventDto changed = Assert.Single(events.Value.Page.Items.Where(item => item.SpendSource is not null));

            Assert.Equal(GuildEventKind.SpendChanged, changed.EventKind);

            Assert.Equal(quest.Id, changed.QuestId);

            Assert.Equal(quest.Id, changed.ObjectId);

            Assert.Empty(changed.ContentRefs);

            Assert.Equal(new GuildSpendSourceDto(initial.EffectId, receipt.ReceiptId, receipt.EvidenceRevision, GuildSpendOrigin.ProviderReported),
                changed.SpendSource);

            Assert.Equal(receipt.EventSequence, changed.EventSequence);

            Assert.True(changed.EventSequence > snapshot.Value.Watermark);

            var saved = await f.Store.ReadOperatorSpendReceiptAsync(owner, quest.Id, initial.EffectId, receipt.ReceiptId, deadline.Token);

            Assert.True(saved.IsSuccess, saved.Error.Message);

            Assert.Equal(receipt, saved.Value);

            var replay = await service.RefreshAsync(owner, quest.Id, initial.EffectId, refreshId, deadline.Token);

            Assert.True(replay.IsSuccess, replay.Error.Message);

            Assert.True(replay.Value.Replayed);

            Assert.Equal(accepted.Value.OperationId, replay.Value.OperationId);

            Assert.Equal(receipt, replay.Value.Receipt);

            LongRunningOperation final = (await operations.GetAsync(accepted.Value.OperationId, deadline.Token))!;

            Assert.Equal(LongRunningOperationState.Completed, final.State);

            Assert.False(ownership.IsClaimed(final.Id));

            var stillRefused = await runtime.Runtime.ExecuteTurnAsync(execution, input.Value,
                new("Metadata is not fresh inference authority.", DisableAllTools: true), deadline.Token);

            Assert.True(stillRefused.IsFailure);

            Assert.Equal(1, transport.Posts);

            Assert.Equal(1, metadataGets);

            Assert.Equal(1L, await f.ScalarAsync("SELECT COUNT(*) FROM BillableOperations"));

            Assert.Equal(0L, await f.ScalarAsync("SELECT COUNT(*) FROM BudgetReservations"));

            Assert.Equal(evidenceBefore + 1, await f.ScalarAsync("SELECT COUNT(*) FROM guild_budget_evidence"));

            Assert.Equal(receiptsBefore + 1, await f.ScalarAsync("SELECT COUNT(*) FROM guild_spend_receipts"));

            Assert.Equal(eventsBefore + 1, await f.ScalarAsync("SELECT COUNT(*) FROM guild_events WHERE SpendEffectId IS NOT NULL"));
        }
        finally
        {
            await wizard.DisposeAsync();
        }
    }

    private sealed record RevocationSpendBill(string Id, string RunId, long OperationType, string Provider, string Model,
        string Purpose, string StartedAt, string CompletedAt, string PricingSnapshotJson, long Status, string? ProviderRequestId,
        long InputTokens, long OutputTokens, long CachedTokens, long ReasoningTokens, decimal Usd);

    private static async Task<RevocationSpendBill> ReadRevocationSpendBillAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT Id,RunId,OperationType,Provider,Model,Purpose,StartedAt,CompletedAt,PricingSnapshotJson,Status,ProviderRequestId,
                InputTokens,OutputTokens,CachedTokens,ReasoningTokens,ActualCostUsd FROM BillableOperations
            """;

        await using SqliteDataReader row = await command.ExecuteReaderAsync(ct);

        Assert.True(await row.ReadAsync(ct));

        RevocationSpendBill bill = new(row.GetString(0), row.GetString(1), row.GetInt64(2), row.GetString(3), row.GetString(4),
            row.GetString(5), row.GetString(6), row.GetString(7), row.GetString(8), row.GetInt64(9), row.IsDBNull(10) ? null : row.GetString(10),
            row.GetInt64(11), row.GetInt64(12), row.GetInt64(13), row.GetInt64(14), ExactUsdText.Read(row, 15));

        Assert.False(await row.ReadAsync(ct));

        return bill;
    }

    private sealed class RevocationSpendCompletionTransport : HttpMessageHandler, IHttpClientFactory
    {
        internal int Posts;

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Posts);

            Assert.Equal(HttpMethod.Post, request.Method);

            Assert.Equal("https://openrouter.ai/api/v1/chat/completions", request.RequestUri!.AbsoluteUri);

            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);

            Assert.Equal(RevocationSpendSecret, request.Headers.Authorization?.Parameter);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"id":"gen-test-123","object":"chat.completion","created":1700000000,"model":"provider/model",
                    "choices":[{"index":0,"message":{"role":"assistant","content":"qualified answer"},"finish_reason":"stop"}],
                    "usage":{"prompt_tokens":7,"completion_tokens":3,"total_tokens":10}}
                    """, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class RevocationSpendMetadataTransport(Action observe) : DelegatingHandler(new RefreshRecoveryTransport())
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            observe();

            Assert.Equal(HttpMethod.Get, request.Method);

            Assert.Equal("https://openrouter.ai/api/v1/generation?id=gen-test-123", request.RequestUri!.AbsoluteUri);

            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);

            Assert.Equal(RevocationSpendSecret, request.Headers.Authorization?.Parameter);

            return base.SendAsync(request, ct);
        }
    }
}
