using System.Buffers.Binary;

using System.Buffers.Text;

using System.Security.Cryptography;

using System.Text;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Core.DataLifecycle;

public sealed class FullInstallationResetRemediationTrustRoot
{

    private const int MaximumSubjectPublicKeyInfoBytes = 512;

    private readonly byte[] _subjectPublicKeyInfo;

    public FullInstallationResetRemediationTrustRoot(byte[] subjectPublicKeyInfo)
    {

        ArgumentNullException.ThrowIfNull(subjectPublicKeyInfo);

        if (subjectPublicKeyInfo.Length is 0 or > MaximumSubjectPublicKeyInfoBytes)
        {

            throw new ArgumentOutOfRangeException(nameof(subjectPublicKeyInfo));

        }

        _subjectPublicKeyInfo = subjectPublicKeyInfo.ToArray();

    }

    internal ReadOnlySpan<byte> SubjectPublicKeyInfo => _subjectPublicKeyInfo;

}

public interface IFullInstallationResetRemediationTrustRootProvider
{

    bool TryResolve(
        string issuer,
        out FullInstallationResetRemediationTrustRoot? trustRoot);

}

public sealed class FullInstallationResetRemediationAuthorization
{

    internal FullInstallationResetRemediationAuthorization(
        Guid operationId,
        Guid installationId,
        CovenantDigest attestationDigest,
        CovenantDigest nonceDigest,
        CovenantDigest issuerDigest,
        DateTimeOffset acceptedAtUtc)
    {

        OperationId = operationId;

        InstallationId = installationId;

        AttestationDigest = attestationDigest;

        NonceDigest = nonceDigest;

        IssuerDigest = issuerDigest;

        AcceptedAtUtc = acceptedAtUtc;

    }

    public Guid OperationId { get; }

    public Guid InstallationId { get; }

    public CovenantDigest AttestationDigest { get; }

    public CovenantDigest NonceDigest { get; }

    public CovenantDigest IssuerDigest { get; }

    public DateTimeOffset AcceptedAtUtc { get; }

}

public interface IFullInstallationResetRemediationAttestationVerifier
{

    /// <summary>
    /// Compares canonical attestation material with an already authenticated stored claim.
    /// </summary>
    /// <remarks>
    /// This comparison grants no authorization. Callers must supply claim fields recovered from
    /// authenticated storage. The method deliberately does not consult a trust root or clock and
    /// does not verify the signature; new authorization always requires <see cref="Verify"/>.
    /// </remarks>
    bool MatchesAuthenticatedClaim(
        FullInstallationResetExternalRemediationAttestation attestation,
        Guid currentInstallationId,
        HostProcessToolsMatchedPair matchedPair,
        Guid acceptedOperationId,
        Guid acceptedInstallationId,
        CovenantDigest acceptedAttestationDigest,
        CovenantDigest acceptedNonceDigest,
        CovenantDigest acceptedIssuerDigest);

    Result<FullInstallationResetRemediationAuthorization> Verify(
        FullInstallationResetExternalRemediationAttestation attestation,
        Guid currentInstallationId,
        HostProcessToolsMatchedPair matchedPair);

}

