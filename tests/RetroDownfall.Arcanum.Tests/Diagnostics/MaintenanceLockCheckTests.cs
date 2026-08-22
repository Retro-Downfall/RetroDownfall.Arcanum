using RetroDownfall.Arcanum.Core.Cli;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Diagnostics;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Diagnostics;

[Collection("WorkspacePathPolicy")]
public sealed class MaintenanceLockCheckTests : IDisposable
{

    private readonly string _container = Path.Combine(
        Path.GetTempPath(),
        "arcanum-maintenance-lock-check-" + Guid.NewGuid().ToString("N"));

    public MaintenanceLockCheckTests()
    {

        Directory.CreateDirectory(_container);

    }

    public void Dispose()
    {

        SecureFilePermissions.StrictOwnerOnlyVerificationForTests = null;

        if (Directory.Exists(_container))
        {

            Directory.Delete(_container, recursive: true);

        }

    }

    [Fact]
    public void Absent_parent_is_healthy_without_creating_any_path()
    {

        string guarded = Path.Combine(_container, "absent-parent", "arcanum");

        string parent = Path.GetDirectoryName(
            ArcanumMaintenanceLock.LockPathFor(guarded))!;

        DoctorFinding finding = Inspect(guarded);

        Assert.Equal(DoctorOutcome.Healthy, finding.Outcome);

        Assert.False(Directory.Exists(parent));

        Assert.False(File.Exists(ArcanumMaintenanceLock.LockPathFor(guarded)));

    }

