using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// RAG Phase 4 — Saga: an event-driven background service that extracts durable facts, decisions, and
/// preferences from recently completed inference turns into <c>saga_memories</c>. Enqueued by
/// <c>WizardIntelligenceProvider</c> after a successful turn (see <see cref="EnqueueExtraction"/>); the
/// service otherwise idles, blocked on the channel reader — no polling. Follows the same headless
/// extraction pattern as <c>Loremaster</c> (Campaign Logger): <c>SkipSpellRouting</c>,
/// <c>DisableMcpTools</c>, and <c>UnattendedMode</c> are all <c>true</c> for the extraction LLM call.
/// </summary>
[ExcludeFromCodeCoverage] // Reason: BackgroundService Saga memory extraction
public sealed class SagaExtractionService(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<ArcanumSettings> options,
    ILogger<SagaExtractionService> logger) : BackgroundService
{

    private const int QueueCapacity = 100;

    private const string ExtractionSystemPrompt =
        """
        You are the Saga Keeper, responsible for maintaining the long-term memory
        of an AI assistant. Extract any durable facts, decisions, preferences,
        or important context from the following conversation that would be useful
        in future sessions. Return each memory as a single concise sentence.
        Return JSON: { "memories": ["memory 1", "memory 2", ...] }
        If there is nothing worth remembering, return { "memories": [] }.
        """;

    private readonly Channel<Guid> _channel = Channel.CreateBounded<Guid>(
        new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    /// <summary>
    /// Enqueues a session for Saga memory extraction. Thread-safe; never throws. When the queue is
    /// full, the oldest pending session id is dropped (<see cref="BoundedChannelFullMode.DropOldest"/>)
    /// — losing an enqueue here is acceptable, since the next successful turn for that session
    /// re-enqueues it.
    /// </summary>
    public void EnqueueExtraction(Guid sessionId)
    {

        try
        {

            if (!_channel.Writer.TryWrite(sessionId))
            {

                logger.LogDebug(
                    "Saga extraction queue is full; dropping enqueue for session {SessionId}.",
                    sessionId);

            }

        }
        catch (Exception ex)
        {

            logger.LogDebug(ex, "Saga extraction enqueue failed for session {SessionId}.", sessionId);

        }

    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {

        await Task.Yield();

        try
        {

            await foreach (Guid sessionId in _channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {

                try
                {

                    EmbeddingSettings embeddings = options.CurrentValue.Embeddings ?? new EmbeddingSettings();

                    if (!embeddings.Enabled || !embeddings.SagaEnabled || !embeddings.Saga.ExtractionEnabled)
                    {

                        logger.LogDebug(
                            "Saga extraction skipped for session {SessionId}: feature disabled (Enabled={Enabled}, SagaEnabled={SagaEnabled}, Saga.ExtractionEnabled={ExtractionEnabled}).",
                            sessionId,
                            embeddings.Enabled,
                            embeddings.SagaEnabled,
                            embeddings.Saga.ExtractionEnabled);

                        continue;

                    }

                    await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

                    await ExtractForSessionAsync(
                        scope.ServiceProvider,
                        sessionId,
                        embeddings,
                        options.CurrentValue,
                        stoppingToken).ConfigureAwait(false);

                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {

                    return;

                }
                catch (Exception ex)
                {

                    logger.LogWarning(ex, "Saga extraction failed for session {SessionId}", sessionId);

                }

            }

        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {

        }

    }

    /// <summary>
    /// Extraction logic for a single dequeued session id. <c>internal</c> (rather than <c>private</c>)
    /// so tests can drive it directly without needing the full channel/<see cref="ExecuteAsync"/>
    /// machinery — mirrors <c>EntryWeavingService.RunTickAsync</c>'s testability pattern.
    /// </summary>
    internal async Task ExtractForSessionAsync(
        IServiceProvider services,
        Guid sessionId,
        EmbeddingSettings embeddings,
        ArcanumSettings settings,
        CancellationToken cancellationToken)
    {

        IWeaveService weave = services.GetRequiredService<IWeaveService>();

        if (!weave.IsAvailable)
        {

            logger.LogDebug(
                "Saga extraction skipped for session {SessionId}: embedding provider unavailable.",
                sessionId);

            return;

        }

        ISagaMemoryStore store = services.GetRequiredService<ISagaMemoryStore>();

        IGrimoireRepository grimoire = services.GetRequiredService<IGrimoireRepository>();

        DateTimeOffset? watermark = await store.GetWatermarkAsync(sessionId, cancellationToken).ConfigureAwait(false);

        int windowEntries = ArcanumSettingClamps.EmbeddingsSagaExtractionWindowEntries(
            embeddings.Saga.ExtractionWindowEntries);

        List<GrimoireEntryDto>? recent = await grimoire
            .GetRecentSessionEntriesAsync(sessionId, windowEntries, cancellationToken)
            .ConfigureAwait(false);

        if (recent is null || recent.Count == 0)
        {

            logger.LogDebug("Saga extraction skipped for session {SessionId}: no entries found.", sessionId);

            return;

        }

        List<GrimoireEntryDto> newEntries = watermark is null
            ? recent
            : [.. recent.Where(entry => entry.CreatedAt > watermark.Value)];

        if (newEntries.Count == 0)
        {

            logger.LogDebug(
                "Saga extraction skipped for session {SessionId}: no new entries beyond watermark {Watermark:o}.",
                sessionId,
                watermark);

            return;

        }

        int maxTotal = ArcanumSettingClamps.EmbeddingsSagaMaxMemoriesTotal(embeddings.Saga.MaxMemoriesTotal);

        int totalCount = await store.CountAsync(cancellationToken).ConfigureAwait(false);

        if (totalCount >= maxTotal)
        {

            logger.LogWarning(
                "Saga extraction skipped for session {SessionId}: total memory cap ({MaxTotal}) reached.",
                sessionId,
                maxTotal);

            return;

        }

        int maxPerSession = ArcanumSettingClamps.EmbeddingsSagaMaxMemoriesPerSession(
            embeddings.Saga.MaxMemoriesPerSession);

        int sessionCount = await store.CountBySessionAsync(sessionId, cancellationToken).ConfigureAwait(false);

        if (sessionCount >= maxPerSession)
        {

            logger.LogWarning(
                "Saga extraction skipped for session {SessionId}: per-session memory cap ({MaxPerSession}) reached.",
                sessionId,
                maxPerSession);

            return;

        }

        IArcanumIntelligenceProvider intelligence = services.GetRequiredService<IArcanumIntelligenceProvider>();

        string prompt = BuildExtractionPrompt(newEntries);

        string? model = ResolveExtractionModel(embeddings.Saga.ExtractionModel, settings);

        int maxTokens = ArcanumSettingClamps.EmbeddingsSagaExtractionMaxTokens(embeddings.Saga.ExtractionMaxTokens);

        List<CoreChatMessage> statelessMessages =
        [
            new CoreChatMessage("system", ExtractionSystemPrompt),

            new CoreChatMessage("user", prompt),
        ];

        PingRequest ping = new(
            Prompt: string.Empty,
            Model: model,
            WorkingDirectory: string.Empty,
            UnattendedMode: true,
            DisableMcpTools: true,
            StatelessMessages: statelessMessages,
            SkipSpellRouting: true,
            MaxOutputTokens: maxTokens);

        Result<PromptTurnResult> result;

        try
        {

            result = await intelligence.ExecutePromptAsync(ping, cancellationToken).ConfigureAwait(false);

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            throw;

        }
        catch (Exception ex)
        {

            logger.LogWarning(ex, "Saga extraction LLM call threw for session {SessionId}.", sessionId);

            return;

        }

        if (result.IsFailure)
        {

            // Watermark is deliberately not advanced: the next tick (re-enqueued after the session's
            // next successful turn) retries from the same starting point.
            logger.LogWarning(
                "Saga extraction LLM call failed for session {SessionId}: {Code} {Message}",
                sessionId,
                result.Error.Code,
                result.Error.Message);

            return;

        }

        IReadOnlyList<string>? memories = ParseMemories(result.Value.Text, sessionId);

        if (memories is null)
        {

            // Malformed LLM response (JSON parse failure): the watermark is deliberately not advanced
            // — like the LLM-call-failure path above — so the next enqueue for this session retries
            // the same entry window instead of silently skipping it forever.
            return;

        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        int insertedCount = 0;

        foreach (string memory in memories)
        {

            string trimmed = memory.Trim();

            if (trimmed.Length == 0)
            {

                continue;

            }

            if (sessionCount + insertedCount >= maxPerSession)
            {

                logger.LogWarning(
                    "Saga extraction for session {SessionId} stopped mid-batch: per-session memory cap ({MaxPerSession}) reached.",
                    sessionId,
                    maxPerSession);

                break;

            }

            if (totalCount + insertedCount >= maxTotal)
            {

                logger.LogWarning(
                    "Saga extraction for session {SessionId} stopped mid-batch: total memory cap ({MaxTotal}) reached.",
                    sessionId,
                    maxTotal);

                break;

            }

            Result<Embedding<float>> embedResult = await weave.EmbedAsync(trimmed, cancellationToken).ConfigureAwait(false);

            if (embedResult.IsFailure)
            {

                logger.LogDebug(
                    "Saga extraction: failed to embed a memory for session {SessionId}; skipping that memory.",
                    sessionId);

                continue;

            }

            string id = Guid.NewGuid().ToString();

            await store.InsertAsync(
                id,
                trimmed,
                now,
                sessionId,
                tags: null,
                source: "extraction",
                embedResult.Value.Vector.ToArray(),
                cancellationToken).ConfigureAwait(false);

            insertedCount++;

        }

        if (memories.Count > 0 && insertedCount == 0)
        {

            // Every parsed memory failed to embed/insert (e.g. embedding provider outage): treat this
            // like the LLM-call-failure path and leave the watermark alone so the next enqueue retries
            // the same entry window instead of losing these memories forever.
            logger.LogWarning(
                "Saga extraction for session {SessionId}: 0 of {Count} parsed memories were persisted; watermark not advanced.",
                sessionId,
                memories.Count);

            return;

        }

        DateTimeOffset latestEntryCreatedAt = newEntries[^1].CreatedAt;

        await store.SetWatermarkAsync(sessionId, latestEntryCreatedAt, cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// Parses the extraction LLM's JSON response. Returns <c>null</c> (a genuine parse failure) rather
    /// than an empty array when the response could not be deserialized, so callers can distinguish "the
    /// LLM legitimately found nothing worth remembering" (empty array — watermark should still advance)
    /// from "the response was malformed and nothing was reviewed" (null — watermark must not advance).
    /// </summary>
    private IReadOnlyList<string>? ParseMemories(string responseText, Guid sessionId)
    {

        if (string.IsNullOrWhiteSpace(responseText))
        {

            return [];

        }

        string cleaned = StripMarkdownFences(responseText.Trim());

        SagaExtractionResponse? parsed;

        try
        {

            parsed = JsonSerializer.Deserialize(cleaned, TheForgeJsonContext.Default.SagaExtractionResponse);

        }
        catch (JsonException ex)
        {

            string logSnippet = responseText.Length > 200 ? responseText[..200] : responseText;

            logger.LogWarning(
                ex,
                "Saga extraction failed to parse JSON response for session {SessionId}: {ResponseText}",
                sessionId,
                logSnippet);

            return null;

        }

        return parsed?.Memories ?? [];

    }

    private static string BuildExtractionPrompt(List<GrimoireEntryDto> entries)
    {

        StringBuilder sb = new();

        foreach (GrimoireEntryDto entry in entries)
        {

            sb.Append('[').Append(entry.Role).Append("]: ").AppendLine(entry.Content);

        }

        return sb.ToString().TrimEnd();

    }

    private static string? ResolveExtractionModel(string? extractionModel, ArcanumSettings settings)
    {

        if (!string.IsNullOrWhiteSpace(extractionModel))
        {

            return extractionModel.Trim();

        }

        if (!string.IsNullOrWhiteSpace(settings.FastModel))
        {

            return settings.FastModel.Trim();

        }

        if (!string.IsNullOrWhiteSpace(settings.DefaultModel))
        {

            return settings.DefaultModel.Trim();

        }

        return null;

    }

    /// <summary>Mirrors <c>SemanticRouter.StripMarkdownFences</c> — no shared helper exists across the Api/Infrastructure boundary, and the algorithm is small enough to duplicate rather than introduce a cross-project dependency for it.</summary>
    private static string StripMarkdownFences(string trimmed)
    {

        if (trimmed.Length < 3 || !trimmed.StartsWith("```", StringComparison.Ordinal))
        {

            return trimmed;

        }

        ReadOnlySpan<char> afterOpen = trimmed.AsSpan(3).TrimStart();

        if (afterOpen.StartsWith("json", StringComparison.OrdinalIgnoreCase))
        {

            afterOpen = afterOpen[4..].TrimStart();

        }

        ReadOnlySpan<char> content = afterOpen;

        int close = content.LastIndexOf("```".AsSpan(), StringComparison.Ordinal);

        if (close >= 0)
        {

            content = content[..close].TrimEnd();

        }

        return content.ToString();

    }

}
