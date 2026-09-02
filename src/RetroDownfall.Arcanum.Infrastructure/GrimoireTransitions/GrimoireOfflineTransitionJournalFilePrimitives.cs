using System.Buffers.Binary;

using System.Runtime.InteropServices;

using System.Runtime.Versioning;

using System.Security.AccessControl;

using System.Security.Principal;

using Microsoft.Win32.SafeHandles;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

internal enum GrimoireOfflineTransitionPreviousRetention : byte
{

    Working,

    Previous,

}

internal readonly record struct GrimoireOfflineTransitionExchangeResult(
    GrimoireOfflineTransitionPreviousRetention Retention);

internal readonly record struct GrimoireOfflineTransitionWindowsReplaceFileArguments(
    string ReplacedFileName,
    string ReplacementFileName,
    string BackupFileName);

internal delegate bool GrimoireOfflineTransitionReplaceFile(
    string replacedFileName,
    string replacementFileName,
    string backupFileName,
    uint replaceFlags,
    IntPtr exclude,
    IntPtr reserved);

internal sealed class GrimoireOfflineTransitionJournalOpenedFile : IDisposable
{

    private const bool WindowsChildStreamsAreAsync =
        GrimoireOfflineTransitionJournalFilePrimitives.WindowsChildStreamsAreAsync;

    private SafeFileHandle? _handle;

    private FileStream? _stream;

    internal GrimoireOfflineTransitionJournalOpenedFile(
        SafeFileHandle handle,
        string displayPath,
        FileHandleMetadata metadata)
    {

        _handle = handle;

        DisplayPath = displayPath;

        Metadata = metadata;

    }

    internal string DisplayPath { get; }

    internal FileHandleMetadata Metadata { get; }

    internal SafeFileHandle Handle =>
        _stream?.SafeFileHandle
        ?? _handle
        ?? throw new ObjectDisposedException(nameof(GrimoireOfflineTransitionJournalOpenedFile));

    internal FileStream GetStream(FileAccess access)
    {

        if (_stream is not null)
        {

            return _stream;

        }

        SafeFileHandle handle = _handle
            ?? throw new ObjectDisposedException(nameof(GrimoireOfflineTransitionJournalOpenedFile));

        _stream = new RelativeFileStream(
            handle,
            DisplayPath,
            access,
            isAsync: WindowsChildStreamsAreAsync);

        _handle = null;

        return _stream;

    }

    public void Dispose()
    {

        _stream?.Dispose();

        _stream = null;

        _handle?.Dispose();

        _handle = null;

    }

    private sealed class RelativeFileStream(
        SafeFileHandle handle,
        string displayPath,
        FileAccess access,
        bool isAsync)
        : FileStream(handle, access, bufferSize: 4096, isAsync)
    {

        public override string Name { get; } = displayPath;

    }

}

internal sealed class GrimoireOfflineTransitionJournalChildEnumeration : IDisposable
{

    private int _disposed;

    internal GrimoireOfflineTransitionJournalChildEnumeration(
        IReadOnlyList<string> names,
        IReadOnlyDictionary<string, GrimoireOfflineTransitionJournalOpenedFile> exactChildren)
    {

        Names = names;

        ExactChildren = exactChildren;

    }

    internal IReadOnlyList<string> Names { get; }

    internal IReadOnlyDictionary<string, GrimoireOfflineTransitionJournalOpenedFile> ExactChildren { get; }

    public void Dispose()
    {

        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {

            return;

        }

        foreach (GrimoireOfflineTransitionJournalOpenedFile child in ExactChildren.Values)
        {

            child.Dispose();

        }

    }

}

internal interface IGrimoireOfflineTransitionJournalFilePrimitives : IDisposable
{

    FileHandleMetadata ParentMetadata { get; }

    Result<GrimoireOfflineTransitionJournalOpenedFile> CreateWorkingExclusive(string workingLeaf);

    Result PublishFirstNoReplace(string journalLeaf, string workingLeaf);

    Result<GrimoireOfflineTransitionExchangeResult> ExchangeRetainingPrevious(
        string journalLeaf,
        string workingLeaf,
        string previousLeaf);

    Result MoveNoReplace(string sourceLeaf, string destinationLeaf);

    Result ApplyOwnerOnlyAndVerify(
        GrimoireOfflineTransitionJournalOpenedFile expected,
        string relativeLeaf);

    Result CompareUnlink(
        GrimoireOfflineTransitionJournalOpenedFile expected,
        string relativeLeaf);

    Result<GrimoireOfflineTransitionJournalChildEnumeration> EnumerateExactChildren(
        IReadOnlyList<string> exactLeaves);

    Result FlushParent();

}

