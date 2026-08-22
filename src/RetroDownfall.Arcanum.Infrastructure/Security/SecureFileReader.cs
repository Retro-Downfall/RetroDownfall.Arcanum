using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;

using Microsoft.Win32.SafeHandles;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

internal enum SecureFileOpenStatus
{
    Success,
    NotFound,
    Rejected,
    AccessDenied,
    IoError,
}

internal enum SecureFileReadStatus
{
    Success,
    NotFound,
    Rejected,
    AccessDenied,
    IoError,
    TooLarge,
    InvalidUtf8,
}

internal sealed class SecureFileReadResult : IDisposable
{
    private byte[]? _buffer;

    private readonly int _length;

    internal SecureFileReadResult(
        SecureFileReadStatus status,
        byte[]? buffer,
        int length,
        FileHandleMetadata metadata)
    {
        Status = status;

        _buffer = buffer;

        _length = length;

        Metadata = metadata;
    }

    internal SecureFileReadStatus Status { get; }

    internal FileHandleMetadata Metadata { get; }

    internal ReadOnlyMemory<byte> Bytes =>
        _buffer is null
            ? ReadOnlyMemory<byte>.Empty
            : _buffer.AsMemory(0, _length);

    internal int BufferCapacity => _buffer?.Length ?? 0;

    public void Dispose()
    {
        byte[]? buffer = Interlocked.Exchange(ref _buffer, null);

        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}

internal readonly record struct SecureUtf8FileReadResult(
    SecureFileReadStatus Status,
    string? Text,
    int ByteLength,
    FileHandleMetadata Metadata);

/// <summary>
/// Opens a path once with link-following disabled, proves the opened handle is an
/// unaliased regular file, and performs bounded reads from that same handle.
/// </summary>
internal static partial class SecureFileReader
{
    private const int InitialBufferBytes = 4096;

    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Deterministic test seam invoked after the validated handle is open and before
    /// the first bounded read.
    /// </summary>
    internal static Action<string>? AfterOpenForTests { get; set; }

    internal static SecureFileOpenStatus TryOpenRegularFile(
        string path,
        FileHandleIdentity? expectedIdentity,
        [NotNullWhen(true)] out FileStream? stream,
        out FileHandleMetadata metadata)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        stream = null;

        metadata = default;

        SafeFileHandle? handle = null;

        try
        {
            SecureFileOpenStatus openStatus =
                FileHandleIdentityInterop.TryOpenReadOnlyNoFollow(
                path,
                out handle);

            if (openStatus is not SecureFileOpenStatus.Success)
            {
                return openStatus;
            }

            SafeFileHandle openedHandle = handle!;

            if (!FileHandleIdentityInterop.TryGetHandleMetadata(
                    openedHandle,
                    out metadata))
            {
                return SecureFileOpenStatus.Rejected;
            }

            if (metadata.Kind != FileSystemObjectKind.RegularFile)
            {
                return SecureFileOpenStatus.Rejected;
            }

            if (metadata.HardLinkCount != 1)
            {
                return SecureFileOpenStatus.Rejected;
            }

            if (expectedIdentity is FileHandleIdentity expected
                && !FileHandleIdentity.IdentitiesMatch(
                    expected,
                    metadata.Identity))
            {
                return SecureFileOpenStatus.Rejected;
            }

            try
            {
                stream = new SecureRegularFileStream(
                    openedHandle,
                    path,
                    isAsync: OperatingSystem.IsWindows());

                handle = null;

                return SecureFileOpenStatus.Success;
            }
            catch (FileNotFoundException)
            {
                return SecureFileOpenStatus.NotFound;
            }
            catch (DirectoryNotFoundException)
            {
                return SecureFileOpenStatus.NotFound;
            }
            catch (UnauthorizedAccessException)
            {
                return SecureFileOpenStatus.AccessDenied;
            }
            catch (IOException)
            {
                return SecureFileOpenStatus.IoError;
            }
            catch (Exception exception) when (
                exception is ArgumentException
                    or NotSupportedException)
            {
                return SecureFileOpenStatus.Rejected;
            }
        }
        finally
        {
            handle?.Dispose();
        }
    }

    internal static bool TryValidateRegularFileHandle(
        SafeFileHandle handle,
        FileHandleIdentity? expectedIdentity,
        out FileHandleMetadata metadata)
    {
        metadata = default;

        if (!FileHandleIdentityInterop.TryGetHandleMetadata(
                handle,
                out FileHandleMetadata opened)
            || opened.Kind != FileSystemObjectKind.RegularFile
            || opened.HardLinkCount != 1
            || (expectedIdentity is FileHandleIdentity expected
                && !FileHandleIdentity.IdentitiesMatch(
                    expected,
                    opened.Identity)))
        {
            return false;
        }

        metadata = opened;

        return true;
    }

    internal static (int DescriptorFlags, int StatusFlags)
        GetUnixHandleFlagsForTests(
            SafeFileHandle handle)
    {
        int descriptor = handle.DangerousGetHandle().ToInt32();

        return (
            Fcntl(descriptor, FGetDescriptor),
            Fcntl(descriptor, FGetStatus));
    }

    internal static async Task<SecureFileReadResult> ReadBytesAsync(
        string path,
        int maxBytes,
        CancellationToken cancellationToken,
        FileHandleIdentity? expectedIdentity = null,
        bool requireOwnerControlled = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (maxBytes < 0 || maxBytes == int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }

        cancellationToken.ThrowIfCancellationRequested();

        SecureFileOpenStatus openStatus = TryOpenRegularFile(
            path,
            expectedIdentity,
            out FileStream? stream,
            out _);

        if (openStatus is not SecureFileOpenStatus.Success)
        {
            return Failure(MapOpenStatus(openStatus));
        }

        await using (FileStream openedStream = stream!)
        {
            return await ReadBytesAsync(
                    openedStream,
                    maxBytes,
                    cancellationToken,
                    requireOwnerControlled)
                .ConfigureAwait(false);
        }
    }