    [Fact]
    public void Stale_owner_only_lock_is_reusable_and_its_bytes_are_unchanged()
    {

        string guarded = Path.Combine(_container, "stale", "arcanum");

        string path = CreateOwnerOnlyLockFile(guarded, "stale-sentinel");

        byte[] before = File.ReadAllBytes(path);

        DoctorFinding finding = Inspect(guarded);

        Assert.Equal(DoctorOutcome.Healthy, finding.Outcome);

        Assert.Contains("reused", finding.Detail, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(before, File.ReadAllBytes(path));

    }

    [Fact]
    public void Doctor_opens_the_lock_with_the_same_write_access_required_by_real_acquisition()
    {

        ProductionSource source = Assert.Single(
            ProductionSourceInventory.Sources(),
            static candidate => candidate.IsExactOwner(
                "src/RetroDownfall.Arcanum.Infrastructure/Diagnostics/RuntimeDiagnostics.cs"));

        Assert.True(source.Names("Access = FileAccess.ReadWrite"));

        Assert.False(source.Names("Access = FileAccess.Read,"));

    }

    [Fact]
    public void A_genuinely_held_lock_is_reported_as_proven_contention()
    {

        string guarded = Path.Combine(_container, "held", "arcanum");

        using ArcanumMaintenanceLock held = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(guarded));

        DoctorFinding finding = Inspect(guarded);

        Assert.Equal(DoctorOutcome.Degraded, finding.Outcome);

        Assert.Contains("another process", finding.Detail, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("installation reset", finding.Detail, StringComparison.OrdinalIgnoreCase);

    }

    [SkippableFact]
    public void Windows_read_only_stale_lock_is_degraded_and_its_bytes_are_unchanged()
    {

        Skip.IfNot(
            OperatingSystem.IsWindows(),
            "The Windows ReadOnly attribute is the access mismatch this regression pins.");

        string guarded = Path.Combine(_container, "read-only", "arcanum");

        string path = CreateOwnerOnlyLockFile(guarded, "read-only-sentinel");

        byte[] before = File.ReadAllBytes(path);

        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);

        try
        {

            DoctorFinding finding = Inspect(guarded);

            Assert.Equal(DoctorOutcome.Degraded, finding.Outcome);

            Assert.DoesNotContain("another process", finding.Detail, StringComparison.OrdinalIgnoreCase);

            Assert.Equal(before, File.ReadAllBytes(path));

        }
        finally
        {

            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);

        }

    }

    [Fact]
    public void Lock_leaf_symlink_is_unsafe_and_its_target_is_unchanged()
    {

        string guarded = Path.Combine(_container, "symlink", "arcanum");

        string path = ArcanumMaintenanceLock.LockPathFor(guarded);

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(Path.GetDirectoryName(path)!);

        string sentinel = Path.Combine(_container, "symlink-sentinel.txt");

        byte[] original = "diagnostic-must-not-follow"u8.ToArray();

        File.WriteAllBytes(sentinel, original);

        File.CreateSymbolicLink(path, sentinel);

        DoctorFinding finding = Inspect(guarded);

        Assert.Equal(DoctorOutcome.Degraded, finding.Outcome);

        Assert.DoesNotContain("another process", finding.Detail, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(original, File.ReadAllBytes(sentinel));

    }

    [Fact]
    public void Lock_parent_symlink_is_unsafe_and_its_target_is_unchanged()
    {

        string target = Path.Combine(_container, "parent-symlink-target");

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(target);

        string parentLink = Path.Combine(_container, "parent-symlink");

        Directory.CreateSymbolicLink(parentLink, target);

        string guarded = Path.Combine(parentLink, "arcanum");

        DoctorFinding finding = Inspect(guarded);

        Assert.Equal(DoctorOutcome.Degraded, finding.Outcome);

        Assert.DoesNotContain("another process", finding.Detail, StringComparison.OrdinalIgnoreCase);

        Assert.Empty(Directory.GetFileSystemEntries(target));

    }

    [Fact]
    public void Directory_at_the_lock_leaf_is_unsafe_and_unchanged()
    {

        string guarded = Path.Combine(_container, "directory-leaf", "arcanum");

        string path = ArcanumMaintenanceLock.LockPathFor(guarded);

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(Path.GetDirectoryName(path)!);

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(path);

        DoctorFinding finding = Inspect(guarded);

        Assert.Equal(DoctorOutcome.Degraded, finding.Outcome);

        Assert.DoesNotContain("another process", finding.Detail, StringComparison.OrdinalIgnoreCase);

        Assert.True(Directory.Exists(path));

        Assert.Empty(Directory.GetFileSystemEntries(path));

    }

    [SkippableFact]
    public void Hard_link_lock_leaf_is_unsafe_and_its_target_is_unchanged()
    {

        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux() && !OperatingSystem.IsWindows(),
            "Unsupported operating system.");

        string guarded = Path.Combine(_container, "hard-link", "arcanum");

        string path = ArcanumMaintenanceLock.LockPathFor(guarded);

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(Path.GetDirectoryName(path)!);

        string sentinel = Path.Combine(_container, "hard-link-sentinel.txt");

        byte[] original = "single-link-required"u8.ToArray();

        File.WriteAllBytes(sentinel, original);

        SecureFilePermissions.ApplyOwnerOnlyFile(sentinel);

        Assert.True(HardLinkTestSupport.TryCreate(path, sentinel));

        DoctorFinding finding = Inspect(guarded);

        Assert.Equal(DoctorOutcome.Degraded, finding.Outcome);

        Assert.DoesNotContain("another process", finding.Detail, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(original, File.ReadAllBytes(sentinel));

    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Non_owner_only_parent_or_leaf_is_unsafe_and_read_only(bool rejectParent)
    {

        string guarded = Path.Combine(_container, "posture", "arcanum");

        string path = CreateOwnerOnlyLockFile(guarded, "posture-sentinel");

        string parent = Path.GetDirectoryName(path)!;

        byte[] before = File.ReadAllBytes(path);

        SecureFilePermissions.StrictOwnerOnlyVerificationForTests =
            (candidate, isDirectory) => rejectParent
                ? !(isDirectory && string.Equals(candidate, parent, StringComparison.Ordinal))
                : isDirectory || !string.Equals(candidate, path, StringComparison.Ordinal);

        try
        {

            DoctorFinding finding = Inspect(guarded);

            Assert.Equal(DoctorOutcome.Degraded, finding.Outcome);

            Assert.DoesNotContain("another process", finding.Detail, StringComparison.OrdinalIgnoreCase);

            Assert.Equal(before, File.ReadAllBytes(path));

        }
        finally
        {

            SecureFilePermissions.StrictOwnerOnlyVerificationForTests = null;

        }

    }

    private static string CreateOwnerOnlyLockFile(string guarded, string contents)
    {

        string path = ArcanumMaintenanceLock.LockPathFor(guarded);

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(Path.GetDirectoryName(path)!);

        File.WriteAllText(path, contents);

        SecureFilePermissions.ApplyOwnerOnlyFile(path);

        return path;

    }

    private static DoctorFinding Inspect(string guarded)
        => MaintenanceLockCheck.Inspect(guarded);

}
