using System.Collections.Immutable;

using System.Buffers.Binary;

using System.Security.Cryptography;

using System.Text;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

internal static class FullInstallationResetMarkerPairResetDigests
{

    private static readonly CovenantDigest ExpectedRemediationActionDigest = new(
        Convert.FromHexString(
            "761e8536128080d5936070524da90a6558b8901ea46d93194646b413bb27a1d9"));

    internal static Result<byte[]> PairEvidencePreimage(
        HostProcessToolsMatchedPair pair)
    {

        if (pair is null
            || pair.Database is null
            || pair.OsMarker is null)
        {

            return Invalid<byte[]>();

        }

        HostProcessToolsDatabaseMarkerEvidence database = pair.Database;

        HostProcessToolsOsMarkerEvidence marker = pair.OsMarker;

        if (database.State is not CovenantHostToolsState.HostToolsTainted
            || database.TransitionId is not { } databaseTransition
            || database.TaintMasterKeyVersion is not { } databaseVersion
            || database.TaintFingerprint is not { IsValid: true } databaseFingerprint
            || database.TaintIdentityDigest is not { IsValid: true } databaseIdentityDigest
            || databaseVersion == 0
            || marker.TransitionId == Guid.Empty
            || marker.TaintMasterKeyVersion == 0
            || !marker.TaintFingerprint.IsValid
            || !marker.MarkerBytesDigest.IsValid
            || !marker.DurableIdentityDigest.IsValid
            || !marker.TaintIdentityDigest.IsValid
            || !database.DatabaseMarkerDigest.IsValid
            || !string.Equals(
                database.InstallationIdentity,
                marker.InstallationIdentity,
                StringComparison.Ordinal)
            || databaseTransition != marker.TransitionId
            || databaseVersion != marker.TaintMasterKeyVersion
            || databaseFingerprint != marker.TaintFingerprint
            || databaseIdentityDigest != marker.TaintIdentityDigest)
        {

            return Invalid<byte[]>();

        }

        using MemoryStream preimage = FullInstallationResetCanonicalEvidenceV1.Start(
            "Arcanum.FullInstallationReset.MarkerPairEvidence.v1");

        if (!FullInstallationResetCanonicalEvidenceV1.TryWriteText(
                preimage,
                database.InstallationIdentity))
        {

            return Invalid<byte[]>();

        }

        preimage.WriteByte((byte)database.State);

        FullInstallationResetCanonicalEvidenceV1.WriteGuid(preimage, databaseTransition);

        FullInstallationResetCanonicalEvidenceV1.WriteUInt64(preimage, databaseVersion);

        FullInstallationResetCanonicalEvidenceV1.WriteDigest(preimage, databaseFingerprint);

        FullInstallationResetCanonicalEvidenceV1.WriteDigest(preimage, databaseIdentityDigest);

        FullInstallationResetCanonicalEvidenceV1.WriteDigest(
            preimage,
            database.DatabaseMarkerDigest);

        if (!FullInstallationResetCanonicalEvidenceV1.TryWriteText(
                preimage,
                marker.InstallationIdentity))
        {

            return Invalid<byte[]>();

        }

        FullInstallationResetCanonicalEvidenceV1.WriteGuid(preimage, marker.TransitionId);

        FullInstallationResetCanonicalEvidenceV1.WriteUInt64(
            preimage,
            marker.TaintMasterKeyVersion);

        FullInstallationResetCanonicalEvidenceV1.WriteDigest(preimage, marker.TaintFingerprint);

        FullInstallationResetCanonicalEvidenceV1.WriteDigest(preimage, marker.MarkerBytesDigest);

        FullInstallationResetCanonicalEvidenceV1.WriteDigest(
            preimage,
            marker.DurableIdentityDigest);

        FullInstallationResetCanonicalEvidenceV1.WriteDigest(
            preimage,
            marker.TaintIdentityDigest);

        return preimage.ToArray();

    }

    internal static Result<CovenantDigest> PairEvidence(
        HostProcessToolsMatchedPair pair) =>
        Hash(PairEvidencePreimage(pair));

    internal static Result<byte[]> CampaignDisplayPathPreimage(string canonicalDisplayPath)
    {

        if (string.IsNullOrEmpty(canonicalDisplayPath))
        {

            return Invalid<byte[]>();

        }

        using MemoryStream preimage = FullInstallationResetCanonicalEvidenceV1.Start(
            "Arcanum.FullInstallationReset.CampaignDisplayPath.v1");

        return FullInstallationResetCanonicalEvidenceV1.TryWriteText(
            preimage,
            canonicalDisplayPath)
            ? preimage.ToArray()
            : Invalid<byte[]>();

    }

