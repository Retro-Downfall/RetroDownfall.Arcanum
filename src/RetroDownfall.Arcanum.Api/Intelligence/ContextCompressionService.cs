using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.Tokenizers;
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

    int CountTokens(IReadOnlyList<MeAiChatMessage> messages);

    int ComputeEffectiveLimit(int contextWindowLimit, int thresholdPercent);

}

internal sealed class ContextCompressionService : IContextCompressionService
{

    private const int DefaultContextWindowLimit = 8192;

    private readonly IGrimoireRepository _grimoire;

    private readonly IOptionsSnapshot<ArcanumSettings> _settings;

    private readonly ManaPreflight _manaPreflight;

    private readonly InferenceTokenizerResolver _inferenceTokenizerResolver;

    private readonly ILogger<ContextCompressionService> _logger;

    public ContextCompressionService(
        IGrimoireRepository grimoire,
        IOptionsSnapshot<ArcanumSettings> settings,
        ManaPreflight manaPreflight,
        InferenceTokenizerResolver inferenceTokenizerResolver,
        ILogger<ContextCompressionService> logger)
    {

        _grimoire = grimoire;

        _settings = settings;

        _manaPreflight = manaPreflight;

        _inferenceTokenizerResolver = inferenceTokenizerResolver;

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

        IntelligenceSettings intelligenceSettings = _settings.Value.Intelligence ?? new IntelligenceSettings();

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

        int tokensBefore = CountTokens(messages);

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

            int estimatedRemoved = 0;

            List<Guid> entryIdsToDelete = [];

            foreach (Entry entry in ordered)
            {

                estimatedRemoved += Math.Max(1, entry.Content.Length / 4);

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

                tokensAfter = CountTokens(messages);

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

    public int CountTokens(IReadOnlyList<MeAiChatMessage> messages)
    {

        string encodingName = _settings.Value.Intelligence.TokenizerEncoding ?? InferenceTokenizerResolver.DefaultEncodingName;

        Tokenizer tokenizer = _inferenceTokenizerResolver.ResolveTokenizer(encodingName);

        int perMessageOverhead = ArcanumSettingClamps.PerMessageTemplateOverheadTokens(
            _settings.Value.Intelligence.PerMessageTemplateOverheadTokens);

        return _manaPreflight.CountTokens(messages, tokenizer, perMessageOverhead, encodingName);

    }

    public int ComputeEffectiveLimit(int contextWindowLimit, int thresholdPercent)
    {

        int clampedLimit = ArcanumSettingClamps.ContextWindowLimit(contextWindowLimit);

        int thresholdPct = ArcanumSettingClamps.ContextWindowCompressionThreshold(thresholdPercent);

        long effectiveLong = (long)clampedLimit * thresholdPct / 100L;

        return effectiveLong > int.MaxValue ? int.MaxValue : (int)effectiveLong;

    }

}
