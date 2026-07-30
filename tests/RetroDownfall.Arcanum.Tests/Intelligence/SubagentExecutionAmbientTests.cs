using RetroDownfall.Arcanum.Api.Intelligence.Subagents;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class SubagentExecutionAmbientTests
{
    [Fact]
    public void EnterChild_ExposesIsolatedDepthAndRestoresParent()
    {
        DelegatedManaTracker tracker = new(1_000, null, 2);

        Assert.Equal(0, SubagentExecutionAmbient.Depth);
        Assert.True(SubagentExecutionAmbient.CanDelegate);

        using (SubagentExecutionAmbient.EnterChild(tracker))
        {
            Assert.Equal(1, SubagentExecutionAmbient.Depth);
            Assert.False(SubagentExecutionAmbient.CanDelegate);
            Assert.True(SubagentExecutionAmbient.IsIsolated);
            Assert.Same(tracker, SubagentExecutionAmbient.Tracker);
        }

        Assert.Equal(0, SubagentExecutionAmbient.Depth);
        Assert.True(SubagentExecutionAmbient.CanDelegate);
        Assert.False(SubagentExecutionAmbient.IsIsolated);
        Assert.Null(SubagentExecutionAmbient.Tracker);
    }

    [Fact]
    public void EnterChild_AtMaximumDepth_RejectsRecursion()
    {
        DelegatedManaTracker parentTracker = new(1_000, null, 2);
        DelegatedManaTracker childTracker = new(500, null, 1);

        using (SubagentExecutionAmbient.EnterChild(parentTracker))
        {
            SubagentDepthExceededException exception = Assert.Throws<SubagentDepthExceededException>(
                () => SubagentExecutionAmbient.EnterChild(childTracker));

            Assert.Equal(SubagentExecutionAmbient.MaxSubagentDepth, exception.MaxDepth);
        }
    }
}
