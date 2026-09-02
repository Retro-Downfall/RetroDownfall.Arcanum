using System.Diagnostics;

using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Security;

[Collection("WorkspacePathPolicy")]
public sealed class SecureFileReaderTests : IAsyncLifetime
{
    private TempWorkspace _workspace = null!;

    public async Task InitializeAsync()
    {
        _workspace = new TempWorkspace();

        await _workspace.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        SecureFileReader.AfterOpenForTests = null;

        FileHandleIdentityInterop.TryGetHandleMetadataForTests = null;

        await _workspace.DisposeAsync();
    }

    [Fact]
    public void TryOpenRegularFile_returns_handle_bound_regular_file()
    {
        string path = _workspace.WriteFile("regular.txt", "regular");

        SecureFileOpenStatus status =
            SecureFileReader.TryOpenRegularFile(
                path,
                expectedIdentity: null,
                out FileStream? stream,
                out FileHandleMetadata metadata);

        Assert.Equal(SecureFileOpenStatus.Success, status);

        Assert.NotNull(stream);

        Assert.Equal(FileSystemObjectKind.RegularFile, metadata.Kind);

        Assert.Equal(1UL, metadata.HardLinkCount);

        using (stream)
        using (StreamReader reader = new(stream))
        {
            Assert.Equal("regular", reader.ReadToEnd());

            if (!OperatingSystem.IsWindows())
            {
                (int descriptorFlags, int statusFlags) =
                    SecureFileReader.GetUnixHandleFlagsForTests(
                        stream.SafeFileHandle);

                Assert.NotEqual(-1, descriptorFlags);

                Assert.NotEqual(-1, statusFlags);

                Assert.NotEqual(0, descriptorFlags & FileDescriptorCloseOnExec);

                int nonBlocking = OperatingSystem.IsMacOS()
                    ? MacOsOpenNonBlocking
                    : LinuxOpenNonBlocking;

                Assert.NotEqual(0, statusFlags & nonBlocking);
            }
        }
    }

    [Fact]
    public async Task ReadBytesAsync_reports_missing_file()
    {
        using SecureFileReadResult result =
            await SecureFileReader.ReadBytesAsync(
                Path.Combine(_workspace.Root, "missing.txt"),
                maxBytes: 32,
                CancellationToken.None);

        Assert.Equal(SecureFileReadStatus.NotFound, result.Status);

        Assert.True(result.Bytes.IsEmpty);
    }

