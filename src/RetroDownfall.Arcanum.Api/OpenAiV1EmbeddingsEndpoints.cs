using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.Tokenizers;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Api;

/// <summary>
/// OpenAI-compatible <c>POST /v1/embeddings</c>. Thin composition over the existing RAG Phase 1
/// infrastructure — <see cref="IWeaveService"/> (imprinting, chunking) and
/// <see cref="InferenceTokenizerResolver"/> (usage token counting) — following the same
/// error-envelope and validation conventions as <c>HandleChatCompletionsAsync</c>.
/// (<see cref="ExcludeFromCodeCoverageAttribute"/> is applied once on the primary
/// <c>OpenAiV1Endpoints.cs</c> partial declaration and covers this file too.)
/// </summary>
internal static partial class OpenAiV1Endpoints
{

    internal static void MapOpenAiV1Embeddings(this RouteGroupBuilder v1)
    {
        _ = v1.MapPost("/embeddings", HandleEmbeddingsAsync)
            .WithName("PostOpenAiEmbeddings")
            .WithLargeRequestBody()
            .AddEndpointFilter(IdempotencyEndpointFilters.ForBoundArgument(0, ArcanumJsonContext.Default.OpenAiEmbeddingRequest));
    }

    private static async Task<IResult> HandleEmbeddingsAsync(
        OpenAiEmbeddingRequest? body,
        IWeaveService weave,
        InferenceTokenizerResolver tokenizerResolver,
        IOptionsSnapshot<ArcanumSettings> settings,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {

        ArcanumSettings arc = settings.Value;

        EmbeddingSettings embeddings = arc.Embeddings ?? new EmbeddingSettings();

        if (body is null || body.Input is null)
        {

            return JsonError(
                "Missing required parameter: 'input'.",
                "invalid_request_error",
                "missing_required_parameter",
                "input",
                StatusCodes.Status400BadRequest);

        }

        string encodingFormat = string.IsNullOrWhiteSpace(body.EncodingFormat)
            ? "float"
            : body.EncodingFormat.Trim().ToLowerInvariant();

        if (encodingFormat is not ("float" or "base64"))
        {

            return JsonError(
                $"'encoding_format' must be 'float' or 'base64' (got '{body.EncodingFormat}').",
                "invalid_request_error",
                "invalid_value",
                "encoding_format",
                StatusCodes.Status400BadRequest);

        }

        // Availability gate first: with embeddings disabled/misconfigured there is no configured
        // model to compare against, so every request fails the same way regardless of `model`.
        if (!weave.IsAvailable)
        {

            return JsonError(
                "Embeddings are disabled or not fully configured on this server (Arcanum:Embeddings:Enabled/Provider/Model).",
                "server_error",
                "embedding_provider_unavailable",
                param: null,
                StatusCodes.Status503ServiceUnavailable);

        }

        string configuredModel = embeddings.Model ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(body.Model) && !string.Equals(body.Model, configuredModel, StringComparison.OrdinalIgnoreCase))
        {

            return JsonError(
                $"The model '{body.Model}' does not match this server's configured embedding model.",
                "invalid_request_error",
                "model_not_found",
                "model",
                StatusCodes.Status404NotFound);

        }

        string echoModel = string.IsNullOrWhiteSpace(body.Model) ? configuredModel : body.Model;

        ILogger logger = loggerFactory.CreateLogger(typeof(OpenAiV1Endpoints));

        Tokenizer tokenizer = tokenizerResolver.ResolveTokenizer(arc.Intelligence.TokenizerEncoding);

        string[] inputs = ResolveInputTexts(body.Input, tokenizer);

        if (inputs.Length == 0 || Array.Exists(inputs, string.IsNullOrEmpty))
        {

            return JsonError(
                "'input' must not be empty, and must not contain empty strings.",
                "invalid_request_error",
                "invalid_value",
                "input",
                StatusCodes.Status400BadRequest);

        }

        int maxInputChars = ArcanumSettingClamps.EmbeddingsMaxEmbeddingInputChars(embeddings.MaxEmbeddingInputChars);

        long totalInputChars = 0L;

        foreach (string text in inputs)
        {

            totalInputChars += text.Length;

        }

        if (totalInputChars > maxInputChars)
        {

            return JsonError(
                $"Total 'input' size ({totalInputChars} chars) exceeds the configured limit ({maxInputChars} chars, Arcanum:Embeddings:MaxEmbeddingInputChars).",
                "invalid_request_error",
                "invalid_value",
                "input",
                StatusCodes.Status400BadRequest);

        }

        if (body.Dimensions.HasValue)
        {

            // Most embedding providers/models do not support server-side dimension truncation;
            // Arcanum does not attempt to slice the returned vector, to avoid silently corrupting
            // the vector's L2 norm (naive truncation is not equivalent to a model retrained/
            // fine-tuned for Matryoshka-style truncation). Logged once per request, not per input.
            logger.LogWarning(
                "POST /v1/embeddings requested 'dimensions: {Dimensions}', but Arcanum does not support provider-side dimension truncation; the full embedding vector is returned.",
                body.Dimensions.Value);

        }

        int chunkSizeChars = ArcanumSettingClamps.EmbeddingsChunkSizeChars(embeddings.ChunkSizeChars);

        float[]?[] resultVectors = new float[]?[inputs.Length];

        List<int> shortIndexes = [];

        List<string> shortTexts = [];

        for (int i = 0; i < inputs.Length; i++)
        {

            if (inputs[i].Length > chunkSizeChars)
            {

                continue;

            }

            shortIndexes.Add(i);

            shortTexts.Add(inputs[i]);

        }

        if (shortTexts.Count > 0)
        {

            Result<Embedding<float>[]> batchResult = await weave.EmbedBatchAsync(shortTexts, cancellationToken).ConfigureAwait(false);

            if (batchResult.IsFailure)
            {

                return WeaveErrorToOpenAiResult(batchResult.Error);

            }

            for (int i = 0; i < shortIndexes.Count; i++)
            {

                resultVectors[shortIndexes[i]] = batchResult.Value[i].Vector.ToArray();

            }

        }

        for (int i = 0; i < inputs.Length; i++)
        {

            if (resultVectors[i] is not null)
            {

                continue;

            }

            Result<float[]> longResult = await EmbedLongInputAsync(inputs[i], weave, cancellationToken).ConfigureAwait(false);

            if (longResult.IsFailure)
            {

                return WeaveErrorToOpenAiResult(longResult.Error);

            }

            resultVectors[i] = longResult.Value;

        }

        List<OpenAiEmbeddingData> data = new(inputs.Length);

        for (int i = 0; i < inputs.Length; i++)
        {

            float[] vector = resultVectors[i]!;

            OpenAiEmbeddingVector wireVector = encodingFormat == "base64"
                ? OpenAiEmbeddingVector.FromBase64(Convert.ToBase64String(EmbeddingBlobCodec.Encode(vector)))
                : OpenAiEmbeddingVector.FromFloats(vector);

            data.Add(new OpenAiEmbeddingData("embedding", i, wireVector));

        }

        int totalTokens = 0;

        foreach (string text in inputs)
        {

            totalTokens += tokenizer.CountTokens(text);

        }

        OpenAiEmbeddingResponse response = new(
            "list",
            data,
            echoModel,
            new OpenAiEmbeddingUsage(totalTokens, totalTokens));

        return Results.Json(response, ArcanumJsonContext.Default.OpenAiEmbeddingResponse);

    }

