using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
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
/// OpenAI-compatible <c>POST /v1/embeddings</c>. Thin composition over The Weave infrastructure —
/// <see cref="IWeaveService"/> (imprinting, chunking) and
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
            // ForRawBody, not ForBoundArgument: the handler reads the body itself (see below), so
            // there is no bound argument to fingerprint. Buffers and rewinds the request the same
            // way /v1/chat/completions does.
            .AddEndpointFilter(IdempotencyEndpointFilters.ForRawBody);
    }

    private static async Task<IResult> HandleEmbeddingsAsync(
        HttpContext httpContext,
        IWeaveService weave,
        InferenceTokenizerResolver tokenizerResolver,
        IOptionsSnapshot<ArcanumSettings> settings,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {

        ArcanumSettings arc = settings.Value;

        EmbeddingSettings embeddings = arc.ResolveEmbeddings();

        OpenAiEmbeddingRequest? body = null;

        // Read the body by hand rather than binding it as a route-handler parameter. RequestDelegateFactory
        // swallows the JsonException and completes the request with an empty 400 (and an empty 415 for a
        // non-JSON Content-Type) before this handler runs, so §8.23's OpenAI error envelope would never be
        // written — and OpenAiEmbeddingInputConverter's deliberately specific "'input' must be a string, an
        // array of strings..." diagnostic would be thrown away unread. Same treatment as
        // HandleChatCompletionsAsync and HandleCreateBatchAsync.
        //
        // A request with no body at all is left to the missing_required_parameter arm below, exactly
        // where it landed before: only an actual non-JSON body is a 415.
        if (httpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody ?? true)
        {

            if (!httpContext.Request.HasJsonContentType())
            {
                return CreateUnsupportedMediaTypeErrorResult();
            }

            try
            {
                body = await httpContext.Request
                    .ReadFromJsonAsync(ArcanumJsonContext.Default.OpenAiEmbeddingRequest, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (JsonException exception)
            {
                // Echoing the parse message is safe here and nowhere else on this surface: it is derived
                // solely from the caller's own request body, never from a provider response. It is also the
                // point of OpenAiEmbeddingInputConverter's hand-written "'input' must be a string, an array
                // of strings..." diagnostic, which a fixed message would discard.
                return JsonError(
                    exception.Message,
                    "invalid_request_error",
                    code: "invalid_json",
                    param: null,
                    statusCode: StatusCodes.Status400BadRequest);
            }
            catch (InvalidOperationException)
            {
                return CreateUnsupportedMediaTypeErrorResult();
            }
            catch (BadHttpRequestException exception)
            {
                return CreateRequestBodyReadErrorResult(exception.StatusCode);
            }

        }

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
                "Embeddings are disabled or not fully configured on this server (Arcanum:Features:Embeddings, Arcanum:Integrations:Embeddings:Provider, and Arcanum:Integrations:Embeddings:Model).",
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

        Tokenizer tokenizer = tokenizerResolver.ResolveTokenizer(
            arc.ResolveIntelligence().TokenizerEncoding);

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
                $"Total 'input' size ({totalInputChars} chars) exceeds the internal limit ({maxInputChars} chars).",
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

            // OpenAI-compatible providers are not guaranteed to answer with one vector per input
            // (and Microsoft.Extensions.AI does not enforce it), so a short array must fail as a
            // sanitized 503 rather than indexing off the end of the batch.
            if (batchResult.Value.Length != shortTexts.Count)
            {

                return WeaveErrorToOpenAiResult(new Error(
                    ErrorCodes.Embeddings.ProviderUnavailable,
                    "The embedding provider returned a mismatched number of vectors."));

            }

            // Ragged widths never throw on this path — each vector lands in its own slot — so an
            // unguarded ragged batch is emitted as one 200 whose data[] rows have different vector
            // lengths, which violates the OpenAI embeddings contract with no signal at all.
            int shortWidth = batchResult.Value[0].Vector.Length;

            if (Array.Exists(batchResult.Value, embedding => embedding.Vector.Length != shortWidth))
            {

                return WeaveErrorToOpenAiResult(new Error(
                    ErrorCodes.Embeddings.ProviderUnavailable,
                    "The embedding provider returned vectors of inconsistent dimensions."));

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

        // The two guards above each police one round trip: the short-input batch, and the chunk batch
        // behind a single long input. A request spans several of those — one for the short inputs plus
        // one per long input — so a load-balanced or mid-rollout pool can answer every batch at a
        // consistent width and still leave this response ragged (a 1,536-wide short vector beside a
        // 3,072-wide mean-pooled long one). Nothing downstream would notice: each vector lands in its
        // own data[] slot, so the request answers 200 carrying rows of two widths, which is the same
        // contract violation the per-batch guards exist to prevent. The response is only well-formed
        // once every vector in it is the same width, so that is where the assertion belongs.
        int responseWidth = resultVectors[0]!.Length;

        if (Array.Exists(resultVectors, vector => vector!.Length != responseWidth))
        {

            return WeaveErrorToOpenAiResult(new Error(
                ErrorCodes.Embeddings.ProviderUnavailable,
                "The embedding provider returned vectors of inconsistent dimensions."));

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
    /// Embeds a single input whose length exceeds the code-owned chunk size by splitting it via
    /// <see cref="IWeaveService.ChunkAsync"/>, embedding every chunk, then
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

        // A chunk batch that comes back short would silently mean-pool fewer chunks than the
        // document actually has (an empty one would throw), so treat any count mismatch as a
        // provider failure instead.
        if (batch.Value.Length == 0 || batch.Value.Length != chunkTexts.Length)
        {

            return Result<float[]>.Failure(new Error(
                ErrorCodes.Embeddings.ProviderUnavailable,
                "The embedding provider returned a mismatched number of vectors."));

        }

        // The same distrust applied to the vector count applies to the vector width. WeaveService
        // issues one round trip per BatchSize sub-batch, so a load-balanced or mid-rollout provider
        // pool can answer one document with vectors of two widths while each HTTP response is
        // well-formed. MeanPoolAndNormalize pins its dimension count to the first vector: a narrower
        // later vector reads off its end, and a wider one is silently truncated, mean-pooled and
        // renormalized into a plausible-looking but wrong unit vector — the worse of the two, since
        // it poisons whatever vector store the caller writes it into.
        int width = batch.Value[0].Vector.Length;

        if (Array.Exists(batch.Value, embedding => embedding.Vector.Length != width))
        {

            return Result<float[]>.Failure(new Error(
                ErrorCodes.Embeddings.ProviderUnavailable,
                "The embedding provider returned vectors of inconsistent dimensions."));

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
