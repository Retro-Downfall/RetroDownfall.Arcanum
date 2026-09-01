using System.Buffers.Binary;

using System.Buffers.Text;

using System.Security.Cryptography;

using System.Text;

using System.Text.Json;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

namespace RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

internal static class GrimoireOfflineTransitionJournalAuthenticator
{

    internal const byte EnvelopeVersion = 1;

    internal const byte AnchorVersion = 1;

    internal const int KeyBytes = 32;

    internal const int NonceBytes = 12;

    internal const int TagBytes = 16;

    internal const int MaxHandlerPayloadBytes = 256 * 1024;

    internal const int MaxPlaintextBytes = 512 * 1024;

    internal const int MaxJournalFileBytes = 1024 * 1024;

    internal const int MaxAnchorCharacters = 2048;

    internal const ulong MaxRevision = 1_000_000;

    internal const ulong MaxSlotEpoch = 1_000_000;

    internal const string JournalLocationDomain =
        "Arcanum.GrimoireOfflineTransition.JournalLocation.v1";

    internal const string EnvelopeAssociatedDataDomain =
        "Arcanum.GrimoireOfflineTransition.JournalEnvelope.v1";

    internal const string EnvelopeDigestDomain =
        "Arcanum.GrimoireOfflineTransition.JournalEnvelopeDigest.v1";

    private const int EncodedNonceCharacters = 16;

    private const int EncodedTagCharacters = 22;

    internal static Result<CovenantDigest> JournalLocation(
        CovenantDigest profileNamespaceDigest,
        CovenantDigest guardedParentPhysicalIdentityDigest,
        string journalChildLeaf)
    {

        if (!profileNamespaceDigest.IsValid || !guardedParentPhysicalIdentityDigest.IsValid
            || !TryEncodeLeaf(journalChildLeaf, out byte[] leaf))
        {

            return Invalid<CovenantDigest>();

        }

        byte[] preimage = new byte[
            JournalLocationDomain.Length + 1 + CovenantLimits.DigestBytes + CovenantLimits.DigestBytes
            + sizeof(ushort) + leaf.Length];

        int written = Encoding.ASCII.GetBytes(JournalLocationDomain, preimage);

        preimage[written++] = 0;

        written += Copy(preimage.AsSpan(written), profileNamespaceDigest);

        written += Copy(preimage.AsSpan(written), guardedParentPhysicalIdentityDigest);

        BinaryPrimitives.WriteUInt16BigEndian(preimage.AsSpan(written), checked((ushort)leaf.Length));

        written += sizeof(ushort);

        leaf.CopyTo(preimage.AsSpan(written));

        return new CovenantDigest(SHA256.HashData(preimage));

    }

