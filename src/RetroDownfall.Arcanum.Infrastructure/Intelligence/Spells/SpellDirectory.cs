using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;

/// <summary>
/// An admitted spell directory. Child creation, rename and cleanup remain relative to retained
/// handles, so replacing a pathname ancestor cannot redirect an effect to a different directory.
/// </summary>
internal sealed partial class SpellDirectory : IDisposable
{
    private static readonly AsyncLocal<Action<string>?> BeforeRenameOverride = new();

    internal static Action<string>? BeforeRenameForTests
    {
        get => BeforeRenameOverride.Value;

        set => BeforeRenameOverride.Value = value;
    }

    private readonly SafeFileHandle _handle;

    private readonly SpellDirectory? _parent;

    private readonly bool _ownsParent;

    private readonly FileHandleIdentity _identity;

    private readonly Dictionary<string, FileHandleIdentity> _writtenFiles = new(StringComparer.Ordinal);

    private bool _published;

    private bool _cleanup;

    private SpellDirectory(string path, SafeFileHandle handle, SpellDirectory? parent, bool ownsParent)
    {
        Path = path;

        _handle = handle;

        _parent = parent;

        _ownsParent = ownsParent;

        if (!FileHandleIdentityInterop.TryGetHandleMetadata(handle, out FileHandleMetadata metadata)
            || metadata.Kind != FileSystemObjectKind.Directory)
        {
            handle.Dispose();

            throw new IOException("The spell directory identity could not be established.");
        }

        _identity = metadata.Identity;
    }

    internal string Path { get; }

    private SpellDirectory Root => _parent?.Root ?? this;

    internal static SpellDirectory Open(string workspaceRoot, string directoryPath, bool create = false)
    {
        string root = System.IO.Path.GetFullPath(workspaceRoot);

        string destination = System.IO.Path.GetFullPath(directoryPath);

        if (!WorkspacePathPolicy.IsPathUnderWorkspace(root, destination)
            || !WorkspacePathPolicy.TryCanonicalize(root, out string? canonical, out bool exists)
            || !exists)
        {
            throw new IOException("The spell directory is outside its admitted workspace.");
        }

        SafeFileHandle rootHandle = OpenRoot(canonical);

        SpellDirectory current = new(root, rootHandle, null, false);

        try
        {
            string relative = System.IO.Path.GetRelativePath(root, destination);

            if (relative != ".")
            {
                foreach (string component in relative.Split(System.IO.Path.DirectorySeparatorChar))
                {
                    current = current.OpenChild(component, create, ownsParent: true);
                }
            }

            current.Validate();

            return current;
        }
        catch
        {
            current.Dispose();

            throw;
        }
    }

    internal SpellDirectory CreateStaging()
    {
        SpellDirectory child = OpenChild($".staging-{Guid.NewGuid():N}", create: true, ownsParent: false, exclusive: true);

        child._cleanup = true;

        return child;
    }

    internal SpellDirectory CreateChild(string leaf) => OpenChild(leaf, create: true, ownsParent: false);

    private SpellDirectory OpenChild(string leaf, bool create, bool ownsParent, bool exclusive = false)
    {
        ValidateLeaf(leaf);

        Validate();

        SafeFileHandle handle;

        if (OperatingSystem.IsWindows())
        {
            handle = OpenWindows(_handle, leaf, directory: true, create ? (exclusive ? 2U : 3U) : 1U);
        }
        else
        {
            if (create && MkdirAt(Descriptor(_handle), leaf, 0x1C0) != 0
                && (exclusive || Marshal.GetLastPInvokeError() != 17))
            {
                throw new IOException("The spell directory could not be created.");
            }

            if (!FileHandleIdentityInterop.TryOpenDirectoryMetadataRelative(_handle, leaf, requestReadControl: false, requestEnumeration: true, out handle, out _))
            {
                throw new IOException("The spell directory could not be opened without following links.");
            }
        }

        SpellDirectory child = new(System.IO.Path.Combine(Path, leaf), handle, this, ownsParent);

        try
        {
            child.Validate();

            return child;
        }
        catch
        {
            // The caller still owns its parent if this child was never returned.
            handle.Dispose();

            throw;
        }
    }

