using System.Buffers.Binary;

using System.Buffers.Text;

using System.Security.Cryptography;

using System.Text;

using System.Text.Json;

using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Covenant;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

/// <summary>
/// One profile root's namespace, derived from the handle it was opened through.
/// </summary>
/// <remarks>
/// Carries no path text into anything durable. The digest is what the three credential accounts are
/// suffixed with and what every envelope and anchor is bound to, so a journal, key, or anchor written
/// under one profile root can never authenticate under another.
/// </remarks>
internal sealed record BackupRestoreProfileNamespace(
    CovenantDigest Digest,
    CovenantDigest ParentPhysicalIdentityDigest,
    string ChildLeaf)
{

    /// <summary>The 64-character lowercase hex suffix every restore-journal account carries.</summary>
    internal string AccountSuffix => Convert.ToHexStringLower(Digest.Bytes);

}

/// <summary>
/// The one AES-256-GCM framing behind every authenticated restore journal.
/// </summary>
/// <remarks>
/// The journal is the only durable evidence a half-finished restore leaves behind, and a restart
/// believes it before it renames a live installation. So every field a reader consults before
/// decryption — version, profile namespace, installation, operation, revision, previous digest — is
/// additional authenticated data, and the payload itself is bounded, source-generated, and strict
/// about unknown members. A decoder that tolerated one unknown field would let a newer producer attach
/// a meaning this build silently drops while still holding a valid tag (§10.19.7).
///
/// <para>Every cryptographic failure collapses to one typed refusal. A decoder that distinguished
/// "wrong key" from "tampered tag" would be an oracle, and neither answer changes what recovery is
/// allowed to do: nothing.</para>
/// </remarks>
internal static class BackupRestoreJournalAuthenticator
{

    internal const byte EnvelopeVersion = 2;

    internal const byte AnchorVersion = 1;

    internal const byte MarkerCleanupCheckpointVersion = 1;

    internal const int NonceBytes = 12;

    internal const int TagBytes = 16;

    internal const int KeyBytes = 32;

    /// <summary>
    /// The ceiling on one decrypted payload.
    /// </summary>
    /// <remarks>
    /// Sized for the largest legal marker checkpoint — <see cref="CampaignPathRestoreCleanupIntentVector.MaximumIntents"/>
    /// identifiers plus five node identities — with room to spare, and checked before any buffer is
    /// allocated from a length the file claims.
    /// </remarks>
    internal const int MaxPayloadBytes = 512 * 1024;

    /// <summary>The ceiling on the encoded journal file, checked before it is read into memory.</summary>
    internal const int MaxJournalFileBytes = 1024 * 1024;

    /// <summary>The ceiling on the encoded anchor, which lives in a platform credential slot.</summary>
    internal const int MaxAnchorCharacters = 2048;

    /// <summary>
    /// The ceiling on any revision this build will believe.
    /// </summary>
    /// <remarks>
    /// One restore advances its journal once per phase plus retries, so a real revision is a small
    /// number. The bound exists so the checked <c>n + 1</c> in every advance and recovery path can
    /// never overflow: without it a stored `ulong.MaxValue` would leave the store as an unhandled
    /// <see cref="OverflowException"/> in the pre-database bootstrap instead of the typed blocker a
    /// malformed anchor is required to produce.
    /// </remarks>
    internal const ulong MaxRevision = 1_000_000;

    internal const int MaxCanonicalParentPathBytes = 4096;

    internal const int MaxChildLeafBytes = 255;

    internal const string ProfileNamespaceDomain = "Arcanum.BackupRestore.ProfileNamespace.v1";

    internal const string PhysicalIdentityDomain = "Arcanum.BackupRestore.PhysicalIdentity.v1";

    internal const string EnvelopeAssociatedDataDomain = "Arcanum.BackupRestore.JournalEnvelope.v2";

    internal const string EnvelopeDigestDomain = "Arcanum.BackupRestore.JournalEnvelopeDigest.v2";

    internal const string JournalLocationDomain = "Arcanum.BackupRestore.JournalLocation.v1";

    private const int EncodedNonceCharacters = 16;

    private const int EncodedTagCharacters = 22;

    /// <summary>
    /// The previous-envelope digest a first revision carries.
    /// </summary>
    /// <remarks>
    /// Thirty-two zero bytes rather than an absent field, so revision one has the same fixed-width
    /// authenticated shape as every later revision and cannot be distinguished from them by length.
    /// </remarks>
    internal static CovenantDigest ZeroDigest { get; } = new(new byte[CovenantLimits.DigestBytes]);

