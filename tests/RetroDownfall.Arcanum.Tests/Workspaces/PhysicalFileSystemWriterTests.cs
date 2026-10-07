using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Workspaces;

[Collection("WorkspacePathPolicy")]
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

    [SkippableFact]
    public async Task WriteFileAsync_rejects_symlink_escape()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "Symlink-escape containment is exercised on Unix hosts.");

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
    [SkippableFact]
    public async Task WriteFileAsync_does_not_create_parent_directories_outside_workspace_through_a_symlinked_ancestor()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "Symlink-escape containment is exercised on Unix hosts.");

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

    [Theory]
    [InlineData(".git/hooks/pre-commit")]
    [InlineData(".git/config")]
    [InlineData(".GIT/hooks/pre-push")]
    [InlineData("nested/checkout/.git/config")]
    [InlineData(".arcanum/campaign.json")]
    public async Task WriteFileAsync_rejects_dot_git_paths(string relativePath)
    {
        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileWriteResult> result = await writer.WriteFileAsync(
            workspace,
            relativePath,
            "#!/bin/sh\necho planted\n",
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.PathNotAllowed", result.Error.Code);

        Assert.Contains("protected", result.Error.Message, StringComparison.OrdinalIgnoreCase);

        Assert.False(File.Exists(Path.Combine(_workspace.Root, relativePath)));

        Assert.False(Directory.Exists(Path.Combine(_workspace.Root, relativePath.Split('/')[0])));
    }

    [Theory]
    [InlineData(".git/config")]
    [InlineData(".arcanum/campaign.json")]
    public async Task ReplaceTextBlockAsync_rejects_dot_git_paths_and_leaves_the_file_untouched(
        string relativePath)
    {
        string absolute = _workspace.WriteFile(relativePath, "[core]\nbare = false\n");

        PhysicalFileSystemWriter writer = CreateWriter();

        Result<TextBlockReplaceResult> result = await writer.ReplaceTextBlockAsync(
            MakeWorkspace(),
            relativePath,
            "bare = false",
            "fsmonitor = /tmp/payload",
            expectedReplacements: null,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.PathNotAllowed", result.Error.Code);

        Assert.Equal("[core]\nbare = false\n", await File.ReadAllTextAsync(absolute));
    }

    [Theory]
    [InlineData(".git/hooks/pre-commit", false)]
    [InlineData(".git", true)]
    [InlineData(".arcanum", true)]
    public async Task DeleteAsync_rejects_dot_git_paths_and_removes_nothing(
        string relativePath,
        bool recursive)
    {
        string absolute = _workspace.WriteFile(
            relativePath == ".git" || relativePath == ".arcanum"
                ? Path.Combine(relativePath, "marker.txt")
                : relativePath,
            "keep");

        PhysicalFileSystemWriter writer = CreateWriter();

        Result<FileDeleteResult> result = await writer.DeleteAsync(
            MakeWorkspace(),
            relativePath,
            recursive,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.PathNotAllowed", result.Error.Code);

        Assert.True(File.Exists(absolute));
    }

    /// <summary>
    /// On a Windows host the NTFS stream suffix and the 8.3 short name reach <c>.git</c> through the real
    /// filesystem. The platform-seam theories in <c>WorkspaceProtectedPathsTests</c> pin the matching logic
    /// on every host; this lane exercises it end to end, and it is not run on macOS or Linux. NTFS gives a
    /// short name only to an entry that exists, so the fixture creates <c>.git/hooks</c> and <c>.arcanum</c>
    /// first (on a volume with 8.3 generation enabled, <c>GIT~1</c> and <c>ARCANU~1</c> then name them).
    /// Whatever code the request is refused with, nothing may be planted: neither inside the protected
    /// directories nor as a literal short-name entry beside them.
    /// </summary>
    [SkippableTheory]
    [InlineData(".git::$INDEX_ALLOCATION/hooks/pre-commit")]
    [InlineData("GIT~1/hooks/pre-commit")]
    [InlineData("ARCANU~1/campaign.json")]
    public async Task Windows_lane_alias_spellings_of_protected_metadata_are_refused_and_plant_nothing(
        string relativePath)
    {
        Skip.IfNot(
            OperatingSystem.IsWindows(),
            "NTFS stream suffixes and 8.3 short names are Windows filesystem behaviours.");

        string hooks = _workspace.CreateSubdir(Path.Combine(".git", "hooks"));

        string arcanum = _workspace.CreateSubdir(".arcanum");

        Result<FileWriteResult> result = await CreateWriter().WriteFileAsync(
            MakeWorkspace(),
            relativePath,
            "#!/bin/sh\necho planted\n",
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.False(File.Exists(Path.Combine(hooks, "pre-commit")));

        Assert.False(File.Exists(Path.Combine(arcanum, "campaign.json")));

        // Enumeration returns long names, so a literal short-name entry shows up here, where
        // Directory.Exists("GIT~1") would resolve the alias to .git and say nothing about it.
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(_workspace.Root).Select(Path.GetFileName),
            static name => name is "GIT~1" or "ARCANU~1");
    }

    /// <summary>
    /// A recursive delete whose own path is not protected still removes everything under it, so a nested
    /// checkout's <c>.git</c> (protected at any depth) must stop it before anything is deleted, not be
    /// removed as a side effect of deleting a parent.
    /// </summary>
    [Theory]
    [InlineData("vendored/checkout")]
    [InlineData("vendored")]
    public async Task DeleteAsync_recursive_over_a_nested_dot_git_removes_nothing(string relativePath)
    {
        string config = _workspace.WriteFile("vendored/checkout/.git/config", "[core]\n");

        string source = _workspace.WriteFile("vendored/checkout/a.txt", "keep");

        string sibling = _workspace.WriteFile("vendored/other/b.txt", "keep");

        Result<FileDeleteResult> result = await CreateWriter().DeleteAsync(
            MakeWorkspace(),
            relativePath,
            recursive: true,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.PathNotAllowed", result.Error.Code);

        Assert.Contains(".git", result.Error.Message, StringComparison.Ordinal);

        Assert.True(File.Exists(config));

        Assert.True(File.Exists(source));

        Assert.True(File.Exists(sibling));
    }

    [Fact]
    public async Task DeleteAsync_recursive_over_a_nested_dot_git_proceeds_when_the_operator_allows_protected_paths()
    {
        string config = _workspace.WriteFile("vendored/checkout/.git/config", "[core]\n");

        PhysicalFileSystemWriter writer = CreateWriter(
            new ArcanumSettings
            {
                Workspaces = new WorkspaceSettings
                {
                    EnableFileWrite = true,
                    AllowProtectedPathWrites = true,
                },
            });

        Result<FileDeleteResult> result = await writer.DeleteAsync(
            MakeWorkspace(),
            "vendored",
            recursive: true,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.False(File.Exists(config));

        Assert.False(Directory.Exists(Path.Combine(_workspace.Root, "vendored")));
    }

    [Fact]
    public async Task DeleteAsync_recursive_over_an_ordinary_tree_still_removes_it()
    {
        _workspace.WriteFile("plain/deep/a.txt", "x");

        _workspace.WriteFile("plain/.github/workflows/build.yml", "x");

        Result<FileDeleteResult> result = await CreateWriter().DeleteAsync(
            MakeWorkspace(),
            "plain",
            recursive: true,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.False(Directory.Exists(Path.Combine(_workspace.Root, "plain")));
    }

    [Theory]
    [InlineData(".git/hooks")]
    [InlineData(".arcanum/state")]
    public async Task CreateDirectoryAsync_rejects_dot_git_paths(string relativePath)
    {
        PhysicalFileSystemWriter writer = CreateWriter();

        Result<DirectoryCreateResult> result = await writer.CreateDirectoryAsync(
            MakeWorkspace(),
            relativePath,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.PathNotAllowed", result.Error.Code);

        Assert.False(Directory.Exists(Path.Combine(_workspace.Root, relativePath.Split('/')[0])));
    }

    [Fact]
    public async Task Every_write_route_accepts_protected_paths_when_the_operator_allows_them()
    {
        PhysicalFileSystemWriter writer = CreateWriter(
            new ArcanumSettings
            {
                Workspaces = new WorkspaceSettings
                {
                    EnableFileWrite = true,
                    AllowProtectedPathWrites = true,
                },
            });

        WorkspaceInfo workspace = MakeWorkspace();

        Result<FileWriteResult> put = await writer.WriteFileAsync(
            workspace,
            ".git/hooks/pre-commit",
            "#!/bin/sh\necho operator-approved\n",
            CancellationToken.None);

        Assert.True(put.IsSuccess, put.IsFailure ? put.Error.Message : null);

        Assert.Equal(
            "#!/bin/sh\necho operator-approved\n",
            await File.ReadAllTextAsync(Path.Combine(_workspace.Root, ".git", "hooks", "pre-commit")));

        Result<TextBlockReplaceResult> patch = await writer.ReplaceTextBlockAsync(
            workspace,
            ".git/hooks/pre-commit",
            "operator-approved",
            "still-approved",
            expectedReplacements: null,
            CancellationToken.None);

        Assert.True(patch.IsSuccess, patch.IsFailure ? patch.Error.Message : null);

        Result<DirectoryCreateResult> mkdir = await writer.CreateDirectoryAsync(
            workspace,
            ".arcanum/state",
            CancellationToken.None);

        Assert.True(mkdir.IsSuccess, mkdir.IsFailure ? mkdir.Error.Message : null);

        Assert.True(Directory.Exists(Path.Combine(_workspace.Root, ".arcanum", "state")));

        Result<FileDeleteResult> delete = await writer.DeleteAsync(
            workspace,
            ".git/hooks/pre-commit",
            recursive: false,
            CancellationToken.None);

        Assert.True(delete.IsSuccess, delete.IsFailure ? delete.Error.Message : null);

        Assert.False(File.Exists(Path.Combine(_workspace.Root, ".git", "hooks", "pre-commit")));
    }

    [Fact]
    public async Task Allowing_protected_paths_does_not_replace_the_file_write_toggle()
    {
        PhysicalFileSystemWriter writer = CreateWriter(
            new ArcanumSettings
            {
                Workspaces = new WorkspaceSettings
                {
                    EnableFileWrite = false,
                    AllowProtectedPathWrites = true,
                },
            });

        Result<FileWriteResult> result = await writer.WriteFileAsync(
            MakeWorkspace(),
            ".git/hooks/pre-commit",
            "#!/bin/sh\n",
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.FileWriteDisabled", result.Error.Code);

        Assert.False(Directory.Exists(Path.Combine(_workspace.Root, ".git")));
    }

    /// <summary>
    /// A committed in-workspace link such as <c>docs/hooks -> ../.git/hooks</c> contains no protected
    /// segment in the spelling the caller supplies and stays inside the workspace, so only the
    /// canonical-location branch of <c>WorkspaceProtectedPaths.IsProtectedPath</c> refuses it.
    /// </summary>
    [SkippableTheory]
    [InlineData(".git/hooks")]
    [InlineData(".arcanum/state")]
    public async Task WriteFileAsync_through_an_in_workspace_link_into_protected_metadata_is_rejected(
        string protectedDirectory)
    {
        string protectedPath = CreateLinkIntoProtectedDirectory(protectedDirectory, out string linkedDirectory);

        string existing = Path.Combine(protectedPath, "existing");

        await File.WriteAllTextAsync(existing, "keep");

        PhysicalFileSystemWriter writer = CreateWriter();

        Result<FileWriteResult> created = await writer.WriteFileAsync(
            MakeWorkspace(),
            $"docs/{Path.GetFileName(linkedDirectory)}/pre-commit",
            "#!/bin/sh\necho planted\n",
            CancellationToken.None);

        Assert.True(created.IsFailure);

        Assert.Equal("Workspace.PathNotAllowed", created.Error.Code);

        Assert.Contains("protected", created.Error.Message, StringComparison.OrdinalIgnoreCase);

        Assert.False(File.Exists(Path.Combine(protectedPath, "pre-commit")));

        Result<FileWriteResult> overwritten = await writer.WriteFileAsync(
            MakeWorkspace(),
            $"docs/{Path.GetFileName(linkedDirectory)}/existing",
            "overwritten",
            CancellationToken.None);

        Assert.True(overwritten.IsFailure);

        Assert.Equal("Workspace.PathNotAllowed", overwritten.Error.Code);

        Assert.Equal("keep", await File.ReadAllTextAsync(existing));
    }

    [SkippableFact]
    public async Task ReplaceTextBlockAsync_through_an_in_workspace_link_into_dot_git_is_rejected()
    {
        string hooks = CreateLinkIntoProtectedDirectory(".git/hooks", out string linkedDirectory);

        string existing = Path.Combine(hooks, "pre-commit");

        await File.WriteAllTextAsync(existing, "echo before\n");

        Result<TextBlockReplaceResult> result = await CreateWriter().ReplaceTextBlockAsync(
            MakeWorkspace(),
            $"docs/{Path.GetFileName(linkedDirectory)}/pre-commit",
            "before",
            "planted",
            expectedReplacements: null,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.PathNotAllowed", result.Error.Code);

        Assert.Equal("echo before\n", await File.ReadAllTextAsync(existing));
    }

    [SkippableFact]
    public async Task DeleteAsync_through_an_in_workspace_link_into_dot_git_is_rejected()
    {
        string hooks = CreateLinkIntoProtectedDirectory(".git/hooks", out string linkedDirectory);

        string existing = Path.Combine(hooks, "pre-commit");

        await File.WriteAllTextAsync(existing, "keep");

        Result<FileDeleteResult> result = await CreateWriter().DeleteAsync(
            MakeWorkspace(),
            $"docs/{Path.GetFileName(linkedDirectory)}/pre-commit",
            recursive: false,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.PathNotAllowed", result.Error.Code);

        Assert.True(File.Exists(existing));
    }

    [SkippableFact]
    public async Task CreateDirectoryAsync_through_an_in_workspace_link_into_dot_git_is_rejected()
    {
        string hooks = CreateLinkIntoProtectedDirectory(".git/hooks", out string linkedDirectory);

        Result<DirectoryCreateResult> result = await CreateWriter().CreateDirectoryAsync(
            MakeWorkspace(),
            $"docs/{Path.GetFileName(linkedDirectory)}/planted",
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.PathNotAllowed", result.Error.Code);

        Assert.False(Directory.Exists(Path.Combine(hooks, "planted")));
    }

    /// <summary>
    /// <c>ws/pending -> missing-dir</c> is an in-workspace link whose target does not exist, so containment
    /// accepts the path but <c>mkdir</c> cannot create through it. The write route names that as
    /// <c>Workspace.PathIsFile</c> (an existing non-directory entry where a directory is needed) instead of a
    /// generic 500 I/O failure, and creates nothing at the link's target.
    /// </summary>
    [SkippableFact]
    public async Task WriteFileAsync_through_a_dangling_in_workspace_link_is_refused_as_path_is_file()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "Symlink creation requires elevation on Windows.");

        Directory.CreateSymbolicLink(Path.Combine(_workspace.Root, "pending"), "missing-dir");

        Result<FileWriteResult> result = await CreateWriter().WriteFileAsync(
            MakeWorkspace(),
            "pending/deeper/new.txt",
            "content",
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.PathIsFile", result.Error.Code);

        Assert.False(Directory.Exists(Path.Combine(_workspace.Root, "missing-dir")));
    }

    /// <summary>
    /// The directory route already refuses the dangling link itself (an existing entry where the directory
    /// would go); a directory spelled below it reached the generic I/O failure instead.
    /// </summary>
    [SkippableTheory]
    [InlineData("pending")]
    [InlineData("pending/child")]
    public async Task CreateDirectoryAsync_at_or_below_a_dangling_in_workspace_link_is_refused_as_path_is_file(
        string relativePath)
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "Symlink creation requires elevation on Windows.");

        Directory.CreateSymbolicLink(Path.Combine(_workspace.Root, "pending"), "missing-dir");

        Result<DirectoryCreateResult> result = await CreateWriter().CreateDirectoryAsync(
            MakeWorkspace(),
            relativePath,
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.PathIsFile", result.Error.Code);

        Assert.False(Directory.Exists(Path.Combine(_workspace.Root, "missing-dir")));
    }

    [Fact]
    public async Task WriteFileAsync_below_an_existing_file_is_refused_as_path_is_file()
    {
        string notes = _workspace.WriteFile("notes.txt", "notes");

        Result<FileWriteResult> result = await CreateWriter().WriteFileAsync(
            MakeWorkspace(),
            "notes.txt/new.txt",
            "content",
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.PathIsFile", result.Error.Code);

        Assert.Equal("notes", await File.ReadAllTextAsync(notes));
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

    /// <summary>
    /// The block limits bound oldString and newString, not what they make together: a short oldString that
    /// occurs many times and a long newString can build a result many times MaxFileWriteSizeBytes. The
    /// projected size is checked before the replacement is built, and nothing is written.
    /// </summary>
    [Fact]
    public async Task ReplaceTextBlockAsync_refuses_a_result_larger_than_MaxFileWriteSizeBytes_and_leaves_the_file_untouched()
    {
        long maxWriteBytes = ArcanumSettingClamps.MaxFileWriteSizeBytes(
            ArcanumRuntimeDefaults.WorkspaceMaxFileWriteSizeBytes);

        string newString = new('y', 200 * 1024);

        int occurrences = checked((int)(maxWriteBytes / newString.Length) + 2);

        string original = string.Concat(Enumerable.Repeat("x\n", occurrences));

        string path = _workspace.WriteFile("target.txt", original);

        Result<TextBlockReplaceResult> result = await CreateWriter().ReplaceTextBlockAsync(
            MakeWorkspace(), "target.txt", "x", newString, occurrences, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.FileTooLarge", result.Error.Code);

        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    /// <summary>
    /// A result far past what a string can hold made string.Replace throw OutOfMemoryException out of the
    /// writer as an unhandled failure; the size is now refused before anything is allocated.
    /// </summary>
    [Fact]
    public async Task ReplaceTextBlockAsync_refuses_a_result_no_string_could_hold_as_FileTooLarge()
    {
        const int Occurrences = 600_000;

        string path = _workspace.WriteFile("target.txt", new string('x', Occurrences));

        Result<TextBlockReplaceResult> result = await CreateWriter().ReplaceTextBlockAsync(
            MakeWorkspace(), "target.txt", "x", new string('y', 400 * 1024), Occurrences, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.FileTooLarge", result.Error.Code);

        Assert.Equal(Occurrences, new FileInfo(path).Length);
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
    public async Task ReplaceTextBlockAsync_rejects_destination_modified_between_read_and_replace()
    {
        string target = _workspace.WriteFile("target.txt", "alpha beta gamma");

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        // An in-place edit by someone else (same inode, same length) lands after our read and before
        // the replace begins; identity-only revalidation cannot see it.
        PhysicalFileSystemWriter.AfterReplaceTextBlockReadForTests = path =>
        {
            Assert.Equal(Path.GetFullPath(target), Path.GetFullPath(path));

            using FileStream stream = new(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

            stream.Write("alpha OMGA gamma"u8);
        };

        try
        {
            Result<TextBlockReplaceResult> result = await writer.ReplaceTextBlockAsync(
                workspace, "target.txt", "beta", "BETA!", null, CancellationToken.None);

            Assert.True(result.IsFailure);

            // The edit is the caller's to re-read and retry, so it is a conflict (409), not a server
            // fault (Workspace.WriteFailed, 500), and the message tells the caller what to do.
            Assert.Equal("Workspace.FileChanged", result.Error.Code);

            Assert.Contains("changed after it was read", result.Error.Message, StringComparison.Ordinal);

            Assert.Contains("Re-read the file and retry", result.Error.Message, StringComparison.Ordinal);

            Assert.Equal("alpha OMGA gamma", await File.ReadAllTextAsync(target));

            Assert.Empty(Directory.GetFiles(_workspace.Root, ".arcanum-*"));
        }
        finally
        {
            PhysicalFileSystemWriter.AfterReplaceTextBlockReadForTests = null;
        }
    }

    [Fact]
    public async Task ReplaceTextBlockAsync_rejects_destination_deleted_between_read_and_replace()
    {
        string target = _workspace.WriteFile("target.txt", "alpha beta gamma");

        PhysicalFileSystemWriter writer = CreateWriter();

        WorkspaceInfo workspace = MakeWorkspace();

        PhysicalFileSystemWriter.AfterReplaceTextBlockReadForTests = path => File.Delete(path);

        try
        {
            Result<TextBlockReplaceResult> result = await writer.ReplaceTextBlockAsync(
                workspace, "target.txt", "beta", "BETA!", null, CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal("Workspace.FileChanged", result.Error.Code);

            Assert.Contains("changed after it was read", result.Error.Message, StringComparison.Ordinal);

            // The destination stays deleted: the replace must not resurrect it from the stale read.
            Assert.False(File.Exists(target));

            Assert.Empty(Directory.GetFiles(_workspace.Root, ".arcanum-*"));
        }
        finally
        {
            PhysicalFileSystemWriter.AfterReplaceTextBlockReadForTests = null;
        }
    }

    [Fact]
    public async Task ReplaceTextBlockAsync_still_replaces_when_nothing_changed_after_the_read()
    {
        string target = _workspace.WriteFile("target.txt", "alpha beta gamma");

        PhysicalFileSystemWriter writer = CreateWriter();

        Result<TextBlockReplaceResult> result = await writer.ReplaceTextBlockAsync(
            MakeWorkspace(), "target.txt", "beta", "BETA!", null, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal("alpha BETA! gamma", await File.ReadAllTextAsync(target));
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

    [SkippableFact]
    public async Task ReplaceTextBlockAsync_write_failure_leaves_no_temp_file_and_original_content_intact()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "Unix owner-only mode bits are what makes the directory unreadable here.");

        // Dead once Skip.If above has run, but kept so the platform-compatibility analyzer still
        // recognizes the guard clause protecting the Unix-only calls below.
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

    [SkippableFact]
    public async Task DeleteAsync_recursive_with_escaping_link_deletes_nothing_and_names_the_link()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "Symlink-escape containment is exercised on Unix hosts.");

        string outsideDir = Path.Combine(Path.GetTempPath(), $"arcanum-outside-{Guid.NewGuid():N}");

        Directory.CreateDirectory(outsideDir);

        try
        {
            string outsideFile = Path.Combine(outsideDir, "target.txt");

            await File.WriteAllTextAsync(outsideFile, "outside secret");

            string parentDir = _workspace.CreateSubdir("recurseDelete");

            _workspace.WriteFile("recurseDelete/keep.txt", "keep me");

            _workspace.WriteFile("recurseDelete/nested/deep.txt", "keep me too");

            string linkPath = Path.Combine(parentDir, "nested", "escape-link");

            File.CreateSymbolicLink(linkPath, outsideFile);

            PhysicalFileSystemWriter writer = CreateWriter();

            WorkspaceInfo workspace = MakeWorkspace();

            Result<FileDeleteResult> result = await writer.DeleteAsync(workspace, "recurseDelete", recursive: true, CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal("Workspace.SymbolicLinkEscape", result.Error.Code);

            Assert.Contains(
                Path.Combine("recurseDelete", "nested", "escape-link"),
                result.Error.Message.Replace('/', Path.DirectorySeparatorChar),
                StringComparison.Ordinal);

            Assert.Contains("nothing was deleted", result.Error.Message, StringComparison.OrdinalIgnoreCase);

            Assert.True(File.Exists(outsideFile));

            Assert.True(File.Exists(Path.Combine(parentDir, "keep.txt")));

            Assert.True(File.Exists(Path.Combine(parentDir, "nested", "deep.txt")));

            Assert.NotNull(new FileInfo(linkPath).LinkTarget);
        }
        finally
        {
            Directory.Delete(outsideDir, recursive: true);
        }
    }

    [SkippableFact]
    public async Task DeleteAsync_recursive_removes_a_link_that_stays_inside_the_workspace_without_following_it()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "Symlink handling is exercised on Unix hosts.");

        string keep = _workspace.WriteFile("kept/target.txt", "stay");

        _workspace.WriteFile("doomed/file.txt", "go");

        File.CreateSymbolicLink(Path.Combine(_workspace.Root, "doomed", "inside-link"), keep);

        PhysicalFileSystemWriter writer = CreateWriter();

        Result<FileDeleteResult> result = await writer.DeleteAsync(MakeWorkspace(), "doomed", recursive: true, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.False(Directory.Exists(Path.Combine(_workspace.Root, "doomed")));

        Assert.True(File.Exists(keep));
    }

    [SkippableFact]
    public async Task WriteFileAsync_to_symlink_returns_SymbolicLinkEscape()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "Symlink handling is exercised on Unix hosts.");

        string real = _workspace.WriteFile("real.txt", "real content");

        string link = Path.Combine(_workspace.Root, "alias.txt");

        File.CreateSymbolicLink(link, real);

        PhysicalFileSystemWriter writer = CreateWriter();

        Result<FileWriteResult> result = await writer.WriteFileAsync(
            MakeWorkspace(), "alias.txt", "overwritten", CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Workspace.SymbolicLinkEscape", result.Error.Code);

        Assert.Equal("real content", await File.ReadAllTextAsync(real));

        Assert.NotNull(new FileInfo(link).LinkTarget);
    }

    [SkippableFact]
    public async Task WriteFileAsync_to_a_hard_linked_destination_returns_SymbolicLinkEscape()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "Hard-link creation is exercised on Unix hosts.");

        string real = _workspace.WriteFile("original.txt", "original");

        string outsideAlias = Path.Combine(Path.GetTempPath(), $"arcanum-hardlink-{Guid.NewGuid():N}.txt");

        try
        {
            Assert.True(HardLinkTestSupport.TryCreate(outsideAlias, real));

            PhysicalFileSystemWriter writer = CreateWriter();

            Result<FileWriteResult> result = await writer.WriteFileAsync(
                MakeWorkspace(), "original.txt", "overwritten", CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal("Workspace.SymbolicLinkEscape", result.Error.Code);

            Assert.Equal("original", await File.ReadAllTextAsync(real));
        }
        finally
        {
            File.Delete(outsideAlias);
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

    [SkippableFact]
    public async Task WriteFileAsync_UnauthorizedAccessException_maps_to_AccessDenied()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "Unix owner-only mode bits are what makes the directory unreadable here.");

        // Dead once Skip.If above has run, but kept so the platform-compatibility analyzer still
        // recognizes the guard clause protecting the Unix-only calls below.
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

    /// <summary>
    /// R-007, replace path: the post-open check asks the kernel where the handle lives. A handle on a file
    /// outside the workspace is rejected even when the stream reports a name inside it and the expected
    /// identity was captured from that outside file (the swapped-link TOCTOU shape).
    /// </summary>
    [Fact]
    public void IsOpenedReadHandleContained_rejects_outside_handle_whose_reported_name_is_inside()
    {
        string outside = Path.Combine(
            Path.GetDirectoryName(_workspace.Root)!,
            $"outside-{Guid.NewGuid():N}.txt");

        File.WriteAllText(outside, "outside secret");

        try
        {
            Assert.True(FileHandleIdentityInterop.TryGetPathIdentity(outside, out FileHandleIdentity outsideIdentity));

            using FileStream stream = new ReportedNameFileStream(
                outside,
                Path.Combine(_workspace.Root, "looks-inside.txt"));

            Assert.False(PhysicalFileSystemWriter.IsOpenedReadHandleContained(
                _workspace.Root,
                stream,
                outsideIdentity));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void IsOpenedReadHandleContained_accepts_inside_handle_with_matching_identity()
    {
        string inside = _workspace.WriteFile("inside.txt", "ok");

        Assert.True(FileHandleIdentityInterop.TryGetPathIdentity(inside, out FileHandleIdentity identity));

        using FileStream stream = new(inside, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        Assert.True(PhysicalFileSystemWriter.IsOpenedReadHandleContained(_workspace.Root, stream, identity));
    }

    private sealed class ReportedNameFileStream(string path, string reportedName)
        : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)
    {
        public override string Name => reportedName;
    }

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// Creates the protected directory and a committed-style relative link <c>docs/linked</c> that points at
    /// it, and returns the protected directory's absolute path. Skips on hosts where an unprivileged
    /// symbolic link cannot be created.
    /// </summary>
    private string CreateLinkIntoProtectedDirectory(
        string protectedDirectory,
        out string linkedDirectory)
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "This asserts POSIX symbolic-link behaviour and runs on macOS and Linux only.");

        string protectedPath = Path.Combine(
            _workspace.Root,
            protectedDirectory.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(protectedPath);

        linkedDirectory = Path.Combine(_workspace.CreateSubdir("docs"), "linked");

        Directory.CreateSymbolicLink(
            linkedDirectory,
            Path.Combine("..", protectedDirectory.Replace('/', Path.DirectorySeparatorChar)));

        return protectedPath;
    }

    private static PhysicalFileSystemWriter CreateWriter() =>
        CreateWriter(new ArcanumSettings { Workspaces = new WorkspaceSettings { EnableFileWrite = true } });

    private static PhysicalFileSystemWriter CreateWriter(ArcanumSettings settings) =>
        new(new TestOptionsSnapshot<ArcanumSettings>(settings));

    private WorkspaceInfo MakeWorkspace() =>
        new("id", "test", _workspace.Root, WorkspaceType.Campaign, DateTimeOffset.UtcNow);
}
