using System.Buffers.Binary;

using System.Security.Cryptography;

using System.Text;

using System.Text.Json;

using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Infrastructure.Backup;

namespace RetroDownfall.Arcanum.Tests.Backup;

/// <summary>Test-only authenticated payload editing, below the production writer's validation.</summary>
internal static class BackupAuthenticatedPayloadFixture
{
    internal static async Task RenameEntryAsync(
        string archivePath,
        string recoveryPassphrase,
        string originalPath,
        string replacementPath)
    {
        byte[] archive = await File.ReadAllBytesAsync(archivePath);

        byte[] header = archive.AsSpan(0, 68).ToArray();

        byte[] plaintext = DecryptPayload(archive, header, recoveryPassphrase);

        using MemoryStream rewritten = new();

        int renamedRecords = 0;

        int renamedManifestEntries = 0;

        try
        {
            int offset = 0;

            while (offset < plaintext.Length)
            {
                int pathLength = BinaryPrimitives.ReadInt32BigEndian(plaintext.AsSpan(offset));

                offset += sizeof(int);

                string path = Encoding.UTF8.GetString(plaintext, offset, pathLength);

                offset += pathLength;

                int contentLength = checked((int)BinaryPrimitives.ReadInt64BigEndian(plaintext.AsSpan(offset)));

                offset += sizeof(long);

                byte[] content = plaintext.AsSpan(offset, contentLength).ToArray();

                offset += contentLength;

                try
                {
                    if (path == originalPath)
                    {
                        path = replacementPath;

                        renamedRecords++;
                    }
                    else if (path == BackupArchivePaths.Manifest)
                    {
                        BackupManifest manifest = JsonSerializer.Deserialize(content, BackupJsonContext.Default.BackupManifest)!;

                        renamedManifestEntries = manifest.Entries.Count(entry => entry.Path == originalPath);

                        manifest = manifest with
                        {
                            Entries = [.. manifest.Entries.Select(entry => entry.Path == originalPath
                                ? entry with { Path = replacementPath }
                                : entry).OrderBy(static entry => entry.Path, StringComparer.Ordinal)],
                        };

                        CryptographicOperations.ZeroMemory(content);

                        content = JsonSerializer.SerializeToUtf8Bytes(manifest, BackupJsonContext.Default.BackupManifest);
                    }

                    byte[] pathBytes = Encoding.UTF8.GetBytes(path);

                    byte[] recordHeader = new byte[sizeof(int) + pathBytes.Length + sizeof(long)];

                    BinaryPrimitives.WriteInt32BigEndian(recordHeader, pathBytes.Length);

                    pathBytes.CopyTo(recordHeader, sizeof(int));

                    BinaryPrimitives.WriteInt64BigEndian(recordHeader.AsSpan(sizeof(int) + pathBytes.Length), content.LongLength);

                    rewritten.Write(recordHeader);

                    rewritten.Write(content);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(content);
                }
            }

            Assert.Equal(1, renamedRecords);

            Assert.Equal(1, renamedManifestEntries);

            byte[] payload = rewritten.ToArray();

            try
            {
                BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(28), payload.LongLength);

                await File.WriteAllBytesAsync(archivePath, EncryptPayload(header, payload, recoveryPassphrase));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);

            CryptographicOperations.ZeroMemory(rewritten.GetBuffer());
        }
    }

    internal static byte[] DecryptPayload(
        byte[] archive,
        byte[] header,
        string recoveryPassphrase)
    {
        int iterations = BinaryPrimitives.ReadInt32BigEndian(
            header.AsSpan(20));

        int chunkSize = BinaryPrimitives.ReadInt32BigEndian(
            header.AsSpan(24));

        long plaintextLength = BinaryPrimitives.ReadInt64BigEndian(
            header.AsSpan(28));

        byte[] key = DeriveTestKey(header, iterations, recoveryPassphrase);

        byte[] plaintext = new byte[checked((int)plaintextLength)];

        int archiveOffset = header.Length;

        int plaintextOffset = 0;

        uint chunkIndex = 0;

        using AesGcm aes = new(key, 16);

        try
        {
            while (plaintextOffset < plaintext.Length)
            {
                int length = BinaryPrimitives.ReadInt32BigEndian(
                    archive.AsSpan(archiveOffset, sizeof(int)));

                archiveOffset += sizeof(int);

                Assert.Equal(
                    Math.Min(chunkSize, plaintext.Length - plaintextOffset),
                    length);

                byte[] nonce = Nonce(header, chunkIndex);

                byte[] associatedData = AssociatedData(header, chunkIndex);

                try
                {
                    aes.Decrypt(
                        nonce,
                        archive.AsSpan(archiveOffset, length),
                        archive.AsSpan(archiveOffset + length, 16),
                        plaintext.AsSpan(plaintextOffset, length),
                        associatedData);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(nonce);

                    CryptographicOperations.ZeroMemory(associatedData);
                }

                archiveOffset += length + 16;

                plaintextOffset += length;

                chunkIndex++;
            }

            Assert.Equal(archive.Length, archiveOffset);

            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    internal static byte[] EncryptPayload(
        byte[] header,
        byte[] plaintext,
        string recoveryPassphrase)
    {
        int iterations = BinaryPrimitives.ReadInt32BigEndian(
            header.AsSpan(20));

        int chunkSize = BinaryPrimitives.ReadInt32BigEndian(
            header.AsSpan(24));

        int chunkCount = plaintext.Length == 0
            ? 0
            : ((plaintext.Length - 1) / chunkSize) + 1;

        byte[] archive = new byte[checked(
            header.Length
            + plaintext.Length
            + (chunkCount * (sizeof(int) + 16)))];

        header.CopyTo(archive, 0);

        byte[] key = DeriveTestKey(header, iterations, recoveryPassphrase);

        int archiveOffset = header.Length;

        int plaintextOffset = 0;

        uint chunkIndex = 0;

        using AesGcm aes = new(key, 16);

        try
        {
            while (plaintextOffset < plaintext.Length)
            {
                int length = Math.Min(
                    chunkSize,
                    plaintext.Length - plaintextOffset);

                BinaryPrimitives.WriteInt32BigEndian(
                    archive.AsSpan(archiveOffset, sizeof(int)),
                    length);

                archiveOffset += sizeof(int);

                byte[] nonce = Nonce(header, chunkIndex);

                byte[] associatedData = AssociatedData(header, chunkIndex);

                try
                {
                    aes.Encrypt(
                        nonce,
                        plaintext.AsSpan(plaintextOffset, length),
                        archive.AsSpan(archiveOffset, length),
                        archive.AsSpan(archiveOffset + length, 16),
                        associatedData);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(nonce);

                    CryptographicOperations.ZeroMemory(associatedData);
                }

                archiveOffset += length + 16;

                plaintextOffset += length;

                chunkIndex++;
            }

            Assert.Equal(archive.Length, archiveOffset);

            return archive;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] DeriveTestKey(
        byte[] header,
        int iterations,
        string recoveryPassphrase)
    {
        byte[] passphrase = Encoding.UTF8.GetBytes(recoveryPassphrase);

        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(
                passphrase,
                header.AsSpan(44, 16),
                iterations,
                HashAlgorithmName.SHA256,
                32);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passphrase);
        }
    }

    private static byte[] Nonce(
        byte[] header,
        uint chunkIndex)
    {
        byte[] nonce = new byte[12];

        header.AsSpan(60, 8).CopyTo(nonce);

        BinaryPrimitives.WriteUInt32BigEndian(
            nonce.AsSpan(8),
            chunkIndex);

        return nonce;
    }

    private static byte[] AssociatedData(
        byte[] header,
        uint chunkIndex)
    {
        byte[] associatedData = new byte[header.Length + sizeof(uint)];

        header.CopyTo(associatedData, 0);

        BinaryPrimitives.WriteUInt32BigEndian(
            associatedData.AsSpan(header.Length),
            chunkIndex);

        return associatedData;
    }
}
