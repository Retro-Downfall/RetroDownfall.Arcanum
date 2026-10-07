using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Win32.SafeHandles;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.CommandCenter;
using RetroDownfall.Arcanum.Cli.Commands;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Coordination;
using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

public sealed class CommandCenterTurnAttachmentBuilderTests : IDisposable
{
    private readonly string _root;

    public CommandCenterTurnAttachmentBuilderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "arcanum-cc-attach-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        _ = TestDirectoryCleanup.TryDelete(_root, nameof(CommandCenterTurnAttachmentBuilderTests));
    }

    [Fact]
    public async Task Stages_text_file_from_at_token_and_strips_token()
    {
        string path = Path.Combine(_root, "notes.txt");
        File.WriteAllText(path, "hello attach", Encoding.UTF8);

        TurnAttachmentBuildResult result = await CommandCenterTurnAttachmentBuilder.BuildAsync(
            $"please read @{path}",
            workingDirectory: _root,
            preStagedPaths: [],
            settings: DefaultSettings(),
            cancellationToken: CancellationToken.None);

        Assert.DoesNotContain("@", result.Prompt, StringComparison.Ordinal);
        Assert.Contains("please read", result.Prompt, StringComparison.Ordinal);
        Assert.NotNull(result.AttachedFiles);
        Assert.Single(result.AttachedFiles!);
        Assert.Equal("hello attach", result.AttachedFiles![0].Content);
        Assert.Null(result.ScryingFoci);
        Assert.Contains(result.StatusLines, static s => s.Contains("Staged:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stages_png_as_scrying_focus()
    {
        string path = Path.Combine(_root, "shot.png");
        // Minimal PNG magic bytes + padding.
        byte[] png =
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        ];
        File.WriteAllBytes(path, png);

        TurnAttachmentBuildResult result = await CommandCenterTurnAttachmentBuilder.BuildAsync(
            $"look at @{path}",
            workingDirectory: _root,
            preStagedPaths: [],
            settings: DefaultSettings(),
            cancellationToken: CancellationToken.None);

        Assert.Null(result.AttachedFiles);
        Assert.NotNull(result.ScryingFoci);
        Assert.Single(result.ScryingFoci!);
        Assert.Equal("image/png", result.ScryingFoci![0].MimeType);
        Assert.Contains(result.StatusLines, static s => s.Contains("Scrying focus:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pre_staged_path_from_attach_slash_is_included()
    {
        string path = Path.Combine(_root, "pre.txt");
        File.WriteAllText(path, "pre-staged", Encoding.UTF8);

        TurnAttachmentBuildResult result = await CommandCenterTurnAttachmentBuilder.BuildAsync(
            "use the attach",
            workingDirectory: _root,
            preStagedPaths: [path],
            settings: DefaultSettings(),
            cancellationToken: CancellationToken.None);

        Assert.NotNull(result.AttachedFiles);
        Assert.Single(result.AttachedFiles!);
        Assert.Equal("pre-staged", result.AttachedFiles![0].Content);
        Assert.True(result.ClearPreStagedAfterTurn);
    }

    [Fact]
    public async Task Missing_at_path_keeps_literal_token_and_reports_status()
    {
        TurnAttachmentBuildResult result = await CommandCenterTurnAttachmentBuilder.BuildAsync(
            "see @missing-file.txt please",
            workingDirectory: _root,
            preStagedPaths: [],
            settings: DefaultSettings(),
            cancellationToken: CancellationToken.None);

        Assert.Contains("@missing-file.txt", result.Prompt, StringComparison.Ordinal);
        Assert.Null(result.AttachedFiles);
        Assert.Contains(result.StatusLines, static s => s.Contains("not found", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A staging failure must never rewrite the operator's message: the token is the only record of
    /// what they actually asked about, and the turn is dispatched (and billed) regardless.
    /// </summary>
    [Fact]
    public async Task Oversized_text_file_is_rejected_and_keeps_the_literal_token()
    {
        string path = Path.Combine(_root, "big.txt");
        long maxFileBytes = ArcanumSettingClamps.MaxAttachFileSizeBytes(
            ArcanumRuntimeDefaults.CliMaxAttachFileSizeBytes);
        File.WriteAllBytes(path, new byte[checked((int)maxFileBytes + 1)]);

        TurnAttachmentBuildResult result = await CommandCenterTurnAttachmentBuilder.BuildAsync(
            $"summarize the failures in @{path} please",
            workingDirectory: _root,
            preStagedPaths: [],
            settings: DefaultSettings(),
            cancellationToken: CancellationToken.None);

        Assert.Null(result.AttachedFiles);
        Assert.Contains(result.StatusLines, static s => s.Contains("exceeds", StringComparison.OrdinalIgnoreCase));
        Assert.Contains($"@{path}", result.Prompt, StringComparison.Ordinal);
        Assert.Contains(
            result.StatusLines,
            static s => s.Contains("literal token kept in the prompt", StringComparison.Ordinal));
    }

    /// <summary>
    /// An oversized inline image already keeps its token; the text branch must match it so one
    /// staging failure does not behave differently from the other.
    /// </summary>
    [Fact]
    public async Task Oversized_image_keeps_the_literal_token()
    {
        string path = Path.Combine(_root, "huge.png");
        byte[] png = new byte[2 * 1024 * 1024];
        png[0] = 0x89;
        png[1] = 0x50;
        png[2] = 0x4E;
        png[3] = 0x47;
        File.WriteAllBytes(path, png);

        TurnAttachmentBuildResult result = await CommandCenterTurnAttachmentBuilder.BuildAsync(
            $"look at @{path} closely",
            workingDirectory: _root,
            preStagedPaths: [],
            settings: DefaultSettings(),
            cancellationToken: CancellationToken.None);

        Assert.Null(result.ScryingFoci);
        Assert.Contains($"@{path}", result.Prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// An inline image passes the size check before its content is read, and the content is judged only
    /// later, by its signature. A file named <c>.png</c> that is not an image (a large-file-storage pointer
    /// is text) fails that later stage, and the token must still be there: otherwise the model receives a
    /// question with neither the image nor any mention of it.
    /// </summary>
    [Fact]
    public async Task An_inline_image_that_fails_to_stage_keeps_the_literal_token()
    {
        string path = Path.Combine(_root, "diagram.png");
        File.WriteAllText(
            path,
            "version https://example.invalid/spec/v1\noid sha256:abc\nsize 12345\n",
            Encoding.UTF8);

        TurnAttachmentBuildResult result = await CommandCenterTurnAttachmentBuilder.BuildAsync(
            "explain @diagram.png please",
            workingDirectory: _root,
            preStagedPaths: [],
            settings: DefaultSettings(),
            cancellationToken: CancellationToken.None);

        Assert.Null(result.ScryingFoci);
        Assert.Equal("explain @diagram.png please", result.Prompt);
        Assert.Contains(
            result.StatusLines,
            static s => s.Contains("Cannot stage Scrying focus diagram.png", StringComparison.Ordinal)
                && s.Contains("literal token kept in the prompt", StringComparison.Ordinal));
    }

    /// <summary>
    /// Only the tokens whose files actually staged are taken out of the prompt: an image and a text file
    /// that staged lose their tokens, and an image that failed its later stage keeps its own.
    /// </summary>
    [Fact]
    public async Task Only_the_tokens_of_files_that_staged_are_stripped()
    {
        File.WriteAllBytes(
            Path.Combine(_root, "shot.png"),
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "hello attach", Encoding.UTF8);
        File.WriteAllText(Path.Combine(_root, "fake.png"), "not an image", Encoding.UTF8);

        TurnAttachmentBuildResult result = await CommandCenterTurnAttachmentBuilder.BuildAsync(
            "compare @shot.png with @fake.png using @notes.txt",
            workingDirectory: _root,
            preStagedPaths: [],
            settings: DefaultSettings(),
            cancellationToken: CancellationToken.None);

        Assert.Single(result.ScryingFoci!);
        Assert.Single(result.AttachedFiles!);
        Assert.Equal("compare  with @fake.png using \n\n[Attached Files: notes.txt]", result.Prompt);
    }

    /// <summary>
    /// A FIFO reports a length of 0 and <c>File.Exists</c> says true, so only a type check keeps it out of
    /// the read: opening it for reading blocks until a writer appears.
    /// </summary>
    [SkippableFact]
    public async Task A_fifo_named_by_an_at_token_is_rejected_and_keeps_the_literal_token()
    {
        Skip.If(OperatingSystem.IsWindows(), "mkfifo is POSIX-only.");

        string fifo = Path.Combine(_root, "trace.log");
        Assert.True(PosixFifo.TryCreate(fifo), "mkfifo did not create the FIFO.");

        TurnAttachmentBuildResult result = await CommandCenterTurnAttachmentBuilder
            .BuildAsync(
                "summarize @trace.log please",
                workingDirectory: _root,
                preStagedPaths: [],
                settings: DefaultSettings(),
                cancellationToken: CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Null(result.AttachedFiles);
        Assert.Contains("@trace.log", result.Prompt, StringComparison.Ordinal);
        Assert.Contains(
            result.StatusLines,
            static line => line.Contains("not a regular file", StringComparison.Ordinal)
                && line.Contains("literal token kept in the prompt", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task A_pre_staged_fifo_is_skipped_with_a_status_line()
    {
        Skip.If(OperatingSystem.IsWindows(), "mkfifo is POSIX-only.");

        string fifo = Path.Combine(_root, "pipe.txt");
        Assert.True(PosixFifo.TryCreate(fifo), "mkfifo did not create the FIFO.");

        TurnAttachmentBuildResult result = await CommandCenterTurnAttachmentBuilder
            .BuildAsync(
                "use the attachment",
                workingDirectory: _root,
                preStagedPaths: [fifo],
                settings: DefaultSettings(),
                cancellationToken: CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Null(result.AttachedFiles);
        Assert.Contains(
            result.StatusLines,
            static line => line.StartsWith("/attach:", StringComparison.Ordinal)
                && line.Contains("not a regular file", StringComparison.Ordinal));
    }

    [SkippableFact]
    public void A_fifo_is_refused_when_it_is_staged_with_attach()
    {
        Skip.If(OperatingSystem.IsWindows(), "mkfifo is POSIX-only.");

        string fifo = Path.Combine(_root, "pipe.txt");
        Assert.True(PosixFifo.TryCreate(fifo), "mkfifo did not create the FIFO.");

        bool staged = CommandCenterTurnAttachmentBuilder.TryStagePathForNextTurn(
            _root,
            "pipe.txt",
            DefaultSettings(),
            out _,
            out string statusLine);

        Assert.False(staged);
        Assert.Contains("not a regular file", statusLine, StringComparison.Ordinal);
    }

    /// <summary>
    /// The stat that refuses a FIFO and the open that reads the file are two steps, and the path can change
    /// between them. The read must therefore never wait for a writer itself: it opens without blocking and
    /// then checks what it opened, so a path swapped for a FIFO is refused instead of parking a thread-pool
    /// thread until something writes to it.
    /// </summary>
    [SkippableFact]
    public async Task Reading_a_path_that_became_a_fifo_after_the_stat_is_refused_without_waiting_for_a_writer()
    {
        Skip.If(OperatingSystem.IsWindows(), "mkfifo is POSIX-only.");

        string fifo = Path.Combine(_root, "swapped.txt");
        Assert.True(PosixFifo.TryCreate(fifo), "mkfifo did not create the FIFO.");

        Task<string?> read = Task.Run(() =>
            CommandCenterTurnAttachmentBuilder.ReadBoundedTextAsync(fifo, 1024, CancellationToken.None));

        try
        {
            IOException refused = await Assert.ThrowsAsync<IOException>(() =>
                read.WaitAsync(TimeSpan.FromSeconds(30)));

            Assert.Contains("not a regular file", refused.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (!read.IsCompleted)
            {
                // A read parked in open(2) is the defect this pins. Pair a writer with it so the test
                // host does not keep a blocked thread for the rest of the run.
                using FileStream writer = new(fifo, FileMode.Open, FileAccess.Write);
            }
        }
    }

    /// <summary>
    /// A path that is gone by the time it is opened (it was there for the stat) is an ordinary I/O failure
    /// the staging loop reports, not a crash and not a hang.
    /// </summary>
    [Fact]
    public async Task Reading_a_path_that_vanished_after_the_stat_fails_with_an_io_error()
    {
        string gone = Path.Combine(_root, "gone.txt");

        _ = await Assert.ThrowsAnyAsync<IOException>(() =>
            CommandCenterTurnAttachmentBuilder.ReadBoundedTextAsync(gone, 1024, CancellationToken.None));
    }

    [SkippableFact]
    public async Task Reading_a_symbolic_link_loop_fails_with_an_io_error_instead_of_following_it_for_ever()
    {
        Skip.If(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege on Windows.");

        string first = Path.Combine(_root, "first.txt");
        string second = Path.Combine(_root, "second.txt");
        File.CreateSymbolicLink(first, second);
        File.CreateSymbolicLink(second, first);

        _ = await Assert.ThrowsAnyAsync<IOException>(() =>
            CommandCenterTurnAttachmentBuilder.ReadBoundedTextAsync(first, 1024, CancellationToken.None));
    }

    /// <summary>
    /// The Windows lane. The open there is an ordinary one followed by a judgement of the handle's own
    /// kind, so an ordinary file must read and a directory must be refused. Not run on a non-Windows host.
    /// </summary>
    [SkippableFact]
    public async Task Windows_the_open_reads_an_ordinary_file_and_refuses_a_directory()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The Windows lane runs this.");

        string path = Path.Combine(_root, "windows.txt");
        File.WriteAllText(path, "read through the handle", Encoding.UTF8);

        string? text = await CommandCenterTurnAttachmentBuilder.ReadBoundedTextAsync(path, 1024, CancellationToken.None);

        Assert.Equal("read through the handle", text);

        _ = await Assert.ThrowsAnyAsync<Exception>(() =>
            CommandCenterTurnAttachmentBuilder.ReadBoundedTextAsync(_root, 1024, CancellationToken.None));
    }

    [SkippableFact]
    public async Task A_symbolic_link_to_a_regular_file_still_stages()
    {
        Skip.If(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege on Windows.");

        string target = Path.Combine(_root, "target.txt");
        string link = Path.Combine(_root, "link.txt");
        File.WriteAllText(target, "through the link", Encoding.UTF8);
        File.CreateSymbolicLink(link, target);

        TurnAttachmentBuildResult result = await CommandCenterTurnAttachmentBuilder.BuildAsync(
            "read @link.txt",
            workingDirectory: _root,
            preStagedPaths: [],
            settings: DefaultSettings(),
            cancellationToken: CancellationToken.None);

        Assert.NotNull(result.AttachedFiles);
        Assert.Equal("through the link", Assert.Single(result.AttachedFiles!).Content);
    }

    [Fact]
    public async Task A_utf8_byte_order_mark_is_not_part_of_the_attached_text()
    {
        string path = Path.Combine(_root, "bom.txt");
        File.WriteAllText(path, "hello", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        TurnAttachmentBuildResult result = await CommandCenterTurnAttachmentBuilder.BuildAsync(
            "read @bom.txt",
            workingDirectory: _root,
            preStagedPaths: [],
            settings: DefaultSettings(),
            cancellationToken: CancellationToken.None);

        Assert.Equal("hello", Assert.Single(result.AttachedFiles!).Content);
    }

    [Fact]
    public async Task A_pre_staged_path_that_cannot_be_resolved_is_reported_instead_of_thrown()
    {
        TurnAttachmentBuildResult result = await CommandCenterTurnAttachmentBuilder.BuildAsync(
            "use the attachment",
            workingDirectory: _root,
            preStagedPaths: ["bad\0name.txt"],
            settings: DefaultSettings(),
            cancellationToken: CancellationToken.None);

        Assert.Null(result.AttachedFiles);
        Assert.Contains(
            result.StatusLines,
            static line => line.StartsWith("/attach:", StringComparison.Ordinal)
                && line.Contains("could not be resolved", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_cancelled_build_throws_instead_of_reading_the_attachment()
    {
        string path = Path.Combine(_root, "notes.txt");
        File.WriteAllText(path, "never read", Encoding.UTF8);
        using CancellationTokenSource cts = new();
        cts.Cancel();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CommandCenterTurnAttachmentBuilder.BuildAsync(
                "read @notes.txt",
                workingDirectory: _root,
                preStagedPaths: [],
                settings: DefaultSettings(),
                cancellationToken: cts.Token));
    }

    /// <summary>
    /// The Windows lane. Windows has no FIFO, so the gate that matters there is the opposite one: an
    /// ordinary file must not be refused.
    /// </summary>
    [SkippableFact]
    public async Task Windows_an_ordinary_file_stages_by_at_token_and_by_attach()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The Windows lane runs this.");

        string path = Path.Combine(_root, "ordinary.txt");
        File.WriteAllText(path, "ordinary content", Encoding.UTF8);

        TurnAttachmentBuildResult result = await CommandCenterTurnAttachmentBuilder.BuildAsync(
            "read @ordinary.txt",
            workingDirectory: _root,
            preStagedPaths: [],
            settings: DefaultSettings(),
            cancellationToken: CancellationToken.None);

        Assert.Equal("ordinary content", Assert.Single(result.AttachedFiles!).Content);
        Assert.True(
            CommandCenterTurnAttachmentBuilder.TryStagePathForNextTurn(
                _root,
                "ordinary.txt",
                DefaultSettings(),
                out _,
                out string statusLine),
            statusLine);
    }

    [SkippableFact]
    public async Task Windows_a_symbolic_link_to_a_regular_file_stages()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The Windows lane runs this.");

        string target = Path.Combine(_root, "target.txt");
        string link = Path.Combine(_root, "link.txt");
        File.WriteAllText(target, "through the link", Encoding.UTF8);

        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Skip.If(true, "This account may not create symbolic links: " + ex.Message);
        }

        TurnAttachmentBuildResult result = await CommandCenterTurnAttachmentBuilder.BuildAsync(
            "read @link.txt",
            workingDirectory: _root,
            preStagedPaths: [],
            settings: DefaultSettings(),
            cancellationToken: CancellationToken.None);

        Assert.Equal("through the link", Assert.Single(result.AttachedFiles!).Content);
    }

    /// <summary>
    /// A reparse point that names no other location (a cloud placeholder, deduplicated or compressed data)
    /// is served by the operating system as an ordinary file, and Windows reports it as something other
    /// than a regular file. The test makes the closest thing a user account can make on demand, a file
    /// whose data the system compresses in place, and skips when the volume will not.
    /// </summary>
    [SkippableFact]
    public async Task Windows_a_reparse_point_that_names_no_other_location_stages()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The Windows lane runs this.");

        string path = Path.Combine(_root, "compressed.txt");
        string content = string.Concat(Enumerable.Repeat("arcanum attachment ", 8192));
        File.WriteAllText(path, content, Encoding.UTF8);

        Skip.IfNot(
            TryMakeCompressedReparsePoint(path),
            "The volume did not turn the file into a reparse point that names no other location.");

        TurnAttachmentBuildResult result = await CommandCenterTurnAttachmentBuilder.BuildAsync(
            "read @compressed.txt",
            workingDirectory: _root,
            preStagedPaths: [],
            settings: DefaultSettings(),
            cancellationToken: CancellationToken.None);

        Assert.Equal(content, Assert.Single(result.AttachedFiles!).Content);
        Assert.True(
            CommandCenterTurnAttachmentBuilder.TryStagePathForNextTurn(
                _root,
                "compressed.txt",
                DefaultSettings(),
                out _,
                out string statusLine),
            statusLine);
    }

    private static bool TryMakeCompressedReparsePoint(string path)
    {
        using System.Diagnostics.Process? compact = System.Diagnostics.Process.Start(
            new ProcessStartInfo("compact.exe", ["/c", "/exe:xpress4k", path]) { UseShellExecute = false });

        if (compact is null)
        {
            return false;
        }

        if (!compact.WaitForExit(30_000))
        {
            compact.Kill(entireProcessTree: true);
            return false;
        }

        FileAttributes attributes = File.GetAttributes(path);
        return (attributes & FileAttributes.ReparsePoint) != 0 && new FileInfo(path).LinkTarget is null;
    }

    private static ArcanumSettings DefaultSettings() =>
        new()
        {
            Features = new FeatureSettings { Scrying = true },
        };
}

/// <summary>
/// The kind gate that sits in front of every attachment read, driven through the same seams the path
/// policy tests use so its decisions are pinned on every platform. Windows reports every reparse point as
/// <see cref="FileSystemObjectKind.Other"/>, including the ones that name no other location (a cloud
/// placeholder, deduplicated data), which the repository's path policy treats as ordinary files; the
/// Windows-only branch is pinned through its parameters because it cannot be reached by a real file
/// here.
/// </summary>
[Collection("WorkspacePathPolicy")]
public sealed class CommandCenterAttachmentFileKindTests : IDisposable
{
    private static readonly FileHandleIdentity AnIdentity = new(VolumeId: 1, FileId: 2);

    private readonly string _root;

    private readonly Func<string, FileHandleMetadata?>? _previousPathMetadataHook;

    private readonly Func<SafeFileHandle, FileHandleMetadata?>? _previousHandleMetadataHook;

    public CommandCenterAttachmentFileKindTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "arcanum-cc-kind-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _previousPathMetadataHook = FileHandleIdentityInterop.TryGetPathMetadataForTests;
        _previousHandleMetadataHook = FileHandleIdentityInterop.TryGetHandleMetadataForTests;
    }

    public void Dispose()
    {
        FileHandleIdentityInterop.TryGetPathMetadataForTests = _previousPathMetadataHook;
        FileHandleIdentityInterop.TryGetHandleMetadataForTests = _previousHandleMetadataHook;
        _ = TestDirectoryCleanup.TryDelete(_root, nameof(CommandCenterAttachmentFileKindTests));
    }

    [Theory]
    [InlineData("RegularFile", false, false, true, false)]
    [InlineData("RegularFile", true, false, true, false)]
    [InlineData("Directory", false, true, false, false)]
    [InlineData("Directory", true, true, false, false)]
    [InlineData("Other", false, true, false, false)]
    [InlineData("Other", true, true, true, true)]
    [InlineData("Other", true, false, false, true)]
    public void The_kind_gate_decides_from_the_kind_the_platform_and_only_then_the_reparse_point(
        string kindName,
        bool onWindows,
        bool isReparsePointWithoutTarget,
        bool expectedAttachable,
        bool expectedProbeAsked)
    {
        FileSystemObjectKind kind = Enum.Parse<FileSystemObjectKind>(kindName);
        int asked = 0;

        bool attachable = AttachableFile.IsAttachableFileKind(
            kind,
            onWindows,
            () =>
            {
                asked++;
                return isReparsePointWithoutTarget;
            });

        Assert.Equal(expectedAttachable, attachable);
        Assert.Equal(expectedProbeAsked ? 1 : 0, asked);
    }

    /// <summary>
    /// An opened handle is the file itself: the operating system has already followed every link to reach
    /// it, so the handle's own kind is the whole answer and there is no second look at the path to race.
    /// Windows reports a reparse point that names no other location (a cloud placeholder, deduplicated
    /// data) as <see cref="FileSystemObjectKind.Other"/>, so there that kind is attachable.
    /// </summary>
    [Theory]
    [InlineData("RegularFile", false, true)]
    [InlineData("RegularFile", true, true)]
    [InlineData("Directory", false, false)]
    [InlineData("Directory", true, false)]
    [InlineData("Other", false, false)]
    [InlineData("Other", true, true)]
    public void An_opened_handle_is_judged_by_its_own_kind_and_never_by_a_second_look_at_the_path(
        string kindName,
        bool onWindows,
        bool expectedAttachable)
    {
        FileSystemObjectKind kind = Enum.Parse<FileSystemObjectKind>(kindName);

        Assert.Equal(expectedAttachable, AttachableFile.IsAttachableHandleKind(kind, onWindows));
    }

    [SkippableFact]
    public async Task A_path_the_stat_reports_as_neither_file_nor_directory_is_refused_off_windows()
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows serves such an entry as a file when it is a reparse point.");

        string path = WriteNotes();
        FileHandleIdentityInterop.TryGetPathMetadataForTests = _ =>
            new FileHandleMetadata(AnIdentity, 1, FileSystemObjectKind.Other);

        TurnAttachmentBuildResult result = await BuildAsync("read @notes.txt");

        Assert.Null(result.AttachedFiles);
        Assert.Contains("@notes.txt", result.Prompt, StringComparison.Ordinal);
        Assert.Contains(
            result.StatusLines,
            line => line.Contains("not a regular file", StringComparison.Ordinal)
                && line.Contains("literal token kept in the prompt", StringComparison.Ordinal));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task A_path_the_stat_reports_as_a_directory_is_refused_on_every_platform()
    {
        _ = WriteNotes();
        FileHandleIdentityInterop.TryGetPathMetadataForTests = _ =>
            new FileHandleMetadata(AnIdentity, 1, FileSystemObjectKind.Directory);

        TurnAttachmentBuildResult result = await BuildAsync("read @notes.txt");

        Assert.Null(result.AttachedFiles);
        Assert.Contains(
            result.StatusLines,
            static line => line.Contains("not a regular file", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_path_the_stat_reports_as_a_regular_file_is_staged_on_every_platform()
    {
        _ = WriteNotes();
        FileHandleIdentityInterop.TryGetPathMetadataForTests = _ =>
            new FileHandleMetadata(AnIdentity, 1, FileSystemObjectKind.RegularFile);

        TurnAttachmentBuildResult result = await BuildAsync("read @notes.txt");

        Assert.Equal("notes", Assert.Single(result.AttachedFiles!).Content);
    }

    [SkippableFact]
    public async Task A_handle_that_is_not_a_regular_file_once_opened_is_refused_off_windows()
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows serves such a handle as a file when it is a reparse point.");

        _ = WriteNotes();
        FileHandleIdentityInterop.TryGetHandleMetadataForTests = _ =>
            new FileHandleMetadata(AnIdentity, 1, FileSystemObjectKind.Other);

        TurnAttachmentBuildResult result = await BuildAsync("read @notes.txt");

        Assert.Null(result.AttachedFiles);
        Assert.Contains(
            result.StatusLines,
            static line => line.Contains("Cannot stage notes.txt", StringComparison.Ordinal)
                && line.Contains("not a regular file", StringComparison.Ordinal));
    }

    /// <summary>
    /// A regular file the account may not read passes the stat gate and fails at the open, and the
    /// operator is told it is a permission problem. Reporting it as "not a regular file" would send them
    /// looking at the file's type instead of its mode.
    /// </summary>
    [SkippableFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task An_unreadable_regular_file_is_reported_as_access_denied_and_not_as_a_non_regular_file()
    {
        Skip.If(OperatingSystem.IsWindows(), "File modes are POSIX-only.");

        string path = WriteNotes();
        File.SetUnixFileMode(path, UnixFileMode.None);

        try
        {
            Skip.If(CanOpenForRead(path), "This account reads a mode-000 file (privileged).");

            _ = Assert.Throws<UnauthorizedAccessException>(() => AttachableFile.OpenForRead(path, 4096).Dispose());

            TurnAttachmentBuildResult result = await BuildAsync("read @notes.txt");

            Assert.Null(result.AttachedFiles);
            Assert.Contains(
                result.StatusLines,
                static line => line.Contains("Cannot stage notes.txt", StringComparison.Ordinal)
                    && line.Contains("denied", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(
                result.StatusLines,
                static line => line.Contains("not a regular file", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>
    /// Every way the non-blocking open can end names its own cause, so none of them is read as another.
    /// </summary>
    [Theory]
    [InlineData("NotFound", typeof(IOException), "no longer exists")]
    [InlineData("Rejected", typeof(IOException), "not a regular file")]
    [InlineData("AccessDenied", typeof(UnauthorizedAccessException), "denied")]
    [InlineData("IoError", typeof(IOException), "could not be opened")]
    public void Each_open_status_is_reported_as_its_own_cause(
        string statusName,
        Type expectedType,
        string expectedWords)
    {
        SecureFileOpenStatus status = Enum.Parse<SecureFileOpenStatus>(statusName);

        Exception failure = AttachableFile.OpenFailureFor(status);

        Assert.Equal(expectedType, failure.GetType());
        Assert.Contains(expectedWords, failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanOpenForRead(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);

            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string WriteNotes()
    {
        string path = Path.Combine(_root, "notes.txt");
        File.WriteAllText(path, "notes", Encoding.UTF8);
        return path;
    }

    private Task<TurnAttachmentBuildResult> BuildAsync(string prompt) =>
        CommandCenterTurnAttachmentBuilder.BuildAsync(
            prompt,
            workingDirectory: _root,
            preStagedPaths: [],
            settings: new ArcanumSettings { Features = new FeatureSettings { Scrying = true } },
            cancellationToken: CancellationToken.None);
}

internal static class PosixFifo
{
    public static bool TryCreate(string path)
    {
        using System.Diagnostics.Process? mkfifo = System.Diagnostics.Process.Start(
            new ProcessStartInfo("/usr/bin/mkfifo", [path]) { UseShellExecute = false });

        if (mkfifo is null)
        {
            return false;
        }

        mkfifo.WaitForExit();
        return mkfifo.ExitCode == 0 && File.Exists(path);
    }
}

/// <summary>
/// Submit reaches <see cref="CommandCenterChatRunner.RunTurnAsync"/> straight from the Terminal.Gui
/// key handler with nothing awaited in between, so anything the turn does before its first yield runs
/// on the main loop: no redraw, no spinner, and Ctrl+C never pumped.
/// </summary>
public sealed class CommandCenterTurnStartThreadingTests : IDisposable
{
    private static readonly TimeSpan AsyncTestTimeout = TimeSpan.FromSeconds(30);

    private readonly string _root;

    public CommandCenterTurnStartThreadingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "arcanum-cc-fifo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        _ = TestDirectoryCleanup.TryDelete(_root, nameof(CommandCenterTurnStartThreadingTests));
    }

    /// <summary>
    /// A FIFO in the workspace is the unbounded case: <c>File.Exists</c> reports it as a file, its length
    /// is 0 so the size guard passes, and opening it for reading blocks until a writer appears. Staging
    /// rejects anything that is not a regular file, so the turn reports why and carries on with the
    /// literal token instead of wedging the TUI.
    /// </summary>
    [SkippableFact]
    public async Task A_staged_fifo_is_refused_with_a_status_line_and_the_literal_token_stays()
    {
        Skip.If(
            OperatingSystem.IsWindows(),
            "mkfifo is POSIX-only; the blocking-read hazard this pins is a Unix file type.");

        string fifo = Path.Combine(_root, "trace.log");
        Assert.True(PosixFifo.TryCreate(fifo), "mkfifo did not create the FIFO.");

        CommandCenterChatRunner runner = CreateRunner();
        CommandCenterState state = new(new SessionLogBuffer()) { WorkingDirectory = _root };
        Channel<CommandCenterUiUpdate> updates = Channel.CreateUnbounded<CommandCenterUiUpdate>();

        // No writer ever pairs with the FIFO: the turn has to finish because staging refused it.
        await runner.RunTurnAsync("summarize @trace.log", state, updates.Writer, CancellationToken.None)
            .WaitAsync(AsyncTestTimeout);

        string transcript = state.Log.RenderPlainText();
        Assert.Contains("not a regular file", transcript, StringComparison.Ordinal);
        Assert.Contains("@trace.log", transcript, StringComparison.Ordinal);
        Assert.False(state.ThinkingActive);
    }

    /// <summary>
    /// Submit reaches <see cref="CommandCenterChatRunner.RunTurnAsync"/> straight from the Terminal.Gui key
    /// handler with nothing awaited in between, so whatever the turn does before its first yield runs on the
    /// main loop. The build is made to block outright, which is what a read inside the operating system
    /// does: the turn must hand the caller back while it is still blocked.
    /// </summary>
    [Fact]
    public async Task A_turn_hands_its_caller_back_while_the_attachment_build_is_still_blocked()
    {
        using ManualResetEventSlim buildStarted = new();
        using ManualResetEventSlim releaseBuild = new();
        CommandCenterChatRunner runner = CreateRunner(
            new TestOptionsMonitor(new ArcanumSettings()),
            BlockingBuild(buildStarted, releaseBuild));
        CommandCenterState state = new(new SessionLogBuffer()) { WorkingDirectory = _root };
        Channel<CommandCenterUiUpdate> updates = Channel.CreateUnbounded<CommandCenterUiUpdate>();

        Task turn = Task.CompletedTask;
        Thread mainLoop = new(() =>
            turn = runner.RunTurnAsync("hello", state, updates.Writer, CancellationToken.None))
        {
            IsBackground = true,
        };

        mainLoop.Start();
        bool yielded = mainLoop.Join(AsyncTestTimeout);

        try
        {
            Assert.True(
                yielded,
                "RunTurnAsync ran the attachment build on its caller; on the real submit path that caller "
                    + "is the Terminal.Gui main loop.");
            Assert.True(buildStarted.Wait(AsyncTestTimeout), "The attachment build never started.");
            Assert.False(turn.IsCompleted);
            Assert.True(state.ThinkingActive);
        }
        finally
        {
            releaseBuild.Set();
        }

        await turn.WaitAsync(AsyncTestTimeout);

        Assert.False(state.ThinkingActive);
    }

    /// <summary>
    /// Ctrl+C has to get the composer back even when the build can no longer be reached: a read parked in
    /// the operating system never observes the token. The turn walks away from it rather than waiting.
    /// </summary>
    [Fact]
    public async Task Cancelling_releases_a_turn_whose_attachment_build_is_stuck_in_the_operating_system()
    {
        using ManualResetEventSlim buildStarted = new();
        using ManualResetEventSlim releaseBuild = new();
        CommandCenterChatRunner runner = CreateRunner(
            new TestOptionsMonitor(new ArcanumSettings()),
            BlockingBuild(buildStarted, releaseBuild));
        CommandCenterState state = new(new SessionLogBuffer()) { WorkingDirectory = _root };
        Channel<CommandCenterUiUpdate> updates = Channel.CreateUnbounded<CommandCenterUiUpdate>();
        using CancellationTokenSource cts = new();

        Task run = runner.RunTurnAsync("hello", state, updates.Writer, cts.Token);

        try
        {
            Assert.True(buildStarted.Wait(AsyncTestTimeout), "The attachment build never started.");

            cts.Cancel();

            await run.WaitAsync(AsyncTestTimeout);

            Assert.False(releaseBuild.IsSet);
            Assert.False(state.ThinkingActive);
            Assert.Contains(
                "Cancelled before the message was sent.",
                state.Log.RenderPlainText(),
                StringComparison.Ordinal);
            Assert.DoesNotContain(state.Log.Snapshot(), static entry => entry.Streaming);
        }
        finally
        {
            releaseBuild.Set();
        }
    }

    /// <summary>
    /// Ctrl+C must always get the composer back. A staged FIFO used to park the build inside
    /// <c>open(O_RDONLY)</c> on a thread-pool thread that cancellation could not reach, so the turn never
    /// reached its <c>finally</c> and every later submit answered "Already generating".
    /// </summary>
    [SkippableFact]
    public async Task A_cancelled_turn_with_a_staged_fifo_returns_and_clears_thinking()
    {
        Skip.If(
            OperatingSystem.IsWindows(),
            "mkfifo is POSIX-only; the blocking-open hazard this pins is a Unix file type.");

        string fifo = Path.Combine(_root, "trace.log");
        Assert.True(PosixFifo.TryCreate(fifo), "mkfifo did not create the FIFO.");

        CommandCenterChatRunner runner = CreateRunner();
        CommandCenterState state = new(new SessionLogBuffer()) { WorkingDirectory = _root };
        Channel<CommandCenterUiUpdate> updates = Channel.CreateUnbounded<CommandCenterUiUpdate>();
        using CancellationTokenSource cts = new();

        Task run = runner.RunTurnAsync("summarize @trace.log", state, updates.Writer, cts.Token);
        cts.Cancel();

        await run.WaitAsync(AsyncTestTimeout);

        Assert.False(state.ThinkingActive);
        Assert.DoesNotContain(state.Log.Snapshot(), static entry => entry.Streaming);
    }

    /// <summary>
    /// The turn's finally is what gives the composer back, so it has to cover the setup too: a failure
    /// before the stream starts used to escape with the Thinking spinner still on.
    /// </summary>
    [Fact]
    public async Task A_turn_whose_setup_throws_still_clears_thinking_and_reports_the_error()
    {
        CommandCenterChatRunner runner = CreateRunner(new ThrowingOptionsMonitor());
        CommandCenterState state = new(new SessionLogBuffer()) { WorkingDirectory = _root };
        Channel<CommandCenterUiUpdate> updates = Channel.CreateUnbounded<CommandCenterUiUpdate>();

        await runner.RunTurnAsync("hello", state, updates.Writer, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(state.ThinkingActive);
        Assert.Contains("settings unavailable", state.Log.RenderPlainText(), StringComparison.Ordinal);
        Assert.DoesNotContain(state.Log.Snapshot(), static entry => entry.Streaming);
    }

    /// <summary>
    /// Longer than <see cref="AsyncTestTimeout"/>, so a turn that waits for the stuck build fails on its own
    /// wait rather than racing the build's own release.
    /// </summary>
    private static readonly TimeSpan StuckBuildCeiling = TimeSpan.FromMinutes(2);

    /// <summary>
    /// A build that never returns on its own, as a read stalled inside the operating system does: it
    /// ignores the token and holds its thread until the test releases it.
    /// </summary>
    private static AttachmentBuildDelegate BlockingBuild(
        ManualResetEventSlim started,
        ManualResetEventSlim release) =>
        (prompt, _, _, _, _) =>
        {
            started.Set();
            _ = release.Wait(StuckBuildCeiling);
            return Task.FromResult(new TurnAttachmentBuildResult(prompt, null, null, [], false));
        };

    private static CommandCenterChatRunner CreateRunner() =>
        CreateRunner(new TestOptionsMonitor(new ArcanumSettings()));

    private static CommandCenterChatRunner CreateRunner(
        IOptionsMonitor<ArcanumSettings> settingsMonitor,
        AttachmentBuildDelegate? buildAttachments = null)
    {
        string ndjson = JsonSerializer.Serialize(
            new IntelligenceEvent(IntelligenceEventType.Result, "done", "done"),
            ArcanumJsonContext.Default.IntelligenceEvent) + "\n";
        ArcanumApiClient client = new(
            new Factory(new StaticNdjsonHandler(ndjson)),
            ArcanumApiCredentialLeaseTestFactory.Create("test-key"));
        SessionWorkspaceService workspace = new(
            client,
            new NoopLastSessionStore(),
            NullLogger<SessionWorkspaceService>.Instance);
        CommandCenterHardModalArbiter arbiter = new();
        CommandCenterChatRunner runner = new(
            client,
            settingsMonitor,
            workspace,
            new CommandCenterHumanPromptCoordinator(client, arbiter),
            NullLogger<CommandCenterChatRunner>.Instance);

        if (buildAttachments is not null)
        {
            runner.BuildAttachmentsAsync = buildAttachments;
        }

        return runner;
    }

    private sealed class ThrowingOptionsMonitor : IOptionsMonitor<ArcanumSettings>
    {
        public ArcanumSettings CurrentValue => throw new InvalidOperationException("settings unavailable");

        public ArcanumSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ArcanumSettings, string?> listener) => null;
    }

    private sealed class StaticNdjsonHandler(string ndjson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ndjson, Encoding.UTF8, "application/x-ndjson"),
            });
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost:5001/"),
                Timeout = Timeout.InfiniteTimeSpan,
            };
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>("test-key");

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Ok("test-key"));

        public Task SaveApiKeyAsync(string apiKey) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => Task.CompletedTask;
    }

    private sealed class NoopLastSessionStore : ILastSessionStore
    {
        public Guid? GetLastSessionId() => null;

        public Task<ArcanumClientMutationResult<CliContextDocument>>
            SaveSessionIdAsync(
                Guid id,
                Func<Guid, CancellationToken, Task<Result<bool>>> revalidateAsync,
                CancellationToken cancellationToken) =>
            Task.FromResult(
                ArcanumClientMutationResult<CliContextDocument>.Completed(
                    CliContextDocument.Empty with { SessionId = id }));
    }

    private sealed class TestOptionsMonitor(ArcanumSettings current) : IOptionsMonitor<ArcanumSettings>
    {
        public ArcanumSettings CurrentValue { get; } = current;

        public ArcanumSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ArcanumSettings, string?> listener) => null;
    }
}

public sealed class AbandonableBlockingWorkTests
{
    private static readonly TimeSpan AsyncTestTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Completed_work_returns_its_value()
    {
        int value = await AbandonableBlockingWork.RunAsync(
            static () => Task.FromResult(7),
            CancellationToken.None);

        Assert.Equal(7, value);
    }

    [Fact]
    public async Task An_already_cancelled_token_never_starts_the_work()
    {
        bool started = false;
        using CancellationTokenSource cts = new();
        cts.Cancel();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AbandonableBlockingWork.RunAsync(
                () =>
                {
                    started = true;
                    return Task.FromResult(0);
                },
                cts.Token));

        Assert.False(started);
    }

    /// <summary>
    /// The case a plain <c>Task.Run(work, token)</c> gets wrong: once the delegate is parked in a system
    /// call the token no longer reaches it, so cancellation has to release the caller on its own.
    /// </summary>
    [Fact]
    public async Task Cancellation_releases_the_caller_while_the_work_is_still_blocked()
    {
        using ManualResetEventSlim running = new();
        using ManualResetEventSlim release = new();
        using CancellationTokenSource cts = new();

        Task<int> call = AbandonableBlockingWork.RunAsync(
            () =>
            {
                running.Set();
                release.Wait(AsyncTestTimeout);
                return Task.FromResult(1);
            },
            cts.Token);

        Assert.True(running.Wait(AsyncTestTimeout), "The work never started.");

        cts.Cancel();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(AsyncTestTimeout));
        Assert.False(release.IsSet);

        release.Set();
    }

    [Fact]
    public async Task A_fault_from_the_work_reaches_the_caller()
    {
        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AbandonableBlockingWork.RunAsync<int>(
                static () => throw new InvalidOperationException("boom"),
                CancellationToken.None));

        Assert.Equal("boom", thrown.Message);
    }
}

public sealed class CommandCenterAttachmentTests
{
    /// <summary>
    /// The composer thread stages paths and references while a turn on a worker thread snapshots the same
    /// sets and clears what it sent. Unsynchronised <c>HashSet</c> access made that throw (and can corrupt
    /// the set); one lock on the state now covers both sets.
    /// </summary>
    [Fact]
    public async Task Staging_while_a_turn_clears_its_snapshot_is_race_free()
    {
        CommandCenterState state = new(new SessionLogBuffer());
        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(2));
        ConcurrentQueue<Exception> failures = new();

        Task stager = Task.Run(() =>
        {
            int next = 0;
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    _ = state.StageAttachmentPath("staged-" + (next++ % 5_000));
                    _ = state.StageAttachmentReference(Guid.NewGuid());
                }
                catch (Exception ex)
                {
                    failures.Enqueue(ex);
                }
            }
        });

        Task turn = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    StagedAttachmentSnapshot snapshot = state.SnapshotStaged();
                    state.ClearStaged(snapshot);
                    _ = state.StagedAttachmentPaths.Count;
                    _ = state.StagedAttachmentReferences.Count;
                }
                catch (Exception ex)
                {
                    failures.Enqueue(ex);
                }
            }
        });

        await Task.WhenAll(stager, turn).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(
            failures.IsEmpty,
            $"{failures.Count} failures under concurrent staging; first: {failures.FirstOrDefault()}");
    }

    [Fact]
    public void Clearing_a_snapshot_keeps_what_was_staged_after_it_was_taken()
    {
        CommandCenterState state = new(new SessionLogBuffer());
        Guid before = Guid.NewGuid();
        Guid after = Guid.NewGuid();
        _ = state.StageAttachmentPath("sent.txt");
        _ = state.StageAttachmentReference(before);

        StagedAttachmentSnapshot snapshot = state.SnapshotStaged();
        _ = state.StageAttachmentPath("next.txt");
        _ = state.StageAttachmentReference(after);

        state.ClearStaged(snapshot);

        Assert.Equal(["next.txt"], state.StagedAttachmentPaths);
        Assert.Equal([after], state.StagedAttachmentReferences);
    }

    [Fact]
    public void The_exposed_sets_are_copies_so_a_caller_cannot_mutate_the_state_through_them()
    {
        CommandCenterState state = new(new SessionLogBuffer());
        _ = state.StageAttachmentPath("one.txt");

        IReadOnlyCollection<string> view = state.StagedAttachmentPaths;
        _ = state.StageAttachmentPath("two.txt");

        Assert.Equal(["one.txt"], view);
        Assert.Equal(2, state.StagedAttachmentPaths.Count);
    }
}

public sealed class ShellCommandParserAttachTests
{
    private readonly ShellCommandParser _parser = new();

    [Fact]
    public void Attach_with_path_is_allowlisted()
    {
        ParsedShellCommand parsed = _parser.Parse("/attach ./notes.txt");
        Assert.Equal(ShellCommandKind.Attach, parsed.Kind);
        Assert.Equal("./notes.txt", parsed.Argument);
    }

    [Fact]
    public void Attach_without_path_asks_for_argument()
    {
        ParsedShellCommand parsed = _parser.Parse("/attach");
        Assert.Equal(ShellCommandKind.Attach, parsed.Kind);
        Assert.True(string.IsNullOrEmpty(parsed.Argument));
    }

    [Fact]
    public void Attachments_list_is_allowlisted()
    {
        ParsedShellCommand parsed = _parser.Parse("/attachments");
        Assert.Equal(ShellCommandKind.AttachmentsList, parsed.Kind);
        Assert.Null(parsed.Argument);
        Assert.Null(parsed.Version);
    }

    [Theory]
    [InlineData("/attachments add notes", "notes", null)]
    [InlineData("/attachments add notes v2", "notes", 2)]
    [InlineData("/attachments add notes 2", "notes", 2)]
    [InlineData("/attachments add shot.png v1", "shot.png", 1)]
    public void Attachments_add_parses_logical_name_and_optional_version(
        string input,
        string logicalName,
        int? version)
    {
        ParsedShellCommand parsed = _parser.Parse(input);
        Assert.Equal(ShellCommandKind.AttachmentsAdd, parsed.Kind);
        Assert.Equal(logicalName, parsed.Argument);
        Assert.Equal(version, parsed.Version);
    }

    [Theory]
    [InlineData("/attachments reveal notes", "notes", null)]
    [InlineData("/attachments reveal notes v3", "notes", 3)]
    [InlineData("/attachments reveal notes 3", "notes", 3)]
    public void Attachments_reveal_parses_logical_name_and_optional_version(
        string input,
        string logicalName,
        int? version)
    {
        ParsedShellCommand parsed = _parser.Parse(input);
        Assert.Equal(ShellCommandKind.AttachmentsReveal, parsed.Kind);
        Assert.Equal(logicalName, parsed.Argument);
        Assert.Equal(version, parsed.Version);
    }

    [Theory]
    [InlineData("/attachments foo")]
    [InlineData("/attachments add")]
    [InlineData("/attachments reveal")]
    [InlineData("/attachments add notes extra junk")]
    [InlineData("/attachments reveal notes vX")]
    public void Unknown_attachments_forms_are_denied_with_usage(string input)
    {
        ParsedShellCommand parsed = _parser.Parse(input);
        Assert.Equal(ShellCommandKind.Denied, parsed.Kind);
        Assert.Contains("/attachments", parsed.DenialMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/attachment")]
    [InlineData("/attached")]
    [InlineData("/attaching")]
    public void Ambiguous_attach_star_forms_deny_and_mention_attachments(string input)
    {
        ParsedShellCommand parsed = _parser.Parse(input);
        Assert.Equal(ShellCommandKind.Denied, parsed.Kind);
        Assert.Contains("/attachments", parsed.DenialMessage, StringComparison.Ordinal);
        Assert.Contains("/attach", parsed.DenialMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Attachments_is_not_confused_with_attach()
    {
        ParsedShellCommand list = _parser.Parse("/attachments");
        ParsedShellCommand attach = _parser.Parse("/attach notes.txt");
        Assert.Equal(ShellCommandKind.AttachmentsList, list.Kind);
        Assert.Equal(ShellCommandKind.Attach, attach.Kind);
    }

    [Theory]

    [InlineData("/attachments refresh notes", "notes")]

    [InlineData("/attachments refresh notes.txt", "notes.txt")]

    public void Attachments_refresh_parses_logical_name(string input, string logicalName)

    {
        ParsedShellCommand parsed = _parser.Parse(input);

        Assert.Equal(ShellCommandKind.AttachmentsRefresh, parsed.Kind);

        Assert.Equal(logicalName, parsed.Argument);
    }
}

public sealed class SessionAttachmentRevealTests
{
    [Fact]
    public void Unattended_reveal_is_noop_with_status()
    {
        string status = SessionAttachmentReveal.TryReveal(
            "/tmp/example.txt",
            isInteractive: false,
            out bool started);

        Assert.False(started);
        Assert.Contains("interactive", status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Builds_macos_reveal_start_info_with_argument_list()
    {
        Assert.True(
            SessionAttachmentReveal.TryBuildStartInfo(
                "/tmp/notes.txt",
                OperatingSystemFamily.MacOS,
                out ProcessStartInfo? psi,
                out string? error));
        Assert.Null(error);
        Assert.NotNull(psi);
        Assert.Equal("open", psi!.FileName);
        Assert.False(psi.UseShellExecute);
        Assert.Equal(["-R", "/tmp/notes.txt"], psi.ArgumentList.ToArray());
    }

    [Fact]
    public void Builds_windows_reveal_start_info()
    {
        Assert.True(
            SessionAttachmentReveal.TryBuildStartInfo(
                @"C:\tmp\notes.txt",
                OperatingSystemFamily.Windows,
                out ProcessStartInfo? psi,
                out string? error));
        Assert.Null(error);
        Assert.NotNull(psi);
        Assert.Equal("explorer", psi!.FileName);
        Assert.False(psi.UseShellExecute);
        Assert.Equal([@"/select,C:\tmp\notes.txt"], psi.ArgumentList.ToArray());
    }

    [Fact]
    public void Builds_linux_reveal_start_info_for_parent_dir()
    {
        const string attachmentPath = "/tmp/dir/notes.txt";

        Assert.True(
            SessionAttachmentReveal.TryBuildStartInfo(
                attachmentPath,
                OperatingSystemFamily.Linux,
                out ProcessStartInfo? psi,
                out string? error));
        Assert.Null(error);
        Assert.NotNull(psi);
        Assert.Equal("xdg-open", psi!.FileName);
        Assert.False(psi.UseShellExecute);
        Assert.Equal([Path.GetDirectoryName(attachmentPath)!], psi.ArgumentList.ToArray());
    }
}

public sealed class ShellCommandDispatcherAttachmentsTests
{
    [Fact]
    public async Task Attachments_list_requires_session()
    {
        ShellCommandDispatcher dispatcher = CreateDispatcher(_ => Down());
        CommandCenterState state = new(new SessionLogBuffer());

        _ = await dispatcher.DispatchAsync("/attachments", state, CancellationToken.None);

        Assert.Contains("session", state.Log.RenderPlainText(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(state.StagedAttachmentReferences);
    }

    [Fact]
    public async Task Attachments_list_formats_api_rows_into_transcript()
    {
        Guid sessionId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Guid attachmentId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        SessionAttachmentDto[] payload =
        [
            new(
                attachmentId,
                "notes",
                "notes.txt",
                1,
                $"{sessionId:N}/notes/v1/notes.txt",
                "text/plain",
                11,
                SessionAttachmentKind.Text,
                "abc123",
                DateTimeOffset.Parse("2026-07-19T12:00:00Z")),
        ];

        ShellCommandDispatcher dispatcher = CreateDispatcher(req =>
        {
            Assert.Equal($"/api/sessions/{sessionId:D}/attachments", req.RequestUri!.AbsolutePath);
            return OkAttachments(payload);
        });
        CommandCenterState state = new(new SessionLogBuffer());
        state.ApplySessionMeta(sessionId, "S", "Active", 1);

        _ = await dispatcher.DispatchAsync("/attachments", state, CancellationToken.None);

        string text = state.Log.RenderPlainText();
        Assert.Contains("notes", text, StringComparison.Ordinal);
        Assert.Contains("v1", text, StringComparison.Ordinal);
        Assert.Contains("notes.txt", text, StringComparison.Ordinal);
    }

    [Fact]

    public async Task Attachments_list_renders_authoritative_snapshot_live_and_stale_badges_with_versions()

    {
        Guid sessionId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        DateTimeOffset observed = DateTimeOffset.Parse("2026-08-01T15:04:05Z");

        SessionAttachmentDto[] payload =

        [
            Attachment(sessionId, "snapshot", "SNAPSHOT-HASH"),

            Attachment(
                sessionId,

                "live",

                "LIVE-CONTENT-HASH",

                AttachmentSourceKind.WorkspaceFile,

                AttachmentSourceStatus.Refreshable,

                true,

                "src/live.txt",

                "LIVE-CONTENT-HASH",

                observed),

            Attachment(
                sessionId,

                "stale",

                "LOADED-CONTENT-HASH",

                AttachmentSourceKind.WorkspaceFile,

                AttachmentSourceStatus.PriorVersion,

                false,

                "src/stale.txt",

                "DISK-CONTENT-HASH",

                observed),
        ];

        ShellCommandDispatcher dispatcher = CreateDispatcher(_ => OkAttachments(payload));

        CommandCenterState state = new(new SessionLogBuffer());

        state.ApplySessionMeta(sessionId, "S", "Active", 1);

        _ = await dispatcher.DispatchAsync("/attachments", state, CancellationToken.None);

        string text = state.Log.RenderPlainText();

        Assert.Contains("[Snapshot]", text, StringComparison.Ordinal);

        Assert.Contains("[Live]", text, StringComparison.Ordinal);

        Assert.Contains("[Stale]", text, StringComparison.Ordinal);

        Assert.Contains("loaded=LIVE-CON", text, StringComparison.Ordinal);

        Assert.Contains("disk=DISK-CON", text, StringComparison.Ordinal);

        Assert.Contains("2026-08-01T15:04:05", text, StringComparison.Ordinal);
    }

    [Fact]

    public async Task Attachments_refresh_posts_selected_attachment_and_renders_backend_confirmation()

    {
        Guid sessionId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        Guid attachmentId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        SessionAttachmentDto row = Attachment(
            sessionId,

            "notes",

            "OLD-HASH",

            AttachmentSourceKind.WorkspaceFile,

            AttachmentSourceStatus.PriorVersion,

            false,

            "src/notes.txt",

            "NEW-HASH",

            DateTimeOffset.Parse("2026-08-01T15:00:00Z"),

            attachmentId);

        int requests = 0;

        ShellCommandDispatcher dispatcher = CreateDispatcher(request =>

        {
            requests++;

            if (request.Method == HttpMethod.Get)

            {
                return OkAttachments([row]);
            }

            Assert.Equal(HttpMethod.Post, request.Method);

            Assert.Equal(
                $"/api/sessions/{sessionId:D}/attachments/{attachmentId:D}/refresh",

                request.RequestUri!.AbsolutePath);

            return OkAttachmentRefresh(new AttachmentRefreshEvent(
                Guid.Parse("bbbbbbbb-bbbb-cccc-dddd-eeeeeeeeeeee"),

                "notes",

                2,

                true,

                false,

                "src/notes.txt",

                "CURRENT-HASH",

                12,

                DateTimeOffset.Parse("2026-08-01T15:05:00Z")));
        });

        CommandCenterState state = new(new SessionLogBuffer());

        state.ApplySessionMeta(sessionId, "S", "Active", 1);

        _ = await dispatcher.DispatchAsync("/attachments refresh notes", state, CancellationToken.None);

        string text = state.Log.RenderPlainText();

        Assert.Equal(3, requests);

        Assert.Contains("Live", text, StringComparison.Ordinal);

        Assert.Contains("v2", text, StringComparison.Ordinal);

        Assert.Contains("CURRENT-", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Attachments_add_stages_guid_reference()
    {
        Guid sessionId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Guid attachmentId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        SessionAttachmentDto[] payload =
        [
            new(
                attachmentId,
                "notes",
                "notes.txt",
                2,
                $"{sessionId:N}/notes/v2/notes.txt",
                "text/plain",
                11,
                SessionAttachmentKind.Text,
                "abc123",
                DateTimeOffset.Parse("2026-07-19T12:00:00Z")),
            new(
                Guid.Parse("bbbbbbbb-bbbb-cccc-dddd-eeeeeeeeeeee"),
                "notes",
                "notes.txt",
                1,
                $"{sessionId:N}/notes/v1/notes.txt",
                "text/plain",
                10,
                SessionAttachmentKind.Text,
                "def456",
                DateTimeOffset.Parse("2026-07-19T11:00:00Z")),
        ];

        ShellCommandDispatcher dispatcher = CreateDispatcher(_ => OkAttachments(payload));
        CommandCenterState state = new(new SessionLogBuffer());
        state.ApplySessionMeta(sessionId, "S", "Active", 1);

        _ = await dispatcher.DispatchAsync("/attachments add notes", state, CancellationToken.None);

        Assert.Contains(attachmentId, state.StagedAttachmentReferences);
        Assert.DoesNotContain(
            Guid.Parse("bbbbbbbb-bbbb-cccc-dddd-eeeeeeeeeeee"),
            state.StagedAttachmentReferences);
        Assert.Contains("Staged", state.Log.RenderPlainText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Attachments_add_with_version_stages_that_version()
    {
        Guid sessionId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Guid v1 = Guid.Parse("bbbbbbbb-bbbb-cccc-dddd-eeeeeeeeeeee");
        Guid v2 = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        SessionAttachmentDto[] payload =
        [
            new(v2, "notes", "notes.txt", 2, "p/v2", "text/plain", 11, SessionAttachmentKind.Text, "a", DateTimeOffset.UtcNow),
            new(v1, "notes", "notes.txt", 1, "p/v1", "text/plain", 10, SessionAttachmentKind.Text, "b", DateTimeOffset.UtcNow),
        ];

        ShellCommandDispatcher dispatcher = CreateDispatcher(_ => OkAttachments(payload));
        CommandCenterState state = new(new SessionLogBuffer());
        state.ApplySessionMeta(sessionId, "S", "Active", 1);

        _ = await dispatcher.DispatchAsync("/attachments add notes v1", state, CancellationToken.None);

        Assert.Contains(v1, state.StagedAttachmentReferences);
        Assert.DoesNotContain(v2, state.StagedAttachmentReferences);
    }

    [Fact]
    public async Task Attachments_reveal_reports_absolute_path_under_attachments_directory()
    {
        Guid sessionId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Guid attachmentId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        string relative = $"{sessionId:N}/notes/v1/notes.txt";
        SessionAttachmentDto[] payload =
        [
            new(
                attachmentId,
                "notes",
                "notes.txt",
                1,
                relative,
                "text/plain",
                11,
                SessionAttachmentKind.Text,
                "abc123",
                DateTimeOffset.UtcNow),
        ];

        ShellCommandDispatcher dispatcher = CreateDispatcher(_ => OkAttachments(payload));
        CommandCenterState state = new(new SessionLogBuffer());
        state.ApplySessionMeta(sessionId, "S", "Active", 1);

        _ = await dispatcher.DispatchAsync("/attachments reveal notes", state, CancellationToken.None);

        string expected = Path.GetFullPath(Path.Combine(ArcanumPaths.AttachmentsDirectory, relative));
        string text = state.Log.RenderPlainText();
        Assert.Contains(expected, text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sent_snapshot_clears_the_staged_references_it_carried()
    {
        CommandCenterState state = new(new SessionLogBuffer());
        Guid id = Guid.NewGuid();
        _ = state.StageAttachmentReference(id);
        Assert.Single(state.StagedAttachmentReferences);

        state.ClearStaged(state.SnapshotStaged());

        Assert.Empty(state.StagedAttachmentReferences);
    }

    private static ShellCommandDispatcher CreateDispatcher(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        RecordingHandler handler = new(respond);
        ArcanumApiClient client = new(
            new Factory(handler),
            ArcanumApiCredentialLeaseTestFactory.Create("test-key"));
        SessionWorkspaceService workspace = new(
            client,
            new NoopLastSessionStore(),
            NullLogger<SessionWorkspaceService>.Instance);
        return new ShellCommandDispatcher(
            client,
            new ShellCommandParser(),
            new TestOptionsMonitor(new ArcanumSettings()),
            workspace,
            NullLogger<ShellCommandDispatcher>.Instance);
    }

    private static HttpResponseMessage OkAttachments(SessionAttachmentDto[] payload)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            new ApiResponse<SessionAttachmentDto[]>(payload, true, null),
            ArcanumJsonContext.Default.ApiResponseSessionAttachmentDtoArray);
        HttpResponseMessage response = new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(json),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return response;
    }

    private static SessionAttachmentDto Attachment(
        Guid sessionId,

        string logicalKey,

        string contentSha256,

        AttachmentSourceKind sourceKind = AttachmentSourceKind.SnapshotOnly,

        AttachmentSourceStatus sourceStatus = AttachmentSourceStatus.NotApplicable,

        bool isRefreshable = false,

        string? sourceRelativePath = null,

        string? observedSha256 = null,

        DateTimeOffset? observedWriteTime = null,

        Guid? id = null) =>

        new(
            id ?? Guid.NewGuid(),

            logicalKey,

            logicalKey + ".txt",

            1,

            $"{sessionId:N}/{logicalKey}/v1/{logicalKey}.txt",

            "text/plain",

            11,

            SessionAttachmentKind.Text,

            contentSha256,

            DateTimeOffset.Parse("2026-08-01T14:00:00Z"),

            sourceKind,

            SourceWorkspaceIdentity: sourceKind == AttachmentSourceKind.WorkspaceFile ? "workspace" : null,

            sourceRelativePath,

            isRefreshable,

            sourceStatus,

            LastObservedSourceContentSha256: observedSha256,

            LastObservedSourceWriteTime: observedWriteTime);

    private static HttpResponseMessage OkAttachmentRefresh(AttachmentRefreshEvent payload)

    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            new ApiResponse<AttachmentRefreshEvent>(payload, true, null),

            ArcanumJsonContext.Default.ApiResponseAttachmentRefreshEvent);

        HttpResponseMessage response = new(HttpStatusCode.OK)

        {
            Content = new ByteArrayContent(json),
        };

        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        return response;
    }

    private static HttpResponseMessage Down() =>
        new(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("""{"success":false,"error":{"code":"Test.Down","message":"down"}}"""),
        };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost:5001/") };
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>("test-key");

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Ok("test-key"));

        public Task SaveApiKeyAsync(string apiKey) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => Task.CompletedTask;
    }

    private sealed class NoopLastSessionStore : ILastSessionStore
    {
        public Guid? GetLastSessionId() => null;

        public Task<ArcanumClientMutationResult<CliContextDocument>>
            SaveSessionIdAsync(
                Guid id,
                Func<Guid, CancellationToken, Task<Result<bool>>> revalidateAsync,
                CancellationToken cancellationToken) =>
            Task.FromResult(
                ArcanumClientMutationResult<CliContextDocument>.Completed(
                    CliContextDocument.Empty with { SessionId = id }));
    }

    private sealed class TestOptionsMonitor(ArcanumSettings current) : IOptionsMonitor<ArcanumSettings>
    {
        public ArcanumSettings CurrentValue { get; } = current;

        public ArcanumSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ArcanumSettings, string?> listener) => null;
    }
}

public sealed class CommandCenterAttachmentDriftMonitorTests

{
    [Fact]

    public void Backend_snapshot_is_the_only_authority_for_live_to_stale_transition()

    {
        Guid id = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        CommandCenterState state = new(new SessionLogBuffer())

        {
            SessionAttachments =

            [
                Attachment(id, AttachmentSourceStatus.Refreshable, true, "LOADED"),
            ],
        };

        IReadOnlyList<string> notices = CommandCenterAttachmentDriftMonitor.ApplyBackendSnapshot(
            state,

            [Attachment(id, AttachmentSourceStatus.PriorVersion, false, "DISK")]);

        string notice = Assert.Single(notices);

        Assert.Contains("[Stale]", notice, StringComparison.Ordinal);

        Assert.Contains("DISK", notice, StringComparison.Ordinal);

        Assert.Equal(AttachmentSourceStatus.PriorVersion, state.SessionAttachments[0].SourceStatus);
    }

    private static SessionAttachmentDto Attachment(
        Guid id,

        AttachmentSourceStatus status,

        bool refreshable,

        string observedHash) =>

        new(
            id,

            "notes",

            "notes.txt",

            1,

            "session/notes/v1/notes.txt",

            "text/plain",

            10,

            SessionAttachmentKind.Text,

            "LOADED",

            DateTimeOffset.Parse("2026-08-01T14:00:00Z"),

            AttachmentSourceKind.WorkspaceFile,

            "workspace",

            "src/notes.txt",

            refreshable,

            status,

            LastObservedSourceContentSha256: observedHash,

            LastObservedSourceWriteTime: DateTimeOffset.Parse("2026-08-01T15:00:00Z"));
}
