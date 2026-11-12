using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildSpendCorrectionPolicyTests
{
    private static readonly GuildSpendFacts Current = new(100, 20, 40, 5, .25m, true, true, false);
    private static readonly GuildSpendAmountsRequest Higher = new(110, 25, 45, 6, .30m);
    private static readonly GuildSpendCorrectionRequest Request = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), 1,
        GuildSpendOrigin.OperatorAttested, Higher, new string('a', 64));

    [Fact]
    public void Increasing_facts_preserves_uncertainty_and_completion()
    {
        var result = GuildSpendCorrectionPolicy.Validate(Current, Higher);

        Assert.True(result.IsSuccess);

        Assert.Equal(new GuildSpendFacts(110, 25, 45, 6, .30m, true, true, false), result.Value);

        Assert.True(GuildSpendCorrectionPolicy.Validate(Current, new(100, 20, 40, 5, .25m)).IsSuccess);

        var known = GuildSpendCorrectionPolicy.Validate(Current with { UnknownUsd = false, UnknownTokens = false, Complete = true }, Higher);

        Assert.Equal(new GuildSpendFacts(110, 25, 45, 6, .30m, false, false, true), known.Value);
    }

    [Fact]
    public void Each_decreasing_dimension_is_a_conflict()
    {
        GuildSpendAmountsRequest[] decreases = [Higher with { InputTokens = 99 }, Higher with { OutputTokens = 19 },
            Higher with { CachedTokens = 39 }, Higher with { ReasoningTokens = 4 }, Higher with { Usd = .24m }];

        foreach (var proposed in decreases)
        {
            var result = GuildSpendCorrectionPolicy.Validate(Current, proposed);

            Assert.True(result.IsFailure);

            Assert.Equal(ErrorCodes.Guild.RevisionConflict, result.Error.Code);
        }
    }

    [Fact]
    public void Invalid_and_missing_proposed_dimensions_are_refused()
    {
        GuildSpendAmountsRequest?[] invalid = [null, Higher with { InputTokens = null }, Higher with { OutputTokens = null },
            Higher with { CachedTokens = null }, Higher with { ReasoningTokens = null }, Higher with { Usd = null },
            Higher with { InputTokens = -1 }, Higher with { OutputTokens = -1 }, Higher with { CachedTokens = -1 },
            Higher with { ReasoningTokens = -1 }, Higher with { Usd = -1 }, Higher with { CachedTokens = 111 },
            Higher with { ReasoningTokens = 26 }, Higher with { InputTokens = long.MaxValue }];

        foreach (var proposed in invalid)
        {
            Assert.Equal(ErrorCodes.Guild.InvalidRequest, GuildSpendCorrectionPolicy.Validate(Current, proposed).Error.Code);
        }
    }

    [Fact]
    public void Missing_or_malformed_current_facts_are_refused()
    {
        GuildSpendFacts?[] invalid = [null, Current with { InputTokens = -1 }, Current with { OutputTokens = -1 },
            Current with { CachedTokens = -1 }, Current with { ReasoningTokens = -1 }, Current with { Usd = -1 },
            Current with { CachedTokens = 101 }, Current with { ReasoningTokens = 21 }, Current with { InputTokens = long.MaxValue }];

        foreach (var current in invalid)
        {
            Assert.Equal(ErrorCodes.Guild.InvalidRequest, GuildSpendCorrectionPolicy.Validate(current, Higher).Error.Code);
        }
    }

    [Fact]
    public void Operator_request_requires_identity_revision_origin_evidence_and_complete_amounts()
    {
        Assert.True(GuildSpendCorrectionPolicy.ValidateOperatorRequest(Request).IsSuccess);

        Assert.True(GuildSpendCorrectionPolicy.ValidateOperatorRequest(Request with { EvidenceDigest = new string('F', 64) }).IsSuccess);

        GuildSpendCorrectionRequest?[] invalid = [null, Request with { IdempotencyKey = Guid.Empty }, Request with { ExpectedRevision = 0 },
            Request with { ExpectedRevision = -1 }, Request with { Origin = null }, Request with { Origin = GuildSpendOrigin.LocalResponse },
            Request with { Origin = GuildSpendOrigin.RemoteReport }, Request with { Origin = GuildSpendOrigin.ProviderReported },
            Request with { Origin = (GuildSpendOrigin)99 }, Request with { EvidenceDigest = null },
            Request with { EvidenceDigest = new string('a', 63) }, Request with { EvidenceDigest = new string('a', 65) },
            Request with { EvidenceDigest = new string('g', 64) }, Request with { Amounts = null },
            Request with { Amounts = Higher with { Usd = null } }, Request with { Amounts = Higher with { CachedTokens = 111 } }];

        foreach (var request in invalid)
        {
            Assert.Equal(ErrorCodes.Guild.InvalidRequest, GuildSpendCorrectionPolicy.ValidateOperatorRequest(request).Error.Code);
        }
    }
}
