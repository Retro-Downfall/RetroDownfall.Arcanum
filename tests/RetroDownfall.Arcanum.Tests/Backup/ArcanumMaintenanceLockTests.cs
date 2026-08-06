using RetroDownfall.Arcanum.Infrastructure.Backup;

namespace RetroDownfall.Arcanum.Tests.Backup;

public sealed class ArcanumMaintenanceLockTests : IDisposable
{

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "arcanum-maintenance-lock-" + Guid.NewGuid().ToString("N"));

    public ArcanumMaintenanceLockTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {

        if (Directory.Exists(_root))
        {

            Directory.Delete(_root, recursive: true);

        }

    }

    [Fact]
    public void An_uncontended_lock_is_acquired_and_released()
    {

        Assert.False(ArcanumMaintenanceLock.IsHeld(_root));

        using (ArcanumMaintenanceLock? held = ArcanumMaintenanceLock.TryAcquire(_root))
        {

            Assert.NotNull(held);

            Assert.True(ArcanumMaintenanceLock.IsHeld(_root));

        }

        Assert.False(ArcanumMaintenanceLock.IsHeld(_root));

        using ArcanumMaintenanceLock? reacquired = ArcanumMaintenanceLock.TryAcquire(_root);

        Assert.NotNull(reacquired);

    }

    [Fact]
    public void A_second_acquisition_is_refused_while_the_first_is_held()
    {

        using ArcanumMaintenanceLock? first = ArcanumMaintenanceLock.TryAcquire(_root);

        Assert.NotNull(first);

        Assert.Null(ArcanumMaintenanceLock.TryAcquire(_root));

    }

    [Fact]
    public void A_stale_lock_file_left_by_a_dead_process_does_not_block_acquisition()
    {

        File.WriteAllText(ArcanumMaintenanceLock.LockPathFor(_root), "stale");

        using ArcanumMaintenanceLock? acquired = ArcanumMaintenanceLock.TryAcquire(_root);

        Assert.NotNull(acquired);

    }

    [Fact]
    public void The_lock_file_lives_outside_the_directory_it_guards()
    {

        string guarded = Path.Combine(_root, "arcanum");

        Directory.CreateDirectory(guarded);

        using ArcanumMaintenanceLock? acquired = ArcanumMaintenanceLock.TryAcquire(guarded);

        Assert.NotNull(acquired);

        Assert.Equal(
            Path.Combine(_root, ".arcanum-maintenance-arcanum.lock"),
            acquired.Path);

        Assert.Empty(Directory.GetFileSystemEntries(guarded));

    }

    [Fact]
    public void Acquiring_creates_the_owning_directory_when_it_is_absent()
    {

        string absent = Path.Combine(_root, "nested", "arcanum");

        using ArcanumMaintenanceLock? acquired = ArcanumMaintenanceLock.TryAcquire(absent);

        Assert.NotNull(acquired);

        Assert.True(File.Exists(ArcanumMaintenanceLock.LockPathFor(absent)));

    }

    [Fact]
    public void Sibling_installations_under_one_parent_take_independent_locks()
    {

        using ArcanumMaintenanceLock? first = ArcanumMaintenanceLock.TryAcquire(
            Path.Combine(_root, "arcanum"));

        Assert.NotNull(first);

        using ArcanumMaintenanceLock? second = ArcanumMaintenanceLock.TryAcquire(
            Path.Combine(_root, "arcanum-two"));

        Assert.NotNull(second);

    }

    [Fact]
    public void Disposing_twice_is_harmless()
    {

        ArcanumMaintenanceLock? acquired = ArcanumMaintenanceLock.TryAcquire(_root);

        Assert.NotNull(acquired);

        acquired.Dispose();

        acquired.Dispose();

        Assert.False(ArcanumMaintenanceLock.IsHeld(_root));

    }

}
