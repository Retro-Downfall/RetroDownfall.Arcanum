using System.Security.Cryptography;

using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Core.Backup;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(BackupManifest))]
[JsonSerializable(typeof(PortableBackupRecoveryMaterial))]
[JsonSerializable(typeof(PortableBackupFileKey[]))]
[JsonSerializable(typeof(BackupOperationCheckpoint))]
[JsonSerializable(typeof(BackupRestoreJournalRecord))]
[JsonSerializable(typeof(BackupRestoreStagingIndexRecord))]

// The authenticated V2 restore journal. Every nested type is registered explicitly rather than left to
// the generator's transitive walk, so an added member whose type has no context entry is a build
// failure here rather than a reflection fallback on a Native AOT host — and the one place that would
// surface is a restart trying to authenticate a half-finished restore (§10.19.7).
[JsonSerializable(typeof(BackupRestoreJournalPayloadV2))]
[JsonSerializable(typeof(BackupRestoreJournalEnvelopeV2))]
[JsonSerializable(typeof(BackupRestoreJournalAnchorV1))]
[JsonSerializable(typeof(BackupRestoreDurableNodeIdentityV1))]
[JsonSerializable(typeof(BackupRestoreMarkerCleanupCheckpointV1))]
internal sealed partial class BackupJsonContext : JsonSerializerContext;

internal sealed record BackupOperationCheckpoint(
    int Version,
    BackupScope Scope,
    Guid? SessionId,
    BackupComponent[] Include,
    BackupComponent[] Exclude,
    string OutputPath,
    string StagingRoot,
    bool Overwrite,
    string Phase,
    ulong? StagingVolumeId = null,
    ulong? StagingFileId = null,

    // The disclosure subject and the receipts already acknowledged for it. Crash recovery closes or
    // resumes the same subject rather than opening a second one, so a known-unattempted effect is
    // never disclosed twice — and an attempt that did happen is never dropped from the count.
    Guid? DisclosureSubjectId = null,
    string? SnapshotReadReceiptDigestHex = null,
    ulong? SnapshotReadAttemptOrdinal = null,
    string? ArchiveWriteReceiptDigestHex = null,
    ulong? ArchiveWriteAttemptOrdinal = null);

internal sealed class PortableBackupRecoveryMaterial : IDisposable
{

    public required int Version { get; init; }

    public required byte[] GrimoireEncryptionSecretUtf8 { get; init; }

    public required string? ActiveFileEncryptionKeyId { get; init; }

    public required PortableBackupFileKey[] FileEncryptionKeys { get; init; }

    public byte[]? MasterApiKeyUtf8 { get; init; }

    public void Dispose()
    {

        CryptographicOperations.ZeroMemory(GrimoireEncryptionSecretUtf8);

        if (MasterApiKeyUtf8 is not null)
        {

            CryptographicOperations.ZeroMemory(MasterApiKeyUtf8);

        }

        foreach (PortableBackupFileKey key in FileEncryptionKeys)
        {

            CryptographicOperations.ZeroMemory(key.KeyBytes);

        }

    }

}

internal sealed record PortableBackupFileKey(
    string KeyId,
    byte[] KeyBytes);

internal static class BackupArchivePaths
{

    public const string Manifest = "manifest.json";

    public const string GrimoireDatabase = "grimoire/arcanum.db";

    public const string GrimoireKdf = "grimoire/arcanum.db.kdf";

    public const string Configuration = "configuration/arcanum.json";

    public const string ConfigurationPresetState =
        "configuration/arcanum.preset.json";

    public const string ConfigurationPresetRollback =
        "configuration/arcanum.preset.rollback.json";

    public const string PortableRecoveryKeys = "recovery/portable-keys.json";

    public const string MasterApiKey = "secrets/master-api-key.txt";

}
