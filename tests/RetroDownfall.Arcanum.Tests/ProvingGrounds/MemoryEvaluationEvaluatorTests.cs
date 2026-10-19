using System.Text.Json;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;

namespace RetroDownfall.Arcanum.Tests.ProvingGrounds;

public sealed class MemoryEvaluationEvaluatorTests
{
    [Fact]
    public void Regex_evaluates_whole_answer_with_declared_flags()
    {
        var result = MemoryEvaluationDeterministicEvaluator.Evaluate("Hello world", [new MemoryEvaluationRegexCheck("hello", IgnoreCase: true), new MemoryEvaluationRegexCheck("absent", ShouldMatch: false), new MemoryEvaluationRegexCheck("hello")], default);

        Assert.Equal(new[] { MemoryEvaluationVerdict.Pass, MemoryEvaluationVerdict.Pass, MemoryEvaluationVerdict.Fail }, result.Value.ToArray());
    }

    [Fact]
    public void Invalid_json_answer_is_assertion_failure_while_invalid_schema_is_evaluator_failure()
    {
        using JsonDocument schema = JsonDocument.Parse("{\"type\":\"object\"}");

        Assert.Equal(MemoryEvaluationVerdict.Fail, MemoryEvaluationDeterministicEvaluator.Evaluate("not json", [new MemoryEvaluationJsonSchemaCheck(schema.RootElement)], default).Value[0]);

        using JsonDocument unsupported = JsonDocument.Parse("{\"minimum\":1}");

        Assert.True(MemoryEvaluationDeterministicEvaluator.Evaluate("2", [new MemoryEvaluationJsonSchemaCheck(unsupported.RootElement)], default).IsFailure);
    }

    [Fact]
    public void Supported_schema_enforces_properties_items_enum_required_union_and_additional_properties()
    {
        using JsonDocument schema = JsonDocument.Parse("""{"type":"object","properties":{"items":{"type":"array","items":{"type":["string","null"],"enum":["yes",null]}}},"required":["items"],"additionalProperties":false}""");

        MemoryEvaluationJsonSchemaCheck check = new(schema.RootElement);

        Assert.Equal(MemoryEvaluationVerdict.Pass, MemoryEvaluationDeterministicEvaluator.Evaluate("""{"items":["yes",null]}""", [check], default).Value[0]);

        Assert.Equal(MemoryEvaluationVerdict.Fail, MemoryEvaluationDeterministicEvaluator.Evaluate("""{"items":["no"],"extra":1}""", [check], default).Value[0]);

        Assert.Equal(MemoryEvaluationVerdict.Fail, MemoryEvaluationDeterministicEvaluator.Evaluate("{}", [check], default).Value[0]);
    }

    [Fact]
    public void Regex_timeout_is_invalid_evaluation_instead_of_failed_assertion()
    {
        var result = MemoryEvaluationDeterministicEvaluator.Evaluate(new string('a', 32000) + "!", [new MemoryEvaluationRegexCheck("^(a+)+$")], default);

        Assert.True(result.IsFailure);

        Assert.Equal("MemoryEvaluation.InvalidEvaluation", result.Error.Code);
    }

    [Fact]
    public void Enum_does_not_bypass_other_schema_constraints()
    {
        using JsonDocument schema = JsonDocument.Parse("""{"type":"object","enum":[{"x":1}],"additionalProperties":false}""");

        Assert.Equal(MemoryEvaluationVerdict.Fail, MemoryEvaluationDeterministicEvaluator.Evaluate("""{"x":1}""", [new MemoryEvaluationJsonSchemaCheck(schema.RootElement)], default).Value[0]);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Required_preserves_empty_and_whitespace_property_names(string name)
    {
        using JsonDocument schema = JsonDocument.Parse("{\"type\":\"object\",\"required\":[\"" + name + "\"]}");

        MemoryEvaluationJsonSchemaCheck check = new(schema.RootElement);

        Assert.Equal(MemoryEvaluationVerdict.Fail, MemoryEvaluationDeterministicEvaluator.Evaluate("{}", [check], default).Value[0]);

        Assert.Equal(MemoryEvaluationVerdict.Pass, MemoryEvaluationDeterministicEvaluator.Evaluate("{\"" + name + "\":1}", [check], default).Value[0]);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Required_names_survive_nested_object_and_array_item_schemas(string name)
    {
        string requiredObject = "{\"type\":\"object\",\"required\":[\"" + name + "\"]}";

        using JsonDocument schema = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"items\":{\"type\":\"array\",\"items\":" + requiredObject + "}}}");

        MemoryEvaluationJsonSchemaCheck check = new(schema.RootElement);

        Assert.Equal(MemoryEvaluationVerdict.Fail, MemoryEvaluationDeterministicEvaluator.Evaluate("{\"items\":[{}]}", [check], default).Value[0]);

        Assert.Equal(MemoryEvaluationVerdict.Pass, MemoryEvaluationDeterministicEvaluator.Evaluate("{\"items\":[{\"" + name + "\":1}]}", [check], default).Value[0]);
    }

    [Fact]
    public void Output_overflow_and_cancellation_are_failures()
    {
        Assert.True(MemoryEvaluationDeterministicEvaluator.Evaluate(new string('x', 65537), [new MemoryEvaluationRegexCheck("x")], default).IsFailure);

        Assert.True(MemoryEvaluationDeterministicEvaluator.Evaluate("x", [new MemoryEvaluationRegexCheck("x")], new CancellationToken(true)).IsFailure);
    }
}
