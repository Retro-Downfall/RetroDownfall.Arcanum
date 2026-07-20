using RetroDownfall.Arcanum.Cli.CommandCenter;
using Xunit;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

public sealed class CommandCenterHardModalArbiterTests
{
    [Fact]
    public void ActiveHardModal_NeverPreempted_SubsequentQueued()
    {
        CommandCenterHardModalArbiter arbiter = new();
        List<string> shown = [];

        Assert.True(arbiter.RequestShow(
            CommandCenterHardModalKind.HumanPrompt,
            "p1",
            () => shown.Add("p1")));
        Assert.False(arbiter.RequestShow(
            CommandCenterHardModalKind.WardConfirm,
            "w1",
            () => shown.Add("w1")));

        Assert.Equal(["p1"], shown);
        Assert.True(arbiter.HasActiveHardModal);
        Assert.True(arbiter.HasQueuedHardModal);
        Assert.True(arbiter.IsQueued(CommandCenterHardModalKind.WardConfirm, "w1"));
    }

    [Fact]
    public void Promote_PrefersWard_OverHumanPrompt()
    {
        CommandCenterHardModalArbiter arbiter = new();
        List<string> shown = [];

        Assert.True(arbiter.RequestShow(
            CommandCenterHardModalKind.HumanPrompt,
            "active",
            () => shown.Add("active")));
        _ = arbiter.RequestShow(
            CommandCenterHardModalKind.HumanPrompt,
            "p2",
            () => shown.Add("p2"));
        _ = arbiter.RequestShow(
            CommandCenterHardModalKind.WardConfirm,
            "w1",
            () => shown.Add("w1"));

        Assert.True(arbiter.TryClose(CommandCenterHardModalKind.HumanPrompt, "active"));
        Assert.Equal(["active", "w1"], shown);
        Assert.Equal(CommandCenterHardModalKind.WardConfirm, arbiter.ActiveKind);
    }

    [Fact]
    public void QueuedWard_RemovedBeforeDisplay_DoesNotShow()
    {
        CommandCenterHardModalArbiter arbiter = new();
        List<string> shown = [];

        Assert.True(arbiter.RequestShow(
            CommandCenterHardModalKind.HumanPrompt,
            "p1",
            () => shown.Add("p1")));
        _ = arbiter.RequestShow(
            CommandCenterHardModalKind.WardConfirm,
            "w1",
            () => shown.Add("w1"));

        Assert.True(arbiter.TryRemoveQueued(CommandCenterHardModalKind.WardConfirm, "w1"));
        Assert.True(arbiter.TryClose(CommandCenterHardModalKind.HumanPrompt, "p1"));
        Assert.Equal(["p1"], shown);
        Assert.False(arbiter.HasActiveHardModal);
        Assert.False(arbiter.HasQueuedHardModal);
    }

    [Fact]
    public void BlocksAuxiliary_WhenActiveOrQueued()
    {
        CommandCenterHardModalArbiter arbiter = new();
        Assert.False(arbiter.BlocksAuxiliary);

        _ = arbiter.RequestShow(CommandCenterHardModalKind.HumanPrompt, "p1", () => { });
        Assert.True(arbiter.BlocksAuxiliary);

        _ = arbiter.RequestShow(CommandCenterHardModalKind.WardConfirm, "w1", () => { });
        Assert.True(arbiter.BlocksAuxiliary);

        _ = arbiter.TryClose(CommandCenterHardModalKind.HumanPrompt, "p1");
        Assert.True(arbiter.BlocksAuxiliary); // ward promoted

        _ = arbiter.TryClose(CommandCenterHardModalKind.WardConfirm, "w1");
        Assert.False(arbiter.BlocksAuxiliary);
    }

    [Fact]
    public void TryClose_StaleId_Ignored()
    {
        CommandCenterHardModalArbiter arbiter = new();
        _ = arbiter.RequestShow(CommandCenterHardModalKind.HumanPrompt, "p-b", () => { });
        Assert.False(arbiter.TryClose(CommandCenterHardModalKind.HumanPrompt, "p-a"));
        Assert.True(arbiter.IsActive(CommandCenterHardModalKind.HumanPrompt, "p-b"));
    }
}
