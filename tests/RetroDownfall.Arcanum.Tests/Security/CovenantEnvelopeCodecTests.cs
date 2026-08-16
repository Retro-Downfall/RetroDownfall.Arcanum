using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Tests.Support;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

/// <summary>
/// The six-purpose envelope framing: exact codes, exact bounds, and one content-free refusal.
/// </summary>
public sealed class CovenantEnvelopeCodecTests
{

    private static readonly Guid Installation = Guid.Parse("2C4A5E3B-9F17-4D0C-8A6E-1B3D5F70921A");

    private static readonly Guid Dataset = Guid.Parse("0D1E2F30-4152-4637-8899-AABBCCDDEEFF");

    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(CovenantEnvelopePurpose.Cursor, 1, "Arcanum.Covenant.Cursor.v1")]
    [InlineData(CovenantEnvelopePurpose.OperatorPreflight, 2, "Arcanum.Covenant.OperatorPreflight.v1")]
    [InlineData(CovenantEnvelopePurpose.WardRetirement, 3, "Arcanum.Covenant.WardRetirement.v1")]
    [InlineData(CovenantEnvelopePurpose.FamilyReinitialize, 4, "Arcanum.Covenant.FamilyReinitialize.v1")]
    [InlineData(CovenantEnvelopePurpose.CampaignPathIdentity, 5, "Arcanum.Campaign.PathIdentity.v1")]
    [InlineData(CovenantEnvelopePurpose.SessionCampaignBinding, 6, "Arcanum.Session.CampaignBinding.v1")]
    public void Purpose_codes_and_labels_are_immutable(
        CovenantEnvelopePurpose purpose,
        byte code,
        string label)
    {

        Assert.Equal(code, (byte)purpose);
        Assert.Equal(label, CovenantEnvelopeLimits.Label(purpose));

    }

    [Fact]
    public void Purpose_set_is_closed_and_splits_dataset_from_recovery_keying()
    {

        Assert.Equal(6, Enum.GetValues<CovenantEnvelopePurpose>().Length);

        Assert.True(CovenantEnvelopeLimits.IsDatasetKeyed(CovenantEnvelopePurpose.Cursor));
        Assert.True(CovenantEnvelopeLimits.IsDatasetKeyed(CovenantEnvelopePurpose.OperatorPreflight));
        Assert.True(CovenantEnvelopeLimits.IsDatasetKeyed(CovenantEnvelopePurpose.WardRetirement));

        Assert.False(CovenantEnvelopeLimits.IsDatasetKeyed(CovenantEnvelopePurpose.FamilyReinitialize));
        Assert.False(CovenantEnvelopeLimits.IsDatasetKeyed(CovenantEnvelopePurpose.CampaignPathIdentity));
        Assert.False(CovenantEnvelopeLimits.IsDatasetKeyed(CovenantEnvelopePurpose.SessionCampaignBinding));

    }

    [Theory]
    [InlineData(CovenantEnvelopePurpose.Cursor)]
    [InlineData(CovenantEnvelopePurpose.OperatorPreflight)]
    [InlineData(CovenantEnvelopePurpose.WardRetirement)]
    [InlineData(CovenantEnvelopePurpose.FamilyReinitialize)]
    [InlineData(CovenantEnvelopePurpose.CampaignPathIdentity)]
    [InlineData(CovenantEnvelopePurpose.SessionCampaignBinding)]
    public void Round_trip_preserves_the_exact_payload_for_every_purpose(CovenantEnvelopePurpose purpose)
    {

        using CodecHarness harness = CodecHarness.Create();

        byte[] payload = [0x00, 0x01, 0xFE, 0xFF, 0x7F, 0x80];

        Result<string> encoded = harness.Codec.Encode(purpose, payload, TimeSpan.FromMinutes(5));

        Assert.True(encoded.IsSuccess);

        Result<CovenantEnvelopeBody> decoded = harness.Codec.Decode(purpose, encoded.Value);

        Assert.True(decoded.IsSuccess);
        Assert.Equal(purpose, decoded.Value.Purpose);
        Assert.Equal(payload, decoded.Value.Payload);
        Assert.Equal(Now, decoded.Value.IssuedAtUtc);
        Assert.Equal(Now.AddMinutes(5), decoded.Value.ExpiresAtUtc);

    }

    [Fact]
    public void Wire_layout_is_the_exact_forty_six_byte_header_plus_ciphertext_and_tag()
    {

        using CodecHarness harness = CodecHarness.Create();

        byte[] payload = Encoding.ASCII.GetBytes("cursor-state");

        string token = harness.Codec.Encode(
            CovenantEnvelopePurpose.Cursor,
            payload,
            TimeSpan.FromMinutes(1)).Value;

        Assert.DoesNotContain('=', token);

        byte[] wire = Base64Url.DecodeFromChars(token);

        Assert.Equal(
            CovenantEnvelopeLimits.HeaderBytes
            + CovenantEnvelopeLimits.BodyTimeBytes
            + payload.Length
            + CovenantEnvelopeLimits.TagBytes,
            wire.Length);

        Assert.Equal("ACVE", Encoding.ASCII.GetString(wire.AsSpan(0, 4)));
        Assert.Equal(CovenantEnvelopeLimits.Version, wire[4]);
        Assert.Equal((byte)CovenantEnvelopePurpose.Cursor, wire[5]);
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32BigEndian(wire.AsSpan(6)));
        Assert.Equal(3L, BinaryPrimitives.ReadInt64BigEndian(wire.AsSpan(10)));
        Assert.Equal(1ul, BinaryPrimitives.ReadUInt64BigEndian(wire.AsSpan(18)));
        Assert.Equal(Now.ToUnixTimeMilliseconds(), BinaryPrimitives.ReadInt64BigEndian(wire.AsSpan(26)));
        Assert.Equal(
            Now.AddMinutes(1).ToUnixTimeMilliseconds(),
            BinaryPrimitives.ReadInt64BigEndian(wire.AsSpan(34)));
        Assert.Equal(
            (uint)(CovenantEnvelopeLimits.BodyTimeBytes + payload.Length),
            BinaryPrimitives.ReadUInt32BigEndian(wire.AsSpan(42)));

    }

    [Fact]
    public void Counter_advances_per_purpose_and_never_repeats_a_nonce()
    {

        using CodecHarness harness = CodecHarness.Create();

        ulong first = Counter(harness.Codec.Encode(CovenantEnvelopePurpose.Cursor, [1], TimeSpan.FromMinutes(1)).Value);

        ulong second = Counter(harness.Codec.Encode(CovenantEnvelopePurpose.Cursor, [1], TimeSpan.FromMinutes(1)).Value);

        ulong otherPurpose = Counter(
            harness.Codec.Encode(CovenantEnvelopePurpose.WardRetirement, [1], TimeSpan.FromMinutes(1)).Value);

        Assert.Equal(1ul, first);
        Assert.Equal(2ul, second);
        Assert.Equal(1ul, otherPurpose);

    }

    [Fact]
    public void Cross_purpose_presentation_is_refused()
    {

        using CodecHarness harness = CodecHarness.Create();

        string cursor = harness.Codec.Encode(
            CovenantEnvelopePurpose.Cursor,
            [7],
            TimeSpan.FromMinutes(1)).Value;

        Result<CovenantEnvelopeBody> decoded =
            harness.Codec.Decode(CovenantEnvelopePurpose.OperatorPreflight, cursor);

        Assert.False(decoded.IsSuccess);
        Assert.Equal(ErrorCodes.Covenant.InvalidCursor, decoded.Error.Code);

    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(20)]
    [InlineData(45)]
    [InlineData(50)]
    public void Tampering_with_any_authenticated_byte_is_refused(int index)
    {

        using CodecHarness harness = CodecHarness.Create();

        string token = harness.Codec.Encode(
            CovenantEnvelopePurpose.Cursor,
            [1, 2, 3, 4, 5, 6, 7, 8],
            TimeSpan.FromMinutes(1)).Value;

        byte[] wire = Base64Url.DecodeFromChars(token);

        wire[index] ^= 0x01;

        Result<CovenantEnvelopeBody> decoded =
            harness.Codec.Decode(CovenantEnvelopePurpose.Cursor, Base64Url.EncodeToString(wire));

        Assert.False(decoded.IsSuccess);
        Assert.Equal(ErrorCodes.Covenant.InvalidCursor, decoded.Error.Code);

    }

    [Fact]
    public void Body_times_must_equal_the_authenticated_header_times()
    {

        using CodecHarness harness = CodecHarness.Create();

        // Forge a token whose plaintext times disagree with its header. The forger has the key, which
        // is the strongest attacker this check is meant to catch: an internal caller assembling a body
        // against one deadline while framing a header against another.
        byte[] payload = [9, 9, 9];

        Span<byte> header = stackalloc byte[CovenantEnvelopeLimits.HeaderBytes];

        long issuedMs = Now.ToUnixTimeMilliseconds();

        long headerExpiresMs = Now.AddMinutes(1).ToUnixTimeMilliseconds();

        long bodyExpiresMs = Now.AddHours(24).ToUnixTimeMilliseconds();

        Encoding.ASCII.GetBytes("ACVE").CopyTo(header);

        header[4] = CovenantEnvelopeLimits.Version;

        header[5] = (byte)CovenantEnvelopePurpose.Cursor;

        BinaryPrimitives.WriteUInt32BigEndian(header[6..], 7);

        BinaryPrimitives.WriteInt64BigEndian(header[10..], 3);

        BinaryPrimitives.WriteUInt64BigEndian(header[18..], 99);

        BinaryPrimitives.WriteInt64BigEndian(header[26..], issuedMs);

        BinaryPrimitives.WriteInt64BigEndian(header[34..], headerExpiresMs);

        BinaryPrimitives.WriteUInt32BigEndian(
            header[42..],
            (uint)(CovenantEnvelopeLimits.BodyTimeBytes + payload.Length));

        Span<byte> plaintext = stackalloc byte[CovenantEnvelopeLimits.BodyTimeBytes + payload.Length];

        BinaryPrimitives.WriteInt64BigEndian(plaintext, issuedMs);

        BinaryPrimitives.WriteInt64BigEndian(plaintext[8..], bodyExpiresMs);

        payload.CopyTo(plaintext[CovenantEnvelopeLimits.BodyTimeBytes..]);

        Span<byte> nonce = stackalloc byte[CovenantEnvelopeLimits.NonceBytes];

        BinaryPrimitives.WriteUInt32BigEndian(nonce, (uint)CovenantEnvelopePurpose.Cursor);

        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], 99);

        Span<byte> wire = stackalloc byte[
            CovenantEnvelopeLimits.HeaderBytes + plaintext.Length + CovenantEnvelopeLimits.TagBytes];

        header.CopyTo(wire);

        using AesGcm aes = new(harness.CursorKey, CovenantEnvelopeLimits.TagBytes);

        aes.Encrypt(
            nonce,
            plaintext,
            wire.Slice(CovenantEnvelopeLimits.HeaderBytes, plaintext.Length),
            wire[(CovenantEnvelopeLimits.HeaderBytes + plaintext.Length)..],
            header);

        Result<CovenantEnvelopeBody> decoded =
            harness.Codec.Decode(CovenantEnvelopePurpose.Cursor, Base64Url.EncodeToString(wire));

        Assert.False(decoded.IsSuccess);
        Assert.Equal(ErrorCodes.Covenant.InvalidCursor, decoded.Error.Code);

    }

    [Fact]
    public void An_elapsed_lifetime_is_reported_separately_from_an_invalid_token()
    {

        using CodecHarness harness = CodecHarness.Create();

        string token = harness.Codec.Encode(
            CovenantEnvelopePurpose.OperatorPreflight,
            [4],
            TimeSpan.FromMinutes(5)).Value;

        harness.Time.Advance(TimeSpan.FromMinutes(5));

        Result<CovenantEnvelopeBody> decoded =
            harness.Codec.Decode(CovenantEnvelopePurpose.OperatorPreflight, token);

        Assert.False(decoded.IsSuccess);
        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, decoded.Error.Code);

    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64url!")]
    [InlineData("AAAA=")]
    [InlineData("A")]
    [InlineData("QUNWRQ")]
    public void Malformed_input_is_refused_before_any_cryptography(string? token)
    {

        using CodecHarness harness = CodecHarness.Create();

        Result<CovenantEnvelopeBody> decoded = harness.Codec.Decode(CovenantEnvelopePurpose.Cursor, token);

        Assert.False(decoded.IsSuccess);
        Assert.Equal(ErrorCodes.Covenant.InvalidCursor, decoded.Error.Code);

    }

    [Fact]
    public void An_oversized_token_is_refused_without_allocating_from_its_length()
    {

        using CodecHarness harness = CodecHarness.Create();

        Result<CovenantEnvelopeBody> decoded = harness.Codec.Decode(
            CovenantEnvelopePurpose.Cursor,
            new string('A', CovenantEnvelopeLimits.MaxTokenCharacters + 1));

        Assert.False(decoded.IsSuccess);
        Assert.Equal(ErrorCodes.Covenant.InvalidCursor, decoded.Error.Code);

    }

    [Fact]
    public void An_oversized_payload_or_lifetime_is_refused_at_issuance()
    {

        using CodecHarness harness = CodecHarness.Create();

        Result<string> tooLarge = harness.Codec.Encode(
            CovenantEnvelopePurpose.Cursor,
            new byte[CovenantEnvelopeLimits.MaxPayloadBytes + 1],
            TimeSpan.FromMinutes(1));

        Assert.False(tooLarge.IsSuccess);
        Assert.Equal(ErrorCodes.Covenant.CapacityExceeded, tooLarge.Error.Code);

        Result<string> atLimit = harness.Codec.Encode(
            CovenantEnvelopePurpose.Cursor,
            new byte[CovenantEnvelopeLimits.MaxPayloadBytes],
            TimeSpan.FromMinutes(1));

        Assert.True(atLimit.IsSuccess);

        Result<CovenantEnvelopeBody> decodedAtLimit =
            harness.Codec.Decode(CovenantEnvelopePurpose.Cursor, atLimit.Value);

        Assert.True(decodedAtLimit.IsSuccess);
        Assert.Equal(CovenantEnvelopeLimits.MaxPayloadBytes, decodedAtLimit.Value.Payload.Length);

        foreach (TimeSpan lifetime in new[]
        {
            TimeSpan.Zero,
            TimeSpan.FromSeconds(-1),
            CovenantEnvelopeLimits.MaxLifetime + TimeSpan.FromSeconds(1),
        })
        {
            Assert.False(harness.Codec.Encode(CovenantEnvelopePurpose.Cursor, [1], lifetime).IsSuccess);
        }

    }

    [Fact]
    public void A_dataset_keyed_purpose_has_no_key_when_no_dataset_exists()
    {

        using CodecHarness harness = CodecHarness.CreateWithoutDataset();

        Result<string> cursor = harness.Codec.Encode(
            CovenantEnvelopePurpose.Cursor,
            [1],
            TimeSpan.FromMinutes(1));

        Assert.False(cursor.IsSuccess);
        Assert.Equal(ErrorCodes.Covenant.Unavailable, cursor.Error.Code);

        // The recovery families are keyed by installation identity, so they still work. That is the
        // whole point of the split: an operator has to be able to repair a dataset that is not there.
        Assert.True(
            harness.Codec.Encode(
                CovenantEnvelopePurpose.FamilyReinitialize,
                [1],
                TimeSpan.FromMinutes(1)).IsSuccess);

    }

    [Fact]
    public void Encoding_before_initialization_fails_closed()
    {

        using CovenantEnvelopeMasterKeyProvider keys = new();

        CovenantEnvelopeCodec codec = new(keys, FakeClock(Now));

        Result<string> encoded = codec.Encode(CovenantEnvelopePurpose.Cursor, [1], TimeSpan.FromMinutes(1));

        Assert.False(encoded.IsSuccess);
        Assert.Equal(ErrorCodes.Covenant.OperatorAuthorityUnavailable, encoded.Error.Code);

    }

    private static ulong Counter(string token) =>
        BinaryPrimitives.ReadUInt64BigEndian(Base64Url.DecodeFromChars(token).AsSpan(18));

    private sealed class CodecHarness : IDisposable
    {

        private CodecHarness(
            CovenantEnvelopeMasterKeyProvider keys,
            CovenantEnvelopeCodec codec,
            FakeTimeProvider time,
            byte[] cursorKey)
        {

            Keys = keys;

            Codec = codec;

            Time = time;

            CursorKey = cursorKey;

        }

        public CovenantEnvelopeMasterKeyProvider Keys { get; }

        public CovenantEnvelopeCodec Codec { get; }

        public FakeTimeProvider Time { get; }

        /// <summary>The cursor purpose key, copied out so a forgery test can frame its own token.</summary>
        public byte[] CursorKey { get; }

        public static CodecHarness Create() => Build(Dataset);

        public static CodecHarness CreateWithoutDataset() => Build(dataset: null);

        private static CodecHarness Build(Guid? dataset)
        {

            CovenantEnvelopeMasterKeyProvider keys = new();

            _ = keys.Initialize(
                Encoding.UTF8.GetBytes("master-key-material"),
                Transition(dataset, masterKeyVersion: 7));

            byte[] cursorKey = keys.Current!.PurposeKey(CovenantEnvelopePurpose.Cursor).ToArray();

            FakeTimeProvider time = FakeClock(Now);

            return new CodecHarness(keys, new CovenantEnvelopeCodec(keys, time), time, cursorKey);

        }

        public void Dispose() => Keys.Dispose();

    }

    private static CovenantCommittedAuthorityTransition Transition(Guid? dataset, uint masterKeyVersion) =>
        new(
            Installation.ToString().ToUpperInvariant(),
            authorityEpoch: 11,
            masterKeyVersion: masterKeyVersion,
            canonicalEnvelopeEpoch: 3,
            recoveryEnvelopeEpoch: 2,
            capabilityGeneration: 1,
            datasetGeneration: dataset,
            covenantEnabled: true);


    /// <summary>A fixed clock, so envelope timestamps and expiry are exact rather than approximate.</summary>
    private static FakeTimeProvider FakeClock(DateTimeOffset now)
    {

        FakeTimeProvider provider = new();

        provider.SetUtcNow(now);

        return provider;

    }

}