public sealed class FullInstallationResetRemediationAttestationVerifier(
    IFullInstallationResetRemediationTrustRootProvider trustRoots,
    TimeProvider timeProvider)
    : IFullInstallationResetRemediationAttestationVerifier
{

    private const int SignatureBytes = 64;

    private const string P256ObjectIdentifier = "1.2.840.10045.3.1.7";

    private const string AttestationDigestDomain =
        "Arcanum.FullInstallationReset.ExternalRemediationDigest.v1";

    private const string NonceDigestDomain =
        "Arcanum.FullInstallationReset.ExternalRemediationNonce.v1";

    private const string IssuerDigestDomain =
        "Arcanum.FullInstallationReset.ExternalRemediationIssuer.v1";

    private readonly IFullInstallationResetRemediationTrustRootProvider _trustRoots =
        trustRoots ?? throw new ArgumentNullException(nameof(trustRoots));

    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public Result<FullInstallationResetRemediationAuthorization> Verify(
        FullInstallationResetExternalRemediationAttestation attestation,
        Guid currentInstallationId,
        HostProcessToolsMatchedPair matchedPair)
    {

        if (!TryCalculateClaimProjection(
                attestation,
                currentInstallationId,
                matchedPair,
                out ClaimProjection? projection)
            || projection is null)
        {

            return Invalid();

        }

        using (projection)
        {

            DateTimeOffset observedAtUtc = _timeProvider.GetUtcNow();

            if (observedAtUtc < attestation.IssuedAtUtc
                || observedAtUtc >= attestation.ExpiresAtUtc
                || !_trustRoots.TryResolve(attestation.Issuer, out var trustRoot)
                || trustRoot is null
                || !VerifySignature(
                    trustRoot,
                    projection.Preimage,
                    projection.Signature))
            {

                return Invalid();

            }

            DateTimeOffset acceptedAtUtc = DateTimeOffset.FromUnixTimeSeconds(
                observedAtUtc.ToUnixTimeSeconds());

            return new FullInstallationResetRemediationAuthorization(
                projection.OperationId,
                projection.InstallationId,
                projection.CopyAttestationDigest(),
                projection.CopyNonceDigest(),
                projection.CopyIssuerDigest(),
                acceptedAtUtc);

        }

    }

    /// <inheritdoc />
    public bool MatchesAuthenticatedClaim(
        FullInstallationResetExternalRemediationAttestation attestation,
        Guid currentInstallationId,
        HostProcessToolsMatchedPair matchedPair,
        Guid acceptedOperationId,
        Guid acceptedInstallationId,
        CovenantDigest acceptedAttestationDigest,
        CovenantDigest acceptedNonceDigest,
        CovenantDigest acceptedIssuerDigest)
    {

        if (!TryCalculateClaimProjection(
                attestation,
                currentInstallationId,
                matchedPair,
                out ClaimProjection? projection)
            || projection is null)
        {

            return false;

        }

        using (projection)
        {

            int difference = 0;

            difference |= EqualGuid(projection.OperationId, acceptedOperationId) ? 0 : 1;

            difference |= EqualGuid(projection.InstallationId, acceptedInstallationId) ? 0 : 1;

            difference |= EqualDigest(
                projection.AttestationDigest,
                acceptedAttestationDigest) ? 0 : 1;

            difference |= EqualDigest(
                projection.NonceDigest,
                acceptedNonceDigest) ? 0 : 1;

            difference |= EqualDigest(
                projection.IssuerDigest,
                acceptedIssuerDigest) ? 0 : 1;

            return difference == 0;

        }

    }

    private static bool TryCalculateClaimProjection(
        FullInstallationResetExternalRemediationAttestation attestation,
        Guid currentInstallationId,
        HostProcessToolsMatchedPair matchedPair,
        out ClaimProjection? projection)
    {

        projection = null;

        byte[] preimage = [];

        byte[] signature = [];

        byte[] nonce = [];

        byte[] issuer = [];

        byte[] attestationDigest = [];

        byte[] nonceDigest = [];

        byte[] issuerDigest = [];

        try
        {

            if (attestation is null
                || currentInstallationId == Guid.Empty
                || matchedPair is null
                || matchedPair.Database is null
                || matchedPair.OsMarker is null)
            {

                return false;

            }

            Result<byte[]> preimageResult =
                FullInstallationResetRemediationPreimage.Build(attestation);

            if (preimageResult.IsFailure)
            {

                return false;

            }

            preimage = preimageResult.Value;

            if (!FullInstallationResetRemediationAction.IsExpected(
                    attestation.RemediationActionDigest)
                || !MatchesEvidence(attestation, currentInstallationId, matchedPair)
                || !FullInstallationResetRemediationPreimage.TryDecodeCanonicalBase64Url(
                    attestation.SignatureBase64Url,
                    SignatureBytes,
                    SignatureBytes,
                    out signature)
                || !FullInstallationResetRemediationPreimage.TryDecodeCanonicalBase64Url(
                    attestation.NonceBase64Url,
                    FullInstallationResetRemediationPreimage.MinimumNonceBytes,
                    FullInstallationResetRemediationPreimage.MaximumNonceBytes,
                    out nonce)
                || !FullInstallationResetRemediationPreimage.TryEncodeStrictUtf8(
                    attestation.Issuer,
                    out issuer))
            {

                return false;

            }

            attestationDigest = AttestationDigest(preimage, signature);

            nonceDigest = FramedDigest(NonceDigestDomain, nonce);

            issuerDigest = FramedDigest(IssuerDigestDomain, issuer);

            projection = new ClaimProjection(
                attestation.OperationId,
                attestation.InstallationId,
                preimage,
                signature,
                attestationDigest,
                nonceDigest,
                issuerDigest);

            preimage = [];

            signature = [];

            attestationDigest = [];

            nonceDigest = [];

            issuerDigest = [];

            return true;

        }
        finally
        {

            CryptographicOperations.ZeroMemory(preimage);

            CryptographicOperations.ZeroMemory(signature);

            CryptographicOperations.ZeroMemory(nonce);

            CryptographicOperations.ZeroMemory(issuer);

            CryptographicOperations.ZeroMemory(attestationDigest);

            CryptographicOperations.ZeroMemory(nonceDigest);

            CryptographicOperations.ZeroMemory(issuerDigest);

        }

    }

    private static bool MatchesEvidence(
        FullInstallationResetExternalRemediationAttestation attestation,
        Guid currentInstallationId,
        HostProcessToolsMatchedPair matchedPair)
    {

        HostProcessToolsDatabaseMarkerEvidence database = matchedPair.Database;

        HostProcessToolsOsMarkerEvidence marker = matchedPair.OsMarker;

        if (database.State is not CovenantHostToolsState.HostToolsTainted
            || database.TransitionId is null
            || database.TaintMasterKeyVersion is null
            || database.TaintFingerprint is null
            || !Guid.TryParse(database.InstallationIdentity, out Guid databaseInstallationId)
            || !Guid.TryParse(marker.InstallationIdentity, out Guid markerInstallationId))
        {

            return false;

        }

        int difference = 0;

        difference |= EqualGuid(attestation.InstallationId, currentInstallationId) ? 0 : 1;

        difference |= EqualGuid(databaseInstallationId, currentInstallationId) ? 0 : 1;

        difference |= EqualGuid(markerInstallationId, currentInstallationId) ? 0 : 1;

        difference |= EqualGuid(attestation.HostToolsTransitionId, database.TransitionId.Value) ? 0 : 1;

        difference |= EqualGuid(attestation.HostToolsTransitionId, marker.TransitionId) ? 0 : 1;

        difference |= attestation.TaintMasterKeyVersion == database.TaintMasterKeyVersion.Value ? 0 : 1;

        difference |= attestation.TaintMasterKeyVersion == marker.TaintMasterKeyVersion ? 0 : 1;

        difference |= EqualDigest(attestation.AuthorityFingerprint, database.TaintFingerprint.Value) ? 0 : 1;

        difference |= EqualDigest(attestation.AuthorityFingerprint, marker.TaintFingerprint) ? 0 : 1;

        difference |= EqualDigest(attestation.DatabaseMarkerDigest, database.DatabaseMarkerDigest) ? 0 : 1;

        difference |= EqualDigest(attestation.OsMarkerDigest, marker.MarkerBytesDigest) ? 0 : 1;

        return difference == 0;

    }

    private static bool VerifySignature(
        FullInstallationResetRemediationTrustRoot trustRoot,
        ReadOnlySpan<byte> preimage,
        ReadOnlySpan<byte> signature)
    {

        try
        {

            using ECDsa verifier = ECDsa.Create();

            verifier.ImportSubjectPublicKeyInfo(
                trustRoot.SubjectPublicKeyInfo,
                out int bytesRead);

            if (bytesRead != trustRoot.SubjectPublicKeyInfo.Length
                || verifier.KeySize != 256
                || !string.Equals(
                    verifier.ExportParameters(includePrivateParameters: false).Curve.Oid.Value,
                    P256ObjectIdentifier,
                    StringComparison.Ordinal))
            {

                return false;

            }

            return verifier.VerifyData(
                preimage,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        }
        catch (CryptographicException)
        {

            return false;

        }

    }

    private static byte[] AttestationDigest(
        ReadOnlySpan<byte> preimage,
        ReadOnlySpan<byte> signature)
    {

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        AppendDomain(hash, AttestationDigestDomain);

        AppendLengthPrefixed(hash, preimage);

        AppendLengthPrefixed(hash, signature);

        return hash.GetHashAndReset();

    }

    private static byte[] FramedDigest(
        string domain,
        ReadOnlySpan<byte> value)
    {

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        AppendDomain(hash, domain);

        AppendLengthPrefixed(hash, value);

        return hash.GetHashAndReset();

    }

    private static void AppendDomain(IncrementalHash hash, string domain)
    {

        byte[] encoded = Encoding.ASCII.GetBytes(domain);

        Span<byte> separator = stackalloc byte[1];

        separator.Clear();

        try
        {

            hash.AppendData(encoded);

            hash.AppendData(separator);

        }
        finally
        {

            CryptographicOperations.ZeroMemory(encoded);

            CryptographicOperations.ZeroMemory(separator);

        }

    }

    private static void AppendLengthPrefixed(
        IncrementalHash hash,
        ReadOnlySpan<byte> value)
    {

        Span<byte> length = stackalloc byte[sizeof(ushort)];

        try
        {

            BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)value.Length));

            hash.AppendData(length);

            hash.AppendData(value);

        }
        finally
        {

            CryptographicOperations.ZeroMemory(length);

        }

    }

    private static bool EqualGuid(Guid left, Guid right)
    {

        Span<byte> leftBytes = stackalloc byte[16];

        Span<byte> rightBytes = stackalloc byte[16];

        try
        {

            _ = left.TryWriteBytes(leftBytes, bigEndian: true, out _);

            _ = right.TryWriteBytes(rightBytes, bigEndian: true, out _);

            return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);

        }
        finally
        {

            CryptographicOperations.ZeroMemory(leftBytes);

            CryptographicOperations.ZeroMemory(rightBytes);

        }

    }

    private static bool EqualDigest(CovenantDigest left, CovenantDigest right) =>
        left.IsValid
        && right.IsValid
        && CryptographicOperations.FixedTimeEquals(left.Span, right.Span);

    private static bool EqualDigest(
        ReadOnlySpan<byte> projected,
        CovenantDigest accepted)
    {

        Span<byte> acceptedBytes = stackalloc byte[32];

        acceptedBytes.Clear();

        bool valid = accepted.IsValid;

        if (valid)
        {

            accepted.Span.CopyTo(acceptedBytes);

        }

        try
        {

            int difference = valid ? 0 : 1;

            difference |= CryptographicOperations.FixedTimeEquals(
                projected,
                acceptedBytes) ? 0 : 1;

            return difference == 0;

        }
        finally
        {

            CryptographicOperations.ZeroMemory(acceptedBytes);

        }

    }

    private sealed class ClaimProjection(
        Guid operationId,
        Guid installationId,
        byte[] preimage,
        byte[] signature,
        byte[] attestationDigest,
        byte[] nonceDigest,
        byte[] issuerDigest)
        : IDisposable
    {

        private readonly byte[] _preimage = preimage;

        private readonly byte[] _signature = signature;

        private readonly byte[] _attestationDigest = attestationDigest;

        private readonly byte[] _nonceDigest = nonceDigest;

        private readonly byte[] _issuerDigest = issuerDigest;

        internal Guid OperationId { get; } = operationId;

        internal Guid InstallationId { get; } = installationId;

        internal ReadOnlySpan<byte> Preimage => _preimage;

        internal ReadOnlySpan<byte> Signature => _signature;

        internal ReadOnlySpan<byte> AttestationDigest => _attestationDigest;

        internal ReadOnlySpan<byte> NonceDigest => _nonceDigest;

        internal ReadOnlySpan<byte> IssuerDigest => _issuerDigest;

        internal CovenantDigest CopyAttestationDigest() =>
            new(_attestationDigest);

        internal CovenantDigest CopyNonceDigest() =>
            new(_nonceDigest);

        internal CovenantDigest CopyIssuerDigest() =>
            new(_issuerDigest);

        public void Dispose()
        {

            CryptographicOperations.ZeroMemory(_preimage);

            CryptographicOperations.ZeroMemory(_signature);

            CryptographicOperations.ZeroMemory(_attestationDigest);

            CryptographicOperations.ZeroMemory(_nonceDigest);

            CryptographicOperations.ZeroMemory(_issuerDigest);

        }

    }

    private static Error Invalid() =>
        new(
            ErrorCodes.Data.ExternalRemediationInvalid,
            "The external remediation attestation could not be verified.");

}