    internal static Result<CovenantDigest> CampaignDisplayPath(string canonicalDisplayPath) =>
        Hash(CampaignDisplayPathPreimage(canonicalDisplayPath));

    internal static Result<byte[]> SameHandleOwnershipPreimage(
        Guid campaignId,
        long priorPathRevision,
        CovenantDigest markerDigest,
        CovenantDigest indexedPhysicalIdentityDigest,
        CovenantDigest observedPhysicalIdentityDigest,
        ulong rootVolumeId,
        ulong rootFileId)
    {

        if (campaignId == Guid.Empty
            || priorPathRevision <= 0
            || !markerDigest.IsValid
            || !indexedPhysicalIdentityDigest.IsValid
            || !observedPhysicalIdentityDigest.IsValid)
        {

            return Invalid<byte[]>();

        }

        using MemoryStream preimage = FullInstallationResetCanonicalEvidenceV1.Start(
            "Arcanum.FullInstallationReset.CampaignMarkerOwnership.v1");

        FullInstallationResetCanonicalEvidenceV1.WriteGuid(preimage, campaignId);

        FullInstallationResetCanonicalEvidenceV1.WriteUInt64(
            preimage,
            checked((ulong)priorPathRevision));

        FullInstallationResetCanonicalEvidenceV1.WriteDigest(preimage, markerDigest);

        FullInstallationResetCanonicalEvidenceV1.WriteDigest(
            preimage,
            indexedPhysicalIdentityDigest);

        FullInstallationResetCanonicalEvidenceV1.WriteDigest(
            preimage,
            observedPhysicalIdentityDigest);

        FullInstallationResetCanonicalEvidenceV1.WriteUInt64(preimage, rootVolumeId);

        FullInstallationResetCanonicalEvidenceV1.WriteUInt64(preimage, rootFileId);

        return preimage.ToArray();

    }

    internal static Result<CovenantDigest> SameHandleOwnership(
        Guid campaignId,
        long priorPathRevision,
        CovenantDigest markerDigest,
        CovenantDigest indexedPhysicalIdentityDigest,
        CovenantDigest observedPhysicalIdentityDigest,
        ulong rootVolumeId,
        ulong rootFileId) =>
        Hash(SameHandleOwnershipPreimage(
            campaignId,
            priorPathRevision,
            markerDigest,
            indexedPhysicalIdentityDigest,
            observedPhysicalIdentityDigest,
            rootVolumeId,
            rootFileId));

    internal static Result<byte[]> CampaignInventoryEntryPreimage(
        CampaignMarkerInventoryEntryV1 entry)
    {

        using MemoryStream preimage = FullInstallationResetCanonicalEvidenceV1.Start(
            "Arcanum.FullInstallationReset.CampaignMarkerInventoryEntry.v1");

        return TryWriteInventoryEntryFields(preimage, entry)
            ? preimage.ToArray()
            : Invalid<byte[]>();

    }

    internal static Result<CovenantDigest> CampaignInventoryEntry(
        CampaignMarkerInventoryEntryV1 entry) =>
        Hash(CampaignInventoryEntryPreimage(entry));

    internal static Result<byte[]> CampaignInventoryPreimage(
        ImmutableArray<CampaignMarkerInventoryEntryV1> entries)
    {

        if (entries.IsDefault || entries.Length > 4096)
        {

            return Invalid<byte[]>();

        }

        CampaignMarkerInventoryEntryV1[] copied = entries.ToArray();

        for (int index = 0; index < copied.Length; index++)
        {

            if (!IsValidInventoryEntry(copied[index])
                || index > 0
                && FullInstallationResetCanonicalEvidenceV1.CompareGuid(
                    copied[index - 1].CampaignId,
                    copied[index].CampaignId) >= 0)
            {

                return Invalid<byte[]>();

            }

        }

        using MemoryStream preimage = FullInstallationResetCanonicalEvidenceV1.Start(
            "Arcanum.FullInstallationReset.CampaignMarkerInventory.v1");

        FullInstallationResetCanonicalEvidenceV1.WriteUInt64(
            preimage,
            checked((ulong)copied.LongLength));

        foreach (CampaignMarkerInventoryEntryV1 entry in copied)
        {

            _ = TryWriteInventoryEntryFields(preimage, entry);

        }

        return preimage.ToArray();

    }

    internal static Result<CovenantDigest> CampaignInventory(
        ImmutableArray<CampaignMarkerInventoryEntryV1> entries) =>
        Hash(CampaignInventoryPreimage(entries));

