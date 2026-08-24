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
internal sealed class CovenantEnvelopeCodec : ICovenantEnvelopeCodec
{

    private readonly ICovenantEnvelopeMasterKeyProvider keys;

    private readonly TimeProvider timeProvider;

    private readonly ICovenantEnvelopeCodecCheckpoint _checkpoint;

    private static readonly byte[] MagicBytes = Encoding.ASCII.GetBytes(CovenantEnvelopeLimits.Magic);

    private const int MaxCipherTextBytes =
        CovenantEnvelopeLimits.BodyTimeBytes + CovenantEnvelopeLimits.MaxPayloadBytes;

    private const int MaxWireBytes =
        CovenantEnvelopeLimits.HeaderBytes + MaxCipherTextBytes + CovenantEnvelopeLimits.TagBytes;

    internal CovenantEnvelopeCodec(
        ICovenantEnvelopeMasterKeyProvider keys,
        TimeProvider timeProvider)
        : this(keys, timeProvider, CovenantEnvelopeCodecCheckpoint.None)
    {
    }

    internal CovenantEnvelopeCodec(
        ICovenantEnvelopeMasterKeyProvider keys,
        TimeProvider timeProvider,
        ICovenantEnvelopeCodecCheckpoint checkpoint)
    {

        this.keys = keys ?? throw new ArgumentNullException(nameof(keys));

        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        _checkpoint = checkpoint ?? throw new ArgumentNullException(nameof(checkpoint));

    }

    /// <inheritdoc/>
    public CovenantEnvelopeKeySnapshot KeySnapshot =>
        keys.Current?.Snapshot
        ?? new CovenantEnvelopeKeySnapshot(0, 0, 0, string.Empty, null);

    /// <inheritdoc/>
    public Result<string> Encode(
        CovenantEnvelopePurpose purpose,
        ReadOnlySpan<byte> payload,
        TimeSpan lifetime,
        DateTimeOffset? issuedAtUtc = null)
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

        // Backdating only shortens a token's life and is what a caller aligning its payload with this
        // stamp is doing. Forward-dating would extend it past the lifetime that was asked for.
        if (issuedAtUtc is { } stated)
        {

            DateTimeOffset now = timeProvider.GetUtcNow();

            if (stated > now)
            {
                return Result<string>.Failure(
                    new Error(ErrorCodes.Covenant.InvalidCursor, "An envelope cannot be issued in the future."));
            }

            // Bounded on the other side too, on Decode's own terms: a stamp at least a whole lifetime
            // old mints a token that the very next Decode refuses as expired. Without this the caller
            // is handed a dead token and learns why one round trip later, and the header's issued-at —
            // which is authenticated, and which every consumer of this port reads — is caller-asserted
            // with no bound at all on how far into the past it may be stamped.
            if (now - stated >= lifetime)
            {
                return Result<string>.Failure(
                    new Error(ErrorCodes.Covenant.InvalidCursor, "An envelope cannot be issued already expired."));
            }

        }

        Span<byte> key = stackalloc byte[32];

