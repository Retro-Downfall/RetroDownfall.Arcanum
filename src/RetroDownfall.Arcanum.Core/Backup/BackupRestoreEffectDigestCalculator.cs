using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Backup;

/// <summary>
/// Everything about a restore that decides what it is allowed to destroy.
/// </summary>
/// <remarks>
/// Deliberately excludes the dry-run and confirmation transport flags. Those authorize no effect, and
/// including them would make a rehearsal and the real restore it rehearsed produce different owners —
/// so the real run could not adopt the plan the operator just reviewed.
/// </remarks>
public sealed record BackupRestoreEffectDigestInput(
    CovenantDigest ArchiveManifestDigest,
    CovenantDigest ArchivePhysicalIdentityDigest,
    CovenantDigest ProfileNamespaceDigest,
    Guid InstallationId,
    CovenantDigest DestinationRootIdentityDigest,
    BackupRestoreConflictMode ConflictMode,
    BackupProtectedStateMode ProtectedStateMode,
    CovenantDigest PathMappingVectorDigest,
    bool RestoreMasterApiKey,
    bool CreateSafetyBackup);

/// <summary>
/// The sole producer of a restore's effect digest.
/// </summary>
/// <remarks>
/// One producer because the digest is the identity every later stage compares against: the exclusive
/// gate acquisition, the authenticated journal, each marker child row, and recovery's adoption check.
/// Two implementations that agreed on the day they were written would, at the first divergence, let a
/// retry with a changed plan resume a half-finished destructive sequence (§10.12).
/// </remarks>
public interface IBackupRestoreEffectDigestCalculator
{

    Result<CovenantDigest> Compute(BackupRestoreEffectDigestInput input);

}

/// <summary>
/// The one implementation.
/// </summary>
public sealed class BackupRestoreEffectDigestCalculator : IBackupRestoreEffectDigestCalculator
{

    public const string Domain = "Arcanum.Covenant.BackupRestore.Effect.v1";

    public const string PathMappingVectorDomain =
        "Arcanum.Covenant.BackupRestore.PathMappingVector.v1";

    public const string DestinationRootDomain =
        "Arcanum.Covenant.BackupRestore.DestinationRoot.v1";

    /// <summary>
    /// The ASCII wire value each protected-state choice contributes, length-prefixed.
    /// </summary>
    /// <remarks>
    /// The wire value rather than the numeric code, so a future reordering of the enum cannot silently
    /// turn one operator's "purge" into another's "restore" while producing the same digest.
    /// </remarks>
    private static readonly Dictionary<BackupProtectedStateMode, string> ProtectedStateWireValues = new()
    {
        [BackupProtectedStateMode.Reject] = "Reject",

        [BackupProtectedStateMode.RestoreProtectedState] = "RestoreProtectedState",

        [BackupProtectedStateMode.PurgeProtectedState] = "PurgeProtectedState",
    };

    public Result<CovenantDigest> Compute(BackupRestoreEffectDigestInput input)
    {

        if (input is null)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A restore effect digest requires a complete plan.");

        }

        // Selective import authorizes no installation-level effect at all; it uses the separate
        // protected-transfer digest. Accepting it here would mint an installation-wide owner for an
        // operation that never closes the installation.
        if (input.ConflictMode is not BackupRestoreConflictMode.ReplaceInstallation
            and not BackupRestoreConflictMode.NewProfileRoot)
        {

            return new Error(
                ErrorCodes.Covenant.InvalidScope,
                "Only a full replacement or a new profile root has a restore effect digest.");

        }

