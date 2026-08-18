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

}
