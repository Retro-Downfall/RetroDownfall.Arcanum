using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Validates buffered inference responses against a JSON Schema and retries when validation fails.
/// Stateless singleton — all configuration and retry state flows through method parameters.
/// </summary>
public sealed class StructuredOutputValidator
{

    /// <summary>
    /// Validates <paramref name="initialResponse"/> against the supplied JSON Schema. If validation
    /// fails and retries remain, the provider is asked to try again with a corrective system message.
    /// On exhaustion, the behavior depends on <paramref name="strictMode"/>:
    /// <see langword="false"/> returns the last response with a warning; <see langword="true"/>
    /// returns a <see cref="ErrorCodes.StructuredOutput.ValidationFailed"/> failure.
    /// </summary>
    /// <param name="initialResponse">The first provider response.</param>
    /// <param name="schema">The JSON Schema document to validate against.</param>
    /// <param name="maxRetries">Maximum retry attempts after the first failure.</param>
    /// <param name="strictMode">When <see langword="true"/>, exhausted retries become a hard failure.</param>
    /// <param name="schemaMaxDepth">Maximum recursion depth for parsing/validation.</param>
    /// <param name="contextWindowLimit">Provider context-window limit (tokens). Used to avoid retrying when the appended error message would not fit.</param>
    /// <param name="estimateTokenCount">
    /// Optional tokenizer callback. When <see langword="null"/> or when it throws, a conservative
    /// <c>text.Length / 4</c> heuristic is used to estimate the error-message token count.
    /// </param>
    /// <param name="resendAsync">
    /// Callback that receives the corrective error message and returns a new provider response.
    /// The caller is responsible for appending the message to the conversation and re-invoking the model.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A successful result containing the final response and any warnings, or a failure with
    /// <see cref="ErrorCodes.StructuredOutput.ValidationFailed"/> when strict mode is enabled and
    /// validation could not be satisfied.
    /// </returns>
    public async Task<Result<StructuredOutputResult>> ValidateAndRetryAsync(
        ChatResponse initialResponse,
        JsonDocument schema,
        int maxRetries,
        bool strictMode,
        int schemaMaxDepth,
        int contextWindowLimit,
        Func<string, int>? estimateTokenCount,
        Func<string, CancellationToken, Task<ChatResponse>> resendAsync,
        CancellationToken cancellationToken)
    {

        Result<JsonSchemaDefinition> parseResult = JsonSchemaHelper.Parse(schema, schemaMaxDepth);

        if (parseResult.IsFailure)
        {

            return Result<StructuredOutputResult>.Failure(
                new Error(ErrorCodes.StructuredOutput.SchemaInvalid, $"Invalid response schema: {parseResult.Error.Message}"));

        }

        JsonSchemaDefinition definition = parseResult.Value;

        string schemaRawText = schema.RootElement.GetRawText();

        ChatResponse currentResponse = initialResponse;

        int currentPromptTokens = ClampUsageToInt(currentResponse.Usage?.InputTokenCount ?? 0L);

        List<string> warnings = [];

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {

            ValidationResult validation = JsonSchemaHelper.Validate(
                currentResponse.Text,
                definition,
                schemaMaxDepth);

            if (validation.IsValid)
            {

                return Result<StructuredOutputResult>.Success(
                    new StructuredOutputResult(currentResponse, warnings, IsValid: true));

            }

            if (attempt >= maxRetries)
            {

                break;

            }

            string errorMessage =
                $"Your previous response did not match the required JSON schema. " +
                $"Errors: {string.Join("; ", validation.Errors)}. " +
                $"Please respond with valid JSON matching this schema: {schemaRawText}.";

            int estimatedErrorTokens = EstimateTokenCount(errorMessage, estimateTokenCount);

            if (currentPromptTokens + estimatedErrorTokens > contextWindowLimit)
            {

                warnings.Add("context window too small for retry; skipping structured-output retry.");

                break;

            }

            currentResponse = await resendAsync(errorMessage, cancellationToken).ConfigureAwait(false);

            currentPromptTokens = ClampUsageToInt(currentResponse.Usage?.InputTokenCount ?? 0L);

            warnings.Add($"Retry {attempt + 1} triggered by validation failure.");

        }

        ValidationResult finalValidation = JsonSchemaHelper.Validate(
            currentResponse.Text,
            definition,
            schemaMaxDepth);

        if (finalValidation.IsValid)
        {

            return Result<StructuredOutputResult>.Success(
                new StructuredOutputResult(currentResponse, warnings, IsValid: true));

        }

        string joinedErrors = string.Join("; ", finalValidation.Errors);

        if (strictMode)
        {

            return Result<StructuredOutputResult>.Failure(
                new Error(
                    ErrorCodes.StructuredOutput.ValidationFailed,
                    $"Response failed JSON schema validation after {maxRetries} retries: {joinedErrors}"));

        }

        warnings.Add($"validation failed after {maxRetries} retries: {joinedErrors}");

        return Result<StructuredOutputResult>.Success(
            new StructuredOutputResult(currentResponse, warnings, IsValid: false));

    }

    private static int EstimateTokenCount(string text, Func<string, int>? estimateTokenCount)
    {

        if (estimateTokenCount is not null)
        {

            try
            {

                return estimateTokenCount(text);

            }
            catch
            {

                // Conservative fallback if the tokenizer fails or is unavailable.

            }

        }

        return Math.Max(1, text.Length / 4);

    }

    private static int ClampUsageToInt(long value)
    {

        return value > int.MaxValue ? int.MaxValue : (int)value;

    }

}

/// <summary>
/// Result of structured-output validation, including the final provider response and any warnings.
/// </summary>
public sealed record StructuredOutputResult(
    ChatResponse Response,
    IReadOnlyList<string> Warnings,
    bool IsValid);
