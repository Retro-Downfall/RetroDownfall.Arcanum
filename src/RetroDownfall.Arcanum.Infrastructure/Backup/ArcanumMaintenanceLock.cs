using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Infrastructure.Coordination;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

internal enum ArcanumMaintenanceLockAcquisitionDisposition : byte
{

    Unsafe,

    Contended,

    Acquired,

}

internal readonly record struct ArcanumMaintenanceLockAcquisitionResult
{

    private ArcanumMaintenanceLockAcquisitionResult(
        ArcanumMaintenanceLockAcquisitionDisposition disposition,
        ArcanumMaintenanceLock? maintenanceLock)
    {

        Disposition = disposition;

        Lock = maintenanceLock;

    }

    internal ArcanumMaintenanceLockAcquisitionDisposition Disposition { get; }

    internal ArcanumMaintenanceLock? Lock { get; }

    internal static ArcanumMaintenanceLockAcquisitionResult Acquired(
        ArcanumMaintenanceLock maintenanceLock) =>
        new(
            ArcanumMaintenanceLockAcquisitionDisposition.Acquired,
            maintenanceLock);

    internal static ArcanumMaintenanceLockAcquisitionResult Contended() =>
        new(
            ArcanumMaintenanceLockAcquisitionDisposition.Contended,
            maintenanceLock: null);

    internal static ArcanumMaintenanceLockAcquisitionResult Unsafe() =>
        new(
            ArcanumMaintenanceLockAcquisitionDisposition.Unsafe,
            maintenanceLock: null);

    internal ArcanumMaintenanceLock BorrowAcquiredLock() =>
        Disposition is ArcanumMaintenanceLockAcquisitionDisposition.Acquired
        && Lock is { } acquired
            ? acquired
            : throw new InvalidOperationException(
                "This maintenance-lock outcome does not carry an acquired handle.");

}

/// <summary>
/// The dedicated exclusive maintenance mode a restore must hold before it may touch installation
/// state. One owner-only lock file per Grimoire root, opened without sharing, so a running host and
/// a restore — or two concurrent restores — can never operate on the same tree.
/// </summary>
/// <remarks>
/// The lock is advisory only in the sense that it must be *taken*: a process that never asks is
/// never blocked. The host requires it at startup and holds it for its lifetime, which is
/// what lets a restore detect "the host is running" without a heartbeat or a pid registry. A lock
/// file left behind by a killed process is not a lock — nothing holds the handle, so the next
/// acquirer takes it. That is deliberate: a stale file must never wedge recovery.
/// </remarks>
internal sealed class ArcanumMaintenanceLock : IDisposable
{

    private RetainedExclusiveFileLock? _lock;

    private ArcanumMaintenanceLock(
        string path,
        RetainedExclusiveFileLock maintenanceLock)
    {

        Path = path;

        _lock = maintenanceLock;

    }

    public string Path { get; }

    /// <summary>
    /// The lock guarding <paramref name="guardedDirectory"/>. It deliberately lives in the parent
    /// directory: a restore renames the guarded directory wholesale, and Windows refuses to rename a
    /// directory that still contains an open handle.
    /// </summary>
    public static string LockPathFor(string guardedDirectory)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(guardedDirectory);

        string full = System.IO.Path.TrimEndingDirectorySeparator(
            System.IO.Path.GetFullPath(guardedDirectory));

        string? parent = System.IO.Path.GetDirectoryName(full);

        string name = System.IO.Path.GetFileName(full);