        try
        {

            CovenantEnvelopeKeyCopyStatus copyStatus = keys.TryCopyPurposeKeyAndReserve(
                purpose,
                key,
                out CovenantEnvelopeKeyReservation reservation);

            if (copyStatus == CovenantEnvelopeKeyCopyStatus.NoGeneration)
            {
                return Result<string>.Failure(
                    new Error(
                        ErrorCodes.Covenant.OperatorAuthorityUnavailable,
                        "Covenant envelope keys are not available."));
            }

            if (copyStatus == CovenantEnvelopeKeyCopyStatus.PurposeUnavailable)
            {
                return Result<string>.Failure(
                    new Error(
                        ErrorCodes.Covenant.Unavailable,
                        "This envelope purpose has no key in the current generation."));
            }

            if (copyStatus == CovenantEnvelopeKeyCopyStatus.CounterExhausted)
            {
                return Result<string>.Failure(
                    new Error(
                        ErrorCodes.Covenant.CapacityExceeded,
                        "This envelope purpose has exhausted its issuance counter and must re-key."));
            }

            _checkpoint.Reached(CovenantEnvelopeCodecStep.PurposeKeyCopied);

            DateTimeOffset issuedAt = issuedAtUtc ?? timeProvider.GetUtcNow();

            DateTimeOffset expiresAt = issuedAt + lifetime;

            int cipherTextLength = CovenantEnvelopeLimits.BodyTimeBytes + payload.Length;

            Span<byte> wire = stackalloc byte[
                CovenantEnvelopeLimits.HeaderBytes + cipherTextLength + CovenantEnvelopeLimits.TagBytes];

            Span<byte> plaintext = stackalloc byte[cipherTextLength];

            Span<byte> nonce = stackalloc byte[CovenantEnvelopeLimits.NonceBytes];

            try
            {

                WriteHeader(
                    wire,
                    purpose,
                    reservation.Snapshot.MasterKeyVersion,
                    reservation.Epoch,
                    reservation.Counter,
                    issuedAt,
                    expiresAt,
                    cipherTextLength);

                BinaryPrimitives.WriteInt64BigEndian(plaintext, issuedAt.ToUnixTimeMilliseconds());

                BinaryPrimitives.WriteInt64BigEndian(plaintext[8..], expiresAt.ToUnixTimeMilliseconds());

                payload.CopyTo(plaintext[CovenantEnvelopeLimits.BodyTimeBytes..]);

                WriteNonce(nonce, purpose, reservation.Counter);

                using AesGcm aes = new(key, CovenantEnvelopeLimits.TagBytes);

                aes.Encrypt(
                    nonce,
                    plaintext,
                    wire.Slice(CovenantEnvelopeLimits.HeaderBytes, cipherTextLength),
                    wire[(CovenantEnvelopeLimits.HeaderBytes + cipherTextLength)..],
                    wire[..CovenantEnvelopeLimits.HeaderBytes]);

                _checkpoint.Reached(CovenantEnvelopeCodecStep.CryptographyCompleted);

                _checkpoint.Reached(CovenantEnvelopeCodecStep.BeforeGenerationRevalidation);

                using CovenantEnvelopeMaterializationLease materialization =
                    keys.AcquireMaterializationLease(
                        reservation.RuntimeAuthorityGeneration,
                        reservation.Identity);

                if (!materialization.IsCurrent)
                {
                    return Stale<string>();
                }

                _checkpoint.Reached(CovenantEnvelopeCodecStep.CurrentGenerationProven);

                return Result<string>.Success(Base64Url.EncodeToString(wire));

            }
            finally
            {

                ZeroAndObserve(plaintext, CovenantEnvelopeCodecBufferKind.Plaintext);

                ZeroAndObserve(nonce, CovenantEnvelopeCodecBufferKind.Nonce);

                ZeroAndObserve(wire, CovenantEnvelopeCodecBufferKind.Wire);

            }

        }
        finally
        {

            ZeroAndObserve(key, CovenantEnvelopeCodecBufferKind.Key);

        }

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

        Span<byte> wireBuffer = stackalloc byte[MaxWireBytes];

