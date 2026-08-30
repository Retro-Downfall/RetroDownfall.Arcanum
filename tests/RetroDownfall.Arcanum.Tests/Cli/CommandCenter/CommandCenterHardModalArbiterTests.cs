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
            CommandCenterHardModalKind.HumanPrompt,
            "p2",
            () => Record(shown, "p2")));

        Assert.Equal(["p1"], shown);
        Assert.True(arbiter.HasActiveHardModal);
        Assert.True(arbiter.HasQueuedHardModal);
        Assert.True(arbiter.IsQueued(CommandCenterHardModalKind.HumanPrompt, "p2"));
    }

    [Fact]
    public void Promote_uses_arrival_order_for_human_prompts()
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
            CommandCenterHardModalKind.HumanPrompt,
            "p3",
            () => Record(shown, "p3"));

        Assert.True(arbiter.TryClose(CommandCenterHardModalKind.HumanPrompt, "active"));
        Assert.Equal(["active", "p2"], shown);
        Assert.Equal(CommandCenterHardModalKind.HumanPrompt, arbiter.ActiveKind);
    }

    [Fact]
    public void Queued_prompt_removed_before_display_does_not_show()
    {
        CommandCenterHardModalArbiter arbiter = new();
        List<string> shown = [];

        Assert.True(arbiter.RequestShow(
            CommandCenterHardModalKind.HumanPrompt,
            "p1",
            () => Record(shown, "p1")));
        _ = arbiter.RequestShow(
            CommandCenterHardModalKind.HumanPrompt,
            "p2",
            () => Record(shown, "p2"));

        Assert.True(arbiter.TryRemoveQueued(CommandCenterHardModalKind.HumanPrompt, "p2"));
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

        _ = arbiter.RequestShow(CommandCenterHardModalKind.HumanPrompt, "p2", static () => true);
        Assert.True(arbiter.BlocksAuxiliary);

        _ = arbiter.TryClose(CommandCenterHardModalKind.HumanPrompt, "p1");
        Assert.True(arbiter.BlocksAuxiliary); // queued prompt promoted

        _ = arbiter.TryClose(CommandCenterHardModalKind.HumanPrompt, "p2");
        Assert.False(arbiter.BlocksAuxiliary);
    }

    /// <summary>
    /// Promotion dequeues an entry and marks it active under the gate, then invokes its show callback
    /// outside it. A prompt whose owner completes in that gap must be able to decline the slot it
    /// inherited, otherwise <c>_active</c> stays set with no visible modal and blocks every later
    /// prompt and auxiliary overlay.
    /// </summary>
    [Fact]
    public void A_modal_promoted_after_its_owner_resolved_releases_the_slot()
    {
        CommandCenterHardModalArbiter arbiter = new();
        List<string> shown = [];
        bool secondPromptAlreadyResolved = false;

        _ = arbiter.RequestShow(
            CommandCenterHardModalKind.HumanPrompt,
            "p1",
            () => Record(shown, "p1"));
        _ = arbiter.RequestShow(
            CommandCenterHardModalKind.HumanPrompt,
            "p2",
            () =>
            {
                if (secondPromptAlreadyResolved)
                {
                    return false;
                }

                return Record(shown, "p2");
            });
        _ = arbiter.RequestShow(
            CommandCenterHardModalKind.HumanPrompt,
            "p3",
            () => Record(shown, "p3"));

        secondPromptAlreadyResolved = true;

        Assert.True(arbiter.TryClose(CommandCenterHardModalKind.HumanPrompt, "p1"));

        Assert.Equal(["p1", "p3"], shown);
        Assert.True(arbiter.IsActive(CommandCenterHardModalKind.HumanPrompt, "p3"));
    }

    [Fact]
    public void A_declined_promotion_with_nothing_left_queued_leaves_the_arbiter_idle()
    {
        CommandCenterHardModalArbiter arbiter = new();

        _ = arbiter.RequestShow(CommandCenterHardModalKind.HumanPrompt, "p1", static () => true);
        _ = arbiter.RequestShow(CommandCenterHardModalKind.HumanPrompt, "p2", static () => false);

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
