using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Memory;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Memory;

public sealed class MemoryReviewTokenCodecTests
{
    private static readonly Guid MarkerGeneration =
        Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static readonly MemoryReviewDigest ScopeDigest = Digest(0x10);

    [Fact]
    public void Review_bounds_are_fifty_and_tokens_are_short_lived_and_bounded()
    {
        Assert.Equal(50, MemoryReviewLimits.MaxPageSize);

        Assert.Equal(50, MemoryReviewLimits.MaxBulkOperations);

        Assert.Equal(256, MemoryReviewLimits.MaxTokenCharacters);

        Assert.Equal(TimeSpan.FromMinutes(5), MemoryReviewLimits.TokenLifetime);
    }

    [Fact]
    public void A_cursor_round_trip_binds_store_scope_marker_frontiers_and_keyset()
    {
        FakeTimeProvider time = FrozenTime();

        IMemoryReviewTokenCodec issuer = new MemoryReviewTokenCodec(time);

        IMemoryReviewTokenCodec reader = new MemoryReviewTokenCodec(time);

        MemoryReviewCursorTokenFacts facts = new(
            MemoryReviewStore.Covenant,
            ScopeDigest,
            MarkerGeneration,
            MarkerRevision: 7,
            FrozenLowerEventSequence: 100,
            FrozenUpperEventSequence: 900,
            KeysetEventSequence: 650,
            KeysetVersionIdentity: Digest(0x20));

        Result<string> encoded = issuer.IssueCursor(facts);

        Assert.True(encoded.IsSuccess, encoded.Error.Message);

        Assert.InRange(encoded.Value.Length, 1, MemoryReviewLimits.MaxTokenCharacters);

        Result<MemoryReviewCursorTokenFacts> decoded = reader.ReadCursor(encoded.Value);

        Assert.True(decoded.IsSuccess, decoded.Error.Message);

        Assert.Equal(facts, decoded.Value);
    }

    [Fact]
    public void An_observation_round_trip_binds_the_exact_event_and_version_identity()
    {
        FakeTimeProvider time = FrozenTime();

        IMemoryReviewTokenCodec codec = new MemoryReviewTokenCodec(time);

        MemoryReviewObservationTokenFacts facts = new(
            MemoryReviewStore.Saga,
            ScopeDigest,
            MarkerGeneration,
            MarkerRevision: 8,
            FrozenLowerEventSequence: 101,
            FrozenUpperEventSequence: 901,
            EventSequence: 444,
            VersionIdentity: Digest(0x30));

        Result<string> encoded = codec.IssueObservation(facts);

        Assert.True(encoded.IsSuccess, encoded.Error.Message);

        Result<MemoryReviewObservationTokenFacts> decoded = codec.ReadObservation(encoded.Value);

        Assert.True(decoded.IsSuccess, decoded.Error.Message);

        Assert.Equal(facts, decoded.Value);
    }

    [Fact]
    public void A_prepared_plan_round_trip_binds_ordered_request_and_expected_marker_state()
    {
        FakeTimeProvider time = FrozenTime();

        IMemoryReviewTokenCodec codec = new MemoryReviewTokenCodec(time);

        MemoryReviewPreparedPlanTokenFacts facts = new(
            MemoryReviewStore.Lexicon,
            ScopeDigest,
            MarkerGeneration,
            MarkerRevision: 9,
            FrozenLowerEventSequence: 102,
            FrozenUpperEventSequence: 902,
            OrderedRequestDigest: Digest(0x40));

        Result<string> encoded = codec.IssuePreparedPlan(facts);

        Assert.True(encoded.IsSuccess, encoded.Error.Message);

        Result<MemoryReviewPreparedPlanTokenFacts> decoded = codec.ReadPreparedPlan(encoded.Value);

        Assert.True(decoded.IsSuccess, decoded.Error.Message);

        Assert.Equal(facts, decoded.Value);
    }

