using System.ClientModel;
using System.Diagnostics;
using System.Net.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Imprints text into The Weave via <see cref="IEmbeddingGeneratorFactory"/>. Never throws for expected
/// failure modes (feature disabled, provider unreachable, or genuine per-call provider error): callers
/// receive a <see cref="Result{T}"/> and are expected to degrade gracefully. Cancellation from the supplied
/// <see cref="CancellationToken"/> propagates as <see cref="OperationCanceledException"/>, per standard
/// .NET convention. Arcanum adds no whole-operation embedding deadline.
/// </summary>
public sealed class WeaveService(
    IEmbeddingGeneratorFactory generatorFactory,
    IOptionsMonitor<ArcanumSettings> optionsMonitor,
    IServiceScopeFactory scopeFactory,
    ILogger<WeaveService> logger) : IWeaveService
{

    /// <summary>
    /// Computed fresh on every access from <see cref="IOptionsMonitor{ArcanumSettings}.CurrentValue"/> —
    /// no <c>OnChange</c> registration (avoids leak/fragility risk); cheap, since the monitor holds a
    /// cached snapshot, and hot-reload friendly, since the next access always sees current config. Same
    /// singleton pattern as <c>McpConnectionManager</c> / <c>EyeOfTheWorldService</c>.
    /// </summary>
    public bool IsAvailable
    {
        get
        {

            EmbeddingSettings embeddings = optionsMonitor.CurrentValue.ResolveEmbeddings();

            return embeddings.Enabled
                && !string.IsNullOrWhiteSpace(embeddings.Provider)
                && !string.IsNullOrWhiteSpace(embeddings.Model);

        }
    }

    public async Task<Result<Embedding<float>>> EmbedAsync(string text, CancellationToken cancellationToken)
    {

        Result<Embedding<float>[]> batchResult = await EmbedBatchAsync([text], cancellationToken).ConfigureAwait(false);

        if (batchResult.IsFailure)
        {
            return Result<Embedding<float>>.Failure(batchResult.Error);

        }

        return Result<Embedding<float>>.Success(batchResult.Value[0]);

    }

    public async Task<Result<Embedding<float>[]>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {

        // Disabled-path (no generator resolution, no HTTP call, no exception): checked first, before
        // anything else in this method runs.
        if (!IsAvailable)
        {

            return Result<Embedding<float>[]>.Failure(new Error(
                ErrorCodes.Embeddings.FeatureDisabled,
                "Embeddings are disabled or incomplete (enable an embedding-backed Arcanum:Features option and configure Arcanum:Integrations:Embeddings:Provider and Arcanum:Integrations:Embeddings:Model)."));

        }

        if (texts.Count == 0)
        {
            return Result<Embedding<float>[]>.Success([]);

        }

        EmbeddingSettings embeddings = optionsMonitor.CurrentValue.ResolveEmbeddings();

        int batchSize = ArcanumSettingClamps.EmbeddingsBatchSize(embeddings.BatchSize);

        int chunkSizeChars = ArcanumSettingClamps.EmbeddingsChunkSizeChars(embeddings.ChunkSizeChars);

        List<List<string>> sanitizedBatches = [];

        for (int offset = 0; offset < texts.Count; offset += batchSize)
        {
            int count = Math.Min(batchSize, texts.Count - offset);
            List<string> batch = new(count);

            for (int i = 0; i < count; i++)
            {
                string text = texts[offset + i];
                batch.Add(text.Length > chunkSizeChars
                    ? text[..Utf8Truncation.SafeCharSliceLength(text, chunkSizeChars)]
                    : text);
            }

            sanitizedBatches.Add(batch);
        }

        long totalApproxTokens = EstimateApproximateTokens(sanitizedBatches.SelectMany(static batch => batch));
        PricingSettings pricingSettings = optionsMonitor.CurrentValue.ResolvePricing();
        ModelPricingEntry pricing = pricingSettings.ResolveForModel(embeddings.Model);

        string provider = embeddings.Provider ?? "unknown";
        string model = embeddings.Model ?? "unknown";
        IServiceScope? accountingScope = null;
        ITurnRunWriter? turnRunWriter = TurnAccountingAmbient.Writer;
        IBudgetReservationService? budgetReservations = null;
        TurnAccountingHandle? accounting = TurnAccountingAmbient.Current;
        bool ownsAccounting = false;

        if (accounting is null)
        {
            accountingScope = scopeFactory.CreateScope();
            turnRunWriter = accountingScope.ServiceProvider.GetService<ITurnRunWriter>();
            budgetReservations = accountingScope.ServiceProvider.GetService<IBudgetReservationService>();
            decimal reservedUsd = BudgetReservationService.EstimateWorstCaseEmbeddingUsd(pricing, totalApproxTokens);
            Result<TurnAccountingHandle> begin = await TurnAccountingHandle.BeginAsync(
                    turnRunWriter,
                    budgetReservations,
                    pricingSettings,
                    model,
                    sessionId: null,
                    surface: "embedding",
                    purpose: "embedding",
                    requestId: Activity.Current?.Id ?? Guid.NewGuid().ToString("N"),
                    cancellationToken,
                    reservedUsdOverride: reservedUsd)
                .ConfigureAwait(false);

            if (begin.IsFailure)
            {
                accountingScope.Dispose();
                return Result<Embedding<float>[]>.Failure(begin.Error);
            }

            accounting = begin.Value;
            ownsAccounting = true;
        }

        List<Embedding<float>> results = new(texts.Count);
        InferenceRunStatus accountingStatus = InferenceRunStatus.Failed;

        try
        {

            // Sequential, not parallel — avoids overwhelming local providers (e.g. Ollama).
            foreach (List<string> batch in sanitizedBatches)
            {
                Embedding<float>[] batchEmbeddings = await EmbedOneBatchAsync(
                    batch,
                    cancellationToken).ConfigureAwait(false);

                results.AddRange(batchEmbeddings);

                await accounting.RecordUsageAsync(
                        turnRunWriter,
                        BillableOperationType.Embedding,
                        provider,
                        model,
                        purpose: "embedding",
                        EstimateApproximateTokens(batch),
                        outputTokens: 0,
                        cachedTokens: 0,
                        reasoningTokens: 0,
                        pricing,
                        CancellationToken.None)
                    .ConfigureAwait(false);

            }

            accountingStatus = InferenceRunStatus.Completed;
            return Result<Embedding<float>[]>.Success([.. results]);

        }
        catch (ClientResultException ex) when (IsPayloadOrRequestSizeError(ex.Status))
        {

            // OpenAI-SDK-shaped providers (OpenAI, DeepSeek, most self-hosted OpenAI-compatible
            // servers) surface HTTP error responses as ClientResultException, not HttpRequestException.
            logger.LogWarning(
                ex,
                "Embedding provider rejected a batch already bounded by code-owned chunk/batch limits (HTTP {StatusCode}).",
                ex.Status);

            return Result<Embedding<float>[]>.Failure(new Error(
                ErrorCodes.Embeddings.ProviderUnavailable,
                "The embedding provider rejected the request as too large. See server logs for detail."));

        }
        catch (HttpRequestException ex) when (IsPayloadOrRequestSizeError((int?)ex.StatusCode ?? 0))
        {

            // Defense-in-depth for providers/transports that surface a raw HttpRequestException with a
            // populated StatusCode instead of the SDK's ClientResultException.
            logger.LogWarning(
                ex,
                "Embedding provider rejected a batch already bounded by code-owned chunk/batch limits (HTTP {StatusCode}).",
                ex.StatusCode);

            return Result<Embedding<float>[]>.Failure(new Error(
                ErrorCodes.Embeddings.ProviderUnavailable,
                "The embedding provider rejected the request as too large. See server logs for detail."));

        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {

            logger.LogWarning(ex, "Embedding provider call failed; The Weave will be treated as unavailable for this request.");

            return Result<Embedding<float>[]>.Failure(new Error(
                ErrorCodes.Embeddings.ProviderUnavailable,
                "The embedding provider is unavailable. See server logs for detail."));

        }
        finally
        {
            if (ownsAccounting)
            {
                try
                {
                    await accounting.CompleteAsync(
                            turnRunWriter,
                            budgetReservations,
                            accountingStatus,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Failed to complete standalone embedding accounting.");
                }

                accountingScope?.Dispose();
            }
        }

    }

    private static long EstimateApproximateTokens(IEnumerable<string> texts)
    {
        long total = 0L;

        foreach (string text in texts)
        {
            total += Math.Max(1L, (text.Length + 3L) / 4L);
        }

        return total;
    }

    /// <summary>
    /// <c>413 Payload Too Large</c> and <c>400 Bad Request</c> are the two statuses upstream OpenAI-
    /// compatible providers (OpenAI, DeepSeek, Ollama) use to reject an oversized embedding batch or
    /// a single input exceeding the provider's context/dimension limit.
    /// </summary>
    private static bool IsPayloadOrRequestSizeError(int statusCode) =>
        statusCode is 413 or 400;

    private async Task<Embedding<float>[]> EmbedOneBatchAsync(
        List<string> batch,
        CancellationToken cancellationToken)
    {

        using EmbeddingGeneratorLease lease = await generatorFactory
            .ResolveGeneratorAsync(cancellationToken)
            .ConfigureAwait(false);

        GeneratedEmbeddings<Embedding<float>> generated = await lease.Generator
            .GenerateAsync(batch, options: null, cancellationToken)
            .ConfigureAwait(false);

        Embedding<float>[] embeddings = new Embedding<float>[generated.Count];

        for (int i = 0; i < generated.Count; i++)
        {
            embeddings[i] = generated[i];

        }

        return embeddings;

    }

    public Task<Result<(string Chunk, int Offset)[]>> ChunkAsync(string text, CancellationToken cancellationToken)
    {

        // Pure CPU — always runs regardless of IsAvailable (no generator involved).
        if (string.IsNullOrEmpty(text))
        {
            return Task.FromResult(Result<(string Chunk, int Offset)[]>.Success([]));

        }

        EmbeddingSettings embeddings = optionsMonitor.CurrentValue.ResolveEmbeddings();

        int chunkSizeChars = ArcanumSettingClamps.EmbeddingsChunkSizeChars(embeddings.ChunkSizeChars);

        int chunkOverlapChars = ArcanumSettingClamps.EmbeddingsChunkOverlapChars(embeddings.ChunkOverlapChars);

        // Chunking intentionally uses a code-owned naive sliding window with no sentence or word
        // boundary detection, so a chunk boundary can fall mid-word. See
        // docs/Arcanum.DESIGN.md §21.
        int step = Math.Max(1, chunkSizeChars - chunkOverlapChars);

        List<(string Chunk, int Offset)> chunks = [];

        for (int offset = 0; offset < text.Length; offset += step)
        {

            int length = Math.Min(chunkSizeChars, text.Length - offset);

            bool isFinalChunk = offset + length >= text.Length;

            // Never end a non-final chunk on a lone high surrogate — the next window (offset + step,
            // computed independently of this chunk's length) still covers the full character, so
            // shaving one char off this chunk's tail cannot create a coverage gap in the overall scan.
            if (!isFinalChunk && length > 0 && char.IsHighSurrogate(text[offset + length - 1]))
            {
                length--;

            }

            chunks.Add((text.Substring(offset, length), offset));

            if (isFinalChunk)
            {
                break;

            }

        }

        return Task.FromResult(Result<(string Chunk, int Offset)[]>.Success([.. chunks]));

    }

}