internal static class FullInstallationResetRemediationAction
{

    private const byte ScopeAllCode = 1;

    private const string Domain =
        "Arcanum.FullInstallationReset.RemediationAction.v1";

    internal static CovenantDigest ExpectedDigest { get; } = Compute();

    internal static bool IsExpected(CovenantDigest candidate) =>
        candidate.IsValid
        && CryptographicOperations.FixedTimeEquals(
            candidate.Span,
            ExpectedDigest.Span);

    private static CovenantDigest Compute()
    {

        byte[] preimage = new byte[Domain.Length + 2];

        int written = Encoding.ASCII.GetBytes(Domain, preimage);

        preimage[written++] = 0x00;

        preimage[written] = ScopeAllCode;

        return new CovenantDigest(SHA256.HashData(preimage));

    }

}

internal static class FullInstallationResetRemediationPreimage
{

    internal const byte Version = 1;

    internal const int MinimumNonceBytes = 16;

    internal const int MaximumNonceBytes = 32;

    internal const int MaximumIssuerBytes = 128;

    internal static readonly TimeSpan MaximumLifetime = TimeSpan.FromHours(24);

    internal const string Issuer = "RetroDownfall.Remediation.v1";

    private const string Domain =
        "Arcanum.FullInstallationReset.ExternalRemediation.v1";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static Result<byte[]> Build(
        FullInstallationResetExternalRemediationAttestation attestation)
    {

        if (attestation is null
            || attestation.Version != Version
            || attestation.OperationId == Guid.Empty
            || attestation.InstallationId == Guid.Empty
            || attestation.HostToolsTransitionId == Guid.Empty
            || attestation.TaintMasterKeyVersion == 0
            || !attestation.AuthorityFingerprint.IsValid
            || !attestation.DatabaseMarkerDigest.IsValid
            || !attestation.OsMarkerDigest.IsValid
            || !attestation.RemediationActionDigest.IsValid
            || !string.Equals(attestation.Issuer, Issuer, StringComparison.Ordinal)
            || attestation.IssuedAtUtc.Offset != TimeSpan.Zero
            || attestation.ExpiresAtUtc.Offset != TimeSpan.Zero
            || attestation.IssuedAtUtc.Ticks % TimeSpan.TicksPerSecond != 0
            || attestation.ExpiresAtUtc.Ticks % TimeSpan.TicksPerSecond != 0
            || attestation.ExpiresAtUtc <= attestation.IssuedAtUtc
            || attestation.ExpiresAtUtc - attestation.IssuedAtUtc > MaximumLifetime)
        {

            return Invalid();

        }

        byte[] nonce = [];

        byte[] issuer = [];

        try
        {

            if (!TryDecodeCanonicalBase64Url(
                    attestation.NonceBase64Url,
                    MinimumNonceBytes,
                    MaximumNonceBytes,
                    out nonce)
                || !TryEncodeStrictUtf8(attestation.Issuer, out issuer)
                || issuer.Length > MaximumIssuerBytes)
            {

                return Invalid();

            }

            byte[] preimage = new byte[
                Domain.Length
                + 1
                + sizeof(byte)
                + (3 * 16)
                + sizeof(ulong)
                + (4 * 32)
                + sizeof(ushort)
                + nonce.Length
                + sizeof(ushort)
                + issuer.Length
                + (2 * sizeof(long))];

            int written = Encoding.ASCII.GetBytes(Domain, preimage);

            preimage[written++] = 0x00;

            preimage[written++] = attestation.Version;

            WriteGuid(preimage, ref written, attestation.OperationId);

            WriteGuid(preimage, ref written, attestation.InstallationId);

            WriteGuid(preimage, ref written, attestation.HostToolsTransitionId);

            BinaryPrimitives.WriteUInt64BigEndian(
                preimage.AsSpan(written),
                attestation.TaintMasterKeyVersion);

            written += sizeof(ulong);

            WriteDigest(preimage, ref written, attestation.AuthorityFingerprint);

            WriteDigest(preimage, ref written, attestation.DatabaseMarkerDigest);

            WriteDigest(preimage, ref written, attestation.OsMarkerDigest);

            WriteDigest(preimage, ref written, attestation.RemediationActionDigest);

            WriteLengthPrefixed(preimage, ref written, nonce);

            WriteLengthPrefixed(preimage, ref written, issuer);

            BinaryPrimitives.WriteInt64BigEndian(
                preimage.AsSpan(written),
                attestation.IssuedAtUtc.ToUnixTimeSeconds());

            written += sizeof(long);

            BinaryPrimitives.WriteInt64BigEndian(
                preimage.AsSpan(written),
                attestation.ExpiresAtUtc.ToUnixTimeSeconds());

            return preimage;

        }
        finally
        {

            CryptographicOperations.ZeroMemory(nonce);

            CryptographicOperations.ZeroMemory(issuer);

        }

    }

