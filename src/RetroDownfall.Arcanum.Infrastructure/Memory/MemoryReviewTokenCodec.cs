using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;

using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Memory;

internal sealed class MemoryReviewTokenCodec(TimeProvider timeProvider) : IMemoryReviewTokenCodec, IMemoryErasureTokenCodec
{
    private const byte FormatVersion = 1;

    private const int HeaderBytes = 18;

    private const int CommonFactsBytes = 73;

    private const int DigestBytes = MemoryReviewDigest.Size;

    private const int TagBytes = 32;

    private const int CursorPayloadBytes = CommonFactsBytes + sizeof(ulong) + DigestBytes;

    private const int ObservationPayloadBytes = CommonFactsBytes + sizeof(ulong) + DigestBytes;

    private const int PreparedPlanPayloadBytes = CommonFactsBytes + DigestBytes;

    private const int ErasureDigestBytes = MemoryErasureDigestGrammar.DigestBytes;

    /// <summary>
    /// <c>u8 store ‖ 32 request ‖ 32 effect ‖ u8 hasBinding ‖ 32 binding-or-zero ‖ u8 hasGeneration ‖
    /// 16 generation (big-endian)-or-zero</c>: fixed, so every erasure plan token is the same length.
    /// </summary>
    private const int ErasurePlanPayloadBytes = 1 + ErasureDigestBytes + ErasureDigestBytes + 1 + ErasureDigestBytes + 1 + 16;

    /// <summary>
    /// <c>u8 status ‖ 3 × u64be unverifiable fingerprints ‖ 3 × u64be unverifiable receipts</c>, each
    /// triple in store-code order: Covenant, Saga, Lexicon. Its length is unique among the purposes.
    /// </summary>
    private const int ErasureKeyResetPayloadBytes = 1 + (6 * sizeof(ulong));

    // One unexported key for this process lifetime. A process restart deliberately invalidates every
    // token, while independently constructed codec instances in the same process - a host restarted in
    // process included - remain interoperable.
    private static readonly byte[] ProcessKey = RandomNumberGenerator.GetBytes(32);

    private static readonly Error InvalidToken =
        new(ErrorCodes.MemoryReview.InvalidToken, "The memory-review token is invalid, expired, or has the wrong purpose.");

    private static readonly Error InvalidFacts =
        new(ErrorCodes.MemoryReview.InvalidTokenFacts, "The memory-review token facts are incomplete or inconsistent.");

    /// <summary>Every erasure read failure, whatever its cause, so none is distinguishable from another.</summary>
    private static readonly Error InvalidPreflight =
        new(ErrorCodes.MemoryErasure.InvalidPreflight, "The erasure preflight token is invalid, expired, or has the wrong purpose.");

    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public Result<string> IssueCursor(MemoryReviewCursorTokenFacts facts)
    {
        if (facts is null || !ValidCommon(
                facts.Store,
                facts.CanonicalScopeDigest,
                facts.MarkerGeneration,
                facts.FrozenLowerEventSequence,
                facts.FrozenUpperEventSequence)
            || !WithinFrontier(
                facts.KeysetEventSequence,
                facts.FrozenLowerEventSequence,
                facts.FrozenUpperEventSequence)
            || !facts.KeysetVersionIdentity.IsValid)
        {
            return InvalidFacts;
        }

        return Issue(TokenPurpose.Cursor, CursorPayloadBytes, payload =>
        {
            int offset = WriteCommon(payload, facts.Store, facts.CanonicalScopeDigest, facts.MarkerGeneration,
                facts.MarkerRevision, facts.FrozenLowerEventSequence, facts.FrozenUpperEventSequence);

            WriteUInt64(payload, ref offset, facts.KeysetEventSequence);

            WriteDigest(payload, ref offset, facts.KeysetVersionIdentity);
        });
    }

    public Result<MemoryReviewCursorTokenFacts> ReadCursor(string token)
    {
        Result<byte[]> read = Read(token, TokenPurpose.Cursor, CursorPayloadBytes);

        if (read.IsFailure)
        {
            return read.Error;
        }

        ReadOnlySpan<byte> payload = read.Value;

        int offset = 0;

        CommonFacts common = ReadCommon(payload, ref offset);

        MemoryReviewCursorTokenFacts facts = new(
            common.Store,
            common.ScopeDigest,
            common.MarkerGeneration,
            common.MarkerRevision,
            common.LowerFrontier,
            common.UpperFrontier,
            ReadUInt64(payload, ref offset),
            ReadDigest(payload, ref offset));

        return ValidCommon(common) && WithinFrontier(
                facts.KeysetEventSequence,
                facts.FrozenLowerEventSequence,
                facts.FrozenUpperEventSequence)
            && facts.KeysetVersionIdentity.IsValid
            ? facts
            : InvalidToken;
    }