    internal static Result<GrimoireOfflineTransitionEnvelopeV1> Seal(
        GrimoireOfflineTransitionJournalKeyLease key,
        CovenantDigest profileNamespaceDigest,
        Guid installationId,
        ulong slotEpoch,
        Guid operationId,
        GrimoireOfflineTransitionKind kind,
        byte payloadVersion,
        ulong revision,
        CovenantDigest previousEnvelopeDigest,
        CovenantDigest journalLocationDigest,
        ReadOnlySpan<byte> payloadBytes)
    {

        ArgumentNullException.ThrowIfNull(key);

        if (!ValidHeader(
                profileNamespaceDigest,
                installationId,
                slotEpoch,
                operationId,
                kind,
                payloadVersion,
                revision,
                previousEnvelopeDigest,
                journalLocationDigest)
            || payloadBytes.Length > MaxHandlerPayloadBytes)
        {

            return Invalid<GrimoireOfflineTransitionEnvelopeV1>();

        }

        GrimoireOfflineTransitionPayloadV1 payload = new(
            operationId,
            kind,
            payloadVersion,
            Base64Url.EncodeToString(payloadBytes));

        byte[] plaintext;

        try
        {

            plaintext = JsonSerializer.SerializeToUtf8Bytes(
                payload,
                GrimoireOfflineTransitionJournalJsonContext.Default.GrimoireOfflineTransitionPayloadV1);

        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {

            return Invalid<GrimoireOfflineTransitionEnvelopeV1>();

        }

        try
        {

            if (plaintext.Length > MaxPlaintextBytes)
            {

                return Invalid<GrimoireOfflineTransitionEnvelopeV1>();

            }

            byte[] nonce = RandomNumberGenerator.GetBytes(NonceBytes);

            byte[] ciphertext = new byte[plaintext.Length];

            byte[] tag = new byte[TagBytes];

            byte[] aad = AssociatedData(
                EnvelopeVersion,
                profileNamespaceDigest,
                installationId,
                slotEpoch,
                operationId,
                kind,
                payloadVersion,
                revision,
                previousEnvelopeDigest,
                journalLocationDigest);

            BackupRestoreJournalAuthenticator.StableJournalAesOutcome outcome =
                BackupRestoreJournalAuthenticator.EncryptGrimoireOfflineTransitionJournal(
                    key,
                    nonce,
                    plaintext,
                    ciphertext,
                    tag,
                    aad);

            if (outcome is BackupRestoreJournalAuthenticator.StableJournalAesOutcome.LeaseSpent)
            {

                return new Error(
                    ErrorCodes.Covenant.LifecycleConflict,
                    "This transition journal key lease has already been spent.");

            }

            if (outcome is not BackupRestoreJournalAuthenticator.StableJournalAesOutcome.Completed)
            {

                return Invalid<GrimoireOfflineTransitionEnvelopeV1>();

            }

            return new GrimoireOfflineTransitionEnvelopeV1(
                EnvelopeVersion,
                profileNamespaceDigest,
                installationId,
                slotEpoch,
                operationId,
                kind,
                payloadVersion,
                revision,
                previousEnvelopeDigest,
                journalLocationDigest,
                Base64Url.EncodeToString(nonce),
                Base64Url.EncodeToString(ciphertext),
                Base64Url.EncodeToString(tag));

        }
        finally
        {

            CryptographicOperations.ZeroMemory(plaintext);

        }

    }

    internal static Result<byte[]> Open(
        GrimoireOfflineTransitionJournalKeyLease key,
        CovenantDigest expectedProfileNamespaceDigest,
        Guid expectedInstallationId,
        CovenantDigest expectedJournalLocationDigest,
        GrimoireOfflineTransitionEnvelopeV1 envelope)
    {

        ArgumentNullException.ThrowIfNull(key);

        if (envelope is null || !expectedProfileNamespaceDigest.IsValid
            || !expectedJournalLocationDigest.IsValid || expectedInstallationId == Guid.Empty
            || envelope.ProfileNamespaceDigest != expectedProfileNamespaceDigest
            || envelope.InstallationId != expectedInstallationId
            || envelope.JournalLocationDigest != expectedJournalLocationDigest
            || !ValidEnvelope(envelope)
            || !TryDecodeExact(envelope.NonceBase64Url, EncodedNonceCharacters, NonceBytes, out byte[] nonce)
            || !TryDecodeExact(envelope.AuthenticationTagBase64Url, EncodedTagCharacters, TagBytes, out byte[] tag)
            || !TryDecodeBounded(envelope.CiphertextBase64Url, MaxPlaintextBytes, out byte[] ciphertext))
        {

            return Invalid<byte[]>();

        }

        byte[] plaintext = new byte[ciphertext.Length];

        byte[] aad = AssociatedData(
            envelope.Version,
            envelope.ProfileNamespaceDigest,
            envelope.InstallationId,
            envelope.SlotEpoch,
            envelope.OperationId,
            envelope.Kind,
            envelope.PayloadVersion,
            envelope.Revision,
            envelope.PreviousEnvelopeDigest,
            envelope.JournalLocationDigest);

        BackupRestoreJournalAuthenticator.StableJournalAesOutcome outcome =
            BackupRestoreJournalAuthenticator.DecryptGrimoireOfflineTransitionJournal(
                key,
                nonce,
                ciphertext,
                tag,
                plaintext,
                aad);

        if (outcome is BackupRestoreJournalAuthenticator.StableJournalAesOutcome.LeaseSpent)
        {

            CryptographicOperations.ZeroMemory(plaintext);

            return new Error(
                ErrorCodes.Covenant.LifecycleConflict,
                "This transition journal key lease has already been spent.");

        }

        if (outcome is not BackupRestoreJournalAuthenticator.StableJournalAesOutcome.Completed)
        {

            CryptographicOperations.ZeroMemory(plaintext);

            return Invalid<byte[]>();

        }

        try
        {

            GrimoireOfflineTransitionPayloadV1? payload = JsonSerializer.Deserialize(
                plaintext,
                GrimoireOfflineTransitionJournalJsonContext.Default.GrimoireOfflineTransitionPayloadV1);

            if (payload is null || payload.OperationId != envelope.OperationId
                || payload.Kind != envelope.Kind || payload.PayloadVersion != envelope.PayloadVersion
                || !TryDecodeBounded(payload.PayloadBase64Url, MaxHandlerPayloadBytes, out byte[] bytes))
            {

                return Invalid<byte[]>();

            }

            byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(
                payload,
                GrimoireOfflineTransitionJournalJsonContext.Default.GrimoireOfflineTransitionPayloadV1);

            try
            {

                return plaintext.AsSpan().SequenceEqual(canonical) ? bytes : Invalid<byte[]>();

            }
            finally
            {

                CryptographicOperations.ZeroMemory(canonical);

            }

        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
        {

            return Invalid<byte[]>();

        }
        finally
        {

            CryptographicOperations.ZeroMemory(plaintext);

        }

    }

    internal static Result<CovenantDigest> EnvelopeDigest(
        GrimoireOfflineTransitionEnvelopeV1 envelope)
    {

        if (envelope is null || !ValidEnvelope(envelope))
        {

            return Invalid<CovenantDigest>();

        }

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        hash.AppendData(Encoding.ASCII.GetBytes(EnvelopeDigestDomain));

        hash.AppendData([0]);

        hash.AppendData([envelope.Version]);

        hash.AppendData(envelope.ProfileNamespaceDigest.Bytes);

        AppendGuid(hash, envelope.InstallationId);

        AppendUInt64(hash, envelope.SlotEpoch);

        AppendGuid(hash, envelope.OperationId);

        hash.AppendData([(byte)envelope.Kind, envelope.PayloadVersion]);

        AppendUInt64(hash, envelope.Revision);

        hash.AppendData(envelope.PreviousEnvelopeDigest.Bytes);

        hash.AppendData(envelope.JournalLocationDigest.Bytes);

        Span<byte> length = stackalloc byte[sizeof(uint)];

        foreach (string value in (string[])
                 [
                     envelope.NonceBase64Url,
                     envelope.CiphertextBase64Url,
                     envelope.AuthenticationTagBase64Url,
                 ])
        {

            byte[] bytes = Encoding.ASCII.GetBytes(value);

            BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length));

            hash.AppendData(length);

            hash.AppendData(bytes);

        }

        return new CovenantDigest(hash.GetHashAndReset());

    }

