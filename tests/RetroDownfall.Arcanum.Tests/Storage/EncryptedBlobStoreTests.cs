using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Storage;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Storage;

public sealed class EncryptedBlobStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "arcanum-encrypted-blobs-" + Guid.NewGuid().ToString("N"));

    public EncryptedBlobStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(95)]
    [InlineData(96)]
    [InlineData(97)]
    [InlineData(1024 * 1024 + 7)]
    public async Task Write_then_read_round_trips_empty_chunk_boundaries_and_large_payloads(int length)
    {
        byte[] plaintext = RandomNumberGenerator.GetBytes(length);
        EncryptedBlobStore store = CreateStore(chunkSize: 32);
        string path = Path.Combine(_root, "round-trip-" + length);

        EncryptedBlobDescriptor descriptor = await store.WriteAsync(
            path,
            new MemoryStream(plaintext),
            EncryptedBlobPurpose.UploadedFile,
            Encoding.UTF8.GetBytes("record-id"));

        Assert.Equal(EncryptedBlobFormat.CurrentVersion, descriptor.Version);
        Assert.Equal(EncryptedBlobAlgorithm.Aes256Gcm, descriptor.Algorithm);
        Assert.Equal(length, descriptor.PlaintextLength);
        Assert.NotEqual(plaintext, await File.ReadAllBytesAsync(path));

        await using Stream decrypted = await store.OpenReadAsync(
            path,
            EncryptedBlobPurpose.UploadedFile);
        using MemoryStream result = new();
        await decrypted.CopyToAsync(result);

        Assert.Equal(plaintext, result.ToArray());
    }

    [Fact]
    public async Task Read_detects_single_byte_ciphertext_corruption_before_returning_that_chunk()
    {
        EncryptedBlobStore store = CreateStore(chunkSize: 32);
        string path = Path.Combine(_root, "corrupt");
        byte[] plaintext = RandomNumberGenerator.GetBytes(96);
        EncryptedBlobDescriptor descriptor = await store.WriteAsync(
            path,
            new MemoryStream(plaintext),
            EncryptedBlobPurpose.SessionAttachment);

        byte[] envelope = await File.ReadAllBytesAsync(path);
        envelope[descriptor.HeaderLength + 5] ^= 0x01;
        await File.WriteAllBytesAsync(path, envelope);

        await using Stream decrypted = await store.OpenReadAsync(
            path,
            EncryptedBlobPurpose.SessionAttachment);
        byte[] firstChunk = new byte[32];

        await Assert.ThrowsAnyAsync<CryptographicException>(
            () => decrypted.ReadExactlyAsync(firstChunk).AsTask());
    }

    [Fact]
    public async Task Read_rejects_truncation_wrong_key_and_wrong_purpose()
    {
        EncryptedBlobStore writer = CreateStore(chunkSize: 32);
        string path = Path.Combine(_root, "invalid");
        await writer.WriteAsync(
            path,
            new MemoryStream(RandomNumberGenerator.GetBytes(65)),
            EncryptedBlobPurpose.BatchArtifact);

        EncryptedBlobStore wrongKey = CreateStore(
            chunkSize: 32,
            RandomNumberGenerator.GetBytes(32));
        await Assert.ThrowsAsync<EncryptedBlobKeyException>(
            () => wrongKey.OpenReadAsync(path, EncryptedBlobPurpose.BatchArtifact));
        await Assert.ThrowsAsync<CryptographicException>(
            () => writer.OpenReadAsync(path, EncryptedBlobPurpose.UploadedFile));

        await using (FileStream file = new(path, FileMode.Open, FileAccess.Write))
        {
            file.SetLength(file.Length - 1);
        }

        await Assert.ThrowsAsync<InvalidDataException>(
            () => writer.OpenReadAsync(path, EncryptedBlobPurpose.BatchArtifact));
    }

    [Fact]
    public async Task Identical_plaintext_uses_a_fresh_nonce_and_trailing_data_is_rejected()
    {
        EncryptedBlobStore store = CreateStore(chunkSize: 32);
        byte[] plaintext = Encoding.UTF8.GetBytes("same plaintext");
        string firstPath = Path.Combine(_root, "nonce-first");
        string secondPath = Path.Combine(_root, "nonce-second");

        await store.WriteAsync(
            firstPath,
            new MemoryStream(plaintext),
            EncryptedBlobPurpose.UploadedFile);
        await store.WriteAsync(
            secondPath,
            new MemoryStream(plaintext),
            EncryptedBlobPurpose.UploadedFile);

        byte[] first = await File.ReadAllBytesAsync(firstPath);
        byte[] second = await File.ReadAllBytesAsync(secondPath);
        Assert.NotEqual(first, second);

        await using (FileStream append = new(
                         firstPath,
                         FileMode.Append,
                         FileAccess.Write,
                         FileShare.None))
        {
            await append.WriteAsync(new byte[] { 0x42 });
        }

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.OpenReadAsync(firstPath, EncryptedBlobPurpose.UploadedFile));
    }

    [Fact]
    public async Task Failed_or_cancelled_write_leaves_existing_destination_and_no_temp_file()
    {
        EncryptedBlobStore store = CreateStore(chunkSize: 32);
        string path = Path.Combine(_root, "atomic");
        byte[] original = Encoding.UTF8.GetBytes("existing ciphertext");
        await File.WriteAllBytesAsync(path, original);
        using CancellationTokenSource cts = new();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.WriteAsync(
                path,
                new MemoryStream(RandomNumberGenerator.GetBytes(128)),
                EncryptedBlobPurpose.UploadedFile,
                cancellationToken: cts.Token));

        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(_root, ".atomic.tmp.*"));
    }

    [Fact]
    public async Task Concurrent_readers_are_independent_and_sequential_reads_are_bounded()
    {
        byte[] plaintext = RandomNumberGenerator.GetBytes(4097);
        EncryptedBlobStore store = CreateStore(chunkSize: 64);
        string path = Path.Combine(_root, "concurrent");
        await store.WriteAsync(
            path,
            new MemoryStream(plaintext),
            EncryptedBlobPurpose.SessionAttachment);

        Task<byte[]>[] reads = Enumerable.Range(0, 8)
            .Select(async _ =>
            {
                await using Stream stream = await store.OpenReadAsync(
                    path,
                    EncryptedBlobPurpose.SessionAttachment);
                using MemoryStream output = new();
                byte[] buffer = new byte[17];
                int read;
                while ((read = await stream.ReadAsync(buffer)) != 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read));
                }

                return output.ToArray();
            })
            .ToArray();

        byte[][] results = await Task.WhenAll(reads);
        Assert.All(results, result => Assert.Equal(plaintext, result));
    }

    // OpenReadAsync hands out a public Stream and CreateWriterAsync hands out a public
    // EncryptedBlobWriter, so a synchronous consumer lands on Read/Write/Dispose — StreamWriter's own
    // synchronous Flush and Dispose are exactly that, and BatchProcessingService wraps this writer in
    // one. Those overrides blocked on the async path with GetAwaiter().GetResult() over handles opened
    // FileOptions.Asynchronous, pinning a pool thread for the duration of real file I/O.
    [Fact]
    public void Encrypted_blob_streams_have_no_sync_over_async_overrides()
    {
        ProductionSource source = Assert.Single(
            ProductionSourceInventory.Sources(),
            static candidate => candidate.Is("EncryptedBlobStore.cs"));

        Assert.DoesNotContain("GetAwaiter().GetResult()", source.Text, StringComparison.Ordinal);
    }

    // The synchronous overrides are separate code paths through the same AEAD seal/open, so they need
    // their own round-trip: a chunk-straddling payload written through Stream.Write and read back
    // through Stream.CopyTo, which is Stream.Read underneath.
    [Fact]
    public async Task Streaming_writer_and_reader_round_trip_through_the_synchronous_stream_api()
    {
        EncryptedBlobStore store = CreateStore(chunkSize: 32);
        string path = Path.Combine(_root, "synchronous-round-trip");
        byte[] plaintext = RandomNumberGenerator.GetBytes(205);
        EncryptedBlobDescriptor descriptor;

        await using (EncryptedBlobWriter writer = await store.CreateWriterAsync(
                         path,
                         EncryptedBlobPurpose.BatchArtifact))
        {
            for (int offset = 0; offset < plaintext.Length; offset += 7)
            {
                writer.Write(plaintext, offset, Math.Min(7, plaintext.Length - offset));
            }

            writer.Flush();
            descriptor = await writer.CompleteAsync();
        }

        Assert.Equal(plaintext.Length, descriptor.PlaintextLength);
        await using Stream reader = await store.OpenReadAsync(
            path,
            EncryptedBlobPurpose.BatchArtifact);
        using MemoryStream roundTrip = new();
        reader.CopyTo(roundTrip);
        Assert.Equal(plaintext, roundTrip.ToArray());
    }

    [Fact]
    public async Task Streaming_writer_encrypts_unknown_length_without_plaintext_staging()
    {
        EncryptedBlobStore store = CreateStore(chunkSize: 32);
        string path = Path.Combine(_root, "streaming");
        byte[] plaintext = RandomNumberGenerator.GetBytes(205);
        await using EncryptedBlobWriter writer = await store.CreateWriterAsync(
            path,
            EncryptedBlobPurpose.BatchArtifact);

        Assert.False(File.Exists(path));
        for (int offset = 0; offset < plaintext.Length; offset += 7)
        {
            int count = Math.Min(7, plaintext.Length - offset);
            await writer.WriteAsync(plaintext.AsMemory(offset, count));
        }

        EncryptedBlobDescriptor descriptor = await writer.CompleteAsync();

        Assert.Equal(plaintext.Length, descriptor.PlaintextLength);
        byte[] stored = await File.ReadAllBytesAsync(path);
        Assert.True(stored.AsSpan().StartsWith("ARCABLOB"u8));
        Assert.NotEqual(plaintext, stored);
        await using Stream reader = await store.OpenReadAsync(
            path,
            EncryptedBlobPurpose.BatchArtifact);
        using MemoryStream roundTrip = new();
        await reader.CopyToAsync(roundTrip);
        Assert.Equal(plaintext, roundTrip.ToArray());
    }

    // The AEAD must bind the declared plaintext length. Truncating at an exact chunk boundary and
    // rewriting the header length field keeps the envelope self-consistent, so without a final-chunk
    // marker every surviving chunk still authenticates and the caller is handed a silently
    // truncated document.
    [Theory]
    [InlineData(112, 32)]
    [InlineData(96, 32)]
    [InlineData(64, 32)]
    public async Task Read_rejects_a_boundary_truncation_that_rewrites_the_declared_length(
        int length,
        int chunkSize)
    {
        EncryptedBlobStore store = CreateStore(chunkSize);
        string path = Path.Combine(_root, $"truncate-{length}");
        byte[] plaintext = RandomNumberGenerator.GetBytes(length);
        EncryptedBlobDescriptor descriptor = await store.WriteAsync(
            path,
            new MemoryStream(plaintext),
            EncryptedBlobPurpose.UploadedFile,
            Encoding.UTF8.GetBytes("record-id"));

        byte[] envelope = await File.ReadAllBytesAsync(path);
        byte[] truncated = envelope
            .AsSpan(0, descriptor.HeaderLength + chunkSize + 16)
            .ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(
            truncated.AsSpan(16, 8),
            chunkSize);
        await File.WriteAllBytesAsync(path, truncated);

        await using Stream reader = await store.OpenReadAsync(
            path,
            EncryptedBlobPurpose.UploadedFile);
        using MemoryStream output = new();

        await Assert.ThrowsAnyAsync<CryptographicException>(() => reader.CopyToAsync(output));
    }

    // The streaming writer discovers the length as it goes, so the chunk that turns out to be last
    // must still carry the final marker — including when the total is an exact multiple of the
    // chunk size, where the last full buffer would otherwise be sealed as a non-final chunk.
    [Theory]
    [InlineData(64, 32)]
    [InlineData(96, 32)]
    public async Task Streaming_writer_rejects_a_boundary_truncation_on_exact_chunk_multiples(
        int length,
        int chunkSize)
    {
        EncryptedBlobStore store = CreateStore(chunkSize);
        string path = Path.Combine(_root, $"streaming-truncate-{length}");
        byte[] plaintext = RandomNumberGenerator.GetBytes(length);
        EncryptedBlobDescriptor descriptor;
        await using (EncryptedBlobWriter writer = await store.CreateWriterAsync(
                         path,
                         EncryptedBlobPurpose.BatchArtifact))
        {
            await writer.WriteAsync(plaintext);
            descriptor = await writer.CompleteAsync();
        }

        Assert.Equal(length, descriptor.PlaintextLength);
        await using (Stream verify = await store.OpenReadAsync(
                         path,
                         EncryptedBlobPurpose.BatchArtifact))
        {
            using MemoryStream roundTrip = new();
            await verify.CopyToAsync(roundTrip);
            Assert.Equal(plaintext, roundTrip.ToArray());
        }

        byte[] envelope = await File.ReadAllBytesAsync(path);
        byte[] truncated = envelope
            .AsSpan(0, descriptor.HeaderLength + chunkSize + 16)
            .ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(
            truncated.AsSpan(16, 8),
            chunkSize);
        await File.WriteAllBytesAsync(path, truncated);

        await using Stream reader = await store.OpenReadAsync(
            path,
            EncryptedBlobPurpose.BatchArtifact);
        using MemoryStream output = new();

        await Assert.ThrowsAnyAsync<CryptographicException>(() => reader.CopyToAsync(output));
    }

    // The declared plaintext length and chunk size are unauthenticated until a chunk decrypts, so
    // bit rot or a hostile edit can drive the cheap envelope-length pre-check into arithmetic
    // overflow. That must fail closed as invalid data: every corruption handler in the migrate,
    // rotate-key, and doctor call chain filters on InvalidDataException, and an escaping
    // OverflowException aborts the whole durable operation instead of failing one candidate.
    [Theory]
    [InlineData(long.MaxValue, 0)]
    [InlineData(5_000_000_000_000_000_000, 16)]
    public async Task Read_rejects_a_corrupt_header_whose_length_metadata_overflows(
        long declaredLength,
        int corruptedChunkSize)
    {
        EncryptedBlobStore store = CreateStore(chunkSize: 32);
        string path = Path.Combine(_root, $"overflow-{declaredLength}-{corruptedChunkSize}");
        await store.WriteAsync(
            path,
            new MemoryStream(RandomNumberGenerator.GetBytes(64)),
            EncryptedBlobPurpose.UploadedFile);

        byte[] envelope = await File.ReadAllBytesAsync(path);
        if (corruptedChunkSize != 0)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(
                envelope.AsSpan(12, 4),
                corruptedChunkSize);
        }

        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(
            envelope.AsSpan(16, 8),
            declaredLength);
        await File.WriteAllBytesAsync(path, envelope);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.OpenReadAsync(path, EncryptedBlobPurpose.UploadedFile));
    }

    // Migration and key rotation replace the blob while a reader is still open on the same path.
    // On Windows that rename needs FileShare.Delete on the read handle; without it every candidate
    // fails and `arcanum data encryption migrate` / `rotate-key` can never complete.
    [Fact]
    public async Task Open_reader_does_not_block_replacing_the_same_path()
    {
        EncryptedBlobStore store = CreateStore(chunkSize: 32);
        string path = Path.Combine(_root, "replace-while-open");
        byte[] original = RandomNumberGenerator.GetBytes(200);
        await store.WriteAsync(
            path,
            new MemoryStream(original),
            EncryptedBlobPurpose.SessionAttachment);

        await using Stream reader = await store.OpenReadAsync(
            path,
            EncryptedBlobPurpose.SessionAttachment);

        await store.WriteAsync(
            path,
            new MemoryStream(original),
            EncryptedBlobPurpose.SessionAttachment);

        using MemoryStream output = new();
        await reader.CopyToAsync(output);
        Assert.Equal(original, output.ToArray());
    }

    [Fact]
    public async Task Inspection_loads_a_cold_key_without_persistent_credential_mutation()
    {

        string encodedKey = Convert.ToBase64String(
            Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray());

        string path = Path.Combine(_root, "pure-inspection");

        using (FileEncryptionKeyProvider writerKeys = new(
                   new MigrationSensitiveSecretStore(encodedKey)))
        {

            EncryptedBlobStore writer = new(
                writerKeys,
                new EncryptedBlobStoreOptions { ChunkSize = 32 });

            _ = await writer.WriteAsync(
                path,
                new MemoryStream(Encoding.UTF8.GetBytes("diagnostic payload")),
                EncryptedBlobPurpose.UploadedFile);

        }

        MigrationSensitiveSecretStore diagnosticSecrets = new(encodedKey);

        using FileEncryptionKeyProvider diagnosticKeys = new(diagnosticSecrets);

        EncryptedBlobStore inspector = new(
            diagnosticKeys,
            new EncryptedBlobStoreOptions { ChunkSize = 32 });

        EncryptedBlobDescriptor descriptor = await inspector.InspectAsync(
            path,
            EncryptedBlobPurpose.UploadedFile,
            verifyAllChunks: true);

        Assert.Equal("diagnostic payload".Length, descriptor.PlaintextLength);

        Assert.Equal(0, diagnosticSecrets.PersistentMutationCount);

        Assert.Equal(1, diagnosticSecrets.PeekCount);

    }

    private static EncryptedBlobStore CreateStore(int chunkSize, byte[]? key = null)
    {
        byte[] actualKey = key ?? Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
        return new EncryptedBlobStore(
            new FixedFileEncryptionKeyProvider(actualKey),
            new EncryptedBlobStoreOptions { ChunkSize = chunkSize });
    }

    private sealed class FixedFileEncryptionKeyProvider(byte[] masterKey) : IFileEncryptionKeyProvider
    {
        private readonly FileEncryptionKeyMaterial _material = FileEncryptionKeyMaterial.Create(masterKey);

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
                throw new EncryptedBlobKeyException(
                    "The encrypted blob requires unavailable file-encryption key material.");
            }

            return ValueTask.FromResult(_material);
        }
    }

    private sealed class MigrationSensitiveSecretStore(string encodedKey) : ISecretStore
    {

        public int PersistentMutationCount { get; private set; }

        public int PeekCount { get; private set; }

        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>(null);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Missing());

        public Task SaveApiKeyAsync(string apiKey) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() =>
            Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) =>
            Task.CompletedTask;

        public Task<SecretStoreReadResult> GetFileEncryptionSecretReadResultAsync()
        {

            PersistentMutationCount++;

            return Task.FromResult(SecretStoreReadResult.Ok(encodedKey));

        }

        public Task<SecretStoreReadResult> PeekFileEncryptionSecretReadResultAsync()
        {

            PeekCount++;

            return Task.FromResult(SecretStoreReadResult.Ok(encodedKey));

        }

        public Task SaveFileEncryptionSecretAsync(string encryptionSecret) =>
            Task.CompletedTask;

    }
}
