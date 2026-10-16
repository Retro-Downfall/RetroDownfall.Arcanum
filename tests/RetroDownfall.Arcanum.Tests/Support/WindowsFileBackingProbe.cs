using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// What a Windows volume does with a file's data, read through calls that do not depend on whether a file
/// system filter lets the reparse-point attribute show in an ordinary attribute query.
/// </summary>
/// <remarks>
/// <para>A file compressed in place with <c>compact.exe /exe</c> is backed by the Windows Overlay Filter
/// (<c>wof.sys</c>), and on a volume that filter is attached to it hides the reparse point that backs the file
/// from ordinary attribute queries: the framework and the repository's own path policy read an ordinary file.
/// <see cref="ReadExternalBacking"/> asks the filter's own user-mode API instead, so a test can tell a file the
/// volume really backed from one it did not, and <see cref="ReadReparseTag"/> opens the entry without
/// following reparse points to read the tag when the filter lets it show.</para>
///
/// <para>Both calls are evidence for a test to report, never a reason for production code to branch: the
/// repository's path policy does not read a reparse tag.</para>
/// </remarks>
internal static class WindowsFileBackingProbe
{
    /// <summary><c>WOF_PROVIDER_FILE</c>: the provider behind <c>compact.exe /exe</c>.</summary>
    internal const uint FileProvider = 2;

    /// <summary><c>IO_REPARSE_TAG_WOF</c>: data the Windows Overlay Filter backs, as <c>compact.exe /exe</c> leaves it.</summary>
    internal const uint ReparseTagWof = 0x80000017;

    /// <summary><c>IO_REPARSE_TAG_DEDUP</c>: data the volume deduplicates.</summary>
    internal const uint ReparseTagDedup = 0x80000013;

    /// <summary><c>IO_REPARSE_TAG_CLOUD</c>: a cloud placeholder (the base tag; its variants <c>IO_REPARSE_TAG_CLOUD_1</c> to <c>_F</c> differ in bits 12 to 15 of the low word, <c>IO_REPARSE_TAG_CLOUD_MASK</c> 0x0000F000).</summary>
    internal const uint ReparseTagCloud = 0x9000001A;

    /// <summary><c>IO_REPARSE_TAG_HSM</c>: data a storage-management service moved off the volume.</summary>
    internal const uint ReparseTagHsm = 0xC0000004;

    /// <summary><c>IO_REPARSE_TAG_SYMLINK</c>: a symbolic link, which names another location.</summary>
    internal const uint ReparseTagSymbolicLink = 0xA000000C;

    /// <summary><c>IO_REPARSE_TAG_MOUNT_POINT</c>: a junction or volume mount point, which names another location.</summary>
    internal const uint ReparseTagMountPoint = 0xA0000003;

    /// <summary>The bit <c>IsReparseTagNameSurrogate</c> tests: set in a tag whose entry names another file system entity.</summary>
    private const uint NameSurrogateBit = 0x20000000;

    /// <summary>
    /// Larger than any provider's backing information, so the buffer never decides the answer; the bytes the
    /// call reports are returned unread, because their layout differs by provider.
    /// </summary>
    private const int BackingInformationBufferBytes = 256;

    private const uint FileReadAttributes = 0x0080;

    private const uint FileFlagBackupSemantics = 0x02000000;

    private const uint FileFlagOpenReparsePoint = 0x00200000;

    /// <summary><c>FileAttributeTagInfo</c> in <c>FILE_INFO_BY_HANDLE_CLASS</c>.</summary>
    private const int FileAttributeTagInfoClass = 9;

    /// <summary>True when the tag marks an entry that names another location: a link, not data the volume serves in place.</summary>
    internal static bool IsNameSurrogate(uint tag) => (tag & NameSurrogateBit) != 0;

    /// <summary>The documented name of the tag when it is one this probe knows, otherwise its hexadecimal value alone.</summary>
    internal static string DescribeTag(uint tag)
    {
        string? name = tag switch
        {
            ReparseTagWof => "IO_REPARSE_TAG_WOF",
            ReparseTagDedup => "IO_REPARSE_TAG_DEDUP",
            ReparseTagCloud => "IO_REPARSE_TAG_CLOUD",
            ReparseTagHsm => "IO_REPARSE_TAG_HSM",
            ReparseTagSymbolicLink => "IO_REPARSE_TAG_SYMLINK",
            ReparseTagMountPoint => "IO_REPARSE_TAG_MOUNT_POINT",
            _ => null,
        };

        return name is null ? $"0x{tag:X8}" : $"0x{tag:X8} ({name})";
    }

