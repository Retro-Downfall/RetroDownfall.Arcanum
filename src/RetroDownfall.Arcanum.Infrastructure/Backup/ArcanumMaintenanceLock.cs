using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

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

    private FileStream? _stream;

    private ArcanumMaintenanceLock(string path, FileStream stream)
    {

        Path = path;

        _stream = stream;

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
    /// Takes the lock, or returns <see langword="null"/> when another live process holds it.
    /// </summary>
    public static ArcanumMaintenanceLock? TryAcquire(string guardedDirectory)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(guardedDirectory);

        string path = LockPathFor(guardedDirectory);

        string root = System.IO.Path.GetDirectoryName(path)!;

        try
        {

            SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(root);

            FileStream stream = new(
                path,
                new FileStreamOptions
                {

                    Mode = FileMode.OpenOrCreate,

                    Access = FileAccess.ReadWrite,

                    Share = FileShare.None,

                    Options = FileOptions.WriteThrough,

                });

            try
            {

                stream.SetLength(0);

                using StreamWriter writer = new(stream, leaveOpen: true);

                writer.Write(
                    $"{System.Environment.MachineName}:{System.Environment.ProcessId}");

                writer.Flush();

                stream.Flush(flushToDisk: true);

            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {

                // A readable-but-unwritable lock file is still an exclusive claim: keep the handle.

            }

            SecureFilePermissions.ApplyOwnerOnlyFile(path);

            return new ArcanumMaintenanceLock(path, stream);

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException)
        {

            return null;

        }

    }

    /// <summary>
    /// Verifies that this live, undisposed instance is the lock guarding
    /// <paramref name="guardedDirectory"/>, and throws otherwise.
    /// </summary>
    /// <remarks>
    /// Answers the question from evidence this object already carries: the canonical lock path for
    /// the directory the caller names, compared against the path this handle was opened on, plus
    /// whether that handle is still open. It must never probe.
    /// <see cref="IsHeld(string)"/> and <see cref="TryAcquire(string)"/> answer their own questions
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

        ObjectDisposedException.ThrowIf(_stream is null, this);

        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!string.Equals(LockPathFor(guardedDirectory), Path, comparison))
        {

            throw new InvalidOperationException(
                "The maintenance lock supplied for this operation guards a different installation "
                + "directory, so the caller does not hold the lock it claims to hold.");

        }

    }

    /// <summary>Reports whether some other process currently holds the lock.</summary>
    public static bool IsHeld(string guardedDirectory)
    {

        using ArcanumMaintenanceLock? probe = TryAcquire(guardedDirectory);

        return probe is null;

    }

    public void Dispose()
    {

        FileStream? stream = _stream;

        _stream = null;

        if (stream is null)
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
        try
        {

            stream.Dispose();

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException)
        {

        }

    }

}
