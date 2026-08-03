using System.Buffers.Binary;

using System.Security.Cryptography;

using System.Text;

using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Backup;

[Collection("WorkspacePathPolicy")]
public sealed class BackupArchiveCodecCleanupTests : IDisposable
{

    private const string Passphrase = "cleanup test passphrase";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "arcanum-backup-cleanup-" + Guid.NewGuid().ToString("N"));

    public BackupArchiveCodecCleanupTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {

        SecureFilePermissions.StrictOwnerOnlyVerificationForTests = null;

        if (Directory.Exists(_root))
        {

            Directory.Delete(_root, recursive: true);

        }

    }

    [Fact]
    public async Task Write_removes_owned_destination_when_final_owner_only_verification_fails()
    {

        byte[] content = "permission failure payload"u8.ToArray();

        BackupManifestEntry entry = Entry(content);

        string archive = Path.Combine(
            _root,
            "permission-failure.arcbackup");

        SecureFilePermissions.StrictOwnerOnlyVerificationForTests =
            (path, isDirectory) =>
                isDirectory
                || !string.Equals(
                    Path.GetFullPath(path),
                    archive,
                    StringComparison.Ordinal);

        IOException error = await Assert.ThrowsAsync<IOException>(
            () => CreateCodec().WriteAsync(
                archive,
                Manifest(entry),
                [BackupArchiveSource.FromMemory(entry.Path, content)],
                Passphrase.AsMemory(),
                overwrite: false,
                CancellationToken.None));

        Assert.Contains(
            "owner-only",
            error.Message,
            StringComparison.OrdinalIgnoreCase);

        Assert.False(File.Exists(archive));

    }

    [Fact]
    public async Task Write_permission_failure_preserves_a_replacement_destination()
    {

        byte[] content = "replacement permission payload"u8.ToArray();

        BackupManifestEntry entry = Entry(content);

        string archive = Path.Combine(
            _root,
            "permission-replacement.arcbackup");

        string movedArchive = archive + ".owned";

        SecureFilePermissions.StrictOwnerOnlyVerificationForTests =
            (path, isDirectory) =>
            {

                if (isDirectory
                    || !string.Equals(
                        Path.GetFullPath(path),
                        archive,
                        StringComparison.Ordinal))
                {

                    return true;

                }

                File.Move(archive, movedArchive);

                File.WriteAllText(archive, "replacement destination");

                return false;

            };

        IOException error = await Assert.ThrowsAsync<IOException>(
            () => CreateCodec().WriteAsync(
                archive,
                Manifest(entry),
                [BackupArchiveSource.FromMemory(entry.Path, content)],
                Passphrase.AsMemory(),
                overwrite: false,
                CancellationToken.None));

        Assert.Contains(
            "owner-only",
            error.Message,
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal(
            "replacement destination",
            await File.ReadAllTextAsync(archive));

        Assert.True(File.Exists(movedArchive));

    }

    [Fact]
    public async Task Inspect_with_a_passphrase_never_creates_a_plaintext_payload_temp()
    {

        string archive = await WriteArchiveAsync("inspect-without-temp.arcbackup");

        int temporaryPayloadCleanupCalls = 0;

        BackupArchiveCodec codec = new(new BackupArchiveCodecOptions
        {

            KdfIterations = 10_000,

            BeforeTemporaryPayloadCleanupForTests = _ =>
                temporaryPayloadCleanupCalls++,

        });

        BackupInspectResult result = await codec.InspectAsync(
            archive,
            Passphrase.AsMemory(),
            CancellationToken.None);

        Assert.NotNull(result.Manifest);

        Assert.Equal(0, temporaryPayloadCleanupCalls);

        Assert.Empty(Directory.GetFiles(_root, ".*.payload.tmp.*"));

    }

    [Fact]
    public async Task Inspect_skips_large_content_with_chunk_bounded_plaintext_buffers()
    {

        const int chunkSize = 64 * 1024;

        byte[] content = RandomNumberGenerator.GetBytes(
            (5 * 1024 * 1024) + 17);

        BackupManifestEntry entry = Entry(content);

        string archive = Path.Combine(_root, "large-streaming-inspect.arcbackup");

        BackupArchiveCodec writer = new(new BackupArchiveCodecOptions
        {

            ChunkSize = chunkSize,

            KdfIterations = 10_000,

        });

        await writer.WriteAsync(
            archive,
            Manifest(entry),
            [BackupArchiveSource.FromMemory(entry.Path, content)],
            Passphrase.AsMemory(),
            overwrite: false,
            CancellationToken.None);

        List<int> observedPlaintextBuffers = [];

        BackupArchiveCodec inspector = new(new BackupArchiveCodecOptions
        {

            KdfIterations = 10_000,

            InspectPlaintextBufferSizeForTests = size =>
                observedPlaintextBuffers.Add(size),

        });

        BackupInspectResult result = await inspector.InspectAsync(
            archive,
            Passphrase.AsMemory(),
            CancellationToken.None);

        BackupManifest inspected = Assert.IsType<BackupManifest>(
            result.Manifest);

        Assert.Equal(content.LongLength, Assert.Single(inspected.Entries).Size);

        Assert.NotEmpty(observedPlaintextBuffers);

        Assert.True(content.Length > observedPlaintextBuffers.Max());

        Assert.All(
            observedPlaintextBuffers,
            size => Assert.InRange(size, 1, chunkSize));

        Assert.Empty(Directory.GetFiles(_root, ".*.payload.tmp.*"));

    }

    [Fact]
    public async Task Inspect_streaming_honors_cancellation_without_plaintext_staging()
    {

        string archive = await WriteArchiveAsync("cancelled-inspect.arcbackup");

        using CancellationTokenSource cancellation = new();

        long authenticatedPlaintext = 0;

        BackupArchiveCodec codec = new(new BackupArchiveCodecOptions
        {

            KdfIterations = 10_000,

            InspectAuthenticatedPlaintextProgressForTests = progress =>
            {

                authenticatedPlaintext = progress;

                cancellation.Cancel();

            },

        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => codec.InspectAsync(
                archive,
                Passphrase.AsMemory(),
                cancellation.Token));

        Assert.True(authenticatedPlaintext > 0);

        Assert.Empty(Directory.GetFiles(_root, ".*.payload.tmp.*"));

    }

    [Theory]
    [InlineData(43)]
    [InlineData(-1)]
    public async Task Inspect_streaming_authenticates_header_and_payload_bytes(
        int corruptionOffset)
    {

        string archive = await WriteArchiveAsync("authenticated-inspect.arcbackup");

        byte[] bytes = await File.ReadAllBytesAsync(archive);

        int offset = corruptionOffset < 0
            ? bytes.Length - 1
            : corruptionOffset;

        bytes[offset] ^= 0x01;

        string corrupted = Path.Combine(
            _root,
            $"authenticated-inspect-{offset}.arcbackup");

        await File.WriteAllBytesAsync(corrupted, bytes);

        BackupArchiveCodec codec = CreateCodec();

        await Assert.ThrowsAnyAsync<CryptographicException>(
            () => codec.InspectAsync(
                corrupted,
                Passphrase.AsMemory(),
                CancellationToken.None));

        Assert.Empty(Directory.GetFiles(_root, ".*.payload.tmp.*"));

    }

    [Theory]
    [InlineData(MalformedInspectPayload.NonCanonicalPath)]
    [InlineData(MalformedInspectPayload.DuplicatePath)]
    [InlineData(MalformedInspectPayload.ManifestNotLast)]
    public async Task Inspect_streaming_preserves_payload_topology_validation(
        MalformedInspectPayload malformed)
    {

        byte[] firstContent = "first payload"u8.ToArray();

        BackupManifestEntry first = Entry(
            firstContent,
            malformed == MalformedInspectPayload.DuplicatePath
                ? "content/first.txt"
                : "content/payload.txt");

        List<BackupManifestEntry> entries = [first];

        List<BackupArchiveSource> sources =
        [
            BackupArchiveSource.FromMemory(first.Path, firstContent),
        ];

        if (malformed == MalformedInspectPayload.DuplicatePath)
        {

            byte[] secondContent = "second payload"u8.ToArray();

            BackupManifestEntry second = Entry(
                secondContent,
                "content/other.txt");

            entries.Add(second);

            sources.Add(
                BackupArchiveSource.FromMemory(
                    second.Path,
                    secondContent));

        }

        string archive = Path.Combine(
            _root,
            $"malformed-{malformed}.arcbackup");

        await CreateCodec().WriteAsync(
            archive,
            Manifest([.. entries]),
            sources,
            Passphrase.AsMemory(),
            overwrite: false,
            CancellationToken.None);

        await RewriteAuthenticatedPayloadAsync(
            archive,
            payload => MalformPayload(payload, malformed));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => CreateCodec().InspectAsync(
                archive,
                Passphrase.AsMemory(),
                CancellationToken.None));

        Assert.Empty(Directory.GetFiles(_root, ".*.payload.tmp.*"));

    }

    [Fact]
    public async Task Write_routes_all_self_verification_artifacts_through_the_protected_scratch_root()
    {

        byte[] content = "protected scratch payload"u8.ToArray();

        BackupManifestEntry entry = Entry(content);

        string destinationParent = Path.Combine(_root, "published");

        Directory.CreateDirectory(destinationParent);

        string scratchRoot = Path.Combine(
            destinationParent,
            ".arcanum-backup-stage-test");

        Directory.CreateDirectory(scratchRoot);

        SecureFilePermissions.ApplyOwnerOnlyDirectory(scratchRoot);

        string archive = Path.Combine(
            destinationParent,
            "protected-scratch.arcbackup");

        string? stagedArchive = null;

        string? payloadPath = null;

        string? extractionRoot = null;

        BackupArchiveCodec codec = new(new BackupArchiveCodecOptions
        {

            KdfIterations = 10_000,

            BeforeTemporaryPayloadCleanupForTests = path =>
            {

                payloadPath = path;

                stagedArchive = Assert.Single(
                    Directory.GetFiles(scratchRoot, ".*.tmp.*"),
                    candidate => !string.Equals(
                        candidate,
                        path,
                        StringComparison.Ordinal));

            },

            BeforeTemporaryExtractionCleanupForTests = path =>
                extractionRoot = path,

        });

        await codec.WriteAsync(
            archive,
            Manifest(entry),
            [BackupArchiveSource.FromMemory(entry.Path, content)],
            Passphrase.AsMemory(),
            overwrite: false,
            scratchRoot,
            CancellationToken.None);

        Assert.True(File.Exists(archive));

        Assert.Equal(scratchRoot, Path.GetDirectoryName(stagedArchive));

        Assert.Equal(scratchRoot, Path.GetDirectoryName(payloadPath));

        Assert.Equal(scratchRoot, Path.GetDirectoryName(extractionRoot));

        Assert.Empty(Directory.EnumerateFileSystemEntries(scratchRoot));

    }

    [Fact]
    public async Task Write_rejects_a_relative_protected_scratch_root()
    {

        byte[] content = "relative scratch payload"u8.ToArray();

        BackupManifestEntry entry = Entry(content);

        string archive = Path.Combine(_root, "relative-scratch.arcbackup");

        await Assert.ThrowsAsync<ArgumentException>(
            () => CreateCodec().WriteAsync(
                archive,
                Manifest(entry),
                [BackupArchiveSource.FromMemory(entry.Path, content)],
                Passphrase.AsMemory(),
                overwrite: false,
                ".relative-scratch",
                CancellationToken.None));

        Assert.False(File.Exists(archive));

    }

    [Fact]
    public async Task Write_rejects_a_missing_protected_scratch_root()
    {

        byte[] content = "missing scratch payload"u8.ToArray();

        BackupManifestEntry entry = Entry(content);

        string archive = Path.Combine(_root, "missing-scratch.arcbackup");

        string scratchRoot = Path.Combine(_root, ".missing-scratch");

        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => CreateCodec().WriteAsync(
                archive,
                Manifest(entry),
                [BackupArchiveSource.FromMemory(entry.Path, content)],
                Passphrase.AsMemory(),
                overwrite: false,
                scratchRoot,
                CancellationToken.None));

        Assert.False(File.Exists(archive));

    }

    [Fact]
    public async Task Write_rejects_a_protected_scratch_root_outside_the_destination_parent()
    {

        byte[] content = "outside scratch payload"u8.ToArray();

        BackupManifestEntry entry = Entry(content);

        string destinationParent = Path.Combine(_root, "published-parent");

        Directory.CreateDirectory(destinationParent);

        string archive = Path.Combine(destinationParent, "outside-scratch.arcbackup");

        string scratchRoot = Path.Combine(_root, ".outside-scratch");

        Directory.CreateDirectory(scratchRoot);

        await Assert.ThrowsAsync<ArgumentException>(
            () => CreateCodec().WriteAsync(
                archive,
                Manifest(entry),
                [BackupArchiveSource.FromMemory(entry.Path, content)],
                Passphrase.AsMemory(),
                overwrite: false,
                scratchRoot,
                CancellationToken.None));

        Assert.False(File.Exists(archive));

    }

    [Fact]
    public async Task Verify_cleanup_refuses_to_delete_a_replacement_extraction_path()
    {

        string archive = await WriteArchiveAsync("verify-replacement.arcbackup");

        string? replacementRoot = null;

        string? movedRoot = null;

        string? sentinel = null;

        BackupArchiveCodec codec = new(new BackupArchiveCodecOptions
        {

            KdfIterations = 10_000,

            BeforeTemporaryExtractionCleanupForTests = path =>
            {

                replacementRoot = path;

                movedRoot = path + ".owned";

                Directory.Move(path, movedRoot);

                Directory.CreateDirectory(path);

                sentinel = Path.Combine(path, "do-not-delete");

                File.WriteAllText(sentinel, "replacement extraction root");

            },

        });

        BackupVerifyResult result = await codec.VerifyAsync(
            archive,
            Passphrase.AsMemory(),
            CancellationToken.None);

        Assert.True(
            result.IsValid,
            string.Join(global::System.Environment.NewLine, result.Issues));

        Assert.NotNull(replacementRoot);

        Assert.NotNull(movedRoot);

        Assert.NotNull(sentinel);

        Assert.True(Directory.Exists(replacementRoot));

        Assert.True(Directory.Exists(movedRoot));

        Assert.Equal(
            "replacement extraction root",
            await File.ReadAllTextAsync(sentinel));

    }

    [Fact]
    public async Task Write_refuses_to_publish_or_delete_a_replacement_staging_path()
    {

        byte[] content = "staging identity payload"u8.ToArray();

        BackupManifestEntry entry = Entry(content);

        string archive = Path.Combine(_root, "staging-replacement.arcbackup");

        string? replacementPath = null;

        string? movedPath = null;

        BackupArchiveCodec codec = new(new BackupArchiveCodecOptions
        {

            KdfIterations = 10_000,

            BeforeTemporaryPayloadCleanupForTests = payloadPath =>
            {

                replacementPath = Assert.Single(
                    Directory.GetFiles(_root, ".*.tmp.*"),
                    path => !string.Equals(
                        path,
                        payloadPath,
                        StringComparison.Ordinal));

                movedPath = replacementPath + ".owned";

                File.Move(replacementPath, movedPath);

                File.WriteAllText(replacementPath, "replacement staging file");

            },

        });

        IOException error = await Assert.ThrowsAsync<IOException>(
            () => codec.WriteAsync(
                archive,
                Manifest(entry),
                [BackupArchiveSource.FromMemory(entry.Path, content)],
                Passphrase.AsMemory(),
                overwrite: false,
                CancellationToken.None));

        Assert.Contains("changed", error.Message, StringComparison.OrdinalIgnoreCase);

        Assert.False(File.Exists(archive));

        Assert.NotNull(replacementPath);

        Assert.NotNull(movedPath);

        Assert.Equal(
            "replacement staging file",
            await File.ReadAllTextAsync(replacementPath));

        Assert.True(File.Exists(movedPath));

    }

    [Fact]
    public async Task Inspect_and_verify_remove_normally_owned_temporary_artifacts()
    {

        string archive = await WriteArchiveAsync("normal-cleanup.arcbackup");

        BackupArchiveCodec codec = CreateCodec();

        _ = await codec.InspectAsync(
            archive,
            Passphrase.AsMemory(),
            CancellationToken.None);

        BackupVerifyResult result = await codec.VerifyAsync(
            archive,
            Passphrase.AsMemory(),
            CancellationToken.None);

        Assert.True(
            result.IsValid,
            string.Join(global::System.Environment.NewLine, result.Issues));

        Assert.Empty(Directory.GetFiles(_root, ".*.payload.tmp.*"));

        Assert.Empty(Directory.GetDirectories(_root, ".arcanum-backup-verify-*"));

        Assert.Empty(Directory.GetDirectories(_root, ".arcanum-cleanup-*"));

    }

    private async Task<string> WriteArchiveAsync(string name)
    {

        byte[] content = "temporary cleanup payload"u8.ToArray();

        BackupManifestEntry entry = Entry(content);

        string archive = Path.Combine(_root, name);

        await CreateCodec().WriteAsync(
            archive,
            Manifest(entry),
            [BackupArchiveSource.FromMemory(entry.Path, content)],
            Passphrase.AsMemory(),
            overwrite: false,
            CancellationToken.None);

        return archive;

    }

    private static BackupArchiveCodec CreateCodec() =>
        new(new BackupArchiveCodecOptions
        {

            KdfIterations = 10_000,

        });

    private static BackupManifestEntry Entry(
        byte[] content,
        string path = "content/payload.txt") =>
        new(
            path,
            content.LongLength,
            Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
            BackupComponent.Configuration);

    private static BackupManifest Manifest(
        params BackupManifestEntry[] entries) =>
        new(
            BackupArchiveFormat.CurrentVersion,
            "1.0.0-test",
            "test-build",
            "20260730040000_AddBlobEncryptionMigrationState",
            DateTimeOffset.UnixEpoch,
            "test-platform",
            new BackupEnvelopeDescriptor(
                "PBKDF2",
                "HMAC-SHA256",
                10_000,
                string.Empty,
                "AES-256-GCM",
                256,
                12,
                16,
                1024 * 1024),
            BackupScope.Full,
            SessionId: null,
            RequestedIncludes: [],
            RequestedExcludes: [],
            SecurityWarnings: [],
            Components: Enum
                .GetValues<BackupComponent>()
                .Select(component => component == BackupComponent.Configuration
                    ? new BackupManifestComponent(
                        component,
                        BackupComponentStatus.Complete,
                        "included",
                        entries.LongLength,
                        entries.Sum(static entry => entry.Size))
                    : new BackupManifestComponent(
                        component,
                        BackupComponentStatus.OmittedByPolicy,
                        "not selected",
                        0,
                        0))
                .ToArray(),
            Entries: entries);

    private async Task RewriteAuthenticatedPayloadAsync(
        string archivePath,
        Func<byte[], byte[]> rewrite)
    {

        byte[] archive = await File.ReadAllBytesAsync(archivePath);

        byte[] header = archive.AsSpan(0, 68).ToArray();

        byte[] plaintext = DecryptPayload(archive, header);

        byte[] rewritten = rewrite(plaintext);

        Assert.Equal(plaintext.Length, rewritten.Length);

        byte[] authenticated = EncryptPayload(header, rewritten);

        await File.WriteAllBytesAsync(archivePath, authenticated);

    }

    private static byte[] MalformPayload(
        byte[] payload,
        MalformedInspectPayload malformed)
    {

        TestPayloadRecord[] records = ParseRecords(payload);

        if (malformed == MalformedInspectPayload.NonCanonicalPath)
        {

            byte[] replacement = Encoding.UTF8.GetBytes(
                "content//ayload.txt");

            Assert.Equal(records[0].PathLength, replacement.Length);

            replacement.CopyTo(
                payload.AsSpan(
                    records[0].PathOffset,
                    records[0].PathLength));

            return payload;

        }

        if (malformed == MalformedInspectPayload.DuplicatePath)
        {

            Assert.True(records.Length >= 3);

            Assert.Equal(records[0].PathLength, records[1].PathLength);

            payload.AsSpan(
                    records[0].PathOffset,
                    records[0].PathLength)
                .CopyTo(payload.AsSpan(
                    records[1].PathOffset,
                    records[1].PathLength));

            return payload;

        }

        Assert.Equal(MalformedInspectPayload.ManifestNotLast, malformed);

        Assert.Equal(2, records.Length);

        byte[] reordered = new byte[payload.Length];

        payload.AsSpan(
                records[1].Offset,
                records[1].TotalLength)
            .CopyTo(reordered);

        payload.AsSpan(
                records[0].Offset,
                records[0].TotalLength)
            .CopyTo(reordered.AsSpan(records[1].TotalLength));

        return reordered;

    }

    private static TestPayloadRecord[] ParseRecords(byte[] payload)
    {

        List<TestPayloadRecord> records = [];

        int offset = 0;

        while (offset < payload.Length)
        {

            int recordOffset = offset;

            int pathLength = BinaryPrimitives.ReadInt32BigEndian(
                payload.AsSpan(offset, sizeof(int)));

            int pathOffset = offset + sizeof(int);

            int lengthOffset = checked(pathOffset + pathLength);

            long contentLength = BinaryPrimitives.ReadInt64BigEndian(
                payload.AsSpan(lengthOffset, sizeof(long)));

            int contentOffset = checked(lengthOffset + sizeof(long));

            int nextOffset = checked(
                contentOffset + checked((int)contentLength));

            records.Add(new TestPayloadRecord(
                recordOffset,
                pathOffset,
                pathLength,
                nextOffset - recordOffset));

            offset = nextOffset;

        }

        Assert.Equal(payload.Length, offset);

        return [.. records];

    }

    private static byte[] DecryptPayload(
        byte[] archive,
        byte[] header)
    {

        int iterations = BinaryPrimitives.ReadInt32BigEndian(
            header.AsSpan(20));

        int chunkSize = BinaryPrimitives.ReadInt32BigEndian(
            header.AsSpan(24));

        long plaintextLength = BinaryPrimitives.ReadInt64BigEndian(
            header.AsSpan(28));

        byte[] key = DeriveTestKey(header, iterations);

        byte[] plaintext = new byte[checked((int)plaintextLength)];

        int archiveOffset = header.Length;

        int plaintextOffset = 0;

        uint chunkIndex = 0;

        using AesGcm aes = new(key, 16);

        try
        {

            while (plaintextOffset < plaintext.Length)
            {

                int length = BinaryPrimitives.ReadInt32BigEndian(
                    archive.AsSpan(archiveOffset, sizeof(int)));

                archiveOffset += sizeof(int);

                Assert.Equal(
                    Math.Min(chunkSize, plaintext.Length - plaintextOffset),
                    length);

                byte[] nonce = Nonce(header, chunkIndex);

                byte[] associatedData = AssociatedData(header, chunkIndex);

                try
                {

                    aes.Decrypt(
                        nonce,
                        archive.AsSpan(archiveOffset, length),
                        archive.AsSpan(archiveOffset + length, 16),
                        plaintext.AsSpan(plaintextOffset, length),
                        associatedData);

                }
                finally
                {

                    CryptographicOperations.ZeroMemory(nonce);

                    CryptographicOperations.ZeroMemory(associatedData);

                }

                archiveOffset += length + 16;

                plaintextOffset += length;

                chunkIndex++;

            }

            Assert.Equal(archive.Length, archiveOffset);

            return plaintext;

        }
        finally
        {

            CryptographicOperations.ZeroMemory(key);

        }

    }

    private static byte[] EncryptPayload(
        byte[] header,
        byte[] plaintext)
    {

        int iterations = BinaryPrimitives.ReadInt32BigEndian(
            header.AsSpan(20));

        int chunkSize = BinaryPrimitives.ReadInt32BigEndian(
            header.AsSpan(24));

        int chunkCount = plaintext.Length == 0
            ? 0
            : ((plaintext.Length - 1) / chunkSize) + 1;

        byte[] archive = new byte[checked(
            header.Length
            + plaintext.Length
            + (chunkCount * (sizeof(int) + 16)))];

        header.CopyTo(archive, 0);

        byte[] key = DeriveTestKey(header, iterations);

        int archiveOffset = header.Length;

        int plaintextOffset = 0;

        uint chunkIndex = 0;

        using AesGcm aes = new(key, 16);

        try
        {

            while (plaintextOffset < plaintext.Length)
            {

                int length = Math.Min(
                    chunkSize,
                    plaintext.Length - plaintextOffset);

                BinaryPrimitives.WriteInt32BigEndian(
                    archive.AsSpan(archiveOffset, sizeof(int)),
                    length);

                archiveOffset += sizeof(int);

                byte[] nonce = Nonce(header, chunkIndex);

                byte[] associatedData = AssociatedData(header, chunkIndex);

                try
                {

                    aes.Encrypt(
                        nonce,
                        plaintext.AsSpan(plaintextOffset, length),
                        archive.AsSpan(archiveOffset, length),
                        archive.AsSpan(archiveOffset + length, 16),
                        associatedData);

                }
                finally
                {

                    CryptographicOperations.ZeroMemory(nonce);

                    CryptographicOperations.ZeroMemory(associatedData);

                }

                archiveOffset += length + 16;

                plaintextOffset += length;

                chunkIndex++;

            }

            Assert.Equal(archive.Length, archiveOffset);

            return archive;

        }
        finally
        {

            CryptographicOperations.ZeroMemory(key);

        }

    }

    private static byte[] DeriveTestKey(
        byte[] header,
        int iterations)
    {

        byte[] passphrase = Encoding.UTF8.GetBytes(Passphrase);

        try
        {

            return Rfc2898DeriveBytes.Pbkdf2(
                passphrase,
                header.AsSpan(44, 16),
                iterations,
                HashAlgorithmName.SHA256,
                32);

        }
        finally
        {

            CryptographicOperations.ZeroMemory(passphrase);

        }

    }

    private static byte[] Nonce(
        byte[] header,
        uint chunkIndex)
    {

        byte[] nonce = new byte[12];

        header.AsSpan(60, 8).CopyTo(nonce);

        BinaryPrimitives.WriteUInt32BigEndian(
            nonce.AsSpan(8),
            chunkIndex);

        return nonce;

    }

    private static byte[] AssociatedData(
        byte[] header,
        uint chunkIndex)
    {

        byte[] associatedData = new byte[header.Length + sizeof(uint)];

        header.CopyTo(associatedData, 0);

        BinaryPrimitives.WriteUInt32BigEndian(
            associatedData.AsSpan(header.Length),
            chunkIndex);

        return associatedData;

    }

    public enum MalformedInspectPayload
    {

        NonCanonicalPath,

        DuplicatePath,

        ManifestNotLast,

    }

    private readonly record struct TestPayloadRecord(
        int Offset,
        int PathOffset,
        int PathLength,
        int TotalLength);

}
