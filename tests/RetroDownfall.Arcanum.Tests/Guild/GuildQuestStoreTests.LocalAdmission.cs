using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task Local_admission_commits_one_child_and_replay_keeps_the_accepted_inputs()
    {
        await using Fixture f = await Fixture.CreateAsync();

        LocalAdmission participant = new();

        f.UseLocalAdmission(participant);

        Result<GuildQuestStored> issued = await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default);

        Assert.True(issued.IsSuccess, issued.Error.Message);

        Assert.Equal(1, participant.Resolutions);

        Assert.Equal(1L, await f.CountAsync("guild_local_executions"));

        Assert.Equal(1L, await f.CountAsync("Apprentices"));

        await using SqliteConnection connection = await f.OpenAsync();

        await using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            Result<GuildLocalExecutionInput> input = await GuildLocalExecutionStore.ReadInputWithinAsync(
                connection, transaction, issued.Value.Quest.Id, null, default);

            Assert.True(input.IsSuccess, input.Error.Message);

            Assert.Equal("accepted-model", input.Value.Model);
        }

        participant.Refuse = true;

        f.Restart();

        Result<GuildQuestStored> replay = await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(issued.Value.Quest.Id, replay.Value.Quest.Id);

        Assert.True(replay.Value.Quest.Receipt!.Replayed);

        Assert.Equal(1, participant.Resolutions);

        Assert.Equal(1L, await f.CountAsync("guild_local_executions"));

        Assert.Equal(1L, await f.CountAsync("Apprentices"));
    }

    [Fact]
    public async Task Local_admission_failure_rolls_back_the_entire_Quest_and_all_owners()
    {
        await using Fixture f = await Fixture.CreateAsync();

        LocalAdmission participant = new() { Refuse = true };

        f.UseLocalAdmission(participant);

        Result<GuildQuestStored> issued = await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default);

        Assert.True(issued.IsFailure);

        Assert.Equal(ErrorCodes.Guild.Unavailable, issued.Error.Code);

        foreach (string table in new[] { "guild_quests", "guild_signets", "Sessions", "session_campaign_bindings",
            "guild_quest_start_intents", "LongRunningOperations", "guild_local_executions", "Apprentices", "fixture_allocations" })
        {
            Assert.Equal(0L, await f.CountAsync(table));
        }
    }

    [Fact]
    public async Task Local_prepared_dispatch_binds_existing_child_without_claiming_a_paid_attempt()
    {
        await using Fixture f = await Fixture.CreateAsync();

        f.UseLocalAdmission(new LocalAdmission());

        GuildQuestStored row = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(row);

        Result<GuildQuestDispatchRecord> prepared = await f.Store.PrepareDispatchAsync(driver, Guid.NewGuid(),
            ReceiptDigest("prepared local input"), row.Quest.RunId, false, default);

        Assert.True(prepared.IsSuccess, prepared.Error.Message);

        Assert.NotNull(prepared.Value.LocalChildId);

        Assert.Equal(0L, await f.CountAsync("fixture_exposures"));

        Result<GuildQuestDispatchRecord> stopped = await f.Store.RecordDispatchEvidenceAsync(driver,
            new(prepared.Value.DispatchId, prepared.Value.Revision, GuildDispatchEvidence.ResolvedNoDispatch,
                prepared.Value.LocalChildId, NoDispatchProven: true), default);

        Assert.True(stopped.IsSuccess, stopped.Error.Message);

        Assert.Equal(prepared.Value.LocalChildId, stopped.Value.LocalChildId);

        row = (await f.Store.GetAsync(f.Caller, row.Quest.Id, default)).Value;

        GuildQuestResult result = new(new(row.Quest.Id, Guid.NewGuid(), ReceiptDigest(""), null,
            QuestState.Rejected, ErrorCodes.Guild.Rejected), null, NoDispatchProven: true);

        Result<GuildResultReceipt> rejected = await f.Store.CommitResultAsync(driver,
            new(Guid.NewGuid(), ReceiptDigest("pre-dispatch refusal")), row.Quest.Revision, result, default);

        Assert.True(rejected.IsSuccess, rejected.Error.Message);

        Assert.Equal(1L, await f.CountAsync("Apprentices"));

        Assert.Equal(1L, await f.CountAsync("guild_result_receipts"));

        Assert.Equal(0L, await f.CountAsync("fixture_exposures"));
    }

    private sealed class LocalAdmission : IGuildLocalExecutionAdmission
    {
        internal int Resolutions;

        internal bool Refuse;

        public Task<Result<GuildLocalExecutionInput>> ResolveWithinAsync(GuildCallerContext caller, GuildQuestStored quest,
            SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
        {
            Resolutions++;

            return Task.FromResult(Refuse
                ? Result<GuildLocalExecutionInput>.Failure(new(ErrorCodes.Guild.Unavailable, "Local execution is unavailable."))
                : Result<GuildLocalExecutionInput>.Success(new(1, GuildLocalExecutionMode.Apprentice,
                    quest.Quest.GoalRef, "accepted-provider", "accepted-model", [], null)));
        }
    }
}
