using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Workspaces;

public sealed class PhysicalFileSystemWriterTests : IAsyncLifetime
{

    private TempWorkspace _workspace = null!;

    public async Task InitializeAsync()
    {

        _workspace = new TempWorkspace();

        await _workspace.InitializeAsync();

    }

    public async Task DisposeAsync()
    {

        await _workspace.DisposeAsync();

    }

    [Fact]
    public async Task WriteFileAsync_creates_new_file_with_parent_directories()
    {

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileWriteResult> result = await writer.WriteFileAsync(workspace, "nested/deep/new.txt", "hello", CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal("nested/deep/new.txt", result.Value!.RelativePath.Replace('\\', '/'));

        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(_workspace.Root, "nested", "deep", "new.txt")));

    }

    [Fact]
    public async Task WriteFileAsync_overwrites_existing_file()
    {

        _workspace.WriteFile("existing.txt", "old content");

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileWriteResult> result = await writer.WriteFileAsync(workspace, "existing.txt", "new content", CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal("new content", await File.ReadAllTextAsync(Path.Combine(_workspace.Root, "existing.txt")));

    }

    [Fact]
    public async Task WriteFileAsync_rejects_path_traversal()
    {

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileWriteResult> result = await writer.WriteFileAsync(workspace, "../outside.txt", "hello", CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.PathTraversal", result.Error.Code);

    }

    [Fact]
    public async Task WriteFileAsync_rejects_absolute_paths()
    {

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        string absolutePath = OperatingSystem.IsWindows() ? "C:\\outside.txt" : "/etc/outside.txt";

        Result<FileWriteResult> result = await writer.WriteFileAsync(workspace, absolutePath, "hello", CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.PathTraversal", result.Error.Code);

    }

    [Fact]
    public async Task WriteFileAsync_rejects_symlink_escape()
    {

        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            return;
        }

        string outsideDir = Path.Combine(Path.GetTempPath(), $"arcanum-outside-{Guid.NewGuid():N}");

        Directory.CreateDirectory(outsideDir);

        try
        {

            string linkPath = Path.Combine(_workspace.Root, "escape-link.txt");

            string outsideFile = Path.Combine(outsideDir, "target.txt");

            await File.WriteAllTextAsync(outsideFile, "outside secret");

            File.CreateSymbolicLink(linkPath, outsideFile);

            PhysicalFileSystemWriter writer = CreateWriter();

            WorkspaceInfo workspace = MakeWorkspace();

            Result<FileWriteResult> result = await writer.WriteFileAsync(workspace, "escape-link.txt", "overwritten", CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal("Workspace.SymbolicLinkEscape", result.Error.Code);

            Assert.Equal("outside secret", await File.ReadAllTextAsync(outsideFile));

        }
        finally
        {

            Directory.Delete(outsideDir, recursive: true);

        }

    }

    /// <summary>
    /// A symlinked ancestor plus a not-yet-existing leaf skips the resolver's symlink check, so the
    /// containment revalidation must run *before* the parent directories are created: mkdir(2) follows
    /// symlinks in the path prefix, and creating them first leaves an orphaned directory tree outside
    /// the workspace even though the write itself is rejected.
    /// </summary>
    [Fact]
    public async Task WriteFileAsync_does_not_create_parent_directories_outside_workspace_through_a_symlinked_ancestor()
    {

        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            return;
        }

        string outsideDir = Path.Combine(Path.GetTempPath(), $"arcanum-outside-{Guid.NewGuid():N}");

        Directory.CreateDirectory(outsideDir);

        try
        {

            Directory.CreateSymbolicLink(Path.Combine(_workspace.Root, "escape-dir"), outsideDir);

            PhysicalFileSystemWriter writer = CreateWriter();

            WorkspaceInfo workspace = MakeWorkspace();

            Result<FileWriteResult> result = await writer.WriteFileAsync(
                workspace, "escape-dir/injected/deeper/payload.txt", "hello", CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal("Workspace.SymbolicLinkEscape", result.Error.Code);

            Assert.False(Directory.Exists(Path.Combine(outsideDir, "injected")));

            Assert.False(File.Exists(Path.Combine(outsideDir, "injected", "deeper", "payload.txt")));

        }
        finally
        {

            Directory.Delete(outsideDir, recursive: true);

        }

    }

    [Fact]
    public async Task WriteFileAsync_rejects_content_exceeding_MaxFileWriteSizeBytes()
    {

        ArcanumSettings settings = new()
        {
            Workspaces = new WorkspaceSettings { EnableFileWrite = true },
        };

        PhysicalFileSystemWriter writer = CreateWriter(settings);

        WorkspaceInfo workspace = MakeWorkspace();
        int oversizedLength = checked(
            (int)ArcanumSettingClamps.MaxFileWriteSizeBytes(
                ArcanumRuntimeDefaults.WorkspaceMaxFileWriteSizeBytes)
            + 1);

        Result<FileWriteResult> result = await writer.WriteFileAsync(
            workspace,
            "big.txt",
            new string('x', oversizedLength),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.FileTooLarge", result.Error.Code);

    }

    [Fact]
    public async Task WriteFileAsync_returns_FileWriteDisabled_when_toggle_is_off()
    {

        PhysicalFileSystemWriter writer = CreateWriter(new ArcanumSettings());

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileWriteResult> result = await writer.WriteFileAsync(workspace, "any.txt", "hello", CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.FileWriteDisabled", result.Error.Code);

        Assert.False(File.Exists(Path.Combine(_workspace.Root, "any.txt")));

    }

    [Fact]
    public async Task WriteFileAsync_rejects_existing_directory_target()
    {

        _workspace.CreateSubdir("adir");

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileWriteResult> result = await writer.WriteFileAsync(workspace, "adir", "hello", CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.PathIsDirectory", result.Error.Code);

    }

    [Fact]
    public async Task ReplaceTextBlockAsync_replaces_single_occurrence()
    {

        _workspace.WriteFile("target.txt", "hello world, hello universe once");

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<TextBlockReplaceResult> result = await writer.ReplaceTextBlockAsync(
            workspace, "target.txt", "universe once", "galaxy", null, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(1, result.Value!.Replacements);

        Assert.Equal("hello world, hello galaxy", await File.ReadAllTextAsync(Path.Combine(_workspace.Root, "target.txt")));

    }

    [Fact]
    public async Task ReplaceTextBlockAsync_replaces_multiple_occurrences_with_expected_replacements()
    {

        _workspace.WriteFile("target.txt", "foo foo foo");

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<TextBlockReplaceResult> result = await writer.ReplaceTextBlockAsync(
            workspace, "target.txt", "foo", "bar", 3, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(3, result.Value!.Replacements);

        Assert.Equal("bar bar bar", await File.ReadAllTextAsync(Path.Combine(_workspace.Root, "target.txt")));

    }

    [Fact]
    public async Task ReplaceTextBlockAsync_returns_ReplacementNotFound_when_oldString_absent()
    {

        _workspace.WriteFile("target.txt", "hello world");

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<TextBlockReplaceResult> result = await writer.ReplaceTextBlockAsync(
            workspace, "target.txt", "missing", "replacement", null, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.ReplacementNotFound", result.Error.Code);

    }

    [Fact]
    public async Task ReplaceTextBlockAsync_returns_ReplacementAmbiguous_when_multiple_matches_and_no_expected_replacements()
    {

        _workspace.WriteFile("target.txt", "foo foo");

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<TextBlockReplaceResult> result = await writer.ReplaceTextBlockAsync(
            workspace, "target.txt", "foo", "bar", null, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.ReplacementAmbiguous", result.Error.Code);

    }

    [Fact]
    public async Task ReplaceTextBlockAsync_returns_ReplacementAmbiguous_when_expected_replacements_mismatches_actual_count()
    {

        _workspace.WriteFile("target.txt", "foo foo foo");

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<TextBlockReplaceResult> result = await writer.ReplaceTextBlockAsync(
            workspace, "target.txt", "foo", "bar", 2, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.ReplacementAmbiguous", result.Error.Code);

    }

    [Fact]
    public async Task ReplaceTextBlockAsync_returns_FileNotFound_when_file_does_not_exist()
    {

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<TextBlockReplaceResult> result = await writer.ReplaceTextBlockAsync(
            workspace, "missing.txt", "foo", "bar", null, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.FileNotFound", result.Error.Code);

    }

    [Fact]
    public async Task ReplaceTextBlockAsync_returns_FileNotFound_for_directory_path()
    {

        _workspace.CreateSubdir("adir");

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<TextBlockReplaceResult> result = await writer.ReplaceTextBlockAsync(
            workspace, "adir", "foo", "bar", null, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.FileNotFound", result.Error.Code);

    }

    [Fact]
    public async Task ReplaceTextBlockAsync_rejects_combined_size_exceeding_MaxReplaceTextBlockBytes()
    {

        _workspace.WriteFile("target.txt", "foo");

        ArcanumSettings settings = new()
        {
            Workspaces = new WorkspaceSettings { EnableFileWrite = true },
        };

        PhysicalFileSystemWriter writer = CreateWriter(settings);

        WorkspaceInfo workspace = MakeWorkspace();
        int replacementLength = checked(
            (int)ArcanumSettingClamps.MaxReplaceTextBlockBytes(
                ArcanumRuntimeDefaults.WorkspaceMaxReplaceTextBlockBytes));

        Result<TextBlockReplaceResult> result = await writer.ReplaceTextBlockAsync(
            workspace,
            "target.txt",
            "foo",
            new string('y', replacementLength),
            null,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.FileTooLarge", result.Error.Code);

    }

    [Fact]
    public async Task ReplaceTextBlockAsync_write_failure_leaves_no_temp_file_and_original_content_intact()
    {

        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            return;
        }

        string subdir = _workspace.CreateSubdir("readonly-dir");

        string filePath = Path.Combine(subdir, "target.txt");

        await File.WriteAllTextAsync(filePath, "original content");

        UnixFileMode originalMode = File.GetUnixFileMode(subdir);

        File.SetUnixFileMode(subdir, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        try
        {

            PhysicalFileSystemWriter writer = CreateWriter();

            WorkspaceInfo workspace = MakeWorkspace();

            Result<TextBlockReplaceResult> result = await writer.ReplaceTextBlockAsync(
                workspace, "readonly-dir/target.txt", "original", "modified", null, CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal("Workspace.AccessDenied", result.Error.Code);

        }
        finally
        {

            File.SetUnixFileMode(subdir, originalMode);

        }

        Assert.Equal("original content", await File.ReadAllTextAsync(filePath));

        Assert.Empty(Directory.EnumerateFiles(subdir, ".arcanum-*.tmp"));

    }

    [Fact]
    public async Task DeleteAsync_removes_file()
    {

        _workspace.WriteFile("gone.txt", "bye");

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileDeleteResult> result = await writer.DeleteAsync(workspace, "gone.txt", recursive: false, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.False(result.Value!.WasDirectory);

        Assert.False(File.Exists(Path.Combine(_workspace.Root, "gone.txt")));

    }

    [Fact]
    public async Task DeleteAsync_removes_empty_directory()
    {

        string dir = _workspace.CreateSubdir("empty-dir");

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileDeleteResult> result = await writer.DeleteAsync(workspace, "empty-dir", recursive: false, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.True(result.Value!.WasDirectory);

        Assert.False(Directory.Exists(dir));

    }

    [Fact]
    public async Task DeleteAsync_returns_DirectoryNotEmpty_for_non_empty_directory_without_recursive()
    {

        _workspace.WriteFile("non-empty-dir/child.txt", "content");

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileDeleteResult> result = await writer.DeleteAsync(workspace, "non-empty-dir", recursive: false, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.DirectoryNotEmpty", result.Error.Code);

    }

    [Fact]
    public async Task DeleteAsync_recursive_removes_non_empty_directory_tree()
    {

        _workspace.WriteFile("tree/a.txt", "a");

        _workspace.WriteFile("tree/nested/b.txt", "b");

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileDeleteResult> result = await writer.DeleteAsync(workspace, "tree", recursive: true, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.False(Directory.Exists(Path.Combine(_workspace.Root, "tree")));

    }

    [Fact]
    public async Task DeleteAsync_recursive_skips_symlinks_that_escape_workspace()
    {

        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            return;
        }

        string outsideDir = Path.Combine(Path.GetTempPath(), $"arcanum-outside-{Guid.NewGuid():N}");

        Directory.CreateDirectory(outsideDir);

        try
        {

            string outsideFile = Path.Combine(outsideDir, "target.txt");

            await File.WriteAllTextAsync(outsideFile, "outside secret");

            string parentDir = _workspace.CreateSubdir("recurseDelete");

            _workspace.WriteFile("recurseDelete/keep.txt", "keep me gone");

            string linkPath = Path.Combine(parentDir, "escape-link");

            File.CreateSymbolicLink(linkPath, outsideFile);

            PhysicalFileSystemWriter writer = CreateWriter();

            WorkspaceInfo workspace = MakeWorkspace();

            Result<FileDeleteResult> result = await writer.DeleteAsync(workspace, "recurseDelete", recursive: true, CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal("Workspace.DeleteFailed", result.Error.Code);

            Assert.True(File.Exists(outsideFile));

            Assert.True(File.Exists(linkPath) || Directory.Exists(linkPath));

            Assert.False(File.Exists(Path.Combine(parentDir, "keep.txt")));

        }
        finally
        {

            Directory.Delete(outsideDir, recursive: true);

        }

    }

    /// <summary>
    /// The workspace root is never a delete target.
    /// </summary>
    /// <remarks>
    /// <see cref="WorkspacePathResolver.ResolveRelativePath"/> deliberately maps an empty, blank, or
    /// <c>"."</c> relative path to the root, which is what the listing routes want. Nothing below it
    /// distinguishes that case: the resolved path equals the root, so the containment revalidation
    /// short-circuits on the equality branch and passes. Reached from
    /// <c>DELETE /api/workspaces/{id}/files?relativePath=.&amp;recursive=true</c> that unlinks the
    /// registered workspace itself, so the refusal belongs in the writer where every caller inherits it.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("./")]
    public async Task DeleteAsync_refuses_to_delete_the_workspace_root(string relativePath)
    {

        _workspace.WriteFile("keep.txt", "content");

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileDeleteResult> result = await writer.DeleteAsync(
            workspace,
            relativePath,
            recursive: true,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.PathNotAllowed", result.Error.Code);

        Assert.True(Directory.Exists(_workspace.Root));

        Assert.True(File.Exists(Path.Combine(_workspace.Root, "keep.txt")));

    }

    /// <summary>
    /// A relative path that walks back to the root is refused one layer earlier, by the resolver's
    /// traversal rule rather than the root guard. Pinned separately so the two refusals cannot be
    /// collapsed into one and silently lose a case.
    /// </summary>
    [Fact]
    public async Task DeleteAsync_refuses_a_traversal_that_resolves_to_the_workspace_root()
    {

        _workspace.WriteFile("keep.txt", "content");

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileDeleteResult> result = await writer.DeleteAsync(
            workspace,
            "foo/..",
            recursive: true,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.PathTraversal", result.Error.Code);

        Assert.True(Directory.Exists(_workspace.Root));

        Assert.True(File.Exists(Path.Combine(_workspace.Root, "keep.txt")));

    }

    [Fact]
    public async Task DeleteAsync_returns_FileNotFound_when_path_does_not_exist()
    {

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileDeleteResult> result = await writer.DeleteAsync(workspace, "missing.txt", recursive: false, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.FileNotFound", result.Error.Code);

    }

    [Fact]
    public async Task CreateDirectoryAsync_creates_nested_directories()
    {

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<DirectoryCreateResult> result = await writer.CreateDirectoryAsync(workspace, "a/b/c", CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.True(Directory.Exists(Path.Combine(_workspace.Root, "a", "b", "c")));

    }

    [Fact]
    public async Task CreateDirectoryAsync_rejects_path_traversal()
    {

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<DirectoryCreateResult> result = await writer.CreateDirectoryAsync(workspace, "../outside-dir", CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.PathTraversal", result.Error.Code);

    }

    [Fact]
    public async Task CreateDirectoryAsync_rejects_existing_file_target()
    {

        _workspace.WriteFile("afile.txt", "content");

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<DirectoryCreateResult> result = await writer.CreateDirectoryAsync(workspace, "afile.txt", CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.PathIsFile", result.Error.Code);

    }

    [Fact]
    public async Task WriteFileAsync_UnauthorizedAccessException_maps_to_AccessDenied()
    {

        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            return;
        }

        string subdir = _workspace.CreateSubdir("locked-dir");

        UnixFileMode originalMode = File.GetUnixFileMode(subdir);

        File.SetUnixFileMode(subdir, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        try
        {

            PhysicalFileSystemWriter writer = CreateWriter();

            WorkspaceInfo workspace = MakeWorkspace();

            Result<FileWriteResult> result = await writer.WriteFileAsync(workspace, "locked-dir/new.txt", "hello", CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal("Workspace.AccessDenied", result.Error.Code);

        }
        finally
        {

            File.SetUnixFileMode(subdir, originalMode);

        }

    }

    /// <summary>
    /// A FIFO planted in the workspace satisfies File.Exists, so the replace-text-block read reached a
    /// plain blocking FileStream: open(2) on a FIFO never returns until a writer appears, and the
    /// constructor takes no CancellationToken, so the request hung past RequestAborted and leaked a
    /// thread-pool thread per call. The sibling read paths already prove Kind == RegularFile first.
    /// </summary>
    [SkippableFact]
    public async Task ReplaceTextBlockAsync_rejects_a_fifo_instead_of_blocking_forever()
    {

        Skip.If(OperatingSystem.IsWindows(), "mkfifo is a POSIX primitive.");

        string fifoPath = Path.Combine(_workspace.Root, "pipe");

        using (System.Diagnostics.Process? mkfifo = System.Diagnostics.Process.Start("mkfifo", fifoPath))
        {

            Skip.If(mkfifo is null, "mkfifo is unavailable on this host.");

            await mkfifo!.WaitForExitAsync();

            Skip.If(mkfifo.ExitCode != 0, "mkfifo failed on this host.");

        }

        Assert.True(File.Exists(fifoPath));

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Task<Result<TextBlockReplaceResult>> replace = writer.ReplaceTextBlockAsync(
            workspace, "pipe", "a", "b", null, CancellationToken.None);

        Task completed = await Task.WhenAny(replace, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(replace, completed);

        Result<TextBlockReplaceResult> result = await replace;

        Assert.True(result.IsFailure);

    }

    [Fact]
    public async Task ReplaceTextBlockAsync_rejects_a_target_beyond_the_read_size_limit()
    {

        string path = Path.Combine(_workspace.Root, "huge.txt");

        string original = new string('a', 1024 * 1024) + "needle";

        await File.WriteAllTextAsync(path, original);

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<TextBlockReplaceResult> result = await writer.ReplaceTextBlockAsync(
            workspace,
            "huge.txt",
            "needle",
            "found",
            expectedReplacements: null,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Workspace.FileTooLarge, result.Error.Code);

        Assert.Equal(original, await File.ReadAllTextAsync(path));

    }

    /// <summary>
    /// The read side strips the BOM before returning FileReadResult.Content and the DTO's encoding field is
    /// hardcoded to "utf-8", so a read-modify-write through GET then PUT dropped the destination's preamble:
    /// the file silently lost three leading bytes and git showed a diff the user never made. The sibling
    /// write path on the same resource, ReplaceTextBlockAsync, has always re-applied it.
    /// </summary>
    [Fact]
    public async Task WriteFileAsync_preserves_an_existing_utf8_bom()
    {

        string path = Path.Combine(_workspace.Root, "Program.cs");

        await File.WriteAllBytesAsync(path, [.. Utf8Bom, .. "// old"u8]);

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileWriteResult> result = await writer.WriteFileAsync(workspace, "Program.cs", "// new", CancellationToken.None);

        Assert.True(result.IsSuccess);

        byte[] expected = [.. Utf8Bom, .. "// new"u8];

        Assert.Equal(expected, await File.ReadAllBytesAsync(path));

        Assert.Equal(expected.LongLength, result.Value!.BytesWritten);

    }

    /// <summary>
    /// Re-applying the preamble unconditionally would double it whenever the caller's own content already
    /// begins with U+FEFF, because Encoding.UTF8.GetBytes encodes that character as EF BB BF itself.
    /// </summary>
    [Fact]
    public async Task WriteFileAsync_does_not_double_a_bom_the_caller_already_supplied()
    {

        string path = Path.Combine(_workspace.Root, "Program.cs");

        await File.WriteAllBytesAsync(path, [.. Utf8Bom, .. "// old"u8]);

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileWriteResult> result = await writer.WriteFileAsync(workspace, "Program.cs", "﻿// new", CancellationToken.None);

        Assert.True(result.IsSuccess);

        byte[] expected = [.. Utf8Bom, .. "// new"u8];

        Assert.Equal(expected, await File.ReadAllBytesAsync(path));

    }

    [Fact]
    public async Task WriteFileAsync_does_not_add_a_bom_to_a_destination_that_had_none()
    {

        string path = Path.Combine(_workspace.Root, "plain.txt");

        await File.WriteAllBytesAsync(path, "old"u8.ToArray());

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileWriteResult> result = await writer.WriteFileAsync(workspace, "plain.txt", "new", CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal("new"u8.ToArray(), await File.ReadAllBytesAsync(path));

    }

    /// <summary>
    /// The replace path decoded the target with the replacing UTF-8 decoder, so every byte that is not
    /// valid UTF-8 became U+FFFD and the atomic rewrite persisted EF BF BD over the original bytes with
    /// a 200 and no warning. The sibling read paths (PhysicalFileSystemBrowser, SandboxedFileIo) already
    /// fail closed on invalid UTF-8; only this write path was lossy, and it is the one that persists.
    /// </summary>
    [Fact]
    public async Task ReplaceTextBlockAsync_rejects_a_target_that_is_not_valid_utf8_instead_of_corrupting_it()
    {

        string path = Path.Combine(_workspace.Root, "legacy.cs");

        byte[] original = [.. "// caf"u8, 0xE9, .. " TODO"u8];

        await File.WriteAllBytesAsync(path, original);

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<TextBlockReplaceResult> result = await writer.ReplaceTextBlockAsync(
            workspace,
            "legacy.cs",
            "TODO",
            "DONE",
            expectedReplacements: null,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Workspace.PathNotAllowed, result.Error.Code);

        Assert.Equal(original, await File.ReadAllBytesAsync(path));

    }

    /// <summary>
    /// A NUL byte marks the target as binary rather than text; the ordinal search can still match an
    /// ASCII run inside it, so the replace would rewrite a binary file as decoded text. WorkspaceTextFile
    /// already rejects binary payloads on the MCP coding-tool path; this path now matches it.
    /// </summary>
    [Fact]
    public async Task ReplaceTextBlockAsync_rejects_a_binary_target_instead_of_rewriting_it()
    {

        string path = Path.Combine(_workspace.Root, "blob.bin");

        byte[] original = [.. "TODO"u8, 0x00, 0x01, 0x02];

        await File.WriteAllBytesAsync(path, original);

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<TextBlockReplaceResult> result = await writer.ReplaceTextBlockAsync(
            workspace,
            "blob.bin",
            "TODO",
            "DONE",
            expectedReplacements: null,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Workspace.PathNotAllowed, result.Error.Code);

        Assert.Equal(original, await File.ReadAllBytesAsync(path));

    }

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private static PhysicalFileSystemWriter CreateWriter() =>
        CreateWriter(new ArcanumSettings { Workspaces = new WorkspaceSettings { EnableFileWrite = true } });

    private static PhysicalFileSystemWriter CreateWriter(ArcanumSettings settings) =>
        new(new TestOptionsSnapshot<ArcanumSettings>(settings));

    private WorkspaceInfo MakeWorkspace() =>
        new("id", "test", _workspace.Root, WorkspaceType.Campaign, DateTimeOffset.UtcNow);

}
