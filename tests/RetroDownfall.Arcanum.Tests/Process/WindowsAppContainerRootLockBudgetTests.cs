using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

namespace RetroDownfall.Arcanum.Tests.Process;

/// <summary>
/// The broker serialises each root's DACL read-modify-write under a per-root named mutex. Each wait
/// used to carry its own 30-second timeout, so a run with several contended roots — and a grant phase
/// followed by a removal phase — could block for a multiple of that. Every wait in one broker run, or
/// one host replay, now draws on one shared deadline. The mutex is Windows-only; the budget it waits
/// through is pinned here with a fake clock and a fake wait.
/// </summary>
public sealed class WindowsAppContainerRootLockBudgetTests
{
    [Fact]
    public void Contended_roots_share_one_deadline_instead_of_stacking_their_waits()
    {
        DateTime now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
        WindowsAppContainerRootLockBudget budget = new(TimeSpan.FromSeconds(30), () => now);
        List<TimeSpan> offered = [];

        // Four grants and four removals, each on a root another run never releases: every wait burns
        // the whole timeout it is offered.
        for (int attempt = 0; attempt < 8; attempt++)
        {
            bool acquired = budget.TryAcquire(timeout =>
            {
                offered.Add(timeout);
                now += timeout;
                return false;
            });

            Assert.False(acquired);
        }

        Assert.Equal(TimeSpan.FromSeconds(30), offered.Aggregate(TimeSpan.Zero, static (total, wait) => total + wait));
        Assert.Equal(TimeSpan.FromSeconds(30), offered[0]);
        Assert.All(offered.Skip(1), static wait => Assert.Equal(TimeSpan.Zero, wait));
    }

    [Fact]
    public void Each_wait_is_offered_only_what_is_left_and_never_a_negative_timeout()
    {
        DateTime now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
        WindowsAppContainerRootLockBudget budget = new(TimeSpan.FromSeconds(30), () => now);

        now += TimeSpan.FromSeconds(20);
        TimeSpan second = TimeSpan.MinValue;
        Assert.True(budget.TryAcquire(timeout =>
        {
            second = timeout;
            return true;
        }));

        now += TimeSpan.FromSeconds(45);
        TimeSpan third = TimeSpan.MinValue;

        // A spent budget still tries the lock once without blocking: an uncontended root is released
        // even after an earlier root used up the time.
        Assert.True(budget.TryAcquire(timeout =>
        {
            third = timeout;
            return true;
        }));

        Assert.Equal(TimeSpan.FromSeconds(10), second);
        Assert.Equal(TimeSpan.Zero, third);
    }

    [Fact]
    public void The_broker_and_the_host_replay_each_get_thirty_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), WindowsAppContainerRootLockBudget.PerRun);
    }
}
