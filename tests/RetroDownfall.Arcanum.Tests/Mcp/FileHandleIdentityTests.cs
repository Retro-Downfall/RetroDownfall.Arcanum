using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Mcp;

[Collection("WorkspacePathPolicy")]
public sealed class FileHandleIdentityTests : IDisposable
{
    private readonly string _tempFile;

    private readonly Func<string, FileHandleIdentity?>? _previousPathTestHook;

    private readonly Func<SafeFileHandle, FileHandleIdentity?>? _previousHandleTestHook;

    private readonly Func<string, FileHandleMetadata?>? _previousPathMetadataTestHook;

    private readonly Func<SafeFileHandle, FileHandleMetadata?>? _previousHandleMetadataTestHook;

    public FileHandleIdentityTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"arcanum-fhi-test-{Guid.NewGuid():N}.txt");

        File.WriteAllText(_tempFile, "test");

        _previousPathTestHook = FileHandleIdentityInterop.TryGetPathIdentityForTests;

        _previousHandleTestHook = FileHandleIdentityInterop.TryGetHandleIdentityForTests;

        _previousPathMetadataTestHook = FileHandleIdentityInterop.TryGetPathMetadataForTests;

        _previousHandleMetadataTestHook = FileHandleIdentityInterop.TryGetHandleMetadataForTests;
    }

    public void Dispose()
    {
        FileHandleIdentityInterop.TryGetPathIdentityForTests = _previousPathTestHook;

        FileHandleIdentityInterop.TryGetHandleIdentityForTests = _previousHandleTestHook;

        FileHandleIdentityInterop.TryGetPathMetadataForTests = _previousPathMetadataTestHook;

        FileHandleIdentityInterop.TryGetHandleMetadataForTests = _previousHandleMetadataTestHook;

        try
        {
            File.Delete(_tempFile);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public void TryGetPathIdentity_TestHookReturningNull_ReturnsFalse()
    {
        FileHandleIdentityInterop.TryGetPathIdentityForTests = _ => null;

        bool result = FileHandleIdentityInterop.TryGetPathIdentity(_tempFile, out FileHandleIdentity identity);

        Assert.False(result);

        Assert.Equal(default, identity);
    }

    [Fact]
    public void TryGetPathIdentity_TestHookReturningValue_ReturnsTrue()
    {
        FileHandleIdentity expected = new(1, 2);

        FileHandleIdentityInterop.TryGetPathIdentityForTests = _ => expected;

        bool result = FileHandleIdentityInterop.TryGetPathIdentity(_tempFile, out FileHandleIdentity identity);

        Assert.True(result);

        Assert.Equal(expected, identity);
    }

    [Fact]
    public void TryGetHandleIdentity_TestHookReturningNull_ReturnsFalse()
    {
        FileHandleIdentityInterop.TryGetHandleIdentityForTests = _ => null;

        using SafeFileHandle handle = File.OpenHandle(_tempFile, FileMode.Open, FileAccess.Read);

        bool result = FileHandleIdentityInterop.TryGetHandleIdentity(handle, out FileHandleIdentity identity);

        Assert.False(result);

        Assert.Equal(default, identity);
    }

    [Fact]
    public void TryGetHandleIdentity_TestHookReturningValue_ReturnsTrue()
    {
        FileHandleIdentity expected = new(3, 4);

        FileHandleIdentityInterop.TryGetHandleIdentityForTests = _ => expected;

        using SafeFileHandle handle = File.OpenHandle(_tempFile, FileMode.Open, FileAccess.Read);

        bool result = FileHandleIdentityInterop.TryGetHandleIdentity(handle, out FileHandleIdentity identity);

        Assert.True(result);

        Assert.Equal(expected, identity);
    }

    [Fact]
    public void TryGetHandleIdentity_InvalidHandle_ReturnsFalse()
    {
        FileHandleIdentityInterop.TryGetHandleIdentityForTests = _ => null;

        using SafeFileHandle handle = new(new IntPtr(-1), false);

        bool result = FileHandleIdentityInterop.TryGetHandleIdentity(handle, out FileHandleIdentity identity);

        Assert.False(result);

        Assert.Equal(default, identity);
    }

    [Fact]
    public void IdentitiesMatch_SameIdentity_ReturnsTrue()
    {
        FileHandleIdentity a = new(1, 2);

        FileHandleIdentity b = new(1, 2);

        Assert.True(FileHandleIdentity.IdentitiesMatch(a, b));
    }

    [Fact]
    public void IdentitiesMatch_DifferentIdentity_ReturnsFalse()
    {
        FileHandleIdentity a = new(1, 2);

        FileHandleIdentity b = new(1, 3);

        Assert.False(FileHandleIdentity.IdentitiesMatch(a, b));
    }

    [Fact]
    public void TryGetPathMetadata_regular_file_reports_single_link()
    {
        bool resolved = FileHandleIdentityInterop.TryGetPathMetadata(
            _tempFile,
            out FileHandleMetadata metadata);

        Assert.True(resolved);

        Assert.Equal(1UL, metadata.HardLinkCount);

        Assert.Equal(FileSystemObjectKind.RegularFile, metadata.Kind);
    }

    [Fact]
    public void TryGetHandleMetadata_matches_path_metadata()
    {
        Assert.True(
            FileHandleIdentityInterop.TryGetPathMetadata(
                _tempFile,
                out FileHandleMetadata pathMetadata));

        using SafeFileHandle handle = File.OpenHandle(
            _tempFile,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        Assert.True(
            FileHandleIdentityInterop.TryGetHandleMetadata(
                handle,
                out FileHandleMetadata handleMetadata));

        Assert.Equal(pathMetadata, handleMetadata);
    }

    [Fact]
    public void Relative_directory_open_stays_bound_to_the_retained_parent_after_path_substitution()
    {
        string container = Directory.CreateTempSubdirectory(
            "arcanum-relative-directory-open-").FullName;

        try
        {
            string originalRoot = Directory.CreateDirectory(
                Path.Combine(container, "root")).FullName;

            string originalMarkerDirectory = Directory.CreateDirectory(
                Path.Combine(originalRoot, ".arcanum")).FullName;

            string replacementRoot = Directory.CreateDirectory(
                Path.Combine(container, "replacement")).FullName;

            _ = Directory.CreateDirectory(
                Path.Combine(replacementRoot, ".arcanum"));

            Assert.True(FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                originalMarkerDirectory,
                out FileHandleMetadata expected));

            Assert.True(FileHandleIdentityInterop.TryOpenDirectoryMetadata(
                originalRoot,
                out SafeFileHandle retainedRoot,
                out _));

            using (retainedRoot)
            {
                Directory.Move(originalRoot, Path.Combine(container, "retained-root"));

                Directory.Move(replacementRoot, originalRoot);

                Assert.True(FileHandleIdentityInterop.TryOpenDirectoryMetadataRelative(
                    retainedRoot,
                    ".arcanum",
                    requestReadControl: false,
                    out SafeFileHandle opened,
                    out FileHandleMetadata observed));

                using (opened)
                {
                    Assert.Equal(expected.Identity, observed.Identity);
                }
            }
        }
        finally
        {
            Directory.Delete(container, recursive: true);
        }
    }

    [Fact]
    public void Relative_file_open_stays_bound_to_the_retained_directory_after_path_substitution()
    {
        string container = Directory.CreateTempSubdirectory(
            "arcanum-relative-file-open-").FullName;

        try
        {
            string originalDirectory = Directory.CreateDirectory(
                Path.Combine(container, "marker-directory")).FullName;

            string markerLeaf = "campaign-root.marker";

            string originalMarker = Path.Combine(originalDirectory, markerLeaf);

            File.WriteAllText(originalMarker, "authentic marker bytes");

            string replacementDirectory = Directory.CreateDirectory(
                Path.Combine(container, "replacement")).FullName;

            File.WriteAllText(
                Path.Combine(replacementDirectory, markerLeaf),
                "authentic marker bytes");

            Assert.True(FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                originalMarker,
                out FileHandleMetadata expected));

            Assert.True(FileHandleIdentityInterop.TryOpenDirectoryMetadata(
                originalDirectory,
                out SafeFileHandle retainedDirectory,
                out _));

            using (retainedDirectory)
            {
                Directory.Move(
                    originalDirectory,
                    Path.Combine(container, "retained-marker-directory"));

                Directory.Move(replacementDirectory, originalDirectory);

                SecureFileOpenStatus status =
                    FileHandleIdentityInterop.TryOpenReadOnlyNoFollowRelative(
                        retainedDirectory,
                        markerLeaf,
                        out SafeFileHandle? opened);

                Assert.Equal(SecureFileOpenStatus.Success, status);

                Assert.NotNull(opened);

                using (opened)
                {
                    Assert.True(FileHandleIdentityInterop.TryGetHandleMetadata(
                        opened,
                        out FileHandleMetadata observed));

                    Assert.Equal(expected.Identity, observed.Identity);
                }
            }
        }
        finally
        {
            Directory.Delete(container, recursive: true);
        }
    }

    [SkippableFact]
    public void TryGetPathMetadata_hard_link_reports_multiple_links()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux() && !OperatingSystem.IsWindows(),
            "Unsupported operating system.");

        string alias = _tempFile + ".alias";

        try
        {
            Assert.True(HardLinkTestSupport.TryCreate(alias, _tempFile));

            Assert.True(
                FileHandleIdentityInterop.TryGetPathMetadata(
                    _tempFile,
                    out FileHandleMetadata metadata));

            Assert.True(metadata.HardLinkCount > 1);
        }
        finally
        {
            File.Delete(alias);
        }
    }

    [Fact]
    public void Windows_file_information_layout_matches_native_FILETIME_packing()
    {
        WindowsFileInformationLayout layout =
            FileHandleIdentityInterop.GetWindowsFileInformationLayoutForTests();

        Assert.Equal(52, layout.Size);

        Assert.Equal(4, layout.CreationTimeOffset);

        Assert.Equal(12, layout.LastAccessTimeOffset);

        Assert.Equal(20, layout.LastWriteTimeOffset);

        Assert.Equal(28, layout.VolumeSerialNumberOffset);

        Assert.Equal(40, layout.NumberOfLinksOffset);

        Assert.Equal(44, layout.FileIndexHighOffset);

        Assert.Equal(48, layout.FileIndexLowOffset);
    }

    /// <summary>
    /// An NTFS file id is a 64-bit file reference whose upper half is zero, and the legacy identity (the
    /// 32-bit volume serial and 64-bit file index) is exactly that id. It must come back unchanged, because
    /// every registered Campaign root digest, marker and journal entry on an NTFS volume was derived from it.
    /// </summary>
    [Fact]
    public void Windows_file_ids_that_fit_in_64_bits_keep_the_legacy_identity()
    {
        FileHandleIdentity legacy = new(0x1234ABCDUL, 0x0001000000000042UL);

        FileHandleIdentity resolved = FileHandleIdentityInterop.ResolveWindowsFileIdInfoIdentity(
            legacy,
            volumeSerialNumber: 0xAAAA00001234ABCDUL,
            fileIdLow: 0x0001000000000042UL,
            fileIdHigh: 0UL);

        Assert.Equal(legacy, resolved);
    }

    /// <summary>
    /// ReFS and Dev Drive file ids are 128-bit. The legacy 64-bit index drops the upper half, so two
    /// different files whose ids differ only there had the same identity; the 128-bit id is what tells
    /// them apart.
    /// </summary>
    [Fact]
    public void Windows_128_bit_file_ids_that_differ_only_in_the_upper_half_have_distinct_identities()
    {
        FileHandleIdentity legacy = new(0x1234ABCDUL, 0x0000000000000007UL);

        FileHandleIdentity first = FileHandleIdentityInterop.ResolveWindowsFileIdInfoIdentity(
            legacy,
            volumeSerialNumber: 0x00000000_1234ABCDUL,
            fileIdLow: 0x0000000000000007UL,
            fileIdHigh: 1UL);

        FileHandleIdentity second = FileHandleIdentityInterop.ResolveWindowsFileIdInfoIdentity(
            legacy,
            volumeSerialNumber: 0x00000000_1234ABCDUL,
            fileIdLow: 0x0000000000000007UL,
            fileIdHigh: 2UL);

        Assert.NotEqual(first, second);

        Assert.NotEqual(legacy, first);

        Assert.NotEqual(legacy, second);

        // The same inputs always give the same identity.
        Assert.Equal(
            first,
            FileHandleIdentityInterop.ResolveWindowsFileIdInfoIdentity(
                legacy,
                volumeSerialNumber: 0x00000000_1234ABCDUL,
                fileIdLow: 0x0000000000000007UL,
                fileIdHigh: 1UL));
    }

    /// <summary>
    /// The volume half of an identity is a property of the volume alone: the same-volume checks compare it
    /// across different files, so a wide id must never leak into it.
    /// </summary>
    [Fact]
    public void Windows_128_bit_file_ids_keep_the_legacy_volume_and_separate_volumes_by_the_64_bit_serial()
    {
        FileHandleIdentity legacy = new(0x1234ABCDUL, 0x0000000000000007UL);

        FileHandleIdentity onVolumeA = FileHandleIdentityInterop.ResolveWindowsFileIdInfoIdentity(
            legacy,
            volumeSerialNumber: 0x00000001_1234ABCDUL,
            fileIdLow: 0x0000000000000007UL,
            fileIdHigh: 5UL);

        FileHandleIdentity onVolumeB = FileHandleIdentityInterop.ResolveWindowsFileIdInfoIdentity(
            legacy,
            volumeSerialNumber: 0x00000002_1234ABCDUL,
            fileIdLow: 0x0000000000000007UL,
            fileIdHigh: 5UL);

        Assert.Equal(legacy.VolumeId, onVolumeA.VolumeId);

        Assert.Equal(legacy.VolumeId, onVolumeB.VolumeId);

        // Two volumes whose 32-bit serials agree but whose 64-bit serials differ no longer share file ids.
        Assert.NotEqual(onVolumeA.FileId, onVolumeB.FileId);
    }

    [Fact]
    public void Windows_file_id_info_layout_matches_native_FILE_ID_INFO()
    {
        WindowsFileIdInfoLayout layout = FileHandleIdentityInterop.GetWindowsFileIdInfoLayoutForTests();

        Assert.Equal(24, layout.Size);

        Assert.Equal(0, layout.VolumeSerialNumberOffset);

        Assert.Equal(8, layout.FileIdLowOffset);

        Assert.Equal(16, layout.FileIdHighOffset);
    }

    /// <summary>
    /// The Windows lane: the identity a handle reports is the legacy identity widened by the handle's own
    /// <c>FileIdInfo</c>, never a different file's. Not run on non-Windows hosts.
    /// </summary>
    [SkippableFact]
    public void Windows_handle_identity_is_resolved_from_FileIdInfo()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "FILE_ID_INFO is a Windows API.");

        using SafeFileHandle handle = File.OpenHandle(_tempFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        Assert.True(FileHandleIdentityInterop.TryGetHandleMetadata(handle, out FileHandleMetadata metadata));

        Assert.True(
            FileHandleIdentityInterop.TryReadWindowsFileIdInfoForTests(
                handle,
                out ulong volumeSerialNumber,
                out ulong fileIdLow,
                out ulong fileIdHigh));

        FileHandleIdentity expected = FileHandleIdentityInterop.ResolveWindowsFileIdInfoIdentity(
            new FileHandleIdentity(volumeSerialNumber & 0xFFFFFFFFUL, metadata.Identity.FileId),
            volumeSerialNumber,
            fileIdLow,
            fileIdHigh);

        if (fileIdHigh == 0)
        {
            // An NTFS volume: the identity is the legacy one, so previously registered roots still match.
            Assert.Equal(fileIdLow, metadata.Identity.FileId);

            Assert.True(metadata.Identity.VolumeId <= uint.MaxValue);
        }
        else
        {
            Assert.Equal(expected.FileId, metadata.Identity.FileId);
        }
    }

    [Fact]
    public void Windows_directory_enumeration_capability_requests_list_access()
    {
        uint metadataOnly =
            FileHandleIdentityInterop.GetWindowsDirectoryDesiredAccessForTests(
                requestReadControl: false,
                requestEnumeration: false);
        uint enumerable =
            FileHandleIdentityInterop.GetWindowsDirectoryDesiredAccessForTests(
                requestReadControl: false,
                requestEnumeration: true);

        Assert.Equal(0u, metadataOnly & 0x0001u);
        Assert.Equal(0x0001u, enumerable & 0x0001u);
        Assert.Equal(0x00100000u, enumerable & 0x00100000u);
    }

    [Fact]
    public void Macos_arm64_metadata_layout_reads_dev_mode_nlink_and_inode()
    {
        byte[] buffer = new byte[144];

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0), 0x01020304U);

        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), 0x81A4);

        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(6), 3);

        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(8), 0x1112131415161718UL);

        Assert.True(
            FileHandleIdentityInterop.TryParseUnixFileMetadataForTests(
                buffer,
                isMacOS: true,
                Architecture.Arm64,
                out FileHandleMetadata metadata));

        Assert.Equal(
            new FileHandleMetadata(
                new FileHandleIdentity(0x01020304UL, 0x1112131415161718UL),
                3UL),
            metadata);
    }

    [Fact]
    public void Macos_x64_layout_is_rejected()
    {
        // The plain `stat` symbol on macOS x64 is the legacy struct with a 32-bit inode, so reading it
        // through the 64-bit-inode offsets would fabricate an identity from the wrong bytes. Only the
        // arm64 layout is read; x64 is not a shipping RID and fails closed.
        byte[] buffer = new byte[144];

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0), 0x01020304U);

        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), 0x81A4);

        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(6), 3);

        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(8), 0x1112131415161718UL);

        Assert.False(
            FileHandleIdentityInterop.TryParseUnixFileMetadataForTests(
                buffer,
                isMacOS: true,
                Architecture.X64,
                out FileHandleMetadata metadata));

        Assert.Equal(default, metadata);
    }

    [Fact]
    public void Linux_x64_metadata_layout_reads_nlink_before_mode()
    {
        byte[] buffer = new byte[32];

        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(0), 0x0102030405060708UL);

        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(8), 0x1112131415161718UL);

        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(16), 7UL);

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(24), 0x81A4U);

        Assert.True(
            FileHandleIdentityInterop.TryParseUnixFileMetadataForTests(
                buffer,
                isMacOS: false,
                Architecture.X64,
                out FileHandleMetadata metadata));

        Assert.Equal(
            new FileHandleMetadata(
                new FileHandleIdentity(0x0102030405060708UL, 0x1112131415161718UL),
                7UL),
            metadata);
    }

    [Fact]
    public void Linux_arm64_metadata_layout_reads_mode_before_nlink()
    {
        byte[] buffer = new byte[32];

        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(0), 0x0102030405060708UL);

        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(8), 0x1112131415161718UL);

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(16), 0x81A4U);

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(20), 9U);

        Assert.True(
            FileHandleIdentityInterop.TryParseUnixFileMetadataForTests(
                buffer,
                isMacOS: false,
                Architecture.Arm64,
                out FileHandleMetadata metadata));

        Assert.Equal(
            new FileHandleMetadata(
                new FileHandleIdentity(0x0102030405060708UL, 0x1112131415161718UL),
                9UL),
            metadata);
    }

    [Fact]
    public void Unix_metadata_layout_classifies_fifo_as_non_regular()
    {
        byte[] buffer = new byte[32];

        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(0), 1UL);

        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(8), 2UL);

        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(16), 1UL);

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(24), 0x11A4U);

        Assert.True(
            FileHandleIdentityInterop.TryParseUnixFileMetadataForTests(
                buffer,
                isMacOS: false,
                Architecture.X64,
                out FileHandleMetadata metadata));

        Assert.Equal(FileSystemObjectKind.Other, metadata.Kind);
    }

    [Fact]
    public void Unix_metadata_layout_fails_closed_for_unsupported_architecture()
    {
        byte[] buffer = new byte[32];

        Assert.False(
            FileHandleIdentityInterop.TryParseUnixFileMetadataForTests(
                buffer,
                isMacOS: false,
                Architecture.Arm,
                out FileHandleMetadata metadata));

        Assert.Equal(default, metadata);
    }

    [Fact]
    public void TryGetPathIdentity_UnknownPlatform_ReturnsFalse()
    {
        FileHandleIdentityInterop.TryGetPathIdentityForTests = _ => null;

        bool result = FileHandleIdentityInterop.TryGetPathIdentity(
            "/nonexistent-platform-path",
            out FileHandleIdentity identity);

        Assert.False(result);

        Assert.Equal(default, identity);
    }

    [SkippableFact]
    public void TryGetHandleKernelPath_ReportsTheCanonicalLocationOfTheOpenFile()
    {
        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux() && !OperatingSystem.IsWindows(),
            "The kernel path query is implemented for macOS, Linux and Windows.");

        using SafeFileHandle handle = File.OpenHandle(_tempFile);

        Assert.True(FileHandleIdentityInterop.TryGetHandleKernelPath(handle, out string? kernelPath));

        Assert.True(WorkspacePathPolicy.TryCanonicalize(Path.GetFullPath(_tempFile), out string? canonical, out bool exists));

        Assert.True(exists);

        Assert.Equal(canonical, kernelPath, ignoreCase: OperatingSystem.IsWindows() || OperatingSystem.IsMacOS());
    }

    [Fact]
    public void TryGetHandleKernelPath_ClosedHandle_ReturnsFalse()
    {
        SafeFileHandle handle = File.OpenHandle(_tempFile);

        handle.Dispose();

        Assert.False(FileHandleIdentityInterop.TryGetHandleKernelPath(handle, out string? kernelPath));

        Assert.Null(kernelPath);
    }

    [Theory]
    [InlineData(@"\\?\C:\work\file.txt", @"C:\work\file.txt")]
    [InlineData(@"\\?\UNC\server\share\file.txt", @"\\server\share\file.txt")]
    [InlineData(@"\\?\unc\server\share\file.txt", @"\\server\share\file.txt")]
    [InlineData(@"C:\already\plain.txt", @"C:\already\plain.txt")]
    public void NormalizeWindowsFinalPath_StripsTheExtendedLengthPrefix(string finalPath, string expected)
    {
        Assert.Equal(expected, FileHandleIdentityInterop.NormalizeWindowsFinalPath(finalPath));
    }
}
