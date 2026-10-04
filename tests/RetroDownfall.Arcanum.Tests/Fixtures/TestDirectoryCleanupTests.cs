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
