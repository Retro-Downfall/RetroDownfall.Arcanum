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
        FileHandleIdentityInterop.TryGetPathMetadataForTests = null;

        SecureFileReader.AfterOpenForTests = null;

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

        int containmentChecks = 0;

        WorkspacePathPolicy.ContainmentCheckObserverForTests = _ =>
            Interlocked.Increment(ref containmentChecks);

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
            WorkspacePathPolicy.ContainmentCheckObserverForTests = null;
        }

        Assert.True(result.IsSuccess);

        Assert.Equal(entryCount, result.Value!.Entries.Length);

        // One canonical containment check for the starting directory; the tree holds no links, so no
        // entry below it needs its own.
        Assert.True(
            containmentChecks <= 1,
            $"The recursive walk ran {containmentChecks} full containment checks for {entryCount} entries; "
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

    /// <summary>
    /// R-007: <c>d -> b/sub</c> is spelled through <c>b -> ../outside</c>, an escaping directory link. The
    /// read must be refused as a symbolic-link escape rather than returning the outside file's content.
    /// </summary>
    [SkippableFact]
    public async Task ReadAsync_rejects_directory_link_chained_through_escaping_link()
    {
        Skip.If(OperatingSystem.IsWindows(), "Symlink creation requires elevation on Windows.");

        string outsideDir = Path.Combine(
            Path.GetDirectoryName(_workspace.Root)!,
            "outside-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Path.Combine(outsideDir, "sub"));

        try
        {
            await File.WriteAllTextAsync(Path.Combine(outsideDir, "sub", "file.txt"), "outside secret");

            Directory.CreateSymbolicLink(
                Path.Combine(_workspace.Root, "b"),
                Path.Combine("..", Path.GetFileName(outsideDir)));

            Directory.CreateSymbolicLink(
                Path.Combine(_workspace.Root, "d"),
                Path.Combine("b", "sub"));

            PhysicalFileSystemBrowser browser = CreateBrowser();

            Result<FileReadResult> result = await browser.ReadAsync(
                MakeWorkspace(),
                "d/file.txt",
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(ErrorCodes.Workspace.SymbolicLinkEscape, result.Error.Code);
        }
        finally
        {
            Directory.Delete(outsideDir, recursive: true);
        }
    }

    /// <summary>
    /// The read must prove where the opened handle lives, not only that the path still names the same
    /// file afterwards. Here the parent directory is moved out of the workspace and replaced by a link to
    /// its new location between the pre-open check and the open, so the open reads the file while it sits
    /// outside the root; the move is undone once the handle is open. The file's identity never changes and
    /// the path is contained again by the time of the post-read path check, so only the kernel's path for
    /// the open handle shows the read happened outside the workspace.
    /// </summary>
    [SkippableFact]
    public async Task ReadAsync_rejects_a_handle_opened_outside_the_workspace_even_when_the_path_is_restored_before_the_final_check()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "Moving a directory that holds an open file is refused on Windows.");

        string target = _workspace.WriteFile("sub/file.txt", "inside content");

        string insideParent = Path.Combine(_workspace.Root, "sub");

        string outsideDir = Path.Combine(
            Path.GetDirectoryName(_workspace.Root)!,
            "outside-" + Guid.NewGuid().ToString("N"));

        string outsideParent = Path.Combine(outsideDir, "sub");

        Directory.CreateDirectory(outsideDir);

        bool swapped = false;

        // Runs after the pre-open containment check and before the open: the parent leaves the workspace
        // and a link to it takes its place, so the open follows the link out of the root.
        FileHandleIdentityInterop.TryGetPathMetadataForTests = path =>
        {
            FileHandleMetadata? real = ResolveRealPathMetadata(path);

            if (!swapped && Path.GetFullPath(path) == Path.GetFullPath(target))
            {
                swapped = true;

                Directory.Move(insideParent, outsideParent);

                Directory.CreateSymbolicLink(insideParent, outsideParent);
            }

            return real;
        };

        // Runs once the open handle is being read: the move is undone, so the path is contained again.
        SecureFileReader.AfterOpenForTests = _ =>
        {
            SecureFileReader.AfterOpenForTests = null;

            RestoreParent(insideParent, outsideParent);
        };

        try
        {
            Result<FileReadResult> result = await CreateBrowser().ReadAsync(
                MakeWorkspace(),
                "sub/file.txt",
                CancellationToken.None);

            Assert.True(swapped);

            Assert.True(result.IsFailure);

            Assert.Equal(ErrorCodes.Workspace.SymbolicLinkEscape, result.Error.Code);
        }
        finally
        {
            FileHandleIdentityInterop.TryGetPathMetadataForTests = null;

            SecureFileReader.AfterOpenForTests = null;

            RestoreParent(insideParent, outsideParent);

            Directory.Delete(outsideDir, recursive: true);
        }
    }

    private static void RestoreParent(string insideParent, string outsideParent)
    {
        if (!Directory.Exists(outsideParent))
        {
            return;
        }

        if ((File.GetAttributes(insideParent) & FileAttributes.ReparsePoint) != 0)
        {
            File.Delete(insideParent);
        }

        Directory.Move(outsideParent, insideParent);
    }

    // Real metadata through the no-follow probe, which has its own seam, so the seam above never has to
    // unset itself to answer honestly. Every path read here is a regular file, where the two probes agree.
    private static FileHandleMetadata? ResolveRealPathMetadata(string path) =>
        FileHandleIdentityInterop.TryGetPathMetadataNoFollow(path, out FileHandleMetadata metadata)
            ? metadata
            : null;

    private static PhysicalFileSystemBrowser CreateBrowser() =>
        new(new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

    private WorkspaceInfo MakeWorkspace() =>
        new("id", "test", _workspace.Root, WorkspaceType.Campaign, DateTimeOffset.UtcNow);
}
