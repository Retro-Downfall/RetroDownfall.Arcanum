using System.Buffers.Binary;

using System.Diagnostics.CodeAnalysis;

using System.Security.Cryptography;

using System.Text;

using System.Text.Json;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

/// <summary>Bounded readers for the two pieces of archive metadata a restore needs early.</summary>
internal static class BackupPortableRecoveryReader
{

    private const long MaximumRecoveryBytes = 8L * 1024 * 1024;

    /// <summary>
    /// Extracts just the Grimoire encryption secret so the staged snapshot can be opened. The file
    /// keys stay untouched; they are handled by re-wrapping, which owns their lifetime.
    /// </summary>
    public static bool TryReadGrimoireSecret(
        string portableRecoveryPath,
        [NotNullWhen(true)] out string? grimoireSecret)
    {

        grimoireSecret = null;

        try
        {

            if (!File.Exists(portableRecoveryPath)
                || new FileInfo(portableRecoveryPath).Length > MaximumRecoveryBytes)
            {

                return false;

            }

            byte[] bytes = File.ReadAllBytes(portableRecoveryPath);

            PortableBackupRecoveryMaterial? recovery = null;

            try
            {

                recovery = JsonSerializer.Deserialize(
                    bytes,
                    BackupJsonContext.Default.PortableBackupRecoveryMaterial);

                if (recovery is null
                    || recovery.Version != 1
                    || recovery.GrimoireEncryptionSecretUtf8.Length == 0)
                {

                    return false;

                }

                grimoireSecret = Encoding.UTF8.GetString(recovery.GrimoireEncryptionSecretUtf8);

                return true;

            }
            finally
            {

                recovery?.Dispose();

                CryptographicOperations.ZeroMemory(bytes);

            }

        }
        catch (Exception exception) when (
            exception is JsonException
                or NotSupportedException
                or IOException
                or UnauthorizedAccessException)
        {

            return false;

        }

    }

}

/// <summary>
/// Reads only the archive's declared format version. Used when the full header parser has already
/// refused, so the operator can be told "upgrade Arcanum" instead of "the file is corrupt".
/// </summary>
internal static class BackupArchiveHeaderPeek
{

    private static ReadOnlySpan<byte> Magic => "ARCABACK"u8;

    public static async Task<int> TryReadFormatVersionAsync(
        string archivePath,
        CancellationToken cancellationToken)
    {

        try
        {

            await using FileStream input = new(
                archivePath,
                new FileStreamOptions
                {

                    Mode = FileMode.Open,

                    Access = FileAccess.Read,

                    Share = FileShare.Read,

                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,

                });

            byte[] prefix = new byte[Magic.Length + sizeof(int)];

            await input.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);

            return CryptographicOperations.FixedTimeEquals(prefix.AsSpan(0, Magic.Length), Magic)
                ? BinaryPrimitives.ReadInt32BigEndian(prefix.AsSpan(Magic.Length))
                : 0;

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or EndOfStreamException)
        {

            return 0;

        }

    }

}
