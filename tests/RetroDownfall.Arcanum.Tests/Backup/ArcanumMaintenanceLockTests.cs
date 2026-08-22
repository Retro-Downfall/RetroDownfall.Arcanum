using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Backup;

[Collection("WorkspacePathPolicy")]
public sealed class ArcanumMaintenanceLockTests : IDisposable
{

    private readonly string _container;

    private readonly string _root;

    public ArcanumMaintenanceLockTests()
    {

        _container = Path.Combine(
            Path.GetTempPath(),
            "arcanum-maintenance-lock-" + Guid.NewGuid().ToString("N"));

        _root = Path.Combine(_container, "guarded");

        Directory.CreateDirectory(_root);

    }

    public void Dispose()
    {

        SecureFilePermissions.StrictOwnerOnlyVerificationForTests = null;

        SecureFilePermissions.WindowsOwnerOnlyDirectoryCreateForTests = null;

        if (Directory.Exists(_container))
        {

            Directory.Delete(_container, recursive: true);

        }

    }

    [Fact]
    public void Typed_acquisition_distinguishes_acquired_contended_and_unsafe()
    {

        ArcanumMaintenanceLockAcquisitionResult first =
            ArcanumMaintenanceLock.AcquireDetailed(_root);

        Assert.Equal(
            ArcanumMaintenanceLockAcquisitionDisposition.Acquired,
            first.Disposition);

        ArcanumMaintenanceLock held = first.BorrowAcquiredLock();

        try
        {

            ArcanumMaintenanceLockAcquisitionResult second =
                ArcanumMaintenanceLock.AcquireDetailed(_root);

            Assert.Equal(
                ArcanumMaintenanceLockAcquisitionDisposition.Contended,
                second.Disposition);

        }
        finally
        {

            held.Dispose();

        }

        string sentinel = Path.Combine(_root, "typed-sentinel.txt");

        File.WriteAllText(sentinel, "unchanged");

        string lockPath = ArcanumMaintenanceLock.LockPathFor(_root);

        File.Delete(lockPath);

        File.CreateSymbolicLink(lockPath, sentinel);

        ArcanumMaintenanceLockAcquisitionResult unsafeResult =
            ArcanumMaintenanceLock.AcquireDetailed(_root);

        Assert.Equal(
            ArcanumMaintenanceLockAcquisitionDisposition.Unsafe,
            unsafeResult.Disposition);

        Assert.Equal("unchanged", File.ReadAllText(sentinel));

    }

    [Fact]
    public void Sharing_violation_classifier_accepts_only_the_current_platform_constructor_code()
    {

        int expectedCode = OperatingSystem.IsWindows()
            ? unchecked((int)0x80070020)
            : OperatingSystem.IsLinux()
                ? 11
                : OperatingSystem.IsMacOS()
                    ? 35
                    : int.MinValue;

        bool expected = expectedCode != int.MinValue;

        Assert.Equal(
            expected,
            ArcanumMaintenanceLock.IsVerifiedSharingViolation(
                new IOException("exclusive open failed", expectedCode)));

        Assert.False(ArcanumMaintenanceLock.IsVerifiedSharingViolation(
            new IOException("unrelated I/O", 5)));

        Assert.False(ArcanumMaintenanceLock.IsVerifiedSharingViolation(
            new IOException(
                "lock violation is not a sharing violation",
                unchecked((int)0x80070021))));

    }

    [Fact]
    public void A_fresh_parent_uses_owner_only_creation_before_the_lock_leaf_is_opened()
    {

        string guarded = Path.Combine(_root, "fresh-parent", "arcanum");

        string lockPath = ArcanumMaintenanceLock.LockPathFor(guarded);

        int creationAttempts = 0;

        SecureFilePermissions.WindowsOwnerOnlyDirectoryCreateForTests =
            (path, _, _, _, _) =>
            {

                creationAttempts++;

                Assert.Equal(Path.GetDirectoryName(lockPath), path);

                throw new UnauthorizedAccessException("synthetic owner-only creation refusal");

            };

        try
        {

            using ArcanumMaintenanceLock? acquired =
                ArcanumMaintenanceLock.TryAcquire(guarded);

            Assert.Null(acquired);

            Assert.Equal(1, creationAttempts);

            Assert.False(File.Exists(lockPath));

        }
        finally
        {

            SecureFilePermissions.WindowsOwnerOnlyDirectoryCreateForTests = null;

        }

    }

