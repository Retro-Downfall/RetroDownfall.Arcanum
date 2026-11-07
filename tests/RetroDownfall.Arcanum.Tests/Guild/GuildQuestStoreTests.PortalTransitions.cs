using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task Dispatch_evidence_transitions_append_one_content_free_projection_each_and_replay_without_new_events()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored row = await ReceiptRootAsync(f);

        GuildQuestDriver driver = await f.DriverAsync(row);

        Guid dispatchId = Guid.NewGuid(), accountingId = Guid.NewGuid(), childId = Guid.NewGuid();

        string digest = ReceiptDigest("project dispatch");

        var prepared = await f.Store.PrepareDispatchAsync(driver, dispatchId, digest, accountingId, false, default);

        Assert.True(prepared.IsSuccess, prepared.Error.Message);

        Assert.Equal(2L, await f.CountAsync("guild_events"));

        Assert.True((await f.Store.PrepareDispatchAsync(driver, dispatchId, digest, accountingId, false, default)).IsSuccess);

        Assert.Equal(2L, await f.CountAsync("guild_events"));

        var exposed = await f.Store.MarkPossiblyDispatchedAsync(driver, dispatchId, prepared.Value.Revision, default);

        Assert.True(exposed.IsSuccess, exposed.Error.Message);

        Assert.Equal(3L, await f.CountAsync("guild_events"));

        Assert.False((await f.Store.MarkPossiblyDispatchedAsync(driver, dispatchId, prepared.Value.Revision, default)).Value.MayDispatch);

        Assert.Equal(3L, await f.CountAsync("guild_events"));

        GuildQuestDispatchEvidence evidence = new(dispatchId, exposed.Value.Dispatch.Revision, GuildDispatchEvidence.Confirmed, childId);

        Assert.True((await f.Store.RecordDispatchEvidenceAsync(driver, evidence, default)).IsSuccess);

        Assert.Equal(4L, await f.CountAsync("guild_events"));

        Assert.True((await f.Store.RecordDispatchEvidenceAsync(driver, evidence, default)).IsSuccess);

        Assert.Equal(4L, await f.CountAsync("guild_events"));

        Assert.Equal(3L, await ReceiptScalarAsync(f, $"SELECT COUNT(*) FROM guild_events WHERE ObjectId='{dispatchId:D}' COLLATE NOCASE AND Revision IN(1,2,3) AND EnvelopeId IS NULL"));

        Assert.Equal(1L, await f.CountAsync("guild_portal_outbox"));

        Assert.Equal(0L, await f.CountAsync("guild_envelopes"));
    }

    [Fact]
    public async Task Dispatch_identity_equal_to_Quest_identity_projects_a_distinct_source_transition()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored row = await ReceiptRootAsync(f);

        GuildQuestDriver driver = await f.DriverAsync(row);

        Guid dispatchId = row.Quest.Id, accountingId = Guid.NewGuid();

        string digest = ReceiptDigest("same identifier distinct source");

        var prepared = await f.Store.PrepareDispatchAsync(driver, dispatchId, digest, accountingId, false, default);

        Assert.True(prepared.IsSuccess, prepared.Error.Message);

        Assert.Equal(2L, await f.CountAsync("guild_events"));

        Assert.True((await f.Store.PrepareDispatchAsync(driver, dispatchId, digest, accountingId, false, default)).IsSuccess);

        Assert.Equal(2L, await f.CountAsync("guild_events"));

        Assert.Equal(2L, await ReceiptScalarAsync(f, $"SELECT COUNT(*) FROM guild_events WHERE ObjectId='{dispatchId:D}' COLLATE NOCASE AND Revision=1"));
    }

    [Theory]
    [InlineData("prepare")]
    [InlineData("dispatch")]
    [InlineData("evidence")]
    public async Task Projection_failure_rolls_back_dispatch_evidence_and_budget_exposure(string stage)
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored row = await ReceiptRootAsync(f);

        GuildQuestDriver driver = await f.DriverAsync(row);

        Guid dispatchId = Guid.NewGuid(), accountingId = Guid.NewGuid();

        string digest = ReceiptDigest("project dispatch");

        if (stage != "prepare")
            Assert.True((await f.Store.PrepareDispatchAsync(driver, dispatchId, digest, accountingId, false, default)).IsSuccess);

        if (stage == "evidence")
            Assert.True((await f.Store.MarkPossiblyDispatchedAsync(driver, dispatchId, 1, default)).IsSuccess);

        long beforeEvents = await f.CountAsync("guild_events");

        long beforeRevision = (await f.Store.GetAsync(f.Caller, row.Quest.Id, default)).Value.Quest.Revision;

        await f.ExecuteAsync("CREATE TRIGGER fixture_projection_failure BEFORE INSERT ON guild_events BEGIN SELECT RAISE(ABORT,'projection failure'); END");

        bool failed = stage switch
        {
            "prepare" => (await f.Store.PrepareDispatchAsync(driver, dispatchId, digest, accountingId, false, default)).IsFailure,
            "dispatch" => (await f.Store.MarkPossiblyDispatchedAsync(driver, dispatchId, 1, default)).IsFailure,
            _ => (await f.Store.RecordDispatchEvidenceAsync(driver, new(dispatchId, 2, GuildDispatchEvidence.Confirmed, Guid.NewGuid()), default)).IsFailure,
        };

        Assert.True(failed, "Projection and its source transition must commit together.");

        Assert.Equal(beforeEvents, await f.CountAsync("guild_events"));

        Assert.Equal(beforeRevision, (await f.Store.GetAsync(f.Caller, row.Quest.Id, default)).Value.Quest.Revision);

        Assert.Equal(stage == "prepare" ? 0L : 1L, await f.CountAsync("guild_dispatches"));

        Assert.Equal(stage == "evidence" ? 1L : 0L, await f.CountAsync("fixture_exposures"));

        if (stage != "prepare")
            Assert.Equal(stage == "evidence" ? 2L : 1L, await ReceiptScalarAsync(f, "SELECT EvidenceCode FROM guild_dispatches"));
    }

    [Fact]
    public async Task Input_wait_and_answer_append_one_projection_per_transition_without_message_outboxes()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored row = await ReceiptRootAsync(f);

        GuildQuestDriver driver = await f.DriverAsync(row);

        Guid questionId = Guid.NewGuid(), continuationId = Guid.NewGuid();

        var waiting = await f.Store.WaitForInputAsync(driver, questionId, continuationId, default);

        Assert.True(waiting.IsSuccess, waiting.Error.Message);

        Assert.Equal(2L, await f.CountAsync("guild_events"));

        Assert.True((await f.Store.WaitForInputAsync(driver, questionId, continuationId, default)).IsSuccess);

        Assert.Equal(2L, await f.CountAsync("guild_events"));

        GuildContinueRequest request = new(Guid.NewGuid(), waiting.Value.Quest.Revision, continuationId, "text/plain", "Approved answer");

        var accepted = await f.Store.AcceptContinuationAsync(f.Caller, row.Quest.Id, request, default);

        Assert.True(accepted.IsSuccess, accepted.Error.Message);

        Assert.Equal(3L, await f.CountAsync("guild_events"));

        Assert.True((await f.Store.AcceptContinuationAsync(f.Caller, row.Quest.Id, request, default)).Value.Quest.Receipt!.Replayed);

        Assert.Equal(3L, await f.CountAsync("guild_events"));

        Assert.Equal(1L, await f.CountAsync("guild_portal_outbox"));

        Assert.Equal(0L, await f.CountAsync("guild_envelopes"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Projection_failure_rolls_back_input_state_and_accepted_body(bool answer)
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored row = await ReceiptRootAsync(f);

        GuildQuestDriver driver = await f.DriverAsync(row);

        Guid questionId = Guid.NewGuid(), continuationId = Guid.NewGuid();

        if (answer)
            Assert.True((await f.Store.WaitForInputAsync(driver, questionId, continuationId, default)).IsSuccess);

        GuildQuestStored before = (await f.Store.GetAsync(f.Caller, row.Quest.Id, default)).Value;

        long beforeEvents = await f.CountAsync("guild_events");

        long beforeBodies = await f.CountAsync("guild_content");

        long beforeOperation = (long)(await ReceiptScalarAsync(f, "SELECT Revision FROM LongRunningOperations"))!;

        await f.ExecuteAsync("CREATE TRIGGER fixture_projection_failure BEFORE INSERT ON guild_events BEGIN SELECT RAISE(ABORT,'projection failure'); END");

        bool failed = answer
            ? (await f.Store.AcceptContinuationAsync(f.Caller, row.Quest.Id,
                new(Guid.NewGuid(), before.Quest.Revision, continuationId, "text/plain", "Retain only if committed"), default)).IsFailure
            : (await f.Store.WaitForInputAsync(driver, questionId, continuationId, default)).IsFailure;

        Assert.True(failed, "Projection failure must roll back the input state transition.");

        GuildQuestStored after = (await f.Store.GetAsync(f.Caller, row.Quest.Id, default)).Value;

        Assert.Equal(before.Quest.Revision, after.Quest.Revision);

        Assert.Equal(before.Quest.State, after.Quest.State);

        Assert.Equal(beforeOperation, await ReceiptScalarAsync(f, "SELECT Revision FROM LongRunningOperations"));

        Assert.Equal(beforeEvents, await f.CountAsync("guild_events"));

        Assert.Equal(beforeBodies, await f.CountAsync("guild_content"));

        Assert.Equal(0L, await f.CountAsync("guild_continuations"));
    }
}
