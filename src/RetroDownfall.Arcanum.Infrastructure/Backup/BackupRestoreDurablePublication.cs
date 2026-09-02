using Microsoft.Win32.SafeHandles;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

/// <summary>
/// The atomic replace a restore's own control files are published through: rename, then force the
/// directory entry that publishes it.
/// </summary>
/// <remarks>
/// A rename whose containing directory is never flushed is durable only as far as the page cache. The
/// file's own contents are already forced before the rename — every caller here writes and flushes
/// its temporary first — so what a power loss can still lose is the directory entry, and with it the
/// phase a journal had reached or the only pointer back to a decrypted staging tree.
///
/// <para>Deliberately a whole publication rather than an exposed flush. The one native call this uses
/// is kept beside the anchor store for a stated reason — a bare fsync surface invites a caller to
/// flush a handle nobody proved — so what is shared here is the complete protocol, over a handle this
/// type opens on the directory it just renamed into, and nothing narrower.</para>
///
/// <para>Windows exposes no directory-handle flush and journals directory metadata itself, so the
/// barrier is satisfied there rather than demonstrated; the same statement the anchor store makes
/// about its own barrier applies unchanged here.</para>
/// </remarks>
internal static class BackupRestoreDurablePublication
{

    /// <summary>
    /// Replaces <paramref name="path"/> with <paramref name="temporaryPath"/> and forces the rename.
    /// </summary>
    /// <exception cref="IOException">
    /// The directory entry could not be opened or flushed. Thrown rather than swallowed: a caller that
    /// was told its control file is on disk and finds it is not has no way to notice, and both callers
    /// here already fail on <see cref="IOException"/> in a way their own callers handle.
    /// </exception>
    internal static void Publish(string temporaryPath, string path)
    {

        File.Move(temporaryPath, path, overwrite: true);

        string full = Path.GetFullPath(path);

        string? parent = Path.GetDirectoryName(full);

        if (string.IsNullOrEmpty(parent))
        {

            throw new IOException(
                "A restore control file has no containing directory to publish it from: " + full);

        }

        if (!FileHandleIdentityInterop.TryOpenDirectoryMetadata(
                parent,
                out SafeFileHandle handle,
                out _))
        {

            throw new IOException(
                "A restore control file's directory could not be opened to prove its durability: "
                + parent);

        }

        using (handle)
        {

            if (!BackupRestoreJournalNativeMethods.TryFlushDirectory(handle))
            {

                throw new IOException(
                    "A restore control file's directory entry could not be flushed: " + parent);

            }

        }

    }

}