    /// <summary>
    /// Asks <c>wofutil.dll</c> whether the volume backs the file's data externally and through which provider.
    /// The answer comes from the filter's own API, not from an attribute query it can hide the reparse point
    /// from. Never throws for an unavailable API or a volume that does not answer: <see cref="ExternalBacking.Answered"/>
    /// says so and <see cref="ExternalBacking.Detail"/> carries the reason.
    /// </summary>
    internal static ExternalBacking ReadExternalBacking(string path)
    {
        byte[] information = new byte[BackingInformationBufferBytes];

        uint length = (uint)information.Length;

        try
        {
            int result = WofIsExternalFile(path, out bool isExternal, out uint provider, information, ref length);

            if (result != 0)
            {
                return new ExternalBacking(
                    Answered: false,
                    IsExternal: false,
                    Provider: 0,
                    Detail: $"WofIsExternalFile failed with HRESULT 0x{result:X8}.");
            }

            if (!isExternal)
            {
                return new ExternalBacking(
                    Answered: true,
                    IsExternal: false,
                    Provider: 0,
                    Detail: "WofIsExternalFile: not externally backed.");
            }

            int written = (int)Math.Min(length, (uint)information.Length);

            return new ExternalBacking(
                Answered: true,
                IsExternal: true,
                Provider: provider,
                Detail: $"WofIsExternalFile: externally backed; provider {provider}; "
                    + $"backing information [{Convert.ToHexString(information, 0, written)}].");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return new ExternalBacking(
                Answered: false,
                IsExternal: false,
                Provider: 0,
                Detail: "wofutil.dll could not be bound: " + ex.Message);
        }
    }

    /// <summary>
    /// Opens the entry without following reparse points and reads the attribute word and reparse tag the
    /// volume reports for the open. A tag of zero means this view of the entry is not a reparse point; a filter
    /// that hides the reparse point from ordinary queries may hide it from this one too.
    /// </summary>
    internal static ReparseTagReading ReadReparseTag(string path)
    {
        using SafeFileHandle handle = CreateFile(
            path,
            FileReadAttributes,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();

            return new ReparseTagReading(
                Answered: false,
                Attributes: 0,
                Tag: 0,
                Detail: $"opening with FILE_FLAG_OPEN_REPARSE_POINT failed with Win32 error {error}.");
        }

        if (!GetFileInformationByHandleEx(
                handle,
                FileAttributeTagInfoClass,
                out FileAttributeTagInfo information,
                (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
        {
            int error = Marshal.GetLastPInvokeError();

            return new ReparseTagReading(
                Answered: false,
                Attributes: 0,
                Tag: 0,
                Detail: $"GetFileInformationByHandleEx(FileAttributeTagInfo) failed with Win32 error {error}.");
        }

        return new ReparseTagReading(
            Answered: true,
            Attributes: information.FileAttributes,
            Tag: information.ReparseTag,
            Detail: information.ReparseTag == 0
                ? $"open-reparse-point view: attributes 0x{information.FileAttributes:X}; no reparse tag shown."
                : $"open-reparse-point view: attributes 0x{information.FileAttributes:X}; "
                    + $"reparse tag {DescribeTag(information.ReparseTag)}.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        internal uint FileAttributes;

        internal uint ReparseTag;
    }

    [DllImport("wofutil.dll", EntryPoint = "WofIsExternalFile", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WofIsExternalFile(
        string filePath,
        [MarshalAs(UnmanagedType.Bool)] out bool isExternalFile,
        out uint provider,
        [In, Out] byte[] externalFileInfo,
        ref uint bufferLength);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        FileMode creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        out FileAttributeTagInfo fileInformation,
        uint bufferSize);
}

/// <summary>
/// What <see cref="WindowsFileBackingProbe.ReadExternalBacking"/> learned. <paramref name="Answered"/> is false
/// when the API was unavailable or the volume did not answer, in which case the other members mean nothing.
/// </summary>
internal readonly record struct ExternalBacking(bool Answered, bool IsExternal, uint Provider, string Detail);

/// <summary>
/// What <see cref="WindowsFileBackingProbe.ReadReparseTag"/> learned. <paramref name="Answered"/> is false when
/// the entry could not be opened or queried, in which case the other members mean nothing.
/// </summary>
internal readonly record struct ReparseTagReading(bool Answered, uint Attributes, uint Tag, string Detail);
