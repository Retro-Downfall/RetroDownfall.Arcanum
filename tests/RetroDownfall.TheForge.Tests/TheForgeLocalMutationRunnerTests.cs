using System.Runtime.InteropServices;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Coordination;
using RetroDownfall.TheForge.Ux.Services;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

[Collection(TheForgeProcessEnvironmentCollection.Name)]
public sealed class TheForgeLocalMutationRunnerTests
{

    [Fact]
    public async Task Outside_managed_root_bypasses_the_boundary_byte_for_byte()
    {

        using TheForgeTestHomeScope home = new("forge-runner-outside");

        RecordingBoundary boundary = new(
            ArcanumClientMutationResult<bool>.Blocked(
                new Error(ErrorCodes.Data.FileLocked, "blocked")));

        TheForgeLocalMutationRunner runner = new(boundary);

        bool mutated = false;

        string outsidePath = Path.Combine(home.Root, "operator-export.json");

        await runner.RunAsync(
            outsidePath,
            _ =>
            {

                mutated = true;

                return Task.CompletedTask;

            });

        Assert.True(mutated);

        Assert.Equal(0, boundary.CallCount);

    }

    [Theory]
    [InlineData(false, ErrorCodes.Data.FileLocked)]
    [InlineData(true, ErrorCodes.Data.ControlPathUnavailable)]
    public async Task Managed_root_refusal_preserves_the_error_code_and_never_mutates(
        bool unsafeDisposition,
        string expectedCode)
    {

        using TheForgeTestHomeScope home = new("forge-runner-refusal");

        Error error = new(expectedCode, "refused for test");

        RecordingBoundary boundary = new(
            unsafeDisposition
                ? ArcanumClientMutationResult<bool>.Unsafe(error)
                : ArcanumClientMutationResult<bool>.Blocked(error));

        TheForgeLocalMutationRunner runner = new(boundary);

        bool mutated = false;

        string managedPath = Path.Combine(
            ArcanumPaths.GrimoireDirectory,
            "managed.json");

        TheForgeLocalMutationRefusedException thrown =
            await Assert.ThrowsAsync<TheForgeLocalMutationRefusedException>(
                () => runner.RunAsync(
                    managedPath,
                    _ =>
                    {

                        mutated = true;

                        return Task.CompletedTask;

                    }));

        Assert.False(mutated);

        Assert.Contains(expectedCode, thrown.Message, StringComparison.Ordinal);

        Assert.Equal(1, boundary.CallCount);

    }

    [Fact]
    public async Task Outside_directory_symlink_into_managed_root_is_refused_without_mutating_target()
    {

        using TheForgeTestHomeScope home = new("forge-runner-directory-link");

        string managedRoot = ArcanumPaths.GrimoireDirectory;

        Directory.CreateDirectory(managedRoot);

        string managedTarget = Path.Combine(managedRoot, "managed.json");

        await File.WriteAllTextAsync(managedTarget, "original");

        string aliasRoot = Path.Combine(home.Root, "outside-alias");

        Directory.CreateSymbolicLink(aliasRoot, managedRoot);

        string aliasTarget = Path.Combine(aliasRoot, "managed.json");

        RecordingBoundary boundary = new(
            ArcanumClientMutationResult<bool>.Completed(true));

        TheForgeLocalMutationRunner runner = new(boundary);

        TheForgeLocalMutationRefusedException thrown =
            await Assert.ThrowsAsync<TheForgeLocalMutationRefusedException>(
                () => runner.RunAsync(
                    aliasTarget,
                    cancellationToken => File.WriteAllTextAsync(
                        aliasTarget,
                        "replacement",
                        cancellationToken)));

        Assert.Equal(ErrorCodes.Data.ControlPathUnavailable, thrown.Code);

        Assert.Equal("original", await File.ReadAllTextAsync(managedTarget));

        Assert.Equal(0, boundary.CallCount);

    }

    [Fact]
    public async Task Outside_hard_link_to_managed_file_is_refused_without_mutating_target()
    {

        using TheForgeTestHomeScope home = new("forge-runner-hard-link");

        string managedRoot = ArcanumPaths.GrimoireDirectory;

        Directory.CreateDirectory(managedRoot);

        string managedTarget = Path.Combine(managedRoot, "managed.json");

        await File.WriteAllTextAsync(managedTarget, "original");

        string aliasTarget = Path.Combine(home.Root, "outside-alias.json");

        Assert.True(TryCreateHardLink(aliasTarget, managedTarget));

        RecordingBoundary boundary = new(
            ArcanumClientMutationResult<bool>.Completed(true));

        TheForgeLocalMutationRunner runner = new(boundary);

        TheForgeLocalMutationRefusedException thrown =
            await Assert.ThrowsAsync<TheForgeLocalMutationRefusedException>(
                () => runner.RunAsync(
                    aliasTarget,
                    cancellationToken => File.WriteAllTextAsync(
                        aliasTarget,
                        "replacement",
                        cancellationToken)));

        Assert.Equal(ErrorCodes.Data.ControlPathUnavailable, thrown.Code);

        Assert.Equal("original", await File.ReadAllTextAsync(managedTarget));

        Assert.Equal(0, boundary.CallCount);

    }

    private static bool TryCreateHardLink(string linkPath, string existingPath)
    {

        if (OperatingSystem.IsWindows())
        {

            return CreateHardLink(linkPath, existingPath, IntPtr.Zero);

        }

        return (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            && link(existingPath, linkPath) == 0;

    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateHardLinkW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string lpFileName,
        string lpExistingFileName,
        IntPtr lpSecurityAttributes);

    [DllImport("libc", SetLastError = true)]
    private static extern int link(string existingPath, string newPath);

    private sealed class RecordingBoundary(
        ArcanumClientMutationResult<bool> result) : IArcanumClientMutationBoundary
    {

        public int CallCount { get; private set; }

        public Task<ArcanumClientMutationResult<T>> RunAsync<T>(
            Func<T> mutation,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<ArcanumClientMutationResult<T>> RunAsync<T>(
            Func<CancellationToken, Task<T>> mutation,
            CancellationToken cancellationToken = default)
        {

            CallCount++;

            if (!result.IsCompleted)
            {

                return result.Disposition is ArcanumClientMutationDisposition.Blocked
                    ? ArcanumClientMutationResult<T>.Blocked(result.Error)
                    : ArcanumClientMutationResult<T>.Unsafe(result.Error);

            }

            return ArcanumClientMutationResult<T>.Completed(
                await mutation(cancellationToken));

        }

    }

}
