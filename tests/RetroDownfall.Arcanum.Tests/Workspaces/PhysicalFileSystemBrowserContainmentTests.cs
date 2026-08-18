using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Workspaces;

/// <summary>
/// Containment behaviour and containment cost of the recursive listing walk. Lives in the
/// WorkspacePathPolicy collection because the resolution counter installs the policy's process-global
/// test seam, which that collection definition serialises against the rest of the suite.
/// </summary>
[Collection("WorkspacePathPolicy")]
public sealed class PhysicalFileSystemBrowserContainmentTests : IAsyncLifetime
{

    private TempWorkspace _workspace = null!;

    public async Task InitializeAsync()
    {

        _workspace = new TempWorkspace();

        await _workspace.InitializeAsync();

    }

    public async Task DisposeAsync()
    {

        WorkspacePathPolicy.ResetTestSeams();

        await _workspace.DisposeAsync();

    }

    /// <summary>
    /// The walk called IsPathUnderWorkspaceWithSymlinkCheck for every entry it enumerated, and that check
    /// re-walks every path component from the workspace root down — File.Exists + Directory.Exists +
    /// ResolveLinkTarget apiece — so listing a directory of N children at depth D cost N x D component
    /// resolutions to prove a chain that was already proven for the first child. Containment is inherited:
    /// once the parent directory is proven, only a symbolic link can break it for a child.
    /// </summary>
    [Fact]
    public async Task ListAsync_recursive_does_not_revalidate_the_ancestor_chain_for_every_child()
    {

        const int leafCount = 40;

        for (int i = 0; i < leafCount; i++)
        {

            _workspace.WriteFile($"a/b/c/d/leaf-{i:D2}.txt", "x");

        }

        // 40 leaves plus the four directories on the way down.
        const int entryCount = leafCount + 4;

        int resolutions = 0;

        WorkspacePathPolicy.SetSymlinkResolverForTests(path =>
        {

            _ = path;

            _ = Interlocked.Increment(ref resolutions);

            return (true, null);

        });

        Result<FileListResult> result;

        try
        {

            PhysicalFileSystemBrowser browser = CreateBrowser();

            result = await browser.ListAsync(
                MakeWorkspace(),
                null,
                recursive: true,
                searchPattern: null,
                CancellationToken.None);

        }
        finally
        {

            WorkspacePathPolicy.SetSymlinkResolverForTests(null);

        }

        Assert.True(result.IsSuccess);

        Assert.Equal(entryCount, result.Value!.Entries.Length);

        Assert.True(
            resolutions <= entryCount,
            $"The recursive walk performed {resolutions} symlink-component resolutions for {entryCount} entries; "
            + "containment must be proven once per directory chain, not re-walked for every child.");

    }

    /// <summary>
    /// Guards the inheritance above: a symbolic link is the one thing that can break a proven chain, so it
    /// must still take the full containment walk. A directory symlinked out of the workspace stays out of
    /// the listing, and nothing underneath it is traversed.
    /// </summary>
    [SkippableFact]
    public async Task ListAsync_recursive_excludes_a_directory_symlinked_outside_the_workspace()
    {

        Skip.If(OperatingSystem.IsWindows(), "Symlink creation requires elevation on Windows.");

        string outsideDir = Path.Combine(Path.GetTempPath(), "arcanum-outside-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(outsideDir);

        try
        {

            await File.WriteAllTextAsync(Path.Combine(outsideDir, "secret.txt"), "outside secret");

            _workspace.WriteFile("inside/kept.txt", "kept");

            Directory.CreateSymbolicLink(Path.Combine(_workspace.Root, "escape-dir"), outsideDir);

            PhysicalFileSystemBrowser browser = CreateBrowser();

            Result<FileListResult> result = await browser.ListAsync(
                MakeWorkspace(),
                null,
                recursive: true,
                searchPattern: null,
                CancellationToken.None);

            Assert.True(result.IsSuccess);

            Assert.Contains(result.Value!.Entries, e => e.Name == "kept.txt");

            Assert.DoesNotContain(result.Value.Entries, e => e.Name == "escape-dir");

            Assert.DoesNotContain(result.Value.Entries, e => e.Name == "secret.txt");

        }
        finally
        {

            if (Directory.Exists(outsideDir))
            {

                Directory.Delete(outsideDir, recursive: true);

            }

        }

    }

    /// <summary>
    /// The other half of the guard: a symbolic link that stays inside the workspace is still listed, so the
    /// containment shortcut cannot be mistaken for "skip every link".
    /// </summary>
    [SkippableFact]
    public async Task ListAsync_recursive_keeps_a_symlink_that_stays_inside_the_workspace()
    {

        Skip.If(OperatingSystem.IsWindows(), "Symlink creation requires elevation on Windows.");

        _workspace.WriteFile("inside/target.txt", "target");

        File.CreateSymbolicLink(
            Path.Combine(_workspace.Root, "alias.txt"),
            Path.Combine(_workspace.Root, "inside", "target.txt"));

        PhysicalFileSystemBrowser browser = CreateBrowser();

        Result<FileListResult> result = await browser.ListAsync(
            MakeWorkspace(),
            null,
            recursive: true,
            searchPattern: null,
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Contains(result.Value!.Entries, e => e.Name == "alias.txt");

        Assert.Contains(result.Value.Entries, e => e.Name == "target.txt");

    }

    private static PhysicalFileSystemBrowser CreateBrowser() =>
        new(new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

    private WorkspaceInfo MakeWorkspace() =>
        new("id", "test", _workspace.Root, WorkspaceType.Campaign, DateTimeOffset.UtcNow);

}
