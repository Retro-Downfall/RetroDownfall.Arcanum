using Microsoft.Win32.SafeHandles;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Mcp;

[Collection("WorkspacePathPolicy")]
public sealed class SandboxedFileIoTests : IAsyncLifetime
{
    private TempWorkspace _workspace = null!;

    private string _outsideFile = null!;

    private readonly List<string> _outsideDirectories = [];

    public async Task InitializeAsync()
    {
        _workspace = new TempWorkspace();

        await _workspace.InitializeAsync();

        _outsideFile = Path.Combine(Path.GetTempPath(), $"arcanum-outside-{Guid.NewGuid():N}.txt");

        await File.WriteAllTextAsync(_outsideFile, "outside secret");
    }

    public async Task DisposeAsync()
    {
        FileHandleIdentityInterop.TryGetPathIdentityForTests = null;

        FileHandleIdentityInterop.TryGetHandleIdentityForTests = null;

        FileHandleIdentityInterop.TryGetPathMetadataForTests = null;

        FileHandleIdentityInterop.TryGetHandleMetadataForTests = null;

        SecureFileReader.AfterOpenForTests = null;

        SandboxedFileIo.AfterCreateParentDirectoryForTests = null;

        WorkspacePathPolicy.ResetTestSeams();

        if (File.Exists(_outsideFile))
        {
            File.Delete(_outsideFile);
        }

        foreach (string outside in _outsideDirectories)
        {
            if (Directory.Exists(outside))
            {
                Directory.Delete(outside, recursive: true);
            }
        }

        await _workspace.DisposeAsync();
    }