    public Result<string> IssueObservation(MemoryReviewObservationTokenFacts facts)
    {
        if (facts is null || !ValidCommon(
                facts.Store,
                facts.CanonicalScopeDigest,
                facts.MarkerGeneration,
                facts.FrozenLowerEventSequence,
                facts.FrozenUpperEventSequence)
            || !WithinFrontier(facts.EventSequence, facts.FrozenLowerEventSequence, facts.FrozenUpperEventSequence)
            || !facts.VersionIdentity.IsValid)
        {
            return InvalidFacts;
        }

        return Issue(TokenPurpose.Observation, ObservationPayloadBytes, payload =>
        {
            int offset = WriteCommon(payload, facts.Store, facts.CanonicalScopeDigest, facts.MarkerGeneration,
                facts.MarkerRevision, facts.FrozenLowerEventSequence, facts.FrozenUpperEventSequence);

            WriteUInt64(payload, ref offset, facts.EventSequence);

            WriteDigest(payload, ref offset, facts.VersionIdentity);
        });
    }

    public Result<MemoryReviewObservationTokenFacts> ReadObservation(string token)
    {
        Result<byte[]> read = Read(token, TokenPurpose.Observation, ObservationPayloadBytes);

        if (read.IsFailure)
        {
            return read.Error;
        }

        ReadOnlySpan<byte> payload = read.Value;

        int offset = 0;

        CommonFacts common = ReadCommon(payload, ref offset);

        MemoryReviewObservationTokenFacts facts = new(
            common.Store,
            common.ScopeDigest,
            common.MarkerGeneration,
            common.MarkerRevision,
            common.LowerFrontier,
            common.UpperFrontier,
            ReadUInt64(payload, ref offset),
            ReadDigest(payload, ref offset));

        return ValidCommon(common) && WithinFrontier(
                facts.EventSequence,
                facts.FrozenLowerEventSequence,
                facts.FrozenUpperEventSequence)
            && facts.VersionIdentity.IsValid
            ? facts
            : InvalidToken;
    }

    public Result<string> IssuePreparedPlan(MemoryReviewPreparedPlanTokenFacts facts)
    {
        if (facts is null || !ValidCommon(
                facts.Store,
                facts.CanonicalScopeDigest,
                facts.MarkerGeneration,
                facts.FrozenLowerEventSequence,
                facts.FrozenUpperEventSequence)
            || !facts.OrderedRequestDigest.IsValid)
        {
            return InvalidFacts;
        }

        return Issue(TokenPurpose.PreparedPlan, PreparedPlanPayloadBytes, payload =>
        {
            int offset = WriteCommon(payload, facts.Store, facts.CanonicalScopeDigest, facts.MarkerGeneration,
                facts.MarkerRevision, facts.FrozenLowerEventSequence, facts.FrozenUpperEventSequence);

            WriteDigest(payload, ref offset, facts.OrderedRequestDigest);
        });
    }

    public Result<MemoryReviewPreparedPlanTokenFacts> ReadPreparedPlan(string token)
    {
        Result<byte[]> read = Read(token, TokenPurpose.PreparedPlan, PreparedPlanPayloadBytes);

        if (read.IsFailure)
        {
            return read.Error;
        }

        ReadOnlySpan<byte> payload = read.Value;

        int offset = 0;

        CommonFacts common = ReadCommon(payload, ref offset);

        MemoryReviewPreparedPlanTokenFacts facts = new(
            common.Store,
            common.ScopeDigest,
            common.MarkerGeneration,
            common.MarkerRevision,
            common.LowerFrontier,
            common.UpperFrontier,
            ReadDigest(payload, ref offset));

        return ValidCommon(common) && facts.OrderedRequestDigest.IsValid
            ? facts
            : InvalidToken;
    }

