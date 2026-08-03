using System.Security.Cryptography;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Storage;

public sealed class EncryptedBlobDiagnostics(
    ISecretStore secretStore,
    IEncryptedBlobStore blobStore) : IEncryptedBlobDiagnostics
{
    public async Task<FileEncryptionDiagnostics> InspectAsync(
        CancellationToken cancellationToken = default)
    {
        SecretStoreReadResult secret = await secretStore
            .GetFileEncryptionSecretReadResultAsync()
            .ConfigureAwait(false);
        FileEncryptionSecretStatus secretStatus = secret.Status switch
        {
            SecretStoreReadStatus.Ok => FileEncryptionSecretStatus.Available,
            SecretStoreReadStatus.Corrupted => FileEncryptionSecretStatus.Corrupted,
            _ => FileEncryptionSecretStatus.Missing,
        };

        int encrypted = 0;
        int legacy = 0;
        int corrupt = 0;
        foreach ((string Path, EncryptedBlobPurpose[] Purposes) candidate in EnumerateCandidates())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!blobStore.HasEnvelope(candidate.Path))
            {
                legacy++;
                continue;
            }

            bool valid = false;
            foreach (EncryptedBlobPurpose purpose in candidate.Purposes)
            {
                try
                {
                    _ = await blobStore.InspectAsync(
                            candidate.Path,
                            purpose,
                            verifyAllChunks: true,
                            cancellationToken)
                        .ConfigureAwait(false);
                    valid = true;
                    break;
                }
                catch (CryptographicException)
                {
                }
                catch (InvalidDataException)
                {
                }
            }

            if (valid)
            {
                encrypted++;
            }
            else
            {
                corrupt++;
            }
        }

        string secretDetail = secretStatus switch
        {
            FileEncryptionSecretStatus.Available => "OS file-encryption key available",
            FileEncryptionSecretStatus.Missing =>
                "OS file-encryption key missing; new writes cannot initialize until OS key storage is available",
            _ => secret.Message
                 ?? "OS file-encryption key or its recovery mirror is undecryptable; restore backup",
        };
        string detail =
            $"{secretDetail}; encrypted={encrypted}; legacyPlaintext={legacy}; corrupt={corrupt}"
            + (legacy > 0
                ? "; run 'arcanum data encryption migrate' followed by 'verify'"
                : string.Empty)
            + ".";
        return new FileEncryptionDiagnostics(
            secretStatus,
            encrypted,
            legacy,
            corrupt,
            false,
            detail);
    }

    private static IEnumerable<(string Path, EncryptedBlobPurpose[] Purposes)>
        EnumerateCandidates()
    {
        if (Directory.Exists(ArcanumPaths.AttachmentsDirectory))
        {
            foreach (string path in Directory.EnumerateFiles(
                         ArcanumPaths.AttachmentsDirectory,
                         "*",
                         SearchOption.AllDirectories))
            {
                if (!Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal))
                {
                    yield return (path, [EncryptedBlobPurpose.SessionAttachment]);
                }
            }
        }

        if (Directory.Exists(ArcanumPaths.FilesDirectory))
        {
            foreach (string path in Directory.EnumerateFiles(
                         ArcanumPaths.FilesDirectory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                if (!Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal))
                {
                    yield return (
                        path,
                        [
                            EncryptedBlobPurpose.UploadedFile,
                            EncryptedBlobPurpose.BatchArtifact,
                        ]);
                }
            }
        }
    }
}