    internal void Validate()
    {
        if (!FileHandleIdentityInterop.TryGetPathMetadata(Path, out FileHandleMetadata current)
            || current.Identity != _identity
            || !FileHandleIdentityInterop.TryGetHandleKernelPath(Root._handle, out string? rootPath)
            || !FileHandleIdentityInterop.TryGetHandleKernelPath(_handle, out string? childPath)
            || !WorkspacePathPolicy.IsPathUnderWorkspace(rootPath, childPath))
        {
            throw new IOException("The admitted spell directory has changed.");
        }
    }

    internal Task WriteTextAsync(string leaf, string content, CancellationToken cancellationToken) =>
        WriteBytesAsync(leaf, Encoding.UTF8.GetBytes(content), cancellationToken);

    internal async Task WriteBytesAsync(string leaf, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        ValidateLeaf(leaf);

        cancellationToken.ThrowIfCancellationRequested();

        Validate();

        FileHandleIdentity? destinationIdentity = GetFileIdentityOrAbsence(leaf);

        string temporary = $".{leaf}.{Guid.NewGuid():N}.tmp";

        // Unix rename/unlink target names, so keep staging in an owner-only directory.
        // Windows publication and deletion instead operate on the validated file handle.
        using SpellDirectory? staging = OperatingSystem.IsWindows() ? null : CreateStaging();

        SpellDirectory writer = staging ?? this;

        using SafeFileHandle file = writer.CreateFileExclusive(temporary);

        if (!SecureFileReader.TryValidateRegularFileHandle(file, null, out FileHandleMetadata owned))
        {
            throw new IOException("The staged spell file is not an unaliased regular file.");
        }

        try
        {
            // A stream over the already opened file cannot be redirected by a parent replacement.
            using (FileStream stream = new(file, FileAccess.Write, 4096, isAsync: false))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);

                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

                stream.Flush(flushToDisk: true);

                cancellationToken.ThrowIfCancellationRequested();

                Validate();

                if (GetFileIdentityOrAbsence(leaf) != destinationIdentity
                    || writer.GetFileIdentityOrAbsence(temporary) != owned.Identity)
                {
                    throw new IOException("The spell file changed before publication.");
                }

                writer.Rename(temporary, leaf, replace: true, directory: false, owned.Identity, this);

                _writtenFiles[leaf] = owned.Identity;
            }