    internal static Result<byte[]> FullResetEffectPreimage(
        Guid operationId,
        Guid installationId,
        Guid hostToolsTransitionId,
        ulong taintMasterKeyVersion,
        CovenantDigest authorityFingerprint,
        CovenantDigest databaseMarkerDigest,
        CovenantDigest osMarkerDigest,
        CovenantDigest remediationActionDigest,
        CovenantDigest campaignInventoryDigest)
    {

        if (operationId == Guid.Empty
            || installationId == Guid.Empty
            || hostToolsTransitionId == Guid.Empty
            || taintMasterKeyVersion == 0
            || !authorityFingerprint.IsValid
            || !databaseMarkerDigest.IsValid
            || !osMarkerDigest.IsValid
            || !remediationActionDigest.IsValid
            || remediationActionDigest != ExpectedRemediationActionDigest
            || !campaignInventoryDigest.IsValid)
        {

            return Invalid<byte[]>();

        }

        using MemoryStream preimage = FullInstallationResetCanonicalEvidenceV1.Start(
            "Arcanum.FullInstallationReset.Effect.v1");

        FullInstallationResetCanonicalEvidenceV1.WriteGuid(preimage, operationId);

        FullInstallationResetCanonicalEvidenceV1.WriteGuid(preimage, installationId);

        FullInstallationResetCanonicalEvidenceV1.WriteGuid(
            preimage,
            hostToolsTransitionId);

        FullInstallationResetCanonicalEvidenceV1.WriteUInt64(
            preimage,
            taintMasterKeyVersion);

        FullInstallationResetCanonicalEvidenceV1.WriteDigest(
            preimage,
            authorityFingerprint);

        FullInstallationResetCanonicalEvidenceV1.WriteDigest(
            preimage,
            databaseMarkerDigest);

        FullInstallationResetCanonicalEvidenceV1.WriteDigest(preimage, osMarkerDigest);

        FullInstallationResetCanonicalEvidenceV1.WriteDigest(
            preimage,
            remediationActionDigest);

        FullInstallationResetCanonicalEvidenceV1.WriteDigest(
            preimage,
            campaignInventoryDigest);

        preimage.WriteByte(0x01);

        return preimage.ToArray();

    }

    internal static Result<CovenantDigest> FullResetEffect(
        Guid operationId,
        Guid installationId,
        Guid hostToolsTransitionId,
        ulong taintMasterKeyVersion,
        CovenantDigest authorityFingerprint,
        CovenantDigest databaseMarkerDigest,
        CovenantDigest osMarkerDigest,
        CovenantDigest remediationActionDigest,
        CovenantDigest campaignInventoryDigest) =>
        Hash(FullResetEffectPreimage(
            operationId,
            installationId,
            hostToolsTransitionId,
            taintMasterKeyVersion,
            authorityFingerprint,
            databaseMarkerDigest,
            osMarkerDigest,
            remediationActionDigest,
            campaignInventoryDigest));

    internal static Result<byte[]> FullResetIntentVectorPreimage(
        ImmutableArray<Guid> intentIds)
    {

        if (intentIds.IsDefault || intentIds.Length > 4096)
        {

            return Invalid<byte[]>();

        }

        Guid[] copied = intentIds.ToArray();

        HashSet<Guid> distinct = [];

        foreach (Guid intentId in copied)
        {

            if (intentId == Guid.Empty || !distinct.Add(intentId))
            {

                return Invalid<byte[]>();

            }

        }

        using MemoryStream preimage = FullInstallationResetCanonicalEvidenceV1.Start(
            "Arcanum.FullInstallationReset.CampaignMarkerIntentVector.v1");

        FullInstallationResetCanonicalEvidenceV1.WriteUInt64(
            preimage,
            checked((ulong)copied.LongLength));

        foreach (Guid intentId in copied)
        {

            FullInstallationResetCanonicalEvidenceV1.WriteGuid(preimage, intentId);

        }

        return preimage.ToArray();

    }

    internal static Result<CovenantDigest> FullResetIntentVector(
        ImmutableArray<Guid> intentIds) =>
        Hash(FullResetIntentVectorPreimage(intentIds));

    internal static Result<byte[]> CampaignObservationPreimage(
        CampaignPathFullResetCleanupObservationCode code,
        CovenantDigest inventoryEntryDigest,
        CovenantDigest? openedSameHandleOwnershipEvidenceDigest)
    {

        bool opened = code is CampaignPathFullResetCleanupObservationCode.Opened;

        bool blocked = code is CampaignPathFullResetCleanupObservationCode.Unavailable
            or CampaignPathFullResetCleanupObservationCode.Mismatch;

        if (!inventoryEntryDigest.IsValid
            || !opened && !blocked
            || opened
                && openedSameHandleOwnershipEvidenceDigest is not { IsValid: true }
            || blocked && openedSameHandleOwnershipEvidenceDigest is not null)
        {

            return Invalid<byte[]>();

        }

        using MemoryStream preimage = FullInstallationResetCanonicalEvidenceV1.Start(
            "Arcanum.FullInstallationReset.CampaignMarkerObservation.v1");

        preimage.WriteByte((byte)code);

        FullInstallationResetCanonicalEvidenceV1.WriteDigest(
            preimage,
            inventoryEntryDigest);

        if (openedSameHandleOwnershipEvidenceDigest is { } openedDigest)
        {

            FullInstallationResetCanonicalEvidenceV1.WriteDigest(preimage, openedDigest);

        }

        return preimage.ToArray();

    }

