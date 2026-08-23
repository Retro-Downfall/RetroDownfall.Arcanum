using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;

using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Api.Intelligence;

public interface IContextCompressionService
{

    Task<CompactResult> CompressSessionAsync(Guid sessionId, int contextWindowLimit, CancellationToken cancellationToken);

    int CountTokens(
        IReadOnlyList<MeAiChatMessage> messages,
        ProviderSettings? provider = null,
        string? model = null,
        ChatOptions? options = null,
        int reservedAnswerTokens = 0,
        int reservedReasoningTokens = 0);

    int ComputeEffectiveLimit(int contextWindowLimit, int thresholdPercent);

}

internal sealed class ContextCompressionService : IContextCompressionService
{

    private const int DefaultContextWindowLimit = 8192;

    private readonly IGrimoireRepository _grimoire;

    private readonly IOptionsSnapshot<ArcanumSettings> _settings;

    private readonly IModelTokenEstimator _modelTokenEstimator;

    private readonly ILogger<ContextCompressionService> _logger;

    private readonly ICovenantSensitiveArtifactPurger? _purger;

    public ContextCompressionService(
        IGrimoireRepository grimoire,
        IOptionsSnapshot<ArcanumSettings> settings,
        InferenceTokenizerResolver inferenceTokenizerResolver,
        ILogger<ContextCompressionService> logger,
        IModelTokenEstimator? modelTokenEstimator = null,
        ICovenantSensitiveArtifactPurger? purger = null)
    {

        _grimoire = grimoire;

        _settings = settings;

        _modelTokenEstimator = modelTokenEstimator
            ?? new ModelTokenEstimator(inferenceTokenizerResolver);

        _logger = logger;

        _purger = purger;

    }

    /// <summary>
    /// Dispatches the selected Entries through the sensitivity purge boundary, in bounded pages.
    /// </summary>
    /// <remarks>
    /// Paged rather than sent whole, because the boundary is bounded and a long Session's compaction can
    /// select more Entries than one page carries. Each page is a stable identity list read before the
    /// purge, so no unexamined labelled Entry can leave through a set-based call.
    /// </remarks>
    private async Task<Result<CovenantSensitivePurgeOutcome>> PurgeSelectedEntriesAsync(
        IReadOnlyCollection<Guid> entryIds,
        CancellationToken cancellationToken)
    {

        List<CovenantSensitivePurgeResult> results = [];

        CovenantArtifactErasureProgress progress = CovenantArtifactErasureProgress.Empty;

        foreach (Guid[] page in entryIds.Chunk(ICovenantSensitiveArtifactPurger.MaxTargets))
        {

            Result<CovenantSensitivePurgeOutcome> purged = await _purger!
                .PurgeAsync(
                    [.. page.Select(static id =>
                        new CovenantSensitivePurgeTarget(SensitiveArtifactKind.AssistantEntry, id))],
                    cancellationToken)
                .ConfigureAwait(false);

            if (purged.IsFailure)
            {

                return purged.Error;

            }

            if (purged.Value.IsBlocked)
            {

                return new Error(
                    ErrorCodes.Covenant.ManualArtifactErasureRequired,
                    "A protected Entry selected by compaction could not be erased and was left unchanged.");

            }

            results.AddRange(purged.Value.Results);

            progress = progress.Add(purged.Value.Progress);

        }

        return Result<CovenantSensitivePurgeOutcome>.Success(
            new CovenantSensitivePurgeOutcome(results, progress));

    }

    public async Task<CompactResult> CompressSessionAsync(Guid sessionId, int contextWindowLimit, CancellationToken cancellationToken)
    {

        Session? session = await _grimoire
            .GetSessionAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);

        if (session is null)
        {

            return new CompactResult(0, 0, 0);

        }

        IntelligenceSettings intelligenceSettings = _settings.Value.ResolveIntelligence();
        ResolveProfileTarget(
            provider: null,
            model: null,
            out ProviderSettings compressionProvider,
            out string compressionModel);

        if (!intelligenceSettings.EnableContextCompression)
        {

            return new CompactResult(0, 0, 0);

        }

        int minMessages = ArcanumSettingClamps.CompressionPreflightMinMessages(
            intelligenceSettings.CompressionPreflightMinMessages);

        List<MeAiChatMessage> messages = InferenceContextBuilder.MapGrimoireToMeAiMessages(session, string.Empty);

        if (messages.Count < minMessages)
        {

            return new CompactResult(0, 0, 0);

        }

        int tokensBefore = CountTokens(messages, compressionProvider, compressionModel);

        int effectiveLimit = ComputeEffectiveLimit(
            contextWindowLimit > 0 ? contextWindowLimit : DefaultContextWindowLimit,
            intelligenceSettings.ContextWindowCompressionThreshold);

        if (tokensBefore <= effectiveLimit)
        {

            return new CompactResult(tokensBefore, tokensBefore, 0);

        }

        List<Entry> ordered = session.Entries
            .Where(e => !e.IsPinned)
            .OrderBy(e => e.CreatedAt)
            .ToList();

        int removed = 0;

        int tokensAfter = tokensBefore;

