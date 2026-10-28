using RetroDownfall.Arcanum.Infrastructure.Weave;
using System.ClientModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Net.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
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
    ILogger<WeaveService> logger,
    ITapestryProviderValidator? tapestryValidator) : IWeaveService
{
    /// <summary>Existing ordinary-only construction; an active Tapestry observation still fails closed.</summary>
    public WeaveService(IEmbeddingGeneratorFactory generatorFactory, IOptionsMonitor<ArcanumSettings> optionsMonitor,
        IServiceScopeFactory scopeFactory, ILogger<WeaveService> logger)
        : this(generatorFactory, optionsMonitor, scopeFactory, logger, null)
    {
    }

    private sealed class DisabledBeforeGenerationResult;

    private readonly ConditionalWeakTable<Result, DisabledBeforeGenerationResult> _disabledBeforeGeneration = new();

    // Observation only, issued by this sealed owner's pre-generation guard; never send/accounting authority.
    internal bool OwnsDisabledBeforeGenerationResult(Result<Embedding<float>> result) =>
        _disabledBeforeGeneration.TryGetValue(result, out _);

    private Result<T> MarkDisabledBeforeGeneration<T>(Result<T> result)
    {
        _disabledBeforeGeneration.Add(result, new());

        return result;
    }

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
            Result<Embedding<float>> failure = Result<Embedding<float>>.Failure(batchResult.Error);

            return _disabledBeforeGeneration.TryGetValue(batchResult, out _)
                ? MarkDisabledBeforeGeneration(failure)
                : failure;
        }

        // EmbedBatchAsync guarantees one vector per input on success. A provider that answered 200
        // with no vectors at all (model still loading, input silently dropped) has already become a
        // ProviderUnavailable failure above, so index zero always exists here.
        return Result<Embedding<float>>.Success(batchResult.Value[0]);
    }

    public async Task<Result<Embedding<float>[]>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        // Disabled-path (no generator resolution, no HTTP call, no exception): checked first, before
        // anything else in this method runs.
        ArcanumSettings operationSettings = optionsMonitor.CurrentValue;

        EmbeddingSettings embeddings = operationSettings.ResolveEmbeddings();

        if (!embeddings.Enabled || string.IsNullOrWhiteSpace(embeddings.Provider) || string.IsNullOrWhiteSpace(embeddings.Model))
        {
            return MarkDisabledBeforeGeneration(Result<Embedding<float>[]>.Failure(new Error(
                ErrorCodes.Embeddings.FeatureDisabled,
                "Embeddings are disabled or incomplete (enable an embedding-backed Arcanum:Features option and configure Arcanum:Integrations:Embeddings:Provider and Arcanum:Integrations:Embeddings:Model).")));
        }

        if (texts.Count == 0)
        {
            return Result<Embedding<float>[]>.Success([]);
        }

        EmbeddingProvenance expectedIdentity = new(embeddings.Provider, embeddings.Model);

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
                batch.Add(TapestryBuildContext.Current is null && text.Length > chunkSizeChars
                    ? text[..Utf8Truncation.SafeCharSliceLength(text, chunkSizeChars)]
                    : text);
            }

            sanitizedBatches.Add(batch);
        }

        long totalApproxTokens = EstimateApproximateTokens(sanitizedBatches.SelectMany(static batch => batch));
        PricingSettings pricingSettings = operationSettings.ResolvePricing();

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
            Result<TurnAccountingHandle> begin;

            // Opening the run row is a durable Grimoire write, and it happens before this method's
            // own try. Only the failed-Result arm below used to give the scope back, so a store
            // fault — a closed maintenance admission gate among them — escaped with the scope still
            // live. The rethrow keeps the caller's behaviour exactly as it was.
            try
            {
                begin = await TurnAccountingHandle.BeginAsync(
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
            }
            catch
            {
                accountingScope.Dispose();

                throw;
            }

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

        EmbeddingAttempt? attempt = null;

        try
        {
            // Sequential, not parallel — avoids overwhelming local providers (e.g. Ollama).
            foreach (List<string> batch in sanitizedBatches)
            {
                attempt = new EmbeddingAttempt();

                Embedding<float>[] batchEmbeddings = await EmbedOneBatchAsync(
                    batch,
                    cancellationToken,
                    attempt,
                    expectedIdentity).ConfigureAwait(false);

                attempt.Stage = "accounting";

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

                attempt.Stage = "generate";

                // The provider answered, so the batch is billed whatever its shape is; the spend is
                // ledgered above before the shape can fail the call. The service promises one vector
                // per input, in order, so every consumer can index positionally without re-checking.
                if (batchEmbeddings.Length != batch.Count)
                {
                    EmbeddingFailure mismatch = new("cardinality_mismatch",
                        "The embedding provider returned a different number of vectors than inputs. Verify integrations.embeddings.model and provider compatibility; run 'arcanum doctor'.", null);

                    LogEmbeddingFailure(mismatch, attempt, embeddings, accounting, "none", batchEmbeddings.Length, batch.Count);

                    return Result<Embedding<float>[]>.Failure(new Error(ErrorCodes.Embeddings.ProviderUnavailable, mismatch.Message));
                }

                results.AddRange(batchEmbeddings);
            }

            accountingStatus = InferenceRunStatus.Completed;
            return Result<Embedding<float>[]>.Success([.. results]);
        }
        catch (TapestryProviderRefusalException ex)
        {
            return Result<Embedding<float>[]>.Failure(ex.Error);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            EmbeddingFailure failure = DescribeEmbeddingFailure(ex, attempt?.Stage);

            LogEmbeddingFailure(failure, attempt, embeddings, accounting, ex.GetType().FullName ?? "unknown");

            return Result<Embedding<float>[]>.Failure(new Error(ErrorCodes.Embeddings.ProviderUnavailable, failure.Message));
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
                    logger.LogWarning(
                        "Standalone embedding accounting completion failed for run {RunId}, reservation {ReservationId} (exception type {ExceptionType}).",
                        accounting.RunId,
                        accounting.ReservationId,
                        ex.GetType().FullName);
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

    private sealed class EmbeddingAttempt
    {
        internal string Stage { get; set; } = "resolve";

        internal EmbeddingProvenance? Provenance { get; set; }
    }

    private sealed class EmbeddingIdentityChangedException : Exception;

    private sealed record EmbeddingFailure(string Reason, string Message, int? HttpStatus);

    private static EmbeddingFailure DescribeEmbeddingFailure(Exception failure, string? stage)
    {
        if (failure is EmbeddingIdentityChangedException)
        {
            return new("embedding_identity_changed", "The embedding identity changed before a provider call; no further batch was sent. Earlier batches may already be billed. Verify the embedding configuration before starting another operation; run 'arcanum doctor'.", null);
        }

        if (stage == "accounting")
        {
            return new("accounting_write_failed", "Embedding usage could not be recorded. Check local storage availability and run 'arcanum doctor'.", null);
        }

        int? status = failure switch
        {
            ClientResultException client when client.Status is >= 100 and <= 599 => client.Status,
            HttpRequestException http when (int?)http.StatusCode is >= 100 and <= 599 => (int?)http.StatusCode,
            _ => null,
        };

        string suffix = status is { } known ? FormattableString.Invariant($" (HTTP {known})") : string.Empty;

        if (stage == "resolve")
        {
            return status switch
            {
                401 => new("credentials_rejected", $"Embedding generator resolution was refused because credentials were rejected{suffix}. Verify provider credentials and credential-store access; run 'arcanum doctor'.", status),
                403 => new("permission_refused", $"Embedding generator resolution was refused permission{suffix}. Verify provider/model access and configuration; run 'arcanum doctor'.", status),
                404 => new("model_not_found", $"The embedding model or provider configuration was not found{suffix}. Verify integrations.embeddings.provider and integrations.embeddings.model; run 'arcanum doctor'.", status),
                413 => new("request_too_large", $"The embedding generator request was too large{suffix}. Verify provider limits and configuration; run 'arcanum doctor'.", status),
                _ => new("provider_unavailable", $"The embedding generator is unavailable{suffix}. Verify integrations.embeddings.provider, integrations.embeddings.model and provider configuration; run 'arcanum doctor'.", status),
            };
        }

        return status switch
        {
            401 => new("credentials_rejected", $"The embedding provider rejected the configured credentials{suffix}. Verify provider credential configuration; run 'arcanum doctor'.", status),
            403 => new("permission_refused", $"The embedding provider refused permission{suffix}. Verify provider and model access; run 'arcanum doctor'.", status),
            404 => new("model_not_found", $"The embedding model or embeddings endpoint was not found{suffix}. Verify integrations.embeddings.model and provider configuration; run 'arcanum doctor'.", status),
            413 => new("request_too_large", $"The embedding provider rejected the bounded request as too large{suffix}. Send fewer or smaller inputs, or select a provider/model with sufficient limits; run 'arcanum doctor'.", status),
            _ => new("provider_unavailable", $"The embedding provider is unavailable for this request{suffix}. Verify integrations.embeddings.provider, integrations.embeddings.model and provider availability; run 'arcanum doctor'.", status),
        };
    }

    private void LogEmbeddingFailure(EmbeddingFailure failure, EmbeddingAttempt? attempt, EmbeddingSettings configured,
        TurnAccountingHandle accounting, string exceptionType, int? actualCount = null, int? expectedCount = null)
    {
        logger.LogWarning(
            "Embedding operation failed ({Reason}, stage {Stage}, provider {Provider}, model {Model}, provenance {Provenance}, configured provider {ConfiguredProvider}, configured model {ConfiguredModel}, HTTP {HttpStatus}, exception type {ExceptionType}, run {RunId}, reservation {ReservationId}, vectors {ActualCount}/{ExpectedCount}). {Remedy}",
            failure.Reason,
            attempt?.Stage ?? "unknown",
            SafeEmbeddingIdentifier(attempt?.Provenance?.Provider),
            SafeEmbeddingIdentifier(attempt?.Provenance?.Model),
            attempt?.Provenance is null ? "unknown" : "resolved_lease",
            SafeEmbeddingIdentifier(configured.Provider),
            SafeEmbeddingIdentifier(configured.Model),
            failure.HttpStatus,
            exceptionType,
            accounting.RunId,
            accounting.ReservationId,
            actualCount,
            expectedCount,
            failure.Message);
    }

    private static string SafeEmbeddingIdentifier(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "unknown";

        if (value.Length > 128 || !char.IsAsciiLetterOrDigit(value[0]) || value.Contains("..", StringComparison.Ordinal)) return "redacted";

        foreach (char character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-' or ':')) return "redacted";
        }

        return value;
    }

    private async Task<Embedding<float>[]> EmbedOneBatchAsync(
        List<string> batch,
        CancellationToken cancellationToken,
        EmbeddingAttempt attempt,
        EmbeddingProvenance expectedIdentity)
    {
        using EmbeddingGeneratorLease lease = await generatorFactory
            .ResolveGeneratorAsync(cancellationToken)
            .ConfigureAwait(false);

        attempt.Provenance = lease.Provenance;

        if (TapestryBuildContext.Current is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Result validation = tapestryValidator is not null && lease.Provenance is { } actual
                ? await tapestryValidator.ValidateAsync(actual.Provider, actual.Model, true, cancellationToken).ConfigureAwait(false)
                : Result.Failure(new Error(ErrorCodes.Hub.InvalidAdmission, "The Tapestry embedding has no current source and model proof."));

            if (validation.IsFailure) throw new TapestryProviderRefusalException(validation.Error);
        }

        cancellationToken.ThrowIfCancellationRequested();

        attempt.Stage = "identity";

        if (lease.Provenance is { } resolved && !string.Equals(resolved.Identity, expectedIdentity.Identity, StringComparison.Ordinal))
        {
            throw new EmbeddingIdentityChangedException();
        }

        attempt.Stage = "generate";

        GeneratedEmbeddings<Embedding<float>> generated = await lease.Generator
            .GenerateAsync(batch, options: null, cancellationToken)
            .ConfigureAwait(false);

        Embedding<float>[] embeddings = new Embedding<float>[generated.Count];

        for (int i = 0; i < generated.Count; i++)
        {
            embeddings[i] = generated[i];

            // Provider metadata cannot assert first-party provenance. Only this resolved lease can.
            if (lease.Provenance is { } provenance)
            {
                embeddings[i].AdditionalProperties ??= new();

                embeddings[i].AdditionalProperties![EmbeddingProvenance.MetadataKey] = provenance;
            }
            else
            {
                _ = embeddings[i].AdditionalProperties?.Remove(EmbeddingProvenance.MetadataKey);
            }
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
        int step = ResolveChunkStep(chunkSizeChars, chunkOverlapChars);

        List<(string Chunk, int Offset)> chunks = [];

        for (int offset = 0; offset < text.Length; offset += step)
        {
            // Never begin a chunk on an orphaned low surrogate either. Window starts land on multiples
            // of `step`, which is computed from the settings and knows nothing about the text, so an
            // astral character straddling that index would otherwise open the next chunk with half a
            // character — serialized as U+FFFD by the embedding provider. The previous window already
            // carried the whole character (its own tail guard kept the pair intact), so stepping past
            // the low half loses no coverage.
            int start = offset > 0 && char.IsLowSurrogate(text[offset]) ? offset + 1 : offset;

            if (start >= text.Length)
            {
                break;
            }

            // Never end a non-final chunk on a lone high surrogate — the next window (offset + step,
            // computed independently of this chunk's length) still covers the full character, so
            // shaving one char off this chunk's tail cannot create a coverage gap in the overall scan.
            int length = Utf8Truncation.SafeCharSliceLength(text.AsSpan(start), chunkSizeChars);

            bool isFinalChunk = start + length >= text.Length;

            chunks.Add((text.Substring(start, length), start));

            if (isFinalChunk)
            {
                break;
            }
        }

        return Task.FromResult(Result<(string Chunk, int Offset)[]>.Success([.. chunks]));
    }

    /// <summary>
    /// Distance between adjacent window starts. <see cref="ArcanumSettingClamps.EmbeddingsChunkSizeChars"/>
    /// (128–8,192) and <see cref="ArcanumSettingClamps.EmbeddingsChunkOverlapChars"/> (0–1,024) clamp
    /// independently, so an overlap at or above the resolved chunk size survives both and would leave
    /// <c>size - overlap</c> at zero or negative — a one-character step, one near-duplicate chunk per
    /// character of the source, and the batched embedding spend that follows. Bounding the overlap at
    /// half the window keeps the scan advancing and caps the emitted chunk count at twice the minimum.
    /// </summary>
    internal static int ResolveChunkStep(int chunkSizeChars, int chunkOverlapChars)
    {
        int boundedOverlap = Math.Clamp(chunkOverlapChars, 0, chunkSizeChars / 2);

        return Math.Max(1, chunkSizeChars - boundedOverlap);
    }
}
