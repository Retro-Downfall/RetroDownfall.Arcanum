using System.Collections.Immutable;

using System.Text.Json;

using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

/// <summary>
/// The durable commit journal for one restore. It records the identity of the live root, the staged
/// root, the displaced rollback artifact, and the phase reached, so a process that dies mid-restore
/// leaves behind enough to deterministically finish, roll back, or demand reconciliation — never a
/// mixture of old and new trees.
/// </summary>
/// <remarks>
/// This deliberately does not live in the Grimoire's <c>LongRunningOperations</c> table: a restore
/// replaces that database, so a durable record inside it would vanish exactly when it matters. The
/// journal is a plain owner-only file beside the staging root it describes.
/// </remarks>
internal sealed record BackupRestoreJournalRecord(
    int Version,
    Guid OperationId,
    BackupRestoreConflictMode ConflictMode,
    BackupRestorePhase Phase,
    string LiveRoot,
    string StagedRoot,
    string DisplacedRoot,
    string? SafetyBackupPath,
    string ArchivePath,
    ulong StagingVolumeId,
    ulong StagingFileId);

/// <summary>What kind of filesystem object one journalled topology slot names.</summary>
/// <remarks>
/// Closed and numbered because it is persisted inside an authenticated envelope and compared after a
/// restart. A directory slot that could hold a regular file would let the recorded live root be a file
/// the restore then renames a whole tree onto.
/// </remarks>
internal enum BackupRestoreNodeKind : byte
{

    Directory = 1,

    RegularFile = 2,

}

/// <summary>Whether a journalled node existed when the phase was frozen.</summary>
/// <remarks>
/// Two states rather than a nullable identity, because "the root exists" and "the root is absent" have
/// to be different recorded facts. A <c>NewProfileRoot</c> owner replayed over a root that meanwhile
/// came into existence would otherwise still match (§10.19.2).
/// </remarks>
internal enum BackupRestoreNodePresence : byte
{

    Absent = 1,

    Present = 2,

}

/// <summary>Whether a profile's restore anchor still governs a live operation.</summary>
/// <remarks>
/// <see cref="Closed"/> is not deletion. It is the anti-replay tombstone that keeps a terminal
/// operation's envelope from being presented again, and it is replaced only by a different new
/// operation after journal absence is proved.
/// </remarks>
internal enum BackupRestoreJournalAnchorState : byte
{

    Active = 1,

    Closed = 2,

}

/// <summary>
/// One filesystem node named the way physical recovery is allowed to reopen it.
/// </summary>
/// <remarks>
/// The parent's physical identity is the authority; the canonical parent path and the child leaf are
/// how the parent is found again, never why it is believed. Recovery reopens the recorded parent,
/// verifies its identity, opens only the recorded child without following links, and compares the node
/// identity before any rename or deletion — so a path that becomes a symlink between publication and
/// recovery redirects nothing.
///
/// <para>A present node carries its exact same-handle physical identity and an absent one carries
/// none, because a nullable identity standing in for both would make "I did not look" and "it was not
/// there" the same durable claim. Only a present regular file may carry content.</para>
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record BackupRestoreDurableNodeIdentityV1(
    string CanonicalParentPath,
    CovenantDigest ParentPhysicalIdentityDigest,
    string ChildLeaf,
    BackupRestoreNodeKind NodeKind,
    BackupRestoreNodePresence Presence,
    CovenantDigest? NodePhysicalIdentityDigest,
    CovenantDigest? ContentDigest);

