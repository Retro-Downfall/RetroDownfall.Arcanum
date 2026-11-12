using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildWorkspaceLeaseBrokerTests
{
    [Fact]
    public async Task Concurrent_brokers_have_one_overlapping_holder_and_loser_can_retry_after_release()
    {
        await using Fixture f = await Fixture.CreateAsync();

        await using GuildWorkspaceLeaseBroker other = new(new(new FixtureOrdinaryConnectionFactory(f.ConnectionString)),
            new WorkspaceLeaseIdentityResolver(), f.Policy, f.Time, f.Host);

        using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(2));

        TaskCompletionSource readyFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource readySecond = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Guid firstKey = Guid.NewGuid(), secondKey = Guid.NewGuid();

        AcquireWorkspaceLeaseRequest firstRequest = new(firstKey, f.Policy.WorkspaceId,
            ImmutableArray.Create(new GuildPathScope("shared.txt", GuildPathScopeKind.File)));

        AcquireWorkspaceLeaseRequest secondRequest = new(secondKey, f.Policy.WorkspaceId,
            ImmutableArray.Create(new GuildPathScope("shared.txt", GuildPathScopeKind.File)));

        GuildMutationIdentity firstMutation = new(firstKey, new string('A', 64));

        GuildMutationIdentity secondMutation = new(secondKey, new string('B', 64));

        async Task<Result<WorkspaceLeaseDto>> Attempt(GuildWorkspaceLeaseBroker broker, GuildMutationIdentity mutation,
            AcquireWorkspaceLeaseRequest request, TaskCompletionSource ready)
        {
            ready.TrySetResult();

            await start.Task.WaitAsync(deadline.Token);

            return await broker.AcquireAsync(f.Policy.Caller, mutation, request, deadline.Token);
        }

        Task<Result<WorkspaceLeaseDto>> firstTask = Task.Run(() => Attempt(f.Broker, firstMutation, firstRequest, readyFirst));

        Task<Result<WorkspaceLeaseDto>> secondTask = Task.Run(() => Attempt(other, secondMutation, secondRequest, readySecond));

        try
        {
            await Task.WhenAll(readyFirst.Task, readySecond.Task).WaitAsync(deadline.Token);
        }
        finally
        {
            start.TrySetResult();
        }

        Result<WorkspaceLeaseDto>[] attempts = await Task.WhenAll(firstTask, secondTask).WaitAsync(deadline.Token);

        WorkspaceLeaseDto winner = Assert.Single(attempts, static result => result.IsSuccess).Value;

        Assert.Equal(ErrorCodes.Guild.LeaseConflict, Assert.Single(attempts, static result => result.IsFailure).Error.Code);

        await using (SqliteConnection connection = await f.OpenAsync(deadline.Token))
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT count(*) FROM guild_workspace_leases WHERE StateCode=1";

            Assert.Equal(1L, await command.ExecuteScalarAsync(deadline.Token));

            command.CommandText = "SELECT count(*) FROM guild_workspace_leases";

            Assert.Equal(1L, await command.ExecuteScalarAsync(deadline.Token));
        }

        int winningIndex = attempts[0].IsSuccess ? 0 : 1;

        GuildWorkspaceLeaseBroker winningBroker = winningIndex == 0 ? f.Broker : other;

        GuildWorkspaceLeaseBroker losingBroker = winningIndex == 0 ? other : f.Broker;

        Guid releaseKey = Guid.NewGuid();

        var released = await winningBroker.ReleaseAsync(f.Policy.Caller, new(releaseKey, new string('C', 64)), winner.Id,
            new(releaseKey, winner.Revision, winner.Epoch), deadline.Token);

        Assert.True(released.IsSuccess, released.Error.Message);

        Result<WorkspaceLeaseDto> retry = await losingBroker.AcquireAsync(f.Policy.Caller,
            winningIndex == 0 ? secondMutation : firstMutation, winningIndex == 0 ? secondRequest : firstRequest, deadline.Token);

        Assert.True(retry.IsSuccess, retry.Error.Message);

        Assert.NotEqual(winner.Id, retry.Value.Id);

        Guid finalKey = Guid.NewGuid();

        Assert.True((await losingBroker.ReleaseAsync(f.Policy.Caller, new(finalKey, new string('D', 64)), retry.Value.Id,
            new(finalKey, retry.Value.Revision, retry.Value.Epoch), deadline.Token)).IsSuccess);
    }
}