    public Result<MemoryErasureIssuedToken> IssueErasurePlan(MemoryErasurePlanTokenFacts facts)
    {
        if (facts is null
            || facts.Store is not (MemoryReviewStore.Saga or MemoryReviewStore.Lexicon)
            || facts.RequestDigest is not { Length: ErasureDigestBytes }
            || facts.EffectDigest is not { Length: ErasureDigestBytes }
            || facts.ContentBinding is not (null or { Length: ErasureDigestBytes })
            || (facts.ContentBinding is not null && facts.Store is not MemoryReviewStore.Saga)
            || facts.DatasetGeneration == Guid.Empty)
        {
            return InvalidFacts;
        }

        return IssueErasure(TokenPurpose.ErasurePlan, ErasurePlanPayloadBytes, payload =>
        {
            int offset = 0;

            payload[offset++] = (byte)facts.Store;

            WriteBytes(payload, ref offset, facts.RequestDigest);

            WriteBytes(payload, ref offset, facts.EffectDigest);

            payload[offset++] = facts.ContentBinding is null ? (byte)0 : (byte)1;

            if (facts.ContentBinding is { } binding)
            {
                binding.CopyTo(payload[offset..]);
            }

            offset += ErasureDigestBytes;

            payload[offset++] = facts.DatasetGeneration is null ? (byte)0 : (byte)1;

            if (facts.DatasetGeneration is { } generation
                && (!generation.TryWriteBytes(payload[offset..], bigEndian: true, out int written) || written != 16))
            {
                throw new InvalidOperationException("The dataset generation could not be encoded.");
            }
        });
    }

    public Result<MemoryErasurePlanTokenFacts> ReadErasurePlan(string token)
    {
        Result<byte[]> read = Read(token, TokenPurpose.ErasurePlan, ErasurePlanPayloadBytes);

        if (read.IsFailure)
        {
            return InvalidPreflight;
        }

        ReadOnlySpan<byte> payload = read.Value;

        int offset = 0;

        MemoryReviewStore store = (MemoryReviewStore)payload[offset++];

        byte[] requestDigest = ReadBytes(payload, ref offset, ErasureDigestBytes);

        byte[] effectDigest = ReadBytes(payload, ref offset, ErasureDigestBytes);

        byte hasBinding = payload[offset++];

        byte[] binding = ReadBytes(payload, ref offset, ErasureDigestBytes);

        byte hasGeneration = payload[offset++];

        ReadOnlySpan<byte> generationBytes = payload.Slice(offset, 16);

        Guid generation = new(generationBytes, bigEndian: true);

        // A presence byte is 0 or 1, and an absent value is all zero, so each fact has exactly one
        // spelling and a token cannot smuggle bytes the reader would ignore.
        bool valid = store is MemoryReviewStore.Saga or MemoryReviewStore.Lexicon
            && hasBinding is 0 or 1
            && hasGeneration is 0 or 1
            && (hasBinding == 1 || binding.AsSpan().IndexOfAnyExcept((byte)0) < 0)
            && (hasBinding == 0 || store is MemoryReviewStore.Saga)
            && (hasGeneration == 1 ? generation != Guid.Empty : generationBytes.IndexOfAnyExcept((byte)0) < 0);

        return valid
            ? new MemoryErasurePlanTokenFacts(
                store,
                requestDigest,
                effectDigest,
                hasBinding == 1 ? binding : null,
                hasGeneration == 1 ? generation : null)
            : InvalidPreflight;
    }

    public Result<MemoryErasureIssuedToken> IssueErasureKeyReset(MemoryErasureKeyResetTokenFacts facts)
    {
        if (facts is null
            || !ValidKeyStatus(facts.KeyStatus)
            || facts.UnverifiableCovenantFingerprints < 0
            || facts.UnverifiableSagaFingerprints < 0
            || facts.UnverifiableLexiconFingerprints < 0
            || facts.UnverifiableCovenantReceipts < 0
            || facts.UnverifiableSagaReceipts < 0
            || facts.UnverifiableLexiconReceipts < 0)
        {
            return InvalidFacts;
        }

        return IssueErasure(TokenPurpose.ErasureKeyReset, ErasureKeyResetPayloadBytes, payload =>
        {
            int offset = 0;

            payload[offset++] = (byte)facts.KeyStatus;

            foreach (long count in (long[])
                [
                    facts.UnverifiableCovenantFingerprints,
                    facts.UnverifiableSagaFingerprints,
                    facts.UnverifiableLexiconFingerprints,
                    facts.UnverifiableCovenantReceipts,
                    facts.UnverifiableSagaReceipts,
                    facts.UnverifiableLexiconReceipts,
                ])
            {
                WriteUInt64(payload, ref offset, (ulong)count);
            }
        });
    }

