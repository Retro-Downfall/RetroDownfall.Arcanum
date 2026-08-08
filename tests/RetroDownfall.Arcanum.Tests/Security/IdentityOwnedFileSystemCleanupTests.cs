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

    private static void WriteExternalReplacementWithFreshIdentity(string path)
    {
        string staging = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(path))!,
            $".identity-swap-{Guid.NewGuid():N}");

        File.WriteAllText(staging, "external replacement");

        File.Move(staging, path, overwrite: true);
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
                // Swap via overwrite-rename from a staged file so the replacement deterministically
                // carries a fresh (dev, ino) identity — ext4/tmpfs immediately recycle the inode of
                // an in-place delete+create, which would make the swap invisible to the cleanup.
                WriteExternalReplacementWithFreshIdentity(file);

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
                    WriteExternalReplacementWithFreshIdentity(path);
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

    [Fact]
    public void TryQuarantine_preserves_name_and_can_restore_the_owned_file()
    {
        IdentityOwnedFileSystemArtifact artifact =
            CreateCapturedFile("reversible-cleanup.tmp");

        Assert.True(
            IdentityOwnedFileSystemCleanup.TryQuarantine(
                artifact,
                out IdentityOwnedFileSystemQuarantine quarantine));

        Assert.Equal(
            Path.GetFileName(artifact.Path),
            Path.GetFileName(quarantine.Quarantined.Path));

        Assert.False(File.Exists(artifact.Path));

        Assert.True(File.Exists(quarantine.Quarantined.Path));

        Assert.True(
            IdentityOwnedFileSystemCleanup.TryRestoreQuarantined(
                quarantine));

        Assert.True(File.Exists(artifact.Path));

        Assert.Equal("owned", File.ReadAllText(artifact.Path));

        Assert.False(Directory.Exists(quarantine.Directory.Path));
    }

    [Fact]
    public void TryQuarantine_exposes_post_move_artifact_for_retryable_restore()
    {
        IdentityOwnedFileSystemArtifact artifact =
            CreateCapturedFile("post-move-retry.tmp");

        int fileMetadataReads = 0;

        FileHandleIdentityInterop
            .TryGetPathMetadataNoFollowForTests = path =>
            {
                if (Directory.Exists(path))
                {
                    return ReadActualMetadata(path);
                }

                fileMetadataReads++;

                return fileMetadataReads == 1
                    ? artifact.Metadata
                    : null;
            };

        Assert.False(
            IdentityOwnedFileSystemCleanup.TryQuarantine(
                artifact,
                out IdentityOwnedFileSystemQuarantine quarantine));

        Assert.NotEqual(default, quarantine);

        Assert.False(File.Exists(artifact.Path));

        FileHandleIdentityInterop
            .TryGetPathMetadataNoFollowForTests = null;

        Assert.True(
            IdentityOwnedFileSystemCleanup.TryRestoreQuarantined(
                quarantine));

        Assert.True(File.Exists(artifact.Path));
    }

    [Fact]
    public void TryDeleteQuarantined_finalizes_the_owned_file()
    {
        IdentityOwnedFileSystemArtifact artifact =
            CreateCapturedFile("finalized-cleanup.tmp");

        Assert.True(
            IdentityOwnedFileSystemCleanup.TryQuarantine(
                artifact,
                out IdentityOwnedFileSystemQuarantine quarantine));

        Assert.True(
            IdentityOwnedFileSystemCleanup.TryDeleteQuarantined(
                quarantine));

        Assert.False(File.Exists(artifact.Path));

        Assert.False(File.Exists(quarantine.Quarantined.Path));

        Assert.False(Directory.Exists(quarantine.Directory.Path));
    }

    [Theory]
    [InlineData("cleanup-")]
    [InlineData(".arcanum-cleanup")]
    [InlineData(".arcanum-nested/cleanup-")]
    [InlineData(".arcanum-cleanup -")]
    public void TryQuarantine_rejects_unsafe_quarantine_directory_prefixes(
        string quarantineDirectoryPrefix)
    {
        IdentityOwnedFileSystemArtifact artifact =
            CreateCapturedFile("unsafe-prefix.tmp");

        Assert.False(
            IdentityOwnedFileSystemCleanup.TryQuarantine(
                artifact,
                quarantineDirectoryPrefix,
                out IdentityOwnedFileSystemQuarantine quarantine));

        Assert.Equal(default, quarantine);

        Assert.True(File.Exists(artifact.Path));

        Assert.Equal("owned", File.ReadAllText(artifact.Path));

        Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public void TryQuarantine_accepts_a_safe_custom_quarantine_directory_prefix()
    {
        IdentityOwnedFileSystemArtifact artifact =
            CreateCapturedFile("safe-prefix.tmp");

        Assert.True(
            IdentityOwnedFileSystemCleanup.TryQuarantine(
                artifact,
                ".arcanum-custom-",
                out IdentityOwnedFileSystemQuarantine quarantine));

        Assert.False(File.Exists(artifact.Path));

        Assert.StartsWith(
            ".arcanum-custom-",
            Path.GetFileName(quarantine.Directory.Path),
            StringComparison.Ordinal);

        Assert.True(
            IdentityOwnedFileSystemCleanup.TryRestoreQuarantined(
                quarantine));

        Assert.Equal("owned", File.ReadAllText(artifact.Path));
    }

    [Fact]
    public void TryQuarantine_restores_the_owned_file_when_post_move_metadata_is_unavailable()
    {
        IdentityOwnedFileSystemArtifact artifact =
            CreateCapturedFile("post-move-restored.tmp");

        bool suppressedPostMoveRead = false;

        FileHandleIdentityInterop
            .TryGetPathMetadataNoFollowForTests = path =>
            {
                if (!suppressedPostMoveRead
                    && IsInsideQuarantineDirectory(path))
                {
                    suppressedPostMoveRead = true;

                    return null;
                }

                return ReadActualMetadata(path);
            };

        Assert.False(
            IdentityOwnedFileSystemCleanup.TryQuarantine(
                artifact,
                out IdentityOwnedFileSystemQuarantine quarantine));

        Assert.True(suppressedPostMoveRead);

        Assert.Equal(default, quarantine);

        Assert.True(File.Exists(artifact.Path));

        Assert.Equal("owned", File.ReadAllText(artifact.Path));

        Assert.Empty(
            Directory.GetDirectories(
                _root,
                ".arcanum-cleanup-*"));
    }

    [Fact]
    public void TryQuarantine_restores_the_owned_file_when_post_move_identity_does_not_match()
    {
        IdentityOwnedFileSystemArtifact artifact =
            CreateCapturedFile("post-move-mismatch-restored.tmp");

        bool reportedMismatch = false;

        FileHandleIdentityInterop
            .TryGetPathMetadataNoFollowForTests = path =>
            {
                if (!reportedMismatch
                    && IsInsideQuarantineDirectory(path))
                {
                    reportedMismatch = true;

                    return ForeignRegularFileMetadata;
                }

                return ReadActualMetadata(path);
            };

        Assert.False(
            IdentityOwnedFileSystemCleanup.TryQuarantine(
                artifact,
                out IdentityOwnedFileSystemQuarantine quarantine));

        Assert.True(reportedMismatch);

        Assert.Equal(default, quarantine);

        Assert.True(File.Exists(artifact.Path));

        Assert.Equal("owned", File.ReadAllText(artifact.Path));

        Assert.Empty(
            Directory.GetDirectories(
                _root,
                ".arcanum-cleanup-*"));
    }

    [Fact]
    public void TryQuarantine_retains_quarantine_when_mismatched_artifact_cannot_be_restored()
    {
        IdentityOwnedFileSystemArtifact artifact =
            CreateCapturedFile("post-move-mismatch-retained.tmp");

        FileHandleIdentityInterop
            .TryGetPathMetadataNoFollowForTests = path =>
                IsInsideQuarantineDirectory(path)
                    ? ForeignRegularFileMetadata
                    : ReadActualMetadata(path);

        Assert.False(
            IdentityOwnedFileSystemCleanup.TryQuarantine(
                artifact,
                out IdentityOwnedFileSystemQuarantine quarantine));

        Assert.NotEqual(default, quarantine);

        Assert.Equal(
            artifact.Metadata,
            quarantine.Quarantined.Metadata);

        Assert.False(File.Exists(artifact.Path));

        string quarantineDirectory = Assert.Single(
            Directory.GetDirectories(
                _root,
                ".arcanum-cleanup-*"));

        string quarantined = Assert.Single(
            Directory.GetFiles(quarantineDirectory));

        Assert.Equal("owned", File.ReadAllText(quarantined));
    }

    [Fact]
    public void TryRestoreQuarantined_refuses_to_overwrite_a_repopulated_original_path()
    {
        IdentityOwnedFileSystemArtifact artifact =
            CreateCapturedFile("restore-occupied.tmp");

        Assert.True(
            IdentityOwnedFileSystemCleanup.TryQuarantine(
                artifact,
                out IdentityOwnedFileSystemQuarantine quarantine));

        File.WriteAllText(artifact.Path, "squatter");

        Assert.False(
            IdentityOwnedFileSystemCleanup.TryRestoreQuarantined(
                quarantine));

        Assert.Equal("squatter", File.ReadAllText(artifact.Path));

        Assert.True(File.Exists(quarantine.Quarantined.Path));

        Assert.Equal(
            "owned",
            File.ReadAllText(quarantine.Quarantined.Path));
    }

    [Fact]
    public void TryRestoreQuarantined_refuses_when_original_path_holds_an_unidentifiable_file()
    {
        IdentityOwnedFileSystemArtifact artifact =
            CreateCapturedFile("restore-unidentifiable-file.tmp");

        Assert.True(
            IdentityOwnedFileSystemCleanup.TryQuarantine(
                artifact,
                out IdentityOwnedFileSystemQuarantine quarantine));

        File.WriteAllText(artifact.Path, "squatter");

        FileHandleIdentityInterop
            .TryGetPathMetadataNoFollowForTests = path =>
                string.Equals(
                    path,
                    artifact.Path,
                    StringComparison.Ordinal)
                    ? null
                    : ReadActualMetadata(path);

        Assert.False(
            IdentityOwnedFileSystemCleanup.TryRestoreQuarantined(
                quarantine));

        Assert.Equal("squatter", File.ReadAllText(artifact.Path));

        Assert.True(File.Exists(quarantine.Quarantined.Path));
    }

    [Fact]
    public void TryRestoreQuarantined_refuses_when_original_path_holds_an_unidentifiable_directory()
    {
        IdentityOwnedFileSystemArtifact artifact =
            CreateCapturedFile("restore-unidentifiable-directory.tmp");

        Assert.True(
            IdentityOwnedFileSystemCleanup.TryQuarantine(
                artifact,
                out IdentityOwnedFileSystemQuarantine quarantine));

        Directory.CreateDirectory(artifact.Path);

        FileHandleIdentityInterop
            .TryGetPathMetadataNoFollowForTests = path =>
                string.Equals(
                    path,
                    artifact.Path,
                    StringComparison.Ordinal)
                    ? null
                    : ReadActualMetadata(path);

        Assert.False(
            IdentityOwnedFileSystemCleanup.TryRestoreQuarantined(
                quarantine));

        Assert.True(Directory.Exists(artifact.Path));

        Assert.True(File.Exists(quarantine.Quarantined.Path));
    }

    [Fact]
    public void TryRestoreQuarantined_reports_failure_when_restored_metadata_is_unavailable()
    {
        IdentityOwnedFileSystemArtifact artifact =
            CreateCapturedFile("restore-unverifiable.tmp");

        Assert.True(
            IdentityOwnedFileSystemCleanup.TryQuarantine(
                artifact,
                out IdentityOwnedFileSystemQuarantine quarantine));

        FileHandleIdentityInterop
            .TryGetPathMetadataNoFollowForTests = path =>
                string.Equals(
                    path,
                    artifact.Path,
                    StringComparison.Ordinal)
                    ? null
                    : ReadActualMetadata(path);

        Assert.False(
            IdentityOwnedFileSystemCleanup.TryRestoreQuarantined(
                quarantine));

        Assert.True(Directory.Exists(quarantine.Directory.Path));
    }

    [Fact]
    public void TryRestoreQuarantined_reports_failure_when_restored_identity_does_not_match()
    {
        IdentityOwnedFileSystemArtifact artifact =
            CreateCapturedFile("restore-identity-mismatch.tmp");

        Assert.True(
            IdentityOwnedFileSystemCleanup.TryQuarantine(
                artifact,
                out IdentityOwnedFileSystemQuarantine quarantine));

        int originalPathReads = 0;

        FileHandleIdentityInterop
            .TryGetPathMetadataNoFollowForTests = path =>
            {
                if (!string.Equals(
                        path,
                        artifact.Path,
                        StringComparison.Ordinal))
                {
                    return ReadActualMetadata(path);
                }

                originalPathReads++;

                return originalPathReads == 1
                    ? null
                    : ForeignRegularFileMetadata;
            };

        Assert.False(
            IdentityOwnedFileSystemCleanup.TryRestoreQuarantined(
                quarantine));

        Assert.Equal(2, originalPathReads);

        Assert.True(Directory.Exists(quarantine.Directory.Path));
    }

    private static FileHandleMetadata ForeignRegularFileMetadata =>
        new(
            new FileHandleIdentity(
                ulong.MaxValue,
                ulong.MaxValue),
            HardLinkCount: 1,
            FileSystemObjectKind.RegularFile);

    private static bool IsInsideQuarantineDirectory(string path) =>
        Path.GetFileName(Path.GetDirectoryName(path))
            ?.StartsWith(
                ".arcanum-cleanup-",
                StringComparison.Ordinal)
        == true;

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