    [SkippableFact]
    public void TryOpenForRead_rejects_symlink_to_outside_workspace()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "This asserts POSIX behaviour and runs on macOS and Linux only.");

        string linkPath = Path.Combine(_workspace.Root, "escape-link.txt");

        if (File.Exists(linkPath))
        {
            File.Delete(linkPath);
        }

        File.CreateSymbolicLink(linkPath, _outsideFile);

        bool opened = SandboxedFileIo.TryOpenForRead(
            _workspace.Root,
            linkPath,
            out _,
            out _);

        Assert.False(opened);
    }

    [Fact]
    public void TryOpenForRead_rejects_path_outside_workspace()
    {
        bool opened = SandboxedFileIo.TryOpenForRead(
            _workspace.Root,
            _outsideFile,
            out FileStream? stream,
            out McpToolsCallResultWire? error);

        Assert.False(opened);

        Assert.Null(stream);

        AssertSandboxError(error);
    }

    /// <summary>
    /// R-007, read side: <c>d -> b/sub</c> spelled through <c>b -> ../outside</c> must not open the outside file.
    /// </summary>
    [SkippableFact]
    public void TryOpenForRead_rejects_file_reached_through_chained_escaping_link()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "This asserts POSIX behaviour and runs on macOS and Linux only.");

        string outside = CreateOutsideDirectory();

        Directory.CreateDirectory(Path.Combine(outside, "sub"));

        File.WriteAllText(Path.Combine(outside, "sub", "secret.txt"), "outside secret");

        Directory.CreateSymbolicLink(
            Path.Combine(_workspace.Root, "b"),
            Path.Combine("..", Path.GetFileName(outside)));

        Directory.CreateSymbolicLink(
            Path.Combine(_workspace.Root, "d"),
            Path.Combine("b", "sub"));

        bool opened = SandboxedFileIo.TryOpenForRead(
            _workspace.Root,
            Path.Combine(_workspace.Root, "d", "secret.txt"),
            out FileStream? stream,
            out McpToolsCallResultWire? error);

        Assert.False(opened);

        Assert.Null(stream);

        AssertSandboxError(error);
    }

    [Fact]
    public void TryOpenForRead_rejects_missing_file_when_identity_cannot_be_resolved()
    {
        string missing = Path.Combine(_workspace.Root, "missing.txt");

        bool opened = SandboxedFileIo.TryOpenForRead(
            _workspace.Root,
            missing,
            out FileStream? stream,
            out McpToolsCallResultWire? error);

        Assert.False(opened);

        Assert.Null(stream);

        Assert.False(File.Exists(missing));

        AssertSandboxError(error);
    }

    [Fact]
    public async Task TryWriteAllTextAtomicallyAsync_writes_inside_workspace()
    {
        string target = Path.Combine(_workspace.Root, "atomic.txt");

        (bool success, _) = await SandboxedFileIo.TryWriteAllTextAtomicallyAsync(
            _workspace.Root,
            target,
            "atomic content",
            CancellationToken.None);

        Assert.True(success);

        Assert.Equal("atomic content", await File.ReadAllTextAsync(target));
    }

    [Theory]
    [InlineData(".git/hooks/pre-commit")]
    [InlineData(".git/config")]
    [InlineData(".GIT/hooks/pre-push")]
    [InlineData(".arcanum/campaign.json")]
    public async Task Write_under_dot_git_is_rejected(string relativePath)
    {
        string target = Path.Combine(
            _workspace.Root,
            relativePath.Replace('/', Path.DirectorySeparatorChar));

        (bool success, McpToolsCallResultWire? error) = await SandboxedFileIo.TryWriteAllTextAtomicallyAsync(
            _workspace.Root,
            target,
            "#!/bin/sh\necho planted\n",
            CancellationToken.None);

        Assert.False(success);

        Assert.NotNull(error);

        Assert.True(error!.IsError);

        Assert.Contains(
            "protected",
            Assert.IsType<McpToolContentTextWire>(Assert.Single(error.Content!)).Text!,
            StringComparison.OrdinalIgnoreCase);

        Assert.False(File.Exists(target));

        Assert.False(Directory.Exists(Path.Combine(_workspace.Root, relativePath.Split('/')[0])));
    }

    [Fact]
    public async Task Write_with_a_read_baseline_refuses_a_destination_edited_in_place_after_the_read()
    {
        string target = _workspace.WriteFile("edited.txt", "alpha beta gamma");

        (string? content, McpToolsCallResultWire? readError, FileContentBaseline? baseline) =
            await SandboxedFileIo.TryReadAllTextForEditAsync(
                _workspace.Root,
                target,
                maxBytes: 4096,
                CancellationToken.None);

        Assert.Null(readError);

        Assert.Equal("alpha beta gamma", content);

        Assert.NotNull(baseline);

        // Same inode, same length: only the content differs.
        await using (FileStream stream = new(target, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            await stream.WriteAsync("alpha OMGA gamma"u8.ToArray());
        }

        (bool success, McpToolsCallResultWire? error) = await SandboxedFileIo.TryWriteAllTextAtomicallyAsync(
            _workspace.Root,
            target,
            "alpha BETA! gamma",
            CancellationToken.None,
            baseline);

        Assert.False(success);

        Assert.NotNull(error);

        Assert.Contains(
            "changed",
            Assert.IsType<McpToolContentTextWire>(Assert.Single(error!.Content!)).Text!,
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal("alpha OMGA gamma", await File.ReadAllTextAsync(target));

        Assert.Empty(Directory.GetFiles(_workspace.Root, ".arcanum-*"));
    }

    [Fact]
    public async Task Write_with_a_read_baseline_succeeds_when_the_destination_is_unchanged_and_ignores_the_preamble_reread()
    {
        string target = Path.Combine(_workspace.Root, "bom.txt");

        await File.WriteAllBytesAsync(target, [0xEF, 0xBB, 0xBF, .. "alpha beta"u8.ToArray()]);

        (string? content, _, FileContentBaseline? baseline) =
            await SandboxedFileIo.TryReadAllTextForEditAsync(
                _workspace.Root,
                target,
                maxBytes: 4096,
                CancellationToken.None);

        Assert.Equal("alpha beta", content);

        (bool success, _) = await SandboxedFileIo.TryWriteAllTextAtomicallyAsync(
            _workspace.Root,
            target,
            "alpha BETA!",
            CancellationToken.None,
            baseline);

        Assert.True(success);

        Assert.Equal(
            [0xEF, 0xBB, 0xBF, .. "alpha BETA!"u8.ToArray()],
            await File.ReadAllBytesAsync(target));
    }

    [Fact]
    public async Task Write_to_an_existing_dot_git_file_leaves_its_content_untouched()
    {
        string config = _workspace.WriteFile(".git/config", "[core]\n");

        (bool success, _) = await SandboxedFileIo.TryWriteAllTextAtomicallyAsync(
            _workspace.Root,
            config,
            "[core]\n\tfsmonitor = /tmp/payload\n",
            CancellationToken.None);

        Assert.False(success);

        Assert.Equal("[core]\n", await File.ReadAllTextAsync(config));
    }

    [Theory]
    [InlineData(".git/hooks/pre-commit")]
    [InlineData(".GIT/config")]
    [InlineData(".arcanum/campaign.json")]
    public async Task Write_under_a_protected_path_succeeds_when_the_operator_allows_protected_path_writes(
        string relativePath)
    {
        string target = Path.Combine(
            _workspace.Root,
            relativePath.Replace('/', Path.DirectorySeparatorChar));

        (bool success, McpToolsCallResultWire? error) = await SandboxedFileIo.TryWriteAllTextAtomicallyAsync(
            _workspace.Root,
            target,
            "operator-approved\n",
            CancellationToken.None,
            allowProtectedPathWrites: true);

        Assert.True(success);

        Assert.Null(error);

        Assert.Equal("operator-approved\n", await File.ReadAllTextAsync(target));
    }

    /// <summary>
    /// A committed in-workspace link such as <c>docs/hooks -> ../.git/hooks</c> is the realistic way past a
    /// lexical-only check: the spelling the model supplies contains no protected segment, and the link
    /// stays inside the workspace so containment alone accepts it. Only the canonical-location branch of
    /// <c>WorkspaceProtectedPaths.IsProtectedPath</c> stops the write.
    /// </summary>
    [SkippableTheory]
    [InlineData(".git/hooks")]
    [InlineData(".arcanum/state")]
    public async Task Write_through_an_in_workspace_link_into_protected_metadata_is_rejected(
        string protectedDirectory)
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "This asserts POSIX symbolic-link behaviour and runs on macOS and Linux only.");

        string protectedPath = Path.Combine(
            _workspace.Root,
            protectedDirectory.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(protectedPath);

        string existing = Path.Combine(protectedPath, "existing");

        await File.WriteAllTextAsync(existing, "keep");

        string linkedDirectory = Path.Combine(_workspace.CreateSubdir("docs"), "linked");

        Directory.CreateSymbolicLink(
            linkedDirectory,
            Path.Combine("..", protectedDirectory.Replace('/', Path.DirectorySeparatorChar)));

        string planted = Path.Combine(linkedDirectory, "pre-commit");

        (bool createdSuccess, McpToolsCallResultWire? createdError) =
            await SandboxedFileIo.TryWriteAllTextAtomicallyAsync(
                _workspace.Root,
                planted,
                "#!/bin/sh\necho planted\n",
                CancellationToken.None);

        Assert.False(createdSuccess);

        Assert.Contains(
            "protected",
            Assert.IsType<McpToolContentTextWire>(Assert.Single(createdError!.Content!)).Text!,
            StringComparison.OrdinalIgnoreCase);

        Assert.False(File.Exists(Path.Combine(protectedPath, "pre-commit")));

        (bool overwriteSuccess, McpToolsCallResultWire? overwriteError) =
            await SandboxedFileIo.TryWriteAllTextAtomicallyAsync(
                _workspace.Root,
                Path.Combine(linkedDirectory, "existing"),
                "overwritten",
                CancellationToken.None);

        Assert.False(overwriteSuccess);

        Assert.Contains(
            "protected",
            Assert.IsType<McpToolContentTextWire>(Assert.Single(overwriteError!.Content!)).Text!,
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal("keep", await File.ReadAllTextAsync(existing));
    }

    [SkippableFact]
    public async Task Write_through_an_in_workspace_link_to_dot_git_config_is_rejected()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "This asserts POSIX symbolic-link behaviour and runs on macOS and Linux only.");

        string config = _workspace.WriteFile(".git/config", "[core]\n");

        string link = Path.Combine(_workspace.CreateSubdir("docs"), "gitconfig");

        File.CreateSymbolicLink(link, Path.Combine("..", ".git", "config"));

        (bool success, McpToolsCallResultWire? error) = await SandboxedFileIo.TryWriteAllTextAtomicallyAsync(
            _workspace.Root,
            link,
            "[core]\n\tfsmonitor = /tmp/payload\n",
            CancellationToken.None);

        Assert.False(success);

        Assert.NotNull(error);

        Assert.Equal("[core]\n", await File.ReadAllTextAsync(config));
    }

    /// <summary>
    /// An overwrite carries the destination's UTF-8 BOM across, as the other write paths do.
    /// </summary>
    /// <remarks>
    /// <c>SecureFileReader.ReadUtf8TextAsync</c> strips the preamble before handing the model any
    /// text, so a read_file/write_file round-trip through MCP used to silently strip the BOM from
    /// every file that had one — an unrequested re-encoding the model never asked for.
    /// <c>PhysicalFileSystemWriter</c> and <c>WorkspaceTextFile</c> both preserve it; this was the
    /// last write path that did not.
    /// </remarks>
    [Fact]
    public async Task TryWriteAllTextAtomicallyAsync_preserves_an_existing_utf8_bom()
    {
        string target = Path.Combine(_workspace.Root, "bom.txt");

        byte[] preamble = [0xEF, 0xBB, 0xBF];

        await File.WriteAllBytesAsync(target, [.. preamble, .. "original"u8.ToArray()]);

        (bool success, _) = await SandboxedFileIo.TryWriteAllTextAtomicallyAsync(
            _workspace.Root,
            target,
            "replacement",
            CancellationToken.None);

        Assert.True(success);

        byte[] expected = [.. preamble, .. "replacement"u8.ToArray()];

        Assert.Equal(expected, await File.ReadAllBytesAsync(target));
    }

    /// <summary>
    /// A destination without a BOM never gains one, and a caller who supplies the preamble himself
    /// does not get it twice.
    /// </summary>
    [Fact]
    public async Task TryWriteAllTextAtomicallyAsync_never_introduces_or_doubles_a_preamble()
    {
        string plain = Path.Combine(_workspace.Root, "plain.txt");

        await File.WriteAllBytesAsync(plain, "original"u8.ToArray());

        (bool plainWritten, _) = await SandboxedFileIo.TryWriteAllTextAtomicallyAsync(
            _workspace.Root,
            plain,
            "replacement",
            CancellationToken.None);

        Assert.True(plainWritten);

        Assert.Equal("replacement"u8.ToArray(), await File.ReadAllBytesAsync(plain));

        byte[] preamble = [0xEF, 0xBB, 0xBF];

        string bommed = Path.Combine(_workspace.Root, "double.txt");

        await File.WriteAllBytesAsync(bommed, [.. preamble, .. "original"u8.ToArray()]);

        (bool bommedWritten, _) = await SandboxedFileIo.TryWriteAllTextAtomicallyAsync(
            _workspace.Root,
            bommed,
            "\uFEFFreplacement",
            CancellationToken.None);

        Assert.True(bommedWritten);

        byte[] expected = [.. preamble, .. "replacement"u8.ToArray()];

        Assert.Equal(expected, await File.ReadAllBytesAsync(bommed));
    }

    [Fact]
    public async Task TryWriteAllTextAtomicallyAsync_does_not_treat_a_short_destination_as_a_preamble()
    {
        string target = Path.Combine(_workspace.Root, "short.txt");

        await File.WriteAllBytesAsync(target, [0xEF]);

        (bool success, _) = await SandboxedFileIo.TryWriteAllTextAtomicallyAsync(
            _workspace.Root,
            target,
            "replacement",
            CancellationToken.None);

        Assert.True(success);

        Assert.Equal("replacement"u8.ToArray(), await File.ReadAllBytesAsync(target));
    }

    [SkippableFact]
    public async Task TryWriteAllTextAtomicallyAsync_rejects_existing_hard_link()
    {
        Skip.If(
            !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "Unsupported operating system.");

        string target = Path.Combine(_workspace.Root, "linked-write.txt");

        string outsideAlias = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-sandbox-link-{Guid.NewGuid():N}.txt");

        await File.WriteAllTextAsync(target, "original");

        try
        {
            Assert.True(HardLinkTestSupport.TryCreate(outsideAlias, target));

            (bool success, McpToolsCallResultWire? error) =
                await SandboxedFileIo.TryWriteAllTextAtomicallyAsync(
                    _workspace.Root,
                    target,
                    "replacement",
                    CancellationToken.None);

            Assert.False(success);

            Assert.NotNull(error);

            Assert.Equal("original", await File.ReadAllTextAsync(target));

            Assert.Equal("original", await File.ReadAllTextAsync(outsideAlias));
        }
        finally
        {
            File.Delete(outsideAlias);
        }
    }

    [Fact]
    public async Task TryWriteAllTextAtomicallyAsync_fails_closed_when_link_count_is_unavailable()
    {
        string target = Path.Combine(_workspace.Root, "unknown-link-count.txt");

        await File.WriteAllTextAsync(target, "original");

        FileHandleIdentityInterop.TryGetPathMetadataForTests = _ => null;

        try
        {
            (bool success, McpToolsCallResultWire? error) =
                await SandboxedFileIo.TryWriteAllTextAtomicallyAsync(
                    _workspace.Root,
                    target,
                    "replacement",
                    CancellationToken.None);

            Assert.False(success);

            Assert.NotNull(error);

            Assert.Equal("original", await File.ReadAllTextAsync(target));
        }
        finally
        {
            FileHandleIdentityInterop.TryGetPathMetadataForTests = null;
        }
    }

    [Fact]
    public void TryOpenForRead_accepts_regular_file_inside_workspace()
    {
        string target = Path.Combine(_workspace.Root, "inside.txt");

        File.WriteAllText(target, "inside");

        bool opened = SandboxedFileIo.TryOpenForRead(
            _workspace.Root,
            target,
            out FileStream? stream,
            out McpToolsCallResultWire? error);

        Assert.True(
            opened,
            error?.Content?[0].Text);

        Assert.NotNull(stream);

        using (stream)
        {
            Assert.Equal("inside", new StreamReader(stream).ReadToEnd());
        }
    }

    [Fact]
    public void TryOpenForRead_rejects_hard_linked_regular_file()
    {
        string target = Path.Combine(_workspace.Root, "linked-read.txt");

        string outsideAlias = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-sandbox-read-link-{Guid.NewGuid():N}.txt");

        File.WriteAllText(target, "inside");

        try
        {
            Assert.True(HardLinkTestSupport.TryCreate(outsideAlias, target));

            bool opened = SandboxedFileIo.TryOpenForRead(
                _workspace.Root,
                target,
                out FileStream? stream,
                out McpToolsCallResultWire? error);

            Assert.False(opened);

            Assert.Null(stream);

            AssertSandboxError(error);
        }
        finally
        {
            File.Delete(outsideAlias);
        }
    }

    [Fact]
    public async Task TryReadAllTextAsync_rejects_malformed_utf8()
    {
        string target = Path.Combine(_workspace.Root, "malformed.txt");

        await File.WriteAllBytesAsync(target, [0x66, 0x80, 0x6f]);

        (string? content, McpToolsCallResultWire? error) =
            await SandboxedFileIo.TryReadAllTextAsync(
                _workspace.Root,
                target,
                maxBytes: 1024,
                CancellationToken.None);

        Assert.Null(content);

        Assert.NotNull(error);

        Assert.True(error!.IsError);

        string message = Assert.Single(error.Content!).Text!;

        Assert.DoesNotContain(target, message, StringComparison.Ordinal);

        Assert.DoesNotContain("f�o", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryReadAllTextAsync_observes_cancellation_inside_read_loop()
    {
        string target = Path.Combine(_workspace.Root, "cancel-read.txt");

        await File.WriteAllTextAsync(target, new string('x', 8192));

        using CancellationTokenSource cancellation = new();

        SecureFileReader.AfterOpenForTests = _ =>
        {
            SecureFileReader.AfterOpenForTests = null;

            cancellation.Cancel();
        };

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => SandboxedFileIo.TryReadAllTextAsync(
                _workspace.Root,
                target,
                maxBytes: 8192,
                cancellation.Token));
    }

    /// <summary>
    /// The revalidation never trusts <see cref="FileStream.Name"/>: a handle on an outside file is rejected
    /// even when the stream reports a name inside the workspace.
    /// </summary>
    [Fact]
    public void TryRevalidateOpenedHandle_rejects_outside_handle_whose_reported_name_is_inside()
    {
        Assert.True(
            FileHandleIdentityInterop.TryGetPathIdentity(_outsideFile, out FileHandleIdentity expectedIdentity));

        using FileStream stream = new ReportedNameFileStream(
            _outsideFile,
            Path.Combine(_workspace.Root, "looks-inside.txt"));

        bool valid = SandboxedFileIo.TryRevalidateOpenedHandle(
            _workspace.Root,
            stream,
            expectedIdentity,
            out McpToolsCallResultWire? error);

        Assert.False(valid);

        AssertSandboxError(error);
    }

    /// <summary>
    /// R-007: a write spelled through <c>ws/d -> b/sub</c>, where <c>ws/b -> ../outside</c> escapes, must not
    /// create anything under <c>outside</c>.
    /// </summary>
    [SkippableFact]
    public async Task TryWriteAllTextAtomicallyAsync_ThroughChainedLink_DoesNotCreateFileOutsideRoot()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "This asserts POSIX behaviour and runs on macOS and Linux only.");

        string outside = CreateOutsideDirectory();

        Directory.CreateDirectory(Path.Combine(outside, "sub"));

        Directory.CreateSymbolicLink(
            Path.Combine(_workspace.Root, "b"),
            Path.Combine("..", Path.GetFileName(outside)));

        Directory.CreateSymbolicLink(
            Path.Combine(_workspace.Root, "d"),
            Path.Combine("b", "sub"));

        (bool success, McpToolsCallResultWire? error) =
            await SandboxedFileIo.TryWriteAllTextAtomicallyAsync(
                _workspace.Root,
                Path.Combine(_workspace.Root, "d", "planted.txt"),
                "attacker content",
                CancellationToken.None);

        Assert.False(success);

        AssertSandboxError(error);

        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(outside, "sub")));
    }

    /// <summary>
    /// The post-open revalidation asks the kernel where the opened handle lives. A file moved out of the
    /// workspace after it was opened keeps its identity, and the path the stream was opened with still
    /// reads as contained, so only the handle's own path can tell that it now sits outside the root.
    /// </summary>
    [SkippableFact]
    public void TryRevalidateOpenedHandle_rejects_handle_whose_file_moved_outside_workspace()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "Moving a file that has an open handle is refused on Windows.");

        string outside = CreateOutsideDirectory();

        string target = Path.Combine(_workspace.Root, "moved-after-open.txt");

        File.WriteAllText(target, "inside");

        Assert.True(FileHandleIdentityInterop.TryGetPathIdentity(target, out FileHandleIdentity expectedIdentity));

        using FileStream stream = new(
            target,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        Assert.True(SandboxedFileIo.TryRevalidateOpenedHandle(
            _workspace.Root,
            stream,
            expectedIdentity,
            out _));

        File.Move(target, Path.Combine(outside, "moved-after-open.txt"));

        bool valid = SandboxedFileIo.TryRevalidateOpenedHandle(
            _workspace.Root,
            stream,
            expectedIdentity,
            out McpToolsCallResultWire? error);

        Assert.False(valid);

        AssertSandboxError(error);
    }

    [Fact]
    public void TryRevalidateOpenedHandle_rejects_open_file_outside_workspace()
    {
        Assert.True(
            FileHandleIdentityInterop.TryGetPathIdentity(_outsideFile, out FileHandleIdentity expectedIdentity));

        using FileStream stream = new(
            _outsideFile,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        bool valid = SandboxedFileIo.TryRevalidateOpenedHandle(
            _workspace.Root,
            stream,
            expectedIdentity,
            out McpToolsCallResultWire? error);

        Assert.False(valid);

        AssertSandboxError(error);
    }

    [Fact]
    public void TryRevalidateOpenedHandle_rejects_when_handle_identity_cannot_be_resolved()
    {
        string target = Path.Combine(_workspace.Root, "missing-handle-identity.txt");

        File.WriteAllText(target, "inside");

        Assert.True(
            FileHandleIdentityInterop.TryGetPathIdentity(target, out FileHandleIdentity expectedIdentity));

        using FileStream stream = new(
            target,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        FileHandleIdentityInterop.TryGetHandleMetadataForTests = _ => null;

        try
        {
            bool valid = SandboxedFileIo.TryRevalidateOpenedHandle(
                _workspace.Root,
                stream,
                expectedIdentity,
                out McpToolsCallResultWire? error);

            Assert.False(valid);

            AssertSandboxError(error);
        }
        finally
        {
            FileHandleIdentityInterop.TryGetHandleMetadataForTests = null;
        }
    }

    [Fact]
    public void TryOpenForRead_rejects_when_handle_identity_mismatches_preopen_identity()
    {
        FileHandleIdentityInterop.TryGetPathMetadataForTests = _ =>
            new FileHandleMetadata(
                new FileHandleIdentity(1, 1),
                HardLinkCount: 1);

        FileHandleIdentityInterop.TryGetHandleMetadataForTests = _ =>
            new FileHandleMetadata(
                new FileHandleIdentity(1, 2),
                HardLinkCount: 1);

        try
        {
            string target = Path.Combine(_workspace.Root, "mismatch.txt");

            File.WriteAllText(target, "mismatch");

            bool opened = SandboxedFileIo.TryOpenForRead(
                _workspace.Root,
                target,
                out _,
                out McpToolsCallResultWire? error);

            Assert.False(opened);

            Assert.NotNull(error);

            Assert.Contains("sandbox", error!.Content![0].Text!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            FileHandleIdentityInterop.TryGetPathMetadataForTests = null;

            FileHandleIdentityInterop.TryGetHandleMetadataForTests = null;
        }
    }

    // W3.4 Group C #7: the write path validates the target lexically then File.Move's the
    // temp file (TOCTOU between validation and the move). A post-move handle-identity check
    // mirrors the read path: the destination's opened handle identity must match the temp
    // file's pre-move identity (the move preserves the inode on the same filesystem). A
    // mismatch means the destination was swapped (e.g. to a symlink) between validation and
    // the move, and the write is rejected as a sandbox escape.
    [Fact]
    public async Task TryWriteAllTextAtomicallyAsync_rejects_when_destination_handle_mismatches_temp_identity()
    {
        FileHandleIdentityInterop.TryGetPathIdentityForTests = _ => new FileHandleIdentity(7, 7);

        FileHandleIdentityInterop.TryGetHandleIdentityForTests = _ => new FileHandleIdentity(7, 8);

        try
        {
            string target = Path.Combine(_workspace.Root, "write-mismatch.txt");

            File.WriteAllText(target, "original");

            (bool success, McpToolsCallResultWire? error) = await SandboxedFileIo.TryWriteAllTextAtomicallyAsync(
                _workspace.Root,
                target,
                "new content",
                CancellationToken.None);

            Assert.False(success);

            Assert.NotNull(error);

            Assert.Contains("sandbox", error!.Content![0].Text!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            FileHandleIdentityInterop.TryGetPathIdentityForTests = null;

            FileHandleIdentityInterop.TryGetHandleIdentityForTests = null;
        }
    }

    [SkippableFact]
    public void TryGetPathIdentity_MatchesHandleIdentity_OnUnix()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "This asserts POSIX behaviour and runs on macOS and Linux only.");

        string target = Path.Combine(_workspace.Root, "identity.txt");

        File.WriteAllText(target, "identity");

        using SafeFileHandle handle = File.OpenHandle(
            target,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            FileOptions.None);

        bool pathOk = FileHandleIdentityInterop.TryGetPathIdentity(target, out FileHandleIdentity pathIdentity);

        bool handleOk = FileHandleIdentityInterop.TryGetHandleIdentity(handle, out FileHandleIdentity handleIdentity);

        Assert.True(pathOk);

        Assert.True(handleOk);

        Assert.True(FileHandleIdentity.IdentitiesMatch(pathIdentity, handleIdentity));
    }

    [Fact]
    public async Task TryWriteAllTextAtomicallyAsync_rejects_target_outside_workspace()
    {
        (bool success, McpToolsCallResultWire? error) =
            await SandboxedFileIo.TryWriteAllTextAtomicallyAsync(
                _workspace.Root,
                _outsideFile,
                "attacker content",
                CancellationToken.None);

        Assert.False(success);

        AssertSandboxError(error);

        Assert.Equal("outside secret", await File.ReadAllTextAsync(_outsideFile));
    }

    // The write path revalidates containment a second time after creating the parent directory,
    // because directory creation is an observable pause an attacker can use to swap the parent for
    // a symlink out of the workspace. That second revalidation must fail closed before any staging.
    [SkippableFact]
    public async Task TryWriteAllTextAtomicallyAsync_rejects_target_that_escapes_after_parent_directory_creation()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "This asserts POSIX behaviour and runs on macOS and Linux only.");

        string outside = CreateOutsideDirectory();

        string parent = Path.Combine(_workspace.Root, "created-then-swapped");

        string target = Path.Combine(parent, "escapes-after-mkdir.txt");

        SandboxedFileIo.AfterCreateParentDirectoryForTests = created =>
        {
            SandboxedFileIo.AfterCreateParentDirectoryForTests = null;

            Directory.Delete(created);

            Directory.CreateSymbolicLink(created, outside);
        };

        try
        {
            (bool success, McpToolsCallResultWire? error) =
                await SandboxedFileIo.TryWriteAllTextAtomicallyAsync(
                    _workspace.Root,
                    target,
                    "attacker content",
                    CancellationToken.None);

            Assert.False(success);

            AssertSandboxError(error);

            Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
        }
        finally
        {
            SandboxedFileIo.AfterCreateParentDirectoryForTests = null;
        }
    }

    // A path with no parent directory (a filesystem root) falls back to the workspace root when
    // choosing the staging directory. The write must still be rejected: the destination is a
    // directory, so nothing may be staged or replaced.
    [SkippableFact]
    public async Task TryWriteAllTextAtomicallyAsync_rejects_root_path_that_has_no_parent_directory()
    {
        // A Windows drive root cannot be probed for a link target: the metadata query underneath
        // WorkspacePathPolicy's symlink check cannot name a root directory, so the check fails
        // closed and containment revalidation rejects the path before the staging fallback this
        // test exists to cover is reached. Failing closed at a drive root is the posture we want,
        // which makes the branch unreachable on Windows rather than broken.
        Skip.If(
            OperatingSystem.IsWindows(),
            "Containment revalidation fails closed at a Windows drive root, short of the staging fallback under test.");

        string filesystemRoot = Path.GetPathRoot(_workspace.Root)!;

        Assert.Null(Path.GetDirectoryName(filesystemRoot));

        // Pins that containment revalidation passes, so the rejection below comes from the
        // staging/replace step rather than from the pre-write containment check.
        Assert.True(WorkspacePathPolicy.RevalidatePathBeforeIo(filesystemRoot, filesystemRoot));

        (bool success, McpToolsCallResultWire? error) =
            await SandboxedFileIo.TryWriteAllTextAtomicallyAsync(
                filesystemRoot,
                filesystemRoot,
                "attacker content",
                CancellationToken.None);

        Assert.False(success);

        AssertSandboxError(error);

        Assert.Empty(Directory.EnumerateFiles(filesystemRoot, ".arcanum-*.tmp"));
    }

    // W3.4 Group C #7: when the post-move identity check fails AND the best-effort rollback cannot
    // confirm the quarantined destination, the caller must be told the destination is unverified
    // instead of receiving a success or a generic pre-move failure.
    [Fact]
    public async Task TryWriteAllTextAtomicallyAsync_reports_unverified_destination_when_rollback_cannot_confirm_quarantine()
    {
        string target = Path.Combine(_workspace.Root, "unverified-write.txt");

        await File.WriteAllTextAsync(target, "original");

        // Forces the post-move handle-identity check to fail: the identity captured before the
        // move can never match the identity of the real moved file.
        FileHandleIdentityInterop.TryGetPathIdentityForTests = _ => new FileHandleIdentity(7, 7);

        // Everything else resolves normally except the quarantined copy, whose metadata cannot be
        // read back, so the rollback cannot confirm the unverified content was contained.
        FileHandleIdentityInterop.TryGetPathMetadataForTests = path =>
            path.Contains(".arcanum-quarantine-", StringComparison.Ordinal)
                ? null
                : ResolveRealPathMetadata(path);

        try
        {
            (bool success, McpToolsCallResultWire? error) =
                await SandboxedFileIo.TryWriteAllTextAtomicallyAsync(
                    _workspace.Root,
                    target,
                    "replacement",
                    CancellationToken.None);

            Assert.False(success);

            Assert.NotNull(error);

            Assert.True(error!.IsError);

            Assert.Contains(
                "unverified",
                Assert.Single(error.Content!).Text!,
                StringComparison.OrdinalIgnoreCase);

            string? destinationContent = File.Exists(target)
                ? await File.ReadAllTextAsync(target)
                : null;

            Assert.NotEqual("replacement", destinationContent);
        }
        finally
        {
            FileHandleIdentityInterop.TryGetPathIdentityForTests = null;

            FileHandleIdentityInterop.TryGetPathMetadataForTests = null;
        }
    }

    [Fact]
    public async Task TryReadAllTextAsync_returns_no_content_for_path_outside_workspace()
    {
        (string? content, McpToolsCallResultWire? error) =
            await SandboxedFileIo.TryReadAllTextAsync(
                _workspace.Root,
                _outsideFile,
                maxBytes: 1024,
                CancellationToken.None);

        Assert.Null(content);

        AssertSandboxError(error);
    }

    // The handle is revalidated again after the bytes are read, so content read through a handle
    // that no longer resolves inside the workspace is discarded rather than returned to the caller.
    [SkippableFact]
    public async Task TryReadAllTextAsync_discards_content_when_post_read_revalidation_fails()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "Moving a file that has an open handle is refused on Windows.");

        string outside = CreateOutsideDirectory();

        string target = Path.Combine(_workspace.Root, "escapes-after-read.txt");

        await File.WriteAllTextAsync(target, "secret payload");

        // Runs after the handle passed its post-open check and before the bytes are read: the open
        // file leaves the workspace while the read is in flight.
        SecureFileReader.AfterOpenForTests = _ =>
        {
            SecureFileReader.AfterOpenForTests = null;

            File.Move(target, Path.Combine(outside, "escapes-after-read.txt"));
        };

        (string? content, McpToolsCallResultWire? error) =
            await SandboxedFileIo.TryReadAllTextAsync(
                _workspace.Root,
                target,
                maxBytes: 1024,
                CancellationToken.None);

        Assert.Null(content);

        AssertSandboxError(error);
    }

    /// <summary>
    /// <c>ws/pending -> missing-dir</c> is an in-workspace link whose target does not exist. Containment
    /// accepts a write spelled through it, because the canonical walk keeps the missing target lexically
    /// and it stays under the root, but <c>mkdir</c> never creates through a dangling link. The write must
    /// say so instead of reporting a generic I/O error, and must create nothing at the link's target.
    /// </summary>
    [SkippableFact]
    public async Task TryWriteAllTextAtomicallyAsync_through_a_dangling_in_workspace_link_names_the_link_and_creates_nothing()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "This asserts POSIX behaviour and runs on macOS and Linux only.");

        string link = Path.Combine(_workspace.Root, "pending");

        Directory.CreateSymbolicLink(link, "missing-dir");

        (bool success, McpToolsCallResultWire? error) =
            await SandboxedFileIo.TryWriteAllTextAtomicallyAsync(
                _workspace.Root,
                Path.Combine(link, "deeper", "new.txt"),
                "content",
                CancellationToken.None);

        Assert.False(success);

        AssertBlockedParentError(error);

        Assert.False(Directory.Exists(Path.Combine(_workspace.Root, "missing-dir")));

        Assert.Equal("missing-dir", new DirectoryInfo(link).LinkTarget);
    }

    /// <summary>
    /// The same refusal when the blocking component is an ordinary file: <c>notes.txt/new.txt</c> passes
    /// containment (nothing exists below a file) and then cannot have its parent created.
    /// </summary>
    [Fact]
    public async Task TryWriteAllTextAtomicallyAsync_below_an_existing_file_names_the_file_and_leaves_it_untouched()
    {
        string notes = _workspace.WriteFile("notes.txt", "notes");

        (bool success, McpToolsCallResultWire? error) =
            await SandboxedFileIo.TryWriteAllTextAtomicallyAsync(
                _workspace.Root,
                Path.Combine(notes, "new.txt"),
                "content",
                CancellationToken.None);

        Assert.False(success);

        AssertBlockedParentError(error);

        Assert.Equal("notes", await File.ReadAllTextAsync(notes));
    }

    // Real metadata for paths the seam is not simulating. Resolved through the no-follow probe,
    // which has its own seam, so this never has to unset (and race with) the seam it is called from.
    // Every path in these tests is a regular file, where the two probes agree.
    private static FileHandleMetadata? ResolveRealPathMetadata(string path) =>
        FileHandleIdentityInterop.TryGetPathMetadataNoFollow(path, out FileHandleMetadata metadata)
            ? metadata
            : null;

    /// <summary>
    /// A real directory beside the workspace root, removed in <see cref="DisposeAsync"/>.
    /// </summary>
    private string CreateOutsideDirectory()
    {
        string outside = Path.Combine(
            Path.GetDirectoryName(_workspace.Root)!,
            "outside-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(outside);

        _outsideDirectories.Add(outside);

        return outside;
    }

    private static void AssertSandboxError(McpToolsCallResultWire? error)
    {
        Assert.NotNull(error);

        Assert.True(error!.IsError);

        McpToolContentTextWire content = Assert.IsType<McpToolContentTextWire>(Assert.Single(error.Content!));

        Assert.Contains("sandbox", content.Text!, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertBlockedParentError(McpToolsCallResultWire? error)
    {
        Assert.NotNull(error);

        Assert.True(error!.IsError);

        McpToolContentTextWire content = Assert.IsType<McpToolContentTextWire>(Assert.Single(error.Content!));

        Assert.Contains(
            "an existing file or a symbolic link that does not lead to a directory",
            content.Text!,
            StringComparison.Ordinal);
    }

    private sealed class ReportedNameFileStream(string path, string reportedName)
        : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)
    {
        public override string Name => reportedName;
    }
}
