using System.Text;

using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Infrastructure.Backup;

namespace RetroDownfall.Arcanum.Tests.Backup;

public sealed class BackupArchiveExtractionTests : IDisposable
{

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "arcanum-backup-extract-" + Guid.NewGuid().ToString("N"));

    public BackupArchiveExtractionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {

        if (Directory.Exists(_root))
        {

            Directory.Delete(_root, recursive: true);

        }

    }

    [Fact]
    public async Task Extraction_materializes_every_authenticated_entry_under_the_supplied_root()
    {

        string archive = await WriteArchiveAsync(
            "extract.arcbackup",
            [
                ("configuration/arcanum.json", "{\"a\":1}"),
                ("authored/CODEX.md", "# codex"),
            ]);

        string destination = Path.Combine(_root, "destination");

        string scratch = Path.Combine(_root, "scratch");

        Directory.CreateDirectory(destination);

        Directory.CreateDirectory(scratch);

        BackupArchiveExtraction extraction = await Codec().ExtractAsync(
            archive,
            "extract passphrase".AsMemory(),
            destination,
            scratch,
            CancellationToken.None);

        Assert.Empty(extraction.Issues);

        Assert.Equal(2, extraction.Entries);

        Assert.Equal(
            "{\"a\":1}",
            await File.ReadAllTextAsync(
                Path.Combine(destination, "configuration", "arcanum.json")));

        Assert.Equal(
            "# codex",
            await File.ReadAllTextAsync(
                Path.Combine(destination, "authored", "CODEX.md")));

        Assert.Empty(Directory.GetFileSystemEntries(scratch));

    }

    [Fact]
    public async Task Extraction_reports_a_wrong_passphrase_without_writing_destination_bytes()
    {

        string archive = await WriteArchiveAsync(
            "wrong-passphrase.arcbackup",
            [("configuration/arcanum.json", "{}")]);

        string destination = Path.Combine(_root, "wrong-destination");

        string scratch = Path.Combine(_root, "wrong-scratch");

        Directory.CreateDirectory(destination);

        Directory.CreateDirectory(scratch);

        BackupArchiveExtraction extraction = await Codec().ExtractAsync(
            archive,
            "not the passphrase".AsMemory(),
            destination,
            scratch,
            CancellationToken.None);

        Assert.Contains(
            extraction.Issues,
            static issue => issue.Code == "backup.authentication_failed");

        Assert.Null(extraction.Manifest);

        Assert.Empty(Directory.GetFileSystemEntries(destination));

        Assert.Empty(Directory.GetFileSystemEntries(scratch));

    }

    [Fact]
    public async Task Extraction_rejects_a_newer_declared_format_before_touching_the_destination()
    {

        string archive = await WriteArchiveAsync(
            "newer-format.arcbackup",
            [("configuration/arcanum.json", "{}")]);

        byte[] bytes = await File.ReadAllBytesAsync(archive);

        bytes[11] = (byte)(BackupArchiveFormat.CurrentVersion + 1);

        await File.WriteAllBytesAsync(archive, bytes);

        string destination = Path.Combine(_root, "newer-destination");

        string scratch = Path.Combine(_root, "newer-scratch");

        Directory.CreateDirectory(destination);

        Directory.CreateDirectory(scratch);

        BackupArchiveExtraction extraction = await Codec().ExtractAsync(
            archive,
            "extract passphrase".AsMemory(),
            destination,
            scratch,
            CancellationToken.None);

        Assert.Contains(
            extraction.Issues,
            static issue => issue.Code == "backup.restore_format_newer");

        Assert.Empty(Directory.GetFileSystemEntries(destination));

    }

    [Fact]
    public async Task Extraction_refuses_a_destination_that_is_not_an_existing_directory()
    {

        string archive = await WriteArchiveAsync(
            "missing-destination.arcbackup",
            [("configuration/arcanum.json", "{}")]);

        string scratch = Path.Combine(_root, "missing-scratch");

        Directory.CreateDirectory(scratch);

        _ = await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => Codec().ExtractAsync(
                archive,
                "extract passphrase".AsMemory(),
                Path.Combine(_root, "absent"),
                scratch,
                CancellationToken.None));

    }

    /// <summary>
    /// A failure mid-extraction whose type the catch chain does not enumerate still leaves the
    /// destination as empty as it was found.
    /// </summary>
    /// <remarks>
    /// The method's own contract is absolute — "Any failure leaves the destination and scratch roots
    /// as empty as they were found" — but the destination cleanup was duplicated across three
    /// enumerated catches, so it held for a list of exception types rather than for every exit. What
    /// is left behind on the exit nobody enumerated is decrypted plaintext: entries already written
    /// under a root the caller was told is empty.
    ///
    /// <para>The failure is injected after an entry has been written, because before that there is
    /// nothing to clean up and the promise holds by accident. The type is deliberately one no catch
    /// names, which is the whole class the enumerated list cannot cover.</para>
    /// </remarks>
    [Fact]
    public async Task An_unenumerated_failure_after_the_first_entry_still_empties_the_destination()
    {

        string archive = await WriteArchiveAsync(
            "unenumerated.arcbackup",
            [("configuration/arcanum.json", "{\"a\":1}"), ("authored/CODEX.md", "# codex")]);

        string destination = Path.Combine(_root, "unenumerated-destination");

        string scratch = Path.Combine(_root, "unenumerated-scratch");

        Directory.CreateDirectory(destination);

        Directory.CreateDirectory(scratch);

        BackupArchiveCodec codec = new(new BackupArchiveCodecOptions
        {

            KdfIterations = 10_000,

            ChunkSize = 64 * 1024,

            AfterExtractedEntryForTests = static _ =>
                throw new InvalidOperationException("The extraction failed in an unenumerated way."),

        });

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => codec.ExtractAsync(
                archive,
                "extract passphrase".AsMemory(),
                destination,
                scratch,
                CancellationToken.None));

        Assert.Empty(
            Directory.GetFileSystemEntries(destination, "*", SearchOption.AllDirectories));

    }

    private static BackupArchiveCodec Codec() =>
        new(new BackupArchiveCodecOptions
        {

            KdfIterations = 10_000,

            ChunkSize = 64 * 1024,

        });

    private async Task<string> WriteArchiveAsync(
        string name,
        (string Path, string Content)[] entries)
    {

        string sourceRoot = Path.Combine(_root, "sources-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(sourceRoot);

        List<BackupArchiveSource> sources = [];

        List<BackupManifestEntry> manifestEntries = [];

        foreach ((string path, string content) in entries
                     .OrderBy(static entry => entry.Path, StringComparer.Ordinal))
        {

            string sourcePath = Path.Combine(sourceRoot, path.Replace('/', Path.DirectorySeparatorChar));

            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);

            await File.WriteAllTextAsync(sourcePath, content);

            byte[] bytes = Encoding.UTF8.GetBytes(content);

            sources.Add(new BackupArchiveSource(path, sourcePath));

            manifestEntries.Add(new BackupManifestEntry(
                path,
                bytes.LongLength,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),
                BackupComponent.Configuration));

        }

        BackupManifest manifest = new(
            BackupArchiveFormat.CurrentVersion,
            "0.1.0-beta",
            "0.1.0-beta+test",
            "not-applicable",
            DateTimeOffset.UtcNow,
            "test-platform",
            new BackupEnvelopeDescriptor("PBKDF2", "HMAC-SHA256", 0, string.Empty, "AES-256-GCM", 256, 12, 16, 65536),
            BackupScope.ConfigurationAndAuthoredAssets,
            SessionId: null,
            RequestedIncludes: [],
            RequestedExcludes: [],
            SecurityWarnings: [],
            Components: Enum.GetValues<BackupComponent>()
                .Select(component =>
                {

                    BackupManifestEntry[] owned = manifestEntries
                        .Where(entry => entry.Component == component)
                        .ToArray();

                    return new BackupManifestComponent(
                        component,
                        owned.Length > 0
                            ? BackupComponentStatus.Complete
                            : BackupComponentStatus.OmittedByPolicy,
                        owned.Length > 0 ? "Selected." : "Not selected by this scope.",
                        owned.LongLength,
                        owned.Sum(static entry => entry.Size));

                })
                .ToArray(),
            [.. manifestEntries]);

        string archive = Path.Combine(_root, name);

        _ = await Codec().WriteAsync(
            archive,
            manifest,
            sources,
            "extract passphrase".AsMemory(),
            overwrite: false,
            CancellationToken.None);

        return archive;

    }

}