    internal static Result<CovenantDigest> CampaignObservation(
        CampaignPathFullResetCleanupObservationCode code,
        CovenantDigest inventoryEntryDigest,
        CovenantDigest? openedSameHandleOwnershipEvidenceDigest) =>
        Hash(CampaignObservationPreimage(
            code,
            inventoryEntryDigest,
            openedSameHandleOwnershipEvidenceDigest));

    private static Result<T> Invalid<T>() =>
        Result<T>.Failure(new Error(
            ErrorCodes.Data.InvalidRequest,
            "The full-installation reset evidence is invalid."));

    private static Result<CovenantDigest> Hash(Result<byte[]> preimage)
    {

        if (preimage.IsFailure)
        {

            return Result<CovenantDigest>.Failure(preimage.Error);

        }

        return new CovenantDigest(SHA256.HashData(preimage.Value));

    }

    private static bool TryWriteInventoryEntryFields(
        MemoryStream target,
        CampaignMarkerInventoryEntryV1 entry)
    {

        if (!IsValidInventoryEntry(entry))
        {

            return false;

        }

        FullInstallationResetCanonicalEvidenceV1.WriteGuid(target, entry.CampaignId);

        FullInstallationResetCanonicalEvidenceV1.WriteUInt64(
            target,
            checked((ulong)entry.PriorPathRevision));

        FullInstallationResetCanonicalEvidenceV1.WriteDigest(target, entry.MarkerDigest);

        FullInstallationResetCanonicalEvidenceV1.WriteDigest(
            target,
            entry.IndexedPhysicalIdentityDigest);

        FullInstallationResetCanonicalEvidenceV1.WriteDigest(
            target,
            entry.CanonicalDisplayPathDigest);

        FullInstallationResetCanonicalEvidenceV1.WriteDigest(
            target,
            entry.SameHandleOwnershipEvidenceDigest);

        return true;

    }

    private static bool IsValidInventoryEntry(CampaignMarkerInventoryEntryV1 entry) =>
        entry is not null
        && entry.CampaignId != Guid.Empty
        && entry.PriorPathRevision > 0
        && entry.MarkerDigest.IsValid
        && entry.IndexedPhysicalIdentityDigest.IsValid
        && entry.CanonicalDisplayPathDigest.IsValid
        && entry.SameHandleOwnershipEvidenceDigest.IsValid;

}

internal static class FullInstallationResetCanonicalEvidenceV1
{

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static MemoryStream Start(string domain)
    {

        MemoryStream preimage = new();

        preimage.Write(Encoding.ASCII.GetBytes(domain));

        preimage.WriteByte(0x00);

        return preimage;

    }

    internal static bool TryWriteText(MemoryStream target, string value)
    {

        byte[] encoded;

        try
        {

            encoded = StrictUtf8.GetBytes(value);

        }
        catch (EncoderFallbackException)
        {

            return false;

        }

        if (encoded.Length is 0 or > ushort.MaxValue)
        {

            return false;

        }

        Span<byte> length = stackalloc byte[sizeof(ushort)];

        BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)encoded.Length));

        target.Write(length);

        target.Write(encoded);

        return true;

    }

    internal static void WriteGuid(MemoryStream target, Guid value)
    {

        Span<byte> encoded = stackalloc byte[16];

        _ = value.TryWriteBytes(encoded, bigEndian: true, out _);

        target.Write(encoded);

    }

    internal static int CompareGuid(Guid left, Guid right)
    {

        Span<byte> leftBytes = stackalloc byte[16];

        Span<byte> rightBytes = stackalloc byte[16];

        _ = left.TryWriteBytes(leftBytes, bigEndian: true, out _);

        _ = right.TryWriteBytes(rightBytes, bigEndian: true, out _);

        return leftBytes.SequenceCompareTo(rightBytes);

    }

    internal static void WriteUInt64(MemoryStream target, ulong value)
    {

        Span<byte> encoded = stackalloc byte[sizeof(ulong)];

        BinaryPrimitives.WriteUInt64BigEndian(encoded, value);

        target.Write(encoded);

    }

    internal static void WriteDigest(MemoryStream target, CovenantDigest value) =>
        target.Write(value.Bytes);

}
