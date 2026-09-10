using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace RetroDownfall.Arcanum.Api.Security;

/// <summary>
/// Proves that the process answering Arcanum's local port knows the installed API-key digest before
/// a client sends the API key. Every proof is bound to a fresh nonce, the loopback authority, and
/// this protocol version, so a response cannot be replayed at another request or endpoint.
/// </summary>
public static class ArcanumPresenceProofProtocol
{
    public const int NonceBytes = 32;

    public const int ProofBytes = 32;

    public const int KeyDigestBytes = 32;

    public const int CapabilityEnvelopeBytes =
        AesNonceBytes + ArcanumProcessCapabilityService.TokenBytes + AesTagBytes;

    public const int MaxAuthorityBytes = 512;

    public const string Path = "/api/presence";

    public const string Version = "2";

    private const int AesNonceBytes = 12;

    private const int AesTagBytes = 16;

    private static ReadOnlySpan<byte> ProofDomain =>
        "Arcanum.LocalPresenceProof.v2\0"u8;

    private static ReadOnlySpan<byte> EncryptionKeyDomain =>
        "Arcanum.LocalPresenceCapabilityKey.v1\0"u8;

    private static ReadOnlySpan<byte> AssociatedDataDomain =>
        "Arcanum.LocalPresenceCapabilityAad.v1\0"u8;

    public static byte[] ComputeProof(
        ReadOnlySpan<byte> keyDigest,
        ReadOnlySpan<byte> nonce,
        string authority,
        ReadOnlySpan<byte> capabilityEnvelope)
    {
        ValidateInputs(keyDigest, nonce, authority);

        if (capabilityEnvelope.Length != CapabilityEnvelopeBytes)
        {
            throw new ArgumentException(
                $"The encrypted process capability must contain exactly {CapabilityEnvelopeBytes} bytes.",
                nameof(capabilityEnvelope));
        }

        byte[] message = BuildTranscript(
            ProofDomain,
            nonce,
            authority,
            capabilityEnvelope);

        try
        {
            return HMACSHA256.HashData(keyDigest, message);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(message);
        }
    }

    /// <summary>
    /// Reads the bounded validity window carried by a decrypted process capability. This validates
    /// only the canonical token shape; callers must first authenticate the enclosing presence proof.
    /// </summary>
    public static bool TryReadCapabilityValidityWindow(
        ReadOnlySpan<byte> capability,
        out DateTimeOffset issuedAt,
        out DateTimeOffset expiresAt) =>
        ArcanumProcessCapabilityService.TryReadValidityWindow(
            capability,
            out issuedAt,
            out expiresAt);

