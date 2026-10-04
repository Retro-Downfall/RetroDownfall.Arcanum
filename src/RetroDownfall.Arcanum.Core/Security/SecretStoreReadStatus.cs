namespace RetroDownfall.Arcanum.Core.Security;

public enum SecretStoreReadStatus
{
    Ok,

    Missing,

    /// <summary>
    /// The stored credential is present but its content is unusable: it does not decrypt, or it is
    /// empty. Never a reason to mint a replacement while encrypted data exists.
    /// </summary>
    Corrupted,

    /// <summary>
    /// The stored credential is present but could not be read at all — access denied, an I/O error,
    /// over the size ceiling, or not a single-link regular file. Says nothing about its content, so it
    /// fails closed exactly like <see cref="Corrupted"/> and never authorizes a replacement; the
    /// remedy is to fix the file's permissions or type and retry, not to delete anything.
    /// </summary>
    Unreadable,
}