    /// <summary>
    /// Resolves OpenAI's polymorphic <c>input</c> shapes to plain strings. String inputs pass
    /// through unchanged. Pre-tokenized (<c>int[]</c>/<c>int[][]</c>) inputs are decoded back to
    /// text using Arcanum's configured tokenizer encoding — Arcanum always forwards text (never
    /// raw token ids) to the configured embedding provider, so this is the closest reproduction of
    /// the caller's intent achievable without knowing which vocabulary originally produced the ids.
    /// </summary>
    private static string[] ResolveInputTexts(OpenAiEmbeddingInput input, Tokenizer tokenizer)
    {

        if (input.Strings is { } strings)
        {

            return strings;

        }

        if (input.TokenArrays is { } tokenArrays)
        {

            string[] decoded = new string[tokenArrays.Length];

            for (int i = 0; i < tokenArrays.Length; i++)
            {

                decoded[i] = tokenizer.Decode(tokenArrays[i]) ?? string.Empty;

            }

            return decoded;

        }

        return [];

    }

    /// <summary>
    /// Embeds a single input whose length exceeds <c>Arcanum:Embeddings:ChunkSizeChars</c> by
    /// splitting it via <see cref="IWeaveService.ChunkAsync"/>, embedding every chunk, then
    /// mean-pooling and L2-renormalizing the chunk vectors into a single representative vector —
    /// preserving OpenAI's one-embedding-per-input contract while still imprinting the whole
    /// document (rather than silently truncating it, which <see cref="IWeaveService.EmbedBatchAsync"/>
    /// would otherwise do per-call as defense-in-depth).
    /// </summary>
    private static async Task<Result<float[]>> EmbedLongInputAsync(
        string text,
        IWeaveService weave,
        CancellationToken cancellationToken)
    {

        Result<(string Chunk, int Offset)[]> chunked = await weave.ChunkAsync(text, cancellationToken).ConfigureAwait(false);

        if (chunked.IsFailure)
        {

            return Result<float[]>.Failure(chunked.Error);

        }

        string[] chunkTexts = new string[chunked.Value.Length];

        for (int i = 0; i < chunked.Value.Length; i++)
        {

            chunkTexts[i] = chunked.Value[i].Chunk;

        }

        Result<Embedding<float>[]> batch = await weave.EmbedBatchAsync(chunkTexts, cancellationToken).ConfigureAwait(false);

        if (batch.IsFailure)
        {

            return Result<float[]>.Failure(batch.Error);

        }

        return Result<float[]>.Success(MeanPoolAndNormalize(batch.Value));

    }

