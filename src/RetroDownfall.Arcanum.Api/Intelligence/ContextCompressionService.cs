using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.TheForge;
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

    public ContextCompressionService(
        IGrimoireRepository grimoire,
        IOptionsSnapshot<ArcanumSettings> settings,
        InferenceTokenizerResolver inferenceTokenizerResolver,
        ILogger<ContextCompressionService> logger,
        IModelTokenEstimator? modelTokenEstimator = null)
    {

        _grimoire = grimoire;

        _settings = settings;

        _modelTokenEstimator = modelTokenEstimator
            ?? new ModelTokenEstimator(inferenceTokenizerResolver, settings);

        _logger = logger;

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

            foreach (Guid entryId in groupSafeDeletes)
            {

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