    public Result<MemoryErasureKeyResetTokenFacts> ReadErasureKeyReset(string token)
    {
        Result<byte[]> read = Read(token, TokenPurpose.ErasureKeyReset, ErasureKeyResetPayloadBytes);

        if (read.IsFailure)
        {
            return InvalidPreflight;
        }

        ReadOnlySpan<byte> payload = read.Value;

        int offset = 0;

        MemoryErasureKeyStatus status = (MemoryErasureKeyStatus)payload[offset++];

        long[] counts = new long[6];

        for (int index = 0; index < counts.Length; index++)
        {
            ulong count = ReadUInt64(payload, ref offset);

            if (count > long.MaxValue)
            {
                return InvalidPreflight;
            }

            counts[index] = (long)count;
        }

        return ValidKeyStatus(status)
            ? new MemoryErasureKeyResetTokenFacts(status, counts[0], counts[1], counts[2], counts[3], counts[4], counts[5])
            : InvalidPreflight;
    }

    private static bool ValidKeyStatus(MemoryErasureKeyStatus status) =>
        status is MemoryErasureKeyStatus.Absent
            or MemoryErasureKeyStatus.Present
            or MemoryErasureKeyStatus.Unavailable
            or MemoryErasureKeyStatus.Lost;

    /// <summary>
    /// Issues an erasure token and the wall-clock times that describe it, both from this codec's clock.
    /// </summary>
    private Result<MemoryErasureIssuedToken> IssueErasure(TokenPurpose purpose, int payloadBytes, SpanWriter writePayload)
    {
        DateTimeOffset issuedAt = _timeProvider.GetUtcNow();

        Result<string> issued = Issue(purpose, payloadBytes, writePayload);

        return issued.IsFailure
            ? issued.Error
            : new MemoryErasureIssuedToken(issued.Value, issuedAt, issuedAt + MemoryReviewLimits.TokenLifetime);
    }

    private Result<string> Issue(TokenPurpose purpose, int payloadBytes, SpanWriter writePayload)
    {
        int unsignedBytes = HeaderBytes + payloadBytes;

        byte[] token = new byte[unsignedBytes + TagBytes];

        Span<byte> unsigned = token.AsSpan(0, unsignedBytes);

        unsigned[0] = FormatVersion;

        unsigned[1] = (byte)purpose;

        long issuedAt = _timeProvider.GetTimestamp();

        long expiresAt = checked(issuedAt + LifetimeTimestampUnits());

        BinaryPrimitives.WriteInt64BigEndian(unsigned[2..10], issuedAt);

        BinaryPrimitives.WriteInt64BigEndian(unsigned[10..18], expiresAt);

        writePayload(unsigned[HeaderBytes..]);

        _ = HMACSHA256.HashData(ProcessKey, unsigned, token.AsSpan(unsignedBytes, TagBytes));

        string encoded = Base64Url.EncodeToString(token);

        return encoded.Length <= MemoryReviewLimits.MaxTokenCharacters ? encoded : InvalidFacts;
    }

    private Result<byte[]> Read(string token, TokenPurpose purpose, int payloadBytes)
    {
        int expectedBytes = HeaderBytes + payloadBytes + TagBytes;

        if (string.IsNullOrEmpty(token) || token.Length > MemoryReviewLimits.MaxTokenCharacters)
        {
            return InvalidToken;
        }

        // The token comes from whoever calls the route, so every way it can be malformed, from a length
        // no decoder can read to a respelling of a signed token, is the same refusal and never an
        // exception.
        if (!CanonicalBase64Url.TryDecodeExact(token, expectedBytes, out byte[] decoded))
        {
            return InvalidToken;
        }

        ReadOnlySpan<byte> complete = decoded;

        ReadOnlySpan<byte> unsigned = complete[..^TagBytes];

        Span<byte> expectedTag = stackalloc byte[TagBytes];

        _ = HMACSHA256.HashData(ProcessKey, unsigned, expectedTag);

        bool authenticated = CryptographicOperations.FixedTimeEquals(expectedTag, complete[^TagBytes..]);

        CryptographicOperations.ZeroMemory(expectedTag);

        if (!authenticated)
        {
            return InvalidToken;
        }

        long issuedAt = BinaryPrimitives.ReadInt64BigEndian(unsigned[2..10]);

        long expiresAt = BinaryPrimitives.ReadInt64BigEndian(unsigned[10..18]);

        long now = _timeProvider.GetTimestamp();

        if (unsigned[0] != FormatVersion
            || unsigned[1] != (byte)purpose
            || expiresAt - issuedAt != LifetimeTimestampUnits()
            || issuedAt > now
            || now >= expiresAt)
        {
            return InvalidToken;
        }

        return unsigned[HeaderBytes..].ToArray();
    }

