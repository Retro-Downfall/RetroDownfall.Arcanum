using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Platform;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Infrastructure.Platform;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Process;

public sealed class ChildProcessBoundaryBehaviorTests
{

    [Fact]
    public void FilesystemJail_rejects_null_arguments_before_platform_dispatch()
    {

        ChildProcessSandboxRequest request = new()
        {
            ReadWriteRoots = [],
            ReadExecuteRoots = [],
        };

        Assert.Throws<ArgumentNullException>(() =>
            ChildProcessFilesystemJail.Apply(null!, request, NullLogger.Instance));

        Assert.Throws<ArgumentNullException>(() =>
            ChildProcessFilesystemJail.Apply(
                new ProcessStartInfo { FileName = "ignored" },
                null!,
                NullLogger.Instance));

    }

    [Fact]
    public void CleanupTempPaths_deletes_matching_file_and_directory_and_accepts_missing_owned_artifact()
    {

        string root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "arcanum-cleanup-" + Guid.NewGuid().ToString("N"))).FullName;

        string file = Path.Combine(root, "profile.sb");

        string directory = Path.Combine(root, "invocation-temp");

        string missing = Path.Combine(root, "already-gone");

        File.WriteAllText(file, "profile");

        Directory.CreateDirectory(directory);

        File.WriteAllText(Path.Combine(directory, "child.tmp"), "temporary");

        try
        {
            Assert.True(
                IdentityOwnedFileSystemCleanup.TryCapturePath(
                    file,
                    FileSystemObjectKind.RegularFile,
                    out IdentityOwnedFileSystemArtifact fileArtifact));

            Assert.True(
                IdentityOwnedFileSystemCleanup.TryCapturePath(
                    directory,
                    FileSystemObjectKind.Directory,
                    out IdentityOwnedFileSystemArtifact directoryArtifact));

            File.WriteAllText(missing, "already gone");

            Assert.True(
                IdentityOwnedFileSystemCleanup.TryCapturePath(
                    missing,
                    FileSystemObjectKind.RegularFile,
                    out IdentityOwnedFileSystemArtifact missingArtifact));

            File.Delete(missing);

            bool cleaned =
                ChildProcessFilesystemJail.CleanupTempPaths(
                    [
                        missingArtifact,
                        fileArtifact,
                        directoryArtifact,
                    ]);

            Assert.False(File.Exists(file));

            Assert.False(Directory.Exists(directory));

            Assert.True(Directory.Exists(root));

            Assert.True(cleaned);

        }
        finally
        {

            if (Directory.Exists(root))
            {

                Directory.Delete(root, recursive: true);

            }

        }

    }

    [Fact]
    public void CleanupTempPaths_retains_file_replacement_with_different_identity()
    {
        string root = Directory.CreateDirectory(
            Path.Combine(
                Path.GetTempPath(),
                "arcanum-cleanup-file-swap-"
                + Guid.NewGuid().ToString("N"))).FullName;

        string file = Path.Combine(root, "profile.sb");

        File.WriteAllText(file, "owned");

        try
        {
            Assert.True(
                IdentityOwnedFileSystemCleanup.TryCapturePath(
                    file,
                    FileSystemObjectKind.RegularFile,
                    out IdentityOwnedFileSystemArtifact artifact));

            File.Delete(file);

            File.WriteAllText(file, "external replacement");

            bool cleaned =
                ChildProcessFilesystemJail.CleanupTempPaths(
                    [artifact]);

            Assert.False(cleaned);

            Assert.Equal(
                "external replacement",
                File.ReadAllText(file));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CleanupTempPaths_retains_directory_replacement_with_different_identity()
    {
        string root = Directory.CreateDirectory(
            Path.Combine(
                Path.GetTempPath(),
                "arcanum-cleanup-directory-swap-"
                + Guid.NewGuid().ToString("N"))).FullName;

        string directory = Directory.CreateDirectory(
            Path.Combine(root, "invocation-temp")).FullName;

        try
        {
            Assert.True(
                IdentityOwnedFileSystemCleanup.TryCapturePath(
                    directory,
                    FileSystemObjectKind.Directory,
                    out IdentityOwnedFileSystemArtifact artifact));

            Directory.Delete(directory);

            Directory.CreateDirectory(directory);

            File.WriteAllText(
                Path.Combine(directory, "external.txt"),
                "external replacement");

            bool cleaned =
                ChildProcessFilesystemJail.CleanupTempPaths(
                    [artifact]);

            Assert.False(cleaned);

            Assert.Equal(
                "external replacement",
                File.ReadAllText(
                    Path.Combine(
                        directory,
                        "external.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [SkippableFact]
    public void CleanupTempPaths_retains_symlink_replacement()
    {
        Skip.IfNot(
            OperatingSystem.IsMacOS()
            || OperatingSystem.IsLinux(),
            "Symlink replacement semantics are Unix-only.");

        string root = Directory.CreateDirectory(
            Path.Combine(
                Path.GetTempPath(),
                "arcanum-cleanup-symlink-swap-"
                + Guid.NewGuid().ToString("N"))).FullName;

        string file = Path.Combine(root, "profile.sb");

        string target = Path.Combine(root, "external.txt");

        File.WriteAllText(file, "owned");

        File.WriteAllText(target, "external");

        try
        {
            Assert.True(
                IdentityOwnedFileSystemCleanup.TryCapturePath(
                    file,
                    FileSystemObjectKind.RegularFile,
                    out IdentityOwnedFileSystemArtifact artifact));

            File.Delete(file);

            File.CreateSymbolicLink(file, target);

            bool cleaned =
                ChildProcessFilesystemJail.CleanupTempPaths(
                    [artifact]);

            Assert.False(cleaned);

            Assert.NotNull(File.ResolveLinkTarget(file, false));

            Assert.Equal("external", File.ReadAllText(target));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CleanupTempPathsAsync_without_remaining_cleanup_time_returns_without_recursive_delete()
    {
        string root = Directory.CreateDirectory(
            Path.Combine(
                Path.GetTempPath(),
                "arcanum-cleanup-deadline-"
                + Guid.NewGuid().ToString("N"))).FullName;
        string directory = Directory.CreateDirectory(
            Path.Combine(root, "invocation-temp")).FullName;
        File.WriteAllText(
            Path.Combine(directory, "child.tmp"),
            "temporary");

        try
        {
            Assert.True(
                IdentityOwnedFileSystemCleanup.TryCapturePath(
                    directory,
                    FileSystemObjectKind.Directory,
                    out IdentityOwnedFileSystemArtifact artifact));

            bool cleaned =
                await ChildProcessFilesystemJail.CleanupTempPathsAsync(
                    [artifact],
                    TimeSpan.Zero);

            Assert.False(cleaned);
            Assert.True(Directory.Exists(directory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CleanupTempPathsAsync_bounds_an_in_progress_recursive_delete()
    {
        using ManualResetEventSlim releaseCleanup = new(false);
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            bool cleaned =
                await ChildProcessFilesystemJail.CleanupTempPathsAsync(
                    [default],
                    TimeSpan.FromMilliseconds(100),
                    _ =>
                    {
                        releaseCleanup.Wait();

                        return true;
                    });

            stopwatch.Stop();
            Assert.False(cleaned);
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(2),
                $"Cleanup exceeded its bound: {stopwatch.Elapsed}.");
        }
        finally
        {
            releaseCleanup.Set();
        }
    }

    [SkippableFact]
    public void CleanupTempPaths_on_windows_ignores_locked_file_and_continues()
    {

        Skip.IfNot(
            OperatingSystem.IsWindows(),
            "Locked-file deletion semantics require Windows.");

        string root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "arcanum-cleanup-locked-" + Guid.NewGuid().ToString("N"))).FullName;

        string lockedPath = Path.Combine(root, "locked.tmp");

        string removablePath = Path.Combine(root, "removable.tmp");

        File.WriteAllText(lockedPath, "locked");

        File.WriteAllText(removablePath, "remove");

        try
        {
            Assert.True(
                IdentityOwnedFileSystemCleanup.TryCapturePath(
                    lockedPath,
                    FileSystemObjectKind.RegularFile,
                    out IdentityOwnedFileSystemArtifact lockedArtifact));

            Assert.True(
                IdentityOwnedFileSystemCleanup.TryCapturePath(
                    removablePath,
                    FileSystemObjectKind.RegularFile,
                    out IdentityOwnedFileSystemArtifact removableArtifact));

            using (FileStream locked = new(
                       lockedPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.None))
            {

                bool cleaned =
                    ChildProcessFilesystemJail.CleanupTempPaths(
                        [
                            lockedArtifact,
                            removableArtifact,
                        ]);

                Assert.True(File.Exists(lockedPath));

                Assert.False(File.Exists(removablePath));

                Assert.False(cleaned);

            }

            Assert.True(
                ChildProcessFilesystemJail.CleanupTempPaths(
                    [lockedArtifact]));

            Assert.False(File.Exists(lockedPath));

        }
        finally
        {

            if (Directory.Exists(root))
            {

                Directory.Delete(root, recursive: true);

            }

        }

    }

    [Theory]
    [InlineData(null)]
    [InlineData("bounded cleanup")]
    public void ProcessTreeKiller_swallows_unassociated_process_race(string? context)
    {

        using global::System.Diagnostics.Process process = new();

        Action kill = () =>
            ProcessTreeKiller.TryKillEntireTree(process, NullLogger.Instance, context);

        Exception? error = Record.Exception(kill);

        Assert.Null(error);

    }

    [SkippableFact]
    public void ProcessTreeKiller_leaves_already_exited_process_unchanged()
    {

        Skip.IfNot(
            OperatingSystem.IsWindows(),
            "cmd.exe process behavior requires Windows.");

        using global::System.Diagnostics.Process process = new()
        {
            StartInfo = CreateCmdStartInfo("exit /b 0"),
        };

        Assert.True(process.Start());

        Assert.True(process.WaitForExit(5000));

        ProcessTreeKiller.TryKillEntireTree(
            process,
            NullLogger.Instance,
            "already exited");

        Assert.True(process.HasExited);

        Assert.Equal(0, process.ExitCode);

    }

    [SkippableFact]
    public void ProcessTreeKiller_terminates_bounded_waiting_process()
    {

        Skip.IfNot(
            OperatingSystem.IsWindows(),
            "Windows process-tree termination requires Windows.");

        using global::System.Diagnostics.Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                ArgumentList =
                {
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    "Start-Sleep -Seconds 30",
                },
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        Assert.True(process.Start());

        try
        {

            Assert.False(
                process.HasExited,
                "The bounded child must still be alive before the kill is attempted.");

            Stopwatch killStopwatch = Stopwatch.StartNew();

            ProcessTreeKiller.TryKillEntireTree(
                process,
                NullLogger.Instance,
                "bounded wait");

            Assert.True(
                process.WaitForExit(5000),
                "The bounded child should terminate promptly after tree kill.");

            killStopwatch.Stop();

            Assert.True(process.HasExited);

            Assert.True(
                killStopwatch.Elapsed < TimeSpan.FromSeconds(5),
                $"Expected termination well before the 30-second natural exit; took {killStopwatch.Elapsed}.");

        }
        finally
        {

            if (!process.HasExited)
            {

                process.Kill(entireProcessTree: true);

                process.WaitForExit(5000);

            }

        }

    }

    [Fact]
    public async Task Runner_returns_resource_limit_apply_error_without_starting_process()
    {

        RecordingLimiter limiter = new()
        {
            ApplyError = new ResourceLimitError("resource limiter rejected test invocation"),
        };

        ProcessStartInfo startInfo = CreateCmdStartInfo("exit /b 99");

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            startInfo,
            ChildProcessEnvironmentProfile.SpellScript,
            totalOutputCapBytes: 1,
            timeout: TimeSpan.FromSeconds(5),
            resourceLimits: new ResourceLimits(),
            resourceLimiter: limiter,
            CancellationToken.None);

        Assert.Equal(CappedChildProcessOutcome.ResourceLimitApplyFailed, result.Outcome);

        Assert.Equal("resource limiter rejected test invocation", result.ResourceLimitApplyError);

        Assert.Equal(1024, result.PerStreamCapBytes);

        Assert.Equal(1, limiter.ApplyCount);

        Assert.Equal(0, limiter.AssignCount);

        Assert.Empty(limiter.CleanupPids);

    }

    [Fact]
    public async Task Runner_reports_invalid_operation_when_executable_name_is_empty()
    {

        ProcessStartInfo startInfo = new()
        {
            FileName = string.Empty,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            startInfo,
            ChildProcessEnvironmentProfile.SpellScript,
            totalOutputCapBytes: 4096,
            timeout: TimeSpan.FromSeconds(5),
            resourceLimits: null,
            resourceLimiter: null,
            CancellationToken.None);

        Assert.Equal(CappedChildProcessOutcome.FailedToStart, result.Outcome);

        Assert.IsType<InvalidOperationException>(result.FaultException);

        Assert.Equal(2048, result.PerStreamCapBytes);

    }

    [SkippableFact]
    public async Task Runner_reports_win32_start_error_and_runs_cleanup_with_unstarted_pid()
    {

        Skip.IfNot(
            OperatingSystem.IsWindows(),
            "Win32 process-start errors require Windows.");

        RecordingLimiter limiter = new();

        ProcessStartInfo startInfo = new()
        {
            FileName = Path.Combine(
                Path.GetTempPath(),
                "arcanum-missing-" + Guid.NewGuid().ToString("N") + ".exe"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            startInfo,
            ChildProcessEnvironmentProfile.SpellScript,
            totalOutputCapBytes: 4096,
            timeout: TimeSpan.FromSeconds(5),
            resourceLimits: new ResourceLimits(),
            resourceLimiter: limiter,
            CancellationToken.None);

        Assert.Equal(CappedChildProcessOutcome.FailedToStart, result.Outcome);

        Assert.IsType<Win32Exception>(result.FaultException);

        Assert.Equal([-1], limiter.CleanupPids);

        Assert.Equal(0, limiter.AssignCount);

    }

    [SkippableTheory]
    [InlineData(InjectedStreamFailure.Io)]
    [InlineData(InjectedStreamFailure.AccessDenied)]
    [InlineData(InjectedStreamFailure.Canceled)]
    public async Task Runner_maps_stream_setup_failures_and_cleans_unstarted_process(
        InjectedStreamFailure failure)
    {

        Skip.IfNot(
            OperatingSystem.IsWindows(),
            "Windows redirected-stream setup requires Windows.");

        RecordingLimiter limiter = new();

        ProcessStartInfo startInfo = CreateCmdStartInfo("exit /b 0");

        startInfo.StandardOutputEncoding = new ThrowingGetDecoderEncoding(
            () => CreateInjectedException(failure));

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            startInfo,
            ChildProcessEnvironmentProfile.SpellScript,
            totalOutputCapBytes: 4096,
            timeout: TimeSpan.FromSeconds(5),
            resourceLimits: new ResourceLimits(),
            resourceLimiter: limiter,
            CancellationToken.None);

        CappedChildProcessOutcome expectedOutcome = failure switch
        {
            InjectedStreamFailure.Io => CappedChildProcessOutcome.IoErrorOnStart,
            InjectedStreamFailure.AccessDenied => CappedChildProcessOutcome.AccessDeniedOnStart,
            _ => CappedChildProcessOutcome.CanceledBeforeStart,
        };

        Assert.Equal(expectedOutcome, result.Outcome);

        Assert.Equal([-1], limiter.CleanupPids);

        if (failure == InjectedStreamFailure.Canceled)
        {

            Assert.Null(result.FaultException);

        }
        else
        {

            Assert.Equal(
                CreateInjectedException(failure).GetType(),
                result.FaultException?.GetType());

        }

    }

    [SkippableTheory]
    [InlineData(InjectedStreamFailure.Io)]
    [InlineData(InjectedStreamFailure.AccessDenied)]
    [InlineData(InjectedStreamFailure.Canceled)]
    public async Task Runner_maps_stream_decode_failures_after_child_exit(
        InjectedStreamFailure failure)
    {

        Skip.IfNot(
            OperatingSystem.IsWindows(),
            "Windows redirected-stream decoding requires Windows.");

        RecordingLimiter limiter = new();

        ProcessStartInfo startInfo = CreateCmdStartInfo("echo payload");

        startInfo.StandardOutputEncoding = new ThrowingReadEncoding(
            () => CreateInjectedException(failure));

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            startInfo,
            ChildProcessEnvironmentProfile.SpellScript,
            totalOutputCapBytes: 4096,
            timeout: TimeSpan.FromSeconds(5),
            resourceLimits: new ResourceLimits(),
            resourceLimiter: limiter,
            CancellationToken.None);

        CappedChildProcessOutcome expectedOutcome = failure switch
        {
            InjectedStreamFailure.Io => CappedChildProcessOutcome.IoErrorReadingOutput,
            InjectedStreamFailure.AccessDenied => CappedChildProcessOutcome.AccessDeniedReadingOutput,
            _ => CappedChildProcessOutcome.CanceledWhileReadingOutput,
        };

        Assert.Equal(expectedOutcome, result.Outcome);

        int cleanupPid = Assert.Single(limiter.CleanupPids);

        Assert.True(cleanupPid > 0);

        if (failure == InjectedStreamFailure.Canceled)
        {

            Assert.Null(result.FaultException);

        }
        else
        {

            Assert.Equal(
                CreateInjectedException(failure).GetType(),
                result.FaultException?.GetType());

        }

    }

    [SkippableFact]
    public async Task Runner_calls_assign_and_cleanup_for_completed_process()
    {

        Skip.IfNot(
            OperatingSystem.IsWindows(),
            "cmd.exe process behavior requires Windows.");

        RecordingLimiter limiter = new() { IncludeAssignment = true };

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            CreateCmdStartInfo("exit /b 0"),
            ChildProcessEnvironmentProfile.SpellScript,
            totalOutputCapBytes: 4096,
            timeout: TimeSpan.FromSeconds(5),
            resourceLimits: new ResourceLimits(),
            resourceLimiter: limiter,
            CancellationToken.None);

        Assert.Equal(CappedChildProcessOutcome.Completed, result.Outcome);

        Assert.Equal(0, result.ExitCode);

        Assert.Equal(1, limiter.AssignCount);

        int cleanupPid = Assert.Single(limiter.CleanupPids);

        Assert.True(cleanupPid > 0);

        Assert.Equal(cleanupPid, limiter.AssignedPid);

    }

    [SkippableTheory]
    [InlineData(unchecked((int)0xC0000044), 64, 0, true)]
    [InlineData(unchecked((int)0xC0000044), 0, 64, true)]
    [InlineData(unchecked((int)0xC0000044), 0, 0, false)]
    [InlineData(7, 64, 0, false)]
    public async Task Runner_classifies_windows_quota_exit_only_for_configured_memory(
        int exitCode,
        int maxMemoryMb,
        int maxProcessMemoryMb,
        bool expectMemoryLimit)
    {

        Skip.IfNot(
            OperatingSystem.IsWindows(),
            "Windows Job Object exit codes require Windows.");

        ResourceLimits limits = new()
        {
            MaxMemoryMb = maxMemoryMb,
            MaxProcessMemoryMb = maxProcessMemoryMb,
        };

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            CreateCmdStartInfo($"exit /b {exitCode.ToString(CultureInfo.InvariantCulture)}"),
            ChildProcessEnvironmentProfile.SpellScript,
            totalOutputCapBytes: 4096,
            timeout: TimeSpan.FromSeconds(5),
            resourceLimits: limits,
            resourceLimiter: null,
            CancellationToken.None);

        Assert.Equal(exitCode, result.ExitCode);

        if (expectMemoryLimit)
        {

            Assert.Equal(CappedChildProcessOutcome.ResourceLimitExceeded, result.Outcome);

            Assert.Equal(ResourceLimitKind.Memory, result.ExceededResource);

        }
        else
        {

            Assert.Equal(CappedChildProcessOutcome.Completed, result.Outcome);

            Assert.Null(result.ExceededResource);

        }

    }

    private static ProcessStartInfo CreateCmdStartInfo(string command) =>
        new()
        {
            FileName = "cmd.exe",
            ArgumentList = { "/d", "/c", command },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

    private static Exception CreateInjectedException(InjectedStreamFailure failure) =>
        failure switch
        {
            InjectedStreamFailure.Io => new IOException("Injected stream failure."),
            InjectedStreamFailure.AccessDenied => new UnauthorizedAccessException("Injected stream failure."),
            _ => new OperationCanceledException("Injected stream failure."),
        };

    public enum InjectedStreamFailure
    {

        Io,

        AccessDenied,

        Canceled,

    }

    private sealed class ThrowingGetDecoderEncoding(Func<Exception> exceptionFactory) : UTF8Encoding
    {

        public override Decoder GetDecoder() => throw exceptionFactory();

    }

    private sealed class ThrowingReadEncoding(Func<Exception> exceptionFactory) : UTF8Encoding
    {

        public override Decoder GetDecoder() => new ThrowingDecoder(exceptionFactory);

    }

    private sealed class ThrowingDecoder(Func<Exception> exceptionFactory) : Decoder
    {

        public override int GetCharCount(byte[] bytes, int index, int count) =>
            throw exceptionFactory();

        public override int GetChars(
            byte[] bytes,
            int byteIndex,
            int byteCount,
            char[] chars,
            int charIndex) =>
            throw exceptionFactory();

        public override void Convert(
            byte[] bytes,
            int byteIndex,
            int byteCount,
            char[] chars,
            int charIndex,
            int charCount,
            bool flush,
            out int bytesUsed,
            out int charsUsed,
            out bool completed) =>
            throw exceptionFactory();

        public override void Convert(
            ReadOnlySpan<byte> bytes,
            Span<char> chars,
            bool flush,
            out int bytesUsed,
            out int charsUsed,
            out bool completed) =>
            throw exceptionFactory();

    }

    private sealed class RecordingLimiter : IProcessResourceLimiter
    {

        public ResourceLimitError? ApplyError { get; init; }

        public bool IncludeAssignment { get; init; }

        public int ApplyCount { get; private set; }

        public int AssignCount { get; private set; }

        public int AssignedPid { get; private set; } = -1;

        public List<int> CleanupPids { get; } = [];

        public ProcessResourceLimiterResult Apply(ProcessStartInfo startInfo, ResourceLimits limits)
        {

            ApplyCount++;

            if (ApplyError is not null)
            {

                return new ProcessResourceLimiterResult(ApplyError, null);

            }

            return new ProcessResourceLimiterResult(
                null,
                CleanupAsync: pid =>
                {

                    CleanupPids.Add(pid);

                    return Task.CompletedTask;

                },
                WasOomKilledAsync: null,
                AssignAfterStart: IncludeAssignment
                    ? process =>
                    {

                        AssignCount++;

                        AssignedPid = process.Id;

                        return null;

                    }
                    : null);

        }

    }

}
