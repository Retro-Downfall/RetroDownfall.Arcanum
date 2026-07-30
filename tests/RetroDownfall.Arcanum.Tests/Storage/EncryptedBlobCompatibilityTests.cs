using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Storage;

namespace RetroDownfall.Arcanum.Tests.Storage;

public sealed class EncryptedBlobCompatibilityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "arcanum-encrypted-compatibility-" + Guid.NewGuid().ToString("N"));

    public EncryptedBlobCompatibilityTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Legacy_metadata_reads_plaintext_during_migration()
    {
        byte[] plaintext = Encoding.UTF8.GetBytes("legacy content");
        string path = Path.Combine(_root, "legacy");
        await File.WriteAllBytesAsync(path, plaintext);
        EncryptedBlobStore store = CreateStore();

        await using Stream stream = await store.OpenCompatibleReadAsync(
            path,
            EncryptedBlobPurpose.UploadedFile,
            encryptionVersion: 0);
        using MemoryStream output = new();
        await stream.CopyToAsync(output);

        Assert.Equal(plaintext, output.ToArray());
    }

    [Fact]
    public async Task Legacy_metadata_reads_verified_envelope_after_replace_before_metadata_commit()
    {
        byte[] plaintext = Encoding.UTF8.GetBytes("replacement already committed");
        string path = Path.Combine(_root, "replaced");
        EncryptedBlobStore store = CreateStore();
        await store.WriteAsync(
            path,
            new MemoryStream(plaintext),
            EncryptedBlobPurpose.SessionAttachment);

        await using Stream stream = await store.OpenCompatibleReadAsync(
            path,
            EncryptedBlobPurpose.SessionAttachment,
            encryptionVersion: 0);
        using MemoryStream output = new();
        await stream.CopyToAsync(output);

        Assert.Equal(plaintext, output.ToArray());
    }

    [Fact]
    public async Task Encrypted_metadata_never_downgrades_to_plaintext()
    {
        string path = Path.Combine(_root, "downgrade");
        await File.WriteAllTextAsync(path, "not an envelope");
        EncryptedBlobStore store = CreateStore();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.OpenCompatibleReadAsync(
                path,
                EncryptedBlobPurpose.UploadedFile,
                EncryptedBlobFormat.CurrentVersion));
    }

    private static EncryptedBlobStore CreateStore()
    {
        byte[] key = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
        return new EncryptedBlobStore(new FixedKeyProvider(key));
    }

    private sealed class FixedKeyProvider(byte[] key) : IFileEncryptionKeyProvider
    {
        private readonly FileEncryptionKeyMaterial _material = FileEncryptionKeyMaterial.Create(key);

        public ValueTask<FileEncryptionKeyMaterial> GetForWriteAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_material);

        public ValueTask<FileEncryptionKeyMaterial> GetForReadAsync(
            string keyId,
            CancellationToken cancellationToken = default)
        {
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(keyId),
                    Encoding.ASCII.GetBytes(_material.KeyId)))
            {
                throw new EncryptedBlobKeyException("Unknown key.");
            }

            return ValueTask.FromResult(_material);
        }
    }
}
