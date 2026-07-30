namespace RetroDownfall.Arcanum.Tests.Intelligence;

public class TurnPlanTests
{
    [Fact]
    public void TurnPlanTask_Status_Tracks_Progress()
    {
        var task = new RetroDownfall.Arcanum.Core.Intelligence.TurnPlanTask(
            "test-1", "Test description", RetroDownfall.Arcanum.Core.Intelligence.TurnPlanTaskStatus.InProgress,
            null, 0, DateTimeOffset.UtcNow, null);
        Assert.Equal("test-1", task.TaskId);
        Assert.Equal(RetroDownfall.Arcanum.Core.Intelligence.TurnPlanTaskStatus.InProgress, task.Status);
    }

    [Fact]
    public void TurnPlan_Status_Tracks_Plan_Lifecycle()
    {
        var plan = new RetroDownfall.Arcanum.Core.Intelligence.TurnPlan(
            "plan-1", "Plan title", new List<RetroDownfall.Arcanum.Core.Intelligence.TurnPlanTask>(),
            DateTimeOffset.UtcNow, RetroDownfall.Arcanum.Core.Intelligence.TurnPlanStatus.Active);
        Assert.Equal("plan-1", plan.PlanId);
        Assert.Equal(RetroDownfall.Arcanum.Core.Intelligence.TurnPlanStatus.Active, plan.Status);
    }

    [Fact]
    public void TurnPlanTask_Completed_Has_Completion_Time()
    {
        var completedAt = DateTimeOffset.UtcNow;
        var task = new RetroDownfall.Arcanum.Core.Intelligence.TurnPlanTask(
            "test-2", "Done", RetroDownfall.Arcanum.Core.Intelligence.TurnPlanTaskStatus.Completed,
            null, 1, completedAt.AddMinutes(-1), completedAt);
        Assert.NotNull(task.CompletedAt);
    }
}
