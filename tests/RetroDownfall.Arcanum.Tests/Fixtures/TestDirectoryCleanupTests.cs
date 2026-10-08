using System.Runtime.Versioning;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Fixtures;

[Collection("ProcessEnvironment")]
public sealed class TestDirectoryCleanupTests
{
    [Fact]
    public void TryDelete_removes_the_directory_and_reports_nothing()
    {
        string path = Path.Combine(Path.GetTempPath(), $"arcanum-cleanup-{Guid.NewGuid():N}");

        Directory.CreateDirectory(path);

        File.WriteAllText(Path.Combine(path, "child.txt"), "owned by the fixture");

        List<string> reports = [];

        Assert.True(TestDirectoryCleanup.TryDelete(path, "OwnerUnderTest", reports.Add));

        Assert.False(Directory.Exists(path));

        Assert.Empty(reports);
    }

    [Fact]
    public void TryDelete_treats_an_already_missing_directory_as_deleted()
    {
        string path = Path.Combine(Path.GetTempPath(), $"arcanum-cleanup-{Guid.NewGuid():N}");

        List<string> reports = [];

        Assert.True(TestDirectoryCleanup.TryDelete(path, "OwnerUnderTest", reports.Add));

        Assert.Empty(reports);
    }

    [Fact]
    public void TryDelete_reports_a_failed_delete_instead_of_swallowing_it()
    {
        string path = Path.Combine(Path.GetTempPath(), $"arcanum-cleanup-{Guid.NewGuid():N}");

        Directory.CreateDirectory(path);

        try
        {
            List<string> reports = [];

            bool deleted = TestDirectoryCleanup.TryDelete(
                path,
                "OwnerUnderTest",
                reports.Add,
                static _ => throw new IOException("held open by a scanner"));

            Assert.False(deleted);

            string report = Assert.Single(reports);

            Assert.Contains("OwnerUnderTest", report, StringComparison.Ordinal);

            Assert.Contains(path, report, StringComparison.Ordinal);

            Assert.Contains("held open by a scanner", report, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [SkippableFact]
    [UnsupportedOSPlatform("windows")]
    public void DeleteTree_removes_a_tree_a_killed_run_left_with_a_read_only_directory()
    {
        Skip.If(OperatingSystem.IsWindows(), "POSIX permission bits do not apply on Windows.");

        string root = Path.Combine(Path.GetTempPath(), $"arcanum-cleanup-{Guid.NewGuid():N}");

        string readOnly = Path.Combine(root, "restore", "extract");

        Directory.CreateDirectory(readOnly);

        File.WriteAllText(Path.Combine(readOnly, "arcanum.db"), "left behind");

        File.SetUnixFileMode(readOnly, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try
        {
            TestDirectoryCleanup.DeleteTree(root);

            Assert.False(Directory.Exists(root));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                File.SetUnixFileMode(readOnly, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// Restoring owner access walks the doomed tree, and a symlink in it points outside the tree. Following
    /// it would grant owner access to directories the cleanup was never asked to touch.
    /// </summary>
    [SkippableFact]
    [UnsupportedOSPlatform("windows")]
    public void DeleteTree_does_not_change_the_permissions_of_a_directory_a_symlink_points_at()
    {
        Skip.If(OperatingSystem.IsWindows(), "POSIX permission bits do not apply on Windows.");

        const UnixFileMode OwnerReadExecute = UnixFileMode.UserRead | UnixFileMode.UserExecute;

        const UnixFileMode OwnerAll = OwnerReadExecute | UnixFileMode.UserWrite;

        string sandbox = Path.Combine(Path.GetTempPath(), $"arcanum-cleanup-{Guid.NewGuid():N}");

        string root = Path.Combine(sandbox, "doomed");

        string readOnly = Path.Combine(root, "restore", "extract");

        string outside = Path.Combine(sandbox, "outside");

        string outsideInner = Path.Combine(outside, "inner");

        Directory.CreateDirectory(readOnly);

        Directory.CreateDirectory(outsideInner);

        File.WriteAllText(Path.Combine(readOnly, "arcanum.db"), "left behind");

        // The link lives inside the read-only directory, so the first recursive delete cannot unlink it and
        // the owner-access restore is the pass that meets it.
        _ = Directory.CreateSymbolicLink(Path.Combine(readOnly, "link-to-outside"), outside);

        File.SetUnixFileMode(outsideInner, OwnerReadExecute);

        File.SetUnixFileMode(outside, OwnerReadExecute);

        File.SetUnixFileMode(readOnly, OwnerReadExecute);

        try
        {
            TestDirectoryCleanup.DeleteTree(root);

            Assert.False(Directory.Exists(root));

            Assert.Equal(OwnerReadExecute, File.GetUnixFileMode(outside) & OwnerAll);

            Assert.Equal(OwnerReadExecute, File.GetUnixFileMode(outsideInner) & OwnerAll);
        }
        finally
        {
            File.SetUnixFileMode(outsideInner, OwnerAll);

            File.SetUnixFileMode(outside, OwnerAll);

            if (Directory.Exists(readOnly))
            {
                File.SetUnixFileMode(readOnly, OwnerAll);
            }

            Directory.Delete(sandbox, recursive: true);
        }
    }

    /// <summary>
    /// Windows has no owner-access restore: a tree it cannot delete (a file still open, a denied ACL) is a
    /// failure to report, not one to paper over. The portable twin is
    /// <see cref="TryDelete_reports_a_failed_delete_instead_of_swallowing_it"/>; this is the lane that runs
    /// the real Windows delete. It has not been run on a Windows host.
    /// </summary>
    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public void TryDelete_reports_a_tree_Windows_cannot_delete_because_a_file_is_still_open()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Holding a file open blocks deletion only on Windows.");

        string path = Path.Combine(Path.GetTempPath(), $"arcanum-cleanup-{Guid.NewGuid():N}");

        Directory.CreateDirectory(path);

        string held = Path.Combine(path, "held.db");

        using (new FileStream(held, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            List<string> reports = [];

            Assert.False(TestDirectoryCleanup.TryDelete(path, "OwnerUnderTest", reports.Add));

            string report = Assert.Single(reports);

            Assert.Contains("OwnerUnderTest", report, StringComparison.Ordinal);

            Assert.Contains(path, report, StringComparison.Ordinal);

            Assert.True(File.Exists(held));
        }

        Assert.True(TestDirectoryCleanup.TryDelete(path, "OwnerUnderTest"));

        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task Profile_fixture_reports_a_temp_home_it_could_not_delete()
    {
        List<string> reports = [];

        RestartableArcanumProfileFixture profile = new(
            clearPools: static () => { },
            disposeGrimoire: static () => { },
            deleteTempHome: static _ => throw new UnauthorizedAccessException("access denied"),
            report: reports.Add);

        string tempHome = profile.TempHome;

        try
        {
            await profile.DisposeAsync();

            string report = Assert.Single(reports);

            Assert.Contains(nameof(RestartableArcanumProfileFixture), report, StringComparison.Ordinal);

            Assert.Contains(tempHome, report, StringComparison.Ordinal);

            Assert.True(Directory.Exists(tempHome));
        }
        finally
        {
            Directory.Delete(tempHome, recursive: true);
        }
    }
}
