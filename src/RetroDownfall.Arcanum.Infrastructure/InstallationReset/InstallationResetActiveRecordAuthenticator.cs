using System.Buffers.Binary;

using System.Buffers.Text;

using System.Security.Cryptography;

using System.Text;

using System.Text.Json;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

/// <summary>The sole codec for profile-bound installation-reset active evidence.</summary>
internal static class InstallationResetActiveRecordAuthenticator
{

    internal const byte EnvelopeVersion = 2;

    internal const byte AnchorVersion = 1;

    internal const int MaxActiveFileBytes = 64 * 1024;

    internal const int NonceBytes = 12;

    internal const int AuthenticationTagBytes = 16;

    internal const int KeyBytes = 32;

    internal const ulong MaxRevision = ulong.MaxValue - 1;

    internal const string ActiveLocationDomain =
        "Arcanum.InstallationReset.ActiveLocation.v1";

    internal const string EnvelopeAssociatedDataDomain =
        "Arcanum.InstallationReset.ActiveEnvelope.v2";

    internal const string EnvelopeDigestDomain =
        "Arcanum.InstallationReset.ActiveEnvelopeDigest.v2";

    private const int MaxActiveLeafBytes = 255;

    private const int MaxPlanIdBytes = 1024;

    private const int EncodedNonceCharacters = 16;

    private const int EncodedAuthenticationTagCharacters = 22;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static CovenantDigest ZeroDigest { get; } =
        new(new byte[CovenantLimits.DigestBytes]);

    internal static Result<InstallationResetActiveLocation> ResolveLocation(
        string guardedRoot,
        BackupRestoreProfileNamespace profileNamespace)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(guardedRoot);

        ArgumentNullException.ThrowIfNull(profileNamespace);

        Result<BackupRestoreProfileNamespace> resolved =
            BackupRestoreJournalAuthenticator.ResolveProfileNamespace(guardedRoot);

