using System.Buffers;

using System.Buffers.Binary;

using System.Security.Cryptography;

using System.Text;

using System.Text.Json;

using Microsoft.Data.Sqlite;
using SQLitePCL;

using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

public sealed class BackupArchiveCodec
{

    private const int FixedHeaderLength = 68;

    private const int SaltLength = 16;

    private const int NoncePrefixLength = 8;

    private const int NonceLength = 12;

    private const int TagLength = 16;

    private const int KeyLength = 32;

    private const int MaximumPathBytes = 4096;

    private const int MaximumManifestIdentityBytes = 4096;

    private const int MaximumManifestBytes = 64 * 1024 * 1024;

    private const int MaximumEntryCount = 1_000_000;

    private const int MaximumAcceptedKdfIterations = 10_000_000;

    private const int MaximumAcceptedChunkSize = 16 * 1024 * 1024;

    private const byte KdfPbkdf2Sha256 = 1;

    private const byte EncryptionAes256Gcm = 1;

    private static ReadOnlySpan<byte> Magic => "ARCABACK"u8;

    private readonly BackupArchiveCodecOptions _options;

    public BackupArchiveCodec(BackupArchiveCodecOptions? options = null)
    {

        _options = options ?? new BackupArchiveCodecOptions();

        if (_options.ChunkSize is < 16 or > MaximumAcceptedChunkSize)
        {

            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"Backup chunks must be between 16 and {MaximumAcceptedChunkSize} bytes.");

        }