    /// <summary>
    /// The unkeyed commitment to one filesystem object's stable identity.
    /// </summary>
    /// <remarks>
    /// Unkeyed on purpose, unlike the Campaign root identity of §10.12. That key exists to make a
    /// directory identity unguessable; this digest exists to bind an envelope to a handle, it is
    /// already covered by the envelope's own authentication, and it has to be recomputable before any
    /// database or Campaign credential is available. Keying it would let the loss of an unrelated
    /// secret strand restore recovery.
    /// </remarks>
    internal static CovenantDigest PhysicalIdentity(ulong volumeId, ulong fileId)
    {

        byte[] preimage = new byte[PhysicalIdentityDomain.Length + 1 + sizeof(ulong) + sizeof(ulong)];

        int written = Encoding.ASCII.GetBytes(PhysicalIdentityDomain, preimage);

        preimage[written++] = 0x00;

        BinaryPrimitives.WriteUInt64BigEndian(preimage.AsSpan(written), volumeId);

        written += sizeof(ulong);

        BinaryPrimitives.WriteUInt64BigEndian(preimage.AsSpan(written), fileId);

        written += sizeof(ulong);

        return new CovenantDigest(SHA256.HashData(preimage.AsSpan(0, written)));

    }

    /// <summary>
    /// The profile namespace for a retained no-follow parent identity and one bounded child leaf.
    /// </summary>
    internal static Result<CovenantDigest> ProfileNamespace(
        CovenantDigest parentPhysicalIdentityDigest,
        string childLeaf)
    {

        if (!parentPhysicalIdentityDigest.IsValid)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A profile namespace cannot be derived without the retained parent identity.");

        }