        try
        {

            if (!Base64Url.TryDecodeFromChars(token, wireBuffer, out int wireLength))
            {
                return Invalid();
            }

            Span<byte> wire = wireBuffer[..wireLength];

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

            int actualCipherTextLength =
                wireLength - CovenantEnvelopeLimits.HeaderBytes - CovenantEnvelopeLimits.TagBytes;

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

            Span<byte> key = stackalloc byte[32];

            try
            {

                CovenantEnvelopeKeyCopyStatus copyStatus = keys.TryCopyPurposeKey(
                    purpose,
                    key,
                    out CovenantEnvelopeKeyCapture capture);

                if (copyStatus != CovenantEnvelopeKeyCopyStatus.Success)
                {
                    return Invalid();
                }

                _checkpoint.Reached(CovenantEnvelopeCodecStep.PurposeKeyCopied);

                // Only the current key, epoch, and version are accepted. Every transition that advances
                // one of these exists to invalidate work in flight, so a grace window would defeat all of
                // them.
                if (masterKeyVersion != capture.Snapshot.MasterKeyVersion || epoch != capture.Epoch)
                {
                    return Invalid();
                }

                Span<byte> plaintext = stackalloc byte[actualCipherTextLength];

                Span<byte> nonce = stackalloc byte[CovenantEnvelopeLimits.NonceBytes];

                try
                {

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
                        return Invalid();
                    }

                    _checkpoint.Reached(CovenantEnvelopeCodecStep.CryptographyCompleted);

                    _checkpoint.Reached(CovenantEnvelopeCodecStep.BeforeGenerationRevalidation);

                    using CovenantEnvelopeMaterializationLease materialization =
                        keys.AcquireMaterializationLease(
                            capture.RuntimeAuthorityGeneration,
                            capture.Identity);

                    if (!materialization.IsCurrent)
                    {
                        return Stale<CovenantEnvelopeBody>();
                    }

                    _checkpoint.Reached(CovenantEnvelopeCodecStep.CurrentGenerationProven);

                    long bodyIssuedMs = BinaryPrimitives.ReadInt64BigEndian(plaintext);

                    long bodyExpiresMs = BinaryPrimitives.ReadInt64BigEndian(plaintext[8..]);

                    if (bodyIssuedMs != headerIssuedMs || bodyExpiresMs != headerExpiresMs)
                    {
                        return Invalid();
                    }

                    DateTimeOffset issuedAt = DateTimeOffset.FromUnixTimeMilliseconds(bodyIssuedMs);

                    DateTimeOffset expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(bodyExpiresMs);

                    if (timeProvider.GetUtcNow() >= expiresAt)
                    {
                        return Result<CovenantEnvelopeBody>.Failure(
                            CovenantEnvelopeErrors.For(CovenantEnvelopeDecodeFailure.Expired));
                    }

                    byte[] payload = plaintext[CovenantEnvelopeLimits.BodyTimeBytes..].ToArray();

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
                finally
                {

                    ZeroAndObserve(plaintext, CovenantEnvelopeCodecBufferKind.Plaintext);

                    ZeroAndObserve(nonce, CovenantEnvelopeCodecBufferKind.Nonce);

                }

            }
            finally
            {

                ZeroAndObserve(key, CovenantEnvelopeCodecBufferKind.Key);

            }

        }
        finally
        {

            ZeroAndObserve(wireBuffer, CovenantEnvelopeCodecBufferKind.Wire);

        }

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

    private void ZeroAndObserve(
        Span<byte> buffer,
        CovenantEnvelopeCodecBufferKind kind)
    {

        CryptographicOperations.ZeroMemory(buffer);

        _checkpoint.Zeroized(kind, IsZero(buffer));

    }

    private static bool IsZero(ReadOnlySpan<byte> buffer)
    {

        foreach (byte value in buffer)
        {

            if (value != 0)
            {
                return false;
            }

        }

        return true;

    }

    private static Result<T> Stale<T>() =>
        Result<T>.Failure(
            new Error(
                ErrorCodes.Covenant.StaleSnapshot,
                "Covenant envelope keys changed while this operation was in flight."));

    private static Result<CovenantEnvelopeBody> Invalid() =>
        Result<CovenantEnvelopeBody>.Failure(
            CovenantEnvelopeErrors.For(CovenantEnvelopeDecodeFailure.Invalid));

}

/// <summary>Content-free checkpoints exposed only for deterministic codec race tests.</summary>
internal interface ICovenantEnvelopeCodecCheckpoint
{

    void Reached(CovenantEnvelopeCodecStep step);

    void Zeroized(CovenantEnvelopeCodecBufferKind kind, bool isZero);

}

internal enum CovenantEnvelopeCodecStep
{

    PurposeKeyCopied = 1,

    CryptographyCompleted = 2,

    BeforeGenerationRevalidation = 3,

    CurrentGenerationProven = 4,

}

internal enum CovenantEnvelopeCodecBufferKind
{

    Key = 1,

    Plaintext = 2,

    Nonce = 3,

    Wire = 4,

}

internal static class CovenantEnvelopeCodecCheckpoint
{

    internal static ICovenantEnvelopeCodecCheckpoint None { get; } = new NoOpCheckpoint();

    private sealed class NoOpCheckpoint : ICovenantEnvelopeCodecCheckpoint
    {

        public void Reached(CovenantEnvelopeCodecStep step)
        {
        }

        public void Zeroized(CovenantEnvelopeCodecBufferKind kind, bool isZero)
        {
        }

    }

}
