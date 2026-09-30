using System.Buffers.Text;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Memory;
using RetroDownfall.Arcanum.Secrets.Security;
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

    [Fact]
    public void An_erasure_plan_round_trip_binds_store_digests_binding_and_generation()
    {
        FakeTimeProvider time = FrozenTime();

        IMemoryErasureTokenCodec codec = new MemoryReviewTokenCodec(time);

        Guid generation = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        MemoryErasureIssuedToken issued = codec.IssueErasurePlan(
            new(MemoryReviewStore.Saga, Bytes(0x11), Bytes(0x22), Bytes(0x33), generation)).Value;

        // 18 header + 115 payload + 32 tag bytes, as unpadded base64url.
        Assert.Equal(220, issued.Token.Length);

        Assert.Equal(4, Base64Url.DecodeFromChars(issued.Token)[1]);

        Assert.Equal(time.GetUtcNow(), issued.IssuedAtUtc);

        Assert.Equal(issued.IssuedAtUtc + MemoryReviewLimits.TokenLifetime, issued.ExpiresAtUtc);

        MemoryErasurePlanTokenFacts read = codec.ReadErasurePlan(issued.Token).Value;

        Assert.Equal((MemoryReviewStore.Saga, (Guid?)generation), (read.Store, read.DatasetGeneration));

        Assert.Equal(Bytes(0x11), read.RequestDigest);

        Assert.Equal(Bytes(0x22), read.EffectDigest);

        Assert.Equal(Bytes(0x33), read.ContentBinding);
    }

    [Fact]
    public void A_lexicon_erasure_plan_without_label_round_trips_absent_binding_and_generation()
    {
        IMemoryErasureTokenCodec codec = new MemoryReviewTokenCodec(FrozenTime());

        MemoryErasureIssuedToken issued = codec.IssueErasurePlan(
            new(MemoryReviewStore.Lexicon, Bytes(0x44), Bytes(0x55), null, null)).Value;

        Assert.Equal(220, issued.Token.Length);

        MemoryErasurePlanTokenFacts read = codec.ReadErasurePlan(issued.Token).Value;

        Assert.Equal(MemoryReviewStore.Lexicon, read.Store);

        Assert.Equal(Bytes(0x44), read.RequestDigest);

        Assert.Equal(Bytes(0x55), read.EffectDigest);

        Assert.Null(read.ContentBinding);

        Assert.Null(read.DatasetGeneration);
    }

    [Fact]
    public void A_key_reset_round_trip_binds_status_and_counts()
    {
        FakeTimeProvider time = FrozenTime();

        IMemoryErasureTokenCodec codec = new MemoryReviewTokenCodec(time);

        MemoryErasureIssuedToken issued = codec.IssueErasureKeyReset(new(MemoryErasureKeyStatus.Lost, 7, 3)).Value;

        // 18 header + 17 payload + 32 tag bytes.
        Assert.Equal(90, issued.Token.Length);

        Assert.Equal(5, Base64Url.DecodeFromChars(issued.Token)[1]);

        Assert.Equal(time.GetUtcNow(), issued.IssuedAtUtc);

        Assert.Equal(issued.IssuedAtUtc + MemoryReviewLimits.TokenLifetime, issued.ExpiresAtUtc);

        Assert.Equal(new MemoryErasureKeyResetTokenFacts(MemoryErasureKeyStatus.Lost, 7, 3), codec.ReadErasureKeyReset(issued.Token).Value);
    }

    [Fact]
    public void Erasure_tokens_and_review_tokens_refuse_each_others_purposes()
    {
        MemoryReviewTokenCodec codec = new(FrozenTime());

        string plan = codec.IssueErasurePlan(new(MemoryReviewStore.Saga, Bytes(0x11), Bytes(0x22), Bytes(0x33), null)).Value.Token;

        string reset = codec.IssueErasureKeyReset(new(MemoryErasureKeyStatus.Present, 0, 0)).Value.Token;

        string cursor = codec.IssueCursor(Cursor()).Value;

        string observation = codec.IssueObservation(Observation()).Value;

        string prepared = codec.IssuePreparedPlan(PreparedPlan()).Value;

        // Every purpose has its own header byte, so no token can be read as another even where two
        // payloads happened to share a length.
        byte[] purposes = [.. new[] { cursor, observation, prepared, plan, reset }.Select(static token => Base64Url.DecodeFromChars(token)[1])];

        Assert.Equal(purposes.Length, purposes.Distinct().Count());

        Assert.True(codec.ReadCursor(plan).IsFailure);

        Assert.True(codec.ReadObservation(plan).IsFailure);

        Assert.True(codec.ReadPreparedPlan(plan).IsFailure);

        Result<MemoryErasureKeyResetTokenFacts> planAsReset = codec.ReadErasureKeyReset(plan);

        Result<MemoryErasurePlanTokenFacts> cursorAsPlan = codec.ReadErasurePlan(cursor);

        Result<MemoryErasurePlanTokenFacts> resetAsPlan = codec.ReadErasurePlan(reset);

        Result<MemoryErasureKeyResetTokenFacts> preparedAsReset = codec.ReadErasureKeyReset(prepared);

        foreach (Error error in (Error[])[planAsReset.Error, cursorAsPlan.Error, resetAsPlan.Error, preparedAsReset.Error])
        {
            Assert.Equal(ErrorCodes.MemoryErasure.InvalidPreflight, error.Code);
        }

        Assert.True(codec.ReadCursor(reset).IsFailure);
    }

    [Fact]
    public void An_erasure_plan_token_is_refused_at_its_expiry_boundary()
    {
        FakeTimeProvider time = FrozenTime();

        IMemoryErasureTokenCodec codec = new MemoryReviewTokenCodec(time);

        string token = codec.IssueErasurePlan(new(MemoryReviewStore.Lexicon, Bytes(0x44), Bytes(0x55), null, null)).Value.Token;

        time.Advance(MemoryReviewLimits.TokenLifetime - TimeSpan.FromSeconds(1));

        Assert.True(codec.ReadErasurePlan(token).IsSuccess);

        time.Advance(TimeSpan.FromSeconds(1));

        Result<MemoryErasurePlanTokenFacts> expired = codec.ReadErasurePlan(token);

        Assert.Equal(ErrorCodes.MemoryErasure.InvalidPreflight, expired.Error.Code);
    }

    [Fact]
    public void A_tampered_or_malformed_erasure_token_is_an_invalid_preflight()
    {
        IMemoryErasureTokenCodec codec = new MemoryReviewTokenCodec(FrozenTime());

        string token = codec.IssueErasurePlan(new(MemoryReviewStore.Saga, Bytes(0x11), Bytes(0x22), null, null)).Value.Token;

        string tampered = token[..^1] + (token[^1] == 'A' ? 'B' : 'A');

        foreach (string candidate in (string[])[tampered, string.Empty, "not-base64url!", "AA"])
        {
            Assert.Equal(ErrorCodes.MemoryErasure.InvalidPreflight, codec.ReadErasurePlan(candidate).Error.Code);

            Assert.Equal(ErrorCodes.MemoryErasure.InvalidPreflight, codec.ReadErasureKeyReset(candidate).Error.Code);
        }
    }

    [Fact]
    public void Erasure_plan_facts_are_validated_before_issue()
    {
        IMemoryErasureTokenCodec codec = new MemoryReviewTokenCodec(FrozenTime());

        MemoryErasurePlanTokenFacts valid = new(MemoryReviewStore.Saga, Bytes(0x11), Bytes(0x22), Bytes(0x33), Guid.NewGuid());

        Assert.True(codec.IssueErasurePlan(valid).IsSuccess);

        MemoryErasurePlanTokenFacts[] invalid =
        [
            valid with { Store = MemoryReviewStore.Covenant },
            valid with { RequestDigest = new byte[31] },
            valid with { EffectDigest = new byte[33] },
            valid with { ContentBinding = new byte[31] },
            valid with { Store = MemoryReviewStore.Lexicon },
            valid with { DatasetGeneration = Guid.Empty },
        ];

        foreach (MemoryErasurePlanTokenFacts facts in invalid)
        {
            Assert.Equal(ErrorCodes.MemoryReview.InvalidTokenFacts, codec.IssueErasurePlan(facts).Error.Code);
        }

        Assert.Equal(ErrorCodes.MemoryReview.InvalidTokenFacts, codec.IssueErasurePlan(null!).Error.Code);

        Assert.Equal(
            ErrorCodes.MemoryReview.InvalidTokenFacts,
            codec.IssueErasureKeyReset(new((MemoryErasureKeyStatus)9, 0, 0)).Error.Code);

        Assert.Equal(
            ErrorCodes.MemoryReview.InvalidTokenFacts,
            codec.IssueErasureKeyReset(new(MemoryErasureKeyStatus.Lost, -1, 0)).Error.Code);
    }

    /// <summary>
    /// One codec serves both token families, over the host's clock, so a review token and an erasure
    /// token are issued and bounded by the same instance.
    /// </summary>
    [Fact]
    public async Task Host_composition_registers_one_codec_behind_both_token_ports()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();

        builder.Services.AddSingleton<IOsCredentialStore>(new InMemoryOsCredentialStore());

        builder.Services.AddArcanumInfrastructure(new ConfigurationBuilder().Build());

        foreach (Type service in (Type[])[typeof(MemoryReviewTokenCodec), typeof(IMemoryReviewTokenCodec), typeof(IMemoryErasureTokenCodec)])
        {
            ServiceDescriptor descriptor = Assert.Single(builder.Services, candidate => candidate.ServiceType == service);

            Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        }

        await using ServiceProvider provider = builder.Services.BuildServiceProvider();

        MemoryReviewTokenCodec codec = provider.GetRequiredService<MemoryReviewTokenCodec>();

        Assert.Same(codec, provider.GetRequiredService<IMemoryReviewTokenCodec>());

        Assert.Same(codec, provider.GetRequiredService<IMemoryErasureTokenCodec>());
    }

    private static byte[] Bytes(byte value) => [.. Enumerable.Repeat(value, 32)];

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
