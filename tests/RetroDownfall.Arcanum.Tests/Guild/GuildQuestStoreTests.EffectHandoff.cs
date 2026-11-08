using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task Quest_effect_handoff_sees_committed_admission_and_releases_root_while_retaining_installation_work()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildQuestStored row = (await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).Value;

        GuildQuestDriver driver = await f.DriverAsync(row);

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<Result<int>> running = f.Authority.AdmitQuestEffectAsync<int>(row.Quest.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                Result<GuildGrant> live = await signets.VerifyAsync(driver.Execution, token);

                if (live.IsFailure) return live.Error;

                await using SqliteCommand insert = connection.CreateCommand();

                insert.Transaction = transaction;

                insert.CommandText = "INSERT INTO fixture_exposures VALUES($dispatch,$quest,$accounting)";

                insert.Parameters.AddWithValue("$dispatch", Guid.NewGuid().ToString("D"));

                insert.Parameters.AddWithValue("$quest", row.Quest.Id.ToString("D"));

                insert.Parameters.AddWithValue("$accounting", Guid.NewGuid().ToString("D"));

                _ = await insert.ExecuteNonQueryAsync(token);

                return true;
            }, async token =>
            {
                Assert.Equal(1L, await f.CountAsync("fixture_exposures"));

                entered.TrySetResult();

                await release.Task;

                return await GuildSignetRootGate.RunAsync<int>(f.Gate, row.Quest.RootQuestId,
                    _ => Task.FromResult(Result<int>.Success(42)), default);
            }, default);

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Result<int> root = await GuildSignetRootGate.RunAsync<int>(f.Gate, row.Quest.RootQuestId,
                _ => Task.FromResult(Result<int>.Success(7)), default).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(7, root.Value);

            Assert.False(running.IsCompleted);

            CovenantExclusiveRecoveryOwner owner = new(Guid.NewGuid(), CovenantExclusiveOperation.CovenantReset,
                new CovenantDigest(new byte[32]));

            Result<IGrimoireClosingOwner> begun = f.Gate.BeginOrResumeExclusive(owner);

            Assert.True(begun.IsSuccess, begun.Error.Message);

            await using IGrimoireClosingOwner closing = begun.Value;

            Task<Result> drain = f.Gate.DrainRequestAndWorkAsync(closing, default).AsTask();

            Assert.False(drain.IsCompleted);

            release.TrySetResult();

            Assert.Equal(42, (await running).Value);

            Assert.True((await drain.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.Equal(42, (await running).Value);

        Assert.Equal(0, GuildSignetRootGate.RetainedRootCount(f.Gate));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refused_or_replayed_quest_effect_never_hands_off(bool replay)
    {
        await using Fixture f = await Fixture.CreateAsync();

        int effects = 0;

        Result<int> result = await f.Authority.AdmitQuestEffectAsync<int>(Guid.NewGuid(),
            (_, _, _, _) => Task.FromResult(replay ? Result<bool>.Success(false)
                : Result<bool>.Failure(new Error(ErrorCodes.Guild.Forbidden, "Admission refused."))),
            _ =>
            {
                effects++;

                return Task.FromResult(Result<int>.Success(1));
            }, default);

        Assert.True(result.IsFailure);

        Assert.Equal(replay ? ErrorCodes.Guild.ReconciliationRequired : ErrorCodes.Guild.Forbidden, result.Error.Code);

        Assert.Equal(0, effects);

        Assert.Equal(0, GuildSignetRootGate.RetainedRootCount(f.Gate));
    }
}
