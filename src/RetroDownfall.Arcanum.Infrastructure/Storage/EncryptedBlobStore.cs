using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Storage;

public sealed class EncryptedBlobStoreOptions
{
    public const int DefaultChunkSize = 1024 * 1024;

    public int ChunkSize { get; init; } = DefaultChunkSize;
}

public sealed class EncryptedBlobStore : IEncryptedBlobStore
{
    private const int MagicLength = 8;

    private const int FixedHeaderLength = 37;

    private const int NoncePrefixLength = 8;

    private const int NonceLength = 12;

    private const int TagLength = 16;

    private const int MaximumMetadataLength = 16 * 1024;

    private const int MaximumKeyIdLength = 64;

    private const int MinimumChunkSize = 16;

    private const int MaximumChunkSize = 16 * 1024 * 1024;

    private const int LegacyAadSuffixLength = 8;

    private const int LengthBoundAadSuffixLength = 17;
    private const string KeyDerivationLabel = "Arcanum.EncryptedBlob.v1:";
    private static ReadOnlySpan<byte> Magic => "ARCABLOB"u8;

    private readonly IFileEncryptionKeyProvider _keyProvider;

    private readonly int _chunkSize;

    public EncryptedBlobStore(
        IFileEncryptionKeyProvider keyProvider,
        EncryptedBlobStoreOptions? options = null)
    {
        _keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
        _chunkSize = options?.ChunkSize ?? EncryptedBlobStoreOptions.DefaultChunkSize;
        if (_chunkSize is < MinimumChunkSize or > MaximumChunkSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"Encrypted blob chunk size must be between {MinimumChunkSize} and {MaximumChunkSize} bytes.");
        }
    }

    public bool HasEnvelope(string path)
    {
        try
        {
            Span<byte> magic = stackalloc byte[MagicLength];
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return stream.Read(magic) == MagicLength
                && CryptographicOperations.FixedTimeEquals(magic, Magic);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public async Task<EncryptedBlobDescriptor> WriteAsync(
        string destinationPath,
        Stream plaintext,
        EncryptedBlobPurpose purpose,
        ReadOnlyMemory<byte> authenticatedMetadata = default,
        long? plaintextLength = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(plaintext);
        ValidatePurpose(purpose);
        cancellationToken.ThrowIfCancellationRequested();

        long length = ResolvePlaintextLength(plaintext, plaintextLength);
        if (authenticatedMetadata.Length > MaximumMetadataLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(authenticatedMetadata),
                $"Authenticated metadata cannot exceed {MaximumMetadataLength} bytes.");
        }

        FileEncryptionKeyMaterial material = await _keyProvider
            .GetForWriteAsync(cancellationToken)
            .ConfigureAwait(false);
        byte[] header = BuildHeader(
            material.KeyId,
            purpose,
            length,
            _chunkSize,
            authenticatedMetadata.Span);
        byte[] purposeKey = DerivePurposeKey(material.MasterKey.Span, purpose);

        string fullDestinationPath = Path.GetFullPath(destinationPath);
        string directory = Path.GetDirectoryName(fullDestinationPath)
            ?? throw new InvalidOperationException("Encrypted blob destination has no parent directory.");
        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(directory);
        string tempPath = Path.Combine(
            directory,
            "." + Path.GetFileName(fullDestinationPath) + ".tmp." + Guid.NewGuid().ToString("N"));

        try
        {
            await using (FileStream output = SecureFilePermissions.CreateOwnerOnlyTempFile(tempPath))
            {
                await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                await EncryptChunksAsync(
                        plaintext,
                        output,
                        header,
                        purposeKey,
                        length,
                        cancellationToken)
                    .ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            await VerifyEnvelopeAsync(tempPath, purpose, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, fullDestinationPath, overwrite: true);
            SecureFilePermissions.ApplyOwnerOnlyFile(fullDestinationPath);

            return ParseDescriptor(header);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(purposeKey);
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                    // Best-effort cleanup; the owner-only temp never contained plaintext.
                }
                catch (UnauthorizedAccessException)
                {
                    // Best-effort cleanup; the owner-only temp never contained plaintext.
                }
            }
        }
    }

    public async Task<Stream> OpenReadAsync(
        string path,
        EncryptedBlobPurpose purpose,
        CancellationToken cancellationToken = default) =>
        await OpenReadCoreAsync(
                path,
                purpose,
                peekKey: false,
                cancellationToken)
            .ConfigureAwait(false);

    private async Task<Stream> OpenReadCoreAsync(
        string path,
        EncryptedBlobPurpose purpose,
        bool peekKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ValidatePurpose(purpose);
        cancellationToken.ThrowIfCancellationRequested();

        FileStream input = new(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                // FileShare.Delete is required: migration and key rotation replace this very path
                // with File.Move(overwrite: true) while the reader is still open, and on Windows
                // MOVEFILE_REPLACE_EXISTING needs DELETE access the destination handle must share.
                Share = FileShare.Read | FileShare.Delete,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });

        try
        {
            (EncryptedBlobDescriptor descriptor, byte[] header, byte[] noncePrefix) =
                await ReadHeaderAsync(input, purpose, cancellationToken).ConfigureAwait(false);
            ValidateEnvelopeLength(input.Length, descriptor);
            FileEncryptionKeyMaterial material = peekKey
                ? await _keyProvider
                    .PeekForReadAsync(descriptor.KeyId, cancellationToken)
                    .ConfigureAwait(false)
                : await _keyProvider
                    .GetForReadAsync(descriptor.KeyId, cancellationToken)
                    .ConfigureAwait(false);
            byte[] purposeKey = DerivePurposeKey(material.MasterKey.Span, purpose);
            return new EncryptedBlobReadStream(
                input,
                descriptor,
                header,
                noncePrefix,
                purposeKey);
        }
        catch
        {
            await input.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<EncryptedBlobWriter> CreateWriterAsync(
        string destinationPath,
        EncryptedBlobPurpose purpose,
        ReadOnlyMemory<byte> authenticatedMetadata = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ValidatePurpose(purpose);
        cancellationToken.ThrowIfCancellationRequested();
        if (authenticatedMetadata.Length > MaximumMetadataLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(authenticatedMetadata),
                $"Authenticated metadata cannot exceed {MaximumMetadataLength} bytes.");
        }

        FileEncryptionKeyMaterial material = await _keyProvider
            .GetForWriteAsync(cancellationToken)
            .ConfigureAwait(false);
        byte[] header = BuildHeader(
            material.KeyId,
            purpose,
            plaintextLength: 0,
            _chunkSize,
            authenticatedMetadata.Span);
        byte[] purposeKey = DerivePurposeKey(material.MasterKey.Span, purpose);
        string fullDestinationPath = Path.GetFullPath(destinationPath);
        string directory = Path.GetDirectoryName(fullDestinationPath)
            ?? throw new InvalidOperationException("Encrypted blob destination has no parent directory.");
        SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(directory);
        string tempPath = Path.Combine(
            directory,
            "." + Path.GetFileName(fullDestinationPath) + ".tmp." + Guid.NewGuid().ToString("N"));

        try
        {
            FileStream output = SecureFilePermissions.CreateOwnerOnlyTempFile(tempPath);
            try
            {
                await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                return new StreamingEncryptedBlobWriter(
                    this,
                    output,
                    tempPath,
                    fullDestinationPath,
                    purpose,
                    header,
                    purposeKey);
            }
            catch
            {
                await output.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch
        {
            CryptographicOperations.ZeroMemory(purposeKey);
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            throw;
        }
    }

    public async Task<EncryptedBlobDescriptor> InspectAsync(
        string path,
        EncryptedBlobPurpose purpose,
        bool verifyAllChunks,
        CancellationToken cancellationToken = default)
    {
        await using Stream stream = await OpenReadCoreAsync(
                path,
                purpose,
                peekKey: true,
                cancellationToken)
            .ConfigureAwait(false);
        EncryptedBlobDescriptor descriptor =
            ((EncryptedBlobReadStream)stream).Descriptor;
        if (verifyAllChunks)
        {
            byte[] buffer = new byte[Math.Min(descriptor.ChunkSize, 64 * 1024)];
            while (await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) != 0)
            {
            }
        }

        return descriptor;
    }

    private async Task VerifyEnvelopeAsync(
        string path,
        EncryptedBlobPurpose purpose,
        CancellationToken cancellationToken)
    {
        _ = await InspectAsync(path, purpose, verifyAllChunks: true, cancellationToken)
            .ConfigureAwait(false);
    }

    private static long ResolvePlaintextLength(Stream plaintext, long? suppliedLength)
    {
        long length;
        if (suppliedLength is { } expected)
        {
            length = expected;
        }
        else if (plaintext.CanSeek)
        {
            length = plaintext.Length - plaintext.Position;
        }
        else
        {
            throw new ArgumentException(
                "A plaintext length is required for a non-seekable encrypted-blob input.",
                nameof(suppliedLength));
        }

        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(suppliedLength));
        }

        return length;
    }

    private static byte[] BuildHeader(
        string keyId,
        EncryptedBlobPurpose purpose,
        long plaintextLength,
        int chunkSize,
        ReadOnlySpan<byte> metadata)
    {
        byte[] keyIdBytes = Encoding.ASCII.GetBytes(keyId);
        if (keyIdBytes.Length is 0 or > MaximumKeyIdLength)
        {
            throw new InvalidOperationException("The file-encryption key identifier is invalid.");
        }

        byte[] header = new byte[FixedHeaderLength + keyIdBytes.Length + metadata.Length];
        Magic.CopyTo(header);
        header[8] = EncryptedBlobFormat.CurrentVersion;
        header[9] = (byte)EncryptedBlobAlgorithm.Aes256Gcm;
        header[10] = (byte)purpose;
        header[11] = 0;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(12, 4), chunkSize);
        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(16, 8), plaintextLength);
        header[24] = checked((byte)keyIdBytes.Length);
        RandomNumberGenerator.Fill(header.AsSpan(25, NoncePrefixLength));
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(33, 4), metadata.Length);
        keyIdBytes.CopyTo(header, FixedHeaderLength);
        metadata.CopyTo(header.AsSpan(FixedHeaderLength + keyIdBytes.Length));
        return header;
    }

    /// <summary>
    /// Builds the per-chunk associated data prefix. Header bytes 16..24 (the declared plaintext
    /// length) are zeroed because the streaming writer back-patches that field once the total is
    /// known; from <see cref="EncryptedBlobFormat.LengthBoundVersion2"/> onwards the length is bound
    /// instead through the final-chunk suffix written by <see cref="WriteChunkAad"/>.
    /// </summary>
    private static byte[] CreateChunkAad(byte[] header)
    {
        byte[] aad = new byte[header.Length + AadSuffixLength(header[8])];
        header.CopyTo(aad, 0);
        aad.AsSpan(16, 8).Clear();
        return aad;
    }

    private static int AadSuffixLength(byte version) =>
        version >= EncryptedBlobFormat.LengthBoundVersion2
            ? LengthBoundAadSuffixLength
            : LegacyAadSuffixLength;

    private static void WriteChunkAad(
        byte[] aad,
        int headerLength,
        byte version,
        uint chunkIndex,
        int count,
        bool isFinal,
        long totalPlaintextLength)
    {
        BinaryPrimitives.WriteUInt32BigEndian(aad.AsSpan(headerLength, 4), chunkIndex);
        BinaryPrimitives.WriteInt32BigEndian(aad.AsSpan(headerLength + 4, 4), count);
        if (version < EncryptedBlobFormat.LengthBoundVersion2)
        {
            return;
        }

        aad[headerLength + 8] = isFinal ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt64BigEndian(
            aad.AsSpan(headerLength + 9, 8),
            isFinal ? totalPlaintextLength : 0L);
    }

    private static async Task EncryptChunksAsync(
        Stream plaintext,
        Stream output,
        byte[] header,
        byte[] purposeKey,
        long plaintextLength,
        CancellationToken cancellationToken)
    {
        byte version = header[8];
        int chunkSize = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(12, 4));
        byte[] plainBuffer = new byte[chunkSize];
        byte[] cipherBuffer = new byte[chunkSize];
        byte[] tag = new byte[TagLength];
        byte[] aad = CreateChunkAad(header);
        byte[] noncePrefix = header.AsSpan(25, NoncePrefixLength).ToArray();
        byte[] nonce = new byte[NonceLength];
        using AesGcm aes = new(purposeKey, TagLength);

        try
        {
            long remaining = plaintextLength;
            uint chunkIndex = 0;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                int expected = (int)Math.Min(chunkSize, remaining);
                if (expected > 0)
                {
                    await plaintext.ReadExactlyAsync(
                            plainBuffer.AsMemory(0, expected),
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                noncePrefix.CopyTo(nonce, 0);
                BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(NoncePrefixLength), chunkIndex);
                WriteChunkAad(
                    aad,
                    header.Length,
                    version,
                    chunkIndex,
                    expected,
                    isFinal: remaining - expected == 0,
                    plaintextLength);
                aes.Encrypt(
                    nonce,
                    plainBuffer.AsSpan(0, expected),
                    cipherBuffer.AsSpan(0, expected),
                    tag,
                    aad);
                await output.WriteAsync(cipherBuffer.AsMemory(0, expected), cancellationToken)
                    .ConfigureAwait(false);
                await output.WriteAsync(tag, cancellationToken).ConfigureAwait(false);
                remaining -= expected;
                chunkIndex++;
            }

            while (remaining > 0);

            byte[] extra = new byte[1];
            if (await plaintext.ReadAsync(extra, cancellationToken).ConfigureAwait(false) != 0)
            {
                throw new InvalidDataException(
                    "The plaintext stream exceeded its declared encrypted-blob length.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBuffer);
            CryptographicOperations.ZeroMemory(cipherBuffer);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(aad);
            CryptographicOperations.ZeroMemory(noncePrefix);
            CryptographicOperations.ZeroMemory(nonce);
        }
    }

    private static async Task<(EncryptedBlobDescriptor Descriptor, byte[] Header, byte[] NoncePrefix)>
        ReadHeaderAsync(
            Stream input,
            EncryptedBlobPurpose expectedPurpose,
            CancellationToken cancellationToken)
    {
        byte[] fixedHeader = new byte[FixedHeaderLength];
        try
        {
            await input.ReadExactlyAsync(fixedHeader, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("The encrypted blob header is truncated.", ex);
        }

        if (!CryptographicOperations.FixedTimeEquals(fixedHeader.AsSpan(0, MagicLength), Magic))
        {
            throw new InvalidDataException(
                "The file is not an Arcanum encrypted blob. Legacy plaintext must be migrated; ciphertext is never treated as plaintext.");
        }

        if (!EncryptedBlobFormat.IsSupportedVersion(fixedHeader[8]))
        {
            throw new InvalidDataException(
                $"Unsupported encrypted blob version {fixedHeader[8]}.");
        }

        if (fixedHeader[9] != (byte)EncryptedBlobAlgorithm.Aes256Gcm)
        {
            throw new InvalidDataException(
                $"Unsupported encrypted blob algorithm {fixedHeader[9]}.");
        }

        EncryptedBlobPurpose actualPurpose = (EncryptedBlobPurpose)fixedHeader[10];
        if (actualPurpose != expectedPurpose)
        {
            throw new CryptographicException(
                "The encrypted blob purpose does not match the requested purpose.");
        }

        int chunkSize = BinaryPrimitives.ReadInt32BigEndian(fixedHeader.AsSpan(12, 4));
        long plaintextLength = BinaryPrimitives.ReadInt64BigEndian(fixedHeader.AsSpan(16, 8));
        int keyIdLength = fixedHeader[24];
        int metadataLength = BinaryPrimitives.ReadInt32BigEndian(fixedHeader.AsSpan(33, 4));
        if (chunkSize is < MinimumChunkSize or > MaximumChunkSize
            || plaintextLength < 0
            || keyIdLength is 0 or > MaximumKeyIdLength
            || metadataLength is < 0 or > MaximumMetadataLength)
        {
            throw new InvalidDataException("The encrypted blob header contains invalid bounds.");
        }

        byte[] variableHeader = new byte[keyIdLength + metadataLength];
        try
        {
            await input.ReadExactlyAsync(variableHeader, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("The encrypted blob header is truncated.", ex);
        }

        byte[] header = new byte[fixedHeader.Length + variableHeader.Length];
        fixedHeader.CopyTo(header, 0);
        variableHeader.CopyTo(header, fixedHeader.Length);
        string keyId = Encoding.ASCII.GetString(variableHeader, 0, keyIdLength);
        byte[] metadata = variableHeader.AsSpan(keyIdLength, metadataLength).ToArray();
        byte[] noncePrefix = fixedHeader.AsSpan(25, NoncePrefixLength).ToArray();
        EncryptedBlobDescriptor descriptor = new(
            fixedHeader[8],
            (EncryptedBlobAlgorithm)fixedHeader[9],
            chunkSize,
            keyId,
            plaintextLength,
            header.Length,
            actualPurpose,
            metadata);
        return (descriptor, header, noncePrefix);
    }

    private static EncryptedBlobDescriptor ParseDescriptor(byte[] header)
    {
        int keyIdLength = header[24];
        int metadataLength = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(33, 4));
        return new EncryptedBlobDescriptor(
            header[8],
            (EncryptedBlobAlgorithm)header[9],
            BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(12, 4)),
            Encoding.ASCII.GetString(header, FixedHeaderLength, keyIdLength),
            BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(16, 8)),
            header.Length,
            (EncryptedBlobPurpose)header[10],
            header.AsMemory(FixedHeaderLength + keyIdLength, metadataLength).ToArray());
    }

    private static void ValidateEnvelopeLength(long actualLength, EncryptedBlobDescriptor descriptor)
    {
        // The declared length and chunk size are still unauthenticated here, so a corrupt header can
        // overflow this pre-check. Fail closed as invalid data rather than letting an
        // OverflowException escape every corruption handler in the call chain.
        try
        {
            long chunks = Math.Max(
                1,
                checked((descriptor.PlaintextLength + descriptor.ChunkSize - 1) / descriptor.ChunkSize));
            long expectedLength = checked(
                descriptor.HeaderLength
                + descriptor.PlaintextLength
                + chunks * TagLength);
            if (actualLength != expectedLength)
            {
                throw new InvalidDataException(
                    "The encrypted blob is truncated or has unexpected trailing bytes.");
            }
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException(
                "The encrypted blob length metadata overflows supported bounds.",
                ex);
        }
    }

    private static byte[] DerivePurposeKey(
        ReadOnlySpan<byte> masterKey,
        EncryptedBlobPurpose purpose)
    {
        byte[] output = new byte[32];
        byte[] info = Encoding.UTF8.GetBytes(KeyDerivationLabel + purpose);
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            masterKey,
            output,
            salt: ReadOnlySpan<byte>.Empty,
            info);
        return output;
    }

    private static void ValidatePurpose(EncryptedBlobPurpose purpose)
    {
        if (purpose is not EncryptedBlobPurpose.SessionAttachment
            and not EncryptedBlobPurpose.UploadedFile
            and not EncryptedBlobPurpose.BatchArtifact)
        {
            throw new ArgumentOutOfRangeException(nameof(purpose));
        }
    }

    private sealed class EncryptedBlobReadStream : EncryptedBlobReader
    {
        private readonly FileStream _input;

        private readonly byte[] _header;

        private readonly byte[] _noncePrefix;

        private readonly byte[] _purposeKey;

        private readonly AesGcm _aes;

        private readonly byte[] _cipherBuffer;

        private readonly byte[] _plainBuffer;
        private readonly byte[] _tag = new byte[TagLength];
        private readonly byte[] _aad;

        private long _remaining;

        private uint _chunkIndex;

        private int _plainOffset;

        private int _plainCount;

        private bool _disposed;

        public EncryptedBlobReadStream(
            FileStream input,
            EncryptedBlobDescriptor descriptor,
            byte[] header,
            byte[] noncePrefix,
            byte[] purposeKey)
        {
            _input = input;
            Descriptor = descriptor;
            _header = header;
            _noncePrefix = noncePrefix;
            _purposeKey = purposeKey;
            _aes = new AesGcm(_purposeKey, TagLength);
            _cipherBuffer = new byte[descriptor.ChunkSize];
            _plainBuffer = new byte[descriptor.ChunkSize];
            _aad = CreateChunkAad(header);
            _remaining = descriptor.PlaintextLength;
        }

        public override EncryptedBlobDescriptor Descriptor { get; }

        public override bool CanRead => !_disposed;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => Descriptor.PlaintextLength;

        public override long Position
        {
            get => Descriptor.PlaintextLength - _remaining - _plainCount + _plainOffset;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        // A genuine synchronous path. OpenReadAsync builds its FileStream with
        // FileOptions.Asynchronous, so blocking on ReadAsync here would pin a thread-pool thread for
        // the duration of real file I/O on every synchronous consumer — Stream.CopyTo and
        // JsonSerializer.Deserialize(Stream) among them.
        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (buffer.Length == 0)
            {
                return 0;
            }

            if (_plainOffset == _plainCount && !DecryptNextChunk())
            {
                return 0;
            }

            int count = Math.Min(buffer.Length, _plainCount - _plainOffset);
            _plainBuffer.AsSpan(_plainOffset, count).CopyTo(buffer);
            _plainOffset += count;
            return count;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (buffer.Length == 0)
            {
                return 0;
            }

            if (_plainOffset == _plainCount
                && !await DecryptNextChunkAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }

            int count = Math.Min(buffer.Length, _plainCount - _plainOffset);
            _plainBuffer.AsMemory(_plainOffset, count).CopyTo(buffer);
            _plainOffset += count;
            return count;
        }

        private bool DecryptNextChunk()
        {
            if (!TryBeginChunk(out int count))
            {
                return false;
            }

            try
            {
                if (count > 0)
                {
                    _input.ReadExactly(_cipherBuffer.AsSpan(0, count));
                }

                _input.ReadExactly(_tag);
            }
            catch (EndOfStreamException ex)
            {
                throw new InvalidDataException("The encrypted blob chunk is truncated.", ex);
            }

            return DecryptBufferedChunk(count);
        }

        private async ValueTask<bool> DecryptNextChunkAsync(CancellationToken cancellationToken)
        {
            if (!TryBeginChunk(out int count))
            {
                return false;
            }

            try
            {
                if (count > 0)
                {
                    await _input.ReadExactlyAsync(
                            _cipherBuffer.AsMemory(0, count),
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                await _input.ReadExactlyAsync(_tag, cancellationToken).ConfigureAwait(false);
            }
            catch (EndOfStreamException ex)
            {
                throw new InvalidDataException("The encrypted blob chunk is truncated.", ex);
            }

            return DecryptBufferedChunk(count);
        }

        private bool TryBeginChunk(out int count)
        {
            if (_remaining == 0 && _chunkIndex > 0)
            {
                count = 0;
                return false;
            }

            count = (int)Math.Min(Descriptor.ChunkSize, _remaining);
            return true;
        }

        // The AEAD open, shared by both read paths so the nonce, AAD, and chunk bookkeeping exist in
        // exactly one place.
        private bool DecryptBufferedChunk(int count)
        {
            Span<byte> nonce = stackalloc byte[NonceLength];
            _noncePrefix.AsSpan().CopyTo(nonce);
            BinaryPrimitives.WriteUInt32BigEndian(nonce[NoncePrefixLength..], _chunkIndex);
            WriteChunkAad(
                _aad,
                _header.Length,
                Descriptor.Version,
                _chunkIndex,
                count,
                isFinal: _remaining - count == 0,
                Descriptor.PlaintextLength);
            _aes.Decrypt(
                nonce,
                _cipherBuffer.AsSpan(0, count),
                _tag,
                _plainBuffer.AsSpan(0, count),
                _aad);
            _remaining -= count;
            _chunkIndex++;
            _plainOffset = 0;
            _plainCount = count;
            return count > 0;
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                _aes.Dispose();
                _input.Dispose();
                CryptographicOperations.ZeroMemory(_purposeKey);
                CryptographicOperations.ZeroMemory(_cipherBuffer);
                CryptographicOperations.ZeroMemory(_plainBuffer);
                CryptographicOperations.ZeroMemory(_tag);
                CryptographicOperations.ZeroMemory(_aad);
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                _aes.Dispose();
                await _input.DisposeAsync().ConfigureAwait(false);
                CryptographicOperations.ZeroMemory(_purposeKey);
                CryptographicOperations.ZeroMemory(_cipherBuffer);
                CryptographicOperations.ZeroMemory(_plainBuffer);
                CryptographicOperations.ZeroMemory(_tag);
                CryptographicOperations.ZeroMemory(_aad);
            }

            GC.SuppressFinalize(this);
        }
    }

    private sealed class StreamingEncryptedBlobWriter : EncryptedBlobWriter
    {
        private readonly EncryptedBlobStore _store;

        private readonly FileStream _output;

        private readonly string _tempPath;

        private readonly string _destinationPath;

        private readonly EncryptedBlobPurpose _purpose;

        private readonly byte[] _header;

        private readonly byte[] _purposeKey;

        private readonly AesGcm _aes;

        private readonly byte[] _plainBuffer;

        private readonly byte[] _cipherBuffer;
        private readonly byte[] _tag = new byte[TagLength];
        private readonly byte[] _aad;
        private readonly byte[] _nonce = new byte[NonceLength];
        private int _bufferCount;

        private long _plaintextLength;

        private uint _chunkIndex;

        private bool _completed;

        private bool _disposed;

        public StreamingEncryptedBlobWriter(
            EncryptedBlobStore store,
            FileStream output,
            string tempPath,
            string destinationPath,
            EncryptedBlobPurpose purpose,
            byte[] header,
            byte[] purposeKey)
        {
            _store = store;
            _output = output;
            _tempPath = tempPath;
            _destinationPath = destinationPath;
            _purpose = purpose;
            _header = header;
            _purposeKey = purposeKey;
            _aes = new AesGcm(_purposeKey, TagLength);
            int chunkSize = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(12, 4));
            _plainBuffer = new byte[chunkSize];
            _cipherBuffer = new byte[chunkSize];
            _aad = CreateChunkAad(header);
            header.AsSpan(25, NoncePrefixLength).CopyTo(_nonce);
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => !_disposed && !_completed;

        public override long Length => _plaintextLength;

        public override long Position
        {
            get => _plaintextLength;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        // A genuine synchronous path. BatchProcessingService wraps this writer in a StreamWriter, and
        // StreamWriter's synchronous Flush/Dispose call straight through to here; blocking on the async
        // path would pin a thread-pool thread for the duration of real file I/O.
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureWritable();
            while (!buffer.IsEmpty)
            {
                // A full buffer is only sealed once more content arrives, so the chunk that turns
                // out to be last is always encrypted by CompleteAsync with the final marker — even
                // when the total length is an exact multiple of the chunk size.
                if (_bufferCount == _plainBuffer.Length)
                {
                    EncryptBufferedChunk(isFinal: false);
                }

                int copy = Math.Min(_plainBuffer.Length - _bufferCount, buffer.Length);
                buffer[..copy].CopyTo(_plainBuffer.AsSpan(_bufferCount));
                _bufferCount += copy;
                _plaintextLength = checked(_plaintextLength + copy);
                buffer = buffer[copy..];
            }
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            EnsureWritable();
            while (!buffer.IsEmpty)
            {
                // A full buffer is only sealed once more content arrives, so the chunk that turns
                // out to be last is always encrypted by CompleteAsync with the final marker — even
                // when the total length is an exact multiple of the chunk size.
                if (_bufferCount == _plainBuffer.Length)
                {
                    await EncryptBufferedChunkAsync(isFinal: false, cancellationToken)
                        .ConfigureAwait(false);
                }

                int copy = Math.Min(_plainBuffer.Length - _bufferCount, buffer.Length);
                buffer[..copy].CopyTo(_plainBuffer.AsMemory(_bufferCount));
                _bufferCount += copy;
                _plaintextLength = checked(_plaintextLength + copy);
                buffer = buffer[copy..];
            }
        }

        public override async Task<EncryptedBlobDescriptor> CompleteAsync(
            CancellationToken cancellationToken = default)
        {
            EnsureWritable();
            try
            {
                if (_bufferCount > 0 || _plaintextLength == 0)
                {
                    await EncryptBufferedChunkAsync(isFinal: true, cancellationToken)
                        .ConfigureAwait(false);
                }

                BinaryPrimitives.WriteInt64BigEndian(
                    _header.AsSpan(16, 8),
                    _plaintextLength);
                _output.Position = 0;
                await _output.WriteAsync(_header, cancellationToken).ConfigureAwait(false);
                await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
                _output.Flush(flushToDisk: true);
                await _output.DisposeAsync().ConfigureAwait(false);
                await _store.VerifyEnvelopeAsync(_tempPath, _purpose, cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(_tempPath, _destinationPath, overwrite: true);
                SecureFilePermissions.ApplyOwnerOnlyFile(_destinationPath);
                _completed = true;
                return ParseDescriptor(_header);
            }
            catch
            {
                await AbortAsync().ConfigureAwait(false);
                throw;
            }
        }

        private void EncryptBufferedChunk(bool isFinal)
        {
            int count = SealBufferedChunk(isFinal);
            if (count > 0)
            {
                _output.Write(_cipherBuffer.AsSpan(0, count));
            }

            _output.Write(_tag);
            AdvanceChunk(count);
        }

        private async Task EncryptBufferedChunkAsync(bool isFinal, CancellationToken cancellationToken)
        {
            int count = SealBufferedChunk(isFinal);
            if (count > 0)
            {
                await _output.WriteAsync(
                        _cipherBuffer.AsMemory(0, count),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await _output.WriteAsync(_tag, cancellationToken).ConfigureAwait(false);
            AdvanceChunk(count);
        }

        // The AEAD seal, shared by both write paths so the nonce and AAD exist in exactly one place.
        private int SealBufferedChunk(bool isFinal)
        {
            int count = _bufferCount;
            BinaryPrimitives.WriteUInt32BigEndian(
                _nonce.AsSpan(NoncePrefixLength),
                _chunkIndex);
            WriteChunkAad(
                _aad,
                _header.Length,
                _header[8],
                _chunkIndex,
                count,
                isFinal,
                _plaintextLength);
            _aes.Encrypt(
                _nonce,
                _plainBuffer.AsSpan(0, count),
                _cipherBuffer.AsSpan(0, count),
                _tag,
                _aad);
            return count;
        }

        private void AdvanceChunk(int count)
        {
            CryptographicOperations.ZeroMemory(_plainBuffer.AsSpan(0, count));
            CryptographicOperations.ZeroMemory(_cipherBuffer.AsSpan(0, count));
            _bufferCount = 0;
            _chunkIndex++;
        }

        private void EnsureWritable()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_completed)
            {
                throw new InvalidOperationException(
                    "The encrypted blob writer has already been completed.");
            }
        }

        /// <summary>
        /// Flushes the underlying file handle. Plaintext still sitting in the chunk buffer is not
        /// sealed by a flush.
        /// </summary>
        /// <remarks>
        /// It cannot be: the reader frames every non-final chunk as exactly <c>ChunkSize</c> bytes, so
        /// sealing a partial chunk mid-stream would produce an envelope that cannot be read back.
        /// Buffered plaintext reaches disk when the buffer fills or when <c>CompleteAsync</c> writes the
        /// final chunk, and <c>CompleteAsync</c> is the only way to obtain a descriptor at all — an
        /// abandoned writer's temp file is deleted rather than published. Throwing here instead would
        /// break <c>StreamWriter</c>, which flushes its own char buffer through to this stream.
        /// </remarks>
        public override void Flush() => _output.Flush();

        /// <inheritdoc cref="Flush"/>
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _output.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        private async ValueTask AbortAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _aes.Dispose();
            await _output.DisposeAsync().ConfigureAwait(false);
            ScrubAndDiscardTemp();
        }

        private void Abort()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _aes.Dispose();
            _output.Dispose();
            ScrubAndDiscardTemp();
        }

        private void ScrubAndDiscardTemp()
        {
            CryptographicOperations.ZeroMemory(_purposeKey);
            CryptographicOperations.ZeroMemory(_plainBuffer);
            CryptographicOperations.ZeroMemory(_cipherBuffer);
            CryptographicOperations.ZeroMemory(_tag);
            CryptographicOperations.ZeroMemory(_aad);
            CryptographicOperations.ZeroMemory(_nonce);
            if (!File.Exists(_tempPath))
            {
                return;
            }

            try
            {
                File.Delete(_tempPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Abort();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await AbortAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }
}