    private static int WriteCommon(
        Span<byte> payload,
        MemoryReviewStore store,
        MemoryReviewDigest scopeDigest,
        Guid markerGeneration,
        ulong markerRevision,
        ulong lowerFrontier,
        ulong upperFrontier)
    {
        int offset = 0;

        payload[offset++] = (byte)store;

        WriteDigest(payload, ref offset, scopeDigest);

        if (!markerGeneration.TryWriteBytes(payload[offset..], bigEndian: true, out int written) || written != 16)
        {
            throw new InvalidOperationException("The marker generation could not be encoded.");
        }

        offset += written;

        WriteUInt64(payload, ref offset, markerRevision);

        WriteUInt64(payload, ref offset, lowerFrontier);

        WriteUInt64(payload, ref offset, upperFrontier);

        return offset;
    }

    private static CommonFacts ReadCommon(ReadOnlySpan<byte> payload, ref int offset)
    {
        MemoryReviewStore store = (MemoryReviewStore)payload[offset++];

        MemoryReviewDigest scopeDigest = ReadDigest(payload, ref offset);

        Guid markerGeneration = new(payload.Slice(offset, 16), bigEndian: true);

        offset += 16;

        return new CommonFacts(
            store,
            scopeDigest,
            markerGeneration,
            ReadUInt64(payload, ref offset),
            ReadUInt64(payload, ref offset),
            ReadUInt64(payload, ref offset));
    }

    private static void WriteDigest(Span<byte> destination, ref int offset, MemoryReviewDigest digest)
    {
        digest.CopyTo(destination[offset..]);

        offset += DigestBytes;
    }

    private static MemoryReviewDigest ReadDigest(ReadOnlySpan<byte> source, ref int offset)
    {
        MemoryReviewDigest digest = new(source.Slice(offset, DigestBytes).ToArray());

        offset += DigestBytes;

        return digest;
    }

    private static void WriteBytes(Span<byte> destination, ref int offset, byte[] value)
    {
        value.CopyTo(destination[offset..]);

        offset += value.Length;
    }

    private static byte[] ReadBytes(ReadOnlySpan<byte> source, ref int offset, int length)
    {
        byte[] value = source.Slice(offset, length).ToArray();

        offset += length;

        return value;
    }

    private static void WriteUInt64(Span<byte> destination, ref int offset, ulong value)
    {
        BinaryPrimitives.WriteUInt64BigEndian(destination[offset..], value);

        offset += sizeof(ulong);
    }

    private static ulong ReadUInt64(ReadOnlySpan<byte> source, ref int offset)
    {
        ulong value = BinaryPrimitives.ReadUInt64BigEndian(source[offset..]);

        offset += sizeof(ulong);

        return value;
    }

    private static bool ValidCommon(CommonFacts facts) =>
        ValidCommon(facts.Store, facts.ScopeDigest, facts.MarkerGeneration, facts.LowerFrontier, facts.UpperFrontier);

    private static bool ValidCommon(
        MemoryReviewStore store,
        MemoryReviewDigest scopeDigest,
        Guid markerGeneration,
        ulong lowerFrontier,
        ulong upperFrontier) =>
        store is >= MemoryReviewStore.Covenant and <= MemoryReviewStore.Lexicon
        && scopeDigest.IsValid
        && markerGeneration != Guid.Empty
        && lowerFrontier <= upperFrontier;

    private static bool WithinFrontier(ulong sequence, ulong lower, ulong upper) =>
        sequence >= lower && sequence <= upper;

    private long LifetimeTimestampUnits() => checked(
        MemoryReviewLimits.TokenLifetime.Ticks
        / TimeSpan.TicksPerSecond
        * _timeProvider.TimestampFrequency);

    private delegate void SpanWriter(Span<byte> payload);

    private enum TokenPurpose : byte
    {
        Cursor = 1,

        Observation = 2,

        PreparedPlan = 3,

        ErasurePlan = 4,

        ErasureKeyReset = 5,
    }

    private readonly record struct CommonFacts(
        MemoryReviewStore Store,
        MemoryReviewDigest ScopeDigest,
        Guid MarkerGeneration,
        ulong MarkerRevision,
        ulong LowerFrontier,
        ulong UpperFrontier);
}
