using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Security;
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
    ///
    /// <para>A block ends the walk with the same typed code every other direct-deletion route answers: a
    /// label that moved is <c>Covenant.StaleSnapshot</c>, anything else the kernel could not erase is
    /// <c>Covenant.ManualArtifactErasureRequired</c>. The Entries erased before the block are already
    /// gone, so the walk records each one the purge reported erased in <paramref name="erasedByPurge"/>,
    /// the blocked page's included, and the caller counts what the stop removed from that and from the
    /// stored Entries rather than from this failure.</para>
    /// </remarks>
    private async Task<Result<CovenantSensitivePurgeOutcome>> PurgeSelectedEntriesAsync(
        IReadOnlyCollection<Guid> entryIds,
        HashSet<Guid> erasedByPurge,
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

            erasedByPurge.UnionWith(
                purged.Value.Results
                    .Where(static result => result.Disposition is CovenantSensitivePurgeDisposition.Purged)
                    .Select(static result => result.ArtifactId));

            if (purged.Value.IsBlocked)
            {

                return new Error(
                    CovenantSensitiveDeletion.BlockedError(purged.Value).Code,
                    "A protected Entry selected by compaction could not be erased and was left unchanged.");

            }

            results.AddRange(purged.Value.Results);

            progress = progress.Add(purged.Value.Progress);

        }

        return Result<CovenantSensitivePurgeOutcome>.Success(
            new CovenantSensitivePurgeOutcome(results, progress));

    }

    /// <summary>
    /// How many of the dispatched Entries a stopped compaction removed.
    /// </summary>
    /// <remarks>
    /// Not what the purge reported, because a page that stopped at its third item took the first two with
    /// it and a purge that failed after erasing some of its items reports none of them. And not what the
    /// reloaded Session lacks either: the repository returns only the newest Entries of a long Session,
    /// so the oldest ones, which compaction selects first, are missing from it whether or not they still
    /// exist. Each dispatched Entry the window does not show is therefore confirmed against the stored
    /// Entry, one lookup per Entry on this path only and never more than the dispatched set.
    ///
    /// <para>When the Session cannot be read at all, the count is a lower bound: the Entries the purge
    /// itself reported erased. An Entry erased by a purge that then failed outright is not among them,
    /// and nothing here can say it was.</para>
    /// </remarks>
    private async Task<int> CountRemovedAfterStopAsync(
        Guid sessionId,
        IReadOnlyCollection<Guid> dispatched,
        HashSet<Guid> erasedByPurge,
        Session? reloaded,
        CancellationToken cancellationToken)
    {

        if (reloaded is null)
        {

            return erasedByPurge.Count;

        }

        HashSet<Guid> inWindow = [.. reloaded.Entries.Select(static entry => entry.Id)];

        int gone = 0;

        foreach (Guid entryId in dispatched)
        {

            if (inWindow.Contains(entryId))
            {

                continue;

            }

            if (erasedByPurge.Contains(entryId)
                || await _grimoire
                    .GetEntryByIdAsync(sessionId, entryId, cancellationToken)
                    .ConfigureAwait(false) is null)
            {

                gone++;

            }

        }

        return gone;

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

        string? stoppedBy = null;

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
            HashSet<Guid> erasedByPurge = [];

            Result<CovenantSensitivePurgeOutcome>? purged = _purger is null
                ? null
                : await PurgeSelectedEntriesAsync(groupSafeDeletes, erasedByPurge, cancellationToken).ConfigureAwait(false);

            if (purged is { } attempted && attempted.IsFailure)
            {

                // A refused purge stops compaction rather than falling back to the ordinary delete.
                // Removing the unlabelled remainder would leave the Session compacted around protected
                // Entries that are still there, which is worse than not compacting at all.
                stoppedBy = attempted.Error.Code;

            }
            else
            {

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

            }

            session = await _grimoire
                .GetSessionAsync(sessionId, cancellationToken)
                .ConfigureAwait(false);

            if (session is not null)
            {

                messages = InferenceContextBuilder.MapGrimoireToMeAiMessages(session, string.Empty);

                tokensAfter = CountTokens(messages, compressionProvider, compressionModel);

            }

            if (stoppedBy is not null)
            {

                removed = await CountRemovedAfterStopAsync(
                        sessionId,
                        groupSafeDeletes,
                        erasedByPurge,
                        session,
                        cancellationToken)
                    .ConfigureAwait(false);

            }

        }

        if (stoppedBy is not null)
        {

            _logger.LogWarning(
                "Compaction of session {SessionId} stopped after removing {Removed} entries: a protected Entry could not be erased ({Code}).",
                sessionId,
                removed,
                stoppedBy);

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

        return new CompactResult(tokensBefore, tokensAfter, removed, stoppedBy);

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