        if (!ProtectedStateWireValues.TryGetValue(input.ProtectedStateMode, out string? protectedState))
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "The protected-state mode is not one this build can authorize.");

        }

        if (input.InstallationId == Guid.Empty)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A restore effect digest requires a nonempty installation identity.");

        }

        if (!input.ArchiveManifestDigest.IsValid
            || !input.ArchivePhysicalIdentityDigest.IsValid
            || !input.ProfileNamespaceDigest.IsValid
            || !input.DestinationRootIdentityDigest.IsValid
            || !input.PathMappingVectorDigest.IsValid)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A restore effect digest cannot be computed over a malformed evidence digest.");

        }

        byte[] wire = Encoding.ASCII.GetBytes(protectedState);

        byte[] preimage = new byte[
            Domain.Length + 1 + 32 + 32 + 32 + 16 + 32 + 1 + sizeof(uint) + wire.Length + 32 + 1 + 1];

        int written = Encoding.ASCII.GetBytes(Domain, preimage);

        preimage[written++] = 0x00;

        written += Copy(preimage.AsSpan(written), input.ArchiveManifestDigest);

        written += Copy(preimage.AsSpan(written), input.ArchivePhysicalIdentityDigest);

        written += Copy(preimage.AsSpan(written), input.ProfileNamespaceDigest);

        _ = input.InstallationId.TryWriteBytes(preimage.AsSpan(written), bigEndian: true, out int uuidBytes);

        written += uuidBytes;

        written += Copy(preimage.AsSpan(written), input.DestinationRootIdentityDigest);

        preimage[written++] = (byte)input.ConflictMode;

        BinaryPrimitives.WriteUInt32BigEndian(preimage.AsSpan(written), (uint)wire.Length);

        written += sizeof(uint);

        wire.CopyTo(preimage.AsSpan(written));

        written += wire.Length;

        written += Copy(preimage.AsSpan(written), input.PathMappingVectorDigest);

        preimage[written++] = input.RestoreMasterApiKey ? (byte)1 : (byte)0;

        preimage[written++] = input.CreateSafetyBackup ? (byte)1 : (byte)0;

        return new CovenantDigest(SHA256.HashData(preimage.AsSpan(0, written)));

    }

    /// <summary>
    /// The canonical commitment to a destination that cannot be replayed against a different one.
    /// </summary>
    /// <remarks>
    /// Built from the no-follow parent's identity, the bounded child leaf, and whether the root is
    /// present — never from path text. A digest over the display path would let a symlink swap the
    /// destination out from under an owner that still compared equal.
    /// </remarks>
    public static CovenantDigest DestinationRootIdentity(
        CovenantDigest parentPhysicalIdentityDigest,
        string childLeaf,
        bool rootPresent)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(childLeaf);

        byte[] leaf = Encoding.UTF8.GetBytes(childLeaf);

        byte[] preimage = new byte[
            DestinationRootDomain.Length + 1 + 32 + sizeof(ushort) + leaf.Length + 1];

        int written = Encoding.ASCII.GetBytes(DestinationRootDomain, preimage);

        preimage[written++] = 0x00;

        written += Copy(preimage.AsSpan(written), parentPhysicalIdentityDigest);

        BinaryPrimitives.WriteUInt16BigEndian(preimage.AsSpan(written), checked((ushort)leaf.Length));

        written += sizeof(ushort);

        leaf.CopyTo(preimage.AsSpan(written));

        written += leaf.Length;

        preimage[written++] = rootPresent ? (byte)1 : (byte)0;

        return new CovenantDigest(SHA256.HashData(preimage.AsSpan(0, written)));

    }

    /// <summary>
    /// The canonical digest of the normalized path-mapping vector.
    /// </summary>
    /// <remarks>
    /// Ordered by kind, then source, then destination, with a checked count. Deterministic ordering is
    /// what makes two runs of the same operator request produce the same owner; an ordering that
    /// followed the operator's argument order would make a reordered command line a different
    /// destructive plan.
    /// </remarks>
    public static Result<CovenantDigest> PathMappingVector(ImmutableArray<BackupPathMapping> mappings)
    {

        if (mappings.IsDefault)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "An uninitialized path-mapping vector is not an empty one.");

        }

        BackupPathMapping[] ordered = [.. mappings
            .OrderBy(static mapping => (int)mapping.Kind)
            .ThenBy(static mapping => mapping.From, StringComparer.Ordinal)
            .ThenBy(static mapping => mapping.To, StringComparer.Ordinal)];

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        hash.AppendData(Encoding.ASCII.GetBytes(PathMappingVectorDomain));

        hash.AppendData([0x00]);

        Span<byte> count = stackalloc byte[sizeof(ulong)];

        BinaryPrimitives.WriteUInt64BigEndian(count, checked((ulong)ordered.Length));

        hash.AppendData(count);

        Span<byte> length = stackalloc byte[sizeof(uint)];

        foreach (BackupPathMapping mapping in ordered)
        {

            hash.AppendData([(byte)mapping.Kind]);

            foreach (string value in (string[])[mapping.From, mapping.To])
            {

                byte[] utf8 = Encoding.UTF8.GetBytes(value ?? string.Empty);

                BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)utf8.Length));

                hash.AppendData(length);

                hash.AppendData(utf8);

            }

        }

        return new CovenantDigest(hash.GetHashAndReset());

    }

    private static int Copy(Span<byte> destination, CovenantDigest digest)
    {

        digest.Bytes.CopyTo(destination);

        return 32;

    }

}