        return string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name)
            ? System.IO.Path.Combine(full, ".arcanum-maintenance.lock")
            : System.IO.Path.Combine(parent, $".arcanum-maintenance-{name}.lock");

    }

    /// <summary>
    /// Compatibility wrapper that takes the lock, or returns <see langword="null"/> when acquisition
    /// is either genuinely contended or cannot be attempted safely. Callers that need to distinguish
    /// those cases must use <see cref="AcquireDetailed(string)"/>.
    /// </summary>
    public static ArcanumMaintenanceLock? TryAcquire(string guardedDirectory)
        => AcquireDetailed(guardedDirectory).Lock;

    /// <summary>
    /// Attempts one exclusive acquisition and preserves whether a verified sharing violation caused
    /// contention or whether topology, identity, permission, or other I/O evidence was unsafe.
    /// </summary>
    internal static ArcanumMaintenanceLockAcquisitionResult AcquireDetailed(
        string guardedDirectory)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(guardedDirectory);

        string path = LockPathFor(guardedDirectory);

        RetainedExclusiveFileLockAcquisitionResult acquired =
            RetainedExclusiveFileLock.Acquire(path);

        return acquired.Disposition switch
        {
            RetainedExclusiveFileLockAcquisitionDisposition.Acquired
                when acquired.Lock is { } held =>
                ArcanumMaintenanceLockAcquisitionResult.Acquired(
                    new ArcanumMaintenanceLock(path, held)),
            RetainedExclusiveFileLockAcquisitionDisposition.Contended =>
                ArcanumMaintenanceLockAcquisitionResult.Contended(),
            _ => ArcanumMaintenanceLockAcquisitionResult.Unsafe(),
        };

    }

    internal static bool IsVerifiedSharingViolation(IOException exception)
        => RetainedExclusiveFileLock.IsVerifiedSharingViolation(exception);

    /// <summary>
    /// Verifies that this live, undisposed instance is the lock guarding
    /// <paramref name="guardedDirectory"/>, and throws otherwise.
    /// </summary>
    /// <remarks>
    /// Answers the question from evidence this object already carries: the canonical lock path for
    /// the directory the caller names, compared against the path this handle was opened on, plus
    /// whether that handle is still open. It must never probe.
    /// <see cref="CannotAcquireSafely(string)"/> and <see cref="TryAcquire(string)"/> answer their own questions
    /// by opening the lock file, which creates it, truncates it, and rewrites its owner stamp as a
    /// side effect. An assertion that mutates the filesystem is not an assertion, and probing here
    /// would additionally be self-defeating within this process: the probe would contend with the
    /// very handle being asserted and report the caller's own lock as somebody else's.
    ///
    /// <para>This is why the check is identity rather than liveness. A caller passing a lock it
    /// still holds is the only case that can reach here truthfully, because the handle cannot be
    /// open unless acquisition succeeded and cannot survive <see cref="Dispose"/>.</para>
    /// </remarks>
    public void AssertHeldFor(string guardedDirectory)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(guardedDirectory);

        RetainedExclusiveFileLock? held = _lock;

        ObjectDisposedException.ThrowIf(held is null, this);

        held.AssertHeldAt(LockPathFor(guardedDirectory), this);

    }

    /// <summary>
    /// Reports whether this process cannot safely acquire the lock, whether because another owner
    /// holds it or because the lock topology or permissions cannot be admitted.
    /// </summary>
    public static bool CannotAcquireSafely(string guardedDirectory)
    {

        ArcanumMaintenanceLockAcquisitionResult acquired =
            AcquireDetailed(guardedDirectory);

        using ArcanumMaintenanceLock? probe = acquired.Lock;

        return acquired.Disposition
            is not ArcanumMaintenanceLockAcquisitionDisposition.Acquired;

    }

    public void Dispose()
    {

        RetainedExclusiveFileLock? held = _lock;

        _lock = null;

        if (held is null)
        {

            return;

        }

        // Closing the handle is the whole of the release, and the lock file is deliberately left
        // behind. Release cannot be two steps: the share mode is an advisory lock on the open file
        // description, so between the close and an unlink another process can open and lock that
        // same inode — and unlinking it then leaves that process holding a nameless file while the
        // next acquirer creates a fresh inode at the same path and takes what both believe is the
        // installation's only maintenance lock. A file nothing holds is already not a lock, which is
        // why leaving it costs nothing and why no unlink can be made safe (there is no portable
        // compare-inode-and-unlink to close the window with).
        held.Dispose();

    }

}
