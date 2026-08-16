using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// The one AES-256-GCM framing behind every opaque Covenant token.
/// </summary>
/// <remarks>
/// Wire layout is exactly <c>base64url(header || ciphertext || tag)</c>, unpadded, ASCII only. The
/// 46-byte header is the associated data, so every field a route reads before decryption — version,
/// purpose, key version, epoch, counter, both timestamps, ciphertext length — is authenticated even
/// though it is not encrypted (§10.12).
///
/// <para>The nonce carries no random bytes: it is <c>UInt32BE(purpose) || UInt64BE(counter)</c>, and
/// the counter is a per-purpose interlocked sequence starting at one. A deterministic nonce is the
/// stronger choice here precisely because the key is boot-salted — a repeated counter after a database
/// rollback lands under a different key, whereas a random 96-bit nonce would have a birthday bound
/// this design does not need to reason about.</para>
///
/// <para>Both timestamps appear twice: in the authenticated header and again at the head of the
/// plaintext. Decode proves they are equal. That redundancy costs sixteen bytes and removes an entire
/// class of confusion in which a caller reasons about a header value while the payload was built
/// against a different one.</para>
///
/// <para>Every parse bound is checked before any allocation sized from the token, and every
/// cryptographic failure collapses to one content-free result. A decoder that distinguished "wrong
/// key" from "tampered tag" would be an oracle.</para>
/// </remarks>
internal sealed class CovenantEnvelopeCodec(
    ICovenantEnvelopeMasterKeyProvider keys,
    TimeProvider timeProvider) : ICovenantEnvelopeCodec
{

    private static readonly byte[] MagicBytes = Encoding.ASCII.GetBytes(CovenantEnvelopeLimits.Magic);

    private const int MaxCipherTextBytes =
        CovenantEnvelopeLimits.BodyTimeBytes + CovenantEnvelopeLimits.MaxPayloadBytes;

    private const int MaxWireBytes =
        CovenantEnvelopeLimits.HeaderBytes + MaxCipherTextBytes + CovenantEnvelopeLimits.TagBytes;

    /// <inheritdoc/>
    public CovenantEnvelopeKeySnapshot KeySnapshot =>
        keys.Current?.Snapshot
        ?? new CovenantEnvelopeKeySnapshot(0, 0, 0, string.Empty, null);

    /// <inheritdoc/>
    public Result<string> Encode(
        CovenantEnvelopePurpose purpose,
        ReadOnlySpan<byte> payload,
        TimeSpan lifetime)
    {

        if (!Enum.IsDefined(purpose))
        {
            return Result<string>.Failure(
                new Error(ErrorCodes.Covenant.InvalidCursor, "An undefined envelope purpose cannot be issued."));
        }

        if (payload.Length > CovenantEnvelopeLimits.MaxPayloadBytes)
        {
            return Result<string>.Failure(
                new Error(ErrorCodes.Covenant.CapacityExceeded, "This envelope payload exceeds its bound."));
        }

        if (lifetime <= TimeSpan.Zero || lifetime > CovenantEnvelopeLimits.MaxLifetime)
        {
            return Result<string>.Failure(
                new Error(ErrorCodes.Covenant.InvalidCursor, "This envelope lifetime is outside its bound."));
        }

        if (keys.Current is not { } generation)
        {
            return Result<string>.Failure(
                new Error(
                    ErrorCodes.Covenant.OperatorAuthorityUnavailable,
                    "Covenant envelope keys are not available."));
        }

        ReadOnlySpan<byte> key = generation.PurposeKey(purpose);

        if (key.Length != 32)
        {
            return Result<string>.Failure(
                new Error(
                    ErrorCodes.Covenant.Unavailable,
                    "This envelope purpose has no key in the current generation."));
        }

        if (!generation.TryReserveCounter(purpose, out ulong counter))
        {
            return Result<string>.Failure(
                new Error(
                    ErrorCodes.Covenant.CapacityExceeded,
                    "This envelope purpose has exhausted its issuance counter and must re-key."));
        }

        DateTimeOffset issuedAt = timeProvider.GetUtcNow();

        DateTimeOffset expiresAt = issuedAt + lifetime;

        int cipherTextLength = CovenantEnvelopeLimits.BodyTimeBytes + payload.Length;

        Span<byte> wire = stackalloc byte[CovenantEnvelopeLimits.HeaderBytes + cipherTextLength + CovenantEnvelopeLimits.TagBytes];

        WriteHeader(
            wire,
            purpose,
            generation.Snapshot.MasterKeyVersion,
            generation.EpochFor(purpose),
            counter,
            issuedAt,
            expiresAt,
            cipherTextLength);

        Span<byte> plaintext = stackalloc byte[cipherTextLength];

        BinaryPrimitives.WriteInt64BigEndian(plaintext, issuedAt.ToUnixTimeMilliseconds());

        BinaryPrimitives.WriteInt64BigEndian(plaintext[8..], expiresAt.ToUnixTimeMilliseconds());

        payload.CopyTo(plaintext[CovenantEnvelopeLimits.BodyTimeBytes..]);

        Span<byte> nonce = stackalloc byte[CovenantEnvelopeLimits.NonceBytes];

        WriteNonce(nonce, purpose, counter);

        try
        {

            using AesGcm aes = new(key, CovenantEnvelopeLimits.TagBytes);

            aes.Encrypt(
                nonce,
                plaintext,
                wire.Slice(CovenantEnvelopeLimits.HeaderBytes, cipherTextLength),
                wire[(CovenantEnvelopeLimits.HeaderBytes + cipherTextLength)..],
                wire[..CovenantEnvelopeLimits.HeaderBytes]);

        }
        finally
        {

            CryptographicOperations.ZeroMemory(plaintext);

        }

        return Result<string>.Success(Base64Url.EncodeToString(wire));

    }

    /// <inheritdoc/>
    public Result<CovenantEnvelopeBody> Decode(CovenantEnvelopePurpose expectedPurpose, string? token)
    {

        if (!Enum.IsDefined(expectedPurpose))
        {
            return Invalid();
        }

        if (string.IsNullOrEmpty(token) || token.Length > CovenantEnvelopeLimits.MaxTokenCharacters)
        {
            return Invalid();
        }

        if (!IsUnpaddedBase64Url(token))
        {
            return Invalid();
        }

        // Bound the character count against the largest token this framing can produce, before
        // touching a buffer. The check is on the encoded length rather than an estimate of the decoded
        // one so that a token exactly at the payload ceiling still parses.
        if (token.Length > Base64Url.GetEncodedLength(MaxWireBytes))
        {
            return Invalid();
        }

        Span<byte> wire = stackalloc byte[MaxWireBytes];

        if (!Base64Url.TryDecodeFromChars(token, wire, out int wireLength))
        {
            return Invalid();
        }

        wire = wire[..wireLength];

        if (wireLength < CovenantEnvelopeLimits.HeaderBytes + CovenantEnvelopeLimits.BodyTimeBytes + CovenantEnvelopeLimits.TagBytes)
        {
            return Invalid();
        }

        ReadOnlySpan<byte> header = wire[..CovenantEnvelopeLimits.HeaderBytes];

        if (!header[..4].SequenceEqual(MagicBytes) || header[4] != CovenantEnvelopeLimits.Version)
        {
            return Invalid();
        }

        byte purposeCode = header[5];

        if (!Enum.IsDefined((CovenantEnvelopePurpose)purposeCode))
        {
            return Invalid();
        }

        CovenantEnvelopePurpose purpose = (CovenantEnvelopePurpose)purposeCode;

        if (purpose != expectedPurpose)
        {
            return Result<CovenantEnvelopeBody>.Failure(
                CovenantEnvelopeErrors.For(CovenantEnvelopeDecodeFailure.PurposeMismatch));
        }

        uint masterKeyVersion = BinaryPrimitives.ReadUInt32BigEndian(header[6..]);

        long epoch = BinaryPrimitives.ReadInt64BigEndian(header[10..]);

        ulong counter = BinaryPrimitives.ReadUInt64BigEndian(header[18..]);

        long headerIssuedMs = BinaryPrimitives.ReadInt64BigEndian(header[26..]);

        long headerExpiresMs = BinaryPrimitives.ReadInt64BigEndian(header[34..]);

        uint declaredCipherTextLength = BinaryPrimitives.ReadUInt32BigEndian(header[42..]);

        int actualCipherTextLength = wireLength - CovenantEnvelopeLimits.HeaderBytes - CovenantEnvelopeLimits.TagBytes;

        if (declaredCipherTextLength != (uint)actualCipherTextLength
            || actualCipherTextLength < CovenantEnvelopeLimits.BodyTimeBytes
            || actualCipherTextLength > MaxCipherTextBytes)
        {
            return Invalid();
        }

        if (counter is 0 or > CovenantEnvelopeLimits.CounterRolloverBound)
        {
            return Invalid();
        }

        if (headerExpiresMs <= headerIssuedMs)
        {
            return Invalid();
        }

        if (keys.Current is not { } generation)
        {
            return Invalid();
        }

        // Only the current key, epoch, and version are accepted. Every transition that advances one of
        // these exists to invalidate work in flight, so a grace window would defeat all of them.
        if (masterKeyVersion != generation.Snapshot.MasterKeyVersion || epoch != generation.EpochFor(purpose))
        {
            return Invalid();
        }

        ReadOnlySpan<byte> key = generation.PurposeKey(purpose);

        if (key.Length != 32)
        {
            return Invalid();
        }

        Span<byte> plaintext = stackalloc byte[actualCipherTextLength];

        Span<byte> nonce = stackalloc byte[CovenantEnvelopeLimits.NonceBytes];

        WriteNonce(nonce, purpose, counter);

        try
        {

            using AesGcm aes = new(key, CovenantEnvelopeLimits.TagBytes);

            aes.Decrypt(
                nonce,
                wire.Slice(CovenantEnvelopeLimits.HeaderBytes, actualCipherTextLength),
                wire[(CovenantEnvelopeLimits.HeaderBytes + actualCipherTextLength)..],
                plaintext,
                header);

        }
        catch (CryptographicException)
        {

            CryptographicOperations.ZeroMemory(plaintext);

            return Invalid();

        }

        long bodyIssuedMs = BinaryPrimitives.ReadInt64BigEndian(plaintext);

        long bodyExpiresMs = BinaryPrimitives.ReadInt64BigEndian(plaintext[8..]);

        if (bodyIssuedMs != headerIssuedMs || bodyExpiresMs != headerExpiresMs)
        {

            CryptographicOperations.ZeroMemory(plaintext);

            return Invalid();

        }

        byte[] payload = plaintext[CovenantEnvelopeLimits.BodyTimeBytes..].ToArray();

        CryptographicOperations.ZeroMemory(plaintext);

        DateTimeOffset issuedAt = DateTimeOffset.FromUnixTimeMilliseconds(bodyIssuedMs);

        DateTimeOffset expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(bodyExpiresMs);

        if (timeProvider.GetUtcNow() >= expiresAt)
        {
            return Result<CovenantEnvelopeBody>.Failure(
                CovenantEnvelopeErrors.For(CovenantEnvelopeDecodeFailure.Expired));
        }

        return Result<CovenantEnvelopeBody>.Success(
            new CovenantEnvelopeBody(
                purpose,
                masterKeyVersion,
                epoch,
                counter,
                issuedAt,
                expiresAt,
                payload));

    }

    private static void WriteHeader(
        Span<byte> destination,
        CovenantEnvelopePurpose purpose,
        uint masterKeyVersion,
        long epoch,
        ulong counter,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt,
        int cipherTextLength)
    {

        MagicBytes.CopyTo(destination);

        destination[4] = CovenantEnvelopeLimits.Version;

        destination[5] = (byte)purpose;

        BinaryPrimitives.WriteUInt32BigEndian(destination[6..], masterKeyVersion);

        BinaryPrimitives.WriteInt64BigEndian(destination[10..], epoch);

        BinaryPrimitives.WriteUInt64BigEndian(destination[18..], counter);

        BinaryPrimitives.WriteInt64BigEndian(destination[26..], issuedAt.ToUnixTimeMilliseconds());

        BinaryPrimitives.WriteInt64BigEndian(destination[34..], expiresAt.ToUnixTimeMilliseconds());

        BinaryPrimitives.WriteUInt32BigEndian(destination[42..], (uint)cipherTextLength);

    }

    private static void WriteNonce(Span<byte> destination, CovenantEnvelopePurpose purpose, ulong counter)
    {

        BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)purpose);

        BinaryPrimitives.WriteUInt64BigEndian(destination[4..], counter);

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

    private static Result<CovenantEnvelopeBody> Invalid() =>
        Result<CovenantEnvelopeBody>.Failure(
            CovenantEnvelopeErrors.For(CovenantEnvelopeDecodeFailure.Invalid));

}