        if (ordered.Count > 0)
        {

            int tokensToRemove = tokensBefore - effectiveLimit;

            long estimatedRemoved = 0;

            List<Guid> entryIdsToDelete = [];

            foreach (Entry entry in ordered)
            {
                ChatRole role = entry.Role switch
                {
                    MessageRole.User => ChatRole.User,
                    MessageRole.Assistant => ChatRole.Assistant,
                    MessageRole.System => ChatRole.System,
                    _ => ChatRole.User,
                };
                estimatedRemoved += CountTokens(
                    [new MeAiChatMessage(role, entry.Content)],
                    compressionProvider,
                    compressionModel);

                entryIdsToDelete.Add(entry.Id);

                if (estimatedRemoved >= tokensToRemove)
                {

                    break;

                }

            }

            HashSet<Guid> groupSafeDeletes = TurnContextGuards.ExpandDeletionToCompleteToolGroups(ordered, entryIdsToDelete);

            // The complete tool-group-safe set is dispatched, not the subset the token budget picked.
            // Expanding first and purging second is what keeps a partially deleted tool group from
            // existing at any point: a labelled Entry that left through the shared kernel and an
            // unlabelled sibling that left through the ordinary delete are still one group (§10.20.2).
            Result<CovenantSensitivePurgeOutcome>? purged = _purger is null
                ? null
                : await PurgeSelectedEntriesAsync(groupSafeDeletes, cancellationToken).ConfigureAwait(false);

            if (purged is { } attempted && attempted.IsFailure)
            {

                // A refused purge stops compaction rather than falling back to the ordinary delete.
                // Removing the unlabelled remainder would leave the Session compacted around protected
                // Entries that are still there, which is worse than not compacting at all.
                _logger.LogWarning(
                    "Compaction of session {SessionId} stopped: a protected Entry could not be erased ({Code}).",
                    sessionId,
                    attempted.Error.Code);

                return new CompactResult(tokensBefore, tokensBefore, 0);

            }

            foreach (Guid entryId in groupSafeDeletes)
            {

                if (purged is { } outcome && !outcome.Value.RequiresOrdinaryDelete(entryId))
                {

                    if (outcome.Value.WasPurged(entryId))
                    {

                        removed++;

                    }

                    continue;

                }

                await _grimoire
                    .DeleteEntryAsync(sessionId, entryId, cancellationToken)
                    .ConfigureAwait(false);

                removed++;

            }

            session = await _grimoire
                .GetSessionAsync(sessionId, cancellationToken)
                .ConfigureAwait(false);

            if (session is not null)
            {

                messages = InferenceContextBuilder.MapGrimoireToMeAiMessages(session, string.Empty);

                tokensAfter = CountTokens(messages, compressionProvider, compressionModel);

            }

        }

        if (removed > 0 && tokensAfter > effectiveLimit)
        {

            _logger.LogWarning(
                "Compact removed {Removed} entries from session {SessionId} but context remains {TokensAfter} tokens (threshold {EffectiveLimit}).",
                removed,
                sessionId,
                tokensAfter,
                effectiveLimit);

        }

        return new CompactResult(tokensBefore, tokensAfter, removed);

    }

    public int CountTokens(
        IReadOnlyList<MeAiChatMessage> messages,
        ProviderSettings? provider = null,
        string? model = null,
        ChatOptions? options = null,
        int reservedAnswerTokens = 0,
        int reservedReasoningTokens = 0)
    {
        ResolveProfileTarget(provider, model, out ProviderSettings resolvedProvider, out string resolvedModel);
        return Estimate(
                messages,
                resolvedProvider,
                resolvedModel,
                options,
                reservedAnswerTokens,
                reservedReasoningTokens)
            .TotalTokens;
    }

    private ContextTokenBreakdown Estimate(
        IReadOnlyList<MeAiChatMessage> messages,
        ProviderSettings provider,
        string model,
        ChatOptions? options = null,
        int reservedAnswerTokens = 0,
        int reservedReasoningTokens = 0)
    {
        return _modelTokenEstimator.EstimateContext(
            new ModelTokenizationRequest(
                provider,
                model,
                messages,
                options ?? new ChatOptions(),
                reservedAnswerTokens,
                reservedReasoningTokens));
    }

    private void ResolveProfileTarget(
        ProviderSettings? provider,
        string? model,
        out ProviderSettings resolvedProvider,
        out string resolvedModel)
    {
        if (provider is not null && !string.IsNullOrWhiteSpace(model))
        {
            resolvedProvider = provider;
            resolvedModel = model;
            return;
        }

        if (ProviderResolver.TryResolveProviderForModel(
                _settings.Value,
                model,
                out ProviderSettings? configuredProvider,
                out string configuredModel)
            && configuredProvider is not null)
        {
            resolvedProvider = configuredProvider;
            resolvedModel = configuredModel;
            return;
        }

        resolvedModel = string.IsNullOrWhiteSpace(model) ? "unknown" : model;
        resolvedProvider = new ProviderSettings
        {
            Name = "unconfigured",
            Type = AiProviderKind.OpenAICompatible,
            Models = [new ModelEntry(resolvedModel)],
            ContextWindowLimit = DefaultContextWindowLimit,
        };

    }

    public int ComputeEffectiveLimit(int contextWindowLimit, int thresholdPercent)
    {

        int clampedLimit = ArcanumSettingClamps.ContextWindowLimit(contextWindowLimit);

        int thresholdPct = ArcanumSettingClamps.ContextWindowCompressionThreshold(thresholdPercent);

        long effectiveLong = (long)clampedLimit * thresholdPct / 100L;

        return effectiveLong > int.MaxValue ? int.MaxValue : (int)effectiveLong;

    }

}
