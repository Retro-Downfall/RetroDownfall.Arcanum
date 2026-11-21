using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

internal static partial class FileHandleIdentityInterop
{
    /// <summary>Reads the native byte position on a previously validated regular file handle.</summary>
    internal static ValueTask<int> ReadAtOffsetAsync(SafeFileHandle file, Memory<byte> destination, long offset,
        CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            return RandomAccess.ReadAsync(file, destination, offset, cancellationToken);
        }

        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException();
        }

        // A SafeFileHandle constructed from an openat descriptor may select sequential I/O
        // in the runtime's Unix RandomAccess path. pread leaves the descriptor position alone.
        return ValueTask.FromResult(ReadAtOffsetUnix(file, destination.Span, offset, cancellationToken));
    }

    private static unsafe int ReadAtOffsetUnix(SafeFileHandle file, Span<byte> destination, long offset,
        CancellationToken cancellationToken)
    {
        const int interrupted = 4;

        bool retained = false;

        try
        {
            file.DangerousAddRef(ref retained);

            int descriptor = file.DangerousGetHandle().ToInt32();

            fixed (byte* buffer = destination)
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    nint read = ReadUnixAtPosition(descriptor, buffer, (nuint)destination.Length, offset);

                    if (read >= 0)
                    {
                        return (int)read;
                    }

                    if (Marshal.GetLastPInvokeError() != interrupted)
                    {
                        throw new IOException("The validated file could not be read at its requested position.");
                    }
                }
            }
        }
        finally
        {
            if (retained)
            {
                file.DangerousRelease();
            }
        }
    }

    [LibraryImport("libc", EntryPoint = "pread", SetLastError = true)]
    private static unsafe partial nint ReadUnixAtPosition(int descriptor, byte* buffer, nuint count, long offset);
}
