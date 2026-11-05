using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildSignetCleanupBoundaryTests
{
    [Fact]
    public async Task Transactional_seal_refuses_unprepared_revocation_during_admitted_effect()
    {
        await using GuildSignetAuthorityTests.Fixture f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        Guid campaign = Guid.NewGuid();

        Guid owner = Guid.NewGuid();

        await f.SeedOwnersAsync(campaign, owner, Guid.NewGuid());

        f.Policy.NextOwners = new(owner, campaign, null);

        GuildExecutionHandle execution = await f.RootAsync();

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<Result<int>> effect = f.Authority.AdmitToolAsync(execution, GuildSignetAuthorityTests.Fixture.Tool, true,
            async (_, _) =>
            {
                entered.SetResult();

                await release.Task;

                return Result<int>.Success(1);
            }, default);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        try
        {
            await using SqliteConnection connection = await f.OpenAsync();

            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

            IGuildOwnerCleanupParticipant participant = new GuildSignetCleanupParticipant(f.Gate);

            Result sealedOwner = await participant.SealAsync(new(2, owner, null), connection, transaction, default);

            Assert.True(sealedOwner.IsFailure);

            Assert.Equal(ErrorCodes.Guild.Unavailable, sealedOwner.Error.Code);

            await transaction.CommitAsync();

            Assert.Equal(0L, await f.ScalarAsync("SELECT Revoked FROM guild_signets"));
        }
        finally
        {
            release.TrySetResult();

            Assert.True((await effect).IsSuccess);
        }
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Owner_preparation_waits_for_admitted_effect_without_holding_a_database_transaction(int rootCount)
    {
        await using GuildSignetAuthorityTests.Fixture f = await GuildSignetAuthorityTests.Fixture.CreateAsync();

        Guid campaign = Guid.NewGuid();

        Guid owner = Guid.NewGuid();

        await f.SeedOwnersAsync(campaign, owner, Guid.NewGuid());

        f.Policy.NextOwners = new(owner, campaign, null);

        GuildExecutionHandle first = await f.RootAsync();

        GuildExecutionHandle? second = rootCount == 2 ? await f.RootAsync() : null;

        GuildExecutionHandle[] roots = second is null ? [first] : [first, second];

        GuildExecutionHandle execution = roots.OrderBy(root => root.RootCommissionId.ToString("D"), StringComparer.Ordinal).First();

        GuildExecutionHandle? independent = roots.FirstOrDefault(root => !ReferenceEquals(root, execution));

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<Result<int>> effect = f.Authority.AdmitToolAsync(execution, GuildSignetAuthorityTests.Fixture.Tool, true,
            async (_, _) =>
            {
                entered.SetResult();

                await release.Task;

                return Result<int>.Success(1);
            }, default);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await using SqliteConnection cleanupConnection = await f.OpenAsync();

        IGuildOwnerCleanupParticipant participant = new GuildSignetCleanupParticipant(f.Gate);

        GuildCleanupOwner cleanupOwner = new(2, owner, null);

        Task<Result> preparing = participant.PrepareSealAsync(cleanupOwner, cleanupConnection, default).AsTask();

        try
        {
            Assert.False(preparing.IsCompleted);

            Assert.Equal(0L, await f.ScalarAsync("SELECT COUNT(*) FROM guild_signets WHERE Revoked=1"));

            if (independent is not null)
            {
                Assert.True((await f.Authority.VerifyToolAsync(independent, GuildSignetAuthorityTests.Fixture.Tool, true, default)).IsSuccess);
            }

            await f.ExecuteAsync("CREATE TABLE cleanup_waiting_probe (Id INTEGER)");
        }
        finally
        {
            release.TrySetResult();

            Assert.True((await effect).IsSuccess);
        }

        Assert.True((await preparing.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);

        Assert.Equal((long)rootCount, await f.ScalarAsync("SELECT COUNT(*) FROM guild_signets WHERE Revoked=1"));

        await using SqliteTransaction transaction = cleanupConnection.BeginTransaction(deferred: false);

        Assert.True((await participant.SealAsync(cleanupOwner, cleanupConnection, transaction, default)).IsSuccess);

        await transaction.CommitAsync();
    }
}
