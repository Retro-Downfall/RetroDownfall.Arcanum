using System.Collections.Immutable;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Circle_ingress_orders_real_streams_by_durable_acceptance_without_lexical_starvation()
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, _, root, _, _, members) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 2);

        GuildCallerContext[] sources =
        [
            await BindCohortRunAsync(f, owner, members[0]),
            await BindCohortRunAsync(f, owner, members[1]),
        ];

        Task<Result<GuildMessageReceipt>> Publish(GuildCallerContext source, string body, TimeProvider? time = null) =>
            new GuildPortalStore(f.Store, f.Authority, new EventDisclosurePolicy(f.Policy), time ?? TimeProvider.System).PublishAsync(source,
                source.Execution!.QuestId, new(Guid.NewGuid(), new(GuildCircleAddressKind.Parent, root.Quest.Id, null, null),
                    GuildEnvelopeKind.Progress, "text/plain", body, null), default);

        Assert.True((await Publish(sources[0], "warmup one")).IsSuccess);
        Assert.True((await Publish(sources[1], "warmup two")).IsSuccess);

        Guid laterQuest;

        await using (var connection = await f.OpenAsync())
        await using (var query = connection.CreateCommand())
        {
            query.CommandText = "SELECT QuestId FROM guild_portal_streams WHERE ProducerKindCode=0 AND QuestId IN($one,$two) ORDER BY StreamId DESC LIMIT 1";
            query.Parameters.AddWithValue("$one", sources[0].Execution!.QuestId.ToString("D").ToUpperInvariant());
            query.Parameters.AddWithValue("$two", sources[1].Execution!.QuestId.ToString("D").ToUpperInvariant());

            laterQuest = Guid.Parse((string)(await query.ExecuteScalarAsync())!);
        }

        GuildCallerContext later = sources.Single(source => source.Execution!.QuestId == laterQuest);
        GuildCallerContext earlier = sources.Single(source => source.Execution!.QuestId != laterQuest);

        FakeTimeProvider future = new();

        future.SetUtcNow(DateTimeOffset.UtcNow.AddDays(1));

        Assert.True((await Publish(later, "old queued canary", future)).IsSuccess);
        Assert.True((await Publish(earlier, "new busy stream canary")).IsSuccess);

        var current = await f.Store.GetAsync(owner, root.Quest.Id, default);

        Assert.True(current.IsSuccess, current.Error.Message);

        var execution = await ToolExecutionAsync(f, owner, current.Value);
        var prepared = await f.Store.PrepareCircleInputAsync(execution, default);

        Assert.True(prepared.IsSuccess, prepared.Error.Message);

        var batch = Assert.IsType<GuildCircleInputBatch>(prepared.Value);

        int oldIndex = batch.Prompt.IndexOf("old queued canary", StringComparison.Ordinal);
        int newIndex = batch.Prompt.IndexOf("new busy stream canary", StringComparison.Ordinal);

        Assert.True(oldIndex >= 0 && newIndex >= 0);

        Assert.True(oldIndex < newIndex, "An earlier accepted message must precede a later busy-stream message even when stream identities and wall-clock timestamps sort the other way.");
    }

    [Theory]
    [InlineData("erased")]
    [InlineData("protected")]
    [InlineData("forged")]
    [InlineData("budget")]
    [InlineData("accepted-erased")]
    [InlineData("replay")]
    public async Task Distribution_provider_effect_rechecks_sources_and_commits_input_once(string boundary)
    {
        await using Fixture f = await Fixture.CreateAsync(localExecution: true);

        var (owner, issuer, root, cohorts, cohort, members) = await ConvergenceFixtureAsync(f, GuildConvergenceKind.All, null, "text/plain", 1);

        var author = await BindCohortRunAsync(f, owner, members[0]);

        var current = await cohorts.GetAsync(issuer, cohort.Id, default);

        AppendSharedNoteRequest note = new(Guid.NewGuid(), current.Value.Revision, GuildNoteKind.Observation, null, "current source", null);

        var appended = await cohorts.AppendNoteAsync(author, NoteMutation(note), cohort.Id, note, default);

        Assert.True(appended.IsSuccess, appended.Error.Message);

        var freshRoot = await f.Store.GetAsync(owner, root.Quest.Id, default);

        Assert.True(freshRoot.IsSuccess, freshRoot.Error.Message);

        var execution = await ToolExecutionAsync(f, owner, freshRoot.Value);

        var prepared = await f.Store.PrepareCircleInputAsync(execution, default);

        Assert.True(prepared.IsSuccess, prepared.Error.Message);

        var batch = Assert.IsType<GuildCircleInputBatch>(prepared.Value);

        Assert.Single(batch.Items);

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_portal_inputs"));

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

        ArcanumSettings settings = new() { Cost = new() { Budget = new() { Enabled = boundary == "budget", DailyLimitUsd = 0.5m } } };

        BudgetReservationService daily = new(db, new TestOptionsMonitor<ArcanumSettings>(settings));

        GuildBudgetEffectRequest effect = new(Guid.NewGuid(), root.Quest.Id, execution.Dispatch.DispatchId, root.Quest.RunId,
            GuildBudgetEffectKind.LocalProvider, Guid.NewGuid(), null, 1m, 100);

        int sends = 0;

        Task<Result<string>> Send(CancellationToken _) { sends++; return Task.FromResult(Result<string>.Success("answer")); }

        if (boundary is "accepted-erased" or "replay")
        {
            var accepted = await f.Store.ExecuteProviderEffectAsync(execution, daily, effect, Send, default, circleInputs: [batch]);

            Assert.True(accepted.IsSuccess, accepted.Error.Message);

            BillableOperationRecord bill = new(root.Quest.RunId, BillableOperationType.Chat, "provider", "model", "guild",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 7, 3, 0, 0, "{}", 0m, BillableOperationStatus.Completed, null);

            Assert.True((await f.Store.SettleProviderEffectAsync(execution, daily, effect, bill, default)).IsSuccess);
        }

        if (boundary is "erased" or "protected" or "accepted-erased")
        {
            GuildDataRef source = appended.Value.BodyRef!;

            await using var transaction = connection.Connection.BeginTransaction(deferred: false);

            if (boundary == "protected")
            {
                DerivedArtifactWrite label = new(SensitiveArtifactKind.GuildContent, source.ContentId,
                    members[0].Quest.SessionId, members[0].Quest.CampaignId, null, (ulong)source.Revision,
                    new CovenantDigest(Convert.FromHexString(source.Digest)), ContentSensitivity.CovenantDerived,
                    GenerationProvenance.CreateExact([Guid.NewGuid()]));

                Assert.True((await ArtifactSensitivityLedger.WriteWithinAsync(connection.Connection, transaction, label, default)).IsSuccess);
            }
            else
            {
                await GuildContentLifecycle.PrepareOrdinaryPurgeAsync(connection.Connection, transaction, source.ContentId.ToString("N"), default);

                await using var erase = connection.Connection.CreateCommand();

                erase.Transaction = transaction;
                erase.CommandText = "DELETE FROM guild_content WHERE ContentId=$content";
                erase.Parameters.AddWithValue("$content", source.ContentId.ToString("D").ToUpperInvariant());

                Assert.Equal(1, await erase.ExecuteNonQueryAsync());
            }

            await transaction.CommitAsync();
        }

        if (boundary == "forged") batch = new(batch.Items, batch.Prompt);

        GuildBudgetEffectRequest next = boundary == "accepted-erased"
            ? effect with { EffectId = Guid.NewGuid(), SourceId = Guid.NewGuid() } : effect;

        var result = await f.Store.ExecuteProviderEffectAsync(execution, daily, next, Send, default,
            circleInputs: boundary == "accepted-erased" ? null : [batch]);

        Assert.True(result.IsFailure);

        long committed = boundary is "accepted-erased" or "replay" ? 1 : 0;

        Assert.Equal(committed, sends);

        Assert.Equal(committed, await f.ScalarAsync("SELECT count(*) FROM guild_portal_inputs"));

        Assert.Equal(committed, await f.ScalarAsync("SELECT count(*) FROM guild_portal_receipts"));

        Assert.Equal(committed, await f.ScalarAsync("SELECT count(*) FROM guild_budget_effects"));
    }
}