/// <summary>
/// The exact Campaign-marker cleanup children one restore durably committed to its staged database.
/// </summary>
/// <remarks>
/// Copied field for field from <c>CampaignPathRestoreCleanupPreparationReceipt</c>, with the receipt's
/// single owner spelled as the three parts that survive a JSON round trip. Null means preparation has
/// not run; nonnull-empty means a proven zero inventory carrying the frozen
/// <c>CampaignPathRestoreCleanupIntentVector.EmptyDigest</c>. Collapsing those two into one state is
/// exactly what a crash between staged commit and journal publication would exploit (§10.19.5).
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record BackupRestoreMarkerCleanupCheckpointV1(
    byte Version,
    Guid OwnerOperationId,
    CovenantExclusiveOperation OwnerOperation,
    CovenantDigest OwnerEffectDigest,
    ImmutableArray<Guid> OrderedIntentIds,
    ulong IntentCount,
    CovenantDigest IntentVectorDigest)
{

    /// <summary>Compares the children element by element.</summary>
    /// <remarks>
    /// Spelled out because <see cref="ImmutableArray{T}"/> compares by underlying-array reference. Left
    /// to the compiler, a checkpoint reconstructed from the journal after a restart would compare
    /// unequal to the identical receipt preparation produced, and the one comparison that exists to
    /// prove a retry rebuilt the same vector would fail against a vector that is in fact the same.
    /// </remarks>
    public bool Equals(BackupRestoreMarkerCleanupCheckpointV1? other) =>
        other is not null
        && Version == other.Version
        && OwnerOperationId == other.OwnerOperationId
        && OwnerOperation == other.OwnerOperation
        && OwnerEffectDigest == other.OwnerEffectDigest
        && IntentCount == other.IntentCount
        && IntentVectorDigest == other.IntentVectorDigest
        && !OrderedIntentIds.IsDefault
        && !other.OrderedIntentIds.IsDefault
        && OrderedIntentIds.AsSpan().SequenceEqual(other.OrderedIntentIds.AsSpan());

    /// <inheritdoc />
    public override int GetHashCode()
    {

        HashCode hash = new();

        hash.Add(Version);

        hash.Add(OwnerOperationId);

        hash.Add(OwnerOperation);

        hash.Add(OwnerEffectDigest);

        hash.Add(IntentCount);

        hash.Add(IntentVectorDigest);

        foreach (Guid intentId in OrderedIntentIds.IsDefault ? [] : OrderedIntentIds)
        {

            hash.Add(intentId);

        }

        return hash.ToHashCode();

    }

}

/// <summary>
/// Everything one restore knows about itself, encrypted inside the V2 envelope.
/// </summary>
/// <remarks>
/// The exclusive recovery owner is stored as its three parts rather than as
/// <see cref="CovenantExclusiveRecoveryOwner"/> itself. That struct validates in its constructor and
/// throws, and source-generated JSON would have to construct it from hostile bytes before anything had
/// authenticated them — so the journal serializes the parts and rebuilds the owner only after the
/// envelope's tag has verified, exactly as recovery journals do for operation scopes.
///
/// <para>Carries no key, no authored content, and no marker payload: the recovery path decrypts this
/// before it has proven anything about the tree it describes.</para>
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record BackupRestoreJournalPayloadV2(
    Guid OwnerOperationId,
    CovenantExclusiveOperation OwnerOperation,
    CovenantDigest OwnerEffectDigest,
    BackupRestoreConflictMode ConflictMode,
    BackupRestorePhase Phase,
    CovenantDigest RestoreRequestDigest,
    BackupRestoreDurableNodeIdentityV1 LiveRoot,
    BackupRestoreDurableNodeIdentityV1 StagedRoot,
    BackupRestoreDurableNodeIdentityV1 DisplacedRoot,
    BackupRestoreDurableNodeIdentityV1 ArchiveSource,
    BackupRestoreDurableNodeIdentityV1? SafetyBackup,
    BackupRestoreMarkerCleanupCheckpointV1? MarkerCleanup)
{

    /// <summary>
    /// The complete exclusive owner, or a typed refusal when the recorded parts do not form one.
    /// </summary>
    internal Result<CovenantExclusiveRecoveryOwner> RecoveryOwner()
    {

        if (OwnerOperationId == Guid.Empty
            || OwnerOperation is not CovenantExclusiveOperation.BackupRestore
            || !OwnerEffectDigest.IsValid)
        {

            return new Error(
                ErrorCodes.Covenant.ForbiddenAuthority,
                "This restore journal does not name a complete backup-restore recovery owner.");

        }

        return new CovenantExclusiveRecoveryOwner(
            OwnerOperationId,
            OwnerOperation,
            OwnerEffectDigest);

    }

}

/// <summary>
/// The authenticated wrapper the restore journal file actually holds.
/// </summary>
/// <remarks>
/// Every field here is additional authenticated data, so a reader that needs the revision or the
/// operation before it decrypts still cannot be lied to about them. The nonce is twelve random bytes
/// per envelope rather than a counter: the key is installation-stable and outlives any counter this
/// process could keep, so a deterministic nonce would repeat across a crash under the same key.
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record BackupRestoreJournalEnvelopeV2(
    byte Version,
    CovenantDigest ProfileNamespaceDigest,
    Guid InstallationId,
    Guid OperationId,
    ulong Revision,
    CovenantDigest PreviousEnvelopeDigest,
    string NonceBase64Url,
    string CiphertextBase64Url,
    string AuthenticationTagBase64Url);