/// <summary>
/// The retained-parent native capability for the fixed Grimoire transition-journal slot.
/// </summary>
/// <remarks>
/// The parent handle is opened once without following links and every child lookup, enumeration,
/// rename, and unlink is relative to that handle. Unix compare-unlink has no kernel primitive that
/// combines the comparison and unlink: it therefore compares the retained handle with the relative
/// name immediately before <c>unlinkat</c>, then requires the retained handle's link count to be zero
/// and the fixed name to be absent. That proves the delegated operation converged; it deliberately
/// does not claim that a same-UID attacker could not replace the name in the final instruction window.
/// </remarks>
internal sealed partial class GrimoireOfflineTransitionJournalFilePrimitives
    : IGrimoireOfflineTransitionJournalFilePrimitives
{

    internal const bool WindowsChildStreamsAreAsync = false;

    private const int OwnerOnlyUnixMode = 0x180;

    private const uint FileReadData = 0x00000001;

    private const uint FileWriteData = 0x00000002;

    private const uint DeleteAccess = 0x00010000;

    private const uint ReadControlAccess = 0x00020000;

    private const uint WriteDacAccess = 0x00040000;

    private const uint WriteOwnerAccess = 0x00080000;

    private const uint SynchronizeAccess = 0x00100000;

    private const uint FileReadAttributes = 0x00000080;

    private const uint FileListDirectory = 0x00000001;

    private const uint FileShareRead = 0x00000001;

    private const uint FileShareWrite = 0x00000002;

    private const uint FileShareDelete = 0x00000004;

    internal const uint WindowsParentDesiredAccess =
        FileListDirectory | FileReadAttributes | SynchronizeAccess | ReadControlAccess;

    internal const uint WindowsParentShareMode = FileShareRead | FileShareWrite;

    internal const uint WindowsChildReadDesiredAccess =
        FileReadData
        | FileReadAttributes
        | DeleteAccess
        | ReadControlAccess
        | SynchronizeAccess;

    internal const uint WindowsChildWritableDesiredAccess =
        WindowsChildReadDesiredAccess
        | FileWriteData
        | WriteDacAccess
        | WriteOwnerAccess;

    internal const uint WindowsChildShareMode =
        FileShareRead | FileShareWrite | FileShareDelete;

    private const uint OpenExisting = 3;

    private const uint FileFlagBackupSemantics = 0x02000000;

    private const uint FileFlagOpenReparsePoint = 0x00200000;

    private const uint ObjCaseSensitive = 0;

    private const uint FileOpen = 1;

    private const uint FileCreate = 2;

    private const uint FileNonDirectoryFile = 0x00000040;

    private const uint FileOpenReparsePoint = 0x00200000;

    private const uint FileSynchronousIoNonAlert = 0x00000020;

    private const int FileRenameInfo = 3;

    private const int FileDispositionInfoEx = 21;

    private const uint FileDispositionDelete = 0x00000001;

    private const uint FileDispositionPosixSemantics = 0x00000002;

    private const int FileIdBothDirectoryInformation = 37;

    private const int StatusNoMoreFiles = unchecked((int)0x80000006);

    private const uint OwnerSecurityInformation = 0x00000001;

    private const uint DaclSecurityInformation = 0x00000004;

    private const ushort SecurityDescriptorDaclProtected = 0x1000;

    private const int AclSizeInformationClass = 2;

    private const int RenameNoReplace = 1;

    private const int RenameExchange = 2;

    private const uint RenameSwapMac = 0x00000002;

    private const uint RenameExclusiveMac = 0x00000004;

    private readonly string _parentPath;

    private SafeFileHandle? _parent;

    private GrimoireOfflineTransitionJournalFilePrimitives(
        string parentPath,
        SafeFileHandle parent,
        FileHandleMetadata parentMetadata)
    {

        _parentPath = parentPath;

        _parent = parent;

        ParentMetadata = parentMetadata;

    }

    public FileHandleMetadata ParentMetadata { get; }

    internal static Result<GrimoireOfflineTransitionJournalFilePrimitives> Open(
        string parentPath,
        CovenantDigest? expectedParentDigest = null)
    {

        if (string.IsNullOrWhiteSpace(parentPath))
        {

            return Unavailable<GrimoireOfflineTransitionJournalFilePrimitives>();

        }

        SafeFileHandle? parent = null;

        try
        {

            parent = OpenParentNoFollow(Path.GetFullPath(parentPath));

            if (parent is null || parent.IsInvalid || parent.IsClosed
                || !FileHandleIdentityInterop.TryGetHandleMetadata(
                    parent,
                    out FileHandleMetadata metadata)
                || metadata.Kind is not FileSystemObjectKind.Directory
                || !HasStrictOwnerOnlyParentHandlePosture(parent))
            {

                return Unavailable<GrimoireOfflineTransitionJournalFilePrimitives>();

            }

            CovenantDigest physical = BackupRestoreJournalAuthenticator.PhysicalIdentity(
                metadata.Identity.VolumeId,
                metadata.Identity.FileId);

            if (expectedParentDigest is CovenantDigest expected && physical != expected)
            {

                return RecoveryRequired<GrimoireOfflineTransitionJournalFilePrimitives>();

            }

            GrimoireOfflineTransitionJournalFilePrimitives capability = new(
                Path.GetFullPath(parentPath),
                parent,
                metadata);

            parent = null;

            return capability;

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or PlatformNotSupportedException)
        {

            return Unavailable<GrimoireOfflineTransitionJournalFilePrimitives>();

        }
        finally
        {

            parent?.Dispose();

        }

    }

    public Result<GrimoireOfflineTransitionJournalOpenedFile> CreateWorkingExclusive(
        string workingLeaf)
    {

        if (!ValidLeaf(workingLeaf) || !ValidateParent())
        {

            return Unavailable<GrimoireOfflineTransitionJournalOpenedFile>();

        }

        SafeFileHandle? handle = null;

        try
        {

            SecureFileOpenStatus opened = OpenChild(
                workingLeaf,
                createExclusive: true,
                writable: true,
                out handle);

            if (opened is not SecureFileOpenStatus.Success || handle is null)
            {

                return RecoveryRequired<GrimoireOfflineTransitionJournalOpenedFile>();

            }

            if (!FileHandleIdentityInterop.TryGetHandleMetadata(
                    handle,
                    out FileHandleMetadata metadata)
                || metadata.Kind is not FileSystemObjectKind.RegularFile
                || metadata.HardLinkCount != 1)
            {

                return RecoveryRequired<GrimoireOfflineTransitionJournalOpenedFile>();

            }

            string displayPath = ChildPath(workingLeaf);

            bool ownerOnly = OperatingSystem.IsWindows()
                ? ApplyWindowsOwnerOnly(handle)
                : Fchmod(handle.DangerousGetHandle().ToInt32(), OwnerOnlyUnixMode) == 0;

            bool ownerOnlyVerified = OperatingSystem.IsWindows()
                ? VerifyWindowsOwnerOnlyHandle(handle)
                : SecureFilePermissions.HasOwnerControlledFileHandlePosture(
                    handle,
                    displayPath,
                    metadata.Identity);

            if (!ownerOnly || !ownerOnlyVerified)
            {

                using GrimoireOfflineTransitionJournalOpenedFile failed = new(
                    handle,
                    displayPath,
                    metadata);

                handle = null;

                return CompareUnlink(failed, workingLeaf).IsSuccess
                    ? Unavailable<GrimoireOfflineTransitionJournalOpenedFile>()
                    : RecoveryRequired<GrimoireOfflineTransitionJournalOpenedFile>();

            }

            GrimoireOfflineTransitionJournalOpenedFile file = new(
                handle,
                displayPath,
                metadata);

            handle = null;

            return file;

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {

            return Unavailable<GrimoireOfflineTransitionJournalOpenedFile>();

        }
        finally
        {

            handle?.Dispose();

        }

    }

    public Result PublishFirstNoReplace(string journalLeaf, string workingLeaf)
    {

        if (!ValidLeaf(journalLeaf) || !ValidLeaf(workingLeaf) || !ValidateParent())
        {

            return Unavailable();

        }

        try
        {

            bool moved = OperatingSystem.IsLinux()
                ? RenameAt2(ParentDescriptor, workingLeaf, ParentDescriptor, journalLeaf, RenameNoReplace) == 0
                : OperatingSystem.IsMacOS()
                    ? RenameAtXMac(
                        ParentDescriptor,
                        workingLeaf,
                        ParentDescriptor,
                        journalLeaf,
                        RenameExclusiveMac) == 0
                    : OperatingSystem.IsWindows()
                        && RenameWindowsHandle(workingLeaf, journalLeaf);

            return moved && ValidateParent() ? Result.Success() : Unavailable();

        }
        catch (Exception exception) when (
            exception is EntryPointNotFoundException
                or DllNotFoundException
                or IOException
                or UnauthorizedAccessException)
        {

            return Unavailable();

        }

    }

    public Result<GrimoireOfflineTransitionExchangeResult> ExchangeRetainingPrevious(
        string journalLeaf,
        string workingLeaf,
        string previousLeaf)
    {

        if (!ValidLeaf(journalLeaf) || !ValidLeaf(workingLeaf) || !ValidLeaf(previousLeaf)
            || !ValidateParent())
        {

            return Unavailable<GrimoireOfflineTransitionExchangeResult>();

        }

        try
        {

            if (OperatingSystem.IsLinux())
            {

                return RenameAt2(
                    ParentDescriptor,
                    workingLeaf,
                    ParentDescriptor,
                    journalLeaf,
                    RenameExchange) == 0
                    ? new GrimoireOfflineTransitionExchangeResult(
                        GrimoireOfflineTransitionPreviousRetention.Working)
                    : Unavailable<GrimoireOfflineTransitionExchangeResult>();

            }

            if (OperatingSystem.IsMacOS())
            {

                return RenameAtXMac(
                    ParentDescriptor,
                    workingLeaf,
                    ParentDescriptor,
                    journalLeaf,
                    RenameSwapMac) == 0
                    ? new GrimoireOfflineTransitionExchangeResult(
                        GrimoireOfflineTransitionPreviousRetention.Working)
                    : Unavailable<GrimoireOfflineTransitionExchangeResult>();

            }

            GrimoireOfflineTransitionWindowsReplaceFileArguments windowsArguments =
                MapWindowsReplaceFileArguments(_parentPath, journalLeaf, workingLeaf, previousLeaf);

            if (OperatingSystem.IsWindows()
                && InvokeWindowsReplaceFile(windowsArguments, ReplaceFileWindows))
            {

                return new GrimoireOfflineTransitionExchangeResult(
                    GrimoireOfflineTransitionPreviousRetention.Previous);

            }

            return Unavailable<GrimoireOfflineTransitionExchangeResult>();

        }
        catch (Exception exception) when (
            exception is EntryPointNotFoundException
                or DllNotFoundException
                or IOException
                or UnauthorizedAccessException)
        {

            return Unavailable<GrimoireOfflineTransitionExchangeResult>();

        }

    }

    internal static GrimoireOfflineTransitionWindowsReplaceFileArguments
        MapWindowsReplaceFileArguments(
            string parentPath,
            string journalLeaf,
            string workingLeaf,
            string previousLeaf)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(parentPath);

        if (!ValidLeaf(journalLeaf) || !ValidLeaf(workingLeaf) || !ValidLeaf(previousLeaf))
        {

            throw new ArgumentException("Windows replacement arguments require valid relative leaves.");

        }

        return new GrimoireOfflineTransitionWindowsReplaceFileArguments(
            Path.Combine(parentPath, journalLeaf),
            Path.Combine(parentPath, workingLeaf),
            Path.Combine(parentPath, previousLeaf));

    }

    internal static bool InvokeWindowsReplaceFile(
        GrimoireOfflineTransitionWindowsReplaceFileArguments arguments,
        GrimoireOfflineTransitionReplaceFile replaceFile)
    {

        ArgumentNullException.ThrowIfNull(replaceFile);

        return replaceFile(
            arguments.ReplacedFileName,
            arguments.ReplacementFileName,
            arguments.BackupFileName,
            replaceFlags: 0,
            exclude: IntPtr.Zero,
            reserved: IntPtr.Zero);

    }

    public Result MoveNoReplace(string sourceLeaf, string destinationLeaf)
    {

        if (!ValidLeaf(sourceLeaf) || !ValidLeaf(destinationLeaf) || !ValidateParent())
        {

            return Unavailable();

        }

        try
        {

            bool moved = OperatingSystem.IsLinux()
                ? RenameAt2(
                    ParentDescriptor,
                    sourceLeaf,
                    ParentDescriptor,
                    destinationLeaf,
                    RenameNoReplace) == 0
                : OperatingSystem.IsMacOS()
                    ? RenameAtXMac(
                        ParentDescriptor,
                        sourceLeaf,
                        ParentDescriptor,
                        destinationLeaf,
                        RenameExclusiveMac) == 0
                    : OperatingSystem.IsWindows()
                        && RenameWindowsHandle(sourceLeaf, destinationLeaf);

            return moved && ValidateParent() ? Result.Success() : Unavailable();

        }
        catch (Exception exception) when (
            exception is EntryPointNotFoundException
                or DllNotFoundException
                or IOException
                or UnauthorizedAccessException)
        {

            return Unavailable();

        }

    }

    public Result ApplyOwnerOnlyAndVerify(
        GrimoireOfflineTransitionJournalOpenedFile expected,
        string relativeLeaf)
    {

        ArgumentNullException.ThrowIfNull(expected);

        if (!ValidLeaf(relativeLeaf) || !ValidateParent())
        {

            return RecoveryRequired();

        }

        try
        {

            bool applied = OperatingSystem.IsWindows()
                ? ApplyWindowsOwnerOnly(expected.Handle)
                : Fchmod(expected.Handle.DangerousGetHandle().ToInt32(), OwnerOnlyUnixMode) == 0;

            bool capturedOwnerOnly = OperatingSystem.IsWindows()
                ? VerifyWindowsOwnerOnlyHandle(expected.Handle)
                : SecureFilePermissions.HasOwnerControlledFileHandlePosture(
                    expected.Handle,
                    expected.DisplayPath,
                    expected.Metadata.Identity);

            if (!applied
                || !capturedOwnerOnly
                || !FileHandleIdentityInterop.TryGetHandleMetadata(
                    expected.Handle,
                    out FileHandleMetadata captured)
                || captured.Kind is not FileSystemObjectKind.RegularFile
                || captured.HardLinkCount != 1
                || !FileHandleIdentity.IdentitiesMatch(
                    expected.Metadata.Identity,
                    captured.Identity))
            {

                return RecoveryRequired();

            }

            SecureFileOpenStatus status = OpenChild(
                relativeLeaf,
                createExclusive: false,
                writable: false,
                out SafeFileHandle? reopened);

            using (reopened)
            {

                bool reopenedOwnerOnly = reopened is not null
                    && (OperatingSystem.IsWindows()
                        ? VerifyWindowsOwnerOnlyHandle(reopened)
                        : SecureFilePermissions.HasOwnerControlledFileHandlePosture(
                            reopened,
                            expected.DisplayPath,
                            expected.Metadata.Identity));

                if (status is not SecureFileOpenStatus.Success || reopened is null
                    || !reopenedOwnerOnly
                    || !FileHandleIdentityInterop.TryGetHandleMetadata(
                        reopened,
                        out FileHandleMetadata current)
                    || current.Kind is not FileSystemObjectKind.RegularFile
                    || current.HardLinkCount != 1
                    || !FileHandleIdentity.IdentitiesMatch(
                        captured.Identity,
                        current.Identity))
                {

                    return RecoveryRequired();

                }

            }

            return ValidateParent() ? Result.Success() : RecoveryRequired();

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException
                or System.Security.SecurityException)
        {

            return RecoveryRequired();

        }

    }

    public Result CompareUnlink(
        GrimoireOfflineTransitionJournalOpenedFile expected,
        string relativeLeaf)
    {

        ArgumentNullException.ThrowIfNull(expected);

        if (!ValidLeaf(relativeLeaf) || !ValidateParent()
            || !FileHandleIdentityInterop.TryGetHandleMetadata(
                expected.Handle,
                out FileHandleMetadata before)
            || before.Kind is not FileSystemObjectKind.RegularFile
            || before.HardLinkCount != 1
            || !FileHandleIdentity.IdentitiesMatch(expected.Metadata.Identity, before.Identity))
        {

            return RecoveryRequired();

        }

        SecureFileOpenStatus status = OpenChild(
            relativeLeaf,
            createExclusive: false,
            writable: false,
            out SafeFileHandle? named);

        using (named)
        {

            if (status is not SecureFileOpenStatus.Success || named is null
                || !FileHandleIdentityInterop.TryGetHandleMetadata(
                    named,
                    out FileHandleMetadata current)
                || current.Kind is not FileSystemObjectKind.RegularFile
                || current.HardLinkCount != 1
                || !FileHandleIdentity.IdentitiesMatch(before.Identity, current.Identity))
            {

                return RecoveryRequired();

            }

        }

        bool unlinked;

        try
        {

            if (OperatingSystem.IsWindows())
            {

                uint disposition = FileDispositionDelete | FileDispositionPosixSemantics;

                unlinked = SetFileInformationByHandle(
                    expected.Handle,
                    FileDispositionInfoEx,
                    ref disposition,
                    sizeof(uint));

            }
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {

                unlinked = UnlinkAt(ParentDescriptor, relativeLeaf, flags: 0) == 0;

            }
            else
            {

                unlinked = false;

            }

        }
        catch (Exception exception) when (
            exception is EntryPointNotFoundException
                or DllNotFoundException
                or IOException
                or UnauthorizedAccessException)
        {

            unlinked = false;

        }

        if (!unlinked
            || !FileHandleIdentityInterop.TryGetHandleMetadata(
                expected.Handle,
                out FileHandleMetadata after)
            || !FileHandleIdentity.IdentitiesMatch(before.Identity, after.Identity)
            || after.HardLinkCount != 0)
        {

            return RecoveryRequired();

        }

        SecureFileOpenStatus finalStatus = OpenChild(
            relativeLeaf,
            createExclusive: false,
            writable: false,
            out SafeFileHandle? residue);

        residue?.Dispose();

        return finalStatus is SecureFileOpenStatus.NotFound && ValidateParent()
            ? Result.Success()
            : RecoveryRequired();

    }

    public Result<GrimoireOfflineTransitionJournalChildEnumeration> EnumerateExactChildren(
        IReadOnlyList<string> exactLeaves)
    {

        ArgumentNullException.ThrowIfNull(exactLeaves);

        if (!ValidateParent() || exactLeaves.Count == 0 || exactLeaves.Any(leaf => !ValidLeaf(leaf)))
        {

            return Unavailable<GrimoireOfflineTransitionJournalChildEnumeration>();

        }

        List<string>? names = EnumerateNames();

        if (names is null || names.Count != names.Distinct(StringComparer.Ordinal).Count())
        {

            return Unavailable<GrimoireOfflineTransitionJournalChildEnumeration>();

        }

        HashSet<string> observed = new(names, StringComparer.Ordinal);

        Dictionary<string, GrimoireOfflineTransitionJournalOpenedFile> children =
            new(StringComparer.Ordinal);

        try
        {

            foreach (string leaf in exactLeaves)
            {

                SecureFileOpenStatus status = OpenChild(
                    leaf,
                    createExclusive: false,
                    writable: false,
                    out SafeFileHandle? handle);

                if (status is SecureFileOpenStatus.NotFound && !observed.Contains(leaf))
                {

                    continue;

                }

                if (status is not SecureFileOpenStatus.Success || handle is null
                    || !FileHandleIdentityInterop.TryGetHandleMetadata(
                        handle,
                        out FileHandleMetadata metadata))
                {

                    handle?.Dispose();

                    return Unavailable<GrimoireOfflineTransitionJournalChildEnumeration>();

                }

                observed.Add(leaf);

                children.Add(
                    leaf,
                    new GrimoireOfflineTransitionJournalOpenedFile(
                        handle,
                        ChildPath(leaf),
                        metadata));

            }

            if (!ValidateParent())
            {

                return RecoveryRequired<GrimoireOfflineTransitionJournalChildEnumeration>();

            }

            GrimoireOfflineTransitionJournalChildEnumeration result = new(
                observed.Order(StringComparer.Ordinal).ToArray(),
                children);

            children = new Dictionary<string, GrimoireOfflineTransitionJournalOpenedFile>(
                StringComparer.Ordinal);

            return result;

        }
        finally
        {

            foreach (GrimoireOfflineTransitionJournalOpenedFile child in children.Values)
            {

                child.Dispose();

            }

        }

    }

    public Result FlushParent() =>
        ValidateParent() && BackupRestoreJournalNativeMethods.TryFlushDirectory(ParentHandle)
            ? Result.Success()
            : RecoveryRequired();

    public void Dispose()
    {

        _parent?.Dispose();

        _parent = null;

    }

    private SafeFileHandle ParentHandle =>
        _parent ?? throw new ObjectDisposedException(
            nameof(GrimoireOfflineTransitionJournalFilePrimitives));

    private int ParentDescriptor => ParentHandle.DangerousGetHandle().ToInt32();

    private bool ValidateParent() =>
        _parent is { IsInvalid: false, IsClosed: false } parent
        && FileHandleIdentityInterop.TryGetHandleMetadata(parent, out FileHandleMetadata current)
        && current.Kind is FileSystemObjectKind.Directory
        && FileHandleIdentity.IdentitiesMatch(
            ParentMetadata.Identity,
            current.Identity)
        && HasStrictOwnerOnlyParentHandlePosture(parent);

    internal static bool VerifyOwnerControlledOpenedFileHandle(
        GrimoireOfflineTransitionJournalOpenedFile child)
    {

        ArgumentNullException.ThrowIfNull(child);

        if (!FileHandleIdentityInterop.TryGetHandleMetadata(
                child.Handle,
                out FileHandleMetadata current)
            || current.Kind is not FileSystemObjectKind.RegularFile
            || current.HardLinkCount != 1
            || !FileHandleIdentity.IdentitiesMatch(
                child.Metadata.Identity,
                current.Identity))
        {

            return false;

        }

        if (OperatingSystem.IsWindows())
        {

            return VerifyWindowsOwnerOnlyHandle(child.Handle);

        }

        return (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            && SecureFilePermissions.HasOwnerControlledFileHandlePosture(
                child.Handle,
                child.DisplayPath,
                current.Identity);

    }

    private static bool HasStrictOwnerOnlyParentHandlePosture(SafeFileHandle handle)
    {

        if (OperatingSystem.IsWindows())
        {

            return VerifyWindowsOwnerOnlyHandle(handle);

        }

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {

            return false;

        }

        const UnixFileMode ownerOnlyDirectory =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

        return FileHandleIdentityInterop.TryGetUnixHandleAccessMetadata(
                handle,
                out UnixFileMode mode,
                out uint ownerUserId)
            && mode == ownerOnlyDirectory
            && ownerUserId == GetEffectiveUserIdUnix();

    }

    private string ChildPath(string leaf) => Path.Combine(_parentPath, leaf);

    private static SafeFileHandle? OpenParentNoFollow(string path)
    {

        if (OperatingSystem.IsWindows())
        {

            SafeFileHandle handle = CreateFileWindows(
                path,
                WindowsParentDesiredAccess,
                WindowsParentShareMode,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);

            return handle.IsInvalid ? null : handle;

        }

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {

            return null;

        }

        int flags = OperatingSystem.IsMacOS()
            ? 0x00100000 | 0x01000000 | 0x00000100
            : 0x00010000 | 0x00080000 | 0x00020000;

        int descriptor = OpenUnix(path, flags);

        return descriptor < 0 ? null : new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);

    }

    internal SecureFileOpenStatus OpenChild(
        string leaf,
        bool createExclusive,
        bool writable,
        out SafeFileHandle? handle)
    {

        handle = null;

        if (!ValidLeaf(leaf) || !ValidateParent())
        {

            return SecureFileOpenStatus.Rejected;

        }

        if (OperatingSystem.IsWindows())
        {

            return OpenWindowsChild(leaf, createExclusive, writable, out handle);

        }

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {

            return SecureFileOpenStatus.Rejected;

        }

        int flags;

        if (OperatingSystem.IsMacOS())
        {

            flags = (writable ? 0x00000002 : 0)
                | 0x00000004
                | 0x00000100
                | 0x01000000;

            if (createExclusive)
            {

                flags |= 0x00000200 | 0x00000800;

            }

        }
        else
        {

            flags = (writable ? 0x00000002 : 0)
                | 0x00000800
                | 0x00020000
                | 0x00080000;

            if (createExclusive)
            {

                flags |= 0x00000040 | 0x00000080;

            }

        }

        int descriptor = OpenAtUnix(
            ParentDescriptor,
            leaf,
            flags,
            OwnerOnlyUnixMode);

        if (descriptor >= 0)
        {

            handle = new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);

            return SecureFileOpenStatus.Success;

        }

        int error = Marshal.GetLastPInvokeError();

        return error switch
        {
            2 or 20 => SecureFileOpenStatus.NotFound,
            13 => SecureFileOpenStatus.AccessDenied,
            40 or 62 => SecureFileOpenStatus.Rejected,
            _ => SecureFileOpenStatus.IoError,
        };

    }

    [SupportedOSPlatform("windows")]
    private unsafe SecureFileOpenStatus OpenWindowsChild(
        string leaf,
        bool createExclusive,
        bool writable,
        out SafeFileHandle? handle)
    {

        handle = null;

        IntPtr securityDescriptor = IntPtr.Zero;

        try
        {

            if (createExclusive && !TryCreateOwnerOnlySecurityDescriptor(out securityDescriptor))
            {

                return SecureFileOpenStatus.Rejected;

            }

            fixed (char* leafPointer = leaf)
            {

                UnicodeString objectName = new()
                {
                    Length = checked((ushort)(leaf.Length * sizeof(char))),
                    MaximumLength = checked((ushort)(leaf.Length * sizeof(char))),
                    Buffer = new IntPtr(leafPointer),
                };

                ObjectAttributes attributes = new()
                {
                    Length = Marshal.SizeOf<ObjectAttributes>(),
                    RootDirectory = ParentHandle.DangerousGetHandle(),
                    ObjectName = new IntPtr(&objectName),
                    Attributes = ObjCaseSensitive,
                    SecurityDescriptor = securityDescriptor,
                    SecurityQualityOfService = IntPtr.Zero,
                };

                uint desired = writable
                    ? WindowsChildWritableDesiredAccess
                    : WindowsChildReadDesiredAccess;

                int status = NtCreateFile(
                    out IntPtr raw,
                    desired,
                    ref attributes,
                    out _,
                    IntPtr.Zero,
                    fileAttributes: 0x00000080,
                    WindowsChildShareMode,
                    createExclusive ? FileCreate : FileOpen,
                    FileNonDirectoryFile | FileOpenReparsePoint | FileSynchronousIoNonAlert,
                    IntPtr.Zero,
                    eaLength: 0);

                if (status >= 0)
                {

                    handle = new SafeFileHandle(raw, ownsHandle: true);

                    return SecureFileOpenStatus.Success;

                }

                return status switch
                {
                    unchecked((int)0xC0000034) or unchecked((int)0xC000003A) =>
                        SecureFileOpenStatus.NotFound,
                    unchecked((int)0xC0000022) => SecureFileOpenStatus.AccessDenied,
                    unchecked((int)0xC0000035) when createExclusive => SecureFileOpenStatus.IoError,
                    unchecked((int)0xC000050B) => SecureFileOpenStatus.Rejected,
                    _ => SecureFileOpenStatus.IoError,
                };

            }

        }
        finally
        {

            if (securityDescriptor != IntPtr.Zero)
            {

                _ = LocalFreeWindows(securityDescriptor);

            }

        }

    }

    private List<string>? EnumerateNames()
    {

        if (OperatingSystem.IsWindows())
        {

            return EnumerateWindowsNames();

        }

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {

            return null;

        }

        int duplicate = DuplicateUnix(ParentDescriptor);

        if (duplicate < 0)
        {

            return null;

        }

        IntPtr directory = FdOpenDirectoryUnix(duplicate);

        if (directory == IntPtr.Zero)
        {

            _ = CloseUnix(duplicate);

            return null;

        }

        try
        {

            List<string> names = [];

            RewindDirectoryUnix(directory);

            Marshal.SetLastPInvokeError(0);

            while (true)
            {

                IntPtr entry = ReadDirectoryUnix(directory);

                if (entry == IntPtr.Zero)
                {

                    return Marshal.GetLastPInvokeError() == 0 ? names : null;

                }

                int nameOffset = OperatingSystem.IsMacOS() ? 21 : 19;

                int length;

                if (OperatingSystem.IsMacOS())
                {

                    length = Marshal.ReadInt16(entry, 18);

                }
                else
                {

                    length = 0;

                    while (length <= 255 && Marshal.ReadByte(entry, nameOffset + length) != 0)
                    {

                        length++;

                    }

                }

                if (length is <= 0 or > 255)
                {

                    return null;

                }

                byte[] bytes = new byte[length];

                Marshal.Copy(IntPtr.Add(entry, nameOffset), bytes, 0, length);

                string name;

                try
                {

                    name = new System.Text.UTF8Encoding(
                        encoderShouldEmitUTF8Identifier: false,
                        throwOnInvalidBytes: true).GetString(bytes);

                }
                catch (System.Text.DecoderFallbackException)
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

            _ = CloseDirectoryUnix(directory);

        }

    }

    private unsafe List<string>? EnumerateWindowsNames()
    {

        const int bufferBytes = 64 * 1024;

        byte[] buffer = new byte[bufferBytes];

        List<string> names = [];

        bool restart = true;

        fixed (byte* pointer = buffer)
        {

            while (true)
            {

                int status = NtQueryDirectoryFile(
                    ParentHandle.DangerousGetHandle(),
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    out IoStatusBlock io,
                    new IntPtr(pointer),
                    bufferBytes,
                    FileIdBothDirectoryInformation,
                    returnSingleEntry: false,
                    IntPtr.Zero,
                    restart);

                restart = false;

                if (status == StatusNoMoreFiles)
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

                    uint nameBytes = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(60));

                    if ((nameBytes & 1) != 0 || nameBytes > 510 || 104 + nameBytes > entry.Length)
                    {

                        return null;

                    }

                    string name = System.Text.Encoding.Unicode.GetString(
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

    private unsafe bool RenameWindowsHandle(string sourceLeaf, string destinationLeaf)
    {

        SecureFileOpenStatus status = OpenChild(
            sourceLeaf,
            createExclusive: false,
            writable: false,
            out SafeFileHandle? source);

        using (source)
        {

            if (status is not SecureFileOpenStatus.Success || source is null)
            {

                return false;

            }

            byte[] target = System.Text.Encoding.Unicode.GetBytes(destinationLeaf);

            byte[] buffer = new byte[20 + target.Length];

            buffer[0] = 0;

            BinaryPrimitives.WriteInt64LittleEndian(
                buffer.AsSpan(8),
                ParentHandle.DangerousGetHandle().ToInt64());

            BinaryPrimitives.WriteUInt32LittleEndian(
                buffer.AsSpan(16),
                checked((uint)target.Length));

            target.CopyTo(buffer.AsSpan(20));

            fixed (byte* pointer = buffer)
            {

                return SetFileInformationByHandlePointer(
                    source,
                    FileRenameInfo,
                    new IntPtr(pointer),
                    checked((uint)buffer.Length));

            }

        }

    }

    [SupportedOSPlatform("windows")]
    private static bool ApplyWindowsOwnerOnly(SafeFileHandle handle)
    {

        if (!TryCreateOwnerOnlySecurityDescriptor(out IntPtr descriptor))
        {

            return false;

        }

        try
        {

            if (!GetSecurityDescriptorOwnerWindows(
                    descriptor,
                    out IntPtr owner,
                    out _)
                || !GetSecurityDescriptorDaclWindows(
                    descriptor,
                    out bool present,
                    out IntPtr dacl,
                    out _)
                || !present)
            {

                return false;

            }

            const uint ownerInformation = 0x00000001;

            const uint daclInformation = 0x00000004;

            const uint protectedDaclInformation = 0x80000000;

            return SetSecurityInfoWindows(
                handle,
                objectType: 1,
                ownerInformation | daclInformation | protectedDaclInformation,
                owner,
                IntPtr.Zero,
                dacl,
                IntPtr.Zero) == 0;

        }
        finally
        {

            _ = LocalFreeWindows(descriptor);

        }

    }

    [SupportedOSPlatform("windows")]
    private static bool VerifyWindowsOwnerOnlyHandle(SafeFileHandle handle)
    {

        IntPtr securityDescriptor = IntPtr.Zero;

        try
        {

            uint status = GetSecurityInfoWindows(
                handle,
                objectType: 1,
                OwnerSecurityInformation | DaclSecurityInformation,
                out IntPtr owner,
                out _,
                out IntPtr dacl,
                out _,
                out securityDescriptor);

            using WindowsIdentity current = WindowsIdentity.GetCurrent();

            SecurityIdentifier? currentUser = current.User;

            if (status != 0 || securityDescriptor == IntPtr.Zero
                || owner == IntPtr.Zero || dacl == IntPtr.Zero || currentUser is null
                || !GetSecurityDescriptorControlWindows(
                    securityDescriptor,
                    out ushort control,
                    out _)
                || (control & SecurityDescriptorDaclProtected) == 0
                || !new SecurityIdentifier(owner).Equals(currentUser)
                || !GetAclInformationWindows(
                    dacl,
                    out AclSizeInformation size,
                    (uint)Marshal.SizeOf<AclSizeInformation>(),
                    AclSizeInformationClass)
                || size.AclBytesInUse == 0
                || size.AclBytesInUse > int.MaxValue)
            {

                return false;

            }

            byte[] aclBytes = new byte[checked((int)size.AclBytesInUse)];

            Marshal.Copy(dacl, aclBytes, 0, aclBytes.Length);

            RawAcl acl = new(aclBytes, 0);

            bool currentUserAllowed = false;

            foreach (GenericAce ace in acl)
            {

                if (ace is not QualifiedAce qualified
                    || !qualified.SecurityIdentifier.Equals(currentUser))
                {

                    return false;

                }

                currentUserAllowed |= qualified.AceQualifier is AceQualifier.AccessAllowed;

            }

            return currentUserAllowed;

        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or PlatformNotSupportedException
                or System.Security.SecurityException
                or IdentityNotMappedException)
        {

            return false;

        }
        finally
        {

            if (securityDescriptor != IntPtr.Zero)
            {

                _ = LocalFreeWindows(securityDescriptor);

            }

        }

    }

    [SupportedOSPlatform("windows")]
    private static bool TryCreateOwnerOnlySecurityDescriptor(out IntPtr descriptor)
    {

        descriptor = IntPtr.Zero;

        using WindowsIdentity current = WindowsIdentity.GetCurrent();

        SecurityIdentifier? user = current.User;

        if (user is null)
        {

            return false;

        }

        string sddl = $"O:{user.Value}D:P(A;;FA;;;{user.Value})";

        return ConvertStringSecurityDescriptorToSecurityDescriptorWindows(
            sddl,
            stringSdRevision: 1,
            out descriptor,
            out _);

    }

    private static bool ValidLeaf(string? leaf) =>
        GrimoireOfflineTransitionLeafName.IsValid(leaf);

    private static Result Unavailable() => new Error(
        ErrorCodes.Covenant.Unavailable,
        "The transition journal filesystem capability is unavailable.");

    private static Result<T> Unavailable<T>() => Result<T>.Failure(new Error(
        ErrorCodes.Covenant.Unavailable,
        "The transition journal filesystem capability is unavailable."));

    private static Result RecoveryRequired() => new Error(
        ErrorCodes.Data.RecoveryRequired,
        "Transition journal filesystem evidence requires recovery.");

    private static Result<T> RecoveryRequired<T>() => Result<T>.Failure(new Error(
        ErrorCodes.Data.RecoveryRequired,
        "Transition journal filesystem evidence requires recovery."));

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {

        internal ushort Length;

        internal ushort MaximumLength;

        internal IntPtr Buffer;

    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {

        internal int Length;

        internal IntPtr RootDirectory;

        internal IntPtr ObjectName;

        internal uint Attributes;

        internal IntPtr SecurityDescriptor;

        internal IntPtr SecurityQualityOfService;

    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct AclSizeInformation
    {

        internal readonly uint AceCount;

        internal readonly uint AclBytesInUse;

        internal readonly uint AclBytesFree;

    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct IoStatusBlock
    {

        internal readonly IntPtr Status;

        internal readonly IntPtr Information;

    }

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenUnix(string path, int flags);

    /// <summary>
    /// <c>openat</c> is declared <c>int openat(int, const char *, int, ...)</c>: <c>mode</c> is a
    /// variadic argument. On Apple's arm64 ABI a variadic call passes every variadic argument on the
    /// stack rather than in a register, even though a fixed-arity declaration with the same number of
    /// named parameters would place the fourth argument in a register — so the four-parameter shape
    /// below delivers unspecified register contents as the creation mode on osx-arm64. Dispatch to the
    /// stack-shaped overload there and keep the register-shaped one for every other supported platform.
    /// </summary>
    private static int OpenAtUnix(int directory, string path, int flags, int mode) =>
        OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? OpenAtAppleArm64(directory, path, flags, 0, 0, 0, 0, 0, mode)
            : OpenAtUnixFixedArity(directory, path, flags, mode);

    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenAtUnixFixedArity(int directory, string path, int flags, int mode);

    /// <summary>
    /// Fills x0-x7 with the directory descriptor, the path pointer, the flags, and five zero filler
    /// arguments so the ninth argument -- <paramref name="mode"/> -- spills to the first stack slot,
    /// which is where Apple's arm64 ABI requires a variadic callee to read its first variadic argument
    /// regardless of how many named parameters precede it.
    /// </summary>
    [LibraryImport("libc", EntryPoint = "openat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenAtAppleArm64(
        int directory,
        string path,
        int flags,
        int registerFiller3,
        int registerFiller4,
        int registerFiller5,
        int registerFiller6,
        int registerFiller7,
        int mode);

    [LibraryImport("libc", EntryPoint = "renameat2", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int RenameAt2(
        int oldDirectory,
        string oldPath,
        int newDirectory,
        string newPath,
        int flags);

    [LibraryImport("libc", EntryPoint = "renameatx_np", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int RenameAtXMac(
        int oldDirectory,
        string oldPath,
        int newDirectory,
        string newPath,
        uint flags);

    [LibraryImport("libc", EntryPoint = "unlinkat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int UnlinkAt(int directory, string path, int flags);

    [LibraryImport("libc", EntryPoint = "fchmod", SetLastError = true)]
    private static partial int Fchmod(int descriptor, int mode);

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserIdUnix();

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

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileWindows(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "ReplaceFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReplaceFileWindows(
        string replacedFileName,
        string replacementFileName,
        string backupFileName,
        uint replaceFlags,
        IntPtr exclude,
        IntPtr reserved);

    [LibraryImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(
        SafeFileHandle file,
        int informationClass,
        ref uint information,
        uint bufferSize);

    [LibraryImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandlePointer(
        SafeFileHandle file,
        int informationClass,
        IntPtr information,
        uint bufferSize);

    [LibraryImport("kernel32.dll", EntryPoint = "LocalFree", SetLastError = true)]
    private static partial IntPtr LocalFreeWindows(IntPtr memory);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptorWindows(
        string stringSecurityDescriptor,
        uint stringSdRevision,
        out IntPtr securityDescriptor,
        out uint securityDescriptorSize);

    [LibraryImport("advapi32.dll", EntryPoint = "GetSecurityDescriptorOwner", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSecurityDescriptorOwnerWindows(
        IntPtr securityDescriptor,
        out IntPtr owner,
        [MarshalAs(UnmanagedType.Bool)] out bool ownerDefaulted);

    [LibraryImport("advapi32.dll", EntryPoint = "GetSecurityDescriptorDacl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSecurityDescriptorDaclWindows(
        IntPtr securityDescriptor,
        [MarshalAs(UnmanagedType.Bool)] out bool daclPresent,
        out IntPtr dacl,
        [MarshalAs(UnmanagedType.Bool)] out bool daclDefaulted);

    [LibraryImport("advapi32.dll", EntryPoint = "SetSecurityInfo", SetLastError = true)]
    private static partial uint SetSecurityInfoWindows(
        SafeFileHandle handle,
        uint objectType,
        uint securityInformation,
        IntPtr owner,
        IntPtr group,
        IntPtr dacl,
        IntPtr sacl);

    [LibraryImport("advapi32.dll", EntryPoint = "GetSecurityInfo", SetLastError = true)]
    private static partial uint GetSecurityInfoWindows(
        SafeFileHandle handle,
        uint objectType,
        uint securityInformation,
        out IntPtr owner,
        out IntPtr group,
        out IntPtr dacl,
        out IntPtr sacl,
        out IntPtr securityDescriptor);

    [LibraryImport("advapi32.dll", EntryPoint = "GetSecurityDescriptorControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSecurityDescriptorControlWindows(
        IntPtr securityDescriptor,
        out ushort control,
        out uint revision);

    [LibraryImport("advapi32.dll", EntryPoint = "GetAclInformation", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetAclInformationWindows(
        IntPtr acl,
        out AclSizeInformation aclInformation,
        uint aclInformationLength,
        int aclInformationClass);

    [LibraryImport("ntdll.dll", EntryPoint = "NtCreateFile")]
    private static partial int NtCreateFile(
        out IntPtr fileHandle,
        uint desiredAccess,
        ref ObjectAttributes objectAttributes,
        out IoStatusBlock ioStatusBlock,
        IntPtr allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        IntPtr eaBuffer,
        uint eaLength);

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