    [Fact]
    public void A_token_is_rejected_by_every_other_purpose()
    {
        FakeTimeProvider time = FrozenTime();

        IMemoryReviewTokenCodec codec = new MemoryReviewTokenCodec(time);

        string cursor = codec.IssueCursor(Cursor()).Value;

        Assert.True(codec.ReadObservation(cursor).IsFailure);

        Assert.True(codec.ReadPreparedPlan(cursor).IsFailure);
    }

    [Fact]
    public void A_tampered_token_is_rejected()
    {
        FakeTimeProvider time = FrozenTime();

        IMemoryReviewTokenCodec codec = new MemoryReviewTokenCodec(time);

        string token = codec.IssueObservation(Observation()).Value;

        char replacement = token[^1] == 'A' ? 'B' : 'A';

        string tampered = token[..^1] + replacement;

        Assert.True(codec.ReadObservation(tampered).IsFailure);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64url!")]
    [InlineData("AA")]
    public void A_malformed_token_is_rejected(string token)
    {
        IMemoryReviewTokenCodec codec = new MemoryReviewTokenCodec(FrozenTime());

        Assert.True(codec.ReadCursor(token).IsFailure);

        Assert.True(codec.ReadObservation(token).IsFailure);

        Assert.True(codec.ReadPreparedPlan(token).IsFailure);
    }

    [Fact]
    public void An_oversized_token_is_rejected_before_decoding()
    {
        IMemoryReviewTokenCodec codec = new MemoryReviewTokenCodec(FrozenTime());

        string oversized = new('A', MemoryReviewLimits.MaxTokenCharacters + 1);

        Assert.True(codec.ReadCursor(oversized).IsFailure);
    }

    [Fact]
    public void A_token_is_rejected_at_its_expiry_boundary()
    {
        FakeTimeProvider time = FrozenTime();

        IMemoryReviewTokenCodec codec = new MemoryReviewTokenCodec(time);

        string token = codec.IssuePreparedPlan(PreparedPlan()).Value;

        time.Advance(MemoryReviewLimits.TokenLifetime);

        Assert.True(codec.ReadPreparedPlan(token).IsFailure);
    }

    [Fact]
    public void Facts_outside_the_frozen_frontier_are_refused_before_a_token_is_issued()
    {
        IMemoryReviewTokenCodec codec = new MemoryReviewTokenCodec(FrozenTime());

        MemoryReviewObservationTokenFacts outside = Observation() with { EventSequence = 999 };

        MemoryReviewCursorTokenFacts invalidKeyset = Cursor() with { KeysetEventSequence = 99 };

        Assert.True(codec.IssueObservation(outside).IsFailure);

        Assert.True(codec.IssueCursor(invalidKeyset).IsFailure);
    }

    private static MemoryReviewCursorTokenFacts Cursor() => new(
        MemoryReviewStore.Covenant,
        ScopeDigest,
        MarkerGeneration,
        MarkerRevision: 7,
        FrozenLowerEventSequence: 100,
        FrozenUpperEventSequence: 900,
        KeysetEventSequence: 650,
        KeysetVersionIdentity: Digest(0x20));

    private static MemoryReviewObservationTokenFacts Observation() => new(
        MemoryReviewStore.Saga,
        ScopeDigest,
        MarkerGeneration,
        MarkerRevision: 8,
        FrozenLowerEventSequence: 101,
        FrozenUpperEventSequence: 901,
        EventSequence: 444,
        VersionIdentity: Digest(0x30));

    private static MemoryReviewPreparedPlanTokenFacts PreparedPlan() => new(
        MemoryReviewStore.Lexicon,
        ScopeDigest,
        MarkerGeneration,
        MarkerRevision: 9,
        FrozenLowerEventSequence: 102,
        FrozenUpperEventSequence: 902,
        OrderedRequestDigest: Digest(0x40));

    private static MemoryReviewDigest Digest(byte first)
    {
        byte[] bytes = new byte[MemoryReviewDigest.Size];

        bytes[0] = first;

        bytes[^1] = (byte)(first + 1);

        return new MemoryReviewDigest(bytes);
    }

    private static FakeTimeProvider FrozenTime()
    {
        FakeTimeProvider time = new();

        time.SetUtcNow(DateTimeOffset.Parse("2026-09-28T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

        return time;
    }
}