    internal static async Task<SecureFileReadResult> ReadBytesAsync(
        FileStream stream,
        int maxBytes,
        CancellationToken cancellationToken,
        bool requireOwnerControlled = false)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (maxBytes < 0 || maxBytes == int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!TryValidateRegularFileHandle(
                stream.SafeFileHandle,
                expectedIdentity: null,
                out FileHandleMetadata openedMetadata)
            || requireOwnerControlled
                && !SecureFilePermissions.HasOwnerControlledFileHandlePosture(
                    stream.SafeFileHandle,
                    stream.Name,
                    openedMetadata.Identity))
        {
            return Failure(SecureFileReadStatus.Rejected);
        }

        AfterOpenForTests?.Invoke(stream.Name);

        int readLimit = maxBytes + 1;

        byte[]? buffer = ArrayPool<byte>.Shared.Rent(
            Math.Min(readLimit, InitialBufferBytes));

        try
        {
            int length = 0;

            while (length < readLimit)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (length == buffer.Length)
                {
                    int doubledLength = buffer.Length <= readLimit / 2
                        ? buffer.Length * 2
                        : readLimit;

                    int nextLength = Math.Min(
                        readLimit,
                        doubledLength);

                    byte[] grown =
                        ArrayPool<byte>.Shared.Rent(nextLength);

                    buffer.AsSpan(0, length).CopyTo(grown);

                    ArrayPool<byte>.Shared.Return(
                        buffer,
                        clearArray: true);

                    buffer = grown;
                }

                int read = await stream
                    .ReadAsync(
                        buffer.AsMemory(
                            length,
                            Math.Min(
                                buffer.Length - length,
                                readLimit - length)),
                        cancellationToken)
                    .ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();

                if (read == 0)
                {
                    break;
                }

                length += read;
            }

            if (!TryValidateRegularFileHandle(
                    stream.SafeFileHandle,
                    openedMetadata.Identity,
                    out FileHandleMetadata finalMetadata)
                || requireOwnerControlled
                    && !SecureFilePermissions.HasOwnerControlledFileHandlePosture(
                        stream.SafeFileHandle,
                        stream.Name,
                        finalMetadata.Identity))
            {
                return Failure(SecureFileReadStatus.Rejected);
            }

            if (length > maxBytes)
            {
                return Failure(SecureFileReadStatus.TooLarge);
            }

            SecureFileReadResult result = new(
                SecureFileReadStatus.Success,
                buffer,
                length,
                finalMetadata);

            buffer = null;

            return result;
        }
        catch (UnauthorizedAccessException)
        {
            return Failure(SecureFileReadStatus.AccessDenied);
        }
        catch (IOException)
        {
            return Failure(SecureFileReadStatus.IoError);
        }
        finally
        {
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(
                    buffer,
                    clearArray: true);
            }
        }
    }

    internal static async Task<SecureUtf8FileReadResult> ReadUtf8TextAsync(
        string path,
        int maxBytes,
        CancellationToken cancellationToken,
        FileHandleIdentity? expectedIdentity = null)
    {
        using SecureFileReadResult bytes = await ReadBytesAsync(
                path,
                maxBytes,
                cancellationToken,
                expectedIdentity)
            .ConfigureAwait(false);

        return DecodeUtf8(bytes);
    }

    internal static async Task<SecureUtf8FileReadResult> ReadUtf8TextAsync(
        FileStream stream,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        using SecureFileReadResult bytes = await ReadBytesAsync(
                stream,
                maxBytes,
                cancellationToken)
            .ConfigureAwait(false);

        return DecodeUtf8(bytes);
    }

    private static SecureUtf8FileReadResult DecodeUtf8(
        SecureFileReadResult bytes)
    {
        if (bytes.Status is not SecureFileReadStatus.Success)
        {
            return new SecureUtf8FileReadResult(
                bytes.Status,
                null,
                0,
                bytes.Metadata);
        }

        try
        {
            ReadOnlySpan<byte> textBytes = bytes.Bytes.Span;

            ReadOnlySpan<byte> preamble = Encoding.UTF8.Preamble;

            if (textBytes.StartsWith(preamble))
            {
                textBytes = textBytes[preamble.Length..];
            }

            return new SecureUtf8FileReadResult(
                SecureFileReadStatus.Success,
                StrictUtf8.GetString(textBytes),
                bytes.Bytes.Length,
                bytes.Metadata);
        }
        catch (DecoderFallbackException)
        {
            return new SecureUtf8FileReadResult(
                SecureFileReadStatus.InvalidUtf8,
                null,
                0,
                bytes.Metadata);
        }
    }

    private static SecureFileReadResult Failure(
        SecureFileReadStatus status) =>
        new(status, null, 0, default);

    private static SecureFileReadStatus MapOpenStatus(
        SecureFileOpenStatus status) =>
        (SecureFileReadStatus)status;

    private const int FGetDescriptor = 1;

    private const int FGetStatus = 3;

    [LibraryImport(
        "libc",
        EntryPoint = "fcntl",
        SetLastError = true)]
    private static partial int Fcntl(
        int descriptor,
        int command);

    private sealed class SecureRegularFileStream(
        SafeFileHandle handle,
        string path,
        bool isAsync)
        : FileStream(
            handle,
            FileAccess.Read,
            bufferSize: InitialBufferBytes,
            isAsync)
    {
        public override string Name { get; } = Path.GetFullPath(path);
    }
}
