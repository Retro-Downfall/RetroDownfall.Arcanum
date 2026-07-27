using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class StructuredOutputValidatorTests
{

    [Fact]
    public void ValidateAndRetryAsync_has_no_retry_count_parameter()
    {
        System.Reflection.MethodInfo method = Assert.Single(
            typeof(StructuredOutputValidator).GetMethods(),
            static candidate =>
                candidate.Name == nameof(StructuredOutputValidator.ValidateAndRetryAsync));

        Assert.DoesNotContain(
            method.GetParameters(),
            static parameter => string.Equals(
                parameter.Name,
                "maxRetries",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ValidateAndRetryAsync_ValidResponse_ReturnsSuccessWithoutWarnings()
    {

        using JsonDocument schema = JsonDocument.Parse("""
            {"type": "object", "properties": {"name": {"type": "string"}}, "required": ["name"]}
            """);

        ChatResponse response = CreateResponse("""{"name": "Alice"}""", promptTokens: 10);

        StructuredOutputValidator validator = new();

        Result<StructuredOutputResult> result = await validator.ValidateAndRetryAsync(
            response,
            schema,
            strictMode: false,
            schemaMaxDepth: 10,
            contextWindowLimit: 1000,
            estimateTokenCount: null,
            resendAsync: (_, _) => throw new InvalidOperationException("should not be called"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.True(result.Value.IsValid);

        Assert.Empty(result.Value.Warnings);

    }

    [Fact]
    public async Task ValidateAndRetryAsync_InvalidResponse_RetriesAndReturnsSuccess()
    {

        using JsonDocument schema = JsonDocument.Parse("""
            {"type": "object", "properties": {"name": {"type": "string"}}, "required": ["name"]}
            """);

        ChatResponse invalidResponse = CreateResponse("""{"age": 30}""", promptTokens: 10);

        ChatResponse validResponse = CreateResponse("""{"name": "Alice"}""", promptTokens: 30);

        StructuredOutputValidator validator = new();

        int resendCount = 0;

        Result<StructuredOutputResult> result = await validator.ValidateAndRetryAsync(
            invalidResponse,
            schema,
            strictMode: false,
            schemaMaxDepth: 10,
            contextWindowLimit: 1000,
            estimateTokenCount: null,
            resendAsync: (_, _) =>
            {

                resendCount++;

                return Task.FromResult(validResponse);

            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.True(result.Value.IsValid);

        Assert.Equal(1, resendCount);

    }

    [Fact]
    public async Task ValidateAndRetryAsync_ContextWindowTooSmall_SkipsRetryAndReturnsWarning()
    {

        using JsonDocument schema = JsonDocument.Parse("""
            {"type": "object", "properties": {"name": {"type": "string"}}, "required": ["name"]}
            """);

        ChatResponse response = CreateResponse("""{"age": 30}""", promptTokens: 1000);

        StructuredOutputValidator validator = new();

        int resendCount = 0;

        Result<StructuredOutputResult> result = await validator.ValidateAndRetryAsync(
            response,
            schema,
            strictMode: false,
            schemaMaxDepth: 10,
            contextWindowLimit: 1000,
            estimateTokenCount: text => text.Length / 4,
            resendAsync: (_, _) =>
            {

                resendCount++;

                return Task.FromResult(response);

            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(0, resendCount);

        Assert.Contains(result.Value.Warnings, w => w.Contains("context window too small", StringComparison.OrdinalIgnoreCase));

    }

    [Fact]
    public async Task ValidateAndRetryAsync_RepeatedInvalidState_StopsDeterministically()
    {
        using JsonDocument schema = JsonDocument.Parse("""
            {"type": "object", "properties": {"name": {"type": "string"}}, "required": ["name"]}
            """);
        ChatResponse repeatedInvalid = CreateResponse("""{"age": 30}""", promptTokens: 10);
        StructuredOutputValidator validator = new();
        int resendCount = 0;

        Result<StructuredOutputResult> result = await validator.ValidateAndRetryAsync(
            repeatedInvalid,
            schema,
            strictMode: false,
            schemaMaxDepth: 10,
            contextWindowLimit: 1000,
            estimateTokenCount: null,
            resendAsync: (_, _) =>
            {
                resendCount++;
                return Task.FromResult(repeatedInvalid);
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.IsValid);
        Assert.Equal(1, resendCount);
        Assert.Contains(
            result.Value.Warnings,
            static warning => warning.Contains(
                "state repeated",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ValidateAndRetryAsync_RepeatedInvalidState_StrictModeReturnsValidationFailure()
    {
        using JsonDocument schema = JsonDocument.Parse("""
            {"type": "object", "properties": {"name": {"type": "string"}}, "required": ["name"]}
            """);
        ChatResponse repeatedInvalid = CreateResponse("""{"age": 30}""", promptTokens: 10);
        StructuredOutputValidator validator = new();
        int resendCount = 0;

        Result<StructuredOutputResult> result = await validator.ValidateAndRetryAsync(
            repeatedInvalid,
            schema,
            strictMode: true,
            schemaMaxDepth: 10,
            contextWindowLimit: 1000,
            estimateTokenCount: null,
            resendAsync: (_, _) =>
            {
                resendCount++;
                return Task.FromResult(repeatedInvalid);
            },
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.StructuredOutput.ValidationFailed, result.Error.Code);
        Assert.Equal(1, resendCount);
    }

    [Fact]
    public async Task ValidateAndRetryAsync_InvalidSchema_ReturnsSchemaInvalidFailure()
    {

        using JsonDocument schema = JsonDocument.Parse("""{"type": "object", "properties": {"a": {"type": "object", "properties": {"b": {"type": "string"}}}}}""");

        ChatResponse response = CreateResponse("""{}""", promptTokens: 10);

        StructuredOutputValidator validator = new();

        Result<StructuredOutputResult> result = await validator.ValidateAndRetryAsync(
            response,
            schema,
            strictMode: false,
            schemaMaxDepth: 1,
            contextWindowLimit: 1000,
            estimateTokenCount: null,
            resendAsync: (_, _) => Task.FromResult(response),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.StructuredOutput.SchemaInvalid, result.Error.Code);

    }

    private static ChatResponse CreateResponse(string text, int promptTokens)
    {

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {

            Usage = new UsageDetails { InputTokenCount = promptTokens }

        };

    }

}
