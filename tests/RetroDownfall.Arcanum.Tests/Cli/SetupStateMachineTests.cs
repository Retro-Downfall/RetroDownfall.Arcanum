using RetroDownfall.Arcanum.Cli.Services.Setup;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// Issue #19 — the wizard is an explicit, resumable state machine, not an implicit sequence of
/// prompts. The traversal is asserted directly so the ordering and resume contract cannot drift.
/// </summary>
public sealed class SetupStateMachineTests
{

    [Fact]
    public void The_documented_step_order_is_the_implemented_order()
    {

        Assert.Equal(
            [
                SetupStep.Edition,
                SetupStep.Provider,
                SetupStep.ProviderCredential,
                SetupStep.WebResearchCredential,
                SetupStep.Connectivity,
                SetupStep.Workspace,
                SetupStep.Preset,
                SetupStep.Review,
                SetupStep.Commit,
                SetupStep.Done,
            ],
            SetupStateMachine.Order);

    }

    [Fact]
    public void A_new_machine_starts_at_the_first_step()
    {

        SetupStateMachine machine = new();

        Assert.Equal(SetupStep.Edition, machine.Current);

        Assert.False(machine.IsComplete);

    }

    [Fact]
    public void Advancing_walks_every_step_once_and_stops_at_done()
    {

        SetupStateMachine machine = new();

        List<SetupStep> visited = [machine.Current];

        while (machine.MoveNext())
        {

            visited.Add(machine.Current);

        }

        Assert.Equal(SetupStateMachine.Order, visited);

        Assert.True(machine.IsComplete);

        Assert.False(machine.MoveNext());

    }

    [Fact]
    public void Going_back_from_the_first_step_is_a_no_op()
    {

        SetupStateMachine machine = new();

        Assert.False(machine.MoveBack());

        Assert.Equal(SetupStep.Edition, machine.Current);

    }

    [Fact]
    public void Back_returns_to_the_previous_step()
    {

        SetupStateMachine machine = new();

        _ = machine.MoveNext();

        _ = machine.MoveNext();

        Assert.Equal(SetupStep.ProviderCredential, machine.Current);

        Assert.True(machine.MoveBack());

        Assert.Equal(SetupStep.Provider, machine.Current);

    }

    [Fact]
    public void A_run_can_resume_at_an_explicit_step()
    {

        SetupStateMachine machine = new(SetupStep.Connectivity);

        Assert.Equal(SetupStep.Connectivity, machine.Current);

        machine.Reenter(SetupStep.Preset);

        Assert.Equal(SetupStep.Preset, machine.Current);

    }

    [Fact]
    public void Reentering_the_current_step_does_not_advance()
    {

        SetupStateMachine machine = new(SetupStep.Workspace);

        machine.Reenter(machine.Current);

        Assert.Equal(SetupStep.Workspace, machine.Current);

    }

    [Fact]
    public void An_unknown_step_is_rejected_rather_than_silently_reset()
    {

        SetupStateMachine machine = new();

        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => machine.Reenter((SetupStep)999));

        Assert.Equal(SetupStep.Edition, machine.Current);

    }

}
