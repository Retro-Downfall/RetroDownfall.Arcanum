using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Backup;

public static class BackupArchiveFormat
{

    public const int CurrentVersion = 1;

    public const string Extension = ".arcbackup";

}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<BackupScope>))]
public enum BackupScope
{

    Full = 0,

    ConfigurationAndAuthoredAssets = 1,

    SessionsAndMemory = 2,

    SpecificSession = 3,

    MetadataOnly = 4,

}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<BackupComponent>))]
public enum BackupComponent
{

    GrimoireDatabase = 0,

    GrimoireKdfMetadata = 1,

    PortableRecoveryKeys = 2,

    Configuration = 3,

    SessionAttachments = 4,

    UploadedFiles = 5,

    BatchArtifacts = 6,

    GlobalCodex = 7,

    GlobalSpells = 8,

    McpConfiguration = 9,

    TrustedMcpWorkspaceMetadata = 10,

    CliState = 11,

    TheForgeState = 12,

    CompendiumSettings = 13,

    CompendiumCertificates = 14,

    AuditLogs = 15,

    GuardrailLogs = 16,

    MasterApiKey = 17,

}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<BackupComponentStatus>))]
public enum BackupComponentStatus
{

    Complete = 0,

    OmittedByPolicy = 1,

    Unavailable = 2,

    Failed = 3,

}

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<BackupCreateStatus>))]
public enum BackupCreateStatus
{

    Complete = 0,

    Incomplete = 1,

}

public sealed record BackupPlanRequest(
    BackupScope Scope,
    Guid? SessionId,
    BackupComponent[] Include,
    BackupComponent[] Exclude);

public sealed record BackupCreateRequest(
    BackupPlanRequest Plan,
    string? OutputPath,
    bool Overwrite);

public sealed record BackupPlanComponent(
    BackupComponent Component,
    BackupComponentStatus Status,
    string Detail,
    long Files,
    long EstimatedBytes,
    string[] NonportablePaths);

public sealed record BackupPlan(
    DateTimeOffset GeneratedAt,
    BackupScope Scope,
    Guid? SessionId,
    BackupPlanComponent[] Components,
    long EstimatedFiles,
    long EstimatedBytes,
    string[] MissingFiles,
    string[] SecurityWarnings);

public sealed record BackupManifestComponent(
    BackupComponent Component,
    BackupComponentStatus Status,
    string Detail,
    long Files,
    long Bytes);

public sealed record BackupManifestEntry(
    string Path,
    long Size,
    string Sha256,
    BackupComponent Component);

public sealed record BackupEnvelopeDescriptor(
    string Kdf,
    string Prf,
    int KdfIterations,
    string SaltBase64,
    string Encryption,
    int KeyBits,
    int NonceBytes,
    int TagBytes,
    int ChunkSize);

public sealed record BackupManifest(
    int FormatVersion,
    string ArcanumVersion,
    string Build,
    string DatabaseSchemaVersion,
    DateTimeOffset CreatedAt,
    string Platform,
    BackupEnvelopeDescriptor Envelope,
    BackupScope Scope,
    Guid? SessionId,
    BackupComponent[] RequestedIncludes,
    BackupComponent[] RequestedExcludes,
    string[] SecurityWarnings,
    BackupManifestComponent[] Components,
    BackupManifestEntry[] Entries);

public sealed record BackupArchiveHeader(
    int FormatVersion,
    string Kdf,
    int KdfIterations,
    string Encryption,
    int ChunkSize,
    DateTimeOffset CreatedAt,
    long EncryptedPayloadBytes);

public sealed record BackupInspectResult(
    string ArchivePath,
    long ArchiveBytes,
    BackupArchiveHeader Header,
    int FormatVersion,
    BackupManifest? Manifest);

public sealed record BackupVerifyIssue(
    string Code,
    string Message,
    string? Path = null);

public sealed record BackupVerifyResult(
    string ArchivePath,
    bool IsValid,
    int FormatVersion,
    long VerifiedEntries,
    long VerifiedBytes,
    bool DatabaseReadable,
    BackupVerifyIssue[] Issues,
    BackupManifest? Manifest);

public sealed record BackupCreateResult(
    BackupCreateStatus Status,
    string? ArchivePath,
    long ArchiveBytes,
    Guid OperationId,
    BackupManifest? Manifest,
    BackupPlan Plan,
    BackupVerifyIssue[] Issues);

public sealed record BackupListItem(
    string ArchivePath,
    long ArchiveBytes,
    BackupArchiveHeader Header);

public interface IBackupService
{

    Task<BackupPlan> PlanAsync(
        BackupPlanRequest request,
        CancellationToken cancellationToken = default);

    Task<BackupCreateResult> CreateAsync(
        BackupCreateRequest request,
        ReadOnlyMemory<char> recoveryPassphrase,
        CancellationToken cancellationToken = default);

    Task<BackupInspectResult> InspectAsync(
        string archivePath,
        ReadOnlyMemory<char>? recoveryPassphrase,
        CancellationToken cancellationToken = default);

    Task<BackupVerifyResult> VerifyAsync(
        string archivePath,
        ReadOnlyMemory<char> recoveryPassphrase,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BackupListItem>> ListAsync(
        string? directory,
        CancellationToken cancellationToken = default);

}
