using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;

using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Memory;

internal sealed class MemoryReviewTokenCodec(TimeProvider timeProvider) : IMemoryReviewTokenCodec
{
    private const byte FormatVersion = 1;

    private const int HeaderBytes = 18;

    private const int CommonFactsBytes = 73;

    private const int DigestBytes = MemoryReviewDigest.Size;

    private const int TagBytes = 32;

    private const int CursorPayloadBytes = CommonFactsBytes + sizeof(ulong) + DigestBytes;

    private const int ObservationPayloadBytes = CommonFactsBytes + sizeof(ulong) + DigestBytes;

    private const int PreparedPlanPayloadBytes = CommonFactsBytes + DigestBytes;

    // One unexported key for this process lifetime. A restart deliberately invalidates every token,
    // while independently constructed codec instances in the same host remain interoperable.
    private static readonly byte[] ProcessKey = RandomNumberGenerator.GetBytes(32);

    private static readonly Error InvalidToken =
        new(ErrorCodes.MemoryReview.InvalidToken, "The memory-review token is invalid, expired, or has the wrong purpose.");

    private static readonly Error InvalidFacts =
        new(ErrorCodes.MemoryReview.InvalidTokenFacts, "The memory-review token facts are incomplete or inconsistent.");

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

        if (string.IsNullOrEmpty(token)
            || token.Length > MemoryReviewLimits.MaxTokenCharacters
            || token.AsSpan().IndexOfAnyExcept(
                "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_".AsSpan()) >= 0)
        {
            return InvalidToken;
        }

        byte[] decoded = new byte[Base64Url.GetMaxDecodedLength(token.Length)];

        int written;

        try
        {
            if (!Base64Url.TryDecodeFromChars(token, decoded, out written))
            {
                return InvalidToken;
            }
        }
        catch (FormatException)
        {
            return InvalidToken;
        }

        if (written != expectedBytes)
        {
            return InvalidToken;
        }

        ReadOnlySpan<byte> complete = decoded.AsSpan(0, written);

        if (!string.Equals(Base64Url.EncodeToString(complete), token, StringComparison.Ordinal))
        {
            return InvalidToken;
        }

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
    }

    private readonly record struct CommonFacts(
        MemoryReviewStore Store,
        MemoryReviewDigest ScopeDigest,
        Guid MarkerGeneration,
        ulong MarkerRevision,
        ulong LowerFrontier,
        ulong UpperFrontier);
}
