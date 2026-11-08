using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

internal static partial class FileHandleIdentityInterop
{
    internal static bool TryOpenDirectoryForCreation(string path, out SafeFileHandle handle, out FileHandleMetadata metadata)
    {
        if (!OperatingSystem.IsWindows())
        {
            return TryOpenDirectoryMetadata(path, out handle, out metadata);
        }

        handle = CreateFile(path, FileReadAttributes | FileTraverse | 0x0004U, FileShare.Read | FileShare.Write | FileShare.Delete,
            IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);

        metadata = default;

        if (!handle.IsInvalid && TryGetHandleMetadata(handle, out metadata) && metadata.Kind == FileSystemObjectKind.Directory)
        {
            return true;
        }

        handle.Dispose();

        return false;
    }

    internal static bool TryCreateDirectoryExclusiveRelative(SafeFileHandle parent, string leaf, uint mode, bool caseSensitive,
        out SafeFileHandle? created)
    {
        created = null;

        if (mode is not (0x1FF or 0x1C0) || string.IsNullOrEmpty(leaf) || leaf is "." or ".." || leaf.IndexOfAny(['/', '\\', ':', '\0']) >= 0)
        {
            return false;
        }

        bool retained = false;

        try
        {
            parent.DangerousAddRef(ref retained);

            if (OperatingSystem.IsWindows())
            {
                return TryCreateWindowsDirectoryExclusive(parent, leaf, mode == 0x1C0, caseSensitive, out created);
            }

            if (!OperatingSystem.IsMacOS() || MkdirDirectoryAt(parent.DangerousGetHandle().ToInt32(), leaf, mode) != 0)
            {
                return false;
            }

            if (!TryOpenDirectoryMetadataRelative(parent, leaf, false, out SafeFileHandle opened, out _))
            {
                opened.Dispose();

                return false;
            }

            created = opened;

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException or ArgumentException)
        {
            created?.Dispose();

            created = null;

            return false;
        }
        finally
        {
            if (retained)
            {
                parent.DangerousRelease();
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static unsafe bool TryCreateWindowsDirectoryExclusive(SafeFileHandle parent, string leaf, bool ownerOnly, bool sensitive,
        out SafeFileHandle? created)
    {
        created = null;

        byte[]? descriptor = null;

        if (ownerOnly)
        {
            using WindowsIdentity current = WindowsIdentity.GetCurrent();

            if (current.User is not { } user)
            {
                return false;
            }

            DirectorySecurity security = new();

            security.SetOwner(user);

            security.SetAccessRuleProtection(true, false);

            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));

            descriptor = security.GetSecurityDescriptorBinaryForm();
        }

        fixed (char* characters = leaf)
        fixed (byte* security = descriptor)
        {
            UNICODE_STRING name = new() { Length = checked((ushort)(leaf.Length * 2)), MaximumLength = checked((ushort)(leaf.Length * 2)), Buffer = characters };

            OBJECT_ATTRIBUTES attributes = new()
            {
                Length = (uint)sizeof(OBJECT_ATTRIBUTES), RootDirectory = parent.DangerousGetHandle(), ObjectName = &name,
                Attributes = sensitive ? 0 : ObjectAttributeCaseInsensitive, SecurityDescriptor = (IntPtr)security,
            };

            IO_STATUS_BLOCK statusBlock = default;

            int status = CreateDirectoryNt(out IntPtr raw, FileReadAttributes | FileTraverse | Synchronize, &attributes, &statusBlock,
                IntPtr.Zero, 0x10, 7, 2, FileDirectoryFile | FileOpenReparsePoint | FileSynchronousIoNonAlert, IntPtr.Zero, 0);

            if (raw == IntPtr.Zero || raw == new IntPtr(-1))
            {
                return false;
            }

            var handle = new SafeFileHandle(raw, ownsHandle: true);

            if (status < 0 || statusBlock.Information != 2 || !TryGetHandleMetadata(handle, out FileHandleMetadata metadata)
                || metadata.Kind != FileSystemObjectKind.Directory)
            {
                handle.Dispose();

                return false;
            }

            created = handle;

            return true;
        }
    }

    [LibraryImport("libc", EntryPoint = "mkdirat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MkdirDirectoryAt(int parent, string leaf, uint mode);

    [LibraryImport("ntdll.dll", EntryPoint = "NtCreateFile")]
    private static unsafe partial int CreateDirectoryNt(out IntPtr handle, uint access, OBJECT_ATTRIBUTES* attributes,
        IO_STATUS_BLOCK* status, IntPtr allocationSize, uint fileAttributes, uint share, uint disposition, uint options, IntPtr ea, uint eaLength);
}