/// <summary>
/// The OS-secret anti-rollback anchor for one profile's restore journal.
/// </summary>
/// <remarks>
/// It lives in the OS credential store rather than beside the journal because a restore renames the
/// tree the journal sits in. An attacker who can rewrite the staging directory can present any
/// authentic earlier envelope; the anchor is the one piece of state that says which revision this
/// installation actually reached, and it is advanced only under the caller-held installation lock
/// through a checked read-compare-write-readback.
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record BackupRestoreJournalAnchorV1(
    byte Version,
    CovenantDigest ProfileNamespaceDigest,
    Guid InstallationId,
    Guid OperationId,
    ulong Revision,
    CovenantDigest EnvelopeDigest,
    CovenantDigest JournalLocationDigest,
    BackupRestoreJournalAnchorState State);

internal static class BackupRestoreJournal
{

    internal const int CurrentVersion = 1;

    internal const string FileName = "restore-journal.json";

    internal const string StagingPrefix = ".arcanum-restore-";

    internal const string StagedDirectoryName = "staged";

    internal const string DisplacedDirectoryName = "previous";

    internal const string WorkDirectoryName = "work";

    public static BackupRestoreJournalRecord Write(
        string stagingRoot,
        BackupRestoreJournalRecord record)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);

        ArgumentNullException.ThrowIfNull(record);

        string path = Path.Combine(Path.GetFullPath(stagingRoot), FileName);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            record,
            BackupJsonContext.Default.BackupRestoreJournalRecord);

        string temporaryPath = path + ".tmp." + Guid.NewGuid().ToString("N");

        using (FileStream stream = SecureFilePermissions.CreateOwnerOnlyTempFile(temporaryPath))
        {

            stream.Write(payload);

            stream.Flush(flushToDisk: true);

        }

        File.Move(temporaryPath, path, overwrite: true);

        return record;

    }

    public static BackupRestoreJournalRecord Advance(
        string stagingRoot,
        BackupRestoreJournalRecord record,
        BackupRestorePhase phase) =>
        Write(stagingRoot, record with { Phase = phase });

    public static BackupRestoreJournalRecord? TryRead(string stagingRoot)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);

        string path = Path.Combine(Path.GetFullPath(stagingRoot), FileName);

        try
        {

            if (!File.Exists(path))
            {

                return null;

            }

            BackupRestoreJournalRecord? record = JsonSerializer.Deserialize(
                File.ReadAllBytes(path),
                BackupJsonContext.Default.BackupRestoreJournalRecord);

            return record is null || record.Version != CurrentVersion
                ? null
                : record;

        }
        catch (Exception exception) when (
            exception is JsonException
                or NotSupportedException
                or IOException
                or UnauthorizedAccessException)
        {

            return null;

        }

    }

    public static bool Exists(string stagingRoot) =>
        TryRead(stagingRoot) is not null;

    public static void Delete(string stagingRoot)
    {

        string path = Path.Combine(Path.GetFullPath(stagingRoot), FileName);

        try
        {

            File.Delete(path);

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException)
        {

        }

    }

    /// <summary>
    /// Lists the canonically named restore staging roots directly beneath <paramref name="parent"/>
    /// that still hold a readable journal. A directory that merely looks like staging without a
    /// journal is not ours to touch.
    /// </summary>
    public static IReadOnlyList<string> Discover(string parent)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(parent);

        string fullParent = Path.GetFullPath(parent);

        if (!Directory.Exists(fullParent))
        {

            return [];

        }

        List<string> roots = [];

        try
        {

            foreach (string candidate in Directory.EnumerateDirectories(
                         fullParent,
                         StagingPrefix + "*",
                         SearchOption.TopDirectoryOnly))
            {

                string full = Path.GetFullPath(candidate);

                if (IsCanonicalStagingName(Path.GetFileName(full)) && Exists(full))
                {

                    roots.Add(full);

                }

            }

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException)
        {

            return [.. roots.Order(StringComparer.Ordinal)];

        }

        return [.. roots.Order(StringComparer.Ordinal)];

    }

    public static string CreateStagingName() =>
        StagingPrefix + Guid.NewGuid().ToString("N");

    internal static bool IsCanonicalStagingName(string name)
    {

        if (!name.StartsWith(StagingPrefix, StringComparison.Ordinal))
        {

            return false;

        }

        string suffix = name[StagingPrefix.Length..];

        if (suffix.Length != 32)
        {

            return false;

        }

        foreach (char character in suffix)
        {

            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {

                return false;

            }

        }

        return true;

    }

}