    private static float[] MeanPoolAndNormalize(Embedding<float>[] embeddings)
    {

        int dimensions = embeddings[0].Vector.Length;

        double[] sum = new double[dimensions];

        foreach (Embedding<float> embedding in embeddings)
        {

            ReadOnlySpan<float> vector = embedding.Vector.Span;

            for (int i = 0; i < dimensions; i++)
            {

                sum[i] += vector[i];

            }

        }

        float[] mean = new float[dimensions];

        for (int i = 0; i < dimensions; i++)
        {

            mean[i] = (float)(sum[i] / embeddings.Length);

        }

        double normSquared = 0;

        foreach (float value in mean)
        {

            normSquared += (double)value * value;

        }

        if (normSquared <= 0)
        {

            return mean;

        }

        double norm = Math.Sqrt(normSquared);

        for (int i = 0; i < dimensions; i++)
        {

            mean[i] = (float)(mean[i] / norm);

        }

        return mean;

    }

    /// <summary>
    /// Both <see cref="ErrorCodes.Embeddings.FeatureDisabled"/> (should not normally occur here —
    /// already checked via <see cref="IWeaveService.IsAvailable"/> — but Arcanum's settings can hot
    /// reload between the check and the call) and <see cref="ErrorCodes.Embeddings.ProviderUnavailable"/>
    /// map to the same OpenAI-shaped <c>503 embedding_provider_unavailable</c> response.
    /// </summary>
    private static IResult WeaveErrorToOpenAiResult(Error error) =>
        JsonError(
            error.Message,
            "server_error",
            "embedding_provider_unavailable",
            param: null,
            StatusCodes.Status503ServiceUnavailable);

}
