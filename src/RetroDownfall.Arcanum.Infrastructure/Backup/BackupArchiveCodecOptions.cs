using RetroDownfall.Arcanum.Core.Backup;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

/// <summary>
/// The authenticated result of materializing an archive beneath a protected staging root.
/// <see cref="Manifest"/> is present only when every structural, checksum, and database check
/// passed; otherwise <see cref="Issues"/> names the first refusal and the destination is empty.
/// </summary>
internal sealed record BackupArchiveExtraction(
    BackupManifest? Manifest,
    int FormatVersion,
    long Entries,
    long Bytes,
    bool DatabaseReadable,
    BackupVerifyIssue[] Issues)
{

    public static BackupArchiveExtraction Failed(
        int formatVersion,
        BackupVerifyIssue issue) =>
        new(
            Manifest: null,
            formatVersion,
            Entries: 0,
            Bytes: 0,
            DatabaseReadable: false,
            [issue]);

}

public sealed class BackupArchiveCodecOptions
{

    public const int DefaultChunkSize = 1024 * 1024;

    public const int DefaultKdfIterations = 600_000;

    public int ChunkSize { get; init; } = DefaultChunkSize;

    public int KdfIterations { get; init; } = DefaultKdfIterations;

    internal Func<byte[]>? SaltFactory { get; init; }

    internal Func<byte[]>? NoncePrefixFactory { get; init; }

    internal Action<string>? BeforeTemporaryPayloadCleanupForTests { get; init; }

    internal Action<string>? BeforeTemporaryExtractionCleanupForTests { get; init; }

    internal Action<int>? InspectPlaintextBufferSizeForTests { get; init; }

    internal Action<long>? InspectAuthenticatedPlaintextProgressForTests { get; init; }

}

public sealed class BackupArchiveSource
{

    private readonly ReadOnlyMemory<byte> _memory;

    public BackupArchiveSource(
        string archivePath,
        string sourcePath)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        ArchivePath = archivePath;

        SourcePath = sourcePath;

    }

    private BackupArchiveSource(
        string archivePath,
        ReadOnlyMemory<byte> memory)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        ArchivePath = archivePath;

        _memory = memory;

    }

    public string ArchivePath { get; }

    public string? SourcePath { get; }

    internal bool IsMemory => SourcePath is null;

    internal ReadOnlyMemory<byte> Memory => _memory;

    internal long Length => IsMemory
        ? _memory.Length
        : new FileInfo(SourcePath!).Length;

    internal static BackupArchiveSource FromMemory(
        string archivePath,
        ReadOnlyMemory<byte> memory) =>
        new(archivePath, memory);

}
