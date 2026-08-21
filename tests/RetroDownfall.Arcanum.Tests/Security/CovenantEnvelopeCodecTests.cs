using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Tests.Support;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

/// <summary>
/// The six-purpose envelope framing: exact codes, exact bounds, and one content-free refusal.
/// </summary>
public sealed class CovenantEnvelopeCodecTests
{

    private static readonly Guid Installation = Guid.Parse("2C4A5E3B-9F17-4D0C-8A6E-1B3D5F70921A");

    private static readonly Guid Dataset = Guid.Parse("0D1E2F30-4152-4637-8899-AABBCCDDEEFF");

    private static readonly Guid NextDataset = Guid.Parse("10213243-5465-4768-899A-ABBCCDDEEF01");

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

    [Fact]
    public async Task Encoding_returns_stale_and_zeroizes_temporaries_when_retired_after_key_copy()
    {

        using BlockingCodecCheckpoint checkpoint = new(CovenantEnvelopeCodecStep.PurposeKeyCopied);

        using CodecHarness harness = CodecHarness.Create(checkpoint);

        Task<Result<string>> encoding = Task.Run(
            () => harness.Codec.Encode(
                CovenantEnvelopePurpose.Cursor,
                [1, 2, 3],
                TimeSpan.FromMinutes(5)));

        checkpoint.WaitUntilReached();

        try
        {
            harness.Retire();
        }
        finally
        {
            checkpoint.Release();
        }

        Result<string> encoded = await encoding;

        Assert.False(encoded.IsSuccess);
        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, encoded.Error.Code);
        checkpoint.AssertZeroized(CovenantEnvelopeCodecBufferKind.Key, expectedCount: 1);
        checkpoint.AssertZeroized(CovenantEnvelopeCodecBufferKind.Plaintext, expectedCount: 1);
        checkpoint.AssertZeroized(CovenantEnvelopeCodecBufferKind.Nonce, expectedCount: 1);
        checkpoint.AssertZeroized(CovenantEnvelopeCodecBufferKind.Wire, expectedCount: 1);

    }

    [Fact]
    public async Task Encoding_returns_stale_without_a_token_when_a_generation_publishes_after_crypto()
    {

        using BlockingCodecCheckpoint checkpoint = new(
            CovenantEnvelopeCodecStep.BeforeGenerationRevalidation);

        using CodecHarness harness = CodecHarness.Create(checkpoint);

        Task<Result<string>> encoding = Task.Run(
            () => harness.Codec.Encode(
                CovenantEnvelopePurpose.Cursor,
                [4, 5, 6],
                TimeSpan.FromMinutes(5)));

        checkpoint.WaitUntilReached();

        CovenantCommittedAuthorityTransition transition = Transition(
            NextDataset,
            masterKeyVersion: 8);

        Result<CovenantPreparedEnvelopeKeyGeneration> prepared = harness.Keys.PrepareRekey(transition);

        Assert.True(prepared.IsSuccess);

        using CovenantPreparedEnvelopeKeyGeneration owned = prepared.Value;

        try
        {
            harness.PublishOwned(owned, transition);
        }
        finally
        {
            checkpoint.Release();
        }

        Result<string> encoded = await encoding;

        Assert.False(encoded.IsSuccess);
        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, encoded.Error.Code);
        checkpoint.AssertZeroized(CovenantEnvelopeCodecBufferKind.Key, expectedCount: 1);
        checkpoint.AssertZeroized(CovenantEnvelopeCodecBufferKind.Plaintext, expectedCount: 1);
        checkpoint.AssertZeroized(CovenantEnvelopeCodecBufferKind.Nonce, expectedCount: 1);
        checkpoint.AssertZeroized(CovenantEnvelopeCodecBufferKind.Wire, expectedCount: 1);

    }

    [Fact]
    public async Task Decoding_returns_stale_and_zeroizes_temporaries_when_retired_after_key_copy()
    {

        using CodecHarness harness = CodecHarness.Create();

        string token = harness.Codec.Encode(
            CovenantEnvelopePurpose.Cursor,
            [7, 8, 9],
            TimeSpan.FromMinutes(5)).Value;

        using BlockingCodecCheckpoint checkpoint = new(CovenantEnvelopeCodecStep.PurposeKeyCopied);

        CovenantEnvelopeCodec racingCodec = new(harness.Keys, harness.Time, checkpoint);

        Task<Result<CovenantEnvelopeBody>> decoding = Task.Run(
            () => racingCodec.Decode(CovenantEnvelopePurpose.Cursor, token));

        checkpoint.WaitUntilReached();

        try
        {
            harness.Retire();
        }
        finally
        {
            checkpoint.Release();
        }

        Result<CovenantEnvelopeBody> decoded = await decoding;

        Assert.False(decoded.IsSuccess);
        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, decoded.Error.Code);
        checkpoint.AssertZeroized(CovenantEnvelopeCodecBufferKind.Key, expectedCount: 1);
        checkpoint.AssertZeroized(CovenantEnvelopeCodecBufferKind.Plaintext, expectedCount: 1);
        checkpoint.AssertZeroized(CovenantEnvelopeCodecBufferKind.Nonce, expectedCount: 1);
        checkpoint.AssertZeroized(CovenantEnvelopeCodecBufferKind.Wire, expectedCount: 1);

    }

    [Fact]
    public async Task Decoding_returns_stale_without_a_payload_when_a_generation_publishes_after_crypto()
    {

        using CodecHarness harness = CodecHarness.Create();

        string token = harness.Codec.Encode(
            CovenantEnvelopePurpose.Cursor,
            [10, 11, 12],
            TimeSpan.FromMinutes(5)).Value;

        using BlockingCodecCheckpoint checkpoint = new(
            CovenantEnvelopeCodecStep.BeforeGenerationRevalidation);

        CovenantEnvelopeCodec racingCodec = new(harness.Keys, harness.Time, checkpoint);

        Task<Result<CovenantEnvelopeBody>> decoding = Task.Run(
            () => racingCodec.Decode(CovenantEnvelopePurpose.Cursor, token));

        checkpoint.WaitUntilReached();

        CovenantCommittedAuthorityTransition transition = Transition(
            NextDataset,
            masterKeyVersion: 8);

        Result<CovenantPreparedEnvelopeKeyGeneration> prepared = harness.Keys.PrepareRekey(transition);

        Assert.True(prepared.IsSuccess);

        using CovenantPreparedEnvelopeKeyGeneration owned = prepared.Value;

        try
        {
            harness.PublishOwned(owned, transition);
        }
        finally
        {
            checkpoint.Release();
        }

        Result<CovenantEnvelopeBody> decoded = await decoding;

        Assert.False(decoded.IsSuccess);
        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, decoded.Error.Code);
        checkpoint.AssertZeroized(CovenantEnvelopeCodecBufferKind.Key, expectedCount: 1);
        checkpoint.AssertZeroized(CovenantEnvelopeCodecBufferKind.Plaintext, expectedCount: 1);
        checkpoint.AssertZeroized(CovenantEnvelopeCodecBufferKind.Nonce, expectedCount: 1);
        checkpoint.AssertZeroized(CovenantEnvelopeCodecBufferKind.Wire, expectedCount: 1);

    }

    [Fact]
    public async Task Encoding_holds_publication_between_current_proof_and_token_materialization()
    {

        using BlockingCodecCheckpoint checkpoint = new(
            CovenantEnvelopeCodecStep.CurrentGenerationProven);

        using CodecHarness harness = CodecHarness.Create(checkpoint);

        CovenantCommittedAuthorityTransition transition = Transition(
            NextDataset,
            masterKeyVersion: 8);

        Result<CovenantPreparedEnvelopeKeyGeneration> prepared = harness.Keys.PrepareRekey(transition);

        Assert.True(prepared.IsSuccess);

        using CovenantPreparedEnvelopeKeyGeneration owned = prepared.Value;

        Task<Result<string>> encoding = Task.Run(
            () => harness.Codec.Encode(
                CovenantEnvelopePurpose.Cursor,
                [13, 14, 15],
                TimeSpan.FromMinutes(5)));

        checkpoint.WaitUntilReached();

        TaskCompletionSource publicationStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task publication = Task.Run(
            () =>
            {

                publicationStarted.SetResult();

                harness.PublishOwned(owned, transition);

            });

        await publicationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {

            Task completed = await Task.WhenAny(
                publication,
                Task.Delay(TimeSpan.FromMilliseconds(100)));

            Assert.NotSame(publication, completed);

        }
        finally
        {
            checkpoint.Release();
        }

        Result<string> encoded = await encoding;

        await publication;

        Assert.True(encoded.IsSuccess);
        Assert.NotEmpty(encoded.Value);

        CovenantEnvelopeCodec currentCodec = new(harness.Keys, harness.Time);

        Assert.False(currentCodec.Decode(CovenantEnvelopePurpose.Cursor, encoded.Value).IsSuccess);
        checkpoint.AssertZeroized(CovenantEnvelopeCodecBufferKind.Key, expectedCount: 1);
        checkpoint.AssertZeroized(CovenantEnvelopeCodecBufferKind.Plaintext, expectedCount: 1);
        checkpoint.AssertZeroized(CovenantEnvelopeCodecBufferKind.Nonce, expectedCount: 1);
        checkpoint.AssertZeroized(CovenantEnvelopeCodecBufferKind.Wire, expectedCount: 1);

    }

    [Fact]
    public async Task Decoding_holds_retirement_between_current_proof_and_payload_materialization()
    {

        using CodecHarness harness = CodecHarness.Create();

        byte[] payload = [16, 17, 18];

        string token = harness.Codec.Encode(
            CovenantEnvelopePurpose.Cursor,
            payload,
            TimeSpan.FromMinutes(5)).Value;

        using BlockingCodecCheckpoint checkpoint = new(
            CovenantEnvelopeCodecStep.CurrentGenerationProven);

        CovenantEnvelopeCodec racingCodec = new(harness.Keys, harness.Time, checkpoint);

        Task<Result<CovenantEnvelopeBody>> decoding = Task.Run(
            () => racingCodec.Decode(CovenantEnvelopePurpose.Cursor, token));

        checkpoint.WaitUntilReached();

        TaskCompletionSource retirementStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task retirement = Task.Run(
            () =>
            {

                retirementStarted.SetResult();

                harness.Retire();

            });

        await retirementStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {

            Task completed = await Task.WhenAny(
                retirement,
                Task.Delay(TimeSpan.FromMilliseconds(100)));

            Assert.NotSame(retirement, completed);

        }
        finally
        {
            checkpoint.Release();
        }

        Result<CovenantEnvelopeBody> decoded = await decoding;

        await retirement;

        Assert.True(decoded.IsSuccess);
        Assert.Equal(payload, decoded.Value.Payload);
        Assert.Null(harness.Keys.Current);
        checkpoint.AssertZeroized(CovenantEnvelopeCodecBufferKind.Key, expectedCount: 1);
        checkpoint.AssertZeroized(CovenantEnvelopeCodecBufferKind.Plaintext, expectedCount: 1);
        checkpoint.AssertZeroized(CovenantEnvelopeCodecBufferKind.Nonce, expectedCount: 1);
        checkpoint.AssertZeroized(CovenantEnvelopeCodecBufferKind.Wire, expectedCount: 1);

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

        public static CodecHarness Create(ICovenantEnvelopeCodecCheckpoint checkpoint) =>
            Build(Dataset, checkpoint);

        public static CodecHarness CreateWithoutDataset() => Build(dataset: null);

        private static CodecHarness Build(
            Guid? dataset,
            ICovenantEnvelopeCodecCheckpoint? checkpoint = null)
        {

            CovenantEnvelopeMasterKeyProvider keys = new();

            Result initialized = dataset is { } committedDataset
                ? CovenantEnvelopeRuntimeTestHarness.Initialize(
                    keys,
                    Encoding.UTF8.GetBytes("master-key-material"),
                    Transition(committedDataset, masterKeyVersion: 7))
                : CovenantEnvelopeRuntimeTestHarness.Initialize(
                    keys,
                    Encoding.UTF8.GetBytes("master-key-material"),
                    new CovenantEnvelopeBootstrapKeyInput(
                        Installation.ToString().ToUpperInvariant(),
                        masterKeyVersion: 7,
                        canonicalEnvelopeEpoch: 3,
                        recoveryEnvelopeEpoch: 2,
                        datasetGeneration: null));

            Assert.True(initialized.IsSuccess);

            byte[] cursorKey = new byte[32];

            CovenantEnvelopeKeyCopyStatus copyStatus = keys.TryCopyPurposeKey(
                CovenantEnvelopePurpose.Cursor,
                cursorKey,
                out _);

            if (dataset.HasValue)
            {
                Assert.Equal(CovenantEnvelopeKeyCopyStatus.Success, copyStatus);
            }
            else
            {
                Assert.Equal(CovenantEnvelopeKeyCopyStatus.PurposeUnavailable, copyStatus);

                CryptographicOperations.ZeroMemory(cursorKey);

                cursorKey = [];
            }

            FakeTimeProvider time = FakeClock(Now);

            CovenantEnvelopeCodec codec = checkpoint is null
                ? new CovenantEnvelopeCodec(keys, time)
                : new CovenantEnvelopeCodec(keys, time, checkpoint);

            return new CodecHarness(keys, codec, time, cursorKey);

        }

        internal void PublishOwned(
            CovenantPreparedEnvelopeKeyGeneration prepared,
            CovenantCommittedAuthorityTransition transition) =>
            CovenantEnvelopeRuntimeTestHarness.PublishOwned(Keys, prepared, transition);

        internal void Retire() => CovenantEnvelopeRuntimeTestHarness.Retire(Keys);

        public void Dispose()
        {

            CryptographicOperations.ZeroMemory(CursorKey);

            Keys.Dispose();

        }

    }

    private static CovenantCommittedAuthorityTransition Transition(Guid? dataset, uint masterKeyVersion) =>
        new(
            Installation.ToString().ToUpperInvariant(),
            authorityEpoch: 11,
            masterKeyVersion: masterKeyVersion,
            canonicalEnvelopeEpoch: 3,
            recoveryEnvelopeEpoch: 2,
            CovenantHostToolsState.Clean,
            transitionId: null,
            new CovenantCommittedCapabilityTransition(
                ExpectedGeneration: 1,
                Generation: 2,
                FeatureEnabled: true,
                CovenantCapabilityState.Healthy,
                CanonicalSchemaVersion: 1,
                CanonicalInstalledFingerprint: "sha256-canonical",
                CovenantCapabilityState.Healthy,
                AcceleratorSchemaVersion: 1,
                AcceleratorInstalledFingerprint: "sha256-accelerator",
                dataset ?? throw new ArgumentNullException(nameof(dataset)),
                CanonicalSequence: 0,
                CoreCampaignDeletionSequence: 0,
                CanonicalAppliedCampaignDeletionSequence: 0,
                CanonicalAppliedSessionDeletionSequence: 0,
                AppliedDatasetGeneration: null,
                AppliedSequence: null,
                AppliedCampaignDeletionSequence: null,
                AcceleratorEpoch: 1,
                CovenantFtsSynchronizationState.Dirty,
                RebuildRequired: true,
                CleanupAppliedCampaignSequence: 0,
                CleanupAppliedSessionSequence: 0,
                CleanupFullSweepRequired: false,
                CanonicalDiagnosticCode: null,
                AcceleratorDiagnosticCode: null));

    private sealed class BlockingCodecCheckpoint(
        CovenantEnvelopeCodecStep blockedStep) : ICovenantEnvelopeCodecCheckpoint, IDisposable
    {

        private readonly ManualResetEventSlim _reached = new();

        private readonly ManualResetEventSlim _release = new();

        private readonly List<(CovenantEnvelopeCodecBufferKind Kind, bool IsZero)> _zeroizations = [];

        public void Reached(CovenantEnvelopeCodecStep step)
        {

            if (step != blockedStep)
            {
                return;
            }

            _reached.Set();

            _release.Wait();

        }

        public void Zeroized(CovenantEnvelopeCodecBufferKind kind, bool isZero) =>
            _zeroizations.Add((kind, isZero));

        public void WaitUntilReached() => Assert.True(_reached.Wait(TimeSpan.FromSeconds(5)));

        public void Release() => _release.Set();

        public void AssertZeroized(CovenantEnvelopeCodecBufferKind kind, int expectedCount)
        {

            (CovenantEnvelopeCodecBufferKind Kind, bool IsZero)[] matching =
                [.. _zeroizations.Where(item => item.Kind == kind)];

            Assert.Equal(expectedCount, matching.Length);
            Assert.All(matching, static item => Assert.True(item.IsZero));

        }

        public void Dispose()
        {

            _reached.Dispose();

            _release.Dispose();

        }

    }


    /// <summary>A fixed clock, so envelope timestamps and expiry are exact rather than approximate.</summary>
    private static FakeTimeProvider FakeClock(DateTimeOffset now)
    {

        FakeTimeProvider provider = new();

        provider.SetUtcNow(now);

        return provider;

    }

}