        if (!TryEncodeLeaf(childLeaf, out byte[] leaf))
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A profile namespace requires exactly one bounded child name.");

        }

        byte[] preimage = new byte[
            ProfileNamespaceDomain.Length + 1 + CovenantLimits.DigestBytes + sizeof(ushort) + leaf.Length];

        int written = Encoding.ASCII.GetBytes(ProfileNamespaceDomain, preimage);

        preimage[written++] = 0x00;

        written += Copy(preimage.AsSpan(written), parentPhysicalIdentityDigest);

        BinaryPrimitives.WriteUInt16BigEndian(preimage.AsSpan(written), checked((ushort)leaf.Length));

        written += sizeof(ushort);

        leaf.CopyTo(preimage.AsSpan(written));

        written += leaf.Length;

        return new CovenantDigest(SHA256.HashData(preimage.AsSpan(0, written)));

    }

    /// <summary>
    /// Opens the configured profile root's parent without following links and derives its namespace.
    /// </summary>
    /// <remarks>
    /// Recomputed from the same parent handle on every startup rather than stored, so a namespace can
    /// never outlive the directory it names. It runs before any database opens, which is what gives
    /// each profile an independent pre-database binding.
    /// </remarks>
    internal static Result<BackupRestoreProfileNamespace> ResolveProfileNamespace(string profileRootPath)
    {

        if (string.IsNullOrWhiteSpace(profileRootPath))
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A profile namespace requires a configured profile root.");

        }

        string full;

        string? parent;

        string leaf;

        try
        {

            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profileRootPath));

            parent = Path.GetDirectoryName(full);

            leaf = Path.GetFileName(full);

        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "The configured profile root is not a resolvable path.");

        }

        if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(leaf))
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A profile root at a filesystem root has no parent to bind its namespace to.");

        }

        if (!FileHandleIdentityInterop.TryGetPathMetadataNoFollow(parent, out FileHandleMetadata metadata)
            || metadata.Kind is not FileSystemObjectKind.Directory)
        {

            return new Error(
                ErrorCodes.Covenant.Unavailable,
                "The profile root's parent could not be opened as a directory without following links.");

        }

        CovenantDigest parentDigest = PhysicalIdentity(
            metadata.Identity.VolumeId,
            metadata.Identity.FileId);

        Result<CovenantDigest> digest = ProfileNamespace(parentDigest, leaf);

        return digest.IsFailure
            ? Result<BackupRestoreProfileNamespace>.Failure(digest.Error)
            : new BackupRestoreProfileNamespace(digest.Value, parentDigest, leaf);

    }

    /// <summary>
    /// The commitment to where one operation's journal lives, containing no path text.
    /// </summary>
    internal static Result<CovenantDigest> JournalLocation(
        CovenantDigest profileNamespaceDigest,
        Guid installationId,
        Guid operationId,
        CovenantDigest guardedParentPhysicalIdentityDigest,
        string journalChildLeaf)
    {

        if (!profileNamespaceDigest.IsValid || !guardedParentPhysicalIdentityDigest.IsValid)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A journal location cannot be derived over a malformed evidence digest.");

        }

        if (installationId == Guid.Empty || operationId == Guid.Empty)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A journal location requires a nonempty installation and operation identity.");

        }

        if (!TryEncodeLeaf(journalChildLeaf, out byte[] leaf))
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A journal location requires exactly one bounded child name.");

        }

        byte[] preimage = new byte[
            JournalLocationDomain.Length
            + 1
            + CovenantLimits.DigestBytes
            + 16
            + 16
            + CovenantLimits.DigestBytes
            + sizeof(ushort)
            + leaf.Length];

        int written = Encoding.ASCII.GetBytes(JournalLocationDomain, preimage);

        preimage[written++] = 0x00;

        written += Copy(preimage.AsSpan(written), profileNamespaceDigest);

        if (!TryWriteUuid(preimage.AsSpan(written), installationId, ref written)
            || !TryWriteUuid(preimage.AsSpan(written), operationId, ref written))
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A journal location identity could not be encoded.");

        }

        written += Copy(preimage.AsSpan(written), guardedParentPhysicalIdentityDigest);

        BinaryPrimitives.WriteUInt16BigEndian(preimage.AsSpan(written), checked((ushort)leaf.Length));

        written += sizeof(ushort);

        leaf.CopyTo(preimage.AsSpan(written));

        written += leaf.Length;

        return new CovenantDigest(SHA256.HashData(preimage.AsSpan(0, written)));

    }

    /// <summary>
    /// The digest over the complete canonical envelope, including ciphertext and tag.
    /// </summary>
    /// <remarks>
    /// Includes the ciphertext and the tag precisely so that a resealed payload at the same revision is
    /// a different envelope. Without them, an owner who still holds the key could rewrite history at a
    /// revision the anchor already accepted.
    /// </remarks>
    internal static Result<CovenantDigest> EnvelopeDigest(BackupRestoreJournalEnvelopeV2 envelope)
    {

        if (envelope is null)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "An envelope digest requires a complete envelope.");

        }

        if (!envelope.ProfileNamespaceDigest.IsValid || !envelope.PreviousEnvelopeDigest.IsValid)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "An envelope digest cannot be computed over a malformed evidence digest.");

        }

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        hash.AppendData(Encoding.ASCII.GetBytes(EnvelopeDigestDomain));

        hash.AppendData([0x00, envelope.Version]);

        hash.AppendData(envelope.ProfileNamespaceDigest.Bytes);

        hash.AppendData(envelope.InstallationId.ToByteArray(bigEndian: true));

        hash.AppendData(envelope.OperationId.ToByteArray(bigEndian: true));

        Span<byte> revision = stackalloc byte[sizeof(ulong)];

        BinaryPrimitives.WriteUInt64BigEndian(revision, envelope.Revision);

        hash.AppendData(revision);

        hash.AppendData(envelope.PreviousEnvelopeDigest.Bytes);

        Span<byte> length = stackalloc byte[sizeof(uint)];

        foreach (string value in (string[])
                 [
                     envelope.NonceBase64Url,
                     envelope.CiphertextBase64Url,
                     envelope.AuthenticationTagBase64Url,
                 ])
        {

            byte[] ascii = Encoding.ASCII.GetBytes(value ?? string.Empty);

            BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)ascii.Length));

            hash.AppendData(length);

            hash.AppendData(ascii);

        }

        return new CovenantDigest(hash.GetHashAndReset());

    }

    /// <summary>
    /// Encrypts one payload into a V2 envelope, consuming the lease's single take of the key.
    /// </summary>
    internal static Result<BackupRestoreJournalEnvelopeV2> Seal(
        BackupRestoreJournalKeyLease key,
        CovenantDigest profileNamespaceDigest,
        Guid installationId,
        ulong revision,
        CovenantDigest previousEnvelopeDigest,
        BackupRestoreJournalPayloadV2 payload)
    {

        ArgumentNullException.ThrowIfNull(key);

        if (payload is null)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A restore journal envelope requires a complete payload.");

        }

        Result validated = ValidatePayload(payload);

        if (validated.IsFailure)
        {

            return Result<BackupRestoreJournalEnvelopeV2>.Failure(validated.Error);

        }

        if (!profileNamespaceDigest.IsValid || !previousEnvelopeDigest.IsValid)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A restore journal envelope cannot be sealed over a malformed evidence digest.");

        }

        if (installationId == Guid.Empty || revision is 0 or > MaxRevision)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A restore journal envelope requires a nonempty installation and a bounded positive "
                + "revision.");

        }

        byte[] plaintext;

        try
        {

            plaintext = JsonSerializer.SerializeToUtf8Bytes(
                payload,
                BackupJsonContext.Default.BackupRestoreJournalPayloadV2);

        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "This restore journal payload could not be encoded canonically.");

        }

        // From here the plaintext exists, so every exit has to scrub it. A capacity or lease refusal
        // that returned early would hand the complete serialized journal to the GC unscrubbed, where
        // it outlives the decision that rejected it.
        try
        {

            if (plaintext.Length > MaxPayloadBytes)
            {

                return new Error(
                    ErrorCodes.Covenant.CapacityExceeded,
                    "This restore journal payload exceeds the bound one guarded-root journal may carry.");

            }

            byte[] nonce = RandomNumberGenerator.GetBytes(NonceBytes);

            byte[] ciphertext = new byte[plaintext.Length];

            byte[] tag = new byte[TagBytes];

            byte[] associatedData = AssociatedData(
                EnvelopeVersion,
                profileNamespaceDigest,
                installationId,
                payload.OwnerOperationId,
                revision,
                previousEnvelopeDigest);

            if (!key.TryTakeKey(out byte[]? material))
            {

                return new Error(
                    ErrorCodes.Covenant.LifecycleConflict,
                    "This restore journal key lease has already been spent.");

            }

            try
            {

                using AesGcm aes = new(material, TagBytes);

                aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

            }
            catch (CryptographicException)
            {

                return new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    "This restore journal envelope could not be sealed.");

            }
            finally
            {

                CryptographicOperations.ZeroMemory(material);

            }

            return new BackupRestoreJournalEnvelopeV2(
                EnvelopeVersion,
                profileNamespaceDigest,
                installationId,
                payload.OwnerOperationId,
                revision,
                previousEnvelopeDigest,
                Base64Url.EncodeToString(nonce),
                Base64Url.EncodeToString(ciphertext),
                Base64Url.EncodeToString(tag));

        }
        finally
        {

            CryptographicOperations.ZeroMemory(plaintext);

        }

    }

    /// <summary>
    /// Authenticates one envelope against the expected profile and installation, then decrypts it.
    /// </summary>
    /// <remarks>
    /// Nothing inside the envelope is believed until the tag verifies, and the profile namespace and
    /// installation identity are compared before the key is even consulted — a cross-profile envelope
    /// must fail without ever reaching the key of the profile it was presented to.
    /// </remarks>
    internal static Result<BackupRestoreJournalPayloadV2> Open(
        BackupRestoreJournalKeyLease key,
        CovenantDigest expectedProfileNamespaceDigest,
        Guid expectedInstallationId,
        BackupRestoreJournalEnvelopeV2 envelope)
    {

        ArgumentNullException.ThrowIfNull(key);

        if (envelope is null)
        {

            return Invalid();

        }

        if (envelope.Version != EnvelopeVersion)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "This restore journal envelope is not a version this build can authenticate.");

        }

        if (!expectedProfileNamespaceDigest.IsValid
            || !envelope.ProfileNamespaceDigest.IsValid
            || envelope.ProfileNamespaceDigest != expectedProfileNamespaceDigest)
        {

            return new Error(
                ErrorCodes.Covenant.ForbiddenAuthority,
                "This restore journal envelope was written under a different profile namespace.");

        }

        if (expectedInstallationId == Guid.Empty || envelope.InstallationId != expectedInstallationId)
        {

            return new Error(
                ErrorCodes.Covenant.ForbiddenAuthority,
                "This restore journal envelope was written under a different installation identity.");

        }

        if (envelope.OperationId == Guid.Empty
            || envelope.Revision is 0 or > MaxRevision
            || !envelope.PreviousEnvelopeDigest.IsValid)
        {

            return Invalid();

        }

        if (!TryDecodeExact(envelope.NonceBase64Url, EncodedNonceCharacters, NonceBytes, out byte[] nonce)
            || !TryDecodeExact(envelope.AuthenticationTagBase64Url, EncodedTagCharacters, TagBytes, out byte[] tag))
        {

            return Invalid();

        }

        if (!TryDecodeBounded(envelope.CiphertextBase64Url, MaxPayloadBytes, out byte[] ciphertext))
        {

            return Invalid();

        }

        byte[] associatedData = AssociatedData(
            envelope.Version,
            envelope.ProfileNamespaceDigest,
            envelope.InstallationId,
            envelope.OperationId,
            envelope.Revision,
            envelope.PreviousEnvelopeDigest);

        byte[] plaintext = new byte[ciphertext.Length];

        if (!key.TryTakeKey(out byte[]? material))
        {

            return new Error(
                ErrorCodes.Covenant.LifecycleConflict,
                "This restore journal key lease has already been spent.");

        }

        try
        {

            using AesGcm aes = new(material, TagBytes);

            aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);

        }
        catch (CryptographicException)
        {

            CryptographicOperations.ZeroMemory(plaintext);

            return Invalid();

        }
        finally
        {

            CryptographicOperations.ZeroMemory(material);

        }

        BackupRestoreJournalPayloadV2? payload;

        try
        {

            payload = JsonSerializer.Deserialize(
                plaintext,
                BackupJsonContext.Default.BackupRestoreJournalPayloadV2);

        }
        catch (Exception exception) when (IsDecodeFailure(exception))
        {

            return Invalid();

        }
        finally
        {

            CryptographicOperations.ZeroMemory(plaintext);

        }

        if (payload is null)
        {

            return Invalid();

        }

        if (payload.OwnerOperationId != envelope.OperationId)
        {

            return new Error(
                ErrorCodes.Covenant.ForbiddenAuthority,
                "This restore journal payload names a different operation than its envelope.");

        }

        Result validated = ValidatePayload(payload);

        return validated.IsFailure
            ? Result<BackupRestoreJournalPayloadV2>.Failure(validated.Error)
            : payload;

    }

    /// <summary>
    /// Every rule the payload's closed shape freezes, checked before any field is believed.
    /// </summary>
    internal static Result ValidatePayload(BackupRestoreJournalPayloadV2 payload)
    {

        if (payload is null)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A restore journal payload cannot be absent.");

        }

        Result<CovenantExclusiveRecoveryOwner> owner = payload.RecoveryOwner();

        if (owner.IsFailure)
        {

            return owner.Error;

        }

        if (payload.ConflictMode is not BackupRestoreConflictMode.ReplaceInstallation
            and not BackupRestoreConflictMode.NewProfileRoot)
        {

            return new Error(
                ErrorCodes.Covenant.InvalidScope,
                "Only a full replacement or a new profile root has an authenticated restore journal.");

        }

        if (!Enum.IsDefined(payload.Phase))
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "This restore journal names a phase this build cannot resume.");

        }

        if (!payload.RestoreRequestDigest.IsValid)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A restore journal requires the exact restore-request digest.");

        }

        foreach ((BackupRestoreDurableNodeIdentityV1? node, BackupRestoreNodeKind kind) in
                 (ValueTuple<BackupRestoreDurableNodeIdentityV1?, BackupRestoreNodeKind>[])
                 [
                     (payload.LiveRoot, BackupRestoreNodeKind.Directory),
                     (payload.StagedRoot, BackupRestoreNodeKind.Directory),
                     (payload.DisplacedRoot, BackupRestoreNodeKind.Directory),
                     (payload.ArchiveSource, BackupRestoreNodeKind.RegularFile),
                     (payload.SafetyBackup, BackupRestoreNodeKind.Directory),
                 ])
        {

            if (node is null)
            {

                continue;

            }

            Result validated = ValidateNode(node, kind);

            if (validated.IsFailure)
            {

                return validated;

            }

        }

        if (payload.LiveRoot is null || payload.StagedRoot is null
            || payload.DisplacedRoot is null || payload.ArchiveSource is null)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A restore journal requires durable identity evidence for every mandatory node.");

        }

        Result shape = ValidatePhaseShape(payload);

        if (shape.IsFailure)
        {

            return shape;

        }

        return ValidateMarkerCleanup(payload, owner.Value);

    }

    internal static Result<byte[]> EncodeEnvelope(BackupRestoreJournalEnvelopeV2 envelope)
    {

        if (envelope is null)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "An envelope cannot be encoded without its fields.");

        }

        try
        {

            byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(
                envelope,
                BackupJsonContext.Default.BackupRestoreJournalEnvelopeV2);

            return encoded.Length <= MaxJournalFileBytes
                ? encoded
                : new Error(
                    ErrorCodes.Covenant.CapacityExceeded,
                    "This restore journal exceeds the bound one guarded-root file may carry.");

        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "This restore journal envelope could not be encoded canonically.");

        }

    }

    internal static Result<BackupRestoreJournalEnvelopeV2> DecodeEnvelope(ReadOnlySpan<byte> utf8)
    {

        if (utf8.Length is 0 or > MaxJournalFileBytes)
        {

            return Invalid<BackupRestoreJournalEnvelopeV2>();

        }

        BackupRestoreJournalEnvelopeV2? envelope;

        try
        {

            envelope = JsonSerializer.Deserialize(
                utf8,
                BackupJsonContext.Default.BackupRestoreJournalEnvelopeV2);

        }
        catch (Exception exception) when (IsDecodeFailure(exception))
        {

            return Invalid<BackupRestoreJournalEnvelopeV2>();

        }

        if (envelope is null)
        {

            return Invalid<BackupRestoreJournalEnvelopeV2>();

        }

        Result canonical = RequireCanonicalEncoding(
            utf8,
            EncodeEnvelope(envelope));

        return canonical.IsFailure
            ? Invalid<BackupRestoreJournalEnvelopeV2>()
            : envelope;

    }

    internal static Result<string> EncodeAnchor(BackupRestoreJournalAnchorV1 anchor)
    {

        Result validated = ValidateAnchor(anchor);

        if (validated.IsFailure)
        {

            return Result<string>.Failure(validated.Error);

        }

        try
        {

            string encoded = JsonSerializer.Serialize(
                anchor,
                BackupJsonContext.Default.BackupRestoreJournalAnchorV1);

            return encoded.Length <= MaxAnchorCharacters
                ? encoded
                : new Error(
                    ErrorCodes.Covenant.CapacityExceeded,
                    "This restore journal anchor exceeds the bound one credential slot may carry.");

        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "This restore journal anchor could not be encoded canonically.");

        }

    }

    internal static Result<BackupRestoreJournalAnchorV1> DecodeAnchor(string? value)
    {

        if (string.IsNullOrEmpty(value) || value.Length > MaxAnchorCharacters)
        {

            return Invalid<BackupRestoreJournalAnchorV1>();

        }

        BackupRestoreJournalAnchorV1? anchor;

        try
        {

            anchor = JsonSerializer.Deserialize(
                value,
                BackupJsonContext.Default.BackupRestoreJournalAnchorV1);

        }
        catch (Exception exception) when (IsDecodeFailure(exception))
        {

            return Invalid<BackupRestoreJournalAnchorV1>();

        }

        if (anchor is null)
        {

            return Invalid<BackupRestoreJournalAnchorV1>();

        }

        Result validated = ValidateAnchor(anchor);

        if (validated.IsFailure)
        {

            return Result<BackupRestoreJournalAnchorV1>.Failure(validated.Error);

        }

        Result<string> canonical = EncodeAnchor(anchor);

        return canonical.IsSuccess && string.Equals(canonical.Value, value, StringComparison.Ordinal)
            ? anchor
            : Invalid<BackupRestoreJournalAnchorV1>();

    }

    /// <summary>
    /// Requires the decoded value to re-encode byte-for-byte to what was read.
    /// </summary>
    /// <remarks>
    /// This is what makes "canonical" true below the record level. The
    /// <see cref="JsonUnmappedMemberHandlingAttribute"/> on each record rejects an unknown member on
    /// that record — but a digest is a nested Core type this slice does not own, so an unknown member
    /// or a duplicated one *inside* a digest object would otherwise be dropped in silence while the
    /// surrounding evidence still verified. Round-tripping rejects unknown nested members, duplicate
    /// members, reordering, and whitespace in one comparison, on exactly the two surfaces an attacker
    /// can present directly: the journal file and the anchor's credential slot.
    /// </remarks>
    private static Result RequireCanonicalEncoding(ReadOnlySpan<byte> read, Result<byte[]> reencoded) =>
        reencoded.IsSuccess && read.SequenceEqual(reencoded.Value)
            ? Result.Success()
            : new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "This restore journal evidence is not in its one canonical encoding.");

    /// <summary>
    /// Every way decoding hostile bytes can fail.
    /// </summary>
    /// <remarks>
    /// <see cref="ArgumentException"/> belongs here because <c>CovenantDigest</c>'s JSON constructor
    /// validates its own length and throws, and <c>System.Text.Json</c> does not wrap exceptions
    /// thrown from a user constructor. Without it, a journal or anchor carrying a short digest would
    /// leave this method as an unhandled exception in the pre-database bootstrap instead of the typed
    /// blocker a truncated encoding is required to produce.
    /// </remarks>
    private static bool IsDecodeFailure(Exception exception) =>
        exception is JsonException or NotSupportedException or ArgumentException;

    internal static Result ValidateAnchor(BackupRestoreJournalAnchorV1 anchor)
    {

        if (anchor is null)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A restore journal anchor cannot be absent.");

        }

        if (anchor.Version != AnchorVersion)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "This restore journal anchor is not a version this build can authenticate.");

        }

        if (!anchor.ProfileNamespaceDigest.IsValid
            || !anchor.EnvelopeDigest.IsValid
            || !anchor.JournalLocationDigest.IsValid)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A restore journal anchor cannot carry a malformed evidence digest.");

        }

        if (anchor.InstallationId == Guid.Empty || anchor.OperationId == Guid.Empty)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A restore journal anchor requires a nonempty installation and operation identity.");

        }

        if (anchor.Revision > MaxRevision)
        {

            return new Error(
                ErrorCodes.Covenant.CapacityExceeded,
                "This restore journal anchor names a revision beyond the bound one operation may reach.");

        }

        return anchor.State is BackupRestoreJournalAnchorState.Active
            or BackupRestoreJournalAnchorState.Closed
            ? Result.Success()
            : new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "This restore journal anchor names a state this build cannot resume.");

    }

    private static Result ValidateNode(
        BackupRestoreDurableNodeIdentityV1 node,
        BackupRestoreNodeKind expectedKind)
    {

        if (node.NodeKind != expectedKind)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "This restore journal slot names the wrong kind of filesystem object.");

        }

        if (!node.ParentPhysicalIdentityDigest.IsValid)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "Every journalled node requires the retained no-follow parent identity.");

        }

        if (string.IsNullOrEmpty(node.CanonicalParentPath)
            || !Path.IsPathRooted(node.CanonicalParentPath)
            || Encoding.UTF8.GetByteCount(node.CanonicalParentPath) > MaxCanonicalParentPathBytes
            || HasTraversalSegment(node.CanonicalParentPath))
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A journalled node requires a bounded absolute canonical parent path.");

        }

        if (!TryEncodeLeaf(node.ChildLeaf, out _))
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A journalled node requires exactly one bounded child name.");

        }

        return node.Presence switch
        {
            BackupRestoreNodePresence.Present when
                node.NodePhysicalIdentityDigest is not { IsValid: true } =>
                new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    "A present journalled node requires its exact same-handle physical identity."),

            BackupRestoreNodePresence.Absent when node.NodePhysicalIdentityDigest is not null =>
                new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    "An absent journalled node cannot carry a physical identity."),

            BackupRestoreNodePresence.Present or BackupRestoreNodePresence.Absent =>
                node.ContentDigest is null
                || (node.Presence is BackupRestoreNodePresence.Present
                    && node.NodeKind is BackupRestoreNodeKind.RegularFile
                    && node.ContentDigest.Value.IsValid)
                    ? Result.Success()
                    : new Error(
                        ErrorCodes.Covenant.IntegrityFailure,
                        "Only a present regular file may carry a content digest."),

            _ => new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "This journalled node names a presence this build cannot resume."),
        };

    }

    /// <summary>
    /// The present/absent shape each phase freezes for the three topology roots.
    /// </summary>
    /// <remarks>
    /// <see cref="BackupRestorePhase.Commit"/> is the one displacement window, so it constrains
    /// nothing: it is the phase during which the tree is genuinely mid-move, and a journal that could
    /// not describe that state could not drive the recovery it exists for. Every other phase is
    /// determined — before the window nothing is displaced and the staged tree exists, after it the
    /// staged tree is gone and the replaced tree is where the rollback artifact lives.
    /// </remarks>
    private static Result ValidatePhaseShape(BackupRestoreJournalPayloadV2 payload)
    {

        if (payload.Phase is BackupRestorePhase.Commit)
        {

            return Result.Success();

        }

        bool afterSwap = payload.Phase > BackupRestorePhase.Commit;

        bool replacing = payload.ConflictMode is BackupRestoreConflictMode.ReplaceInstallation;

        BackupRestoreNodePresence expectedLive = afterSwap || replacing
            ? BackupRestoreNodePresence.Present
            : BackupRestoreNodePresence.Absent;

        BackupRestoreNodePresence expectedStaged = afterSwap
            ? BackupRestoreNodePresence.Absent
            : BackupRestoreNodePresence.Present;

        BackupRestoreNodePresence expectedDisplaced = afterSwap && replacing
            ? BackupRestoreNodePresence.Present
            : BackupRestoreNodePresence.Absent;

        return payload.LiveRoot.Presence == expectedLive
            && payload.StagedRoot.Presence == expectedStaged
            && payload.DisplacedRoot.Presence == expectedDisplaced
            ? Result.Success()
            : new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "This restore journal claims a topology shape its phase cannot have reached.");

    }

    private static Result ValidateMarkerCleanup(
        BackupRestoreJournalPayloadV2 payload,
        CovenantExclusiveRecoveryOwner owner)
    {

        bool prepared = payload.Phase >= BackupRestorePhase.SafetyPoint;

        if (payload.MarkerCleanup is null)
        {

            return prepared
                ? new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    "A restore may not reach the safety point without a prepared marker checkpoint.")
                : Result.Success();

        }

        if (!prepared)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "A restore journal cannot claim marker children before preparation has committed.");

        }

        BackupRestoreMarkerCleanupCheckpointV1 checkpoint = payload.MarkerCleanup;

        if (checkpoint.Version != MarkerCleanupCheckpointVersion)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "This marker cleanup checkpoint is not a version this build can resume.");

        }

        if (checkpoint.OwnerOperationId != owner.OperationId
            || checkpoint.OwnerOperation != owner.Operation
            || checkpoint.OwnerEffectDigest != owner.EffectDigest)
        {

            return new Error(
                ErrorCodes.Covenant.ForbiddenAuthority,
                "This marker cleanup checkpoint belongs to a different exclusive owner.");

        }

        if (checkpoint.OrderedIntentIds.IsDefault)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "An uninitialized marker-intent vector is not a proven empty inventory.");

        }

        if (checkpoint.OrderedIntentIds.Length > CampaignPathRestoreCleanupIntentVector.MaximumIntents)
        {

            return new Error(
                ErrorCodes.Covenant.CapacityExceeded,
                "This marker cleanup checkpoint carries more children than one journal may hold.");

        }

        if (checkpoint.IntentCount != checked((ulong)checkpoint.OrderedIntentIds.Length))
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "This marker cleanup checkpoint's count disagrees with its vector.");

        }

        Result<CovenantDigest> vector =
            CampaignPathRestoreCleanupIntentVector.Compute(checkpoint.OrderedIntentIds);

        if (vector.IsFailure)
        {

            return vector.Error;

        }

        return vector.Value == checkpoint.IntentVectorDigest
            ? Result.Success()
            : new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "This marker cleanup checkpoint's vector digest does not commit to its children.");

    }

    /// <summary>
    /// The fixed binary order every envelope field is authenticated in.
    /// </summary>
    private static byte[] AssociatedData(
        byte version,
        CovenantDigest profileNamespaceDigest,
        Guid installationId,
        Guid operationId,
        ulong revision,
        CovenantDigest previousEnvelopeDigest)
    {

        byte[] data = new byte[
            EnvelopeAssociatedDataDomain.Length
            + 1
            + CovenantLimits.DigestBytes
            + 16
            + 16
            + sizeof(ulong)
            + CovenantLimits.DigestBytes];

        int written = Encoding.ASCII.GetBytes(EnvelopeAssociatedDataDomain, data);

        data[written++] = version;

        written += Copy(data.AsSpan(written), profileNamespaceDigest);

        _ = installationId.TryWriteBytes(data.AsSpan(written), bigEndian: true, out int installationBytes);

        written += installationBytes;

        _ = operationId.TryWriteBytes(data.AsSpan(written), bigEndian: true, out int operationBytes);

        written += operationBytes;

        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(written), revision);

        written += sizeof(ulong);

        written += Copy(data.AsSpan(written), previousEnvelopeDigest);

        return written == data.Length
            ? data
            : data[..written];

    }

    private static bool TryWriteUuid(Span<byte> destination, Guid value, ref int written)
    {

        if (!value.TryWriteBytes(destination, bigEndian: true, out int bytes) || bytes != 16)
        {

            return false;

        }

        written += bytes;

        return true;

    }

    private static bool TryEncodeLeaf(string? leaf, out byte[] encoded)
    {

        encoded = [];

        if (string.IsNullOrEmpty(leaf)
            || leaf is "." or ".."
            || leaf.Contains(Path.DirectorySeparatorChar)
            || leaf.Contains(Path.AltDirectorySeparatorChar)
            || leaf.Contains('/')
            || leaf.Contains('\\'))
        {

            return false;

        }

        byte[] bytes = Encoding.UTF8.GetBytes(leaf);

        if (bytes.Length is 0 or > MaxChildLeafBytes)
        {

            return false;

        }

        encoded = bytes;

        return true;

    }

    private static bool HasTraversalSegment(string path)
    {

        foreach (string segment in path.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/', '\\'],
                     StringSplitOptions.None))
        {

            if (segment is "." or "..")
            {

                return true;

            }

        }

        return false;

    }

    private static bool TryDecodeExact(
        string? value,
        int expectedCharacters,
        int expectedBytes,
        out byte[] decoded)
    {

        decoded = [];

        if (value is null || value.Length != expectedCharacters || !IsUnpaddedBase64Url(value))
        {

            return false;

        }

        byte[] buffer = new byte[expectedBytes];

        if (!Base64Url.TryDecodeFromChars(value, buffer, out int written) || written != expectedBytes)
        {

            return false;

        }

        // Re-encoding is what rejects a noncanonical final character: the decoder tolerates unused
        // trailing bits, and a token that decodes to the same bytes under two spellings would give one
        // key two names.
        if (!string.Equals(Base64Url.EncodeToString(buffer), value, StringComparison.Ordinal))
        {

            return false;

        }

        decoded = buffer;

        return true;

    }

    private static bool TryDecodeBounded(string? value, int maximumBytes, out byte[] decoded)
    {

        decoded = [];

        if (value is null || value.Length == 0 || !IsUnpaddedBase64Url(value))
        {

            return false;

        }

        // Bound the encoded character count against the largest legal payload before touching a
        // buffer, then size the buffer from the token rather than from the ceiling — a fixed
        // maximum-sized allocation per decode would let a one-byte journal cost half a megabyte.
        if (value.Length > Base64Url.GetEncodedLength(maximumBytes))
        {

            return false;

        }

        byte[] buffer = new byte[Base64Url.GetMaxDecodedLength(value.Length)];

        if (!Base64Url.TryDecodeFromChars(value, buffer, out int written))
        {

            return false;

        }

        decoded = buffer[..written];

        return string.Equals(Base64Url.EncodeToString(decoded), value, StringComparison.Ordinal);

    }

    private static bool IsUnpaddedBase64Url(string token)
    {

        foreach (char value in token)
        {

            bool allowed = value is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '-'
                or '_';

            if (!allowed)
            {

                return false;

            }

        }

        return token.Length % 4 != 1;

    }

    private static int Copy(Span<byte> destination, CovenantDigest digest)
    {

        digest.Bytes.CopyTo(destination);

        return CovenantLimits.DigestBytes;

    }

    private static Result<BackupRestoreJournalPayloadV2> Invalid() =>
        Invalid<BackupRestoreJournalPayloadV2>();

    /// <summary>
    /// The one content-free refusal every parse and cryptographic failure collapses to.
    /// </summary>
    private static Result<T> Invalid<T>() =>
        Result<T>.Failure(new Error(
            ErrorCodes.Covenant.IntegrityFailure,
            "This restore journal evidence did not authenticate."));

}
