using System.Buffers.Binary;
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

    // Version 1 envelopes were written before the plaintext length was bound into the AEAD. They are
    // never written again, but every blob already on disk must still decrypt or the format bump
    // would destroy every stored attachment, upload, and batch artifact.
    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    [InlineData(32)]
    [InlineData(70)]
    public async Task Version_1_envelopes_written_before_the_length_binding_still_decrypt(int length)
    {
        byte[] key = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
        byte[] plaintext = RandomNumberGenerator.GetBytes(length);
        string path = Path.Combine(_root, "legacy-v1-" + length);
        await File.WriteAllBytesAsync(
            path,
            BuildVersion1Envelope(key, plaintext, chunkSize: 32, EncryptedBlobPurpose.UploadedFile));
        EncryptedBlobStore store = new(
            new FixedKeyProvider(key),
            new EncryptedBlobStoreOptions { ChunkSize = 32 });

        await using Stream stream = await store.OpenReadAsync(path, EncryptedBlobPurpose.UploadedFile);
        using MemoryStream output = new();
        await stream.CopyToAsync(output);

        Assert.Equal(plaintext, output.ToArray());
    }

    // Hand-builds the pre-length-binding envelope: header with bytes 16..24 zeroed in the AAD, and a
    // per-chunk suffix of (chunkIndex, chunkLength) with no final-chunk marker.
    private static byte[] BuildVersion1Envelope(
        byte[] masterKey,
        byte[] plaintext,
        int chunkSize,
        EncryptedBlobPurpose purpose)
    {
        const int fixedHeaderLength = 37;
        byte[] keyId = Encoding.ASCII.GetBytes(FileEncryptionKeyMaterial.Create(masterKey).KeyId);
        byte[] header = new byte[fixedHeaderLength + keyId.Length];
        "ARCABLOB"u8.CopyTo(header);
        header[8] = EncryptedBlobFormat.LegacyVersion1;
        header[9] = (byte)EncryptedBlobAlgorithm.Aes256Gcm;
        header[10] = (byte)purpose;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(12, 4), chunkSize);
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(16, 8), plaintext.Length);
        header[24] = (byte)keyId.Length;
        RandomNumberGenerator.Fill(header.AsSpan(25, 8));
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(33, 4), 0);
        keyId.CopyTo(header, fixedHeaderLength);

        byte[] purposeKey = new byte[32];
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            masterKey,
            purposeKey,
            salt: ReadOnlySpan<byte>.Empty,
            Encoding.UTF8.GetBytes("Arcanum.EncryptedBlob.v1:" + purpose));

        byte[] aad = new byte[header.Length + 8];
        header.CopyTo(aad, 0);
        aad.AsSpan(16, 8).Clear();
        byte[] nonce = new byte[12];
        header.AsSpan(25, 8).CopyTo(nonce);
        using AesGcm aes = new(purposeKey, 16);
        using MemoryStream envelope = new();
        envelope.Write(header);

        int offset = 0;
        uint chunkIndex = 0;
        do
        {
            int count = Math.Min(chunkSize, plaintext.Length - offset);
            byte[] cipher = new byte[count];
            byte[] tag = new byte[16];
            BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8), chunkIndex);
            BinaryPrimitives.WriteUInt32BigEndian(aad.AsSpan(header.Length, 4), chunkIndex);
            BinaryPrimitives.WriteInt32BigEndian(aad.AsSpan(header.Length + 4, 4), count);
            aes.Encrypt(nonce, plaintext.AsSpan(offset, count), cipher, tag, aad);
            envelope.Write(cipher);
            envelope.Write(tag);
            offset += count;
            chunkIndex++;
        }
        while (offset < plaintext.Length);

        return envelope.ToArray();
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