    [Fact]
    public async Task ReadBytesAsync_observes_cancellation()
    {
        string path = _workspace.WriteFile(
            "cancel.txt",
            new string('x', 8192));

        using CancellationTokenSource cancellation = new();

        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => SecureFileReader.ReadBytesAsync(
                path,
                maxBytes: 8192,
                cancellation.Token));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public async Task ReadBytesAsync_open_stream_rejects_invalid_caps(
        int maxBytes)
    {
        string path = _workspace.WriteFile(
            "invalid-cap.txt",
            "content");

        Assert.Equal(
            SecureFileOpenStatus.Success,
            SecureFileReader.TryOpenRegularFile(
                path,
                expectedIdentity: null,
                out FileStream? stream,
                out _));

        await using (FileStream openedStream = stream!)
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => SecureFileReader.ReadBytesAsync(
                    openedStream,
                    maxBytes,
                    CancellationToken.None));
        }
    }

    [Fact]
    public async Task ReadBytesAsync_open_stream_rejects_unknown_metadata()
    {
        string path = _workspace.WriteFile(
            "unknown-after-open.txt",
            "content");

        Assert.Equal(
            SecureFileOpenStatus.Success,
            SecureFileReader.TryOpenRegularFile(
                path,
                expectedIdentity: null,
                out FileStream? stream,
                out _));

        await using (FileStream openedStream = stream!)
        {
            FileHandleIdentityInterop.TryGetHandleMetadataForTests =
                _ => null;

            using SecureFileReadResult result =
                await SecureFileReader.ReadBytesAsync(
                    openedStream,
                    maxBytes: 32,
                    CancellationToken.None);

            Assert.Equal(
                SecureFileReadStatus.Rejected,
                result.Status);
        }
    }

    [Fact]
    public async Task ReadUtf8TextAsync_accepts_utf8_bom()
    {
        string path = Path.Combine(
            _workspace.Root,
            "bom.txt");

        await File.WriteAllBytesAsync(
            path,
            [0xef, 0xbb, 0xbf, 0x66, 0x6f, 0x6f]);

        SecureUtf8FileReadResult result =
            await SecureFileReader.ReadUtf8TextAsync(
                path,
                maxBytes: 32,
                CancellationToken.None);

        Assert.Equal(
            SecureFileReadStatus.Success,
            result.Status);

        Assert.Equal("foo", result.Text);

        Assert.Equal(6, result.ByteLength);
    }

    [Fact]
    public async Task ReadUtf8TextAsync_rejects_malformed_utf8()
    {
        string path = Path.Combine(
            _workspace.Root,
            "malformed.txt");

        await File.WriteAllBytesAsync(
            path,
            [0x66, 0x80, 0x6f]);

        SecureUtf8FileReadResult result =
            await SecureFileReader.ReadUtf8TextAsync(
                path,
                maxBytes: 32,
                CancellationToken.None);

        Assert.Equal(
            SecureFileReadStatus.InvalidUtf8,
            result.Status);

        Assert.Null(result.Text);
    }

    [Fact]
    public async Task ReadUtf8TextAsync_propagates_secure_open_failure()
    {
        SecureUtf8FileReadResult result =
            await SecureFileReader.ReadUtf8TextAsync(
                Path.Combine(_workspace.Root, "missing-utf8.txt"),
                maxBytes: 32,
                CancellationToken.None);

        Assert.Equal(
            SecureFileReadStatus.NotFound,
            result.Status);

        Assert.Null(result.Text);
    }

    [SkippableFact]
    public void TryOpenRegularFile_rejects_symlink()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "Symlink rejection is exercised on Unix hosts.");

        string target = _workspace.WriteFile("target.txt", "target");

        string link = Path.Combine(_workspace.Root, "link.txt");

        File.CreateSymbolicLink(link, target);

        SecureFileOpenStatus status =
            SecureFileReader.TryOpenRegularFile(
                link,
                expectedIdentity: null,
                out FileStream? stream,
                out _);

        Assert.Equal(SecureFileOpenStatus.Rejected, status);

        Assert.Null(stream);
    }

    [SkippableFact]
    public void TryOpenRegularFile_on_windows_rejects_reparse_symlink()
    {
        Skip.IfNot(
            OperatingSystem.IsWindows(),
            "Windows reparse-point behavior is platform-specific.");

        string target = _workspace.WriteFile(
            "windows-target.txt",
            "target");

        string link = Path.Combine(
            _workspace.Root,
            "windows-link.txt");

        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException)
        {
            Skip.If(
                true,
                "Windows symlink creation is unavailable: "
                + exception.GetType().Name);
        }

        SecureFileOpenStatus status =
            SecureFileReader.TryOpenRegularFile(
                link,
                expectedIdentity: null,
                out FileStream? stream,
                out _);

        Assert.Equal(SecureFileOpenStatus.Rejected, status);

        Assert.Null(stream);
    }

    [Fact]
    public void TryOpenRegularFile_rejects_hard_link()
    {
        string target = _workspace.WriteFile("target.txt", "target");

        string alias = Path.Combine(_workspace.Root, "alias.txt");

        Assert.True(HardLinkTestSupport.TryCreate(alias, target));

        SecureFileOpenStatus status =
            SecureFileReader.TryOpenRegularFile(
                target,
                expectedIdentity: null,
                out FileStream? stream,
                out _);

        Assert.Equal(SecureFileOpenStatus.Rejected, status);

        Assert.Null(stream);
    }

    [Fact]
    public void TryOpenRegularFile_fails_closed_for_unknown_handle_metadata()
    {
        string path = _workspace.WriteFile("unknown.txt", "unknown");

        FileHandleIdentityInterop.TryGetHandleMetadataForTests = _ => null;

        SecureFileOpenStatus status =
            SecureFileReader.TryOpenRegularFile(
                path,
                expectedIdentity: null,
                out FileStream? stream,
                out _);

        Assert.Equal(SecureFileOpenStatus.Rejected, status);

        Assert.Null(stream);
    }

    [SkippableFact]
    public async Task TryOpenRegularFile_rejects_fifo_without_blocking()
    {
        Skip.IfNot(
            OperatingSystem.IsMacOS()
            || OperatingSystem.IsLinux(),
            "FIFO semantics are Unix-only.");

        string mkfifo = "/usr/bin/mkfifo";

        Skip.IfNot(
            File.Exists(mkfifo),
            "The mkfifo utility is unavailable.");

        string fifo = Path.Combine(_workspace.Root, "input.fifo");

        using global::System.Diagnostics.Process creator = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = mkfifo,
                ArgumentList = { fifo },
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        Assert.True(creator.Start());

        using (CancellationTokenSource creatorDeadline =
               new(TimeSpan.FromSeconds(5)))
        {
            await creator.WaitForExitAsync(creatorDeadline.Token);
        }

        Assert.Equal(0, creator.ExitCode);

        Task<(SecureFileOpenStatus Status, FileStream? Stream)> openTask =
            Task.Run(() =>
            {
                SecureFileOpenStatus status =
                    SecureFileReader.TryOpenRegularFile(
                        fifo,
                        expectedIdentity: null,
                        out FileStream? stream,
                        out _);

                return (status, stream);
            });

        try
        {
            (SecureFileOpenStatus status, FileStream? stream) =
                await openTask.WaitAsync(TimeSpan.FromSeconds(2));

            stream?.Dispose();

            Assert.Equal(SecureFileOpenStatus.Rejected, status);

            Assert.Null(stream);
        }
        catch (TimeoutException)
        {
            await UnblockFifoOpenAsync(fifo, openTask);

            Assert.Fail(
                "Secure FIFO open exceeded its bounded watchdog.");
        }
    }

    [SkippableFact]
    public void TryOpenRegularFile_rejects_unix_character_device()
    {
        Skip.IfNot(
            OperatingSystem.IsMacOS()
            || OperatingSystem.IsLinux(),
            "Unix device semantics are platform-specific.");

        const string device = "/dev/null";

        Skip.IfNot(
            File.Exists(device),
            "The null device is unavailable.");

        SecureFileOpenStatus status =
            SecureFileReader.TryOpenRegularFile(
                device,
                expectedIdentity: null,
                out FileStream? stream,
                out _);

        Assert.Equal(SecureFileOpenStatus.Rejected, status);

        Assert.Null(stream);
    }

    private static async Task UnblockFifoOpenAsync(
        string fifo,
        Task<(SecureFileOpenStatus Status, FileStream? Stream)> openTask)
    {
        using global::System.Diagnostics.Process writer = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                ArgumentList =
                {
                    "-c",
                    "printf x > \"$1\"",
                    "arcanum-fifo-watchdog",
                    fifo,
                },
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        Assert.True(writer.Start());

        (SecureFileOpenStatus _, FileStream? stream) =
            await openTask.WaitAsync(TimeSpan.FromSeconds(2));

        stream?.Dispose();

        if (!writer.WaitForExit(2000))
        {
            writer.Kill(entireProcessTree: true);

            Assert.True(writer.WaitForExit(2000));
        }
    }

    private const int FileDescriptorCloseOnExec = 1;

    private const int LinuxOpenNonBlocking = 0x00000800;

    private const int MacOsOpenNonBlocking = 0x00000004;

}
