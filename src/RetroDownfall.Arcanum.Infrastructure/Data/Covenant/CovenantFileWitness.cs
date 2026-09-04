using System.Security.Cryptography;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// The two things a replacement may be asked to remember about a file: which file it is, and what
/// was in it.
/// </summary>
/// <remarks>
/// Both are digests rather than paths or sizes, because both go into an authenticated journal that an
/// operator may read and that the Covenant may not let disclose where an installation keeps its
/// storage (§10.20.6). A digest answers "is this the same one" without answering "where is it".
///
/// <para>They answer different questions and a replacement needs both. The identity survives a
/// rewrite of the contents and changes on a rename-over, so it is what says whether an install has
/// already happened; the content digest survives a rename and changes on a rewrite, so it is what
/// says whether what landed is what was proven. Either alone leaves a resumed run with an ambiguity
/// it would have to resolve by guessing.</para>
/// </remarks>
internal static class CovenantFileWitness
{

    /// <summary>
    /// The digest of a file's physical identity, or a refusal naming the class of failure only.
    /// </summary>
    /// <remarks>
    /// The lookup does not follow links. A replacement that resolved a symlink would record the
    /// identity of whatever the link pointed at when it looked, which is the one thing an attacker who
    /// can write in the directory gets to change between the look and the install.
    /// </remarks>
    internal static Result<CovenantDigest> IdentityOf(string path)
    {

        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!FileHandleIdentityInterop.TryGetPathMetadataNoFollow(path, out FileHandleMetadata metadata))
        {

            return new Error(
                ErrorCodes.Covenant.ErasureIncomplete,
                "A Covenant replacement could not establish the identity of a file it is about to act "
                + "on, so it cannot say afterwards whether it acted on the same one.");

        }

        return metadata.Kind is FileSystemObjectKind.RegularFile
            ? Result<CovenantDigest>.Success(
                BackupRestoreJournalAuthenticator.PhysicalIdentity(
                    metadata.Identity.VolumeId,
                    metadata.Identity.FileId))
            : new Error(
                ErrorCodes.Covenant.ErasureIncomplete,
                "A Covenant replacement found something other than a regular file where it expected "
                + "one, so it will not act on it.");

    }

    /// <summary>Whether the file at <paramref name="path"/> still has the recorded identity.</summary>
    /// <remarks>
    /// A missing file is a false rather than a failure. "It is not there" is one of the answers a
    /// resumed run is asking for, and turning it into an error would make the caller unable to
    /// distinguish it from being unable to look.
    /// </remarks>
    internal static bool HasIdentity(string path, CovenantDigest expected) =>
        File.Exists(path)
        && IdentityOf(path) is { IsSuccess: true, Value: { } actual }
        && actual == expected;

    /// <summary>The digest of a file's whole contents, read once through a sequential scan.</summary>
    internal static async Task<Result<CovenantDigest>> ContentAsync(
        string path,
        CancellationToken cancellationToken)
    {

        ArgumentException.ThrowIfNullOrEmpty(path);

        try
        {

            await using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);

            return Result<CovenantDigest>.Success(new CovenantDigest(hash));

        }
        catch (Exception failed) when (failed is IOException or UnauthorizedAccessException)
        {

            return new Error(
                ErrorCodes.Covenant.ErasureIncomplete,
                "A Covenant replacement could not read a file it has to prove the contents of, so it "
                + "cannot say whether the database in place is the one it verified.");

        }

    }

}
