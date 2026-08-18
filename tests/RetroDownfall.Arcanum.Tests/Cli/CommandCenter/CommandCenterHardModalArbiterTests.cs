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
            () => Record(shown, "p1")));
        Assert.False(arbiter.RequestShow(
            CommandCenterHardModalKind.WardConfirm,
            "w1",
            () => Record(shown, "w1")));

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
            () => Record(shown, "active")));
        _ = arbiter.RequestShow(
            CommandCenterHardModalKind.HumanPrompt,
            "p2",
            () => Record(shown, "p2"));
        _ = arbiter.RequestShow(
            CommandCenterHardModalKind.WardConfirm,
            "w1",
            () => Record(shown, "w1"));

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
            () => Record(shown, "p1")));
        _ = arbiter.RequestShow(
            CommandCenterHardModalKind.WardConfirm,
            "w1",
            () => Record(shown, "w1"));

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

        _ = arbiter.RequestShow(CommandCenterHardModalKind.HumanPrompt, "p1", static () => true);
        Assert.True(arbiter.BlocksAuxiliary);

        _ = arbiter.RequestShow(CommandCenterHardModalKind.WardConfirm, "w1", static () => true);
        Assert.True(arbiter.BlocksAuxiliary);

        _ = arbiter.TryClose(CommandCenterHardModalKind.HumanPrompt, "p1");
        Assert.True(arbiter.BlocksAuxiliary); // ward promoted

        _ = arbiter.TryClose(CommandCenterHardModalKind.WardConfirm, "w1");
        Assert.False(arbiter.BlocksAuxiliary);
    }

    /// <summary>
    /// Promotion dequeues an entry and marks it active under the gate, then invokes its show callback
    /// outside it. A ward whose approval task completes in that gap tears itself down first, finds
    /// nothing left in the queue to remove, and never closes the slot it was just handed — so
    /// <c>_active</c> stays set for the rest of the session. That blocks F1/Ctrl+K/Ctrl+O and, worse,
    /// queues every later ward prompt behind a modal nobody can answer. The promoted entry therefore
    /// has to be able to decline the slot it inherited.
    /// </summary>
    [Fact]
    public void A_modal_promoted_after_its_owner_resolved_releases_the_slot()
    {
        CommandCenterHardModalArbiter arbiter = new();
        List<string> shown = [];
        bool wardAlreadyResolved = false;

        _ = arbiter.RequestShow(
            CommandCenterHardModalKind.HumanPrompt,
            "p1",
            () => Record(shown, "p1"));
        _ = arbiter.RequestShow(
            CommandCenterHardModalKind.WardConfirm,
            "w1",
            () =>
            {
                if (wardAlreadyResolved)
                {
                    return false;
                }

                return Record(shown, "w1");
            });
        _ = arbiter.RequestShow(
            CommandCenterHardModalKind.HumanPrompt,
            "p2",
            () => Record(shown, "p2"));

        wardAlreadyResolved = true;

        Assert.True(arbiter.TryClose(CommandCenterHardModalKind.HumanPrompt, "p1"));

        Assert.Equal(["p1", "p2"], shown);
        Assert.True(arbiter.IsActive(CommandCenterHardModalKind.HumanPrompt, "p2"));
    }

    [Fact]
    public void A_declined_promotion_with_nothing_left_queued_leaves_the_arbiter_idle()
    {
        CommandCenterHardModalArbiter arbiter = new();

        _ = arbiter.RequestShow(CommandCenterHardModalKind.HumanPrompt, "p1", static () => true);
        _ = arbiter.RequestShow(CommandCenterHardModalKind.WardConfirm, "w1", static () => false);

        Assert.True(arbiter.TryClose(CommandCenterHardModalKind.HumanPrompt, "p1"));

        Assert.False(arbiter.HasActiveHardModal);
        Assert.False(arbiter.BlocksAuxiliary);
    }

    [Fact]
    public void TryClose_StaleId_Ignored()
    {
        CommandCenterHardModalArbiter arbiter = new();
        _ = arbiter.RequestShow(CommandCenterHardModalKind.HumanPrompt, "p-b", static () => true);
        Assert.False(arbiter.TryClose(CommandCenterHardModalKind.HumanPrompt, "p-a"));
        Assert.True(arbiter.IsActive(CommandCenterHardModalKind.HumanPrompt, "p-b"));
    }

    /// <summary>A show callback that takes the slot records itself and claims it.</summary>
    private static bool Record(List<string> shown, string id)
    {
        shown.Add(id);
        return true;
    }
}
