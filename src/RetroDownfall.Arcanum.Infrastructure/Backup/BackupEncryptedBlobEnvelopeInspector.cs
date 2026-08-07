using System.Buffers.Binary;

using System.Security.Cryptography;

using System.Text;

using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

internal static class BackupEncryptedBlobEnvelopeInspector
{

    private const int MagicLength = 8;

    private const int FixedHeaderLength = 37;

    private const int TagLength = 16;

    private const int MaximumMetadataLength = 16 * 1024;

    private const int MaximumKeyIdLength = 64;

    private const int MinimumChunkSize = 16;

    private const int MaximumChunkSize = 16 * 1024 * 1024;

    private static ReadOnlySpan<byte> Magic => "ARCABLOB"u8;

    public static async ValueTask<EncryptedBlobDescriptor?> TryInspectAsync(
        Stream input,
        EncryptedBlobPurpose expectedPurpose,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(input);

        ValidatePurpose(expectedPurpose);

        if (!input.CanRead || !input.CanSeek)
        {

            throw new ArgumentException(
                "Encrypted blob envelope inspection requires a readable, seekable stream.",
                nameof(input));

        }

        long originalPosition = input.Position;

        byte[] magic = new byte[MagicLength];

        try
        {

            int magicLength = await ReadUpToAsync(
                    input,
                    magic,
                    cancellationToken)
                .ConfigureAwait(false);

            if (magicLength != MagicLength
                || !CryptographicOperations.FixedTimeEquals(magic, Magic))
            {

                return null;

            }

            input.Position = originalPosition;

            byte[] fixedHeader = new byte[FixedHeaderLength];

            await ReadHeaderPartAsync(
                    input,
                    fixedHeader,
                    cancellationToken)
                .ConfigureAwait(false);

            ValidateFixedHeader(
                fixedHeader,
                expectedPurpose,
                out int keyIdLength,
                out int metadataLength);

            byte[] variableHeader = new byte[keyIdLength + metadataLength];

            await ReadHeaderPartAsync(
                    input,
                    variableHeader,
                    cancellationToken)
                .ConfigureAwait(false);

            ReadOnlySpan<byte> keyIdBytes = variableHeader.AsSpan(0, keyIdLength);

            if (keyIdBytes.ContainsAnyExceptInRange((byte)0x21, (byte)0x7e))
            {

                throw new InvalidDataException(
                    "The encrypted blob key id is not printable ASCII.");

            }

            EncryptedBlobDescriptor descriptor = new(
                fixedHeader[8],
                (EncryptedBlobAlgorithm)fixedHeader[9],
                BinaryPrimitives.ReadInt32BigEndian(fixedHeader.AsSpan(12, 4)),
                Encoding.ASCII.GetString(keyIdBytes),
                BinaryPrimitives.ReadInt64BigEndian(fixedHeader.AsSpan(16, 8)),
                fixedHeader.Length + variableHeader.Length,
                (EncryptedBlobPurpose)fixedHeader[10],
                variableHeader.AsMemory(keyIdLength, metadataLength).ToArray());

            ValidateEnvelopeLength(
                input.Length - originalPosition,
                descriptor);

            return descriptor;

        }
        finally
        {

            input.Position = originalPosition;

            CryptographicOperations.ZeroMemory(magic);

        }

    }

    private static async ValueTask<int> ReadUpToAsync(
        Stream input,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {

        int total = 0;

        while (total < destination.Length)
        {

            int read = await input
                .ReadAsync(destination[total..], cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {

                break;

            }

            total += read;

        }

        return total;

    }

    private static async ValueTask ReadHeaderPartAsync(
        Stream input,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {

        try
        {

            await input
                .ReadExactlyAsync(destination, cancellationToken)
                .ConfigureAwait(false);

        }
        catch (EndOfStreamException exception)
        {

            throw new InvalidDataException(
                "The encrypted blob header is truncated.",
                exception);

        }

    }

    private static void ValidateFixedHeader(
        ReadOnlySpan<byte> header,
        EncryptedBlobPurpose expectedPurpose,
        out int keyIdLength,
        out int metadataLength)
    {

        if (!CryptographicOperations.FixedTimeEquals(
                header[..MagicLength],
                Magic))
        {

            throw new InvalidDataException(
                "The encrypted blob magic changed while its header was inspected.");

        }

        if (!EncryptedBlobFormat.IsSupportedVersion(header[8]))
        {

            throw new InvalidDataException(
                $"Unsupported encrypted blob version {header[8]}.");

        }

        if (header[9] != (byte)EncryptedBlobAlgorithm.Aes256Gcm)
        {

            throw new InvalidDataException(
                $"Unsupported encrypted blob algorithm {header[9]}.");

        }

        EncryptedBlobPurpose actualPurpose = (EncryptedBlobPurpose)header[10];

        if (actualPurpose != expectedPurpose)
        {

            throw new CryptographicException(
                "The encrypted blob purpose does not match its owning database record.");

        }

        int chunkSize = BinaryPrimitives.ReadInt32BigEndian(header.Slice(12, 4));

        long plaintextLength = BinaryPrimitives.ReadInt64BigEndian(header.Slice(16, 8));

        keyIdLength = header[24];

        metadataLength = BinaryPrimitives.ReadInt32BigEndian(header.Slice(33, 4));

        if (chunkSize is < MinimumChunkSize or > MaximumChunkSize
            || plaintextLength < 0
            || keyIdLength is 0 or > MaximumKeyIdLength
            || metadataLength is < 0 or > MaximumMetadataLength)
        {

            throw new InvalidDataException(
                "The encrypted blob header contains invalid bounds.");

        }

    }

    private static void ValidateEnvelopeLength(
        long actualLength,
        EncryptedBlobDescriptor descriptor)
    {

        try
        {

            long chunks = Math.Max(
                1,
                checked(
                    (descriptor.PlaintextLength + descriptor.ChunkSize - 1)
                    / descriptor.ChunkSize));

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
        catch (OverflowException exception)
        {

            throw new InvalidDataException(
                "The encrypted blob length metadata overflows supported bounds.",
                exception);

        }

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

}
