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
    /// caller decides what a blank credential means.
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
            return SecretStoreReadResult.Corrupted(corruptMessage);
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

    internal static async Task WriteAsync(
        string path,
        string plainText,
        IDataProtector protector,
        CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Invalid secret store path.");

        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(directory);

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