        if (_options.KdfIterations is < 1 or > MaximumAcceptedKdfIterations)
        {

            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"Backup KDF iterations must be between 1 and {MaximumAcceptedKdfIterations}.");

        }

    }

    public Task<BackupManifest> WriteAsync(
        string destinationPath,
        BackupManifest manifest,
        IReadOnlyList<BackupArchiveSource> sources,
        ReadOnlyMemory<char> passphrase,
        bool overwrite,
        CancellationToken cancellationToken)
    {

        return WriteCoreAsync(
            destinationPath,
            manifest,
            sources,
            passphrase,
            overwrite,
            protectedScratchRoot: null,
            cancellationToken);

    }

    internal Task<BackupManifest> WriteAsync(
        string destinationPath,
        BackupManifest manifest,
        IReadOnlyList<BackupArchiveSource> sources,
        ReadOnlyMemory<char> passphrase,
        bool overwrite,
        string protectedScratchRoot,
        CancellationToken cancellationToken)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(protectedScratchRoot);

        return WriteCoreAsync(
            destinationPath,
            manifest,
            sources,
            passphrase,
            overwrite,
            protectedScratchRoot,
            cancellationToken);

    }

    private async Task<BackupManifest> WriteCoreAsync(
        string destinationPath,
        BackupManifest manifest,
        IReadOnlyList<BackupArchiveSource> sources,
        ReadOnlyMemory<char> passphrase,
        bool overwrite,
        string? protectedScratchRoot,
        CancellationToken cancellationToken)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        ArgumentNullException.ThrowIfNull(manifest);

        ArgumentNullException.ThrowIfNull(sources);

        ValidatePassphrase(passphrase.Span);

        cancellationToken.ThrowIfCancellationRequested();

        string fullDestinationPath = Path.GetFullPath(destinationPath);

        string directory = Path.GetDirectoryName(fullDestinationPath)
            ?? throw new InvalidOperationException("Backup destination has no parent directory.");

        string temporaryDirectory;

        if (protectedScratchRoot is null)
        {

            Directory.CreateDirectory(directory);

            temporaryDirectory = directory;

        }
        else
        {

            temporaryDirectory = ValidateProtectedScratchRoot(
                protectedScratchRoot,
                directory);

        }

        if (!overwrite && File.Exists(fullDestinationPath))
        {

            throw new IOException($"Backup destination already exists: {fullDestinationPath}");

        }

        ValidateManifestSources(manifest, sources);

        byte[] salt = CreateRandomBytes(_options.SaltFactory, SaltLength);

        byte[] noncePrefix = CreateRandomBytes(_options.NoncePrefixFactory, NoncePrefixLength);

        BackupEnvelopeDescriptor envelope = new(
            "PBKDF2",
            "HMAC-SHA256",
            _options.KdfIterations,
            Convert.ToBase64String(salt),
            "AES-256-GCM",
            256,
            NonceLength,
            TagLength,
            _options.ChunkSize);

        BackupManifest effectiveManifest = manifest with
        {

            FormatVersion = BackupArchiveFormat.CurrentVersion,

            Envelope = envelope,

        };

        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(
            effectiveManifest,
            BackupJsonContext.Default.BackupManifest);

        if (manifestBytes.Length > MaximumManifestBytes)
        {

            throw new InvalidDataException(
                $"Backup manifest exceeds {MaximumManifestBytes} bytes.");

        }

        long plaintextLength = CalculatePayloadLength(sources, manifestBytes.LongLength);

        byte[] header = BuildHeader(
            _options.KdfIterations,
            _options.ChunkSize,
            plaintextLength,
            effectiveManifest.CreatedAt,
            salt,
            noncePrefix);

        byte[] key = DeriveKey(passphrase.Span, salt, _options.KdfIterations);

        string tempPath = Path.Combine(
            temporaryDirectory,
            "." + Path.GetFileName(fullDestinationPath) + ".tmp." + Guid.NewGuid().ToString("N"));

        OwnedTemporaryFile? temporaryArchive = null;

        try
        {

            temporaryArchive = OwnedTemporaryFile.Create(
                tempPath,
                out FileStream outputStream);

            await using (FileStream output = outputStream)
            {

                await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);

                await using ChunkEncryptingWriter writer = new(
                    output,
                    header,
                    key,
                    noncePrefix,
                    _options.ChunkSize,
                    plaintextLength);

                for (int index = 0; index < sources.Count; index++)
                {

                    cancellationToken.ThrowIfCancellationRequested();

                    await WriteSourceRecordAsync(
                        writer,
                        sources[index],
                        effectiveManifest.Entries[index],
                        cancellationToken).ConfigureAwait(false);

                }

                await WriteRecordHeaderAsync(
                    writer,
                    BackupArchivePaths.Manifest,
                    manifestBytes.LongLength,
                    cancellationToken).ConfigureAwait(false);

                await writer.WriteAsync(manifestBytes, cancellationToken).ConfigureAwait(false);

                await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);

                output.Flush(flushToDisk: true);

            }

            BackupVerifyResult stagedVerification = await VerifyAsync(
                tempPath,
                passphrase,
                cancellationToken).ConfigureAwait(false);

            if (!stagedVerification.IsValid)
            {

                throw new InvalidDataException(
                    "The staged backup archive did not pass authentication and checksum verification.");

            }

            cancellationToken.ThrowIfCancellationRequested();

            if (!temporaryArchive.MatchesPathIdentity())
            {

                throw new IOException(
                    "The staged backup archive path changed before publication.");

            }

            File.Move(tempPath, fullDestinationPath, overwrite);

            if (!temporaryArchive.MatchesPathIdentity(fullDestinationPath)
                || !SecureFilePermissions.TryApplyOwnerOnlyFileStrict(fullDestinationPath)
                || !temporaryArchive.MatchesPathIdentity(fullDestinationPath))
            {

                _ = temporaryArchive.TryDeleteAtPath(fullDestinationPath);

                throw new IOException(
                    "The published backup archive could not be verified with owner-only permissions.");

            }

            return effectiveManifest;

        }
        finally
        {

            CryptographicOperations.ZeroMemory(key);

            CryptographicOperations.ZeroMemory(manifestBytes);

            CryptographicOperations.ZeroMemory(salt);

            CryptographicOperations.ZeroMemory(noncePrefix);

            _ = temporaryArchive?.TryDelete();

        }

    }

    private static string ValidateProtectedScratchRoot(
        string protectedScratchRoot,
        string destinationDirectory)
    {

        if (!Path.IsPathFullyQualified(protectedScratchRoot))
        {

            throw new ArgumentException(
                "The protected scratch root must be a fully qualified path.",
                nameof(protectedScratchRoot));

        }

        string fullScratchRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(protectedScratchRoot));

        if (!Directory.Exists(fullScratchRoot))
        {

            throw new DirectoryNotFoundException(
                $"The protected scratch root does not exist: {fullScratchRoot}");

        }

        string? scratchParent = Path.GetDirectoryName(fullScratchRoot);

        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (scratchParent is null
            || !string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(scratchParent)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDirectory)),
                comparison))
        {

            throw new ArgumentException(
                "The protected scratch root must be a direct child of the backup destination directory.",
                nameof(protectedScratchRoot));

        }

        return fullScratchRoot;

    }

    public async Task<BackupInspectResult> InspectAsync(
        string archivePath,
        ReadOnlyMemory<char>? passphrase,
        CancellationToken cancellationToken)
    {

        string fullPath = Path.GetFullPath(archivePath);

        ParsedHeader parsed = await ReadHeaderAsync(fullPath, cancellationToken).ConfigureAwait(false);

        BackupManifest? manifest = null;

        if (passphrase.HasValue)
        {

            ValidatePassphrase(passphrase.Value.Span);

            manifest = await ReadManifestFromAuthenticatedArchiveAsync(
                fullPath,
                parsed,
                passphrase.Value,
                _options.InspectPlaintextBufferSizeForTests,
                _options.InspectAuthenticatedPlaintextProgressForTests,
                cancellationToken).ConfigureAwait(false);

            ValidateManifest(
                manifest,
                parsed.Header,
                parsed.Raw.AsSpan(44, SaltLength));

        }

        FileInfo info = new(fullPath);

        return new BackupInspectResult(
            fullPath,
            info.Length,
            parsed.Header,
            parsed.Header.FormatVersion,
            manifest);

    }

    public Task<BackupVerifyResult> VerifyAsync(
        string archivePath,
        ReadOnlyMemory<char> passphrase,
        CancellationToken cancellationToken) =>
        VerifyAsync(archivePath, passphrase, protectedScratchRoot: null, cancellationToken);

    /// <summary>
    /// Authenticates an archive end to end, decrypting beneath <paramref name="protectedScratchRoot"/>.
    /// </summary>
    /// <remarks>
    /// Verification is the one archive operation with no destination of its own, so it has to be told
    /// where its scratch belongs. Unnamed, it falls back to the archive's own directory — right only
    /// for the staged self-verification inside a create, where the archive already sits in the
    /// caller's owner-only staging root. A standalone verify inheriting that default writes the
    /// decrypted payload and the entire extraction tree onto whatever volume the operator keeps
    /// backups on: removable media, a sync root, or a filesystem that cannot carry owner-only
    /// permissions at all, where a perfectly good archive is then reported as malformed.
    /// </remarks>
    public async Task<BackupVerifyResult> VerifyAsync(
        string archivePath,
        ReadOnlyMemory<char> passphrase,
        string? protectedScratchRoot,
        CancellationToken cancellationToken)
    {

        string fullPath = Path.GetFullPath(archivePath);

        int formatVersion = 0;

        OwnedTemporaryFile? payload = null;

        OwnedTemporaryDirectory? extraction = null;

        try
        {

            ValidatePassphrase(passphrase.Span);

            string scratchDirectory;

            if (string.IsNullOrWhiteSpace(protectedScratchRoot))
            {

                scratchDirectory = Path.GetDirectoryName(fullPath)!;

            }
            else
            {

                // Only a root this codec was handed is a root it may create or tighten. The fallback
                // is a directory the operator chose for something else — an archive output parent
                // whose existing permissions a create is required to leave exactly as it found them.
                scratchDirectory = Path.GetFullPath(protectedScratchRoot);

                SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(scratchDirectory);

            }

            ParsedHeader parsed = await ReadHeaderAsync(fullPath, cancellationToken).ConfigureAwait(false);

            formatVersion = parsed.Header.FormatVersion;

            payload = await DecryptToProtectedTemporaryAsync(
                fullPath,
                parsed,
                passphrase,
                scratchDirectory,
                cancellationToken).ConfigureAwait(false);

            extraction = CreateProtectedTemporaryDirectory(
                scratchDirectory,
                ".arcanum-backup-verify-");

            ExtractedPayload extracted = await ExtractPayloadAsync(
                payload.Path,
                extraction.Path,
                cancellationToken).ConfigureAwait(false);

            ValidateManifest(
                extracted.Manifest,
                parsed.Header,
                parsed.Raw.AsSpan(44, SaltLength));

            BackupVerifyIssue[] issues = CompareManifest(extracted.Manifest, extracted.Entries);

            bool databaseReadable = false;

            if (issues.Length == 0
                && extracted.Manifest.Entries.Any(
                    static entry => entry.Component == BackupComponent.GrimoireDatabase))
            {

                BackupVerifyIssue? databaseIssue = await VerifyDatabaseAsync(
                    extraction.Path,
                    extracted.Manifest,
                    cancellationToken).ConfigureAwait(false);

                if (databaseIssue is null)
                {

                    databaseReadable = true;

                }
                else
                {

                    issues = [databaseIssue];

                }

            }

            return new BackupVerifyResult(
                fullPath,
                issues.Length == 0,
                parsed.Header.FormatVersion,
                extracted.Entries.Count,
                extracted.Entries.Values.Sum(static entry => entry.Size),
                databaseReadable,
                issues,
                extracted.Manifest);

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            throw;

        }
        catch (CryptographicException)
        {

            return InvalidResult(
                fullPath,
                formatVersion,
                "backup.authentication_failed",
                "The backup passphrase is wrong or authenticated archive bytes were changed.");

        }
        catch (Exception ex) when (
            ex is InvalidDataException
                or JsonException
                or NotSupportedException
                or EndOfStreamException
                or IOException
                or UnauthorizedAccessException)
        {

            return InvalidResult(
                fullPath,
                formatVersion,
                "backup.invalid_archive",
                "The backup archive is malformed, incomplete, unsupported, or unreadable.");

        }
        finally
        {

            if (payload is not null)
            {

                _options.BeforeTemporaryPayloadCleanupForTests?.Invoke(
                    payload.Path);

                _ = payload.TryDelete();

            }

            if (extraction is not null)
            {

                _options.BeforeTemporaryExtractionCleanupForTests?.Invoke(
                    extraction.Path);

                _ = extraction.TryDelete();

            }

        }

    }

    /// <summary>
    /// Authenticates an archive and materializes every entry beneath a caller-owned protected root
    /// so a restore can stage a complete generation before it is allowed to touch live state. The
    /// declared format is classified against the supported matrix first, so an archive written by a
    /// newer Arcanum is refused before any destination byte exists. Any failure leaves the
    /// destination and scratch roots as empty as they were found.
    /// </summary>
    internal async Task<BackupArchiveExtraction> ExtractAsync(
        string archivePath,
        ReadOnlyMemory<char> passphrase,
        string destinationRoot,
        string scratchRoot,
        CancellationToken cancellationToken)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        string fullPath = Path.GetFullPath(archivePath);

        string fullDestination = RequireExistingDirectory(destinationRoot, nameof(destinationRoot));

        string fullScratch = RequireExistingDirectory(scratchRoot, nameof(scratchRoot));

        int formatVersion = 0;

        OwnedTemporaryFile? payload = null;

        bool extracted = false;

        try
        {

            ValidatePassphrase(passphrase.Span);

            formatVersion = await PeekFormatVersionAsync(fullPath, cancellationToken).ConfigureAwait(false);

            if (BackupRestoreFormatCatalog.Classify(formatVersion) is BackupVerifyIssue unsupported)
            {

                return BackupArchiveExtraction.Failed(formatVersion, unsupported);

            }

            ParsedHeader parsed = await ReadHeaderAsync(fullPath, cancellationToken).ConfigureAwait(false);

            payload = await DecryptToProtectedTemporaryAsync(
                fullPath,
                parsed,
                passphrase,
                fullScratch,
                cancellationToken).ConfigureAwait(false);

            extracted = true;

            ExtractedPayload contents = await ExtractPayloadAsync(
                payload.Path,
                fullDestination,
                cancellationToken).ConfigureAwait(false);

            ValidateManifest(
                contents.Manifest,
                parsed.Header,
                parsed.Raw.AsSpan(44, SaltLength));

            BackupVerifyIssue[] issues = CompareManifest(contents.Manifest, contents.Entries);

            bool databaseReadable = false;

            if (issues.Length == 0
                && contents.Manifest.Entries.Any(
                    static entry => entry.Component == BackupComponent.GrimoireDatabase))
            {

                BackupVerifyIssue? databaseIssue = await VerifyDatabaseAsync(
                    fullDestination,
                    contents.Manifest,
                    cancellationToken).ConfigureAwait(false);

                if (databaseIssue is null)
                {

                    databaseReadable = true;

                }
                else
                {

                    issues = [databaseIssue];

                }

            }

            if (issues.Length > 0)
            {

                ClearDirectoryContents(fullDestination);

                return new BackupArchiveExtraction(
                    Manifest: null,
                    formatVersion,
                    Entries: 0,
                    Bytes: 0,
                    DatabaseReadable: false,
                    issues);

            }

            return new BackupArchiveExtraction(
                contents.Manifest,
                formatVersion,
                contents.Entries.Count,
                contents.Entries.Values.Sum(static entry => entry.Size),
                databaseReadable,
                Issues: []);

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            if (extracted)
            {

                ClearDirectoryContents(fullDestination);

            }

            throw;

        }
        catch (CryptographicException)
        {

            ClearDirectoryContents(fullDestination);

            return BackupArchiveExtraction.Failed(
                formatVersion,
                new BackupVerifyIssue(
                    "backup.authentication_failed",
                    "The backup passphrase is wrong or authenticated archive bytes were changed."));

        }
        catch (Exception ex) when (
            ex is InvalidDataException
                or JsonException
                or NotSupportedException
                or EndOfStreamException
                or IOException
                or UnauthorizedAccessException)
        {

            ClearDirectoryContents(fullDestination);

            return BackupArchiveExtraction.Failed(
                formatVersion,
                new BackupVerifyIssue(
                    "backup.invalid_archive",
                    "The backup archive is malformed, incomplete, unsupported, or unreadable."));

        }
        finally
        {

            if (payload is not null)
            {

                _options.BeforeTemporaryPayloadCleanupForTests?.Invoke(payload.Path);

                _ = payload.TryDelete();

            }

        }

    }

    private static string RequireExistingDirectory(string path, string parameterName)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);

        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        return Directory.Exists(full)
            ? full
            : throw new DirectoryNotFoundException(
                $"The protected backup extraction root does not exist: {full}");

    }

    /// <summary>
    /// Reads only the magic and declared format version. Unlike <see cref="ReadHeaderAsync"/> this
    /// does not reject an unknown version, so the caller can report an actionable upgrade message
    /// instead of a generic malformed-archive failure.
    /// </summary>
    private static async Task<int> PeekFormatVersionAsync(
        string path,
        CancellationToken cancellationToken)
    {

        await using FileStream input = new(
            path,
            new FileStreamOptions
            {

                Mode = FileMode.Open,

                Access = FileAccess.Read,

                Share = FileShare.Read,

                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,

            });

        byte[] prefix = new byte[Magic.Length + sizeof(int)];

        await input.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);

        return CryptographicOperations.FixedTimeEquals(prefix.AsSpan(0, Magic.Length), Magic)
            ? BinaryPrimitives.ReadInt32BigEndian(prefix.AsSpan(Magic.Length))
            : throw new InvalidDataException("Backup archive magic is invalid.");

    }

    private static void ClearDirectoryContents(string root)
    {

        try
        {

            foreach (string entry in Directory.EnumerateFileSystemEntries(root))
            {

                if (Directory.Exists(entry) && !IsSymbolicLink(entry))
                {

                    Directory.Delete(entry, recursive: true);

                }
                else
                {

                    File.Delete(entry);

                }

            }

        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or DirectoryNotFoundException)
        {

        }

    }

    private static bool IsSymbolicLink(string path) =>
        new DirectoryInfo(path).LinkTarget is not null;

    private static BackupVerifyResult InvalidResult(
        string archivePath,
        int formatVersion,
        string code,
        string message) =>
        new(
            archivePath,
            IsValid: false,
            formatVersion,
            VerifiedEntries: 0,
            VerifiedBytes: 0,
            DatabaseReadable: false,
            [new BackupVerifyIssue(code, message)],
            Manifest: null);

    private static async Task WriteSourceRecordAsync(
        ChunkEncryptingWriter writer,
        BackupArchiveSource source,
        BackupManifestEntry expected,
        CancellationToken cancellationToken)
    {

        string archivePath = NormalizeArchivePath(source.ArchivePath);

        if (!string.Equals(archivePath, expected.Path, StringComparison.Ordinal))
        {

            throw new InvalidDataException("Backup source order does not match the manifest.");

        }

        if (source.IsMemory)
        {

            ReadOnlyMemory<byte> content = source.Memory;

            if (content.Length != expected.Size)
            {

                throw new IOException("An in-memory backup source changed before capture.");

            }

            string memoryHash = Convert.ToHexString(
                SHA256.HashData(content.Span)).ToLowerInvariant();

            if (!string.Equals(memoryHash, expected.Sha256, StringComparison.Ordinal))
            {

                throw new IOException("An in-memory backup source failed checksum verification.");

            }

            await WriteRecordHeaderAsync(
                writer,
                archivePath,
                expected.Size,
                cancellationToken).ConfigureAwait(false);

            await writer.WriteAsync(content, cancellationToken).ConfigureAwait(false);

            return;

        }

        string fullSourcePath = Path.GetFullPath(source.SourcePath!);

        SecureFileOpenStatus openStatus = SecureFileReader.TryOpenRegularFile(
            fullSourcePath,
            expectedIdentity: null,
            out FileStream? opened,
            out FileHandleMetadata openedMetadata);

        if (openStatus != SecureFileOpenStatus.Success || opened is null)
        {

            throw new IOException($"Backup source is missing or is not a regular file: {fullSourcePath}");

        }

        await using FileStream input = opened;

        long openedLength = input.Length;

        if (openedLength != expected.Size)
        {

            throw new IOException($"Backup source size changed before capture: {fullSourcePath}");

        }

        await WriteRecordHeaderAsync(
            writer,
            archivePath,
            expected.Size,
            cancellationToken).ConfigureAwait(false);

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        SecureFileReader.AfterOpenForTests?.Invoke(fullSourcePath);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);

        long copied = 0;

        try
        {

            while (true)
            {

                int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

                if (read == 0)
                {

                    break;

                }

                copied = checked(copied + read);

                if (copied > expected.Size)
                {

                    throw new IOException($"Backup source grew during capture: {fullSourcePath}");

                }

                hash.AppendData(buffer.AsSpan(0, read));

                await writer.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken).ConfigureAwait(false);

            }

        }
        finally
        {

            CryptographicOperations.ZeroMemory(buffer);

            ArrayPool<byte>.Shared.Return(buffer);

        }

        string actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

        if (copied != expected.Size
            || input.Length != openedLength
            || !SecureFileReader.TryValidateRegularFileHandle(
                input.SafeFileHandle,
                openedMetadata.Identity,
                out _)
            || !FileHandleIdentityInterop.TryGetPathMetadataNoFollow(
                fullSourcePath,
                out FileHandleMetadata finalPathMetadata)
            || finalPathMetadata.Kind != FileSystemObjectKind.RegularFile
            || finalPathMetadata.HardLinkCount != 1
            || !FileHandleIdentity.IdentitiesMatch(
                openedMetadata.Identity,
                finalPathMetadata.Identity)
            || !string.Equals(actualHash, expected.Sha256, StringComparison.Ordinal))
        {

            throw new IOException($"Backup source changed or failed checksum verification: {fullSourcePath}");

        }

    }

    private static async Task WriteRecordHeaderAsync(
        ChunkEncryptingWriter writer,
        string path,
        long length,
        CancellationToken cancellationToken)
    {

        string canonical = NormalizeArchivePath(path);

        byte[] pathBytes = Encoding.UTF8.GetBytes(canonical);

        if (pathBytes.Length > MaximumPathBytes)
        {

            throw new InvalidDataException("Backup archive path metadata is too large.");

        }

        byte[] header = new byte[sizeof(int) + pathBytes.Length + sizeof(long)];

        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, sizeof(int)), pathBytes.Length);

        pathBytes.CopyTo(header.AsSpan(sizeof(int)));

        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(sizeof(int) + pathBytes.Length), length);

        await writer.WriteAsync(header, cancellationToken).ConfigureAwait(false);

    }

    private static long CalculatePayloadLength(
        IReadOnlyList<BackupArchiveSource> sources,
        long manifestLength)
    {

        long length = RecordLength(BackupArchivePaths.Manifest, manifestLength);

        foreach (BackupArchiveSource source in sources)
        {

            length = checked(length + RecordLength(
                source.ArchivePath,
                source.Length));

        }

        return length;

    }

    private static long RecordLength(string path, long contentLength)
    {

        int pathLength = Encoding.UTF8.GetByteCount(NormalizeArchivePath(path));

        return checked(sizeof(int) + pathLength + sizeof(long) + contentLength);

    }

    private static void ValidateManifestSources(
        BackupManifest manifest,
        IReadOnlyList<BackupArchiveSource> sources)
    {

        if (manifest.Entries.Length != sources.Count)
        {

            throw new InvalidDataException("Backup manifest and source counts differ.");

        }

        if (sources.Count > MaximumEntryCount)
        {

            throw new InvalidDataException("Backup manifest contains too many entry metadata records.");

        }

        HashSet<string> paths = new(StringComparer.Ordinal);

        for (int index = 0; index < sources.Count; index++)
        {

            string sourcePath = NormalizeArchivePath(sources[index].ArchivePath);

            string manifestPath = NormalizeArchivePath(manifest.Entries[index].Path);

            if (!string.Equals(sourcePath, manifestPath, StringComparison.Ordinal)
                || !paths.Add(sourcePath)
                || string.Equals(sourcePath, BackupArchivePaths.Manifest, StringComparison.Ordinal))
            {

                throw new InvalidDataException("Backup archive paths are duplicate, reserved, or non-canonical.");

            }

            if (manifest.Entries[index].Size < 0
                || manifest.Entries[index].Sha256.Length != 64)
            {

                throw new InvalidDataException("Backup manifest entry metadata is invalid.");

            }

        }

    }

    internal static void ValidateManifest(
        BackupManifest manifest,
        BackupArchiveHeader header,
        ReadOnlySpan<byte> salt)
    {

        ArgumentNullException.ThrowIfNull(manifest);

        ArgumentNullException.ThrowIfNull(header);

        BackupEnvelopeDescriptor? envelope = manifest.Envelope;

        if (manifest.FormatVersion != header.FormatVersion
            || manifest.FormatVersion != BackupArchiveFormat.CurrentVersion)
        {

            throw new NotSupportedException("Backup manifest format version is unsupported.");

        }

        if (envelope is null
            || manifest.CreatedAt.ToUnixTimeMilliseconds()
                != header.CreatedAt.ToUnixTimeMilliseconds()
            || !string.Equals(header.Kdf, "PBKDF2-HMAC-SHA256", StringComparison.Ordinal)
            || !string.Equals(envelope.Kdf, "PBKDF2", StringComparison.Ordinal)
            || !string.Equals(envelope.Prf, "HMAC-SHA256", StringComparison.Ordinal)
            || envelope.KdfIterations != header.KdfIterations
            || !string.Equals(
                envelope.SaltBase64,
                Convert.ToBase64String(salt),
                StringComparison.Ordinal)
            || !string.Equals(header.Encryption, "AES-256-GCM", StringComparison.Ordinal)
            || !string.Equals(envelope.Encryption, "AES-256-GCM", StringComparison.Ordinal)
            || envelope.KeyBits != KeyLength * 8
            || envelope.NonceBytes != NonceLength
            || envelope.TagBytes != TagLength
            || envelope.ChunkSize != header.ChunkSize)
        {

            throw new InvalidDataException(
                "Backup manifest envelope does not match the authenticated archive header.");

        }

        if (manifest.RequestedIncludes is null
            || manifest.RequestedExcludes is null
            || manifest.SecurityWarnings is null
            || manifest.Components is null
            || manifest.Entries is null
            || !IsCanonicalManifestIdentity(manifest.ArcanumVersion)
            || !IsCanonicalManifestIdentity(manifest.Build)
            || !IsCanonicalManifestIdentity(manifest.DatabaseSchemaVersion)
            || !IsCanonicalManifestIdentity(manifest.Platform)
            || !Enum.IsDefined(manifest.Scope)
            || (manifest.Scope == BackupScope.SpecificSession
                && manifest.SessionId is null)
            || manifest.SecurityWarnings.Any(static warning => warning is null)
            || manifest.Components.Any(static item =>
                item is null
                || item.Detail is null
                || !Enum.IsDefined(item.Component)
                || !Enum.IsDefined(item.Status))
            || manifest.Entries.Any(static item =>
                item is null
                || string.IsNullOrWhiteSpace(item.Path)
                || item.Sha256 is null
                || !Enum.IsDefined(item.Component)))
        {

            throw new InvalidDataException(
                "Backup manifest contains invalid top-level or typed metadata.");

        }

        if (!IsCanonicalComponentSequence(manifest.RequestedIncludes)
            || !IsCanonicalComponentSequence(manifest.RequestedExcludes)
            || manifest.Entries.Length > MaximumEntryCount)
        {

            throw new InvalidDataException(
                "Backup manifest selections or entry count are not canonical.");

        }

        BackupComponent[] catalog = Enum.GetValues<BackupComponent>();

        if (manifest.Components.Length != catalog.Length)
        {

            throw new InvalidDataException(
                "Backup manifest must contain exactly one status for every component.");

        }

        Dictionary<BackupComponent, int> componentIndexes = new(catalog.Length);

        long[] entryFiles = new long[catalog.Length];

        long[] entryBytes = new long[catalog.Length];

        for (int index = 0; index < catalog.Length; index++)
        {

            BackupManifestComponent component = manifest.Components[index];

            if (component.Component != catalog[index]
                || component.Files < 0
                || component.Bytes < 0
                || component.Status == BackupComponentStatus.Failed
                || (component.Status != BackupComponentStatus.Complete
                    && (component.Files != 0 || component.Bytes != 0)))
            {

                throw new InvalidDataException(
                    "Backup manifest component status metadata is inconsistent.");

            }

            componentIndexes.Add(component.Component, index);

        }

        string? previousPath = null;

        foreach (BackupManifestEntry entry in manifest.Entries)
        {

            string canonical = NormalizeArchivePath(entry.Path);

            if (!string.Equals(canonical, entry.Path, StringComparison.Ordinal)
                || (previousPath is not null
                    && StringComparer.Ordinal.Compare(previousPath, canonical) >= 0)
                || string.Equals(canonical, BackupArchivePaths.Manifest, StringComparison.Ordinal)
                || entry.Size < 0
                || !IsLowerHexSha256(entry.Sha256))
            {

                throw new InvalidDataException("Backup manifest entry metadata is invalid.");

            }

            previousPath = canonical;

            int componentIndex = componentIndexes[entry.Component];

            if (manifest.Components[componentIndex].Status
                    != BackupComponentStatus.Complete
                || entryFiles[componentIndex] == long.MaxValue
                || entryBytes[componentIndex] > long.MaxValue - entry.Size)
            {

                throw new InvalidDataException(
                    "Backup manifest entry ownership or size accounting is invalid.");

            }

            entryFiles[componentIndex]++;

            entryBytes[componentIndex] += entry.Size;

        }

        for (int index = 0; index < manifest.Components.Length; index++)
        {

            BackupManifestComponent component = manifest.Components[index];

            if (component.Files != entryFiles[index]
                || component.Bytes != entryBytes[index])
            {

                throw new InvalidDataException(
                    "Backup manifest component totals do not match their entries.");

            }

        }

    }

    private static bool IsCanonicalComponentSequence(
        IReadOnlyList<BackupComponent> components)
    {

        int previous = -1;

        foreach (BackupComponent component in components)
        {

            int current = (int)component;

            if (!Enum.IsDefined(component) || current <= previous)
            {

                return false;

            }

            previous = current;

        }

        return true;

    }

    private static bool IsCanonicalManifestIdentity(string? value)
    {

        if (string.IsNullOrWhiteSpace(value)
            || Encoding.UTF8.GetByteCount(value) > MaximumManifestIdentityBytes
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {

            return false;

        }

        try
        {

            return value.IsNormalized(NormalizationForm.FormC);

        }
        catch (ArgumentException)
        {

            return false;

        }

    }

    private static bool IsLowerHexSha256(string value)
    {

        if (value.Length != 64)
        {

            return false;

        }

        foreach (char character in value)
        {

            if (character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f'))
            {

                return false;

            }

        }

        return true;

    }

    private static string NormalizeArchivePath(string path)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (path.IndexOf('\0') >= 0
            || path.Contains('\\', StringComparison.Ordinal)
            || Path.IsPathRooted(path))
        {

            throw new InvalidDataException("Backup archive path is not canonical.");

        }

        string normalized = path.Normalize(NormalizationForm.FormC);

        string[] segments = normalized.Split('/');

        if (segments.Any(static segment =>
            segment.Length == 0 || segment is "." or ".."))
        {

            throw new InvalidDataException("Backup archive path is not canonical.");

        }

        return string.Join('/', segments);

    }

    private static byte[] BuildHeader(
        int iterations,
        int chunkSize,
        long plaintextLength,
        DateTimeOffset createdAt,
        ReadOnlySpan<byte> salt,
        ReadOnlySpan<byte> noncePrefix)
    {

        byte[] header = new byte[FixedHeaderLength];

        Magic.CopyTo(header);

        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8), BackupArchiveFormat.CurrentVersion);

        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(12), FixedHeaderLength);

        header[16] = KdfPbkdf2Sha256;

        header[17] = EncryptionAes256Gcm;

        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(20), iterations);

        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(24), chunkSize);

        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(28), plaintextLength);

        BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(36), createdAt.ToUnixTimeMilliseconds());

        salt.CopyTo(header.AsSpan(44, SaltLength));

        noncePrefix.CopyTo(header.AsSpan(60, NoncePrefixLength));

        return header;

    }

    private static async Task<ParsedHeader> ReadHeaderAsync(
        string path,
        CancellationToken cancellationToken)
    {

        await using FileStream input = new(
            path,
            new FileStreamOptions
            {

                Mode = FileMode.Open,

                Access = FileAccess.Read,

                Share = FileShare.Read,

                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,

            });

        byte[] raw = new byte[FixedHeaderLength];

        await input.ReadExactlyAsync(raw, cancellationToken).ConfigureAwait(false);

        if (!CryptographicOperations.FixedTimeEquals(raw.AsSpan(0, Magic.Length), Magic))
        {

            throw new InvalidDataException("Backup archive magic is invalid.");

        }

        int version = BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(8));

        int headerLength = BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(12));

        int iterations = BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(20));

        int chunkSize = BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(24));

        long plaintextLength = BinaryPrimitives.ReadInt64BigEndian(raw.AsSpan(28));

        long createdMilliseconds = BinaryPrimitives.ReadInt64BigEndian(raw.AsSpan(36));

        if (version != BackupArchiveFormat.CurrentVersion
            || headerLength != FixedHeaderLength
            || raw[16] != KdfPbkdf2Sha256
            || raw[17] != EncryptionAes256Gcm
            || iterations is < 1 or > MaximumAcceptedKdfIterations
            || chunkSize is < 16 or > MaximumAcceptedChunkSize
            || plaintextLength < 0)
        {

            throw new NotSupportedException("Backup archive header is unsupported or invalid.");

        }

        long expectedLength = CalculateEnvelopeLength(plaintextLength, chunkSize);

        if (input.Length != expectedLength)
        {

            throw new InvalidDataException("Backup archive is truncated or contains trailing data.");

        }

        DateTimeOffset createdAt;

        try
        {

            createdAt = DateTimeOffset.FromUnixTimeMilliseconds(createdMilliseconds);

        }
        catch (ArgumentOutOfRangeException ex)
        {

            throw new InvalidDataException("Backup creation timestamp is invalid.", ex);

        }

        byte[] salt = raw.AsSpan(44, SaltLength).ToArray();

        byte[] noncePrefix = raw.AsSpan(60, NoncePrefixLength).ToArray();

        BackupArchiveHeader header = new(
            version,
            "PBKDF2-HMAC-SHA256",
            iterations,
            "AES-256-GCM",
            chunkSize,
            createdAt,
            plaintextLength);

        return new ParsedHeader(raw, header, plaintextLength, salt, noncePrefix);

    }

    private static long CalculateEnvelopeLength(long plaintextLength, int chunkSize)
    {

        long chunks = plaintextLength == 0
            ? 0
            : ((plaintextLength - 1) / chunkSize) + 1;

        try
        {

            return checked(
                FixedHeaderLength
                + plaintextLength
                + chunks * (sizeof(int) + TagLength));

        }
        catch (OverflowException ex)
        {

            throw new InvalidDataException(
                "Backup archive length metadata is invalid.",
                ex);

        }

    }

    private static Task<OwnedTemporaryFile> DecryptToProtectedTemporaryAsync(
        string archivePath,
        ParsedHeader parsed,
        ReadOnlyMemory<char> passphrase,
        CancellationToken cancellationToken) =>
        DecryptToProtectedTemporaryAsync(
            archivePath,
            parsed,
            passphrase,
            Path.GetDirectoryName(archivePath)!,
            cancellationToken);

    private static async Task<OwnedTemporaryFile> DecryptToProtectedTemporaryAsync(
        string archivePath,
        ParsedHeader parsed,
        ReadOnlyMemory<char> passphrase,
        string scratchDirectory,
        CancellationToken cancellationToken)
    {

        byte[] key = DeriveKey(passphrase.Span, parsed.Salt, parsed.Header.KdfIterations);

        string payloadPath = Path.Combine(
            scratchDirectory,
            "." + Path.GetFileName(archivePath) + ".payload.tmp." + Guid.NewGuid().ToString("N"));

        OwnedTemporaryFile? payload = null;

        try
        {

            await using FileStream input = new(
                archivePath,
                new FileStreamOptions
                {

                    Mode = FileMode.Open,

                    Access = FileAccess.Read,

                    Share = FileShare.Read,

                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,

                });

            input.Position = FixedHeaderLength;

            payload = OwnedTemporaryFile.Create(
                payloadPath,
                out FileStream outputStream);

            await using FileStream output = outputStream;

            using AesGcm aes = new(key, TagLength);

            byte[] cipher = new byte[parsed.Header.ChunkSize];

            byte[] plain = new byte[parsed.Header.ChunkSize];

            byte[] tag = new byte[TagLength];

            byte[] nonce = new byte[NonceLength];

            parsed.NoncePrefix.CopyTo(nonce, 0);

            byte[] associatedData = new byte[parsed.Raw.Length + sizeof(uint)];

            parsed.Raw.CopyTo(associatedData, 0);

            long remaining = parsed.PlaintextLength;

            uint chunkIndex = 0;

            try
            {

                while (remaining > 0)
                {

                    cancellationToken.ThrowIfCancellationRequested();

                    byte[] lengthBytes = new byte[sizeof(int)];

                    await input.ReadExactlyAsync(lengthBytes, cancellationToken).ConfigureAwait(false);

                    int length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);

                    int expected = (int)Math.Min(parsed.Header.ChunkSize, remaining);

                    if (length != expected)
                    {

                        throw new InvalidDataException("Backup encrypted chunk length is invalid.");

                    }

                    await input.ReadExactlyAsync(
                        cipher.AsMemory(0, length),
                        cancellationToken).ConfigureAwait(false);

                    await input.ReadExactlyAsync(tag, cancellationToken).ConfigureAwait(false);

                    BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(NoncePrefixLength), chunkIndex);

                    BinaryPrimitives.WriteUInt32BigEndian(
                        associatedData.AsSpan(parsed.Raw.Length),
                        chunkIndex);

                    aes.Decrypt(
                        nonce,
                        cipher.AsSpan(0, length),
                        tag,
                        plain.AsSpan(0, length),
                        associatedData);

                    await output.WriteAsync(
                        plain.AsMemory(0, length),
                        cancellationToken).ConfigureAwait(false);

                    CryptographicOperations.ZeroMemory(plain.AsSpan(0, length));

                    remaining -= length;

                    chunkIndex = checked(chunkIndex + 1);

                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);

                output.Flush(flushToDisk: true);

            }
            finally
            {

                CryptographicOperations.ZeroMemory(cipher);

                CryptographicOperations.ZeroMemory(plain);

                CryptographicOperations.ZeroMemory(tag);

                CryptographicOperations.ZeroMemory(nonce);

                CryptographicOperations.ZeroMemory(associatedData);

            }

            return payload;

        }
        catch
        {

            _ = payload?.TryDelete();

            throw;

        }
        finally
        {

            CryptographicOperations.ZeroMemory(key);

            CryptographicOperations.ZeroMemory(parsed.Salt);

            CryptographicOperations.ZeroMemory(parsed.NoncePrefix);

        }

    }

    private static async Task<BackupManifest> ReadManifestFromAuthenticatedArchiveAsync(
        string archivePath,
        ParsedHeader parsed,
        ReadOnlyMemory<char> passphrase,
        Action<int>? observePlaintextBuffer,
        Action<long>? observeAuthenticatedPlaintextProgress,
        CancellationToken cancellationToken)
    {

        await using AuthenticatedPayloadReader input =
            AuthenticatedPayloadReader.Create(
                archivePath,
                parsed,
                passphrase,
                observePlaintextBuffer,
                observeAuthenticatedPlaintextProgress);

        HashSet<string> paths = new(StringComparer.Ordinal);

        int count = 0;

        BackupManifest? manifest = null;

        while (input.Remaining > 0)
        {

            PayloadRecord record = await ReadStreamingRecordAsync(
                input,
                observePlaintextBuffer,
                cancellationToken).ConfigureAwait(false);

            if (!paths.Add(record.Path) || ++count > MaximumEntryCount + 1)
            {

                throw new InvalidDataException("Backup payload contains duplicate or excessive entries.");

            }

            if (string.Equals(record.Path, BackupArchivePaths.Manifest, StringComparison.Ordinal))
            {

                if (record.Length > MaximumManifestBytes
                    || record.Length != input.Remaining)
                {

                    throw new InvalidDataException("Backup manifest must be the final bounded entry.");

                }

                byte[] bytes = new byte[checked((int)record.Length)];

                if (bytes.Length > 0)
                {

                    observePlaintextBuffer?.Invoke(bytes.Length);

                }

                await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);

                try
                {

                    manifest = JsonSerializer.Deserialize(
                        bytes,
                        BackupJsonContext.Default.BackupManifest)
                        ?? throw new InvalidDataException("Backup manifest is empty.");

                }
                finally
                {

                    CryptographicOperations.ZeroMemory(bytes);

                }

            }
            else
            {

                await input.SkipExactlyAsync(
                    record.Length,
                    cancellationToken).ConfigureAwait(false);

            }

        }

        input.EnsureFullyConsumed();

        return manifest ?? throw new InvalidDataException("Backup manifest is missing.");

    }

    private static async Task<PayloadRecord> ReadStreamingRecordAsync(
        AuthenticatedPayloadReader input,
        Action<int>? observePlaintextBuffer,
        CancellationToken cancellationToken)
    {

        byte[] pathLengthBytes = new byte[sizeof(int)];

        observePlaintextBuffer?.Invoke(pathLengthBytes.Length);

        int pathLength;

        try
        {

            await input.ReadExactlyAsync(
                pathLengthBytes,
                cancellationToken).ConfigureAwait(false);

            pathLength = BinaryPrimitives.ReadInt32BigEndian(
                pathLengthBytes);

        }
        finally
        {

            CryptographicOperations.ZeroMemory(pathLengthBytes);

        }

        if (pathLength is < 1 or > MaximumPathBytes
            || pathLength > input.Remaining)
        {

            throw new InvalidDataException(
                "Backup payload path length is invalid.");

        }

        byte[] pathBytes = new byte[pathLength];

        observePlaintextBuffer?.Invoke(pathBytes.Length);

        string path;

        try
        {

            await input.ReadExactlyAsync(
                pathBytes,
                cancellationToken).ConfigureAwait(false);

            path = new UTF8Encoding(false, true).GetString(pathBytes);

        }
        finally
        {

            CryptographicOperations.ZeroMemory(pathBytes);

        }

        string canonical = NormalizeArchivePath(path);

        if (!string.Equals(path, canonical, StringComparison.Ordinal))
        {

            throw new InvalidDataException(
                "Backup payload path is not canonical.");

        }

        byte[] lengthBytes = new byte[sizeof(long)];

        observePlaintextBuffer?.Invoke(lengthBytes.Length);

        long length;

        try
        {

            await input.ReadExactlyAsync(
                lengthBytes,
                cancellationToken).ConfigureAwait(false);

            length = BinaryPrimitives.ReadInt64BigEndian(lengthBytes);

        }
        finally
        {

            CryptographicOperations.ZeroMemory(lengthBytes);

        }

        if (length < 0 || length > input.Remaining)
        {

            throw new InvalidDataException(
                "Backup payload entry length is invalid.");

        }

        return new PayloadRecord(path, length);

    }

    private static async Task<ExtractedPayload> ExtractPayloadAsync(
        string payloadPath,
        string extractionRoot,
        CancellationToken cancellationToken)
    {

        await using FileStream input = new(
            payloadPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);

        Dictionary<string, ObservedEntry> entries = new(StringComparer.Ordinal);

        BackupManifest? manifest = null;

        int count = 0;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);

        try
        {

            while (input.Position < input.Length)
            {

                PayloadRecord record = await ReadRecordAsync(input, cancellationToken).ConfigureAwait(false);

                if (++count > MaximumEntryCount + 1
                    || entries.ContainsKey(record.Path)
                    || (manifest is not null))
                {

                    throw new InvalidDataException("Backup payload entry topology is invalid.");

                }

                if (string.Equals(record.Path, BackupArchivePaths.Manifest, StringComparison.Ordinal))
                {

                    if (record.Length > MaximumManifestBytes
                        || input.Position + record.Length != input.Length)
                    {

                        throw new InvalidDataException("Backup manifest must be the final bounded entry.");

                    }

                    byte[] bytes = new byte[record.Length];

                    await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);

                    try
                    {

                        manifest = JsonSerializer.Deserialize(
                            bytes,
                            BackupJsonContext.Default.BackupManifest)
                            ?? throw new InvalidDataException("Backup manifest is empty.");

                    }
                    finally
                    {

                        CryptographicOperations.ZeroMemory(bytes);

                    }

                    continue;

                }

                string destination = ResolveExtractionPath(extractionRoot, record.Path);

                string? destinationDirectory = Path.GetDirectoryName(destination);

                if (destinationDirectory is not null)
                {

                    SecureFilePermissions.EnsureOwnerOnlyDirectoryExists(destinationDirectory);

                }

                using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                await using FileStream output = SecureFilePermissions.CreateOwnerOnlyTempFile(destination);

                long remaining = record.Length;

                while (remaining > 0)
                {

                    int wanted = (int)Math.Min(buffer.Length, remaining);

                    int read = await input.ReadAsync(
                        buffer.AsMemory(0, wanted),
                        cancellationToken).ConfigureAwait(false);

                    if (read == 0)
                    {

                        throw new EndOfStreamException();

                    }

                    hash.AppendData(buffer.AsSpan(0, read));

                    await output.WriteAsync(
                        buffer.AsMemory(0, read),
                        cancellationToken).ConfigureAwait(false);

                    remaining -= read;

                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);

                output.Flush(flushToDisk: true);

                entries.Add(
                    record.Path,
                    new ObservedEntry(
                        record.Length,
                        Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()));

            }

        }
        finally
        {

            CryptographicOperations.ZeroMemory(buffer);

            ArrayPool<byte>.Shared.Return(buffer);

        }

        return new ExtractedPayload(
            manifest ?? throw new InvalidDataException("Backup manifest is missing."),
            entries);

    }

    private static async Task<PayloadRecord> ReadRecordAsync(
        FileStream input,
        CancellationToken cancellationToken)
    {

        byte[] pathLengthBytes = new byte[sizeof(int)];

        await input.ReadExactlyAsync(pathLengthBytes, cancellationToken).ConfigureAwait(false);

        int pathLength = BinaryPrimitives.ReadInt32BigEndian(pathLengthBytes);

        if (pathLength is < 1 or > MaximumPathBytes
            || pathLength > input.Length - input.Position)
        {

            throw new InvalidDataException("Backup payload path length is invalid.");

        }

        byte[] pathBytes = new byte[pathLength];

        await input.ReadExactlyAsync(pathBytes, cancellationToken).ConfigureAwait(false);

        string path = new UTF8Encoding(false, true).GetString(pathBytes);

        string canonical = NormalizeArchivePath(path);

        if (!string.Equals(path, canonical, StringComparison.Ordinal))
        {

            throw new InvalidDataException("Backup payload path is not canonical.");

        }

        byte[] lengthBytes = new byte[sizeof(long)];

        await input.ReadExactlyAsync(lengthBytes, cancellationToken).ConfigureAwait(false);

        long length = BinaryPrimitives.ReadInt64BigEndian(lengthBytes);

        if (length < 0 || length > input.Length - input.Position)
        {

            throw new InvalidDataException("Backup payload entry length is invalid.");

        }

        return new PayloadRecord(path, length);

    }

    private static BackupVerifyIssue[] CompareManifest(
        BackupManifest manifest,
        IReadOnlyDictionary<string, ObservedEntry> observed)
    {

        List<BackupVerifyIssue> issues = [];

        Dictionary<BackupComponent, int> componentIndexes = manifest.Components
            .Select(static (component, index) => (component.Component, index))
            .ToDictionary(static item => item.Component, static item => item.index);

        long[] observedFiles = new long[manifest.Components.Length];

        long[] observedBytes = new long[manifest.Components.Length];

        if (manifest.Entries.Length != observed.Count)
        {

            issues.Add(new BackupVerifyIssue(
                "backup.entry_set_mismatch",
                "The encrypted payload and manifest contain different entry sets."));

        }

        foreach (BackupManifestEntry expected in manifest.Entries)
        {

            if (!observed.TryGetValue(expected.Path, out ObservedEntry? actual))
            {

                issues.Add(new BackupVerifyIssue(
                    "backup.entry_missing",
                    "A manifest-listed entry is missing from the encrypted payload.",
                    expected.Path));

                continue;

            }

            if (actual.Size != expected.Size
                || !string.Equals(actual.Sha256, expected.Sha256, StringComparison.Ordinal))
            {

                issues.Add(new BackupVerifyIssue(
                    "backup.checksum_mismatch",
                    "A manifest-listed entry failed size or SHA-256 verification.",
                    expected.Path));

            }

            int componentIndex = componentIndexes[expected.Component];

            observedFiles[componentIndex]++;

            observedBytes[componentIndex] = checked(
                observedBytes[componentIndex] + actual.Size);

        }

        for (int index = 0; index < manifest.Components.Length; index++)
        {

            BackupManifestComponent component = manifest.Components[index];

            if (component.Files != observedFiles[index]
                || component.Bytes != observedBytes[index])
            {

                issues.Add(new BackupVerifyIssue(
                    "backup.component_summary_mismatch",
                    "A component summary does not match its authenticated payload entries."));

            }

        }

        return [.. issues];

    }

    private static async Task<BackupVerifyIssue?> VerifyDatabaseAsync(
        string extractionRoot,
        BackupManifest manifest,
        CancellationToken cancellationToken)
    {

        string databasePath = ResolveExtractionPath(
            extractionRoot,
            BackupArchivePaths.GrimoireDatabase);

        string recoveryPath = ResolveExtractionPath(
            extractionRoot,
            BackupArchivePaths.PortableRecoveryKeys);

        if (!File.Exists(databasePath) || !File.Exists(recoveryPath))
        {

            return new BackupVerifyIssue(
                "backup.database_recovery_missing",
                "The database or its portable recovery material is missing.");

        }

        byte[] recoveryBytes = await File.ReadAllBytesAsync(
            recoveryPath,
            cancellationToken).ConfigureAwait(false);

        PortableBackupRecoveryMaterial? recovery = null;

        string? passphrase = null;

        try
        {

            recovery = JsonSerializer.Deserialize(
                recoveryBytes,
                BackupJsonContext.Default.PortableBackupRecoveryMaterial);

            if (recovery is null || recovery.Version != 1)
            {

                return new BackupVerifyIssue(
                    "backup.recovery_material_invalid",
                    "Portable recovery material is missing or unsupported.");

            }

            GrimoireKdfSidecar sidecar = GrimoireKdfSidecarFile.Read(databasePath);

            byte[] salt = sidecar.GetSaltBytes();

            string secret = Encoding.UTF8.GetString(recovery.GrimoireEncryptionSecretUtf8);

            try
            {

                passphrase = GrimoireKeyDerivation.DerivePassphraseFromEncryptionSecret(
                    secret,
                    salt);

            }
            finally
            {

                CryptographicOperations.ZeroMemory(salt);

            }

            SqliteNativeRuntime.Instance.Initialize();

            string connectionString = new SqliteConnectionStringBuilder
            {

                DataSource = databasePath,

                Password = passphrase,

                Mode = SqliteOpenMode.ReadOnly,

                Pooling = false,

            }.ToString();

            await using SqliteConnection connection = new(connectionString);

            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using SqliteCommand check = connection.CreateCommand();

            check.CommandText = "PRAGMA quick_check;";

            object? result = await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            if (!string.Equals(result as string, "ok", StringComparison.OrdinalIgnoreCase))
            {

                return new BackupVerifyIssue(
                    "backup.database_integrity_failed",
                    "The decrypted Grimoire snapshot failed SQLite quick_check.");

            }

            string actualSchema = await GrimoireSchemaIdentity
                .ComputeAsync(connection, cancellationToken)
                .ConfigureAwait(false);

            if (!string.Equals(
                    actualSchema,
                    manifest.DatabaseSchemaVersion,
                    StringComparison.Ordinal))
            {

                return new BackupVerifyIssue(
                    "backup.database_schema_mismatch",
                    "The Grimoire schema version does not match the authenticated manifest.");

            }

            return null;

        }
        catch (Exception ex) when (
            ex is SqliteException
                or InvalidDataException
                or JsonException
                or CryptographicException
                or FormatException)
        {

            return new BackupVerifyIssue(
                "backup.database_unreadable",
                "The Grimoire snapshot could not be opened and validated with portable recovery material.");

        }
        finally
        {

            if (passphrase is not null)
            {

                passphrase = null;

            }

            recovery?.Dispose();

            CryptographicOperations.ZeroMemory(recoveryBytes);

        }

    }

    private static string ResolveExtractionPath(string root, string archivePath)
    {

        string canonical = NormalizeArchivePath(archivePath);

        string destination = Path.GetFullPath(
            Path.Combine(root, canonical.Replace('/', Path.DirectorySeparatorChar)));

        string fullRoot = Path.GetFullPath(root);

        string prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        if (!destination.StartsWith(prefix, StringComparison.Ordinal))
        {

            throw new InvalidDataException("Backup extraction path escapes the protected root.");

        }

        return destination;

    }

    private static OwnedTemporaryDirectory CreateProtectedTemporaryDirectory(
        string parent,
        string prefix)
    {

        string path = Path.Combine(parent, prefix + Guid.NewGuid().ToString("N"));

        return OwnedTemporaryDirectory.Create(path);

    }

    private static byte[] DeriveKey(
        ReadOnlySpan<char> passphrase,
        ReadOnlySpan<byte> salt,
        int iterations)
    {

        int length = Encoding.UTF8.GetByteCount(passphrase);

        byte[] utf8 = new byte[length];

        Encoding.UTF8.GetBytes(passphrase, utf8);

        try
        {

            return Rfc2898DeriveBytes.Pbkdf2(
                utf8,
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                KeyLength);

        }
        finally
        {

            CryptographicOperations.ZeroMemory(utf8);

        }

    }

    private static void ValidatePassphrase(ReadOnlySpan<char> passphrase)
    {

        if (passphrase.IsEmpty)
        {

            throw new ArgumentException("A non-empty backup recovery passphrase is required.");

        }

    }

    private static byte[] CreateRandomBytes(Func<byte[]>? factory, int length)
    {

        byte[] bytes = factory?.Invoke() ?? RandomNumberGenerator.GetBytes(length);

        if (bytes.Length != length)
        {

            throw new InvalidOperationException($"Backup random value must contain {length} bytes.");

        }

        return bytes;

    }

    private sealed record ParsedHeader(
        byte[] Raw,
        BackupArchiveHeader Header,
        long PlaintextLength,
        byte[] Salt,
        byte[] NoncePrefix);

    private sealed record PayloadRecord(
        string Path,
        long Length);

    private sealed record ObservedEntry(
        long Size,
        string Sha256);

    private sealed record ExtractedPayload(
        BackupManifest Manifest,
        IReadOnlyDictionary<string, ObservedEntry> Entries);

    private sealed class AuthenticatedPayloadReader : IAsyncDisposable
    {

        private readonly FileStream _input;

        private readonly AesGcm _aes;

        private readonly byte[] _key;

        private readonly byte[] _cipher;

        private readonly byte[] _plain;

        private readonly byte[] _tag;

        private readonly byte[] _nonce;

        private readonly byte[] _associatedData;

        private readonly byte[] _lengthBytes;

        private readonly int _chunkSize;

        private readonly Action<long>? _observeAuthenticatedPlaintextProgress;

        private int _plainOffset;

        private int _plainLength;

        private uint _chunkIndex;

        private long _authenticatedPlaintext;

        private bool _disposed;

        private AuthenticatedPayloadReader(
            FileStream input,
            AesGcm aes,
            byte[] key,
            byte[] cipher,
            byte[] plain,
            byte[] tag,
            byte[] nonce,
            byte[] associatedData,
            byte[] lengthBytes,
            int chunkSize,
            long plaintextLength,
            Action<long>? observeAuthenticatedPlaintextProgress)
        {

            _input = input;

            _aes = aes;

            _key = key;

            _cipher = cipher;

            _plain = plain;

            _tag = tag;

            _nonce = nonce;

            _associatedData = associatedData;

            _lengthBytes = lengthBytes;

            _chunkSize = chunkSize;

            _observeAuthenticatedPlaintextProgress =
                observeAuthenticatedPlaintextProgress;

            Remaining = plaintextLength;

        }

        public long Remaining { get; private set; }

        public static AuthenticatedPayloadReader Create(
            string archivePath,
            ParsedHeader parsed,
            ReadOnlyMemory<char> passphrase,
            Action<int>? observePlaintextBuffer,
            Action<long>? observeAuthenticatedPlaintextProgress)
        {

            byte[] key = DeriveKey(
                passphrase.Span,
                parsed.Salt,
                parsed.Header.KdfIterations);

            FileStream? input = null;

            AesGcm? aes = null;

            byte[] cipher = [];

            byte[] plain = [];

            byte[] tag = [];

            byte[] nonce = [];

            byte[] associatedData = [];

            byte[] lengthBytes = [];

            try
            {

                input = new FileStream(
                    archivePath,
                    new FileStreamOptions
                    {

                        Mode = FileMode.Open,

                        Access = FileAccess.Read,

                        Share = FileShare.Read,

                        Options = FileOptions.Asynchronous
                            | FileOptions.SequentialScan,

                    });

                long expectedLength = CalculateEnvelopeLength(
                    parsed.PlaintextLength,
                    parsed.Header.ChunkSize);

                if (input.Length != expectedLength)
                {

                    throw new InvalidDataException(
                        "Backup archive changed before authenticated inspection.");

                }

                input.Position = FixedHeaderLength;

                aes = new AesGcm(key, TagLength);

                cipher = new byte[parsed.Header.ChunkSize];

                plain = new byte[parsed.Header.ChunkSize];

                tag = new byte[TagLength];

                nonce = new byte[NonceLength];

                parsed.NoncePrefix.CopyTo(nonce, 0);

                associatedData = new byte[parsed.Raw.Length + sizeof(uint)];

                parsed.Raw.CopyTo(associatedData, 0);

                lengthBytes = new byte[sizeof(int)];

                observePlaintextBuffer?.Invoke(plain.Length);

                AuthenticatedPayloadReader result = new(
                    input,
                    aes,
                    key,
                    cipher,
                    plain,
                    tag,
                    nonce,
                    associatedData,
                    lengthBytes,
                    parsed.Header.ChunkSize,
                    parsed.PlaintextLength,
                    observeAuthenticatedPlaintextProgress);

                input = null;

                aes = null;

                key = [];

                cipher = [];

                plain = [];

                tag = [];

                nonce = [];

                associatedData = [];

                lengthBytes = [];

                return result;

            }
            finally
            {

                input?.Dispose();

                aes?.Dispose();

                CryptographicOperations.ZeroMemory(key);

                CryptographicOperations.ZeroMemory(cipher);

                CryptographicOperations.ZeroMemory(plain);

                CryptographicOperations.ZeroMemory(tag);

                CryptographicOperations.ZeroMemory(nonce);

                CryptographicOperations.ZeroMemory(associatedData);

                CryptographicOperations.ZeroMemory(lengthBytes);

                CryptographicOperations.ZeroMemory(parsed.Salt);

                CryptographicOperations.ZeroMemory(parsed.NoncePrefix);

            }

        }

        public async ValueTask ReadExactlyAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken)
        {

            ThrowIfDisposed();

            if (destination.Length > Remaining)
            {

                throw new EndOfStreamException();

            }

            int written = 0;

            while (written < destination.Length)
            {

                cancellationToken.ThrowIfCancellationRequested();

                if (_plainOffset == _plainLength)
                {

                    await DecryptNextChunkAsync(
                        cancellationToken).ConfigureAwait(false);

                }

                int count = Math.Min(
                    _plainLength - _plainOffset,
                    destination.Length - written);

                _plain.AsMemory(_plainOffset, count).CopyTo(
                    destination[written..]);

                CryptographicOperations.ZeroMemory(
                    _plain.AsSpan(_plainOffset, count));

                _plainOffset += count;

                written += count;

                Remaining -= count;

            }

        }

        public async ValueTask SkipExactlyAsync(
            long length,
            CancellationToken cancellationToken)
        {

            ThrowIfDisposed();

            if (length < 0 || length > Remaining)
            {

                throw new InvalidDataException(
                    "Backup payload entry length is invalid.");

            }

            long skipped = 0;

            while (skipped < length)
            {

                cancellationToken.ThrowIfCancellationRequested();

                if (_plainOffset == _plainLength)
                {

                    await DecryptNextChunkAsync(
                        cancellationToken).ConfigureAwait(false);

                }

                int count = (int)Math.Min(
                    _plainLength - _plainOffset,
                    length - skipped);

                CryptographicOperations.ZeroMemory(
                    _plain.AsSpan(_plainOffset, count));

                _plainOffset += count;

                skipped += count;

                Remaining -= count;

            }

        }

        public void EnsureFullyConsumed()
        {

            ThrowIfDisposed();

            if (Remaining != 0
                || _plainOffset != _plainLength
                || _input.Position != _input.Length)
            {

                throw new InvalidDataException(
                    "Backup payload authentication did not consume the complete archive.");

            }

        }

        public async ValueTask DisposeAsync()
        {

            if (_disposed)
            {

                return;

            }

            _disposed = true;

            try
            {

                _aes.Dispose();

            }
            finally
            {

                try
                {

                    await _input.DisposeAsync().ConfigureAwait(false);

                }
                finally
                {

                    CryptographicOperations.ZeroMemory(_key);

                    CryptographicOperations.ZeroMemory(_cipher);

                    CryptographicOperations.ZeroMemory(_plain);

                    CryptographicOperations.ZeroMemory(_tag);

                    CryptographicOperations.ZeroMemory(_nonce);

                    CryptographicOperations.ZeroMemory(_associatedData);

                    CryptographicOperations.ZeroMemory(_lengthBytes);

                }

            }

        }

        private async ValueTask DecryptNextChunkAsync(
            CancellationToken cancellationToken)
        {

            if (Remaining <= 0)
            {

                throw new EndOfStreamException();

            }

            await _input.ReadExactlyAsync(
                _lengthBytes,
                cancellationToken).ConfigureAwait(false);

            int length = BinaryPrimitives.ReadInt32BigEndian(
                _lengthBytes);

            int expected = (int)Math.Min(_chunkSize, Remaining);

            if (length != expected)
            {

                throw new InvalidDataException(
                    "Backup encrypted chunk length is invalid.");

            }

            await _input.ReadExactlyAsync(
                _cipher.AsMemory(0, length),
                cancellationToken).ConfigureAwait(false);

            await _input.ReadExactlyAsync(
                _tag,
                cancellationToken).ConfigureAwait(false);

            BinaryPrimitives.WriteUInt32BigEndian(
                _nonce.AsSpan(NoncePrefixLength),
                _chunkIndex);

            BinaryPrimitives.WriteUInt32BigEndian(
                _associatedData.AsSpan(
                    _associatedData.Length - sizeof(uint)),
                _chunkIndex);

            _aes.Decrypt(
                _nonce,
                _cipher.AsSpan(0, length),
                _tag,
                _plain.AsSpan(0, length),
                _associatedData);

            _plainOffset = 0;

            _plainLength = length;

            _authenticatedPlaintext = checked(
                _authenticatedPlaintext + length);

            _observeAuthenticatedPlaintextProgress?.Invoke(
                _authenticatedPlaintext);

            _chunkIndex = checked(_chunkIndex + 1);

        }

        private void ThrowIfDisposed()
        {

            ObjectDisposedException.ThrowIf(
                _disposed,
                this);

        }

    }

    private sealed class ChunkEncryptingWriter : IAsyncDisposable
    {

        private readonly Stream _output;

        private readonly byte[] _header;

        private readonly byte[] _key;

        private readonly byte[] _noncePrefix;

        private readonly byte[] _buffer;

        private readonly long _expectedPlaintextLength;

        private int _buffered;

        private long _written;

        private uint _chunkIndex;

        private bool _completed;

        public ChunkEncryptingWriter(
            Stream output,
            byte[] header,
            byte[] key,
            byte[] noncePrefix,
            int chunkSize,
            long expectedPlaintextLength)
        {

            _output = output;

            _header = header;

            _key = key;

            _noncePrefix = noncePrefix;

            _buffer = new byte[chunkSize];

            _expectedPlaintextLength = expectedPlaintextLength;

        }

        public async ValueTask WriteAsync(
            ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken)
        {

            if (_completed)
            {

                throw new InvalidOperationException("Backup encryption writer is already complete.");

            }

            int offset = 0;

            while (offset < bytes.Length)
            {

                int count = Math.Min(_buffer.Length - _buffered, bytes.Length - offset);

                bytes.Span.Slice(offset, count).CopyTo(_buffer.AsSpan(_buffered));

                _buffered += count;

                offset += count;

                _written = checked(_written + count);

                if (_written > _expectedPlaintextLength)
                {

                    throw new InvalidDataException("Backup payload exceeded its authenticated length.");

                }

                if (_buffered == _buffer.Length)
                {

                    await EncryptBufferedAsync(cancellationToken).ConfigureAwait(false);

                }

            }

        }

        public async Task CompleteAsync(CancellationToken cancellationToken)
        {

            if (_completed)
            {

                return;

            }

            if (_buffered > 0)
            {

                await EncryptBufferedAsync(cancellationToken).ConfigureAwait(false);

            }

            if (_written != _expectedPlaintextLength)
            {

                throw new InvalidDataException("Backup payload did not match its authenticated length.");

            }

            _completed = true;

        }

        public ValueTask DisposeAsync()
        {

            CryptographicOperations.ZeroMemory(_buffer);

            return ValueTask.CompletedTask;

        }

        private async Task EncryptBufferedAsync(CancellationToken cancellationToken)
        {

            byte[] cipher = new byte[_buffered];

            byte[] tag = new byte[TagLength];

            byte[] nonce = new byte[NonceLength];

            _noncePrefix.CopyTo(nonce, 0);

            BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(NoncePrefixLength), _chunkIndex);

            byte[] associatedData = new byte[_header.Length + sizeof(uint)];

            _header.CopyTo(associatedData, 0);

            BinaryPrimitives.WriteUInt32BigEndian(
                associatedData.AsSpan(_header.Length),
                _chunkIndex);

            try
            {

                using AesGcm aes = new(_key, TagLength);

                aes.Encrypt(
                    nonce,
                    _buffer.AsSpan(0, _buffered),
                    cipher,
                    tag,
                    associatedData);

                byte[] lengthBytes = new byte[sizeof(int)];

                BinaryPrimitives.WriteInt32BigEndian(lengthBytes, _buffered);

                await _output.WriteAsync(lengthBytes, cancellationToken).ConfigureAwait(false);

                await _output.WriteAsync(cipher, cancellationToken).ConfigureAwait(false);

                await _output.WriteAsync(tag, cancellationToken).ConfigureAwait(false);

                CryptographicOperations.ZeroMemory(_buffer.AsSpan(0, _buffered));

                _buffered = 0;

                _chunkIndex = checked(_chunkIndex + 1);

            }
            finally
            {

                CryptographicOperations.ZeroMemory(cipher);

                CryptographicOperations.ZeroMemory(tag);

                CryptographicOperations.ZeroMemory(nonce);

                CryptographicOperations.ZeroMemory(associatedData);

            }

        }

    }

}
