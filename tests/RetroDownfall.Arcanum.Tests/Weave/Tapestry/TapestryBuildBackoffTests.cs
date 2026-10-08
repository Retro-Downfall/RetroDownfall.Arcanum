using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Infrastructure.Weave;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry;

/// <summary>
/// The record of failed Tapestry builds (DESIGN §21.11): a build that keeps failing waits twice as long
/// each time, and any change to what is being built starts it over.
/// </summary>
public sealed class TapestryBuildBackoffTests
{
    private static readonly TapestryScope Scope = new(TapestryScopeKind.Workspace, "/repo");

    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private static readonly DateTimeOffset Start = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(5, 16)]
    [InlineData(6, 24)]
    [InlineData(40, 24)]
    public void WaitAfter_doubles_with_each_consecutive_failure_up_to_a_day(int failures, int expectedHours) =>
        Assert.Equal(TimeSpan.FromHours(expectedHours), TapestryBuildBackoff.WaitAfter(failures, Interval));

    [Fact]
    public void WaitAfter_never_drops_below_the_sweep_interval_even_when_it_is_longer_than_a_day() =>
        Assert.Equal(
            TimeSpan.FromHours(48),
            TapestryBuildBackoff.WaitAfter(5, TimeSpan.FromHours(48)));

    [Fact]
    public void A_failed_build_is_backing_off_until_its_wait_has_passed()
    {
        TapestryBuildBackoff backoff = new();

        DateTimeOffset retryAfter = backoff.RecordFailure(Scope, "build-1", Start, Interval);

        Assert.Equal(Start + Interval, retryAfter);

        Assert.True(backoff.IsBackingOff(Scope, "build-1", Start));

        Assert.True(backoff.IsBackingOff(Scope, "build-1", retryAfter - TimeSpan.FromTicks(1)));

        Assert.False(backoff.IsBackingOff(Scope, "build-1", retryAfter));
    }

    [Fact]
    public void Consecutive_failures_of_one_build_lengthen_the_wait()
    {
        TapestryBuildBackoff backoff = new();

        DateTimeOffset first = backoff.RecordFailure(Scope, "build-1", Start, Interval);

        DateTimeOffset second = backoff.RecordFailure(Scope, "build-1", first, Interval);

        DateTimeOffset third = backoff.RecordFailure(Scope, "build-1", second, Interval);

        Assert.Equal(Interval, first - Start);

        Assert.Equal(2 * Interval, second - first);

        Assert.Equal(4 * Interval, third - second);
    }

    [Fact]
    public void A_different_build_is_not_held_back_and_starts_the_count_over()
    {
        TapestryBuildBackoff backoff = new();

        _ = backoff.RecordFailure(Scope, "build-1", Start, Interval);

        _ = backoff.RecordFailure(Scope, "build-1", Start, Interval);

        Assert.False(backoff.IsBackingOff(Scope, "build-2", Start));

        DateTimeOffset retryAfter = backoff.RecordFailure(Scope, "build-2", Start, Interval);

        Assert.Equal(Start + Interval, retryAfter);
    }

    [Fact]
    public void A_failure_is_per_scope()
    {
        TapestryBuildBackoff backoff = new();

        _ = backoff.RecordFailure(Scope, "build-1", Start, Interval);

        Assert.False(backoff.IsBackingOff(new TapestryScope(TapestryScopeKind.Session, "S"), "build-1", Start));
    }

    [Fact]
    public void Clear_forgets_every_scope()
    {
        TapestryBuildBackoff backoff = new();

        TapestryScope other = new(TapestryScopeKind.Session, "S");

        _ = backoff.RecordFailure(Scope, "build-1", Start, Interval);

        _ = backoff.RecordFailure(other, "build-2", Start, Interval);

        backoff.Clear();

        Assert.False(backoff.IsBackingOff(Scope, "build-1", Start));

        Assert.False(backoff.IsBackingOff(other, "build-2", Start));

        Assert.Equal(Start + Interval, backoff.RecordFailure(Scope, "build-1", Start, Interval));
    }

    [Fact]
    public void RetainOnly_forgets_the_scopes_that_no_longer_exist_and_keeps_the_rest()
    {
        TapestryBuildBackoff backoff = new();

        TapestryScope deleted = new(TapestryScopeKind.Session, "GONE");

        _ = backoff.RecordFailure(Scope, "build-1", Start, Interval);

        _ = backoff.RecordFailure(deleted, "build-2", Start, Interval);

        backoff.RetainOnly([Scope]);

        Assert.True(backoff.IsBackingOff(Scope, "build-1", Start));

        Assert.False(backoff.IsBackingOff(deleted, "build-2", Start));
    }

    [Fact]
    public void A_published_scope_forgets_its_failures()
    {
        TapestryBuildBackoff backoff = new();

        _ = backoff.RecordFailure(Scope, "build-1", Start, Interval);

        backoff.RecordSuccess(Scope);

        Assert.False(backoff.IsBackingOff(Scope, "build-1", Start));

        Assert.Equal(Start + Interval, backoff.RecordFailure(Scope, "build-1", Start, Interval));
    }
}
