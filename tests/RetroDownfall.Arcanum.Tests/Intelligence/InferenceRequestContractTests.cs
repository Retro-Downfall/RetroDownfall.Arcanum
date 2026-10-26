using System.Text.Json;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class InferenceRequestContractTests
{
    [Theory]
    [InlineData("temperature", -0.01f)]
    [InlineData("temperature", 7f)]
    [InlineData("temperature", float.NaN)]
    [InlineData("temperature", float.PositiveInfinity)]
    [InlineData("topP", -0.01f)]
    [InlineData("topP", 3f)]
    [InlineData("topP", float.NaN)]
    [InlineData("presencePenalty", -9f)]
    [InlineData("presencePenalty", 3f)]
    [InlineData("presencePenalty", float.NegativeInfinity)]
    [InlineData("frequencyPenalty", -3f)]
    [InlineData("frequencyPenalty", 3f)]
    [InlineData("frequencyPenalty", float.NaN)]
    public void Invalid_sampling_values_are_refused_with_the_field_and_bounds(string field, float value)
    {
        PingRequest request = WithSampling(field, value);

        Result result = PingRequestBoundsValidator.Validate(request);

        Assert.True(result.IsFailure);

        Assert.StartsWith("Validation.", result.Error.Code, StringComparison.Ordinal);

        Assert.Contains(field, result.Error.Message, StringComparison.Ordinal);

        Assert.Contains("finite", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("temperature", 0f)]
    [InlineData("temperature", 2f)]
    [InlineData("topP", 0f)]
    [InlineData("topP", 1f)]
    [InlineData("presencePenalty", -2f)]
    [InlineData("presencePenalty", 2f)]
    [InlineData("frequencyPenalty", -2f)]
    [InlineData("frequencyPenalty", 2f)]
    public void Inclusive_sampling_bounds_remain_valid(string field, float value) =>
        Assert.True(PingRequestBoundsValidator.Validate(WithSampling(field, value)).IsSuccess);

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Nonpositive_output_token_limits_are_refused(int value)
    {
        Result result = PingRequestBoundsValidator.Validate(new PingRequest("hello", MaxOutputTokens: value));

        Assert.True(result.IsFailure);

        Assert.Contains("maxOutputTokens", result.Error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("xml", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("json_schema", null)]
    [InlineData("JSON_SCHEMA", "null")]
    [InlineData("json_schema", "[]")]
    [InlineData("json_schema", "{\"schema\":null}")]
    [InlineData("json_schema", "{\"schema\":17}")]
    [InlineData("json_schema", "true")]
    [InlineData("json_schema", "false")]
    [InlineData("json_schema", "{\"type\":17}")]
    [InlineData("json_schema", "{\"schema\":{\"type\":false}}")]
    [InlineData("json_schema", "{\"schema\":{},\"strict\":\"true\"}")]
    [InlineData("json_schema", "{\"schema\":{},\"name\":17}")]
    public void Invalid_response_format_or_missing_schema_is_refused(string format, string? schema)
    {
        using JsonDocument? document = schema is null ? null : JsonDocument.Parse(schema);

        Result result = PingRequestBoundsValidator.Validate(new PingRequest(
            "hello", ResponseFormat: format, ResponseFormatJsonSchema: document?.RootElement));

        Assert.True(result.IsFailure);

        Assert.Contains("responseFormat", result.Error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("text", null)]
    [InlineData(" JSON_OBJECT ", null)]
    [InlineData("json_schema", "{}")]
    [InlineData(" JSON_SCHEMA ", "{\"schema\":{\"type\":\"object\"},\"strict\":true}")]
    public void Valid_optional_controls_remain_valid(string? format, string? schema)
    {
        using JsonDocument? document = schema is null ? null : JsonDocument.Parse(schema);

        Result result = PingRequestBoundsValidator.Validate(new PingRequest(
            "hello", MaxOutputTokens: 1, ResponseFormat: format, ResponseFormatJsonSchema: document?.RootElement));

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(80, false)]
    public void In_process_schema_depth_returns_a_validation_result(int depth, bool expectedSuccess)
    {
        string json = string.Concat(Enumerable.Repeat("{\"extension\":", depth)) + "{}" + new string('}', depth);

        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 128 });

        Result result = PingRequestBoundsValidator.Validate(new PingRequest(
            "hello", ResponseFormat: "json_schema", ResponseFormatJsonSchema: document.RootElement));

        Assert.Equal(expectedSuccess, result.IsSuccess);

        if (!expectedSuccess)
        {
            Assert.Equal(ErrorCodes.Validation.InvalidResponseSchema, result.Error.Code);

            Assert.Contains("responseFormatJsonSchema", result.Error.Message, StringComparison.Ordinal);
        }
    }

    private static PingRequest WithSampling(string field, float value) => field switch
    {
        "temperature" => new("hello", Temperature: value),
        "topP" => new("hello", TopP: value),
        "presencePenalty" => new("hello", PresencePenalty: value),
        "frequencyPenalty" => new("hello", FrequencyPenalty: value),
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    [Theory]
    [InlineData("temperature")]
    [InlineData("topP")]
    [InlineData("presencePenalty")]
    [InlineData("frequencyPenalty")]
    [InlineData("maxOutputTokens")]
    [InlineData("responseFormat")]
    [InlineData("responseFormatJsonSchema")]
    public void Invalid_inference_controls_keep_safe_preflight_failure_classification(string field)
    {
        PingRequest request = field switch
        {
            "maxOutputTokens" => new("hello", MaxOutputTokens: 0),
            "responseFormat" => new("hello", ResponseFormat: "SECRET_UNKNOWN_FORMAT"),
            "responseFormatJsonSchema" => new("hello", ResponseFormat: "json_schema"),
            _ => WithSampling(field, float.NaN),
        };

        Result result = PingRequestPreflightValidator.Validate(request, new ArcanumSettings(), out PingRequestFailureDetails? details);

        Assert.True(result.IsFailure);

        Assert.NotNull(details);

        Assert.Equal(result.Error.Code, details.ExpectedCode);

        Assert.DoesNotContain("SECRET_UNKNOWN_FORMAT", details.ToString(), StringComparison.Ordinal);
    }
}
