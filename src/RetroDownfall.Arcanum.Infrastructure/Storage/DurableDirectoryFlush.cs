using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Storage;

/// <summary>
/// The directory durability barrier every retained-directory flush issues.
/// </summary>
/// <remarks>
/// On macOS a plain <c>fsync</c> hands the directory entry to the drive without asking the drive to
/// empty its own write cache, so a rename it "flushed" can still be lost to a power cut;
/// <c>fcntl(F_FULLFSYNC)</c> is the call that asks. Some filesystems (network and FUSE mounts) refuse
/// <c>F_FULLFSYNC</c>, and falling back to <c>fsync</c> there keeps the barrier no weaker than it was.
/// Linux <c>fsync</c> on a directory already reaches stable storage. Windows exposes no directory-handle
/// flush and journals directory metadata itself, so the barrier is satisfied there rather than
/// demonstrated (§10.17).
///
/// <para>Callers keep their own handle proof: this takes only a handle or descriptor its caller already
/// opened and verified, so it adds no way to flush a directory nobody proved.</para>
/// </remarks>
internal static partial class DurableDirectoryFlush
{
    /// <summary><c>F_FULLFSYNC</c> from macOS <c>fcntl.h</c>.</summary>
    internal const int MacFullFsyncCommand = 51;

    /// <summary>Flushes one retained directory handle.</summary>
    internal static bool TryFlush(SafeFileHandle directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        if (directory is null || directory.IsInvalid || directory.IsClosed)
        {
            return false;
        }

        bool referenced = false;

        try
        {
            directory.DangerousAddRef(ref referenced);

            return TryFlushDescriptor(directory.DangerousGetHandle().ToInt32());
        }
        catch (Exception exception) when (
            exception is ObjectDisposedException
                or EntryPointNotFoundException
                or DllNotFoundException)
        {
            return false;
        }
        finally
        {
            if (referenced)
            {
                directory.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Opens the directory that holds <paramref name="path"/> without following a final link and
    /// flushes it, so a rename into that directory survives a power loss.
    /// </summary>
    internal static bool TryFlushParentOf(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        string? parent = Path.GetDirectoryName(Path.GetFullPath(path));

        if (string.IsNullOrEmpty(parent)
            || !FileHandleIdentityInterop.TryOpenDirectoryMetadata(
                parent,
                out SafeFileHandle handle,
                out _))
        {
            return false;
        }

        using (handle)
        {
            return TryFlush(handle);
        }
    }

    /// <summary>
    /// The native calls the production barrier issues on this host, named so a test can pin the
    /// binding itself and not only the decision below.
    /// </summary>
    internal static DirectoryBarrierCalls ProductionCalls { get; } = new(
        OperatingSystem.IsMacOS(),
        static target => Fcntl(target, MacFullFsyncCommand),
        static target => Fsync(target));

    /// <summary>The production barrier for one descriptor on this host.</summary>
    internal static bool TryFlushDescriptor(int descriptor) =>
        TryFlushDescriptor(
            descriptor,
            ProductionCalls.FullFsyncHost,
            ProductionCalls.FullFsync,
            ProductionCalls.Fsync);

    /// <summary>
    /// The barrier's decision with both native calls supplied, so a test on any host can pin what the
    /// macOS arm issues.
    /// </summary>
    internal static bool TryFlushDescriptor(
        int descriptor,
        bool macOS,
        Func<int, int> fullFsync,
        Func<int, int> fsync)
    {
        ArgumentNullException.ThrowIfNull(fullFsync);

        ArgumentNullException.ThrowIfNull(fsync);

        if (descriptor < 0)
        {
            return false;
        }

        if (macOS && fullFsync(descriptor) == 0)
        {
            return true;
        }

        return fsync(descriptor) == 0;
    }

    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static partial int Fcntl(int descriptor, int command);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(int descriptor);
}

/// <summary>Whether this host issues <c>F_FULLFSYNC</c>, and the two native calls the barrier makes.</summary>
internal sealed record DirectoryBarrierCalls(
    bool FullFsyncHost,
    Func<int, int> FullFsync,
    Func<int, int> Fsync);
