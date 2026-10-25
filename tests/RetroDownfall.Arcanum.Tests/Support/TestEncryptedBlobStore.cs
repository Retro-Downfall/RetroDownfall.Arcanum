using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Storage;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static class TestEncryptedBlobStore
{
    public static EncryptedBlobStore Create(int chunkSize = 64) =>
        new(
            new FixedKeyProvider(),
            new EncryptedBlobStoreOptions { ChunkSize = chunkSize });

    internal sealed class ReadFaultStore(
        IEncryptedBlobStore inner,
        Action<string, CancellationToken> beforeRead) : IEncryptedBlobStore
    {
        public Task<EncryptedBlobDescriptor> WriteAsync(string destinationPath, Stream plaintext,
            EncryptedBlobPurpose purpose, ReadOnlyMemory<byte> authenticatedMetadata = default,
            long? plaintextLength = null, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(destinationPath, plaintext, purpose, authenticatedMetadata, plaintextLength, cancellationToken);

        public Task<Stream> OpenReadAsync(string path, EncryptedBlobPurpose purpose,
            CancellationToken cancellationToken = default)
        {
            beforeRead(path, cancellationToken);

            return inner.OpenReadAsync(path, purpose, cancellationToken);
        }

        public Task<EncryptedBlobWriter> CreateWriterAsync(string destinationPath, EncryptedBlobPurpose purpose,
            ReadOnlyMemory<byte> authenticatedMetadata = default, CancellationToken cancellationToken = default) =>
            inner.CreateWriterAsync(destinationPath, purpose, authenticatedMetadata, cancellationToken);

        public Task<EncryptedBlobPlaintextInspection> InspectAuthenticatedAsync(string path,
            EncryptedBlobPurpose purpose, CancellationToken cancellationToken = default) =>
            inner.InspectAuthenticatedAsync(path, purpose, cancellationToken);

        public Task<EncryptedBlobDescriptor> InspectAsync(string path, EncryptedBlobPurpose purpose,
            bool verifyAllChunks, CancellationToken cancellationToken = default) =>
            inner.InspectAsync(path, purpose, verifyAllChunks, cancellationToken);

        public bool HasEnvelope(string path) => inner.HasEnvelope(path);
    }

    private sealed class FixedKeyProvider : IFileEncryptionKeyProvider
    {
        private readonly FileEncryptionKeyMaterial _material = FileEncryptionKeyMaterial.Create(
            Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray());

        public ValueTask<FileEncryptionKeyMaterial> GetForWriteAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_material);

        public ValueTask<FileEncryptionKeyMaterial> GetForReadAsync(
            string keyId,
            CancellationToken cancellationToken = default)
        {
            if (!string.Equals(keyId, _material.KeyId, StringComparison.Ordinal))
            {
                throw new EncryptedBlobKeyException("The test file-encryption key is unavailable.");
            }

            return ValueTask.FromResult(_material);
        }
    }
}
