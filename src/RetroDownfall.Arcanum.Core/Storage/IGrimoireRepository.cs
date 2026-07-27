using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage.Entities;

namespace RetroDownfall.Arcanum.Core.Storage;

public interface IGrimoireRepository
{

    Task<(Guid SessionId, Guid AssistantEntryId)> BeginAssistantReplyAsync(
        Guid? sessionId,
        string prompt,
        string model,
        CancellationToken cancellationToken = default);

    Task FinalizeAssistantEntryAsync(
        Guid assistantEntryId,
        string fullContent,
        CancellationToken cancellationToken = default);

    Task DiscardAssistantEntryAsync(
        Guid assistantEntryId,
        CancellationToken cancellationToken = default);

    Task AppendToolInteractionAsync(
        Guid sessionId,
        string toolName,
        string arguments,
        string result,
        string modelUsed,
        CancellationToken cancellationToken = default);

    Task<MandatoryToolInteractionProbeResult> ProbeMandatoryToolInteractionAsync(
        MandatoryToolInteractionProbe probe,
        CancellationToken cancellationToken = default)
    {

        if (cancellationToken.IsCancellationRequested)
        {

            return Task.FromCanceled<MandatoryToolInteractionProbeResult>(
                cancellationToken);

        }

        return Task.FromResult(
            new MandatoryToolInteractionProbeResult(
                MandatoryToolInteractionProbeOutcome.Unavailable,
                Result: null));

    }

    Task<MandatoryToolInteractionPreflightResult>
        PreflightMandatoryToolInteractionAsync(
            MandatoryToolInteraction interaction,
            CancellationToken cancellationToken = default)
    {

        if (cancellationToken.IsCancellationRequested)
        {

            return Task.FromCanceled<MandatoryToolInteractionPreflightResult>(
                cancellationToken);

        }

        return Task.FromResult(
            new MandatoryToolInteractionPreflightResult(
                MandatoryToolInteractionPreflightOutcome.Unavailable,
                Result: null));

    }

    /// <summary>
    /// Durably appends one receipt-owned call/result pair using deterministic entry primary keys.
    /// This path does not publish process-local session events; the owning API handoff does so only
    /// after a committed outcome. Implementations that do not support mandatory receipts fail closed.
    /// </summary>
    Task<MandatoryToolInteractionAppendResult> AppendMandatoryToolInteractionAsync(
        MandatoryToolInteraction interaction,
        CancellationToken cancellationToken = default)
    {

        if (cancellationToken.IsCancellationRequested)
        {

            return Task.FromCanceled<MandatoryToolInteractionAppendResult>(cancellationToken);

        }

        return Task.FromResult(
            new MandatoryToolInteractionAppendResult(
                MandatoryToolInteractionAppendOutcome.Failed,
                interaction.Receipt));

    }

    Task SaveCompletedExchangeAsync(
        string userPrompt,
        string assistantText,
        string modelUsed,
        CancellationToken cancellationToken = default);

    Task<int> PurgeSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<Session?> GetSessionAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<Session?> GetSessionHeaderAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<List<GrimoireEntryDto>?> GetSessionEntriesAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task<List<GrimoireEntryDto>?> GetRecentSessionEntriesAsync(
        Guid sessionId,
        int takeLast,
        CancellationToken cancellationToken = default);

    Task<GrimoireEntryDto?> GetEntryByIdAsync(
        Guid sessionId,
        Guid entryId,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteEntryAsync(
        Guid sessionId,
        Guid entryId,
        CancellationToken cancellationToken = default);

    Task<bool> SetEntryPinnedAsync(
        Guid sessionId,
        Guid entryId,
        bool pinned,
        CancellationToken cancellationToken = default);

    Task<int> GetPinnedEntryCountAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    Task<List<Guid>> GetSessionsNeedingSummarizationAsync(
        int threshold,
        DateTime idleCutoff,
        CancellationToken cancellationToken = default);

    Task<List<Entry>> GetUnsummarizedEntriesAsync(
        Guid sessionId,
        DateTime watermark,
        int batchSize,
        CancellationToken cancellationToken = default);

    Task<bool> SessionExistsAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task IncrementSessionTokensAsync(Guid sessionId, long totalTokens, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically increments both <see cref="Session.TotalTokensUsed"/> and <see cref="Session.TotalCostUsd"/> in a single UPDATE.
    /// </summary>
    Task IncrementSessionTokensAndCostAsync(
        Guid sessionId,
        long totalTokens,
        decimal costUsd,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the sum of <see cref="Session.TotalCostUsd"/> across all sessions created today (UTC).
    /// </summary>
    Task<decimal> GetTodaySpendAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Advances <see cref="Session.LastSummarizedMessageAt"/> to the latest entry timestamp (or UTC now if there are no entries).
    /// </summary>
    Task AdvanceCampaignLogWatermarkAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically updates <see cref="Session.Summary"/> and <see cref="Session.LastSummarizedMessageAt"/> only. Does not modify <see cref="Entry"/> rows.
    /// </summary>
    Task UpdateSessionCampaignRollupAsync(
        Guid sessionId,
        string summary,
        DateTime lastSummarizedMessageAt,
        CancellationToken cancellationToken = default);

    Task<string?> ReadLoreAsync(string key, CancellationToken cancellationToken = default);

    Task<LoreDto> ScribeLoreAsync(string key, string value, CancellationToken cancellationToken = default);

    Task<bool> DeleteLoreAsync(string key, CancellationToken cancellationToken = default);

    Task<ListPageResult<LoreDto>> ListLoreAsync(
        int? limit = null,
        int offset = 0,
        CancellationToken cancellationToken = default);

    Task<LoreDto?> GetLoreAsync(string key, CancellationToken cancellationToken = default);

    Task<string> SearchArchivesAsync(string query, int maxResults, CancellationToken cancellationToken = default);

    Task RecordWorkspaceContextAsync(WorkspaceContext context, CancellationToken cancellationToken = default);

    Task<WorkspaceContext?> GetLatestWorkspaceContextAsync(string workspacePath, CancellationToken cancellationToken = default);

}
