using System.Buffers.Binary;

using System.Security.Cryptography;

using System.Text;

using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Backup;

[Collection("WorkspacePathPolicy")]
public sealed class BackupArchiveCodecTests : IDisposable
{

    private const string GoldenPassphrase = "golden recovery passphrase";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "arcanum-backup-codec-" + Guid.NewGuid().ToString("N"));

    public BackupArchiveCodecTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {

        if (Directory.Exists(_root))
        {

            Directory.Delete(_root, recursive: true);

        }

    }

    [Fact]
    public async Task Encrypted_archive_round_trips_empty_and_large_entries_without_plaintext_leakage()
    {

        string empty = WriteSource("empty.bin", []);

        byte[] largeBytes = RandomNumberGenerator.GetBytes((2 * 1024 * 1024) + 17);

        string large = WriteSource("large.bin", largeBytes);

        string archive = Path.Combine(_root, "round-trip.arcbackup");

        BackupManifest manifest = Manifest(
            Entry("content/empty.bin", empty, BackupComponent.Configuration),
            Entry("content/large.bin", large, BackupComponent.UploadedFiles));

        BackupArchiveCodec codec = new(new BackupArchiveCodecOptions
        {

            ChunkSize = 64 * 1024,

            KdfIterations = 10_000,

        });

        await codec.WriteAsync(
            archive,
            manifest,
            [
                new BackupArchiveSource("content/empty.bin", empty),
                new BackupArchiveSource("content/large.bin", large),
            ],
            "correct horse battery staple".AsMemory(),
            overwrite: false,
            CancellationToken.None);

        byte[] ciphertext = await File.ReadAllBytesAsync(archive);

        Assert.StartsWith("ARCABACK", Encoding.ASCII.GetString(ciphertext, 0, 8));

        Assert.DoesNotContain(
            Encoding.UTF8.GetBytes("content/large.bin"),
            ciphertext);

        BackupInspectResult publicInspection = await codec.InspectAsync(
            archive,
            passphrase: null,
            CancellationToken.None);

        Assert.Equal(BackupArchiveFormat.CurrentVersion, publicInspection.FormatVersion);

        Assert.Null(publicInspection.Manifest);

        BackupInspectResult privateInspection = await codec.InspectAsync(
            archive,
            "correct horse battery staple".AsMemory(),
            CancellationToken.None);

        Assert.Equal(2, privateInspection.Manifest?.Entries.Length);

        BackupVerifyResult verification = await codec.VerifyAsync(
            archive,
            "correct horse battery staple".AsMemory(),
            CancellationToken.None);

        Assert.True(
            verification.IsValid,
            string.Join(global::System.Environment.NewLine, verification.Issues));

        Assert.Equal(largeBytes.LongLength, privateInspection.Manifest?.Entries[1].Size);

    }

    [Fact]
    public async Task Verify_rejects_wrong_passphrase_and_single_byte_header_or_payload_corruption()
    {

        string source = WriteSource("payload.txt", Encoding.UTF8.GetBytes("sensitive payload"));

        string archive = Path.Combine(_root, "corrupt.arcbackup");

        BackupManifest manifest = Manifest(
            Entry("content/payload.txt", source, BackupComponent.Configuration));

        BackupArchiveCodec codec = new(new BackupArchiveCodecOptions
        {

            ChunkSize = 32,

            KdfIterations = 10_000,

        });

        await codec.WriteAsync(
            archive,
            manifest,
            [new BackupArchiveSource("content/payload.txt", source)],
            "right-passphrase".AsMemory(),
            overwrite: false,
            CancellationToken.None);

        BackupVerifyResult wrong = await codec.VerifyAsync(
            archive,
            "wrong-passphrase".AsMemory(),
            CancellationToken.None);

        Assert.False(wrong.IsValid);

        Assert.Contains(wrong.Issues, issue => issue.Code == "backup.authentication_failed");

        byte[] original = await File.ReadAllBytesAsync(archive);

        foreach (int offset in new[] { 12, original.Length - 17 })
        {

            byte[] corrupted = [.. original];

            corrupted[offset] ^= 0x01;

            string corruptPath = Path.Combine(_root, $"corrupt-{offset}.arcbackup");

            await File.WriteAllBytesAsync(corruptPath, corrupted);

            BackupVerifyResult result = await codec.VerifyAsync(
                corruptPath,
                "right-passphrase".AsMemory(),
                CancellationToken.None);

            Assert.False(result.IsValid);

        }

    }

    [Fact]
    public async Task Huge_nonnegative_plaintext_length_is_reported_as_invalid_archive()
    {

        string archive = Path.Combine(_root, "huge-length.arcbackup");

        BackupArchiveCodec codec = new(new BackupArchiveCodecOptions
        {

            KdfIterations = 10_000,

        });

        await codec.WriteAsync(
            archive,
            Manifest(),
            [],
            "length passphrase".AsMemory(),
            overwrite: false,
            CancellationToken.None);

        byte[] malicious = await File.ReadAllBytesAsync(archive);

        BinaryPrimitives.WriteInt64BigEndian(
            malicious.AsSpan(28, sizeof(long)),
            long.MaxValue);

        await File.WriteAllBytesAsync(archive, malicious);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            codec.InspectAsync(
                archive,
                passphrase: null,
                CancellationToken.None));

        BackupVerifyResult verification = await codec.VerifyAsync(
            archive,
            "length passphrase".AsMemory(),
            CancellationToken.None);

        Assert.False(verification.IsValid);

        Assert.Contains(
            verification.Issues,
            issue => issue.Code == "backup.invalid_archive");

    }

    [Fact]
    public async Task Cancellation_and_existing_destination_never_publish_a_partial_archive()
    {

        string source = WriteSource("payload.txt", Encoding.UTF8.GetBytes("new payload"));

        string archive = Path.Combine(_root, "existing.arcbackup");

        byte[] original = Encoding.UTF8.GetBytes("existing archive sentinel");

        await File.WriteAllBytesAsync(archive, original);

        BackupArchiveCodec codec = new(new BackupArchiveCodecOptions
        {

            KdfIterations = 10_000,

        });

        BackupManifest manifest = Manifest(
            Entry("content/payload.txt", source, BackupComponent.Configuration));

        await Assert.ThrowsAsync<IOException>(
            () => codec.WriteAsync(
                archive,
                manifest,
                [new BackupArchiveSource("content/payload.txt", source)],
                "passphrase".AsMemory(),
                overwrite: false,
                CancellationToken.None));

        Assert.Equal(original, await File.ReadAllBytesAsync(archive));

        string cancelledArchive = Path.Combine(_root, "cancelled.arcbackup");

        using CancellationTokenSource cancellation = new();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => codec.WriteAsync(
                cancelledArchive,
                manifest,
                [new BackupArchiveSource("content/payload.txt", source)],
                "passphrase".AsMemory(),
                overwrite: false,
                cancellation.Token));

        Assert.False(File.Exists(cancelledArchive));

        Assert.Empty(Directory.GetFiles(_root, ".*.tmp.*"));

    }

    [Fact]
    public async Task Published_archive_is_owner_only_on_Unix()
    {

        if (OperatingSystem.IsWindows())
        {

            return;

        }

        string source = WriteSource("payload.txt", Encoding.UTF8.GetBytes("payload"));

        string archive = Path.Combine(_root, "permissions.arcbackup");

        BackupArchiveCodec codec = new(new BackupArchiveCodecOptions
        {

            KdfIterations = 10_000,

        });

        BackupManifest manifest = Manifest(
            Entry("content/payload.txt", source, BackupComponent.Configuration));

        await codec.WriteAsync(
            archive,
            manifest,
            [new BackupArchiveSource("content/payload.txt", source)],
            "passphrase".AsMemory(),
            overwrite: false,
            CancellationToken.None);

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(archive));

    }

    [Fact]
    public async Task Write_preserves_an_existing_destination_parent_and_creates_a_missing_one()
    {

        string existingParent = Path.Combine(_root, "shared-output");

        Directory.CreateDirectory(existingParent);

        UnixFileMode existingMode =
            UnixFileMode.UserRead
            | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead
            | UnixFileMode.GroupExecute;

        if (!OperatingSystem.IsWindows())
        {

            File.SetUnixFileMode(existingParent, existingMode);

        }

        BackupArchiveCodec codec = new(new BackupArchiveCodecOptions
        {

            KdfIterations = 10_000,

        });

        string existingParentArchive = Path.Combine(
            existingParent,
            "existing-parent.arcbackup");

        await codec.WriteAsync(
            existingParentArchive,
            Manifest(),
            [],
            "passphrase".AsMemory(),
            overwrite: false,
            CancellationToken.None);

        if (!OperatingSystem.IsWindows())
        {

            Assert.Equal(
                existingMode,
                File.GetUnixFileMode(existingParent));

        }

        string missingParent = Path.Combine(_root, "new-output", "nested");

        string missingParentArchive = Path.Combine(
            missingParent,
            "missing-parent.arcbackup");

        await codec.WriteAsync(
            missingParentArchive,
            Manifest(),
            [],
            "passphrase".AsMemory(),
            overwrite: false,
            CancellationToken.None);

        Assert.True(Directory.Exists(missingParent));

        Assert.True(File.Exists(missingParentArchive));

    }

    [Fact]
    public async Task Source_replacement_after_open_aborts_without_publishing_an_archive()
    {

        byte[] original = Enumerable.Repeat((byte)0x11, 4096).ToArray();

        byte[] replacement = Enumerable.Repeat((byte)0x22, original.Length).ToArray();

        string source = WriteSource("active-source.bin", original);

        string replacementPath = WriteSource("replacement-source.bin", replacement);

        string archive = Path.Combine(_root, "active-source.arcbackup");

        BackupArchiveCodec codec = new(new BackupArchiveCodecOptions
        {

            ChunkSize = 1024,

            KdfIterations = 10_000,

        });

        BackupManifest manifest = Manifest(
            Entry("content/active-source.bin", source, BackupComponent.UploadedFiles));

        int replacements = 0;

        SecureFileReader.AfterOpenForTests = openedPath =>
        {

            if (string.Equals(openedPath, source, StringComparison.Ordinal)
                && Interlocked.Exchange(ref replacements, 1) == 0)
            {

                File.Move(replacementPath, source, overwrite: true);

            }

        };

        try
        {

            Exception error = await FilesystemRefusal.ThrowsAsync(
                () => codec.WriteAsync(
                    archive,
                    manifest,
                    [new BackupArchiveSource("content/active-source.bin", source)],
                    "passphrase".AsMemory(),
                    overwrite: false,
                    CancellationToken.None));

            Assert.Contains("changed", error.Message, StringComparison.OrdinalIgnoreCase);

        }
        finally
        {

            SecureFileReader.AfterOpenForTests = null;

        }

        Assert.Equal(1, replacements);

        Assert.False(File.Exists(archive));

        Assert.Empty(Directory.GetFiles(_root, ".*.tmp.*"));

    }

    [Fact]
    public async Task Sensitive_generated_entries_stream_from_memory_without_plaintext_staging()
    {

        byte[] sensitive = Encoding.UTF8.GetBytes(
            "portable-recovery-secret-that-must-never-be-staged");

        string archive = Path.Combine(_root, "memory-source.arcbackup");

        BackupManifestEntry entry = new(
            "recovery/portable-keys.json",
            sensitive.LongLength,
            Convert.ToHexString(SHA256.HashData(sensitive)).ToLowerInvariant(),
            BackupComponent.PortableRecoveryKeys);

        BackupArchiveCodec codec = new(new BackupArchiveCodecOptions
        {

            KdfIterations = 10_000,

        });

        await codec.WriteAsync(
            archive,
            Manifest(entry),
            [BackupArchiveSource.FromMemory(entry.Path, sensitive)],
            "memory passphrase".AsMemory(),
            overwrite: false,
            CancellationToken.None);

        byte[] encrypted = await File.ReadAllBytesAsync(archive);

        Assert.DoesNotContain(sensitive, encrypted);

        BackupVerifyResult result = await codec.VerifyAsync(
            archive,
            "memory passphrase".AsMemory(),
            CancellationToken.None);

        Assert.True(result.IsValid);

    }

    [Theory]
    [InlineData(
        GoldenArchiveShape.Empty,
        "5bb2a06bdf4bc536f632514c64332350da22778614b266e0bf417a9deba73e87")]
    [InlineData(
        GoldenArchiveShape.Minimal,
        "d4c7e81c5400592d42fa72f4c04ee60ae534df30fe5ab8034d5cfc14f08034f9")]
    [InlineData(
        GoldenArchiveShape.Fullish,
        "64cf7c44559f572fb27a2dafd68898d1eb1811d68623afd7da3aa006b9c92b0e")]
    public async Task Golden_vectors_have_stable_authenticated_archive_bytes(
        GoldenArchiveShape shape,
        string expectedSha256)
    {

        GoldenArchiveVector vector = BuildGoldenVector(shape);

        string archive = Path.Combine(
            _root,
            $"golden-{shape.ToString().ToLowerInvariant()}.arcbackup");

        BackupArchiveCodec codec = GoldenCodec();

        await codec.WriteAsync(
            archive,
            vector.Manifest,
            vector.Sources,
            GoldenPassphrase.AsMemory(),
            overwrite: false,
            CancellationToken.None);

        byte[] bytes = await File.ReadAllBytesAsync(archive);

        Assert.Equal(expectedSha256, Sha256Hex(bytes));

        BackupVerifyResult verification = await codec.VerifyAsync(
            archive,
            GoldenPassphrase.AsMemory(),
            CancellationToken.None);

        Assert.True(
            verification.IsValid,
            string.Join(global::System.Environment.NewLine, verification.Issues));

    }

    [Fact]
    public async Task Corrupt_golden_vector_has_stable_bytes_and_is_rejected()
    {

        GoldenArchiveVector vector = BuildGoldenVector(
            GoldenArchiveShape.Minimal);

        string archive = Path.Combine(_root, "golden-corrupt.arcbackup");

        BackupArchiveCodec codec = GoldenCodec();

        await codec.WriteAsync(
            archive,
            vector.Manifest,
            vector.Sources,
            GoldenPassphrase.AsMemory(),
            overwrite: false,
            CancellationToken.None);

        byte[] corrupted = await File.ReadAllBytesAsync(archive);

        corrupted[^1] ^= 0x01;

        Assert.Equal(
            "75283a13078bac23ae5e231aae5f97c7ed767920bfe1d7803b6e67bb4c92fbb9",
            Sha256Hex(corrupted));

        string corruptArchive = Path.Combine(
            _root,
            "golden-corrupt-mutated.arcbackup");

        await File.WriteAllBytesAsync(corruptArchive, corrupted);

        BackupVerifyResult verification = await codec.VerifyAsync(
            corruptArchive,
            GoldenPassphrase.AsMemory(),
            CancellationToken.None);

        Assert.False(verification.IsValid);

        Assert.Contains(
            verification.Issues,
            issue => issue.Code == "backup.authentication_failed");

    }

    private static BackupArchiveCodec GoldenCodec() =>
        new(new BackupArchiveCodecOptions
        {

            ChunkSize = 32,

            KdfIterations = 10_000,

            SaltFactory = static () =>
                Convert.FromHexString("000102030405060708090a0b0c0d0e0f"),

            NoncePrefixFactory = static () =>
                Convert.FromHexString("1011121314151617"),

        });

    private static GoldenArchiveVector BuildGoldenVector(
        GoldenArchiveShape shape) =>
        shape switch
        {
            GoldenArchiveShape.Empty => EmptyGoldenVector(),
            GoldenArchiveShape.Minimal => MinimalGoldenVector(),
            GoldenArchiveShape.Fullish => FullishGoldenVector(),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

    private static GoldenArchiveVector EmptyGoldenVector()
    {

        BackupManifest manifest = GoldenManifest(
            BackupScope.MetadataOnly,
            sessionId: null,
            requestedIncludes: [],
            requestedExcludes: [],
            securityWarnings: [],
            components: [],
            entries: []);

        return new GoldenArchiveVector(manifest, []);

    }

    private static GoldenArchiveVector MinimalGoldenVector()
    {

        byte[] content = Encoding.UTF8.GetBytes("minimal golden payload\n");

        BackupManifestEntry entry = MemoryEntry(
            "configuration/arcanum.json",
            content,
            BackupComponent.Configuration);

        BackupManifest manifest = GoldenManifest(
            BackupScope.MetadataOnly,
            sessionId: null,
            requestedIncludes: [BackupComponent.Configuration],
            requestedExcludes: [],
            securityWarnings: [],
            components:
            [
                GoldenComponent(
                    BackupComponent.Configuration,
                    entry),
            ],
            entries: [entry]);

        return new GoldenArchiveVector(
            manifest,
            [BackupArchiveSource.FromMemory(entry.Path, content)]);

    }

    private static GoldenArchiveVector FullishGoldenVector()
    {

        byte[] attachment = Encoding.UTF8.GetBytes(
            "session attachment golden bytes\n");

        byte[] codex = Encoding.UTF8.GetBytes(
            "# Golden CODEX\n\nDeterministic authored guidance.\n");

        byte[] configuration = Encoding.UTF8.GetBytes(
            "{\"defaultProvider\":\"golden\",\"telemetry\":false}\n");

        byte[] upload = Enumerable
            .Range(0, 97)
            .Select(static index => (byte)((index * 37) % 256))
            .ToArray();

        byte[] recovery = Encoding.UTF8.GetBytes(
            "{\"version\":1,\"activeFileEncryptionKeyId\":\"golden-key\"}\n");

        BackupManifestEntry[] entries =
        [
            MemoryEntry(
                "attachments/11111111111111111111111111111111/note.bin",
                attachment,
                BackupComponent.SessionAttachments),
            MemoryEntry(
                "authored/CODEX.md",
                codex,
                BackupComponent.GlobalCodex),
            MemoryEntry(
                "configuration/arcanum.json",
                configuration,
                BackupComponent.Configuration),
            MemoryEntry(
                "files/22222222222222222222222222222222",
                upload,
                BackupComponent.UploadedFiles),
            MemoryEntry(
                BackupArchivePaths.PortableRecoveryKeys,
                recovery,
                BackupComponent.PortableRecoveryKeys),
        ];

        BackupManifest manifest = GoldenManifest(
            BackupScope.Full,
            sessionId: null,
            requestedIncludes:
            [
                BackupComponent.TrustedMcpWorkspaceMetadata,
                BackupComponent.AuditLogs,
            ],
            requestedExcludes: [BackupComponent.MasterApiKey],
            securityWarnings:
            [
                "Golden warning: trusted workspace metadata was requested.",
                "Golden warning: master API key remains excluded.",
            ],
            components:
            [
                GoldenComponent(
                    BackupComponent.PortableRecoveryKeys,
                    entries[4]),
                GoldenComponent(
                    BackupComponent.Configuration,
                    entries[2]),
                GoldenComponent(
                    BackupComponent.SessionAttachments,
                    entries[0]),
                GoldenComponent(
                    BackupComponent.UploadedFiles,
                    entries[3]),
                GoldenComponent(
                    BackupComponent.GlobalCodex,
                    entries[1]),
                new BackupManifestComponent(
                    BackupComponent.AuditLogs,
                    BackupComponentStatus.Unavailable,
                    "Golden vector has no audit log bytes.",
                    Files: 0,
                    Bytes: 0),
            ],
            entries: entries);

        return new GoldenArchiveVector(
            manifest,
            [
                BackupArchiveSource.FromMemory(entries[0].Path, attachment),
                BackupArchiveSource.FromMemory(entries[1].Path, codex),
                BackupArchiveSource.FromMemory(entries[2].Path, configuration),
                BackupArchiveSource.FromMemory(entries[3].Path, upload),
                BackupArchiveSource.FromMemory(entries[4].Path, recovery),
            ]);

    }

    private static BackupManifest GoldenManifest(
        BackupScope scope,
        Guid? sessionId,
        BackupComponent[] requestedIncludes,
        BackupComponent[] requestedExcludes,
        string[] securityWarnings,
        BackupManifestComponent[] components,
        BackupManifestEntry[] entries) =>
        new(
            BackupArchiveFormat.CurrentVersion,
            "1.0.0-golden",
            "golden-build",
            "20260730040000_AddBlobEncryptionMigrationState",
            new DateTimeOffset(
                2026,
                8,
                2,
                12,
                34,
                56,
                TimeSpan.Zero),
            "golden-platform",
            new BackupEnvelopeDescriptor(
                "PBKDF2",
                "HMAC-SHA256",
                10_000,
                string.Empty,
                "AES-256-GCM",
                256,
                12,
                16,
                32),
            scope,
            sessionId,
            requestedIncludes,
            requestedExcludes,
            securityWarnings,
            CompleteComponentCatalog(entries, components),
            entries);

    private static BackupManifestComponent GoldenComponent(
        BackupComponent component,
        BackupManifestEntry entry) =>
        new(
            component,
            BackupComponentStatus.Complete,
            "Golden vector component is complete.",
            Files: 1,
            Bytes: entry.Size);

    private static BackupManifestComponent[] CompleteComponentCatalog(
        IReadOnlyList<BackupManifestEntry> entries,
        IReadOnlyList<BackupManifestComponent>? overrides = null)
    {

        Dictionary<BackupComponent, BackupManifestComponent> overridden =
            overrides?.ToDictionary(static component => component.Component)
            ?? [];

        return Enum.GetValues<BackupComponent>()
            .Select(component =>
            {

                if (overridden.TryGetValue(
                        component,
                        out BackupManifestComponent? specified))
                {

                    return specified;

                }

                BackupManifestEntry[] owned = entries
                    .Where(entry => entry.Component == component)
                    .ToArray();

                return new BackupManifestComponent(
                    component,
                    owned.Length == 0
                        ? BackupComponentStatus.OmittedByPolicy
                        : BackupComponentStatus.Complete,
                    owned.Length == 0 ? "omitted" : "included",
                    owned.LongLength,
                    owned.Sum(static entry => entry.Size));

            })
            .ToArray();

    }

    private static BackupManifestEntry MemoryEntry(
        string path,
        byte[] content,
        BackupComponent component) =>
        new(
            path,
            content.LongLength,
            Sha256Hex(content),
            component);

    private static string Sha256Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private string WriteSource(string name, byte[] content)
    {

        string path = Path.Combine(_root, name);

        File.WriteAllBytes(path, content);

        return path;

    }

    private static BackupManifestEntry Entry(
        string archivePath,
        string sourcePath,
        BackupComponent component)
    {

        byte[] bytes = File.ReadAllBytes(sourcePath);

        return new BackupManifestEntry(
            archivePath,
            bytes.LongLength,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            component);

    }

    private static BackupManifest Manifest(params BackupManifestEntry[] entries) =>
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
                32),
            BackupScope.Full,
            SessionId: null,
            RequestedIncludes: [],
            RequestedExcludes: [],
            SecurityWarnings: [],
            Components: CompleteComponentCatalog(entries),
            Entries: entries);

    public enum GoldenArchiveShape
    {

        Empty = 0,

        Minimal = 1,

        Fullish = 2,

    }

    private sealed record GoldenArchiveVector(
        BackupManifest Manifest,
        BackupArchiveSource[] Sources);

}
