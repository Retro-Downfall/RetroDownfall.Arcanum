using System.Collections.Immutable;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Covenant;
using System.Text.Json;
using System.Runtime.InteropServices;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.ProvingGrounds;

public sealed class MemoryEvaluationValidationTests
{
    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(16, true)]
    [InlineData(17, false)]
    public void Pair_count_boundaries(int count, bool valid) => Assert.Equal(valid, MemoryEvaluationValidator.Validate(MemoryEvaluationTestData.ValidRequest(count)).IsSuccess);

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(4096, true)]
    [InlineData(4097, false)]
    public void Output_token_boundaries(int count, bool valid)
    {
        Assert.Equal(valid, MemoryEvaluationValidator.Validate(MemoryEvaluationTestData.ValidRequest() with { Options = new(MaxOutputTokens: count) }).IsSuccess);
    }

    [Theory]
    [InlineData(16383, true)]
    [InlineData(16384, true)]
    [InlineData(16385, false)]
    public void System_prompt_utf8_boundaries(int bytes, bool valid)
    {
        Assert.Equal(valid, MemoryEvaluationValidator.Validate(MemoryEvaluationTestData.ValidRequest() with { Workload = new(new string('a', bytes), "user") }).IsSuccess);
    }

    [Theory]
    [InlineData(32767, true)]
    [InlineData(32768, true)]
    [InlineData(32769, false)]
    public void User_prompt_utf8_boundaries(int bytes, bool valid)
    {
        Assert.Equal(valid, MemoryEvaluationValidator.Validate(MemoryEvaluationTestData.ValidRequest() with { Workload = new("system", new string('a', bytes)) }).IsSuccess);
    }

    [Theory]
    [InlineData(1023, true)]
    [InlineData(1024, true)]
    [InlineData(1025, false)]
    public void Regex_boundaries(int bytes, bool valid)
    {
        Assert.Equal(valid, MemoryEvaluationValidator.Validate(MemoryEvaluationTestData.ValidRequest() with { Checks = [new MemoryEvaluationRegexCheck(new string('a', bytes))] }).IsSuccess);
    }

    [Fact]
    public void Empty_and_duplicate_targets_or_checks_are_refused()
    {
        MemoryEvaluationRequest request = MemoryEvaluationTestData.ValidRequest();

        Assert.True(MemoryEvaluationValidator.Validate(request with { Memory = new([], [], []) }).IsFailure);

        Assert.True(MemoryEvaluationValidator.Validate(request with { Memory = request.Memory with { Covenant = [request.Memory.Covenant[0], request.Memory.Covenant[0]] } }).IsFailure);

        Assert.True(MemoryEvaluationValidator.Validate(request with { Checks = [] }).IsFailure);

        Assert.True(MemoryEvaluationValidator.Validate(request with { Checks = [request.Checks[0], request.Checks[0]] }).IsFailure);
    }

    [Fact]
    public void Invalid_scope_options_identity_and_utf16_are_refused()
    {
        MemoryEvaluationRequest request = MemoryEvaluationTestData.ValidRequest();

        Assert.True(MemoryEvaluationValidator.Validate(request with { EvaluationId = Guid.Empty }).IsFailure);

        Assert.True(MemoryEvaluationValidator.Validate(request with { Scope = new(MemoryEvaluationScopeKind.Campaign) }).IsFailure);

        Assert.True(MemoryEvaluationValidator.Validate(request with { Scope = new(MemoryEvaluationScopeKind.Global, Guid.NewGuid()) }).IsFailure);

        Assert.True(MemoryEvaluationValidator.Validate(request with { Options = new(Temperature: double.NaN) }).IsFailure);

        Assert.True(MemoryEvaluationValidator.Validate(request with { Workload = new("\ud800", "user") }).IsFailure);
    }

    [Theory]
    [InlineData("{\"type\":\"object\",\"$ref\":\"https://example.org\"}")]
    [InlineData("{\"type\":\"string\",\"minLength\":1}")]
    [InlineData("{\"type\":\"mystery\"}")]
    [InlineData("{\"type\":[\"string\",5]}")]
    [InlineData("{\"required\":[5]}")]
    [InlineData("{\"additionalProperties\":{}}")]
    [InlineData("{\"properties\":[]}")]
    public void Unsupported_or_malformed_schema_is_refused(string json)
    {
        using JsonDocument schema = JsonDocument.Parse(json);

        Assert.True(MemoryEvaluationValidator.Validate(MemoryEvaluationTestData.ValidRequest() with { Checks = [new MemoryEvaluationJsonSchemaCheck(schema.RootElement)] }).IsFailure);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(16, true)]
    [InlineData(17, false)]
    public void Target_count_boundaries(int count, bool valid)
    {
        MemoryEvaluationRequest request = MemoryEvaluationTestData.ValidRequest();

        var targets = Enumerable.Range(0, count).Select(_ => request.Memory.Covenant[0] with { EntryId = Guid.NewGuid() }).ToImmutableArray();

        Assert.Equal(valid, MemoryEvaluationValidator.Validate(request with { Memory = request.Memory with { Covenant = targets } }).IsSuccess);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(16, true)]
    [InlineData(17, false)]
    public void Check_count_boundaries(int count, bool valid)
    {
        var checks = Enumerable.Range(0, count).Select(index => (MemoryEvaluationCheck)new MemoryEvaluationRegexCheck(index.ToString())).ToImmutableArray();

        Assert.Equal(valid, MemoryEvaluationValidator.Validate(MemoryEvaluationTestData.ValidRequest() with { Checks = checks }).IsSuccess);
    }

    [Theory]
    [InlineData(15, true)]
    [InlineData(16, true)]
    [InlineData(17, false)]
    public void Schema_depth_boundaries(int depth, bool valid)
    {
        string json = "{}";

        for (int level = 1; level < depth; level++)
        {
            json = "{\"items\":" + json + "}";
        }

        using JsonDocument schema = JsonDocument.Parse(json);

        Assert.Equal(valid, MemoryEvaluationValidator.Validate(MemoryEvaluationTestData.ValidRequest() with { Checks = [new MemoryEvaluationJsonSchemaCheck(schema.RootElement)] }).IsSuccess);
    }

    [Theory]
    [InlineData(16383, true)]
    [InlineData(16384, true)]
    [InlineData(16385, false)]
    public void Total_schema_specification_byte_boundaries(int bytes, bool valid)
    {
        const string prefix = "{\"enum\":[\"";

        const string suffix = "\"]}";

        using JsonDocument schema = JsonDocument.Parse(prefix + new string('a', bytes - prefix.Length - suffix.Length) + suffix);

        Assert.Equal(valid, MemoryEvaluationValidator.Validate(MemoryEvaluationTestData.ValidRequest() with { Checks = [new MemoryEvaluationJsonSchemaCheck(schema.RootElement)] }).IsSuccess);
    }

    [Fact]
    public void Validated_request_copies_borrowed_array_storage()
    {
        MemoryEvaluationCheck[] borrowed = [new MemoryEvaluationRegexCheck("yes")];

        var validated = MemoryEvaluationValidator.Validate(MemoryEvaluationTestData.ValidRequest() with { Checks = ImmutableCollectionsMarshal.AsImmutableArray(borrowed) }).Value;

        borrowed[0] = new MemoryEvaluationRegexCheck("no");

        Assert.Equal("yes", Assert.IsType<MemoryEvaluationRegexCheck>(validated.Request.Checks[0]).Pattern);
    }

    [Fact]
    public void Opaque_Saga_and_Annals_identifiers_are_preserved_without_Guid_assumptions()
    {
        MemoryEvaluationRequest request = MemoryEvaluationTestData.ValidRequest() with { Memory = new([], [new("legacy-memory", "imported-version", AnnalContentHashFormat.LegacyStoreDigest, new CovenantDigest(new byte[32]))], []) };

        Assert.Equal("imported-version", MemoryEvaluationValidator.Validate(request).Value.Request.Memory.Saga[0].VersionId);
    }

    [Fact]
    public void Invalid_regex_is_refused_before_evaluation()
    {
        Assert.True(MemoryEvaluationValidator.Validate(MemoryEvaluationTestData.ValidRequest() with { Checks = [new MemoryEvaluationRegexCheck("[")] }).IsFailure);
    }

    [Fact]
    public void Duplicate_canonical_schema_cannot_increase_assertion_weight()
    {
        using JsonDocument first = JsonDocument.Parse("""{"type":"object","additionalProperties":false}""");

        using JsonDocument second = JsonDocument.Parse("""{"additionalProperties":false,"type":"object"}""");

        Assert.True(MemoryEvaluationValidator.Validate(MemoryEvaluationTestData.ValidRequest() with { Checks = [new MemoryEvaluationJsonSchemaCheck(first.RootElement), new MemoryEvaluationJsonSchemaCheck(second.RootElement)] }).IsFailure);
    }

    [Fact]
    public void Distinct_opaque_target_pairs_cannot_collide_through_delimiter_characters()
    {
        MemoryEvaluationRequest request = MemoryEvaluationTestData.ValidRequest() with
        {
            Memory = new([], [new("a:b", "c", AnnalContentHashFormat.LegacyStoreDigest, new CovenantDigest(new byte[32])), new("a", "b:c", AnnalContentHashFormat.LegacyStoreDigest, new CovenantDigest(new byte[32]))], [])
        };

        Assert.True(MemoryEvaluationValidator.Validate(request).IsSuccess);
    }

    [Fact]
    public void Actual_duplicate_opaque_target_pair_is_refused_even_with_a_different_expected_digest()
    {
        MemoryEvaluationSagaTarget target = new("a:b", "c", AnnalContentHashFormat.LegacyStoreDigest, new CovenantDigest(new byte[32]));

        MemoryEvaluationRequest request = MemoryEvaluationTestData.ValidRequest() with { Memory = new([], [target, target], []) };

        Assert.True(MemoryEvaluationValidator.Validate(request).IsFailure);

        var differentDigest = target with { ContentDigest = new CovenantDigest(Enumerable.Repeat((byte)1, 32).ToArray()) };

        Assert.True(MemoryEvaluationValidator.Validate(request with { Memory = new([], [target, differentDigest], []) }).IsFailure);
    }

    [Fact]
    public void Schema_ownership_survives_caller_document_disposal()
    {
        MemoryEvaluationValidatedRequest validated;

        using (JsonDocument document = JsonDocument.Parse("{\"type\":\"string\"}"))
        {
            validated = MemoryEvaluationValidator.Validate(MemoryEvaluationTestData.ValidRequest() with { Checks = [new MemoryEvaluationJsonSchemaCheck(document.RootElement)] }).Value;
        }

        Assert.Equal(JsonValueKind.Object, Assert.IsType<MemoryEvaluationJsonSchemaCheck>(validated.Request.Checks[0]).Schema.ValueKind);
    }
}
