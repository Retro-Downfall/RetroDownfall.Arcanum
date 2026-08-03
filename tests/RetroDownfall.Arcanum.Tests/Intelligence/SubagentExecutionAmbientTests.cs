using RetroDownfall.Arcanum.Api.Intelligence.Subagents;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class SubagentExecutionAmbientTests
{
    [Fact]
    public void EnterChild_ExposesIsolationAndRestoresParent()
    {
        DelegatedManaTracker tracker = new(1_000, null);

        Assert.False(SubagentExecutionAmbient.IsIsolated);

        using (SubagentExecutionAmbient.EnterChild(tracker))
        {
            Assert.True(SubagentExecutionAmbient.IsIsolated);
            Assert.Same(tracker, SubagentExecutionAmbient.Tracker);
        }

        Assert.False(SubagentExecutionAmbient.IsIsolated);
        Assert.Null(SubagentExecutionAmbient.Tracker);
    }

    [Fact]
    public void EnterChild_NestedScope_UsesChildTrackerAndRestoresParentTracker()
    {
        DelegatedManaTracker parentTracker = new(1_000, null);
        DelegatedManaTracker childTracker = new(500, null);

        using (SubagentExecutionAmbient.EnterChild(parentTracker))
        {
            using (SubagentExecutionAmbient.EnterChild(childTracker))
            {

                Assert.True(SubagentExecutionAmbient.IsIsolated);

                Assert.Same(childTracker, SubagentExecutionAmbient.Tracker);

            }

            Assert.Same(parentTracker, SubagentExecutionAmbient.Tracker);
        }
    }
}
