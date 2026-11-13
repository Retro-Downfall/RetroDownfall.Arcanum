using System.Net;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    private const string SequenceHigh = """
        {"data":{"api_type":"completions","id":"gen-test-123","is_byok":false,"model":"provider/model",
        "native_tokens_cached":40,"native_tokens_completion":20,"native_tokens_completion_images":0,
        "native_tokens_prompt":110,"native_tokens_reasoning":5,"num_input_audio_prompt":0,
        "num_media_completion":0,"num_media_prompt":0,"total_cost":0.30}}
        """;

    private const string SequenceLower = """
        {"data":{"api_type":"completions","id":"gen-test-123","is_byok":false,"model":"provider/model",
        "native_tokens_cached":40,"native_tokens_completion":20,"native_tokens_completion_images":0,
        "native_tokens_prompt":105,"native_tokens_reasoning":5,"num_input_audio_prompt":0,
        "num_media_completion":0,"num_media_prompt":0,"total_cost":0.28}}
        """;

    [Fact]
    public async Task Provider_sequence_converges_replays_original_and_rejects_new_lower_facts_after_operator_increase()
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync();

        await using SqliteConnection connection = await f.Scenario.Fixture.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        using LongRunningOperationStore operations = new(db, f.Scenario.Fixture.Connections);

        var acquired = GuildWorkspaceLeaseHostLock.TryAcquire(Path.Combine(Path.GetTempPath(), "arcanum-sequence-" + Guid.NewGuid().ToString("N")));

        Assert.True(acquired.IsSuccess);

        using GuildWorkspaceLeaseHostLock hostLock = acquired.Value;

        SequenceResponse response = new() { Body = SequenceHigh };

        GuildSpendRefreshService service = new(f.Scenario.Fixture.Store, f.Configuration, f.Resolver,
            new GuildOpenRouterSpendReader(new PublicSpendDns(), () => response.Handler()), new RefreshTestReadiness(hostLock),
            f.Scenario.Fixture.Gate, operations, new LongRunningOperationOwnership(), TimeProvider.System, new GuildSpendRefreshTracker());

        Guid firstId = Guid.NewGuid();

        Guid sameId = Guid.NewGuid();

        var first = await service.RefreshAsync(f.Scenario.Fixture.Caller, f.QuestId, f.Scenario.EffectId, firstId, default);

        Assert.True(first.IsSuccess, first.IsFailure ? first.Error.Message : null);

        GuildSpendReceiptDto receipt = Assert.IsType<GuildSpendReceiptDto>(first.Value.Receipt);

        Assert.Equal(GuildSpendOrigin.ProviderReported, receipt.Origin);

        Assert.Equal(.30m, receipt.Facts.KnownUsd);

        long receipts = await f.Scenario.Fixture.CountAsync("guild_spend_receipts");

        long evidence = await f.Scenario.Fixture.CountAsync("guild_budget_evidence");

        long events = await f.Scenario.Fixture.CountAsync("guild_events");

        var same = await service.RefreshAsync(f.Scenario.Fixture.Caller, f.QuestId, f.Scenario.EffectId, sameId, default);

        Assert.True(same.IsSuccess, same.IsFailure ? same.Error.Message : null);

        Assert.Equal(receipt, same.Value.Receipt);

        Assert.Equal(receipts, await f.Scenario.Fixture.CountAsync("guild_spend_receipts"));

        Assert.Equal(evidence, await f.Scenario.Fixture.CountAsync("guild_budget_evidence"));

        Assert.Equal(events, await f.Scenario.Fixture.CountAsync("guild_events"));

        await AssertSequenceAssociationAsync(f, firstId, receipt);

        await AssertSequenceAssociationAsync(f, sameId, receipt);

        var correction = await f.Scenario.Fixture.Store.CorrectOperatorSpendAsync(f.Scenario.Fixture.Caller, f.QuestId,
            f.Scenario.EffectId, new(Guid.NewGuid(), receipt.EvidenceRevision, GuildSpendOrigin.OperatorAttested,
                new(120, 25, 45, 6, .40m), new string('B', 64)), default);

        Assert.True(correction.IsSuccess, correction.IsFailure ? correction.Error.Message : null);

        long afterReceipts = await f.Scenario.Fixture.CountAsync("guild_spend_receipts");

        long afterEvidence = await f.Scenario.Fixture.CountAsync("guild_budget_evidence");

        long afterEvents = await f.Scenario.Fixture.CountAsync("guild_events");

        var replay = await service.RefreshAsync(f.Scenario.Fixture.Caller, f.QuestId, f.Scenario.EffectId, firstId, default);

        Assert.True(replay.IsSuccess, replay.IsFailure ? replay.Error.Message : null);

        Assert.True(replay.Value.Replayed);

        Assert.Equal(receipt, replay.Value.Receipt);

        Assert.Equal(2, response.Reads);

        response.Body = SequenceLower;

        Guid lowerId = Guid.NewGuid();

        var lower = await service.RefreshAsync(f.Scenario.Fixture.Caller, f.QuestId, f.Scenario.EffectId, lowerId, default);

        Assert.True(lower.IsSuccess, lower.IsFailure ? lower.Error.Message : null);

        Assert.True(lower.Value.RequiresAttention);

        Assert.Null(lower.Value.Receipt);

        Assert.Equal(3, response.Reads);

        Assert.Equal(afterReceipts, await f.Scenario.Fixture.CountAsync("guild_spend_receipts"));

        Assert.Equal(afterEvidence, await f.Scenario.Fixture.CountAsync("guild_budget_evidence"));

        Assert.Equal(afterEvents, await f.Scenario.Fixture.CountAsync("guild_events"));

        await AssertSequenceNoPointerAndBillAsync(f, lowerId, correction.Value.Receipt);

        Assert.Equal(1, f.Scenario.Transport.Calls);

        Assert.Equal(1L, await f.Scenario.Fixture.CountAsync("BillableOperations"));
    }

    [Fact]
    public async Task Provider_sequence_held_response_does_not_overwrite_newer_operator_correction()
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync();

        await using SqliteConnection connection = await f.Scenario.Fixture.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        using LongRunningOperationStore operations = new(db, f.Scenario.Fixture.Connections);

        var acquired = GuildWorkspaceLeaseHostLock.TryAcquire(Path.Combine(Path.GetTempPath(), "arcanum-sequence-" + Guid.NewGuid().ToString("N")));

        Assert.True(acquired.IsSuccess);

        using GuildWorkspaceLeaseHostLock hostLock = acquired.Value;

        SequenceResponse response = new() { Body = SequenceHigh, Hold = true };

        GuildSpendRefreshService service = new(f.Scenario.Fixture.Store, f.Configuration, f.Resolver,
            new GuildOpenRouterSpendReader(new PublicSpendDns(), () => response.Handler()), new RefreshTestReadiness(hostLock),
            f.Scenario.Fixture.Gate, operations, new LongRunningOperationOwnership(), TimeProvider.System, new GuildSpendRefreshTracker());

        Guid refresh = Guid.NewGuid();

        Task<RetroDownfall.Arcanum.Core.Primitives.Result<GuildSpendRefreshResult>> pending = service.RefreshAsync(
            f.Scenario.Fixture.Caller, f.QuestId, f.Scenario.EffectId, refresh, default);

        try
        {
            await response.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

            f.Query.CommandText = "SELECT Revision FROM guild_budget_effects WHERE EffectId=$effect";

            long revision = Assert.IsType<long>(await f.Query.ExecuteScalarAsync());

            var correction = await f.Scenario.Fixture.Store.CorrectOperatorSpendAsync(f.Scenario.Fixture.Caller, f.QuestId,
                f.Scenario.EffectId, new(Guid.NewGuid(), revision, GuildSpendOrigin.OperatorAttested,
                    new(120, 25, 45, 6, .40m), new string('C', 64)), default);

            Assert.True(correction.IsSuccess, correction.IsFailure ? correction.Error.Message : null);

            long receipts = await f.Scenario.Fixture.CountAsync("guild_spend_receipts");

            long evidence = await f.Scenario.Fixture.CountAsync("guild_budget_evidence");

            long events = await f.Scenario.Fixture.CountAsync("guild_events");

            response.Release.TrySetResult();

            var result = await pending.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

            Assert.True(result.Value.RequiresAttention);

            Assert.Null(result.Value.Receipt);

            Assert.Equal(receipts, await f.Scenario.Fixture.CountAsync("guild_spend_receipts"));

            Assert.Equal(evidence, await f.Scenario.Fixture.CountAsync("guild_budget_evidence"));

            Assert.Equal(events, await f.Scenario.Fixture.CountAsync("guild_events"));

            await AssertSequenceNoPointerAndBillAsync(f, refresh, correction.Value.Receipt);

            Assert.Equal(1, response.Reads);

            Assert.Equal(1, f.Scenario.Transport.Calls);

            Assert.Equal(1L, await f.Scenario.Fixture.CountAsync("BillableOperations"));
        }
        finally
        {
            response.Release.TrySetResult();

            await pending;
        }
    }

    [Fact]
    public async Task Provider_sequence_held_response_rechecks_configuration_before_accepting_facts()
    {
        await using RefreshApplicationScenario f = await RefreshApplicationScenario.CreateAsync();

        await using SqliteConnection connection = await f.Scenario.Fixture.OpenAsync();

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

        using LongRunningOperationStore operations = new(db, f.Scenario.Fixture.Connections);

        var acquired = GuildWorkspaceLeaseHostLock.TryAcquire(Path.Combine(Path.GetTempPath(), "arcanum-sequence-" + Guid.NewGuid().ToString("N")));

        Assert.True(acquired.IsSuccess);

        using GuildWorkspaceLeaseHostLock hostLock = acquired.Value;

        SequenceResponse response = new() { Body = SequenceHigh, Hold = true };

        GuildSpendRefreshService service = new(f.Scenario.Fixture.Store, f.Configuration, f.Resolver,
            new GuildOpenRouterSpendReader(new PublicSpendDns(), () => response.Handler()), new RefreshTestReadiness(hostLock),
            f.Scenario.Fixture.Gate, operations, new LongRunningOperationOwnership(), TimeProvider.System, new GuildSpendRefreshTracker());

        Guid refresh = Guid.NewGuid();

        long receipts = await f.Scenario.Fixture.CountAsync("guild_spend_receipts");

        long evidence = await f.Scenario.Fixture.CountAsync("guild_budget_evidence");

        long events = await f.Scenario.Fixture.CountAsync("guild_events");

        f.Query.CommandText = "SELECT Revision FROM guild_budget_effects WHERE EffectId=$effect";

        long revision = Assert.IsType<long>(await f.Query.ExecuteScalarAsync());

        Task<RetroDownfall.Arcanum.Core.Primitives.Result<GuildSpendRefreshResult>> pending = service.RefreshAsync(
            f.Scenario.Fixture.Caller, f.QuestId, f.Scenario.EffectId, refresh, default);

        try
        {
            await response.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

            f.Scenario.Settings.Providers[0].Endpoint = "https://changed.example/v1";

            response.Release.TrySetResult();

            var result = await pending.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

            Assert.True(result.Value.RequiresAttention);

            Assert.Null(result.Value.Receipt);

            Assert.Equal(receipts, await f.Scenario.Fixture.CountAsync("guild_spend_receipts"));

            Assert.Equal(evidence, await f.Scenario.Fixture.CountAsync("guild_budget_evidence"));

            Assert.Equal(events, await f.Scenario.Fixture.CountAsync("guild_events"));

            f.Query.CommandText = "SELECT AcceptedReceiptId FROM guild_spend_refreshes WHERE RefreshId=$id";

            f.Query.Parameters.AddWithValue("$id", refresh.ToString("D").ToUpperInvariant());

            Assert.Equal(DBNull.Value, await f.Query.ExecuteScalarAsync());

            f.Query.CommandText = "SELECT Revision FROM guild_budget_effects WHERE EffectId=$effect";

            Assert.Equal(revision, await f.Query.ExecuteScalarAsync());

            f.Query.CommandText = "SELECT ActualCostUsd FROM BillableOperations";

            Assert.Equal(0m, ExactUsdText.Parse(Assert.IsType<string>(await f.Query.ExecuteScalarAsync())));

            Assert.Equal(1, response.Reads);

            Assert.Equal(1, f.Scenario.Transport.Calls);

            Assert.Equal(1L, await f.Scenario.Fixture.CountAsync("BillableOperations"));
        }
        finally
        {
            response.Release.TrySetResult();

            await pending;
        }
    }

    private static async Task AssertSequenceAssociationAsync(RefreshApplicationScenario f, Guid refresh, GuildSpendReceiptDto receipt)
    {
        f.Query.CommandText = """
            SELECT count(*) FROM guild_spend_refreshes f JOIN guild_spend_receipts r ON r.ReceiptId=f.AcceptedReceiptId
            JOIN guild_events e ON e.SpendReceiptId=r.ReceiptId AND e.SpendEffectId=r.EffectId
                AND e.SpendEvidenceRevision=r.EvidenceRevision AND e.EventSequence=r.OriginalEventSequence
            WHERE f.RefreshId=$id AND r.ReceiptId=$receipt AND r.EffectId=$effect AND r.OriginCode=3
            """;

        f.Query.Parameters.AddWithValue("$id", refresh.ToString("D").ToUpperInvariant());

        f.Query.Parameters.AddWithValue("$receipt", receipt.ReceiptId.ToString("D").ToUpperInvariant());

        Assert.Equal(1L, await f.Query.ExecuteScalarAsync());

        f.Query.Parameters.RemoveAt("$id");

        f.Query.Parameters.RemoveAt("$receipt");
    }

    private static async Task AssertSequenceNoPointerAndBillAsync(RefreshApplicationScenario f, Guid refresh, GuildSpendReceiptDto correction)
    {
        f.Query.CommandText = "SELECT AcceptedReceiptId FROM guild_spend_refreshes WHERE RefreshId=$id";

        f.Query.Parameters.AddWithValue("$id", refresh.ToString("D").ToUpperInvariant());

        Assert.Equal(DBNull.Value, await f.Query.ExecuteScalarAsync());

        f.Query.Parameters.RemoveAt("$id");

        f.Query.CommandText = "SELECT Revision FROM guild_budget_effects WHERE EffectId=$effect";

        Assert.Equal(correction.EvidenceRevision, await f.Query.ExecuteScalarAsync());

        f.Query.CommandText = "SELECT InputTokens,OutputTokens,CachedTokens,ReasoningTokens,ActualCostUsd FROM BillableOperations";

        await using SqliteDataReader row = await f.Query.ExecuteReaderAsync();

        Assert.True(await row.ReadAsync());

        Assert.Equal(120L, row.GetInt64(0));

        Assert.Equal(25L, row.GetInt64(1));

        Assert.Equal(45L, row.GetInt64(2));

        Assert.Equal(6L, row.GetInt64(3));

        Assert.Equal(.40m, ExactUsdText.Read(row, 4));

        Assert.False(await row.ReadAsync());
    }

    private sealed class SequenceResponse
    {
        internal string Body { get; set; } = "";

        internal bool Hold { get; init; }

        internal int Reads;

        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal HttpMessageHandler Handler() => new SequenceHandler(this);

        private sealed class SequenceHandler(SequenceResponse owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref owner.Reads);

                Assert.Equal(SpendSecret, request.Headers.Authorization?.Parameter);

                Assert.Contains("gen-test-123", request.RequestUri!.Query, StringComparison.Ordinal);

                owner.Entered.TrySetResult();

                if (owner.Hold) await owner.Release.Task.WaitAsync(cancellationToken);

                return new(HttpStatusCode.OK) { Content = new StringContent(owner.Body) };
            }
        }
    }
}
