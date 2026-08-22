using RetroDownfall.Arcanum.Infrastructure.Coordination;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Coordination;

[Collection("WorkspacePathPolicy")]
public sealed class ArcanumClientMutationLockTests : IDisposable
{

    private readonly string _container;

    private readonly string _guardedRoot;

    public ArcanumClientMutationLockTests()
    {

        _container = Path.Combine(
            Path.GetTempPath(),
            "arcanum-client-mutation-lock-" + Guid.NewGuid().ToString("N"));

        _guardedRoot = Path.Combine(_container, "arcanum");

        Directory.CreateDirectory(_guardedRoot);

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
    public void A_symlink_at_the_lock_leaf_is_unsafe_and_does_not_change_its_target()
    {

        string sentinel = Path.Combine(_container, "sentinel.txt");

        byte[] original = "client-mutation-sentinel"u8.ToArray();

        File.WriteAllBytes(sentinel, original);

        File.CreateSymbolicLink(
            ArcanumClientMutationLock.LockPathFor(_guardedRoot),
            sentinel);

        ArcanumClientMutationLockAcquisitionResult acquisition =
            ArcanumClientMutationLock.AcquireDetailed(_guardedRoot);

        acquisition.Lock?.Dispose();

        Assert.Equal(
            ArcanumClientMutationLockAcquisitionDisposition.Unsafe,
            acquisition.Disposition);

        Assert.Equal(original, File.ReadAllBytes(sentinel));

    }

    [Fact]
    public void A_symlink_ancestor_is_unsafe_and_receives_no_lock_file()
    {

        string target = Path.Combine(_container, "target");

        string ancestor = Path.Combine(_container, "ancestor");

        Directory.CreateDirectory(target);

        Directory.CreateSymbolicLink(ancestor, target);

        string guarded = Path.Combine(ancestor, "retained-parent", "arcanum");

        ArcanumClientMutationLockAcquisitionResult acquisition =
            ArcanumClientMutationLock.AcquireDetailed(guarded);

        acquisition.Lock?.Dispose();

        Assert.Equal(
            ArcanumClientMutationLockAcquisitionDisposition.Unsafe,
            acquisition.Disposition);

        Assert.Empty(Directory.GetFileSystemEntries(target));

    }

    [Fact]
    public void Parent_owner_verification_failure_is_unsafe_before_the_leaf_is_opened()
    {

        string guarded = Path.Combine(_container, "parent-posture", "arcanum");

        string lockPath = ArcanumClientMutationLock.LockPathFor(guarded);

        SecureFilePermissions.StrictOwnerOnlyVerificationForTests =
            (_, isDirectory) => !isDirectory;

        try
        {

            ArcanumClientMutationLockAcquisitionResult acquisition =
                ArcanumClientMutationLock.AcquireDetailed(guarded);

            acquisition.Lock?.Dispose();

            Assert.Equal(
                ArcanumClientMutationLockAcquisitionDisposition.Unsafe,
                acquisition.Disposition);

            Assert.False(File.Exists(lockPath));

        }
        finally
        {

            SecureFilePermissions.StrictOwnerOnlyVerificationForTests = null;

        }

    }

    [Fact]
    public void Lock_leaf_owner_verification_failure_is_unsafe_and_releases_the_handle()
    {

        string lockPath = ArcanumClientMutationLock.LockPathFor(_guardedRoot);

        byte[] original = "stale-client-lock"u8.ToArray();

        File.WriteAllBytes(lockPath, original);

        SecureFilePermissions.StrictOwnerOnlyVerificationForTests =
            (_, isDirectory) => isDirectory;

        try
        {

            ArcanumClientMutationLockAcquisitionResult acquisition =
                ArcanumClientMutationLock.AcquireDetailed(_guardedRoot);

            acquisition.Lock?.Dispose();

            Assert.Equal(
                ArcanumClientMutationLockAcquisitionDisposition.Unsafe,
                acquisition.Disposition);

            Assert.Equal(original, File.ReadAllBytes(lockPath));

        }
        finally
        {

            SecureFilePermissions.StrictOwnerOnlyVerificationForTests = null;

        }

        using ArcanumClientMutationLock reacquired =
            ArcanumClientMutationLock
                .AcquireDetailed(_guardedRoot)
                .BorrowAcquiredLock();

    }

    [SkippableFact]
    public void A_hard_link_at_the_lock_leaf_is_unsafe_and_does_not_change_its_target()
    {

        Skip.If(
            !OperatingSystem.IsMacOS()
                && !OperatingSystem.IsLinux()
                && !OperatingSystem.IsWindows(),
            "Unsupported operating system.");

        string sentinel = Path.Combine(_container, "hard-link-sentinel.txt");

        byte[] original = "client-hard-link"u8.ToArray();

        File.WriteAllBytes(sentinel, original);

        Assert.True(HardLinkTestSupport.TryCreate(
            ArcanumClientMutationLock.LockPathFor(_guardedRoot),
            sentinel));

        ArcanumClientMutationLockAcquisitionResult acquisition =
            ArcanumClientMutationLock.AcquireDetailed(_guardedRoot);

        acquisition.Lock?.Dispose();

        Assert.Equal(
            ArcanumClientMutationLockAcquisitionDisposition.Unsafe,
            acquisition.Disposition);

        Assert.Equal(original, File.ReadAllBytes(sentinel));

    }

    [Fact]
    public void A_client_mutation_lock_is_a_distinct_retained_sibling_and_excludes_a_second_owner()
    {

        string expected = Path.Combine(
            _container,
            ".arcanum-client-mutation-arcanum.lock");

        ArcanumClientMutationLockAcquisitionResult first =
            ArcanumClientMutationLock.AcquireDetailed(_guardedRoot);

        Assert.Equal(
            ArcanumClientMutationLockAcquisitionDisposition.Acquired,
            first.Disposition);

        using ArcanumClientMutationLock held = first.BorrowAcquiredLock();

        Assert.Equal(expected, held.Path);

        Assert.NotEqual(
            ArcanumMaintenanceLock.LockPathFor(_guardedRoot),
            held.Path);

        ArcanumClientMutationLockAcquisitionResult second =
            ArcanumClientMutationLock.AcquireDetailed(_guardedRoot);

        Assert.Equal(
            ArcanumClientMutationLockAcquisitionDisposition.Contended,
            second.Disposition);

    }

    [Fact]
    public void Maintenance_and_client_mutexes_share_the_same_hardened_retained_lock_kernel()
    {

        IReadOnlyList<ProductionSource> sources = ProductionSourceInventory.Sources();

        ProductionSource maintenance = Assert.Single(
            sources,
            static source => source.IsExactOwner(
                "src/RetroDownfall.Arcanum.Infrastructure/Backup/ArcanumMaintenanceLock.cs"));

        ProductionSource client = Assert.Single(
            sources,
            static source => source.IsExactOwner(
                "src/RetroDownfall.Arcanum.Infrastructure/Coordination/ArcanumClientMutationLock.cs"));

        Assert.True(maintenance.Names("RetainedExclusiveFileLock.Acquire"));

        Assert.True(client.Names("RetainedExclusiveFileLock.Acquire"));

    }

}
