using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

[Collection("WorkspacePathPolicy")]
public sealed class EncryptedBlobPresenceInspectorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "arcanum-encrypted-blob-presence-tests",
        Guid.NewGuid().ToString("N"));

    private string Attachments => Path.Combine(_root, "attachments");

    private string Files => Path.Combine(_root, "files");

    [Fact]
    public void Missing_managed_roots_are_proven_absent()
    {
        EncryptedBlobPresenceInspector inspector = new(Attachments, Files);

        EncryptedBlobPresence result = inspector.Inspect();

        Assert.Equal(EncryptedBlobPresence.Absent, result);
    }

    [Fact]
    public void Envelope_in_a_managed_root_is_present()
    {
        Directory.CreateDirectory(Path.Combine(Attachments, "session"));
        File.WriteAllBytes(
            Path.Combine(Attachments, "session", "attachment.bin"),
            [.. "ARCABLOB"u8, 2, 1, 0, 0]);

        EncryptedBlobPresenceInspector inspector = new(Attachments, Files);

        EncryptedBlobPresence result = inspector.Inspect();

        Assert.Equal(EncryptedBlobPresence.Present, result);
    }

    [Fact]
    public void Legacy_plaintext_candidates_are_proven_absent()
    {
        Directory.CreateDirectory(Attachments);
        Directory.CreateDirectory(Files);
        File.WriteAllText(Path.Combine(Attachments, "attachment.bin"), "legacy plaintext");
        File.WriteAllBytes(Path.Combine(Files, "upload.bin"), [1, 2, 3, 4]);

        EncryptedBlobPresenceInspector inspector = new(Attachments, Files);

        EncryptedBlobPresence result = inspector.Inspect();

        Assert.Equal(EncryptedBlobPresence.Absent, result);
    }

    [Fact]
    public void Files_root_scans_only_its_existing_top_level_accounting_scope()
    {
        Directory.CreateDirectory(Attachments);
        Directory.CreateDirectory(Path.Combine(Files, "not-a-managed-file"));
        File.WriteAllBytes(
            Path.Combine(Files, "not-a-managed-file", "nested-envelope.bin"),
            [.. "ARCABLOB"u8, 2, 1, 0, 0]);

        EncryptedBlobPresenceInspector inspector = new(Attachments, Files);

        EncryptedBlobPresence result = inspector.Inspect();

        Assert.Equal(EncryptedBlobPresence.Absent, result);
    }

    [Fact]
    public void Cancellation_is_never_downgraded_to_indeterminate()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        EncryptedBlobPresenceInspector inspector = new(Attachments, Files);

        Assert.Throws<OperationCanceledException>(() => inspector.Inspect(cancellation.Token));
    }

    [SkippableFact]
    public void Symbolic_link_managed_root_is_indeterminate_without_following_it()
    {
        Directory.CreateDirectory(_root);
        string outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(
            Path.Combine(outside, "envelope.bin"),
            [.. "ARCABLOB"u8, 2, 1, 0, 0]);

        Skip.IfNot(
            TryCreateDirectorySymbolicLink(Attachments, outside),
            "Symbolic links are unavailable on this runner.");

        EncryptedBlobPresenceInspector inspector = new(Attachments, Files);

        EncryptedBlobPresence result = inspector.Inspect();

        Assert.Equal(EncryptedBlobPresence.Indeterminate, result);
    }

    [SkippableFact]
    public void Ancestor_swapped_to_a_symbolic_link_between_check_and_open_is_indeterminate()
    {
        Directory.CreateDirectory(_root);
        string outside = Path.Combine(_root, "outside");
        string outsideAttachments = Path.Combine(outside, "attachments");
        Directory.CreateDirectory(outsideAttachments);
        File.WriteAllBytes(
            Path.Combine(outsideAttachments, "envelope.bin"),
            [.. "ARCABLOB"u8, 2, 1, 0, 0]);

        string linkedAncestor = Path.Combine(_root, "linked-ancestor");
        Skip.IfNot(
            TryCreateDirectorySymbolicLink(linkedAncestor, outside),
            "Symbolic links are unavailable on this runner.");

        string attachmentsThroughLink = Path.Combine(linkedAncestor, "attachments");
        FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests = path =>
            string.Equals(
                Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)),
                "linked-ancestor",
                StringComparison.Ordinal)
                ? new FileHandleMetadata(
                    new FileHandleIdentity(0xA11CE, 0xBADC0DE),
                    HardLinkCount: 1,
                    FileSystemObjectKind.Directory)
                : FileHandleIdentityInterop.TryGetPathMetadataNoFollowIgnoringTestSeam(
                    path,
                    out FileHandleMetadata metadata)
                    ? metadata
                    : null;

        try
        {
            EncryptedBlobPresenceInspector inspector = new(
                attachmentsThroughLink,
                Files);

            EncryptedBlobPresence result = inspector.Inspect();

            Assert.Equal(EncryptedBlobPresence.Indeterminate, result);
        }
        finally
        {
            FileHandleIdentityInterop.TryGetPathMetadataNoFollowForTests = null;
        }
    }

    [SkippableFact]
    public void Symbolic_link_candidate_is_indeterminate_without_following_it()
    {
        Directory.CreateDirectory(Attachments);
        Directory.CreateDirectory(Files);
        string target = Path.Combine(_root, "outside-envelope.bin");
        File.WriteAllBytes(target, [.. "ARCABLOB"u8, 2, 1, 0, 0]);
        string link = Path.Combine(Attachments, "linked-envelope.bin");

        Skip.IfNot(TryCreateSymbolicLink(link, target), "Symbolic links are unavailable on this runner.");

        EncryptedBlobPresenceInspector inspector = new(Attachments, Files);

        EncryptedBlobPresence result = inspector.Inspect();

        Assert.Equal(EncryptedBlobPresence.Indeterminate, result);
    }

    [SkippableFact]
    public void Definite_envelope_dominates_an_indeterminate_candidate()
    {
        Directory.CreateDirectory(Attachments);
        Directory.CreateDirectory(Files);
        string target = Path.Combine(_root, "outside-legacy.bin");
        File.WriteAllText(target, "legacy plaintext");
        string link = Path.Combine(Attachments, "a-linked-legacy.bin");

        Skip.IfNot(TryCreateSymbolicLink(link, target), "Symbolic links are unavailable on this runner.");

        File.WriteAllBytes(
            Path.Combine(Attachments, "z-encrypted.bin"),
            [.. "ARCABLOB"u8, 2, 1, 0, 0]);

        EncryptedBlobPresenceInspector inspector = new(Attachments, Files);

        EncryptedBlobPresence result = inspector.Inspect();

        Assert.Equal(EncryptedBlobPresence.Present, result);
    }

    [SkippableFact]
    public void Unreadable_managed_directory_is_indeterminate()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip.If(true, "Unix mode bits provide the deterministic denial used by this test.");

            return;
        }

        Directory.CreateDirectory(Attachments);
        File.WriteAllText(Path.Combine(Attachments, "candidate.bin"), "legacy plaintext");
        Directory.CreateDirectory(Files);

        File.SetUnixFileMode(Attachments, UnixFileMode.None);

        try
        {
            EncryptedBlobPresenceInspector inspector = new(Attachments, Files);

            EncryptedBlobPresence result = inspector.Inspect();

            Assert.Equal(EncryptedBlobPresence.Indeterminate, result);
        }
        finally
        {
            File.SetUnixFileMode(
                Attachments,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Theory]
    [InlineData(unchecked((int)0xC000000F), true, 0, true)]
    [InlineData(unchecked((int)0xC000000F), false, 0, false)]
    [InlineData(unchecked((int)0xC000000F), true, 1, false)]
    [InlineData(unchecked((int)0x80000006), false, 1, true)]
    [InlineData(unchecked((int)0xC0000022), true, 0, false)]
    [InlineData(0, true, 0, false)]
    public void Windows_directory_enumeration_accepts_no_such_file_only_for_an_empty_initial_query(
        int status,
        bool initialQuery,
        int observedNameCount,
        bool expected)
    {
        Assert.Equal(
            expected,
            SecureDirectoryNameEnumerator.IsWindowsDirectoryEnumerationComplete(
                status,
                initialQuery,
                observedNameCount));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static bool TryCreateSymbolicLink(string link, string target)
    {
        try
        {
            _ = File.CreateSymbolicLink(link, target);

            return true;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool TryCreateDirectorySymbolicLink(string link, string target)
    {
        try
        {
            _ = Directory.CreateSymbolicLink(link, target);

            return true;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