    internal static Result<byte[]> EncodeEnvelope(GrimoireOfflineTransitionEnvelopeV1 envelope)
    {

        if (envelope is null || !ValidEnvelope(envelope))
        {

            return Invalid<byte[]>();

        }

        try
        {

            byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(
                envelope,
                GrimoireOfflineTransitionJournalJsonContext.Default.GrimoireOfflineTransitionEnvelopeV1);

            return encoded.Length <= MaxJournalFileBytes ? encoded : Invalid<byte[]>();

        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {

            return Invalid<byte[]>();

        }

    }

    internal static Result<GrimoireOfflineTransitionEnvelopeV1> DecodeEnvelope(ReadOnlySpan<byte> utf8)
    {

        if (utf8.Length is 0 or > MaxJournalFileBytes)
        {

            return Invalid<GrimoireOfflineTransitionEnvelopeV1>();

        }

        try
        {

            GrimoireOfflineTransitionEnvelopeV1? envelope = JsonSerializer.Deserialize(
                utf8,
                GrimoireOfflineTransitionJournalJsonContext.Default.GrimoireOfflineTransitionEnvelopeV1);

            if (envelope is null || !ValidEnvelope(envelope))
            {

                return Invalid<GrimoireOfflineTransitionEnvelopeV1>();

            }

            Result<byte[]> canonical = EncodeEnvelope(envelope);

            return canonical.IsSuccess && utf8.SequenceEqual(canonical.Value)
                ? envelope
                : Invalid<GrimoireOfflineTransitionEnvelopeV1>();

        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
        {

            return Invalid<GrimoireOfflineTransitionEnvelopeV1>();

        }

    }

    internal static Result<string> EncodeAnchor(GrimoireOfflineTransitionAnchorV1 anchor)
    {

        Result valid = ValidateAnchor(anchor);

        if (valid.IsFailure)
        {

            return Result<string>.Failure(valid.Error);

        }

        try
        {

            string encoded = JsonSerializer.Serialize(
                anchor,
                GrimoireOfflineTransitionJournalJsonContext.Default.GrimoireOfflineTransitionAnchorV1);

            return encoded.Length <= MaxAnchorCharacters ? encoded : Invalid<string>();

        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {

            return Invalid<string>();

        }

    }

    internal static Result<GrimoireOfflineTransitionAnchorV1> DecodeAnchor(string? value)
    {

        if (string.IsNullOrEmpty(value) || value.Length > MaxAnchorCharacters)
        {

            return Invalid<GrimoireOfflineTransitionAnchorV1>();

        }

        try
        {

            GrimoireOfflineTransitionAnchorV1? anchor = JsonSerializer.Deserialize(
                value,
                GrimoireOfflineTransitionJournalJsonContext.Default.GrimoireOfflineTransitionAnchorV1);

            if (anchor is null || ValidateAnchor(anchor).IsFailure)
            {

                return Invalid<GrimoireOfflineTransitionAnchorV1>();

            }

            Result<string> canonical = EncodeAnchor(anchor);

            return canonical.IsSuccess && string.Equals(canonical.Value, value, StringComparison.Ordinal)
                ? anchor
                : Invalid<GrimoireOfflineTransitionAnchorV1>();

        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
        {

            return Invalid<GrimoireOfflineTransitionAnchorV1>();

        }

    }

    internal static Result ValidateAnchor(GrimoireOfflineTransitionAnchorV1 anchor)
    {

        if (anchor is null || anchor.Version != AnchorVersion || !anchor.ProfileNamespaceDigest.IsValid
            || anchor.InstallationId == Guid.Empty || !anchor.JournalLocationDigest.IsValid
            || anchor.SlotEpoch > MaxSlotEpoch || anchor.Revision > MaxRevision
            || anchor.State is not (GrimoireOfflineTransitionAnchorState.Active
                or GrimoireOfflineTransitionAnchorState.Closed))
        {

            return Invalid();

        }

        bool hasOperation = anchor.OperationId is { } operation && operation != Guid.Empty;

        bool hasKind = anchor.Kind is { } kind && Enum.IsDefined(kind);

        bool hasPayloadVersion = anchor.PayloadVersion is > 0;

        bool hasDigest = anchor.EnvelopeDigest is { } digest && digest.IsValid;

        if (anchor.SlotEpoch == 0)
        {

            return anchor.State is GrimoireOfflineTransitionAnchorState.Closed
                && anchor.Revision == 0
                && anchor.OperationId is null
                && anchor.Kind is null
                && anchor.PayloadVersion is null
                && anchor.EnvelopeDigest is null
                ? Result.Success()
                : Invalid();

        }

        if (!hasOperation || !hasKind || !hasPayloadVersion
            || (anchor.EnvelopeDigest is not null && !hasDigest))
        {

            return Invalid();

        }

        return anchor.State is GrimoireOfflineTransitionAnchorState.Active
            ? anchor.Revision == 0
                ? anchor.EnvelopeDigest is null ? Result.Success() : Invalid()
                : hasDigest ? Result.Success() : Invalid()
            : anchor.Revision == 0
                ? anchor.EnvelopeDigest is null ? Result.Success() : Invalid()
                : hasDigest ? Result.Success() : Invalid();

    }

    private static bool ValidEnvelope(GrimoireOfflineTransitionEnvelopeV1 envelope) =>
        envelope.Version == EnvelopeVersion
        && ValidHeader(
            envelope.ProfileNamespaceDigest,
            envelope.InstallationId,
            envelope.SlotEpoch,
            envelope.OperationId,
            envelope.Kind,
            envelope.PayloadVersion,
            envelope.Revision,
            envelope.PreviousEnvelopeDigest,
            envelope.JournalLocationDigest)
        && IsCanonicalEncoded(envelope.NonceBase64Url, EncodedNonceCharacters, NonceBytes)
        && IsCanonicalEncoded(envelope.AuthenticationTagBase64Url, EncodedTagCharacters, TagBytes)
        && IsCanonicalEncodedBounded(envelope.CiphertextBase64Url, MaxPlaintextBytes);

    private static bool ValidHeader(
        CovenantDigest profileNamespaceDigest,
        Guid installationId,
        ulong slotEpoch,
        Guid operationId,
        GrimoireOfflineTransitionKind kind,
        byte payloadVersion,
        ulong revision,
        CovenantDigest previousEnvelopeDigest,
        CovenantDigest journalLocationDigest) =>
        profileNamespaceDigest.IsValid
        && installationId != Guid.Empty
        && slotEpoch is > 0 and <= MaxSlotEpoch
        && operationId != Guid.Empty
        && Enum.IsDefined(kind)
        && payloadVersion != 0
        && revision is > 0 and <= MaxRevision
        && previousEnvelopeDigest.IsValid
        && journalLocationDigest.IsValid;

    private static byte[] AssociatedData(
        byte version,
        CovenantDigest profileNamespaceDigest,
        Guid installationId,
        ulong slotEpoch,
        Guid operationId,
        GrimoireOfflineTransitionKind kind,
        byte payloadVersion,
        ulong revision,
        CovenantDigest previousEnvelopeDigest,
        CovenantDigest journalLocationDigest)
    {

        byte[] bytes = new byte[
            EnvelopeAssociatedDataDomain.Length + 1 + CovenantLimits.DigestBytes + 16 + sizeof(ulong)
            + 16 + 1 + 1 + sizeof(ulong) + CovenantLimits.DigestBytes + CovenantLimits.DigestBytes];

        int written = Encoding.ASCII.GetBytes(EnvelopeAssociatedDataDomain, bytes);

        bytes[written++] = version;

        written += Copy(bytes.AsSpan(written), profileNamespaceDigest);

        WriteGuid(bytes.AsSpan(written), installationId);

        written += 16;

        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(written), slotEpoch);

        written += sizeof(ulong);

        WriteGuid(bytes.AsSpan(written), operationId);

        written += 16;

        bytes[written++] = (byte)kind;

        bytes[written++] = payloadVersion;

        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(written), revision);

        written += sizeof(ulong);

        written += Copy(bytes.AsSpan(written), previousEnvelopeDigest);

        _ = Copy(bytes.AsSpan(written), journalLocationDigest);

        return bytes;

    }

    private static void AppendGuid(IncrementalHash hash, Guid value)
    {

        Span<byte> bytes = stackalloc byte[16];

        WriteGuid(bytes, value);

        hash.AppendData(bytes);

    }

    private static void AppendUInt64(IncrementalHash hash, ulong value)
    {

        Span<byte> bytes = stackalloc byte[sizeof(ulong)];

        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);

        hash.AppendData(bytes);

    }

    private static void WriteGuid(Span<byte> destination, Guid value) =>
        value.TryWriteBytes(destination, bigEndian: true, out _);

    private static int Copy(Span<byte> destination, CovenantDigest value)
    {

        value.Bytes.CopyTo(destination);

        return CovenantLimits.DigestBytes;

    }

    private static bool TryEncodeLeaf(string? value, out byte[] leaf)
    {

        leaf = [];

        if (string.IsNullOrEmpty(value) || value is "." or ".." || value.Contains(Path.DirectorySeparatorChar)
            || value.Contains(Path.AltDirectorySeparatorChar))
        {

            return false;

        }

        try
        {

            leaf = Encoding.UTF8.GetBytes(value);

            return leaf.Length is > 0 and <= 255;

        }
        catch (EncoderFallbackException)
        {

            return false;

        }

    }

    private static bool IsCanonicalEncoded(string? value, int expectedCharacters, int expectedBytes) =>
        TryDecodeExact(value, expectedCharacters, expectedBytes, out _);

    private static bool IsCanonicalEncodedBounded(string? value, int maximumBytes) =>
        TryDecodeBounded(value, maximumBytes, out _);

    private static bool TryDecodeExact(
        string? encoded,
        int expectedCharacters,
        int expectedBytes,
        out byte[] decoded)
    {

        decoded = [];

        return encoded is { Length: var length }
            && length == expectedCharacters
            && TryDecodeBounded(encoded, expectedBytes, out decoded)
            && decoded.Length == expectedBytes;

    }

    private static bool TryDecodeBounded(string? encoded, int maximumBytes, out byte[] decoded)
    {

        decoded = [];

        if (string.IsNullOrEmpty(encoded) || encoded.Any(static value => value is not (>= 'A' and <= 'Z')
            and not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '-' and not '_'))
        {

            return false;

        }

        int maximumCharacters = checked(((maximumBytes + 2) / 3) * 4);

        if (encoded.Length > maximumCharacters)
        {

            return false;

        }

        byte[] buffer = new byte[Base64Url.GetMaxDecodedLength(encoded.Length)];

        if (buffer.Length > maximumBytes || !Base64Url.TryDecodeFromChars(encoded, buffer, out int written))
        {

            CryptographicOperations.ZeroMemory(buffer);

            return false;

        }

        if (written != buffer.Length)
        {

            Array.Resize(ref buffer, written);

        }

        if (!string.Equals(Base64Url.EncodeToString(buffer), encoded, StringComparison.Ordinal))
        {

            CryptographicOperations.ZeroMemory(buffer);

            return false;

        }

        decoded = buffer;

        return true;

    }

    private static Result Invalid() => new Error(
        ErrorCodes.Covenant.IntegrityFailure,
        "This transition journal evidence is malformed or cannot be authenticated.");

    private static Result<T> Invalid<T>() => Result<T>.Failure(Invalid().Error);

}