    public static bool VerifyProof(
        ReadOnlySpan<byte> keyDigest,
        ReadOnlySpan<byte> nonce,
        string authority,
        ReadOnlySpan<byte> capabilityEnvelope,
        ReadOnlySpan<byte> proof)
    {
        if (proof.Length != ProofBytes)
        {
            return false;
        }

        byte[] expected = ComputeProof(
            keyDigest,
            nonce,
            authority,
            capabilityEnvelope);

        try
        {
            return CryptographicOperations.FixedTimeEquals(expected, proof);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expected);
        }
    }

    public static string Encode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value);

    public static byte[] CreateCapabilityEnvelope(
        ReadOnlySpan<byte> keyDigest,
        ReadOnlySpan<byte> nonce,
        string authority,
        ReadOnlySpan<byte> processCapability)
    {
        ValidateInputs(keyDigest, nonce, authority);

        if (processCapability.Length != ArcanumProcessCapabilityService.TokenBytes)
        {
            throw new ArgumentException(
                $"The process capability must contain exactly {ArcanumProcessCapabilityService.TokenBytes} bytes.",
                nameof(processCapability));
        }

        byte[] derivedKey = DeriveEncryptionKey(
            keyDigest,
            nonce,
            authority);

        byte[] associatedData = BuildTranscript(
            AssociatedDataDomain,
            nonce,
            authority,
            ReadOnlySpan<byte>.Empty);

        byte[] envelope = new byte[CapabilityEnvelopeBytes];
        Span<byte> aesNonce = envelope.AsSpan(0, AesNonceBytes);
        Span<byte> ciphertext = envelope.AsSpan(
            AesNonceBytes,
            ArcanumProcessCapabilityService.TokenBytes);
        Span<byte> tag = envelope.AsSpan(
            AesNonceBytes + ArcanumProcessCapabilityService.TokenBytes,
            AesTagBytes);

        RandomNumberGenerator.Fill(aesNonce);

        try
        {
            using AesGcm aes = new(derivedKey, AesTagBytes);
            aes.Encrypt(
                aesNonce,
                processCapability,
                ciphertext,
                tag,
                associatedData);

            return envelope;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(envelope);

            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedKey);
            CryptographicOperations.ZeroMemory(associatedData);
        }
    }

    public static bool TryDecryptCapabilityEnvelope(
        ReadOnlySpan<byte> keyDigest,
        ReadOnlySpan<byte> nonce,
        string authority,
        ReadOnlySpan<byte> capabilityEnvelope,
        out byte[]? processCapability)
    {
        processCapability = null;

        if (keyDigest.Length != KeyDigestBytes
            || nonce.Length != NonceBytes
            || string.IsNullOrWhiteSpace(authority)
            || Encoding.UTF8.GetByteCount(authority) > MaxAuthorityBytes
            || capabilityEnvelope.Length != CapabilityEnvelopeBytes)
        {
            return false;
        }

        byte[] derivedKey = DeriveEncryptionKey(
            keyDigest,
            nonce,
            authority);

        byte[] associatedData = BuildTranscript(
            AssociatedDataDomain,
            nonce,
            authority,
            ReadOnlySpan<byte>.Empty);

        byte[] plaintext = new byte[ArcanumProcessCapabilityService.TokenBytes];

        try
        {
            using AesGcm aes = new(derivedKey, AesTagBytes);
            aes.Decrypt(
                capabilityEnvelope[..AesNonceBytes],
                capabilityEnvelope.Slice(
                    AesNonceBytes,
                    ArcanumProcessCapabilityService.TokenBytes),
                capabilityEnvelope[^AesTagBytes..],
                plaintext,
                associatedData);

            processCapability = plaintext;

            return true;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plaintext);

            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedKey);
            CryptographicOperations.ZeroMemory(associatedData);
        }
    }

    public static bool TryDecode(
        string? encoded,
        int expectedBytes,
        out byte[]? value)
    {
        value = null;

        if (expectedBytes <= 0
            || encoded is null
            || encoded.Length != ((expectedBytes + 2) / 3) * 4)
        {
            return false;
        }

        byte[] decoded = new byte[expectedBytes];

        if (!Convert.TryFromBase64String(encoded, decoded, out int written)
            || written != expectedBytes
            || !string.Equals(
                Convert.ToBase64String(decoded),
                encoded,
                StringComparison.Ordinal))
        {
            CryptographicOperations.ZeroMemory(decoded);

            return false;
        }

        value = decoded;

        return true;
    }

    public static bool TryCanonicalAuthority(
        Uri uri,
        out string? authority)
    {
        ArgumentNullException.ThrowIfNull(uri);

        authority = null;

        if (!uri.IsAbsoluteUri
            || !uri.IsLoopback
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        string host = uri.IdnHost.ToLowerInvariant();

        if (uri.HostNameType == UriHostNameType.IPv6)
        {
            host = $"[{host.Trim('[', ']')}]";
        }

        string candidate = $"{uri.Scheme.ToLowerInvariant()}://{host}:{uri.Port}";

        if (Encoding.UTF8.GetByteCount(candidate) > MaxAuthorityBytes)
        {
            return false;
        }

        authority = candidate;

        return true;
    }

    private static void ValidateInputs(
        ReadOnlySpan<byte> keyDigest,
        ReadOnlySpan<byte> nonce,
        string authority)
    {
        if (keyDigest.Length != KeyDigestBytes)
        {
            throw new ArgumentException(
                $"The API-key digest must contain exactly {KeyDigestBytes} bytes.",
                nameof(keyDigest));
        }

        if (nonce.Length != NonceBytes)
        {
            throw new ArgumentException(
                $"The presence nonce must contain exactly {NonceBytes} bytes.",
                nameof(nonce));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(authority);

        if (Encoding.UTF8.GetByteCount(authority) > MaxAuthorityBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(authority),
                $"The authority may contain at most {MaxAuthorityBytes} UTF-8 bytes.");
        }
    }

    private static byte[] DeriveEncryptionKey(
        ReadOnlySpan<byte> keyDigest,
        ReadOnlySpan<byte> nonce,
        string authority)
    {
        byte[] transcript = BuildTranscript(
            EncryptionKeyDomain,
            nonce,
            authority,
            ReadOnlySpan<byte>.Empty);

        try
        {
            return HMACSHA256.HashData(keyDigest, transcript);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(transcript);
        }
    }

    private static byte[] BuildTranscript(
        ReadOnlySpan<byte> domain,
        ReadOnlySpan<byte> nonce,
        string authority,
        ReadOnlySpan<byte> payload)
    {
        int authorityByteCount = Encoding.UTF8.GetByteCount(authority);

        byte[] message = new byte[
            domain.Length
            + sizeof(int)
            + authorityByteCount
            + nonce.Length
            + sizeof(int)
            + payload.Length];

        Span<byte> destination = message;
        domain.CopyTo(destination);

        int offset = domain.Length;

        BinaryPrimitives.WriteInt32BigEndian(
            destination.Slice(offset, sizeof(int)),
            authorityByteCount);

        offset += sizeof(int);
        offset += Encoding.UTF8.GetBytes(authority, destination[offset..]);
        nonce.CopyTo(destination[offset..]);
        offset += nonce.Length;

        BinaryPrimitives.WriteInt32BigEndian(
            destination.Slice(offset, sizeof(int)),
            payload.Length);

        offset += sizeof(int);
        payload.CopyTo(destination[offset..]);

        return message;
    }
}
