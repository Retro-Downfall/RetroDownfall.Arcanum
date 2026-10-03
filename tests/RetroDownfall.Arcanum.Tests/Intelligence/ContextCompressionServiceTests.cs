using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class ContextCompressionServiceTests
{
    [Fact]
    public async Task CompressSessionAsync_MissingSession_ReturnsEmptyResult()
    {
        CompressionGrimoireRepository grimoire = new();
        ContextCompressionService service = CreateService(grimoire);

        CompactResult result = await service.CompressSessionAsync(
            Guid.NewGuid(),
            256,
            CancellationToken.None);

        Assert.Equal(new CompactResult(0, 0, 0), result);
        Assert.Empty(grimoire.DeletedEntryIds);
    }

    [Fact]
    public async Task CompressSessionAsync_ContextUnderDefaultLimit_PreservesMeasuredTokens()
    {
        Session session = CreateSession(
            [.. Enumerable.Range(0, 6)
                .Select(index => CreateEntry($"short context {index}", createdAtOffset: index))]);
        CompressionGrimoireRepository grimoire = new() { Session = session };
        ContextCompressionService service = CreateService(grimoire);

        CompactResult result = await service.CompressSessionAsync(
            session.Id,
            contextWindowLimit: 0,
            CancellationToken.None);

        Assert.True(result.TokensBefore > 0);
        Assert.Equal(result.TokensBefore, result.TokensAfter);
        Assert.Equal(0, result.EntriesRemoved);
        Assert.Empty(grimoire.DeletedEntryIds);
    }

    [Fact]
    public async Task CompressSessionAsync_OverLimit_DeletesOldestEntryAndRecounts()
    {
        Entry oldest = CreateEntry(new string('a', 20_000), createdAtOffset: 0);
        Entry retained = CreateEntry("retained", createdAtOffset: 1);
        Entry[] fillers = Enumerable.Range(2, 4)
            .Select(index => CreateEntry($"filler-{index}", isPinned: true, createdAtOffset: index))
            .ToArray();
        Session session = CreateSession([oldest, retained, .. fillers]);
        CompressionGrimoireRepository grimoire = new() { Session = session };
        ContextCompressionService service = CreateService(grimoire);

        CompactResult result = await service.CompressSessionAsync(
            session.Id,
            256,
            CancellationToken.None);

        Assert.Equal([oldest.Id], grimoire.DeletedEntryIds);
        Assert.Equal(1, result.EntriesRemoved);
        Assert.True(result.TokensBefore > result.TokensAfter);
        Assert.DoesNotContain(session.Entries, entry => entry.Id == oldest.Id);
        Assert.Contains(session.Entries, entry => entry.Id == retained.Id);
    }

    [Fact]
    public async Task CompressSessionAsync_OnlyPinnedEntries_LeavesOverLimitContextUntouched()
    {
        Entry[] pinned = Enumerable.Range(0, 6)
            .Select(index => CreateEntry(
                new string('p', 20_000),
                isPinned: true,
                createdAtOffset: index))
            .ToArray();
        Session session = CreateSession(pinned);
        CompressionGrimoireRepository grimoire = new() { Session = session };
        ContextCompressionService service = CreateService(grimoire);

        CompactResult result = await service.CompressSessionAsync(
            session.Id,
            256,
            CancellationToken.None);

        Assert.True(result.TokensBefore > 128);
        Assert.Equal(result.TokensBefore, result.TokensAfter);
        Assert.Equal(0, result.EntriesRemoved);
        Assert.Empty(grimoire.DeletedEntryIds);
    }

    [Fact]
    public async Task CompressSessionAsync_ReloadMissingAfterDelete_ReportsConservativeCounts()
    {
        Entry removable = CreateEntry(string.Empty, createdAtOffset: 0);
        Entry pinned = CreateEntry(new string('p', 20_000), isPinned: true, createdAtOffset: 1);
        Entry[] fillers = Enumerable.Range(2, 4)
            .Select(index => CreateEntry($"filler-{index}", isPinned: true, createdAtOffset: index))
            .ToArray();
        Session session = CreateSession([removable, pinned, .. fillers]);
        CompressionGrimoireRepository grimoire = new()
        {
            Session = session,
            ReturnNullAfterFirstLoad = true,
        };
        CapturingLogger<ContextCompressionService> logger = new();
        ContextCompressionService service = CreateService(
            grimoire,
            logger);

        CompactResult result = await service.CompressSessionAsync(
            session.Id,
            256,
            CancellationToken.None);

        Assert.Equal([removable.Id], grimoire.DeletedEntryIds);
        Assert.Equal(1, result.EntriesRemoved);
        Assert.Equal(result.TokensBefore, result.TokensAfter);
        Assert.Contains(
            logger.Messages,
            message => message.Contains("context remains", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CompressSessionAsync_CancelledLoad_PropagatesCancellation()
    {
        CompressionGrimoireRepository grimoire = new();
        ContextCompressionService service = CreateService(grimoire);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.CompressSessionAsync(Guid.NewGuid(), 256, cancellation.Token));
    }

    /// <summary>
    /// A purge that erases two pages' worth of protected Entries and then meets a refused one reports
    /// every Entry it erased, and the refusal's code.
    /// </summary>
    /// <remarks>
    /// The second page is the interesting one: the Entries erased before the refused item are gone, and
    /// so are all of the first page's. A result that said nothing was removed told the operator a
    /// compaction had done no harm while it had already erased protected Entries.
    /// </remarks>
    [Fact]
    public async Task CompressSessionAsync_PurgeBlockedAfterErasing_ReportsEveryEntryErasedAndTheBlock()
    {
        Session session = CreateOverLimitSession(entryCount: 300);
        CompressionGrimoireRepository grimoire = new() { Session = session };
        ScriptedPurger purger = new(
            EraseAll(session),
            EraseThenBlock(session, erased: 2, CovenantErasureBlocker.AuthorityStale));
        ContextCompressionService service = CreateService(grimoire, purger: purger);

        CompactResult result = await service.CompressSessionAsync(session.Id, 256, CancellationToken.None);

        Assert.Equal(2, purger.CallCount);
        Assert.Equal(ICovenantSensitiveArtifactPurger.MaxTargets + 2, result.EntriesRemoved);
        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, result.StoppedBy);
        Assert.Equal(300 - result.EntriesRemoved, session.Entries.Count);
        Assert.True(result.TokensAfter < result.TokensBefore);
        Assert.Empty(grimoire.DeletedEntryIds);
    }

    /// <summary>
    /// A block that is not a stale label reports the manual-erasure code, not the stale one.
    /// </summary>
    [Fact]
    public async Task CompressSessionAsync_PurgeBlockedByIntegrity_ReportsManualErasureRequired()
    {
        Session session = CreateOverLimitSession(entryCount: 8);
        CompressionGrimoireRepository grimoire = new() { Session = session };
        ScriptedPurger purger = new(EraseThenBlock(session, erased: 0, CovenantErasureBlocker.IntegrityFailure));
        ContextCompressionService service = CreateService(grimoire, purger: purger);

        CompactResult result = await service.CompressSessionAsync(session.Id, 256, CancellationToken.None);

        Assert.Equal(0, result.EntriesRemoved);
        Assert.Equal(ErrorCodes.Covenant.ManualArtifactErasureRequired, result.StoppedBy);
        Assert.Equal(result.TokensBefore, result.TokensAfter);
        Assert.Equal(8, session.Entries.Count);
        Assert.Empty(grimoire.DeletedEntryIds);
    }

    /// <summary>
    /// A purge that fails after erasing some of its own items still has those items counted.
    /// </summary>
    /// <remarks>
    /// The purger drops the dispositions of items it erased before a later item's step failed, so the
    /// count cannot come from its results. It comes from what the Session no longer holds.
    /// </remarks>
    [Fact]
    public async Task CompressSessionAsync_PurgeFailsAfterErasing_CountsWhatTheSessionLost()
    {
        Session session = CreateOverLimitSession(entryCount: 300);
        CompressionGrimoireRepository grimoire = new() { Session = session };
        ScriptedPurger purger = new(
            EraseAll(session),
            EraseThenFail(session, erased: 2, new Error(ErrorCodes.Covenant.Unavailable, "Storage could not answer.")));
        ContextCompressionService service = CreateService(grimoire, purger: purger);

        CompactResult result = await service.CompressSessionAsync(session.Id, 256, CancellationToken.None);

        Assert.Equal(ICovenantSensitiveArtifactPurger.MaxTargets + 2, result.EntriesRemoved);
        Assert.Equal(ErrorCodes.Covenant.Unavailable, result.StoppedBy);
        Assert.Equal(300 - result.EntriesRemoved, session.Entries.Count);
        Assert.Empty(grimoire.DeletedEntryIds);
    }

    /// <summary>
    /// A stop leaves the unlabelled remainder where it was, even the part that sat before the block.
    /// </summary>
    [Fact]
    public async Task CompressSessionAsync_PurgeBlocked_DoesNotDeleteTheUnlabeledRemainder()
    {
        Session session = CreateOverLimitSession(entryCount: 8);
        Guid[] order = [.. session.Entries.OrderBy(entry => entry.CreatedAt).Select(entry => entry.Id)];
        CompressionGrimoireRepository grimoire = new() { Session = session };
        ScriptedPurger purger = new(ids => PurgeOutcome(
            ids,
            [
                (CovenantSensitivePurgeDisposition.Unlabeled, CovenantErasureBlocker.None),
                (CovenantSensitivePurgeDisposition.Purged, CovenantErasureBlocker.None),
                (CovenantSensitivePurgeDisposition.Blocked, CovenantErasureBlocker.AuthorityStale),
            ],
            session));
        ContextCompressionService service = CreateService(grimoire, purger: purger);

        CompactResult result = await service.CompressSessionAsync(session.Id, 256, CancellationToken.None);

        Assert.Equal(1, result.EntriesRemoved);
        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, result.StoppedBy);
        Assert.Empty(grimoire.DeletedEntryIds);
        Assert.DoesNotContain(session.Entries, entry => entry.Id == order[1]);
        Assert.Contains(session.Entries, entry => entry.Id == order[0]);
    }

    /// <summary>
    /// Compaction that runs to the end names no stop, and an unlabelled Entry still leaves through the
    /// ordinary delete.
    /// </summary>
    [Fact]
    public async Task CompressSessionAsync_PurgeFindsNothingProtected_DeletesOrdinarilyAndReportsNoStop()
    {
        Session session = CreateOverLimitSession(entryCount: 8);
        CompressionGrimoireRepository grimoire = new() { Session = session };
        ScriptedPurger purger = new(ids => PurgeOutcome(
            ids,
            [(CovenantSensitivePurgeDisposition.Unlabeled, CovenantErasureBlocker.None)],
            session));
        ContextCompressionService service = CreateService(grimoire, purger: purger);

        CompactResult result = await service.CompressSessionAsync(session.Id, 256, CancellationToken.None);

        Assert.True(result.EntriesRemoved > 0);
        Assert.Null(result.StoppedBy);
        Assert.Equal(result.EntriesRemoved, grimoire.DeletedEntryIds.Count);
    }

    private static ContextCompressionService CreateService(
        CompressionGrimoireRepository grimoire,
        ILogger<ContextCompressionService>? logger = null,
        ICovenantSensitiveArtifactPurger? purger = null)
    {
        InferenceTokenizerResolver tokenizerResolver = new(
            NullLogger<InferenceTokenizerResolver>.Instance);

        return new ContextCompressionService(
            grimoire,
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()),
            tokenizerResolver,
            logger ?? NullLogger<ContextCompressionService>.Instance,
            purger: purger);
    }

    /// <summary>
    /// Enough small Entries, oldest first, that a 256-token window selects nearly all of them.
    /// </summary>
    private static Session CreateOverLimitSession(int entryCount) =>
        CreateSession(
            [.. Enumerable.Range(0, entryCount)
                .Select(index => CreateEntry(
                    string.Join(' ', Enumerable.Repeat($"ward-stone-{index}", 12)),
                    createdAtOffset: index))]);

    private static void RemoveFromSession(Session session, Guid entryId)
    {
        Entry? entry = session.Entries.SingleOrDefault(candidate => candidate.Id == entryId);

        if (entry is not null)
        {
            _ = session.Entries.Remove(entry);
        }
    }

    /// <summary>
    /// One purge call that erases every target and removes it from the Session, as the kernel would.
    /// </summary>
    private static Func<Guid[], Result<CovenantSensitivePurgeOutcome>> EraseAll(Session session) =>
        ids =>
        {
            foreach (Guid id in ids)
            {
                RemoveFromSession(session, id);
            }

            return PurgeOutcome(
                ids,
                [(CovenantSensitivePurgeDisposition.Purged, CovenantErasureBlocker.None)],
                session: null);
        };

    /// <summary>
    /// One purge call that erases its first targets, then blocks the next one and every one after it,
    /// which is what the coordinator reports when a labelled item cannot be erased.
    /// </summary>
    private static Func<Guid[], Result<CovenantSensitivePurgeOutcome>> EraseThenBlock(
        Session session,
        int erased,
        CovenantErasureBlocker blocker) =>
        ids => PurgeOutcome(
            ids,
            [
                .. Enumerable.Repeat((CovenantSensitivePurgeDisposition.Purged, CovenantErasureBlocker.None), erased),
                (CovenantSensitivePurgeDisposition.Blocked, blocker),
            ],
            session);

    /// <summary>
    /// One purge call that erases its first targets and then fails outright, reporting none of them.
    /// </summary>
    private static Func<Guid[], Result<CovenantSensitivePurgeOutcome>> EraseThenFail(
        Session session,
        int erased,
        Error error) =>
        ids =>
        {
            foreach (Guid id in ids.Take(erased))
            {
                RemoveFromSession(session, id);
            }

            return error;
        };

    /// <summary>
    /// An outcome that gives the targets the listed dispositions in order, the last one repeating for
    /// the rest. A purged target is also removed from <paramref name="session"/>.
    /// </summary>
    private static Result<CovenantSensitivePurgeOutcome> PurgeOutcome(
        Guid[] ids,
        (CovenantSensitivePurgeDisposition Disposition, CovenantErasureBlocker Blocker)[] steps,
        Session? session)
    {
        List<CovenantSensitivePurgeResult> results = [];

        for (int index = 0; index < ids.Length; index++)
        {
            (CovenantSensitivePurgeDisposition disposition, CovenantErasureBlocker blocker) =
                steps[Math.Min(index, steps.Length - 1)];

            if (disposition is CovenantSensitivePurgeDisposition.Purged && session is not null)
            {
                RemoveFromSession(session, ids[index]);
            }

            results.Add(new CovenantSensitivePurgeResult(
                ids[index],
                SensitiveArtifactKind.AssistantEntry,
                disposition,
                blocker));
        }

        return Result<CovenantSensitivePurgeOutcome>.Success(
            new CovenantSensitivePurgeOutcome(results, CovenantArtifactErasureProgress.Empty));
    }

    private static Session CreateSession(params Entry[] entries)
    {
        Guid sessionId = Guid.NewGuid();
        foreach (Entry entry in entries)
        {
            entry.SessionId = sessionId;
        }

        return new Session
        {
            Id = sessionId,
            Entries = entries.ToList(),
        };
    }

    private static Entry CreateEntry(
        string content,
        bool isPinned = false,
        int createdAtOffset = 0) =>
        new()
        {
            Id = Guid.NewGuid(),
            Role = MessageRole.User,
            Content = content,
            IsPinned = isPinned,
            CreatedAt = DateTimeOffset.UnixEpoch.AddSeconds(createdAtOffset),
        };

    private sealed class ScriptedPurger(
        params Func<Guid[], Result<CovenantSensitivePurgeOutcome>>[] calls) : ICovenantSensitiveArtifactPurger
    {
        public int CallCount { get; private set; }

        public ValueTask<Result<CovenantSensitivePurgeOutcome>> PurgeAsync(
            IReadOnlyList<CovenantSensitivePurgeTarget> targets,
            CancellationToken cancellationToken = default)
        {
            Func<Guid[], Result<CovenantSensitivePurgeOutcome>> call = calls[Math.Min(CallCount, calls.Length - 1)];

            CallCount++;

            return ValueTask.FromResult(call([.. targets.Select(target => target.ArtifactId)]));
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private sealed class CompressionGrimoireRepository : IGrimoireRepository
    {
        private int _sessionLoadCount;

        public Session? Session { get; init; }

        public bool ReturnNullAfterFirstLoad { get; init; }

        public List<Guid> DeletedEntryIds { get; } = [];

        public Task<Session?> GetSessionAsync(
            Guid id,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _sessionLoadCount++;

            return Task.FromResult(
                ReturnNullAfterFirstLoad && _sessionLoadCount > 1
                    ? null
                    : Session);
        }

        public Task<bool> DeleteEntryAsync(
            Guid sessionId,
            Guid entryId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeletedEntryIds.Add(entryId);

            Entry? entry = Session?.Entries.SingleOrDefault(candidate => candidate.Id == entryId);
            if (entry is not null)
            {
                _ = Session!.Entries.Remove(entry);
            }

            return Task.FromResult(entry is not null);
        }

        public Task<(Guid SessionId, Guid AssistantEntryId)> BeginAssistantReplyAsync(
            Guid? sessionId,
            string prompt,
            string model,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task FinalizeAssistantEntryAsync(
            Guid assistantEntryId,
            string fullContent,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task DiscardAssistantEntryAsync(
            Guid assistantEntryId,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task AppendToolInteractionAsync(
            Guid sessionId,
            string toolName,
            string arguments,
            string result,
            string modelUsed,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task SaveCompletedExchangeAsync(
            string userPrompt,
            string assistantText,
            string modelUsed,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<int> PurgeSessionAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<Session?> GetSessionHeaderAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<List<GrimoireEntryDto>?> GetSessionEntriesAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<List<GrimoireEntryDto>?> GetRecentSessionEntriesAsync(
            Guid sessionId,
            int takeLast,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<GrimoireEntryDto?> GetEntryByIdAsync(
            Guid sessionId,
            Guid entryId,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> SetEntryPinnedAsync(
            Guid sessionId,
            Guid entryId,
            bool pinned,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<int> GetPinnedEntryCountAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<List<Guid>> GetSessionsNeedingSummarizationAsync(
            int threshold,
            DateTime idleCutoff,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<List<Entry>> GetUnsummarizedEntriesAsync(
            Guid sessionId,
            DateTime watermark,
            int batchSize,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> SessionExistsAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task IncrementSessionTokensAsync(
            Guid sessionId,
            long totalTokens,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task IncrementSessionTokensAndCostAsync(
            Guid sessionId,
            long totalTokens,
            decimal costUsd,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<decimal> GetTodaySpendAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task AdvanceCampaignLogWatermarkAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task UpdateSessionCampaignRollupAsync(
            Guid sessionId,
            string summary,
            DateTime lastSummarizedMessageAt,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<string?> ReadLoreAsync(
            string key,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<LoreDto> ScribeLoreAsync(
            string key,
            string value,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> DeleteLoreAsync(
            string key,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<ListPageResult<LoreDto>> ListLoreAsync(
            int? limit = null,
            int offset = 0,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<LoreDto?> GetLoreAsync(
            string key,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<string> SearchArchivesAsync(
            string query,
            int maxResults,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task RecordWorkspaceContextAsync(
            WorkspaceContext context,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<WorkspaceContext?> GetLatestWorkspaceContextAsync(
            string workspacePath,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }
}
