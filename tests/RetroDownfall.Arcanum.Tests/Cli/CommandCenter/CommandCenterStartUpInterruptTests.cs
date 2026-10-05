using RetroDownfall.Arcanum.Cli.CommandCenter;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

/// <summary>
/// Until the terminal UI owns the console, Ctrl+C is a signal, and the first one asks start-up to unwind.
/// A start-up step that never looks at its token (a credential read waiting on an operating-system
/// approval) would otherwise leave the operator with no way to end the process, so the second one does
/// not get absorbed.
/// </summary>
public sealed class CommandCenterStartUpInterruptTests
{
    [Fact]
    public void The_first_interrupt_cancels_start_up_and_is_absorbed()
    {
        int cancels = 0;
        CommandCenterStartUpInterrupt interrupt = new(() => cancels++);

        bool absorbed = interrupt.Absorb();

        Assert.True(absorbed);
        Assert.Equal(1, cancels);
    }

    [Fact]
    public void A_second_interrupt_is_left_to_end_the_process_and_cancels_nothing_more()
    {
        int cancels = 0;
        CommandCenterStartUpInterrupt interrupt = new(() => cancels++);

        _ = interrupt.Absorb();
        bool second = interrupt.Absorb();
        bool third = interrupt.Absorb();

        Assert.False(second);
        Assert.False(third);
        Assert.Equal(1, cancels);
    }

    [Fact]
    public void An_interrupt_that_lands_after_the_run_finished_is_still_absorbed_without_throwing()
    {
        CommandCenterStartUpInterrupt interrupt = new(
            static () => throw new ObjectDisposedException("run"));

        Assert.True(interrupt.Absorb());
    }
}
