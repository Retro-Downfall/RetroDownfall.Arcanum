using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// Enumerates names from an already-open directory capability. Traversal never resolves the
/// directory again by path, so swapping a checked directory for a link cannot redirect the walk.
/// </summary>
internal static partial class SecureDirectoryNameEnumerator
{
    private const int FileIdBothDirectoryInformation = 37;
    private const int StatusNoMoreFiles = unchecked((int)0x80000006);
    private const int StatusNoSuchFile = unchecked((int)0xC000000F);
    private const int WindowsBufferBytes = 64 * 1024;

    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    internal static bool TryEnumerate(
        SafeFileHandle directory,
        CancellationToken cancellationToken,
        out string[] names)
    {
        names = [];
        if (directory is null || directory.IsInvalid || directory.IsClosed)
        {
            return false;
        }

        List<string>? found = OperatingSystem.IsWindows()
            ? EnumerateWindows(directory, cancellationToken)
            : OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()
                ? EnumerateUnix(directory, cancellationToken)
                : null;

        if (found is null)
        {
            return false;
        }

        names = [.. found.Order(StringComparer.Ordinal)];

        return true;
    }

    /// <summary>
    /// Where a <c>readdir</c> entry keeps its name, and on macOS its name length, for the given platform.
    /// </summary>
    /// <param name="nameLengthOffset">The offset of the 16-bit name length, or -1 when the name is read up to its terminator.</param>
    internal static bool TryGetUnixDirentLayout(
        bool isMacOS,
        Architecture architecture,
        out int nameOffset,
        out int nameLengthOffset)
    {
        if (isMacOS)
        {
            nameOffset = 21;
            nameLengthOffset = 18;

            // Only arm64 is read. The plain readdir symbol on macOS x64 returns the legacy 32-bit-inode
            // dirent, whose length and name sit at different offsets than the 64-bit-inode layout above;
            // x64 is not a shipping RID, so it fails closed as FileHandleIdentity's stat reader does.
            return architecture is Architecture.Arm64;
        }

        nameOffset = 19;
        nameLengthOffset = -1;

        return architecture is Architecture.X64 or Architecture.Arm64;
    }

    private static List<string>? EnumerateUnix(
        SafeFileHandle directory,
        CancellationToken cancellationToken)
    {
        if (!TryGetUnixDirentLayout(
                OperatingSystem.IsMacOS(),
                RuntimeInformation.ProcessArchitecture,
                out int nameOffset,
                out int nameLengthOffset))
        {
            return null;
        }

        bool referenceAdded = false;
        int duplicate = -1;

        try
        {
            directory.DangerousAddRef(ref referenceAdded);
            duplicate = DuplicateUnix(directory.DangerousGetHandle().ToInt32());
            if (duplicate < 0)
            {
                return null;
            }

            IntPtr stream = FdOpenDirectoryUnix(duplicate);
            if (stream == IntPtr.Zero)
            {
                _ = CloseUnix(duplicate);
                duplicate = -1;

                return null;
            }

            duplicate = -1;

            try
            {
                // dup shares the open file description, hence the directory offset, with the caller's
                // handle. Without a rewind a second enumeration of the same capability starts where the
                // last one ended and silently reports a populated directory as empty.
                RewindDirectoryUnix(stream);

                List<string> names = [];
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Marshal.SetLastPInvokeError(0);
                    IntPtr entry = ReadDirectoryUnix(stream);
                    if (entry == IntPtr.Zero)
                    {
                        return Marshal.GetLastPInvokeError() == 0 ? names : null;
                    }

                    int length = nameLengthOffset >= 0
                        ? Marshal.ReadInt16(entry, nameLengthOffset)
                        : NullTerminatedNameLength(entry, nameOffset);
                    if (length is <= 0 or > 255)
                    {
                        return null;
                    }

                    byte[] bytes = new byte[length];
                    Marshal.Copy(IntPtr.Add(entry, nameOffset), bytes, 0, length);

                    string name;
                    try
                    {
                        name = StrictUtf8.GetString(bytes);
                    }
                    catch (DecoderFallbackException)
                    {
                        return null;
                    }

                    if (name is not "." and not "..")
                    {
                        names.Add(name);
                    }
                }
            }
            finally
            {
                _ = CloseDirectoryUnix(stream);
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or ObjectDisposedException
                or NotSupportedException)
        {
            return null;
        }
        finally
        {
            if (duplicate >= 0)
            {
                _ = CloseUnix(duplicate);
            }

            if (referenceAdded)
            {
                directory.DangerousRelease();
            }
        }
    }

