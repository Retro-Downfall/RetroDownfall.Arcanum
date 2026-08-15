using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// The compiler is the only normalization and escaping path from operator text into SQL.
/// </summary>
public sealed class CovenantSearchQueryCompilerTests
{

    private static readonly CovenantSearchQueryCompiler Compiler = new();

    [Fact]
    public void Terms_become_quoted_literals_joined_by_explicit_and()
    {

        CovenantCompiledSearchTerms compiled = Compile("build root");

        Assert.Equal("\"build\"* AND \"root\"*", compiled.MatchExpression);

        Assert.Equal(["build", "root"], (string[])[.. compiled.NormalizedTerms]);

        Assert.Equal(["%build%", "%root%"], (string[])[.. compiled.LikePatterns]);

    }

    [Fact]
    public void Raw_fts_syntax_is_neutralized_rather_than_accepted()
    {

        // Each of these is an FTS5 operator or wildcard. After compilation they are ordinary tokens
        // inside quoted literals, so none of them can change the shape of the query.
        CovenantCompiledSearchTerms compiled = Compile("a OR b NEAR(c) key:d *e ^f -g");

        Assert.DoesNotContain(" OR ", compiled.MatchExpression, StringComparison.Ordinal);

        Assert.DoesNotContain("NEAR(", compiled.MatchExpression.Replace("\"NEAR(c)\"", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);

        Assert.Equal(8, compiled.NormalizedTerms.Length);

        Assert.Contains("\"OR\"*", compiled.MatchExpression, StringComparison.Ordinal);

        Assert.Contains("\"key:d\"*", compiled.MatchExpression, StringComparison.Ordinal);

        // Exactly one AND per gap between terms, all emitted by the compiler.
        Assert.Equal(7, CountOccurrences(compiled.MatchExpression, " AND "));

    }

    [Fact]
    public void Embedded_double_quotes_are_doubled()
    {

        CovenantCompiledSearchTerms compiled = Compile("\"quoted\"");

        Assert.Equal("\"\"\"quoted\"\"\"*", compiled.MatchExpression);

    }

    [Fact]
    public void The_prefix_marker_is_a_suffix_outside_the_quote()
    {

        CovenantCompiledSearchTerms compiled = Compile("term");

        Assert.EndsWith("\"*", compiled.MatchExpression, StringComparison.Ordinal);

        Assert.StartsWith("\"term\"", compiled.MatchExpression, StringComparison.Ordinal);

    }

    [Fact]
    public void Like_metacharacters_are_escaped_under_the_declared_escape()
    {

        CovenantCompiledSearchTerms compiled = Compile("100%_x\\y");

        string pattern = Assert.Single((string[])[.. compiled.LikePatterns]);

        Assert.Equal("%100\\%\\_x\\\\y%", pattern);

        Assert.Equal('\\', CovenantCompiledSearchTerms.LikeEscape);

    }

    [Fact]
    public void Policy_whitespace_collapses_and_splits_terms_identically()
    {

        // A no-break space and an ideographic space are whitespace under policy v1, so these two
        // queries have to compile to the same terms or they would produce different cursors.
        Assert.Equal(
            Compile("alpha beta").MatchExpression,
            Compile("alpha 　beta").MatchExpression);

        Assert.Equal(["padded"], (string[])[.. Compile("  padded  ").NormalizedTerms]);

    }

    [Fact]
    public void Composed_and_decomposed_text_normalize_to_the_same_terms()
    {

        Assert.Equal(Compile("café").MatchExpression, Compile("café").MatchExpression);

    }

    [Fact]
    public void Oversized_and_over_termed_queries_are_refused()
    {

        Result<CovenantCompiledSearchTerms> tooLong = Compiler.Compile(new string('a', CovenantLimits.MaxSearchQueryBytes + 1));

        Assert.Equal("Validation.InvalidQuery", tooLong.Error.Code);

        Result<CovenantCompiledSearchTerms> tooMany = Compiler.Compile(
            string.Join(' ', Enumerable.Range(0, CovenantLimits.MaxSearchQueryTerms + 1).Select(static index => $"t{index}")));

        Assert.Equal("Validation.InvalidQuery", tooMany.Error.Code);

        Result<CovenantCompiledSearchTerms> atLimit = Compiler.Compile(
            string.Join(' ', Enumerable.Range(0, CovenantLimits.MaxSearchQueryTerms).Select(static index => $"t{index}")));

        Assert.True(atLimit.IsSuccess);

    }

    [Theory]
    [InlineData("\0")]
    [InlineData("ab")]
    [InlineData("ab")]
    [InlineData("a​b")]
    [InlineData("a‎b")]
    [InlineData("a﻿b")]
    public void Prohibited_scalars_are_refused(string query)
    {

        Assert.Equal("Validation.InvalidQuery", Compiler.Compile(query).Error.Code);

    }

    [Fact]
    public void Malformed_utf16_is_refused()
    {

        Assert.Equal("Validation.InvalidQuery", Compiler.Compile("a\ud800b").Error.Code);

    }

    [Fact]
    public void A_whitespace_only_query_has_no_terms()
    {

        Assert.Equal("Validation.InvalidQuery", Compiler.Compile("   \t  ").Error.Code);

    }

    [Fact]
    public void A_malicious_corpus_never_produces_unquoted_syntax()
    {

        Random random = new(20260815);

        string alphabet = "\"*^-():abcAND OR NEAR{}[]\\%_'`;";

        for (int attempt = 0; attempt < 512; attempt++)
        {

            string candidate = new(
                [.. Enumerable.Range(0, random.Next(1, 24)).Select(_ => alphabet[random.Next(alphabet.Length)])]);

            Result<CovenantCompiledSearchTerms> compiled = Compiler.Compile(candidate);

            if (compiled.IsFailure)
            {

                continue;

            }

            // Outside the quoted literals, the expression may contain only the operators the
            // compiler itself emitted.
            string skeleton = StripQuotedLiterals(compiled.Value.MatchExpression);

            Assert.Equal(
                string.Empty,
                skeleton.Replace(" AND ", string.Empty, StringComparison.Ordinal)
                    .Replace("*", string.Empty, StringComparison.Ordinal));

        }

    }

    [Fact]
    public void Cursor_scores_round_trip_and_reject_nonfinite_values()
    {

        double[] finite = [0d, -0d, 1.5d, -1.5d, double.Epsilon, double.MaxValue, double.MinValue];

        foreach (double score in finite)
        {

            Result<ulong> bits = CovenantSearchKeyset.EncodeScore(score);

            Assert.True(bits.IsSuccess);

            CovenantSearchKeyset keyset = new(CovenantSearchMatchClass.Ranked, bits.Value, Guid.NewGuid(), Guid.NewGuid());

            Assert.Equal(score == 0d ? 0d : score, keyset.Score);

        }

        // Negative zero canonicalizes, so an equal score compares equal instead of splitting a tie.
        Assert.Equal(
            CovenantSearchKeyset.EncodeScore(0d).Value,
            CovenantSearchKeyset.EncodeScore(-0d).Value);

        foreach (double invalid in (double[])[double.NaN, double.PositiveInfinity, double.NegativeInfinity])
        {

            Assert.Equal("Covenant.InvalidCursor", CovenantSearchKeyset.EncodeScore(invalid).Error.Code);

        }

    }

    [Fact]
    public void Every_cursor_filter_constructs_the_pinned_filter_digest_input()
    {

        CovenantDigest digest = CovenantDigests.CursorFilter(
            new CursorFilterDigestInput(
                CovenantCursorEndpoint.FtsQuery,
                CovenantCursorScopeSelection.Campaign,
                CovenantOperationGateFixture.CampaignOne,
                CovenantOperationGateFixture.CampaignOne,
                CovenantLane.Confirmed,
                CovenantLifecycle.Set,
                CovenantOperationGateFixture.Digest(9),
                50,
                CovenantCursorSort.FtsRank));

        Assert.True(digest.IsValid);

        // The same filter always yields the same digest, which is what makes a cursor bindable.
        Assert.Equal(
            digest,
            CovenantDigests.CursorFilter(
                new CursorFilterDigestInput(
                    CovenantCursorEndpoint.FtsQuery,
                    CovenantCursorScopeSelection.Campaign,
                    CovenantOperationGateFixture.CampaignOne,
                    CovenantOperationGateFixture.CampaignOne,
                    CovenantLane.Confirmed,
                    CovenantLifecycle.Set,
                    CovenantOperationGateFixture.Digest(9),
                    50,
                    CovenantCursorSort.FtsRank)));

    }

    [Fact]
    public void A_cursor_body_distinguishes_stale_sources_from_an_invalid_binding()
    {

        CovenantDigest filter = CovenantOperationGateFixture.Digest(21);

        CovenantSearchSourceSnapshot sources = new(
            CovenantOperationGateFixture.DatasetGeneration,
            12,
            3,
            CovenantOperationGateFixture.DatasetGeneration,
            12,
            3,
            4);

        CovenantFtsCursorBody body = new(
            filter,
            sources.DatasetGeneration,
            sources.CanonicalSearchSequence,
            sources.CoreCampaignDeletionSequence,
            sources.AppliedDatasetGeneration!.Value,
            sources.AppliedSearchSequence!.Value,
            sources.AppliedCampaignDeletionSequence!.Value,
            sources.AcceleratorEpoch,
            EnvelopeKeyVersion: 1,
            new CovenantSearchKeyset(CovenantSearchMatchClass.Ranked, 0, Guid.NewGuid(), Guid.NewGuid()));

        Assert.Equal(
            CovenantCursorRejection.None,
            CovenantCursorBodyValidator.Validate(body, filter, sources, envelopeKeyVersion: 1));

        Assert.Equal(
            CovenantCursorRejection.Invalid,
            CovenantCursorBodyValidator.Validate(body, CovenantOperationGateFixture.Digest(99), sources, 1));

        Assert.Equal(
            CovenantCursorRejection.Invalid,
            CovenantCursorBodyValidator.Validate(body, filter, sources, envelopeKeyVersion: 2));

        Assert.Equal(
            CovenantCursorRejection.Stale,
            CovenantCursorBodyValidator.Validate(
                body,
                filter,
                sources with { CanonicalSearchSequence = 13 },
                1));

        Assert.Equal(
            CovenantCursorRejection.Stale,
            CovenantCursorBodyValidator.Validate(body, filter, sources with { AcceleratorEpoch = 5 }, 1));

    }

    [Fact]
    public void Accelerator_eligibility_requires_every_applied_fact_to_match()
    {

        CovenantSearchSourceSnapshot current = new(
            CovenantOperationGateFixture.DatasetGeneration,
            12,
            3,
            CovenantOperationGateFixture.DatasetGeneration,
            12,
            3,
            4);

        Assert.True(current.AcceleratorEligible);

        Assert.False((current with { AppliedSearchSequence = 11 }).AcceleratorEligible);

        Assert.False((current with { AppliedCampaignDeletionSequence = 2 }).AcceleratorEligible);

        Assert.False((current with { AppliedDatasetGeneration = null }).AcceleratorEligible);

    }

    private static CovenantCompiledSearchTerms Compile(string query)
    {

        Result<CovenantCompiledSearchTerms> compiled = Compiler.Compile(query);

        Assert.True(compiled.IsSuccess, compiled.IsFailure ? compiled.Error.Message : null);

        return compiled.Value;

    }

    private static string StripQuotedLiterals(string expression)
    {

        System.Text.StringBuilder skeleton = new();

        bool inside = false;

        for (int index = 0; index < expression.Length; index++)
        {

            if (expression[index] == '"')
            {

                // A doubled quote inside a literal is content, not a delimiter.
                if (inside && index + 1 < expression.Length && expression[index + 1] == '"')
                {

                    index++;

                    continue;

                }

                inside = !inside;

                continue;

            }

            if (!inside)
            {

                _ = skeleton.Append(expression[index]);

            }

        }

        return skeleton.ToString();

    }

    private static int CountOccurrences(string value, string needle)
    {

        int count = 0;

        for (int index = value.IndexOf(needle, StringComparison.Ordinal);
            index >= 0;
            index = value.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
        {

            count++;

        }

        return count;

    }

}
