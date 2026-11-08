using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public async Task Real_driver_preserves_its_authenticated_caller_and_refuses_copied_context()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildCallerContext owner = await f.OperatorAsync();

        Result<GuildQuestStored> issued = await f.IssueAsync(owner);

        Assert.True(issued.IsSuccess, issued.Error.Message);

        GuildQuestDto quest = issued.Value.Quest;

        Result<GuildRecoveryContext> recovery = await f.Policy.CreateRecoveryAsync(owner, quest.Id, quest.OperationId, quest.SignetRevision, default);

        Assert.True(recovery.IsSuccess, recovery.Error.Message);

        Result<GuildExecutionHandle> execution = await f.Authority.RebindAsync(recovery.Value, default);

        Assert.True(execution.IsSuccess, execution.Error.Message);

        Result<GuildCallerContext> run = await f.Policy.CreateRunCallerAsync(f.Authority, execution.Value, Guid.NewGuid(), default);

        Assert.True(run.IsSuccess, run.Error.Message);

        var opened = await f.Connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, default);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        await using var connection = opened.Value;

        await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection.Connection);

        LongRunningOperationStore operations = new(db, f.Connections);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        var leased = await operations.TryAcquireLeaseAsync(quest.OperationId, "real-driver", now, now.AddMinutes(5));

        Assert.True(leased.Acquired);

        GuildQuestDriver driver = new(execution.Value, "real-driver", leased.Operation.Revision, run.Value);

        Guid dispatch = Guid.NewGuid();

        Guid accounting = Guid.NewGuid();

        Result<GuildQuestDispatchRecord> prepared = await f.Store.PrepareDispatchAsync(driver, dispatch, new string('A', 64), accounting, false, default);

        Assert.True(prepared.IsSuccess, prepared.Error.Message);

        GuildCallerContext copied = new(GuildCallerKind.Run, run.Value.PrincipalId!.Value, run.Value.InvocationId,
            run.Value.InstallationGeneration, execution.Value);

        Assert.True((await f.Store.PrepareDispatchAsync(driver with { Caller = copied }, dispatch, new string('A', 64), accounting, false, default)).IsFailure);

        Assert.True((await f.Store.PrepareDispatchAsync(driver with { Caller = null }, dispatch, new string('A', 64), accounting, false, default)).IsFailure);
    }
}