            _ = DurableDirectoryFlush.TryFlush(_handle);
        }
        finally
        {
            writer.DeleteOwnedLeaf(temporary, owned.Identity, directory: false);
        }
    }

    internal void Publish(string leaf)
    {
        ValidateLeaf(leaf);

        Validate();

        _parent!.Validate();

        _parent.Rename(System.IO.Path.GetFileName(Path), leaf, replace: false, directory: true, _identity);

        _published = true;

        _ = DurableDirectoryFlush.TryFlush(_parent._handle);
    }

    private SafeFileHandle CreateFileExclusive(string leaf)
    {
        if (OperatingSystem.IsWindows())
        {
            return OpenWindows(_handle, leaf, directory: false, disposition: 2);
        }

        int flags = OperatingSystem.IsMacOS() ? 0x0002 | 0x0200 | 0x0800 | 0x0100 | 0x1000000 : 0x0002 | 0x0040 | 0x0080 | 0x20000 | 0x80000;

        int descriptor = OpenAt(Descriptor(_handle), leaf, flags, 0x180);

        return descriptor >= 0
            ? new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true)
            : throw new IOException("The staged spell file could not be created.");
    }

    private void Rename(string source, string target, bool replace, bool directory, FileHandleIdentity expected, SpellDirectory? destination = null)
    {
        BeforeRenameForTests?.Invoke(System.IO.Path.Combine(Path, source));

        SpellDirectory targetDirectory = destination ?? this;

        Validate();

        targetDirectory.Validate();

        using SafeFileHandle sourceHandle = OpenRenameSource(source, directory);

        if (!FileHandleIdentityInterop.TryGetHandleMetadata(sourceHandle, out FileHandleMetadata actual)
            || actual.Identity != expected)
        {
            throw new IOException("The staged spell artifact changed before publication.");
        }

        bool renamed;

        if (OperatingSystem.IsWindows())
        {
            byte[] name = Encoding.Unicode.GetBytes(target);

            byte[] buffer = new byte[24 + name.Length];

            // FileRenameInformationEx keeps an already-open reader on the old file while
            // the replacement becomes the directory entry. Plain replacement refuses it.
            const uint replaceIfExists = 0x00000001;

            const uint posixSemantics = 0x00000002;

            BinaryPrimitives.WriteUInt32LittleEndian(buffer, replace ? replaceIfExists | posixSemantics : 0);

            // FILE_RENAME_INFORMATION binds the relative name to the retained destination directory.
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(8), targetDirectory._handle.DangerousGetHandle().ToInt64());

            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(16), name.Length);

            name.CopyTo(buffer.AsSpan(20));

            const int fileRenameInformation = 10;

            const int fileRenameInformationEx = 65;

            renamed = SetWindowsInformation(sourceHandle, buffer, replace ? fileRenameInformationEx : fileRenameInformation);

            GC.KeepAlive(targetDirectory);
        }
        else if (replace)
        {
            renamed = RenameAt(Descriptor(_handle), source, Descriptor(targetDirectory._handle), target) == 0;
        }
        else if (OperatingSystem.IsMacOS())
        {
            renamed = RenameAtMac(Descriptor(_handle), source, Descriptor(targetDirectory._handle), target, 4) == 0;
        }
        else
        {
            renamed = RenameAtLinux(Descriptor(_handle), source, Descriptor(targetDirectory._handle), target, 1) == 0;
        }

        if (!renamed)
        {
            throw new IOException("The spell artifact could not be published.");
        }
    }

    private SafeFileHandle OpenRenameSource(string leaf, bool directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return OpenWindows(_handle, leaf, directory, disposition: 1);
        }

        SafeFileHandle? handle;

        bool opened = directory
            ? FileHandleIdentityInterop.TryOpenDirectoryMetadataRelative(_handle, leaf, requestReadControl: false, out handle, out _)
            : FileHandleIdentityInterop.TryOpenReadOnlyNoFollowRelative(_handle, leaf, out handle) == SecureFileOpenStatus.Success;

        return opened && handle is not null
            ? handle
            : throw new IOException("The staged spell artifact could not be opened without following links.");
    }

    private bool DeleteOwnedLeaf(string leaf, FileHandleIdentity expected, bool directory)
    {
        SafeFileHandle? child = null;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                child = OpenWindows(_handle, leaf, directory, disposition: 1);
            }
            else if (directory)
            {
                if (!FileHandleIdentityInterop.TryOpenDirectoryMetadataRelative(_handle, leaf, requestReadControl: false, out child, out _))
                {
                    return false;
                }
            }
            else if (FileHandleIdentityInterop.TryOpenReadOnlyNoFollowRelative(_handle, leaf, out child)
                     != SecureFileOpenStatus.Success)
            {
                return false;
            }

            if (child is null || !FileHandleIdentityInterop.TryGetHandleMetadata(child, out FileHandleMetadata actual)
                || actual.Identity != expected)
            {
                return false;
            }

            if (OperatingSystem.IsWindows())
            {
                return SetWindowsInformation(child, [1], 13);
            }
            else
            {
                return UnlinkAt(Descriptor(_handle), leaf, directory ? (OperatingSystem.IsMacOS() ? 0x80 : 0x200) : 0) == 0;
            }
        }
        catch (IOException)
        {
            return false;
        }
        finally
        {
            child?.Dispose();
        }
    }

    private FileHandleIdentity? GetFileIdentityOrAbsence(string leaf)
    {
        SecureFileOpenStatus status = FileHandleIdentityInterop.TryOpenReadOnlyNoFollowRelative(_handle, leaf, out SafeFileHandle? handle);

        using (handle)
        {
            if (status == SecureFileOpenStatus.NotFound)
            {
                return null;
            }

            if (status != SecureFileOpenStatus.Success || handle is null
                || !SecureFileReader.TryValidateRegularFileHandle(handle, null, out FileHandleMetadata metadata))
            {
                throw new IOException("The spell destination is not an unaliased regular file.");
            }

            return metadata.Identity;
        }
    }

    internal void DeleteFile(string leaf)
    {
        ValidateLeaf(leaf);

        Validate();

        if (FileHandleIdentityInterop.TryOpenReadOnlyNoFollowRelative(_handle, leaf, out SafeFileHandle? file)
            == SecureFileOpenStatus.Success && file is not null)
        {
            using (file)
            {
                if (_writtenFiles.TryGetValue(leaf, out FileHandleIdentity owned)
                    && SecureFileReader.TryValidateRegularFileHandle(file, owned, out FileHandleMetadata metadata))
                {
                    DeleteOwnedLeaf(leaf, metadata.Identity, directory: false);
                }
            }
        }
    }

    internal void Delete()
    {
        Validate();

        if (_parent is null || !DeleteContents()
            || !_parent.DeleteOwnedLeaf(System.IO.Path.GetFileName(Path), _identity, directory: true))
        {
            throw new IOException("The admitted spell directory could not be deleted.");
        }
    }

    private bool DeleteContents()
    {
        if (!SecureDirectoryNameEnumerator.TryEnumerate(_handle, CancellationToken.None, out string[] names))
        {
            return false;
        }

        bool deleted = true;

        foreach (string leaf in names)
        {
            if (FileHandleIdentityInterop.TryOpenReadOnlyNoFollowRelative(_handle, leaf, out SafeFileHandle? file)
                == SecureFileOpenStatus.Success && file is not null)
            {
                using (file)
                {
                    deleted &= SecureFileReader.TryValidateRegularFileHandle(file, null, out FileHandleMetadata metadata)
                        && DeleteOwnedLeaf(leaf, metadata.Identity, directory: false);
                }
            }
            else if (FileHandleIdentityInterop.TryOpenDirectoryMetadataRelative(_handle, leaf, requestReadControl: false, requestEnumeration: true, out SafeFileHandle child, out FileHandleMetadata metadata))
            {
                using SpellDirectory nested = new(System.IO.Path.Combine(Path, leaf), child, this, false);

                deleted &= nested.DeleteContents()
                    && DeleteOwnedLeaf(leaf, metadata.Identity, directory: true);
            }
            else
            {
                deleted = false;
            }
        }

        return deleted;
    }

    public void Dispose()
    {
        try
        {
            if (_cleanup && !_published)
            {
                // Cleanup follows retained directory identities, never the potentially replaced pathname.
                DeleteContents();

                _parent!.DeleteOwnedLeaf(System.IO.Path.GetFileName(Path), _identity, directory: true);
            }
        }
        finally
        {
            _handle.Dispose();

            if (_ownsParent)
            {
                _parent?.Dispose();
            }
        }
    }

    private static SafeFileHandle OpenRoot(string root)
    {
        if (OperatingSystem.IsWindows())
        {
            SafeFileHandle handle = CreateFileWindows(root, 0x001100A7, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);

            if (handle.IsInvalid)
            {
                handle.Dispose();

                throw new IOException("The workspace directory could not be retained.");
            }

            return handle;
        }

        return FileHandleIdentityInterop.TryOpenDirectoryMetadata(root, requestEnumeration: true, out SafeFileHandle opened, out _)
            ? opened
            : throw new IOException("The workspace directory could not be retained.");
    }

    private static unsafe SafeFileHandle OpenWindows(SafeFileHandle parent, string leaf, bool directory, uint disposition)
    {
        fixed (char* characters = leaf)
        {
            UnicodeString name = new() { Length = checked((ushort)(leaf.Length * 2)), MaximumLength = checked((ushort)(leaf.Length * 2)), Buffer = (IntPtr)characters };

            ObjectAttributes attributes = new() { Length = Marshal.SizeOf<ObjectAttributes>(), RootDirectory = parent.DangerousGetHandle(), ObjectName = (IntPtr)(&name), Attributes = 0x40 };

            int status = NtCreateFile(out IntPtr raw, directory ? 0x001100A7U : 0x00110083U, ref attributes, out _, IntPtr.Zero, 0x80, 7, disposition, 0x00200000U | 0x20U | (directory ? 1U : 0x40U), IntPtr.Zero, 0);

            if (status < 0)
            {
                throw new IOException("The relative spell artifact could not be opened.");
            }

            SafeFileHandle handle = new(raw, ownsHandle: true);

            if (!FileHandleIdentityInterop.TryGetHandleMetadata(handle, out FileHandleMetadata metadata)
                || metadata.Kind != (directory ? FileSystemObjectKind.Directory : FileSystemObjectKind.RegularFile))
            {
                handle.Dispose();

                throw new IOException("The relative spell artifact is not the required kind.");
            }

            return handle;
        }
    }

    private static unsafe bool SetWindowsInformation(SafeFileHandle handle, byte[] buffer, int informationClass)
    {
        fixed (byte* pointer = buffer)
        {
            return NtSetInformationFile(handle, out _, (IntPtr)pointer, checked((uint)buffer.Length), informationClass) >= 0;
        }
    }

    private static void ValidateLeaf(string leaf)
    {
        if (string.IsNullOrWhiteSpace(leaf) || leaf is "." or ".." || leaf.IndexOfAny(['/', '\\', ':', '\0']) >= 0)
        {
            throw new IOException("A spell artifact must have a bare file name.");
        }
    }

    private static int Descriptor(SafeFileHandle handle) => handle.DangerousGetHandle().ToInt32();

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { internal ushort Length; internal ushort MaximumLength; internal IntPtr Buffer; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes { internal int Length; internal IntPtr RootDirectory; internal IntPtr ObjectName; internal uint Attributes; internal IntPtr SecurityDescriptor; internal IntPtr SecurityQualityOfService; }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock { internal IntPtr Status; internal UIntPtr Information; }

    [LibraryImport("libc", EntryPoint = "mkdirat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MkdirAt(int directory, string leaf, int mode);

    // Apple's arm64 ABI passes openat's variadic mode on the stack. This is the same ABI
    // dispatch used by GrimoireOfflineTransitionJournalFilePrimitives for exclusive creation.
    private static int OpenAt(int directory, string leaf, int flags, int mode) =>
        OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? OpenAtAppleArm64(directory, leaf, flags, 0, 0, 0, 0, 0, mode)
            : OpenAtFixedArity(directory, leaf, flags, mode);

    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenAtFixedArity(int directory, string leaf, int flags, int mode);

    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenAtAppleArm64(int directory, string leaf, int flags, int filler3, int filler4, int filler5, int filler6, int filler7, int mode);

    [LibraryImport("libc", EntryPoint = "renameat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int RenameAt(int sourceDirectory, string source, int targetDirectory, string target);

    [LibraryImport("libc", EntryPoint = "renameatx_np", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int RenameAtMac(int sourceDirectory, string source, int targetDirectory, string target, uint flags);

    [LibraryImport("libc", EntryPoint = "renameat2", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int RenameAtLinux(int sourceDirectory, string source, int targetDirectory, string target, uint flags);

    [LibraryImport("libc", EntryPoint = "unlinkat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int UnlinkAt(int directory, string leaf, int flags);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileWindows(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [LibraryImport("ntdll.dll", EntryPoint = "NtCreateFile")]
    private static partial int NtCreateFile(out IntPtr handle, uint access, ref ObjectAttributes attributes, out IoStatusBlock status, IntPtr allocation, uint fileAttributes, uint share, uint disposition, uint options, IntPtr ea, uint eaLength);

    [LibraryImport("ntdll.dll", EntryPoint = "NtSetInformationFile")]
    private static partial int NtSetInformationFile(SafeFileHandle handle, out IoStatusBlock status, IntPtr information, uint length, int informationClass);
}
