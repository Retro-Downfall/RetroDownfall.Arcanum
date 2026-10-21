using System.Security.Cryptography;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Storage;

namespace RetroDownfall.Arcanum.Tests.Storage;

public sealed class EncryptedBlobInspectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arcanum-blob-inspection-" + Guid.NewGuid().ToString("N"));

    public EncryptedBlobInspectionTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65)]
    [InlineData(1024 * 1024 + 7)]
    public async Task Inspection_returns_authenticated_plaintext_identity_and_closes_reader(int length)
    {
        using ReadOnlyInspectionKeys keys = new();
        EncryptedBlobStore store = new(keys, new EncryptedBlobStoreOptions { ChunkSize = 32 });
        byte[] plaintext = RandomNumberGenerator.GetBytes(length);
        byte[] metadata = [1, 3, 5, 7];
        string path = Path.Combine(_root, "content");
        EncryptedBlobDescriptor written = await store.WriteAsync(
            path, new MemoryStream(plaintext), EncryptedBlobPurpose.SessionAttachment, metadata);

        int peeksBeforeInspection = keys.PeekCalls;

        EncryptedBlobPlaintextInspection inspection = await store.InspectAuthenticatedAsync(
            path, EncryptedBlobPurpose.SessionAttachment);

        Assert.Equal(SHA256.HashData(plaintext), inspection.PlaintextSha256.ToArray());
        Assert.Equal(length, inspection.Descriptor.PlaintextLength);
        Assert.Equal(written.Version, inspection.Descriptor.Version);
        Assert.Equal(written.KeyId, inspection.Descriptor.KeyId);
        Assert.Equal(metadata, inspection.Descriptor.AuthenticatedMetadata.ToArray());
        Assert.Equal(peeksBeforeInspection + 1, keys.PeekCalls);
        Assert.Equal(0, keys.OrdinaryReadCalls);
        using FileStream exclusive = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData("final-tag")]
    [InlineData("truncated")]
    [InlineData("wrong-purpose")]
    public async Task Inspection_rejects_unauthenticated_content_and_closes_reader(string failure)
    {
        using ReadOnlyInspectionKeys keys = new();
        EncryptedBlobStore store = new(keys, new EncryptedBlobStoreOptions { ChunkSize = 32 });
        string path = Path.Combine(_root, "invalid");
        await store.WriteAsync(path, new MemoryStream(new byte[97]), EncryptedBlobPurpose.SessionAttachment);
        byte[] envelope = await File.ReadAllBytesAsync(path);
        if (failure == "final-tag")
        {
            envelope[^1] ^= 1;
            await File.WriteAllBytesAsync(path, envelope);
        }
        else if (failure == "truncated")
        {
            await File.WriteAllBytesAsync(path, envelope[..^1]);
        }

        Exception? error = await Record.ExceptionAsync(() => store.InspectAuthenticatedAsync(
            path, failure == "wrong-purpose" ? EncryptedBlobPurpose.UploadedFile : EncryptedBlobPurpose.SessionAttachment));

        Assert.True(error is CryptographicException or InvalidDataException, error?.ToString() ?? "Inspection accepted unauthenticated content.");
        using FileStream exclusive = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task Inspection_honors_cancellation_and_closes_reader()
    {
        using ReadOnlyInspectionKeys keys = new();
        EncryptedBlobStore store = new(keys, new EncryptedBlobStoreOptions { ChunkSize = 32 });
        string path = Path.Combine(_root, "cancelled");
        await store.WriteAsync(path, new MemoryStream(new byte[97]), EncryptedBlobPurpose.SessionAttachment);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.InspectAuthenticatedAsync(
            path, EncryptedBlobPurpose.SessionAttachment, cancellation.Token));

        using FileStream exclusive = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private sealed class ReadOnlyInspectionKeys : IFileEncryptionKeyProvider, IDisposable
    {
        private readonly FileEncryptionKeyMaterial _key = FileEncryptionKeyMaterial.Create(new byte[32]);

        public int PeekCalls { get; private set; }

        public int OrdinaryReadCalls { get; private set; }

        public ValueTask<FileEncryptionKeyMaterial> GetForWriteAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(_key);

        public ValueTask<FileEncryptionKeyMaterial> GetForReadAsync(string keyId, CancellationToken cancellationToken = default)
        {
            OrdinaryReadCalls++;
            return ValueTask.FromResult(_key);
        }

        public ValueTask<FileEncryptionKeyMaterial> PeekForReadAsync(string keyId, CancellationToken cancellationToken = default)
        {
            PeekCalls++;
            return ValueTask.FromResult(_key);
        }

        public void Dispose() => _key.Dispose();
    }
}