    internal static bool TryDecodeCanonicalBase64Url(
        string? value,
        int minimumBytes,
        int maximumBytes,
        out byte[] decoded)
    {

        decoded = [];

        if (string.IsNullOrEmpty(value)
            || value.Length > Base64Url.GetEncodedLength(maximumBytes))
        {

            return false;

        }

        foreach (char character in value)
        {

            if (character is not (>= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '-'
                or '_'))
            {

                return false;

            }

        }

        byte[] buffer = new byte[Base64Url.GetMaxDecodedLength(value.Length)];

        try
        {

            if (!Base64Url.TryDecodeFromChars(value, buffer, out int written)
                || written < minimumBytes
                || written > maximumBytes)
            {

                return false;

            }

            byte[] exact = buffer[..written];

            if (!string.Equals(
                    Base64Url.EncodeToString(exact),
                    value,
                    StringComparison.Ordinal))
            {

                CryptographicOperations.ZeroMemory(exact);

                return false;

            }

            decoded = exact;

            return true;

        }
        catch (FormatException)
        {

            return false;

        }
        finally
        {

            CryptographicOperations.ZeroMemory(buffer);

        }

    }

    internal static bool TryEncodeStrictUtf8(string? value, out byte[] encoded)
    {

        encoded = [];

        if (value is null)
        {

            return false;

        }

        try
        {

            encoded = StrictUtf8.GetBytes(value);

            return true;

        }
        catch (EncoderFallbackException)
        {

            return false;

        }

    }

    private static void WriteGuid(byte[] destination, ref int offset, Guid value)
    {

        _ = value.TryWriteBytes(destination.AsSpan(offset), bigEndian: true, out int written);

        offset += written;

    }

    private static void WriteDigest(
        byte[] destination,
        ref int offset,
        CovenantDigest value)
    {

        value.Span.CopyTo(destination.AsSpan(offset));

        offset += 32;

    }

    private static void WriteLengthPrefixed(
        byte[] destination,
        ref int offset,
        byte[] value)
    {

        BinaryPrimitives.WriteUInt16BigEndian(
            destination.AsSpan(offset),
            checked((ushort)value.Length));

        offset += sizeof(ushort);

        value.CopyTo(destination.AsSpan(offset));

        offset += value.Length;

    }

    private static Error Invalid() =>
        new(
            ErrorCodes.Data.ExternalRemediationInvalid,
            "The external remediation attestation could not be verified.");

}
