using System.Security.Cryptography;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Storage;

public sealed class BlobEncryptionFileProcessor(
    IBlobEncryptionMetadataStore metadataStore,
    IEncryptedBlobStore blobStore)
{
    public async Task<BlobEncryptionFileResult> MigrateAsync(
        BlobEncryptionCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(candidate.Path))
        {
            throw new FileNotFoundException("Stored blob is missing.", candidate.Path);
        }

        if (blobStore.HasEnvelope(candidate.Path))
        {
            BlobEncryptionVerificationResult existing = await InspectContentAsync(
                    candidate,
                    encrypted: true,
                    CancellationToken.None)
                .ConfigureAwait(false);
            ThrowIfInvalid(existing);
            EncryptedBlobDescriptor descriptor = existing.Descriptor
                ?? throw new InvalidDataException("Encrypted blob descriptor was unavailable.");
            await metadataStore.UpdateEncryptionMetadataAsync(
                    candidate,
                    descriptor,
                    existing.PlaintextSha256!,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return new BlobEncryptionFileResult(
                existing.PlaintextLength,
                existing.PlaintextSha256!,
                descriptor);
        }

        BlobEncryptionVerificationResult legacy = await InspectContentAsync(
                candidate,
                encrypted: false,
                CancellationToken.None)
            .ConfigureAwait(false);
        ThrowIfInvalid(legacy);

        await using (Stream plaintext = OpenLegacy(candidate.Path))
        {
            _ = await blobStore.WriteAsync(
                    candidate.Path,
                    plaintext,
                    candidate.Purpose,
                    plaintextLength: legacy.PlaintextLength,
                    cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
        }

        BlobEncryptionVerificationResult replacement = await InspectContentAsync(
                candidate,
                encrypted: true,
                CancellationToken.None)
            .ConfigureAwait(false);
        ThrowIfInvalid(replacement);
        EncryptedBlobDescriptor replacementDescriptor = replacement.Descriptor
            ?? throw new InvalidDataException("Encrypted replacement descriptor was unavailable.");
        await metadataStore.UpdateEncryptionMetadataAsync(
                candidate,
                replacementDescriptor,
                replacement.PlaintextSha256!,
                CancellationToken.None)
            .ConfigureAwait(false);
        return new BlobEncryptionFileResult(
            replacement.PlaintextLength,
            replacement.PlaintextSha256!,
            replacementDescriptor);
    }

    public async Task<BlobEncryptionFileResult> ReencryptAsync(
        BlobEncryptionCandidate candidate,
        string targetKeyId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKeyId);
        cancellationToken.ThrowIfCancellationRequested();

        BlobEncryptionFileResult migrated = await MigrateAsync(candidate, cancellationToken)
            .ConfigureAwait(false);
        if (string.Equals(migrated.Descriptor.KeyId, targetKeyId, StringComparison.Ordinal))
        {
            return migrated;
        }

        await using (Stream plaintext = await blobStore.OpenReadAsync(
                         candidate.Path,
                         candidate.Purpose,
                         CancellationToken.None)
                     .ConfigureAwait(false))
        {
            _ = await blobStore.WriteAsync(
                    candidate.Path,
                    plaintext,
                    candidate.Purpose,
                    plaintextLength: migrated.PlaintextLength,
                    cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
        }

        BlobEncryptionVerificationResult replacement = await InspectContentAsync(
                candidate,
                encrypted: true,
                CancellationToken.None)
            .ConfigureAwait(false);
        ThrowIfInvalid(replacement);
        EncryptedBlobDescriptor descriptor = replacement.Descriptor
            ?? throw new InvalidDataException("Rotated blob descriptor was unavailable.");
        if (!string.Equals(descriptor.KeyId, targetKeyId, StringComparison.Ordinal))
        {
            throw new EncryptedBlobKeyException(
                "The encrypted replacement was not written with the requested active key.");
        }

        await metadataStore.UpdateEncryptionMetadataAsync(
                candidate,
                descriptor,
                replacement.PlaintextSha256!,
                CancellationToken.None)
            .ConfigureAwait(false);
        return new BlobEncryptionFileResult(
            replacement.PlaintextLength,
            replacement.PlaintextSha256!,
            descriptor);
    }

    public async Task<BlobEncryptionVerificationResult> VerifyAsync(
        BlobEncryptionCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(candidate.Path))
        {
            return new(BlobEncryptionVerificationIssue.MissingFile);
        }

        bool encrypted = blobStore.HasEnvelope(candidate.Path);
        BlobEncryptionVerificationResult inspected = await InspectContentAsync(
                candidate,
                encrypted,
                cancellationToken)
            .ConfigureAwait(false);
        if (!inspected.IsValid)
        {
            return inspected;
        }

        if (candidate.EncryptionVersion > 0 && !encrypted)
        {
            return inspected with
            {
                Issue = BlobEncryptionVerificationIssue.MetadataEncryptedFilePlaintext,
            };
        }

        if (candidate.EncryptionVersion == 0 && encrypted)
        {
            return inspected with
            {
                Issue = BlobEncryptionVerificationIssue.MetadataPlaintextFileEncrypted,
            };
        }

        if (!encrypted)
        {
            return inspected with
            {
                Issue = BlobEncryptionVerificationIssue.LegacyPlaintext,
            };
        }

        return inspected;
    }

    private async Task<BlobEncryptionVerificationResult> InspectContentAsync(
        BlobEncryptionCandidate candidate,
        bool encrypted,
        CancellationToken cancellationToken)
    {
        try
        {
            EncryptedBlobDescriptor? descriptor = null;
            Stream plaintext;
            if (encrypted)
            {
                descriptor = await blobStore.InspectAsync(
                        candidate.Path,
                        candidate.Purpose,
                        verifyAllChunks: true,
                        cancellationToken)
                    .ConfigureAwait(false);
                plaintext = await blobStore.OpenReadAsync(
                        candidate.Path,
                        candidate.Purpose,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                plaintext = OpenLegacy(candidate.Path);
            }

            await using (plaintext.ConfigureAwait(false))
            {
                (long length, string hash) = await HashAsync(plaintext, cancellationToken)
                    .ConfigureAwait(false);
                if (length != candidate.ExpectedPlaintextLength)
                {
                    return new(
                        BlobEncryptionVerificationIssue.PlaintextLengthMismatch,
                        length,
                        hash,
                        descriptor);
                }

                if (!string.IsNullOrWhiteSpace(candidate.ExpectedPlaintextSha256)
                    && !string.Equals(
                        hash,
                        candidate.ExpectedPlaintextSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return new(
                        BlobEncryptionVerificationIssue.PlaintextHashMismatch,
                        length,
                        hash,
                        descriptor);
                }

                return new(BlobEncryptionVerificationIssue.None, length, hash, descriptor);
            }
        }
        catch (EncryptedBlobKeyException)
        {
            return new(BlobEncryptionVerificationIssue.UnknownKeyId);
        }
        catch (Exception ex) when (encrypted && ex is CryptographicException or InvalidDataException)
        {
            return new(BlobEncryptionVerificationIssue.CorruptEnvelope);
        }
    }

    private static FileStream OpenLegacy(string path) =>
        new(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });

    private static async Task<(long Length, string Sha256)> HashAsync(
        Stream plaintext,
        CancellationToken cancellationToken)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1024 * 1024];
        long length = 0;
        int read;
        while ((read = await plaintext.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            hash.AppendData(buffer, 0, read);
            length = checked(length + read);
        }

        return (length, Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static void ThrowIfInvalid(BlobEncryptionVerificationResult result)
    {
        if (result.IsValid)
        {
            return;
        }

        throw result.Issue switch
        {
            BlobEncryptionVerificationIssue.MissingFile =>
                new FileNotFoundException("Stored blob is missing."),
            BlobEncryptionVerificationIssue.UnknownKeyId =>
                new EncryptedBlobKeyException("The stored blob references an unavailable key."),
            BlobEncryptionVerificationIssue.PlaintextLengthMismatch =>
                new InvalidDataException("Stored blob plaintext length does not match metadata."),
            BlobEncryptionVerificationIssue.PlaintextHashMismatch =>
                new InvalidDataException("Stored blob plaintext hash does not match metadata."),
            _ => new InvalidDataException("Stored blob could not be verified."),
        };
    }
}
