using System.Security.Cryptography;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Concurrent_award_and_offer_withdrawal_commit_once_and_replay_the_winner()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var setup = await OperatorAwardFixtureAsync(f);

        GuildRevisionRequest request = new(Guid.NewGuid(), setup.Request.ExpectedRevision);

        string digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, GuildJsonContext.Default.GuildRevisionRequest)));

        GuildMutationIdentity mutation = new(request.IdempotencyKey, digest);

        long quests = await f.ScalarAsync("SELECT count(*) FROM guild_quests");

        long signets = await f.ScalarAsync("SELECT count(*) FROM guild_signets");

        long accounts = await f.ScalarAsync("SELECT count(*) FROM guild_budget_accounts");

        long associations = await f.ScalarAsync("SELECT count(*) FROM guild_cohort_member_quests WHERE AssociationKindCode=2");

        TaskCompletionSource<bool> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource<bool> awardReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource<bool> withdrawReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<Result<WorkAwardDto>> Award()
        {
            awardReady.SetResult(true);

            await gate.Task;

            return await setup.Store.AwardAsync(setup.Owner, setup.Mutation, setup.Offer.Id, setup.Request, default);
        }

        async Task<Result<WorkOfferDto>> Withdraw()
        {
            withdrawReady.SetResult(true);

            await gate.Task;

            return await setup.Store.WithdrawOfferAsync(setup.IssuerCaller, mutation, setup.Offer.Id, request, default);
        }

        Task<Result<WorkAwardDto>> awardTask = Award();

        Task<Result<WorkOfferDto>> withdrawTask = Withdraw();

        await Task.WhenAll(awardReady.Task, withdrawReady.Task);

        gate.SetResult(true);

        await Task.WhenAll(awardTask, withdrawTask);

        var award = await awardTask;

        var withdrawn = await withdrawTask;

        Assert.NotEqual(award.IsSuccess, withdrawn.IsSuccess);

        long delta = award.IsSuccess ? 1 : 0;

        Assert.Equal(quests + delta, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));

        Assert.Equal(signets + delta, await f.ScalarAsync("SELECT count(*) FROM guild_signets"));

        Assert.Equal(accounts + delta, await f.ScalarAsync("SELECT count(*) FROM guild_budget_accounts"));

        Assert.Equal(associations + delta, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_member_quests WHERE AssociationKindCode=2"));

        Assert.Equal(delta, await f.ScalarAsync("SELECT count(*) FROM guild_work_awards"));

        Assert.Equal(delta, await f.ScalarAsync("SELECT count(*) FROM guild_distribution_notifications WHERE TypeCode=5"));

        Assert.Equal(withdrawn.IsSuccess ? 1L : 0L, await f.ScalarAsync("SELECT count(*) FROM guild_distribution_notifications WHERE TypeCode=4"));

        if (award.IsSuccess)
        {
            var replay = await setup.Store.AwardAsync(setup.Owner, setup.Mutation, setup.Offer.Id, setup.Request, default);

            Assert.True(replay.IsSuccess, replay.Error.Message);

            Assert.Equal(award.Value.QuestId, replay.Value.QuestId);

            Assert.Equal(award.Value.Id, replay.Value.Id);

            Assert.Equal(award.Value.Receipt!.Id, replay.Value.Receipt!.Id);

            Assert.True(replay.Value.Receipt.Replayed);
        }
        else
        {
            var replay = await setup.Store.WithdrawOfferAsync(setup.IssuerCaller, mutation, setup.Offer.Id, request, default);

            Assert.True(replay.IsSuccess, replay.Error.Message);

            Assert.Equal(withdrawn.Value.Id, replay.Value.Id);

            Assert.Equal(withdrawn.Value.Receipt!.Id, replay.Value.Receipt!.Id);

            Assert.True(replay.Value.Receipt.Replayed);
        }

        Assert.Equal(delta, await f.ScalarAsync("SELECT count(*) FROM guild_distribution_notifications WHERE TypeCode=5"));
    }

    [Fact]
    public async Task Concurrent_award_and_member_removal_commit_once_and_replay_the_winner()
    {
        await using Fixture f = await Fixture.CreateAsync();

        var setup = await OperatorAwardFixtureAsync(f);

        GuildRevisionRequest request = new(Guid.NewGuid(), setup.Request.ExpectedCohortRevision);

        long quests = await f.ScalarAsync("SELECT count(*) FROM guild_quests");

        long signets = await f.ScalarAsync("SELECT count(*) FROM guild_signets");

        long accounts = await f.ScalarAsync("SELECT count(*) FROM guild_budget_accounts");

        long associations = await f.ScalarAsync("SELECT count(*) FROM guild_cohort_member_quests WHERE AssociationKindCode=2");

        TaskCompletionSource<bool> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource<bool> awardReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource<bool> removeReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<Result<WorkAwardDto>> Award()
        {
            awardReady.SetResult(true);

            await gate.Task;

            return await setup.Store.AwardAsync(setup.Owner, setup.Mutation, setup.Offer.Id, setup.Request, default);
        }

        async Task<Result<CohortMemberDto>> Remove()
        {
            removeReady.SetResult(true);

            await gate.Task;

            return await setup.Store.RemoveAsync(setup.IssuerCaller, setup.Offer.CohortId, setup.Bid.MemberId, request, default);
        }

        Task<Result<WorkAwardDto>> awardTask = Award();

        Task<Result<CohortMemberDto>> removeTask = Remove();

        await Task.WhenAll(awardReady.Task, removeReady.Task);

        gate.SetResult(true);

        await Task.WhenAll(awardTask, removeTask);

        var award = await awardTask;

        var removed = await removeTask;

        Assert.NotEqual(award.IsSuccess, removed.IsSuccess);

        long delta = award.IsSuccess ? 1 : 0;

        Assert.Equal(quests + delta, await f.ScalarAsync("SELECT count(*) FROM guild_quests"));

        Assert.Equal(signets + delta, await f.ScalarAsync("SELECT count(*) FROM guild_signets"));

        Assert.Equal(accounts + delta, await f.ScalarAsync("SELECT count(*) FROM guild_budget_accounts"));

        Assert.Equal(associations + delta, await f.ScalarAsync("SELECT count(*) FROM guild_cohort_member_quests WHERE AssociationKindCode=2"));

        Assert.Equal(delta, await f.ScalarAsync("SELECT count(*) FROM guild_work_awards"));

        Assert.Equal(delta, await f.ScalarAsync("SELECT count(*) FROM guild_distribution_notifications WHERE TypeCode=5"));

        if (award.IsSuccess)
        {
            var replay = await setup.Store.AwardAsync(setup.Owner, setup.Mutation, setup.Offer.Id, setup.Request, default);

            Assert.True(replay.IsSuccess, replay.Error.Message);

            Assert.Equal(award.Value.Id, replay.Value.Id);

            Assert.Equal(award.Value.QuestId, replay.Value.QuestId);

            Assert.Equal(award.Value.Receipt!.Id, replay.Value.Receipt!.Id);

            Assert.True(replay.Value.Receipt.Replayed);
        }
        else
        {
            var replay = await setup.Store.RemoveAsync(setup.IssuerCaller, setup.Offer.CohortId, setup.Bid.MemberId, request, default);

            Assert.True(replay.IsSuccess, replay.Error.Message);

            Assert.Equal(removed.Value.Id, replay.Value.Id);

            Assert.Equal(removed.Value.Receipt!.Id, replay.Value.Receipt!.Id);

            Assert.True(replay.Value.Receipt.Replayed);
        }

        Assert.Equal(delta, await f.ScalarAsync("SELECT count(*) FROM guild_distribution_notifications WHERE TypeCode=5"));
    }
}
