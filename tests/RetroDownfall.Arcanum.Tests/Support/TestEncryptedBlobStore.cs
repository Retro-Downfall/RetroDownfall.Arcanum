using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Storage;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static class TestEncryptedBlobStore
{
    public static EncryptedBlobStore Create(int chunkSize = 64) =>
        new(
            new FixedKeyProvider(),
            new EncryptedBlobStoreOptions { ChunkSize = chunkSize });

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
