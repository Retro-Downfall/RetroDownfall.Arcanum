using System.Text.RegularExpressions;

using Microsoft.Win32.SafeHandles;

using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Storage;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Storage;

/// <summary>
/// The directory durability barrier: <c>F_FULLFSYNC</c> on macOS, <c>fsync</c> elsewhere.
/// </summary>
/// <remarks>
/// Power loss is not something a test can arrange, so the barrier's decision is pinned through its
/// native-call seam on every host, the real call is exercised on this host, and the source is checked
/// so no directory barrier can bypass the decision with a bare <c>fsync</c> of its own.
/// </remarks>
public sealed partial class DurableDirectoryFlushTests
{
    [Fact]
    public void Mac_arm_issues_full_fsync_and_falls_back_to_fsync_only_when_refused()
    {
        List<string> calls = [];

        Assert.True(DurableDirectoryFlush.TryFlushDescriptor(
            7,
            macOS: true,
            descriptor => Record(calls, "full", descriptor, 0),
            descriptor => Record(calls, "fsync", descriptor, 0)));

        Assert.Equal(["full:7"], calls);

        calls.Clear();

        Assert.True(DurableDirectoryFlush.TryFlushDescriptor(
            7,
            macOS: true,
            descriptor => Record(calls, "full", descriptor, -1),
            descriptor => Record(calls, "fsync", descriptor, 0)));

        Assert.Equal(["full:7", "fsync:7"], calls);

        calls.Clear();

        Assert.False(DurableDirectoryFlush.TryFlushDescriptor(
            7,
            macOS: true,
            descriptor => Record(calls, "full", descriptor, -1),
            descriptor => Record(calls, "fsync", descriptor, -1)));

        Assert.Equal(["full:7", "fsync:7"], calls);
    }

    [Fact]
    public void Non_mac_arm_issues_only_fsync()
    {
        List<string> calls = [];

        Assert.True(DurableDirectoryFlush.TryFlushDescriptor(
            9,
            macOS: false,
            descriptor => Record(calls, "full", descriptor, 0),
            descriptor => Record(calls, "fsync", descriptor, 0)));

        Assert.Equal(["fsync:9"], calls);

        Assert.False(DurableDirectoryFlush.TryFlushDescriptor(
            -1,
            macOS: true,
            descriptor => Record(calls, "full", descriptor, 0),
            descriptor => Record(calls, "fsync", descriptor, 0)));
    }

    [Fact]
    public void The_real_barrier_flushes_a_directory_on_this_host()
    {
        string directory = Directory.CreateTempSubdirectory("arcanum-durable-flush-").FullName;

        try
        {
            Assert.True(FileHandleIdentityInterop.TryOpenDirectoryMetadata(
                directory,
                out SafeFileHandle handle,
                out _));

            using (handle)
            {
                Assert.True(DurableDirectoryFlush.TryFlush(handle));
            }

            Assert.True(DurableDirectoryFlush.TryFlushParentOf(Path.Combine(directory, "child")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Every directory barrier reaches the decision above. A bare <c>fsync</c> import anywhere else is a
    /// barrier that is not full on macOS.
    /// </summary>
    [Fact]
    public void No_source_file_other_than_the_barrier_imports_fsync()
    {
        string source = Path.Combine(TestRepositoryPaths.RepositoryRoot(), "src");

        string[] offenders = [.. Directory
            .EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(static path => !path.EndsWith("DurableDirectoryFlush.cs", StringComparison.Ordinal))
            .Where(static path => FsyncImport().IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(source, path))
            .Order(StringComparer.Ordinal)];

        Assert.Empty(offenders);
    }

    private static int Record(List<string> calls, string name, int descriptor, int result)
    {
        calls.Add(name + ":" + descriptor);

        return result;
    }

    [GeneratedRegex("EntryPoint\\s*=\\s*\"fsync\"")]
    private static partial Regex FsyncImport();
}
