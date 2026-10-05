using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.DataProtection;

using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// Reads and writes one Data Protection-encrypted credential mirror file. Reads go through
/// <see cref="SecureFileReader"/> as a no-follow, single-link regular file under a 64 KiB ceiling;
/// writes go through <see cref="OwnerOnlyAtomicFile"/>. Every byte buffer this type owns is zeroed.
/// </summary>
internal static class ProtectedCredentialFile
{
    internal const int MaxProtectedSecretBytes = 64 * 1024;

    /// <summary>
    /// Returns <see cref="SecretStoreReadStatus.Ok"/> with the decrypted text, which may be blank: the
    /// caller decides what a blank credential means. Only content that is present and does not
    /// decrypt (or is empty) is <see cref="SecretStoreReadStatus.Corrupted"/> and carries
    /// <paramref name="corruptMessage"/>; a file that could not be read at all is
    /// <see cref="SecretStoreReadStatus.Unreadable"/> with retry guidance, because nothing is known
    /// about its content and the corrupt guidance may tell an operator to delete it.
    /// </summary>
    internal static async Task<SecretStoreReadResult> ReadAsync(
        string path,
        IDataProtector protector,
        string corruptMessage,
        CancellationToken cancellationToken)
    {
        using SecureFileReadResult read = await SecureFileReader
            .ReadBytesAsync(path, MaxProtectedSecretBytes, cancellationToken)
            .ConfigureAwait(false);

        if (read.Status == SecureFileReadStatus.NotFound)
        {
            return SecretStoreReadResult.Missing();
        }

        if (read.Status != SecureFileReadStatus.Success)
        {
            return SecretStoreReadResult.Unreadable(UnreadableMessage(path, read.Status));
        }

        byte[] cipher = read.Bytes.ToArray();

        if (cipher.Length == 0)
        {
            CryptographicOperations.ZeroMemory(cipher);

            return SecretStoreReadResult.Corrupted(corruptMessage);
        }

        try
        {
            byte[] plain = protector.Unprotect(cipher);

            try
            {
                return SecretStoreReadResult.Ok(Encoding.UTF8.GetString(plain));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch (CryptographicException)
        {
            return SecretStoreReadResult.Corrupted(corruptMessage);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cipher);
        }
    }

    /// <summary>
    /// Fixed retry guidance naming only the file and the kind of refusal. Never names a remedy that
    /// deletes anything: the file may well hold the only copy of a live credential.
    /// </summary>
    internal static string UnreadableMessage(string path, SecureFileReadStatus status)
    {
        string reason = status switch
        {
            SecureFileReadStatus.AccessDenied => "access denied",
            SecureFileReadStatus.TooLarge => $"larger than the {MaxProtectedSecretBytes / 1024} KiB limit",
            SecureFileReadStatus.Rejected => "not a single-link regular file",
            _ => "I/O error",
        };

        return $"{Path.GetFileName(path)} exists but could not be read ({reason}). It was left unchanged "
            + "and is not treated as corrupt: make it a regular file owned by the current user with "
            + "owner-only permissions, then retry. No replacement is generated while it is unreadable.";
    }

    internal static async Task WriteAsync(
        string path,
        string plainText,
        IDataProtector protector,
        CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Invalid secret store path.");

        SecureFilePermissions.RequireOwnerOnlyDirectory(directory);

        byte[] plain = Encoding.UTF8.GetBytes(plainText);

        byte[] cipher;

        try
        {
            cipher = protector.Protect(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        try
        {
            await OwnerOnlyAtomicFile.WriteAsync(path, cipher, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cipher);
        }
    }
}