    [Fact]
    public void An_uncontended_lock_is_acquired_and_released()
    {

        Assert.False(ArcanumMaintenanceLock.CannotAcquireSafely(_root));

        using (ArcanumMaintenanceLock? held = ArcanumMaintenanceLock.TryAcquire(_root))
        {

            Assert.NotNull(held);

            Assert.True(ArcanumMaintenanceLock.CannotAcquireSafely(_root));

        }

        Assert.False(ArcanumMaintenanceLock.CannotAcquireSafely(_root));

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
    public void Releasing_a_lock_leaves_its_file_in_place_for_whoever_holds_it_next()
    {

        string guarded = Path.Combine(_root, "arcanum");

        Directory.CreateDirectory(guarded);

        string path = ArcanumMaintenanceLock.LockPathFor(guarded);

        ArcanumMaintenanceLock? held = ArcanumMaintenanceLock.TryAcquire(guarded);

        Assert.NotNull(held);

        held.Dispose();

        // Exclusion is keyed to the file the acquirers open, not to its name: on Unix the share mode
        // is an advisory lock on that one inode. Unlinking on release lets an acquirer that opened
        // the inode during the release keep a lock on a file that no longer has a name, while the
        // next acquirer creates a fresh inode at the same path and takes what it believes is the
        // same exclusive lock. Closing the handle is the whole of the release — a file nothing holds
        // is already not a lock, which is what the stale-file case below depends on.
        Assert.True(File.Exists(path));

        using ArcanumMaintenanceLock? next = ArcanumMaintenanceLock.TryAcquire(guarded);

        Assert.NotNull(next);

        Assert.Null(ArcanumMaintenanceLock.TryAcquire(guarded));

    }

    [Fact]
    public void A_stale_lock_file_left_by_a_dead_process_does_not_block_acquisition()
    {

        File.WriteAllText(ArcanumMaintenanceLock.LockPathFor(_root), "stale");

        using ArcanumMaintenanceLock? acquired = ArcanumMaintenanceLock.TryAcquire(_root);

        Assert.NotNull(acquired);

    }

    [Fact]
    public void Parent_owner_only_verification_failure_refuses_before_opening_the_lock_file()
    {

        string guarded = Path.Combine(_root, "parent-verification-failure");

        string lockPath = ArcanumMaintenanceLock.LockPathFor(guarded);

        int directoryVerifications = 0;

        int fileVerifications = 0;

        SecureFilePermissions.StrictOwnerOnlyVerificationForTests = (_, isDirectory) =>
        {

            if (isDirectory)
            {

                directoryVerifications++;

                return false;

            }

            fileVerifications++;

            return true;

        };

        try
        {

            using ArcanumMaintenanceLock? acquired =
                ArcanumMaintenanceLock.TryAcquire(guarded);

            Assert.Null(acquired);

            Assert.Equal(1, directoryVerifications);

            Assert.Equal(0, fileVerifications);

            Assert.False(File.Exists(lockPath));

        }
        finally
        {

            SecureFilePermissions.StrictOwnerOnlyVerificationForTests = null;

        }

    }

    [Fact]
    public void Lock_leaf_owner_only_verification_failure_preserves_bytes_and_releases_the_handle()
    {

        string guarded = Path.Combine(_root, "leaf-verification-failure");

        string lockPath = ArcanumMaintenanceLock.LockPathFor(guarded);

        byte[] original = "stale-lock-sentinel"u8.ToArray();

        File.WriteAllBytes(lockPath, original);

        SecureFilePermissions.StrictOwnerOnlyVerificationForTests =
            (_, isDirectory) => isDirectory;

        try
        {

            using ArcanumMaintenanceLock? acquired =
                ArcanumMaintenanceLock.TryAcquire(guarded);

            Assert.Null(acquired);

            Assert.Equal(original, File.ReadAllBytes(lockPath));

        }
        finally
        {

            SecureFilePermissions.StrictOwnerOnlyVerificationForTests = null;

        }

        using ArcanumMaintenanceLock? reacquired =
            ArcanumMaintenanceLock.TryAcquire(guarded);

        Assert.NotNull(reacquired);

    }

    [Fact]
    public void A_symlink_at_the_lock_leaf_is_refused_without_changing_its_target()
    {

        string sentinel = Path.Combine(_root, "sentinel.txt");

        byte[] original = [0x41, 0x72, 0x63, 0x61, 0x6E, 0x75, 0x6D];

        File.WriteAllBytes(sentinel, original);

        string lockPath = ArcanumMaintenanceLock.LockPathFor(_root);

        File.CreateSymbolicLink(lockPath, sentinel);

        using ArcanumMaintenanceLock? acquired = ArcanumMaintenanceLock.TryAcquire(_root);

        acquired?.Dispose();

        Assert.Equal(original, File.ReadAllBytes(sentinel));

        Assert.Null(acquired);

        Assert.True(ArcanumMaintenanceLock.CannotAcquireSafely(_root));

    }

    [SkippableFact]
    public void A_hard_link_at_the_lock_leaf_is_refused_without_changing_its_target()
    {

        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux() && !OperatingSystem.IsWindows(),
            "Unsupported operating system.");

        string sentinel = Path.Combine(_root, "hard-link-sentinel.txt");

        byte[] original = [0x57, 0x61, 0x72, 0x64];

        File.WriteAllBytes(sentinel, original);

        string lockPath = ArcanumMaintenanceLock.LockPathFor(_root);

        Assert.True(HardLinkTestSupport.TryCreate(lockPath, sentinel));

        using ArcanumMaintenanceLock? acquired = ArcanumMaintenanceLock.TryAcquire(_root);

        acquired?.Dispose();

        Assert.Equal(original, File.ReadAllBytes(sentinel));

        Assert.Null(acquired);

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
    public void Acquiring_through_a_symlink_ancestor_is_refused_without_mutating_its_target()
    {

        string target = Path.Combine(_root, "symlink-target");

        string ancestor = Path.Combine(_root, "symlink-ancestor");

        Directory.CreateDirectory(target);

        Directory.CreateSymbolicLink(ancestor, target);

        string guarded = Path.Combine(ancestor, "retained-parent", "arcanum");

        using ArcanumMaintenanceLock? acquired = ArcanumMaintenanceLock.TryAcquire(guarded);

        Assert.Empty(Directory.GetFileSystemEntries(target));

        Assert.Null(acquired);

    }

    [Fact]
    public void Acquiring_through_a_non_directory_ancestor_is_refused_without_mutation()
    {

        string obstruction = Path.Combine(_root, "ordinary-file-ancestor");

        File.WriteAllText(obstruction, "unchanged");

        string guarded = Path.Combine(obstruction, "retained-parent", "arcanum");

        using ArcanumMaintenanceLock? acquired = ArcanumMaintenanceLock.TryAcquire(guarded);

        Assert.Null(acquired);

        Assert.Equal("unchanged", File.ReadAllText(obstruction));

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

        Assert.False(ArcanumMaintenanceLock.CannotAcquireSafely(_root));

    }

}
