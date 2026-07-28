using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

[Collection("WorkspacePathPolicy")]
public sealed class IdentityOwnedFileSystemCleanupTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(
            Path.GetTempPath(),
            "arcanum-owned-cleanup-"
            + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        FileHandleIdentityInterop
            .TryGetPathMetadataNoFollowForTests = null;

        FileHandleIdentityInterop.TryGetHandleMetadataForTests = null;

        SecureFilePermissions.StrictOwnerOnlyVerificationForTests =
            null;

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void TryCapturePath_rejects_blank_unavailable_and_wrong_kind()
    {
        Assert.False(
            IdentityOwnedFileSystemCleanup.TryCapturePath(
                " ",
                FileSystemObjectKind.RegularFile,
                out _));

        string file = Path.Combine(_root, "artifact.tmp");

        File.WriteAllText(file, "owned");

        FileHandleIdentityInterop
            .TryGetPathMetadataNoFollowForTests = _ => null;

        Assert.False(
            IdentityOwnedFileSystemCleanup.TryCapturePath(
                file,
                FileSystemObjectKind.RegularFile,
                out _));

        FileHandleIdentityInterop
            .TryGetPathMetadataNoFollowForTests = null;

        Assert.False(
            IdentityOwnedFileSystemCleanup.TryCapturePath(
                file,
                FileSystemObjectKind.Directory,
                out _));
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(2UL)]
    public void TryCapturePath_rejects_nonexclusive_regular_link_count(
        ulong hardLinkCount)
    {
        string file = Path.Combine(_root, "links.tmp");

        File.WriteAllText(file, "owned");

        FileHandleIdentityInterop
            .TryGetPathMetadataNoFollowForTests = _ =>
            new FileHandleMetadata(
                new FileHandleIdentity(1, 2),
                hardLinkCount,
                FileSystemObjectKind.RegularFile);

        Assert.False(
            IdentityOwnedFileSystemCleanup.TryCapturePath(
                file,
                FileSystemObjectKind.RegularFile,
                out _));
    }

    [Fact]
    public void TryCaptureOpenFile_rejects_blank_unavailable_and_wrong_kind()
    {
        string file = Path.Combine(_root, "opened.tmp");

        File.WriteAllText(file, "owned");

        using FileStream stream = File.OpenRead(file);

        Assert.False(
            IdentityOwnedFileSystemCleanup.TryCaptureOpenFile(
                " ",
                stream.SafeFileHandle,
                out _));

        FileHandleIdentityInterop.TryGetHandleMetadataForTests =
            _ => null;

        Assert.False(
            IdentityOwnedFileSystemCleanup.TryCaptureOpenFile(
                file,
                stream.SafeFileHandle,
                out _));

        FileHandleIdentityInterop.TryGetHandleMetadataForTests =
            _ =>
                new FileHandleMetadata(
                    new FileHandleIdentity(1, 2),
                    HardLinkCount: 1,
                    Kind: FileSystemObjectKind.Directory);

        Assert.False(
            IdentityOwnedFileSystemCleanup.TryCaptureOpenFile(
                file,
                stream.SafeFileHandle,
                out _));
    }

    [Fact]
    public void TryDelete_rejects_blank_and_unknown_object_kind()
    {
        Assert.False(
            IdentityOwnedFileSystemCleanup.TryDelete(default));

        string path = Path.Combine(_root, "other");

        FileHandleMetadata metadata = new(
            new FileHandleIdentity(3, 4),
            HardLinkCount: 1,
            FileSystemObjectKind.Other);

        FileHandleIdentityInterop
            .TryGetPathMetadataNoFollowForTests = _ => metadata;

        Assert.False(
            IdentityOwnedFileSystemCleanup.TryDelete(
                new IdentityOwnedFileSystemArtifact(
                    path,
                    metadata)));

        FileHandleMetadata directoryMetadata = metadata with
        {
            Kind = FileSystemObjectKind.Directory,
        };

        FileHandleIdentityInterop
            .TryGetPathMetadataNoFollowForTests = _ =>
            directoryMetadata;

        Assert.False(
            IdentityOwnedFileSystemCleanup.TryDelete(
                new IdentityOwnedFileSystemArtifact(
                    Path.GetPathRoot(_root)!,
                    directoryMetadata)));

        FileHandleMetadata singleLink = metadata with
        {
            HardLinkCount = 1,
            Kind = FileSystemObjectKind.RegularFile,
        };

        FileHandleIdentityInterop
            .TryGetPathMetadataNoFollowForTests = _ =>
            singleLink;

        Assert.False(
            IdentityOwnedFileSystemCleanup.TryDelete(
                new IdentityOwnedFileSystemArtifact(
                    path,
                    singleLink with { HardLinkCount = 2 })));

        FileHandleIdentityInterop
            .TryGetPathMetadataNoFollowForTests = _ =>
            singleLink with { HardLinkCount = 2 };

        Assert.False(
            IdentityOwnedFileSystemCleanup.TryDelete(
                new IdentityOwnedFileSystemArtifact(
                    path,
                    singleLink)));
    }

    [Fact]
    public void TryDelete_with_unavailable_metadata_distinguishes_existing_and_missing_paths()
    {
        string file = Path.Combine(_root, "existing.tmp");

        string directory = Directory.CreateDirectory(
            Path.Combine(_root, "existing-dir")).FullName;

        string missing = Path.Combine(_root, "missing.tmp");

        File.WriteAllText(file, "owned");

        FileHandleIdentityInterop
            .TryGetPathMetadataNoFollowForTests = _ => null;

        IdentityOwnedFileSystemArtifact fileArtifact = new(
            file,
            default);

        IdentityOwnedFileSystemArtifact directoryArtifact = new(
            directory,
            default);

        IdentityOwnedFileSystemArtifact missingArtifact = new(
            missing,
            default);

        Assert.False(
            IdentityOwnedFileSystemCleanup.TryDelete(
                fileArtifact));

        Assert.False(
            IdentityOwnedFileSystemCleanup.TryDelete(
                directoryArtifact));

        Assert.True(
            IdentityOwnedFileSystemCleanup.TryDelete(
                missingArtifact));
    }

    [Fact]
    public void TryDelete_reports_incomplete_when_path_still_has_metadata_after_delete()
    {
        string file = Path.Combine(
            _root,
            "post-delete-replacement.tmp");

        File.WriteAllText(file, "owned");

        Assert.True(
            FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                file,
                out FileHandleMetadata metadata));

        FileHandleIdentityInterop
            .TryGetPathMetadataNoFollowForTests = path =>
            {
                if (string.Equals(
                        path,
                        file,
                        StringComparison.Ordinal)
                    || File.Exists(path)
                    || Path.GetFileName(
                            Path.GetDirectoryName(path))
                        ?.StartsWith(
                            ".arcanum-cleanup-",
                            StringComparison.Ordinal)
                    == true)
                {
                    return metadata;
                }

                return ReadActualMetadata(path);
            };

        Assert.False(
            IdentityOwnedFileSystemCleanup.TryDelete(
                new IdentityOwnedFileSystemArtifact(
                    file,
                    metadata)));

        Assert.False(File.Exists(file));
    }

    [Fact]
    public void TryDelete_deletes_captured_directory_after_nested_directory_changes_link_count()
    {
        string directory = Directory.CreateDirectory(
            Path.Combine(_root, "owned-directory")).FullName;

        Assert.True(
            IdentityOwnedFileSystemCleanup.TryCapturePath(
                directory,
                FileSystemObjectKind.Directory,
                out IdentityOwnedFileSystemArtifact artifact));

        string nested = Directory.CreateDirectory(
            Path.Combine(directory, "nested")).FullName;

        File.WriteAllText(
            Path.Combine(nested, "temporary.txt"),
            "owned");

        Assert.True(
            IdentityOwnedFileSystemCleanup.TryDelete(
                artifact));

        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void TryDelete_retains_replacement_swapped_during_precheck()
    {
        string file = Path.Combine(
            _root,
            "precheck-swap.tmp");

        File.WriteAllText(file, "owned");

        Assert.True(
            IdentityOwnedFileSystemCleanup.TryCapturePath(
                file,
                FileSystemObjectKind.RegularFile,
                out IdentityOwnedFileSystemArtifact artifact));

        FileHandleIdentityInterop
            .TryGetPathMetadataNoFollowForTests = _ =>
            {
                File.Delete(file);

                File.WriteAllText(
                    file,
                    "external replacement");

                FileHandleIdentityInterop
                    .TryGetPathMetadataNoFollowForTests = null;

                return artifact.Metadata;
            };

        Assert.False(
            IdentityOwnedFileSystemCleanup.TryDelete(
                artifact));

        Assert.False(File.Exists(file));

        string quarantineDirectory = Assert.Single(
            Directory.GetDirectories(
                _root,
                ".arcanum-cleanup-*"));

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute,
                File.GetUnixFileMode(
                    quarantineDirectory));
        }

        string replacement = Assert.Single(
            Directory.GetFiles(
                quarantineDirectory));

        Assert.Equal(
            "external replacement",
            File.ReadAllText(replacement));
    }

    [Fact]
    public void TryDelete_retains_quarantine_when_post_move_metadata_is_unavailable()
    {
        string file = Path.Combine(
            _root,
            "post-move-unknown.tmp");

        File.WriteAllText(file, "owned");

        Assert.True(
            IdentityOwnedFileSystemCleanup.TryCapturePath(
                file,
                FileSystemObjectKind.RegularFile,
                out IdentityOwnedFileSystemArtifact artifact));

        int metadataReads = 0;

        FileHandleIdentityInterop
            .TryGetPathMetadataNoFollowForTests = path =>
            {
                if (Directory.Exists(path))
                {
                    return ReadActualMetadata(path);
                }

                metadataReads++;

                return metadataReads == 1
                    ? artifact.Metadata
                    : null;
            };

        Assert.False(
            IdentityOwnedFileSystemCleanup.TryDelete(
                artifact));

        Assert.False(File.Exists(file));

        string quarantineDirectory = Assert.Single(
            Directory.GetDirectories(
                _root,
                ".arcanum-cleanup-*"));

        string quarantine = Assert.Single(
            Directory.GetFiles(
                quarantineDirectory));

        Assert.Equal(
            "owned",
            File.ReadAllText(quarantine));
    }

    [Fact]
    public void TryDelete_does_not_delete_replacement_swapped_after_revalidation()
    {
        string file = Path.Combine(
            _root,
            "post-revalidation-swap.tmp");

        File.WriteAllText(file, "owned");

        Assert.True(
            IdentityOwnedFileSystemCleanup.TryCapturePath(
                file,
                FileSystemObjectKind.RegularFile,
                out IdentityOwnedFileSystemArtifact artifact));

        bool swapped = false;

        FileHandleIdentityInterop
            .TryGetPathMetadataNoFollowForTests = path =>
            {
                if (string.Equals(
                        path,
                        file,
                        StringComparison.Ordinal))
                {
                    return artifact.Metadata;
                }

                FileHandleMetadata? actual =
                    ReadActualMetadata(path);

                if (!swapped
                    && actual is
                    {
                        Kind:
                            FileSystemObjectKind.RegularFile,
                    })
                {
                    File.Delete(path);
                    File.WriteAllText(
                        path,
                        "external replacement");
                    swapped = true;

                    return artifact.Metadata;
                }

                return actual;
            };

        Assert.False(
            IdentityOwnedFileSystemCleanup.TryDelete(
                artifact));

        Assert.True(swapped);

        string quarantineDirectory = Assert.Single(
            Directory.GetDirectories(
                _root,
                ".arcanum-cleanup-*"));

        string replacement = Assert.Single(
            Directory.GetFiles(
                quarantineDirectory,
                "*",
                SearchOption.AllDirectories));

        Assert.Equal(
            "external replacement",
            File.ReadAllText(replacement));
    }

    [Fact]
    public void TryDelete_retains_unproven_quarantine_directory_when_capture_fails()
    {
        IdentityOwnedFileSystemArtifact artifact =
            CreateCapturedFile("quarantine-capture-failure.tmp");

        FileHandleIdentityInterop
            .TryGetPathMetadataNoFollowForTests = path =>
                string.Equals(
                    path,
                    artifact.Path,
                    StringComparison.Ordinal)
                    ? artifact.Metadata
                    : null;

        Assert.False(
            IdentityOwnedFileSystemCleanup.TryDelete(
                artifact));

        Assert.Single(
            Directory.GetDirectories(
                _root,
                ".arcanum-cleanup-*"));
    }

    [Fact]
    public void TryDelete_removes_empty_quarantine_when_owner_only_verification_fails()
    {
        IdentityOwnedFileSystemArtifact artifact =
            CreateCapturedFile("quarantine-permissions-failure.tmp");

        SecureFilePermissions.StrictOwnerOnlyVerificationForTests =
            (_, _) => false;

        Assert.False(
            IdentityOwnedFileSystemCleanup.TryDelete(
                artifact));

        Assert.True(File.Exists(artifact.Path));

        Assert.Empty(
            Directory.GetDirectories(
                _root,
                ".arcanum-cleanup-*"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryDelete_retains_quarantine_when_directory_identity_cannot_be_proven(
        bool returnMismatchedIdentity)
    {
        IdentityOwnedFileSystemArtifact artifact =
            CreateCapturedFile("quarantine-identity-failure.tmp");

        int quarantineMetadataReads = 0;

        FileHandleMetadata mismatch = new(
            new FileHandleIdentity(
                long.MaxValue,
                long.MaxValue),
            HardLinkCount: 1,
            FileSystemObjectKind.Directory);

        FileHandleIdentityInterop
            .TryGetPathMetadataNoFollowForTests = path =>
            {
                if (string.Equals(
                        path,
                        artifact.Path,
                        StringComparison.Ordinal))
                {
                    return artifact.Metadata;
                }

                quarantineMetadataReads++;

                if (quarantineMetadataReads == 1)
                {
                    return ReadActualMetadata(path);
                }

                return returnMismatchedIdentity
                    ? mismatch
                    : null;
            };

        Assert.False(
            IdentityOwnedFileSystemCleanup.TryDelete(
                artifact));

        Assert.True(File.Exists(artifact.Path));

        Assert.Single(
            Directory.GetDirectories(
                _root,
                ".arcanum-cleanup-*"));
    }

    [Fact]
    public void TryDelete_retains_artifact_when_final_quarantine_identity_is_unavailable()
    {
        IdentityOwnedFileSystemArtifact artifact =
            CreateCapturedFile("final-identity-failure.tmp");

        int artifactMetadataReads = 0;

        FileHandleIdentityInterop
            .TryGetPathMetadataNoFollowForTests = path =>
            {
                if (Directory.Exists(path))
                {
                    return ReadActualMetadata(path);
                }

                artifactMetadataReads++;

                return artifactMetadataReads <= 2
                    ? artifact.Metadata
                    : null;
            };

        Assert.False(
            IdentityOwnedFileSystemCleanup.TryDelete(
                artifact));

        Assert.False(File.Exists(artifact.Path));

        string quarantineDirectory = Assert.Single(
            Directory.GetDirectories(
                _root,
                ".arcanum-cleanup-*"));

        Assert.Single(
            Directory.GetFiles(
                quarantineDirectory));
    }

    private IdentityOwnedFileSystemArtifact CreateCapturedFile(
        string fileName)
    {
        string path = Path.Combine(
            _root,
            fileName);

        File.WriteAllText(path, "owned");

        Assert.True(
            IdentityOwnedFileSystemCleanup.TryCapturePath(
                path,
                FileSystemObjectKind.RegularFile,
                out IdentityOwnedFileSystemArtifact artifact));

        return artifact;
    }

    private static FileHandleMetadata? ReadActualMetadata(
        string path)
    {
        Func<string, FileHandleMetadata?>? seam =
            FileHandleIdentityInterop
                .TryGetPathMetadataNoFollowForTests;

        FileHandleIdentityInterop
            .TryGetPathMetadataNoFollowForTests = null;

        try
        {
            return FileHandleIdentityInterop
                .TryGetPathMetadataNoFollow(
                    path,
                    out FileHandleMetadata metadata)
                ? metadata
                : null;
        }
        finally
        {
            FileHandleIdentityInterop
                .TryGetPathMetadataNoFollowForTests = seam;
        }
    }
}
