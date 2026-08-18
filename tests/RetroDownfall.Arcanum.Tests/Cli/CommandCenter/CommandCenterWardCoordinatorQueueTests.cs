using RetroDownfall.Arcanum.Cli.CommandCenter;
using Xunit;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

public sealed class CommandCenterWardCoordinatorQueueTests
{
    [Fact]
    public async Task SecondWard_Queues_DoesNotDenyFirst()
    {
        CommandCenterHardModalArbiter arbiter = new();
        CommandCenterWardCoordinator coordinator = new(arbiter);
        List<string> shown = [];
        coordinator.SetUiCallbacks(r => shown.Add(r.WardId), () => { });

        Task<WardApprovalDecision> first = coordinator.RequestApprovalAsync(
            new WardApprovalRequest("ward-1", "execute_command", "{}"),
            CancellationToken.None);
        await Task.Yield();

        Task<WardApprovalDecision> second = coordinator.RequestApprovalAsync(
            new WardApprovalRequest("ward-2", "write_file", "{}"),
            CancellationToken.None);
        await Task.Yield();

        Assert.Equal(["ward-1"], shown);
        Assert.True(arbiter.IsQueued(CommandCenterHardModalKind.WardConfirm, "ward-2"));
        Assert.False(first.IsCompleted);

        Assert.True(coordinator.TryCompletePending(WardApprovalDecision.Allow));
        Assert.Equal(WardApprovalDecision.Allow, await first);

        await Task.Yield();
        Assert.Contains("ward-2", shown);
        Assert.True(coordinator.TryCompletePending(WardApprovalDecision.Deny));
        Assert.Equal(WardApprovalDecision.Deny, await second);
    }

    [Fact]
    public async Task QueuedWard_TimeoutBeforeDisplay_RemovesWithoutShow()
    {
        CommandCenterHardModalArbiter arbiter = new();
        CommandCenterWardCoordinator coordinator = new(arbiter);
        List<string> shown = [];
        coordinator.SetUiCallbacks(r => shown.Add(r.WardId), () => { });

        using CancellationTokenSource cts = new();
        Task<WardApprovalDecision> humanHold = default!;

        // Occupy slot with a HumanPrompt-shaped active via arbiter directly, then queue ward.
        Assert.True(arbiter.RequestShow(CommandCenterHardModalKind.HumanPrompt, "p1", static () => true));

        Task<WardApprovalDecision> ward = coordinator.RequestApprovalAsync(
            new WardApprovalRequest("ward-q", "execute_command", "{}"),
            cts.Token);
        await Task.Yield();

        Assert.Equal([], shown);
        Assert.True(arbiter.IsQueued(CommandCenterHardModalKind.WardConfirm, "ward-q"));

        cts.Cancel();
        Assert.Equal(WardApprovalDecision.Deny, await ward);
        Assert.False(arbiter.IsQueued(CommandCenterHardModalKind.WardConfirm, "ward-q"));
        Assert.Equal([], shown);

        _ = humanHold;
    }

    [Fact]
    public async Task TryCompleteByWardId_CorrelatesStaleClose()
    {
        CommandCenterHardModalArbiter arbiter = new();
        CommandCenterWardCoordinator coordinator = new(arbiter);
        coordinator.SetUiCallbacks(_ => { }, () => { });

        Task<WardApprovalDecision> pending = coordinator.RequestApprovalAsync(
            new WardApprovalRequest("ward-1", "execute_command", "{}"),
            CancellationToken.None);
        await Task.Yield();

        Assert.False(coordinator.TryCompleteByWardId("ward-other", WardApprovalDecision.Allow));
        Assert.False(pending.IsCompleted);
        Assert.True(coordinator.TryCompleteByWardId("ward-1", WardApprovalDecision.Deny));
        Assert.Equal(WardApprovalDecision.Deny, await pending);
    }
}