        if (resolved.IsFailure
            || resolved.Value.Digest != profileNamespace.Digest
            || resolved.Value.ParentPhysicalIdentityDigest
                != profileNamespace.ParentPhysicalIdentityDigest
            || !string.Equals(
                resolved.Value.ChildLeaf,
                profileNamespace.ChildLeaf,
                StringComparison.Ordinal))
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "The installation-reset active location does not belong to this profile namespace.");

        }

        string lockPath = ArcanumMaintenanceLock.LockPathFor(guardedRoot);

        string parent = Path.GetDirectoryName(lockPath)!;

        string leaf = Path.GetFileNameWithoutExtension(lockPath)
            + ".factory-reset.active.json";

        Result<CovenantDigest> digest = ActiveLocation(
            profileNamespace.Digest,
            profileNamespace.ParentPhysicalIdentityDigest,
            leaf);

        return digest.IsFailure
            ? Result<InstallationResetActiveLocation>.Failure(digest.Error)
            : new InstallationResetActiveLocation(
                Path.Combine(parent, leaf),
                profileNamespace.Digest,
                profileNamespace.ParentPhysicalIdentityDigest,
                leaf,
                digest.Value);

    }

    internal static Result<CovenantDigest> ActiveLocation(
        CovenantDigest profileNamespaceDigest,
        CovenantDigest guardedParentPhysicalIdentityDigest,
        string activeLeaf)
    {

        if (!profileNamespaceDigest.IsValid
            || !guardedParentPhysicalIdentityDigest.IsValid)
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "An installation-reset active location requires complete digest evidence.");

        }

        if (!TryEncodeLeaf(activeLeaf, out byte[] leaf))
        {

            return new Error(
                ErrorCodes.Covenant.IntegrityFailure,
                "An installation-reset active location requires one bounded child name.");

        }

        byte[] preimage = new byte[
            ActiveLocationDomain.Length
            + 1
            + CovenantLimits.DigestBytes
            + CovenantLimits.DigestBytes
            + sizeof(ushort)
            + leaf.Length];

        int written = Encoding.ASCII.GetBytes(ActiveLocationDomain, preimage);

        preimage[written++] = 0x00;

        profileNamespaceDigest.Bytes.CopyTo(preimage.AsSpan(written));

        written += CovenantLimits.DigestBytes;

        guardedParentPhysicalIdentityDigest.Bytes.CopyTo(preimage.AsSpan(written));

        written += CovenantLimits.DigestBytes;

        BinaryPrimitives.WriteUInt16BigEndian(
            preimage.AsSpan(written),
            checked((ushort)leaf.Length));

        written += sizeof(ushort);

        leaf.CopyTo(preimage.AsSpan(written));

        written += leaf.Length;

        return new CovenantDigest(SHA256.HashData(preimage.AsSpan(0, written)));

    }

    internal static Result<InstallationResetActiveEnvelopeV2> Seal(
        InstallationResetActiveRecordKeyLease key,
        InstallationResetActiveLocation location,
        Guid installationId,
        ulong revision,
        CovenantDigest previousEnvelopeDigest,
        InstallationResetActivePayloadV2 payload)
    {

        ArgumentNullException.ThrowIfNull(key);

        if (location is null
            || installationId == Guid.Empty
            || revision is 0 or > MaxRevision
            || !previousEnvelopeDigest.IsValid
            || ValidateLocation(location).IsFailure
            || ValidatePayload(payload).IsFailure
            || !MatchesInstallation(payload, installationId))
        {

            return Invalid<InstallationResetActiveEnvelopeV2>();

        }

        byte[] plaintext;

        try
        {

            plaintext = JsonSerializer.SerializeToUtf8Bytes(
                payload,
                InstallationResetActiveJsonContext.Default.InstallationResetActivePayloadV2);

        }
        catch (Exception exception) when (IsDecodeFailure(exception))
        {

            return Invalid<InstallationResetActiveEnvelopeV2>();

        }

        byte[] nonce = [];

        byte[] ciphertext = [];

        byte[] tag = [];

        byte[] associatedData = [];

        try
        {

            if (plaintext.Length is 0 or > MaxActiveFileBytes)
            {

                return Invalid<InstallationResetActiveEnvelopeV2>();

            }

            nonce = RandomNumberGenerator.GetBytes(NonceBytes);

            ciphertext = new byte[plaintext.Length];

            tag = new byte[AuthenticationTagBytes];

            associatedData = AssociatedData(
                EnvelopeVersion,
                location.ProfileNamespaceDigest,
                installationId,
                payload.OperationId,
                revision,
                previousEnvelopeDigest,
                location.Digest,
                payload.Scope,
                payload.PlanId);

            if (!key.TryTakeKey(out byte[]? material))
            {

                return new Error(
                    ErrorCodes.Covenant.LifecycleConflict,
                    "This installation-reset active-record key lease has already been spent.");

            }

            try
            {

                using AesGcm aes = new(material, AuthenticationTagBytes);

                aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

            }
            catch (CryptographicException)
            {

                return Invalid<InstallationResetActiveEnvelopeV2>();

            }
            finally
            {

                CryptographicOperations.ZeroMemory(material);

            }

            InstallationResetActiveEnvelopeV2 envelope = new(
                EnvelopeVersion,
                location.ProfileNamespaceDigest,
                installationId,
                payload.OperationId,
                revision,
                previousEnvelopeDigest,
                location.Digest,
                payload.Scope,
                payload.PlanId,
                Base64Url.EncodeToString(nonce),
                Base64Url.EncodeToString(ciphertext),
                Base64Url.EncodeToString(tag));

            Result<byte[]> encoded = EncodeEnvelope(envelope);

            return encoded.IsSuccess
                ? envelope
                : Result<InstallationResetActiveEnvelopeV2>.Failure(encoded.Error);

        }
        finally
        {

            CryptographicOperations.ZeroMemory(plaintext);

            CryptographicOperations.ZeroMemory(nonce);

            CryptographicOperations.ZeroMemory(ciphertext);

            CryptographicOperations.ZeroMemory(tag);

            CryptographicOperations.ZeroMemory(associatedData);

        }

    }

    internal static Result PreflightEnvelope(
        InstallationResetActiveLocation location,
        Guid installationId,
        ulong revision,
        CovenantDigest previousEnvelopeDigest,
        InstallationResetActivePayloadV2 payload)
    {

        if (location is null
            || installationId == Guid.Empty
            || revision is 0 or > MaxRevision
            || !previousEnvelopeDigest.IsValid
            || ValidateLocation(location).IsFailure
            || ValidatePayload(payload).IsFailure
            || !MatchesInstallation(payload, installationId))
        {

            return InvalidResult();

        }

        byte[] plaintext = [];

        byte[] nonce = new byte[NonceBytes];

        byte[] tag = new byte[AuthenticationTagBytes];

        byte[] ciphertext = [];

        try
        {

            plaintext = JsonSerializer.SerializeToUtf8Bytes(
                payload,
                InstallationResetActiveJsonContext.Default.InstallationResetActivePayloadV2);

            if (plaintext.Length is 0 or > MaxActiveFileBytes)
            {

                return InvalidResult();

            }

            ciphertext = new byte[plaintext.Length];

            InstallationResetActiveEnvelopeV2 envelope = new(
                EnvelopeVersion,
                location.ProfileNamespaceDigest,
                installationId,
                payload.OperationId,
                revision,
                previousEnvelopeDigest,
                location.Digest,
                payload.Scope,
                payload.PlanId,
                Base64Url.EncodeToString(nonce),
                Base64Url.EncodeToString(ciphertext),
                Base64Url.EncodeToString(tag));

            Result<byte[]> encoded = EncodeEnvelope(envelope);

            return encoded.IsSuccess
                ? Result.Success()
                : Result.Failure(encoded.Error);

        }
        catch (Exception exception) when (IsDecodeFailure(exception))
        {

            return InvalidResult();

        }
        finally
        {

            CryptographicOperations.ZeroMemory(plaintext);

            CryptographicOperations.ZeroMemory(nonce);

            CryptographicOperations.ZeroMemory(tag);

            CryptographicOperations.ZeroMemory(ciphertext);

        }

    }

    internal static Result<InstallationResetActivePayloadV2> Open(
        InstallationResetActiveRecordKeyLease key,
        InstallationResetActiveLocation expectedLocation,
        Guid expectedInstallationId,
        InstallationResetActiveEnvelopeV2 envelope)
    {

        ArgumentNullException.ThrowIfNull(key);

        if (expectedLocation is null
            || expectedInstallationId == Guid.Empty
            || envelope is null
            || ValidateLocation(expectedLocation).IsFailure
            || ValidateEnvelopeFields(envelope).IsFailure
            || envelope.ProfileNamespaceDigest != expectedLocation.ProfileNamespaceDigest
            || envelope.InstallationId != expectedInstallationId
            || envelope.ActiveLocationDigest != expectedLocation.Digest)
        {

            return Invalid<InstallationResetActivePayloadV2>();

        }

        byte[] nonce = [];

        byte[] tag = [];

        byte[] ciphertext = [];

        if (!TryDecodeExact(
                envelope.NonceBase64Url,
                EncodedNonceCharacters,
                NonceBytes,
                out nonce)
            || !TryDecodeExact(
                envelope.AuthenticationTagBase64Url,
                EncodedAuthenticationTagCharacters,
                AuthenticationTagBytes,
                out tag)
            || !TryDecodeBounded(
                envelope.CiphertextBase64Url,
                MaxActiveFileBytes,
                out ciphertext))
        {

            CryptographicOperations.ZeroMemory(nonce);

            CryptographicOperations.ZeroMemory(tag);

            CryptographicOperations.ZeroMemory(ciphertext);

            return Invalid<InstallationResetActivePayloadV2>();

        }

        byte[] plaintext = new byte[ciphertext.Length];

        byte[] associatedData = AssociatedData(
            envelope.Version,
            envelope.ProfileNamespaceDigest,
            envelope.InstallationId,
            envelope.OperationId,
            envelope.Revision,
            envelope.PreviousEnvelopeDigest,
            envelope.ActiveLocationDigest,
            envelope.Scope,
            envelope.PlanId);

        try
        {

            if (!key.TryTakeKey(out byte[]? material))
            {

                return new Error(
                    ErrorCodes.Covenant.LifecycleConflict,
                    "This installation-reset active-record key lease has already been spent.");

            }

            try
            {

                using AesGcm aes = new(material, AuthenticationTagBytes);

                aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);

            }
            catch (CryptographicException)
            {

                return Invalid<InstallationResetActivePayloadV2>();

            }
            finally
            {

                CryptographicOperations.ZeroMemory(material);

            }

            InstallationResetActivePayloadV2? payload;

            try
            {

                payload = JsonSerializer.Deserialize(
                    plaintext,
                    InstallationResetActiveJsonContext.Default.InstallationResetActivePayloadV2);

            }
            catch (Exception exception) when (IsDecodeFailure(exception))
            {

                return Invalid<InstallationResetActivePayloadV2>();

            }

            if (payload is null
                || payload.Version != EnvelopeVersion
                || payload.OperationId != envelope.OperationId
                || payload.Scope != envelope.Scope
                || !string.Equals(payload.PlanId, envelope.PlanId, StringComparison.Ordinal)
                || payload.HostToolsMarkerPairReset is not null
                || ValidatePayload(payload).IsFailure
                || !MatchesInstallation(payload, envelope.InstallationId))
            {

                return Invalid<InstallationResetActivePayloadV2>();

            }

            return payload;

        }
        finally
        {

            CryptographicOperations.ZeroMemory(nonce);

            CryptographicOperations.ZeroMemory(ciphertext);

            CryptographicOperations.ZeroMemory(tag);

            CryptographicOperations.ZeroMemory(plaintext);

            CryptographicOperations.ZeroMemory(associatedData);

        }

    }

    internal static Result<CovenantDigest> EnvelopeDigest(
        InstallationResetActiveEnvelopeV2 envelope)
    {

        if (envelope is null || ValidateEnvelopeFields(envelope).IsFailure)
        {

            return Invalid<CovenantDigest>();

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

        hash.AppendData(envelope.ActiveLocationDigest.Bytes);

        _ = TryScopeCode(envelope.Scope, out byte scopeCode);

        hash.AppendData([scopeCode]);

        _ = TryEncodePlanId(envelope.PlanId, out byte[] plan);

        AppendLengthPrefixed(hash, plan);

        foreach (string value in (string[])
                 [
                     envelope.NonceBase64Url,
                     envelope.CiphertextBase64Url,
                     envelope.AuthenticationTagBase64Url,
                 ])
        {

            AppendLengthPrefixed(hash, Encoding.ASCII.GetBytes(value));

        }

        CryptographicOperations.ZeroMemory(plan);

        return new CovenantDigest(hash.GetHashAndReset());

    }

    internal static Result<byte[]> EncodeEnvelope(
        InstallationResetActiveEnvelopeV2 envelope)
    {

        if (envelope is null || ValidateEnvelopeFields(envelope).IsFailure)
        {

            return Invalid<byte[]>();

        }

        try
        {

            byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(
                envelope,
                InstallationResetActiveJsonContext.Default.InstallationResetActiveEnvelopeV2);

            return encoded.Length <= MaxActiveFileBytes
                ? encoded
                : Invalid<byte[]>();

        }
        catch (Exception exception) when (IsDecodeFailure(exception))
        {

            return Invalid<byte[]>();

        }

    }

    internal static Result<InstallationResetActiveEnvelopeV2> DecodeEnvelope(
        ReadOnlySpan<byte> utf8)
    {

        if (utf8.Length is 0 or > MaxActiveFileBytes)
        {

            return Invalid<InstallationResetActiveEnvelopeV2>();

        }

        InstallationResetActiveEnvelopeV2? envelope;

        try
        {

            envelope = JsonSerializer.Deserialize(
                utf8,
                InstallationResetActiveJsonContext.Default.InstallationResetActiveEnvelopeV2);

        }
        catch (Exception exception) when (IsDecodeFailure(exception))
        {

            return Invalid<InstallationResetActiveEnvelopeV2>();

        }

        if (envelope is null)
        {

            return Invalid<InstallationResetActiveEnvelopeV2>();

        }

        Result<byte[]> canonical = EncodeEnvelope(envelope);

        return canonical.IsSuccess && utf8.SequenceEqual(canonical.Value)
            ? envelope
            : Invalid<InstallationResetActiveEnvelopeV2>();

    }

    internal static Result<string> EncodeAnchor(InstallationResetActiveAnchorV1 anchor)
    {

        if (anchor is null || ValidateAnchor(anchor).IsFailure)
        {

            return Invalid<string>();

        }

        try
        {

            string encoded = JsonSerializer.Serialize(
                anchor,
                InstallationResetActiveJsonContext.Default.InstallationResetActiveAnchorV1);

            return StrictUtf8.GetByteCount(encoded) <= MaxActiveFileBytes
                ? encoded
                : Invalid<string>();

        }
        catch (Exception exception) when (IsDecodeFailure(exception))
        {

            return Invalid<string>();

        }

    }

    internal static Result<InstallationResetActiveAnchorV1> DecodeAnchor(string? value)
    {

        if (string.IsNullOrEmpty(value))
        {

            return Invalid<InstallationResetActiveAnchorV1>();

        }

        try
        {

            if (StrictUtf8.GetByteCount(value) > MaxActiveFileBytes)
            {

                return Invalid<InstallationResetActiveAnchorV1>();

            }

        }
        catch (EncoderFallbackException)
        {

            return Invalid<InstallationResetActiveAnchorV1>();

        }

        InstallationResetActiveAnchorV1? anchor;

        try
        {

            anchor = JsonSerializer.Deserialize(
                value,
                InstallationResetActiveJsonContext.Default.InstallationResetActiveAnchorV1);

        }
        catch (Exception exception) when (IsDecodeFailure(exception))
        {

            return Invalid<InstallationResetActiveAnchorV1>();

        }

        if (anchor is null || ValidateAnchor(anchor).IsFailure)
        {

            return Invalid<InstallationResetActiveAnchorV1>();

        }

        Result<string> canonical = EncodeAnchor(anchor);

        return canonical.IsSuccess
            && string.Equals(canonical.Value, value, StringComparison.Ordinal)
            ? anchor
            : Invalid<InstallationResetActiveAnchorV1>();

    }

    internal static Result ValidatePayload(InstallationResetActivePayloadV2 payload)
    {

        if (payload is null
            || payload.Version != EnvelopeVersion
            || payload.OperationId == Guid.Empty)
        {

            return InvalidResult();

        }

        if (!TryEncodePlanId(payload.PlanId, out byte[] plan))
        {

            return InvalidResult();

        }

        CryptographicOperations.ZeroMemory(plan);

        if (!TryScopeCode(payload.Scope, out _)
            || !Enum.IsDefined(payload.Phase)
            || payload.AcceptedBinding is null
            || string.IsNullOrWhiteSpace(payload.AcceptedBinding.BindingId)
            || payload.AcceptedBinding.SelectedRoots.IsDefault
            || payload.AcceptedBinding.ExcludedRoots.IsDefault
            || payload.AcceptedBinding.PreservedBackups.IsDefault
            || payload.AcceptedBinding.CredentialAccounts.IsDefault
            || payload.AcceptedBinding.DataPlanIds.IsDefault
            || payload.CredentialResults.IsDefault
            || payload.RowsDeleted < 0
            || payload.FilesDeleted < 0
            || payload.EstimatedBytesDeleted < 0
            || payload.HostToolsMarkerPairReset is not null)
        {

            return InvalidResult();

        }

        if (payload.FullInstallationResetRemediationClaim is { } claim
            && (claim.Version != 1
                || claim.OperationId == Guid.Empty
                || claim.OperationId != payload.OperationId
                || claim.InstallationId == Guid.Empty
                || !claim.AttestationDigest.IsValid
                || !claim.NonceDigest.IsValid
                || !claim.IssuerDigest.IsValid
                || claim.AcceptedAtUtc == default
                || claim.AcceptedAtUtc.Offset != TimeSpan.Zero
                || claim.AcceptedAtUtc.Ticks % TimeSpan.TicksPerSecond != 0
                || payload.Scope is not InstallationResetScope.All
                || payload.Phase is not InstallationResetPhase.Prepared
                || payload.PointOfNoReturn
                || payload.RowsDeleted != 0
                || payload.FilesDeleted != 0
                || payload.EstimatedBytesDeleted != 0
                || !payload.CredentialResults.IsEmpty
                || payload.DataHandoff is not null
                || payload.OnlineDataCompletion is not null
                || !string.Equals(
                    payload.LastErrorCode,
                    ErrorCodes.Data.RecoveryRequired,
                    StringComparison.Ordinal)))
        {

            return InvalidResult();

        }

        if (payload.Workspace is { } workspace
            && (workspace.CampaignId == Guid.Empty
                || string.IsNullOrWhiteSpace(workspace.WorkspaceRoot)))
        {

            return InvalidResult();

        }

        foreach (InstallationResetActivePreservedBackupV2 backup in
                 payload.AcceptedBinding.PreservedBackups)
        {

            if (backup is null
                || string.IsNullOrWhiteSpace(backup.CanonicalPath)
                || backup.Identity is null
                || string.IsNullOrWhiteSpace(backup.Identity.Value)
                || backup.Identity.Length < 0
                || backup.Identity.HardLinkCount == 0)
            {

                return InvalidResult();

            }

        }

        foreach (InstallationResetActiveCredentialResultV2 result in payload.CredentialResults)
        {

            if (result is null
                || string.IsNullOrWhiteSpace(result.Account)
                || !Enum.IsDefined(result.Status))
            {

                return InvalidResult();

            }

        }

        if (payload.DataHandoff is { } handoff && !Enum.IsDefined(handoff))
        {

            return InvalidResult();

        }

        if (payload.OnlineDataCompletion is { } completion
            && (completion.ServerOperationId == Guid.Empty
                || completion.RequestedOperationId != payload.OperationId
                || completion.ServerOperationId == completion.RequestedOperationId
                || string.IsNullOrWhiteSpace(completion.DataPlanId)
                || completion.RowsDeleted < 0
                || completion.FilesDeleted < 0
                || completion.EstimatedBytesDeleted < 0
                || completion.DerivedRecordsDeleted < 0))
        {

            return InvalidResult();

        }

        return Result.Success();

    }

    private static bool MatchesInstallation(
        InstallationResetActivePayloadV2 payload,
        Guid installationId) =>
        payload.FullInstallationResetRemediationClaim is not { } claim
        || claim.InstallationId == installationId;

    private static Result ValidateEnvelopeFields(InstallationResetActiveEnvelopeV2 envelope)
    {

        if (envelope.Version != EnvelopeVersion
            || !envelope.ProfileNamespaceDigest.IsValid
            || envelope.InstallationId == Guid.Empty
            || envelope.OperationId == Guid.Empty
            || envelope.Revision is 0 or > MaxRevision
            || !envelope.PreviousEnvelopeDigest.IsValid
            || !envelope.ActiveLocationDigest.IsValid
            || !TryScopeCode(envelope.Scope, out _)
            || !TryEncodePlanId(envelope.PlanId, out byte[] plan))
        {

            return InvalidResult();

        }

        CryptographicOperations.ZeroMemory(plan);

        byte[] nonce = [];

        byte[] tag = [];

        byte[] ciphertext = [];

        if (!TryDecodeExact(
                envelope.NonceBase64Url,
                EncodedNonceCharacters,
                NonceBytes,
                out nonce)
            || !TryDecodeExact(
                envelope.AuthenticationTagBase64Url,
                EncodedAuthenticationTagCharacters,
                AuthenticationTagBytes,
                out tag)
            || !TryDecodeBounded(
                envelope.CiphertextBase64Url,
                MaxActiveFileBytes,
                out ciphertext))
        {

            CryptographicOperations.ZeroMemory(nonce);

            CryptographicOperations.ZeroMemory(tag);

            CryptographicOperations.ZeroMemory(ciphertext);

            return InvalidResult();

        }

        CryptographicOperations.ZeroMemory(nonce);

        CryptographicOperations.ZeroMemory(tag);

        CryptographicOperations.ZeroMemory(ciphertext);

        return Result.Success();

    }

    private static Result ValidateAnchor(InstallationResetActiveAnchorV1 anchor)
    {

        if (anchor.Version != AnchorVersion
            || anchor.State is not InstallationResetActiveAnchorState.Active
                and not InstallationResetActiveAnchorState.Closed
            || !anchor.ProfileNamespaceDigest.IsValid
            || anchor.InstallationId == Guid.Empty
            || anchor.OperationId == Guid.Empty
            || anchor.Revision > MaxRevision
            || !anchor.EnvelopeDigest.IsValid
            || !anchor.ActiveLocationDigest.IsValid)
        {

            return InvalidResult();

        }

        if (anchor.Revision == 0)
        {

            return anchor.State is InstallationResetActiveAnchorState.Active
                && anchor.EnvelopeDigest == ZeroDigest
                ? Result.Success()
                : InvalidResult();

        }

        return anchor.EnvelopeDigest != ZeroDigest
            ? Result.Success()
            : InvalidResult();

    }

    private static Result ValidateLocation(InstallationResetActiveLocation location)
    {

        if (string.IsNullOrWhiteSpace(location.ActivePath)
            || !location.ProfileNamespaceDigest.IsValid
            || !location.GuardedParentPhysicalIdentityDigest.IsValid
            || !location.Digest.IsValid
            || !string.Equals(
                Path.GetFileName(location.ActivePath),
                location.ActiveLeaf,
                StringComparison.Ordinal))
        {

            return InvalidResult();

        }

        Result<CovenantDigest> recomputed = ActiveLocation(
            location.ProfileNamespaceDigest,
            location.GuardedParentPhysicalIdentityDigest,
            location.ActiveLeaf);

        return recomputed.IsSuccess && recomputed.Value == location.Digest
            ? Result.Success()
            : InvalidResult();

    }

    private static byte[] AssociatedData(
        byte version,
        CovenantDigest profileNamespaceDigest,
        Guid installationId,
        Guid operationId,
        ulong revision,
        CovenantDigest previousEnvelopeDigest,
        CovenantDigest activeLocationDigest,
        InstallationResetScope scope,
        string planId)
    {

        _ = TryEncodePlanId(planId, out byte[] plan);

        _ = TryScopeCode(scope, out byte scopeCode);

        byte[] data = new byte[
            EnvelopeAssociatedDataDomain.Length
            + 1
            + 1
            + CovenantLimits.DigestBytes
            + 16
            + 16
            + sizeof(ulong)
            + CovenantLimits.DigestBytes
            + CovenantLimits.DigestBytes
            + 1
            + sizeof(uint)
            + plan.Length];

        int written = Encoding.ASCII.GetBytes(EnvelopeAssociatedDataDomain, data);

        data[written++] = 0x00;

        data[written++] = version;

        profileNamespaceDigest.Bytes.CopyTo(data.AsSpan(written));

        written += CovenantLimits.DigestBytes;

        _ = installationId.TryWriteBytes(
            data.AsSpan(written),
            bigEndian: true,
            out int installationBytes);

        written += installationBytes;

        _ = operationId.TryWriteBytes(
            data.AsSpan(written),
            bigEndian: true,
            out int operationBytes);

        written += operationBytes;

        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(written), revision);

        written += sizeof(ulong);

        previousEnvelopeDigest.Bytes.CopyTo(data.AsSpan(written));

        written += CovenantLimits.DigestBytes;

        activeLocationDigest.Bytes.CopyTo(data.AsSpan(written));

        written += CovenantLimits.DigestBytes;

        data[written++] = scopeCode;

        BinaryPrimitives.WriteUInt32BigEndian(
            data.AsSpan(written),
            checked((uint)plan.Length));

        written += sizeof(uint);

        plan.CopyTo(data.AsSpan(written));

        CryptographicOperations.ZeroMemory(plan);

        return data;

    }

    private static bool TryScopeCode(InstallationResetScope scope, out byte code)
    {

        code = scope switch
        {

            InstallationResetScope.Workspace => 1,

            InstallationResetScope.Global => 2,

            InstallationResetScope.All => 3,

            _ => 0,

        };

        return code != 0;

    }

    private static bool TryEncodePlanId(string? value, out byte[] encoded)
    {

        encoded = [];

        if (string.IsNullOrWhiteSpace(value))
        {

            return false;

        }

        try
        {

            encoded = StrictUtf8.GetBytes(value);

        }
        catch (EncoderFallbackException)
        {

            return false;

        }

        if (encoded.Length is > 0 and <= MaxPlanIdBytes)
        {

            return true;

        }

        CryptographicOperations.ZeroMemory(encoded);

        encoded = [];

        return false;

    }

    private static bool TryDecodeExact(
        string? value,
        int expectedCharacters,
        int expectedBytes,
        out byte[] decoded)
    {

        decoded = [];

        if (value is null
            || value.Length != expectedCharacters
            || !IsUnpaddedBase64Url(value))
        {

            return false;

        }

        byte[] buffer = new byte[expectedBytes];

        if (!TryDecodeBase64Url(value, buffer, out int written)
            || written != expectedBytes
            || !string.Equals(
                Base64Url.EncodeToString(buffer),
                value,
                StringComparison.Ordinal))
        {

            CryptographicOperations.ZeroMemory(buffer);

            return false;

        }

        decoded = buffer;

        return true;

    }

    private static bool TryDecodeBounded(
        string? value,
        int maximumBytes,
        out byte[] decoded)
    {

        decoded = [];

        if (string.IsNullOrEmpty(value)
            || !IsUnpaddedBase64Url(value)
            || value.Length > Base64Url.GetEncodedLength(maximumBytes))
        {

            return false;

        }

        byte[] buffer = new byte[Base64Url.GetMaxDecodedLength(value.Length)];

        if (!TryDecodeBase64Url(value, buffer, out int written))
        {

            CryptographicOperations.ZeroMemory(buffer);

            return false;

        }

        byte[] exact = buffer[..written];

        CryptographicOperations.ZeroMemory(buffer);

        if (!string.Equals(Base64Url.EncodeToString(exact), value, StringComparison.Ordinal))
        {

            CryptographicOperations.ZeroMemory(exact);

            return false;

        }

        decoded = exact;

        return true;

    }

    private static bool TryDecodeBase64Url(
        string value,
        Span<byte> destination,
        out int written)
    {

        try
        {

            return Base64Url.TryDecodeFromChars(value, destination, out written);

        }
        catch (FormatException)
        {

            CryptographicOperations.ZeroMemory(destination);

            written = 0;

            return false;

        }

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

    private static void AppendLengthPrefixed(IncrementalHash hash, ReadOnlySpan<byte> value)
    {

        Span<byte> length = stackalloc byte[sizeof(uint)];

        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)value.Length));

        hash.AppendData(length);

        hash.AppendData(value);

    }

    private static bool IsDecodeFailure(Exception exception) =>
        exception is JsonException or NotSupportedException or ArgumentException;

    private static Result InvalidResult() =>
        Result.Failure(new Error(
            ErrorCodes.Covenant.IntegrityFailure,
            "This installation-reset active evidence did not authenticate."));

    private static Result<T> Invalid<T>() =>
        Result<T>.Failure(new Error(
            ErrorCodes.Covenant.IntegrityFailure,
            "This installation-reset active evidence did not authenticate."));

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

        try
        {

            encoded = StrictUtf8.GetBytes(leaf);

        }
        catch (EncoderFallbackException)
        {

            return false;

        }

        return encoded.Length is > 0 and <= MaxActiveLeafBytes;

    }

}