    private static int NullTerminatedNameLength(IntPtr entry, int nameOffset)
    {
        int length = 0;
        while (length <= 255 && Marshal.ReadByte(entry, nameOffset + length) != 0)
        {
            length++;
        }

        return length;
    }

    private static unsafe List<string>? EnumerateWindows(
        SafeFileHandle directory,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[WindowsBufferBytes];
        List<string> names = [];
        bool restart = true;

        fixed (byte* pointer = buffer)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool initialQuery = restart;
                int status = NtQueryDirectoryFile(
                    directory.DangerousGetHandle(),
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    out IoStatusBlock io,
                    new IntPtr(pointer),
                    WindowsBufferBytes,
                    FileIdBothDirectoryInformation,
                    returnSingleEntry: false,
                    IntPtr.Zero,
                    restart);
                restart = false;

                if (IsWindowsDirectoryEnumerationComplete(
                        status,
                        initialQuery,
                        names.Count))
                {
                    return names;
                }

                if (status < 0 || io.Information.ToInt64() <= 0)
                {
                    return null;
                }

                int offset = 0;
                int available = checked((int)io.Information.ToInt64());
                while (offset + 104 <= available)
                {
                    ReadOnlySpan<byte> entry = buffer.AsSpan(offset, available - offset);
                    uint next = BinaryPrimitives.ReadUInt32LittleEndian(entry);
                    uint nameBytes = BinaryPrimitives.ReadUInt32LittleEndian(entry[60..]);
                    if ((nameBytes & 1) != 0
                        || nameBytes > 510
                        || 104 + nameBytes > entry.Length)
                    {
                        return null;
                    }

                    string name = Encoding.Unicode.GetString(
                        entry.Slice(104, checked((int)nameBytes)));
                    if (name is not "." and not "..")
                    {
                        names.Add(name);
                    }

                    if (next == 0)
                    {
                        break;
                    }

                    if (next > int.MaxValue || offset + next > available)
                    {
                        return null;
                    }

                    offset += checked((int)next);
                }
            }
        }
    }

    internal static bool IsWindowsDirectoryEnumerationComplete(
        int status,
        bool initialQuery,
        int observedNameCount) =>
        status == StatusNoMoreFiles
        || status == StatusNoSuchFile
            && initialQuery
            && observedNameCount == 0;

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct IoStatusBlock
    {
        internal readonly IntPtr Status;

        internal readonly IntPtr Information;
    }

    [LibraryImport("libc", EntryPoint = "dup", SetLastError = true)]
    private static partial int DuplicateUnix(int descriptor);

    [LibraryImport("libc", EntryPoint = "fdopendir", SetLastError = true)]
    private static partial IntPtr FdOpenDirectoryUnix(int descriptor);

    [LibraryImport("libc", EntryPoint = "readdir", SetLastError = true)]
    private static partial IntPtr ReadDirectoryUnix(IntPtr directory);

    [LibraryImport("libc", EntryPoint = "rewinddir")]
    private static partial void RewindDirectoryUnix(IntPtr directory);

    [LibraryImport("libc", EntryPoint = "closedir", SetLastError = true)]
    private static partial int CloseDirectoryUnix(IntPtr directory);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int CloseUnix(int descriptor);

    [LibraryImport("ntdll.dll", EntryPoint = "NtQueryDirectoryFile")]
    private static partial int NtQueryDirectoryFile(
        IntPtr fileHandle,
        IntPtr eventHandle,
        IntPtr apcRoutine,
        IntPtr apcContext,
        out IoStatusBlock ioStatusBlock,
        IntPtr fileInformation,
        int length,
        int fileInformationClass,
        [MarshalAs(UnmanagedType.U1)] bool returnSingleEntry,
        IntPtr fileName,
        [MarshalAs(UnmanagedType.U1)] bool restartScan);
}
