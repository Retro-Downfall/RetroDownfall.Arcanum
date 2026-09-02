using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Repositories;

public sealed class SessionRepository(
    ArcanumDbContext db,
    ISessionAttachmentStore attachments,
    IOptionsMonitor<ArcanumSettings> optionsMonitor,
    ISessionAttachmentIndexQueue? attachmentIndexQueue = null) : ISessionRepository
{

    /// <summary>
    /// Ceiling on how far a session-list page may widen to keep a one-timestamp tie group whole.
    /// </summary>
    /// <remarks>
    /// Widening is unavoidable — a bare-timestamp cursor cannot express a position inside a tie
    /// group — but it must not be unbounded, or a population whose <c>UpdatedAt</c> values collapse
    /// onto one tick turns a paged list into a whole-table materialization.
    /// </remarks>
    internal const int MaxTieGroupWidening = 1_000;

    private const int ExportEntryBatchSize = 500;

    private const int ForkEntryBatchSize = 256;

    /// <summary>
    /// Entry persistence for a repository composed without connection admission.
    /// </summary>
    /// <remarks>
    /// Nothing this repository calls on the shared persistence helper reaches the ordinary-connection
    /// factory — the two members that do, the mandatory-interaction fresh readbacks, have no caller
    /// here — so the public constructor genuinely has no factory to pass. The placeholder therefore
    /// throws rather than refusing with a <see cref="Result" />: a silent refusal would let a future
    /// caller of those readbacks take the unavailable branch and report a missing composition as a
    /// storage outcome.
    /// </remarks>
    private readonly SessionEntryPersistence _entryPersistence = new(
        db,
        UncomposedOrdinaryConnectionAdmission.Instance);

    internal SessionRepository(
        ArcanumDbContext db,
        ISessionAttachmentStore attachments,
        IOptionsMonitor<ArcanumSettings> optionsMonitor,
        ISessionAttachmentIndexQueue? attachmentIndexQueue,
        IGrimoireOrdinaryConnectionFactory connections)
        : this(db, attachments, optionsMonitor, attachmentIndexQueue)
    {

        _entryPersistence = new SessionEntryPersistence(db, connections);

    }

    public async Task<Session> CreateAsync(Guid? campaignId, string? title, CancellationToken ct)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Session session = new()
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
            Status = "active",
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Sessions.Add(session);

        _ = await EfSaveChangesRetry
            .ExecuteAsync(db, ct)
            .ConfigureAwait(false);

        return session;
    }

    public Task<Session?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<SessionQueryResult> QueryAsync(SessionQueryRequest request, CancellationToken ct)
    {
        SessionSettings settings = optionsMonitor.CurrentValue.ResolveSessions();

        int limit = ArcanumSettingClamps.SessionQueryLimit(
            request.Limit
            ?? settings.DefaultQueryLimit
            ?? ArcanumRuntimeDefaults.Sessions.DefaultQueryLimit!.Value);

        string statusFilter = string.IsNullOrWhiteSpace(request.Status) ? "active" : request.Status.Trim();

        string? searchTitlePattern = null;

        string? ftsMatchQuery = null;

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            string search = request.Search.Trim();

            int maxQueryLen = ArcanumSettingClamps.ArchiveSearchMaxQueryLength(
                optionsMonitor.CurrentValue.ResolveIntelligence().ArchiveSearchMaxQueryLength);

            if (search.Length > maxQueryLen)
            {
                search = search[..maxQueryLen];
            }

            searchTitlePattern = SqlLikePatterns.Contains(search);

            string matchQuery = FtsMatchQuerySanitizer.Sanitize(search);

            if (!string.IsNullOrEmpty(matchQuery))
            {
                ftsMatchQuery = matchQuery;
            }
        }

        // The EF Core SQLite provider cannot ORDER BY or compare a DateTimeOffset column in
        // LINQ, so the session list is composed as parameterized SQL over the sortable UTC
        // text columns. Every {n} placeholder is bound to the positional parameter array
        // (injection-safe); no user-supplied value is ever concatenated into the SQL text.
        List<object> parameters = [];

        string Bind(object value)
        {
            string placeholder = string.Concat("{", parameters.Count.ToString(CultureInfo.InvariantCulture), "}");

            parameters.Add(value);

            return placeholder;
        }

        List<string> conditions = [];

        if (!string.Equals(statusFilter, "all", StringComparison.OrdinalIgnoreCase))
        {
            conditions.Add($"\"Status\" = {Bind(statusFilter)}");
        }

        if (request.CampaignId is Guid campaignId)
        {
            conditions.Add($"\"CampaignId\" = {Bind(campaignId)}");
        }

        if (request.From is DateTimeOffset from)
        {
            conditions.Add($"\"UpdatedAt\" >= {Bind(from.ToUniversalTime())}");
        }

        if (request.To is DateTimeOffset to)
        {
            conditions.Add($"\"UpdatedAt\" <= {Bind(to.ToUniversalTime())}");
        }

        if (!string.IsNullOrWhiteSpace(request.Title))
        {
            string titlePattern = SqlLikePatterns.Contains(request.Title.Trim());

            conditions.Add(
                $"(\"Title\" IS NOT NULL AND \"Title\" LIKE {Bind(titlePattern)} ESCAPE {Bind(SqlLikePatterns.EscapeString)})");
        }

        if (searchTitlePattern is not null)
        {
            string titleClause =
                $"\"Title\" IS NOT NULL AND \"Title\" LIKE {Bind(searchTitlePattern)} ESCAPE {Bind(SqlLikePatterns.EscapeString)}";

            if (ftsMatchQuery is not null)
            {
                conditions.Add(
                    $"(({titleClause}) OR EXISTS ("
                    + "SELECT 1 FROM \"Entries_fts\" "
                    + "INNER JOIN \"Entries\" AS fts_entry ON fts_entry.\"Id\" = \"Entries_fts\".\"Id\" "
                    + "WHERE fts_entry.\"SessionId\" = \"Sessions\".\"Id\" "
                    + $"AND \"Entries_fts\" MATCH {Bind(ftsMatchQuery)}))");
            }
            else
            {
                conditions.Add($"({titleClause})");
            }
        }

        if (request.Role is MessageRole role)
        {
            conditions.Add(
                $"EXISTS (SELECT 1 FROM \"Entries\" AS e WHERE e.\"SessionId\" = \"Sessions\".\"Id\" AND e.\"Role\" = {Bind((int)role)})");
        }

        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            string modelPattern = SqlLikePatterns.EscapeLiteral(request.Model.Trim());

            conditions.Add(
                $"EXISTS (SELECT 1 FROM \"Entries\" AS e WHERE e.\"SessionId\" = \"Sessions\".\"Id\" AND e.\"ModelUsed\" LIKE {Bind(modelPattern)} ESCAPE {Bind(SqlLikePatterns.EscapeString)})");
        }

        if (request.BeforeUpdatedAt is DateTimeOffset before)
        {
            conditions.Add($"\"UpdatedAt\" < {Bind(before.ToUniversalTime())}");
        }

        StringBuilder sqlBuilder = new();

        sqlBuilder.Append("SELECT * FROM \"Sessions\"");

        if (conditions.Count > 0)
        {
            sqlBuilder.Append(" WHERE ");

            sqlBuilder.Append(string.Join(" AND ", conditions));
        }

        // Snapshotted before the LIMIT parameter is bound, so the tie-completion query below can reuse
        // exactly the filter placeholders and nothing else.
        List<object> conditionParameters = [.. parameters];

        // "Id" is the identity tie-breaker. Without it the sort is undefined among sessions sharing an
        // "UpdatedAt" (a burst of creations, or a backup import that replays original timestamps
        // verbatim), so a tie straddling a page boundary would be ordered differently on each query and
        // the keyset cursor below could not reason about it at all.
        sqlBuilder.Append($" ORDER BY \"UpdatedAt\" DESC, \"Id\" DESC LIMIT {Bind(limit + 1)}");

        List<Session> page = await db.Sessions
            .FromSqlRaw(sqlBuilder.ToString(), parameters.ToArray())
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        bool hasMore = page.Count > limit;

        if (hasMore)
        {
            // The cursor this method hands back is a bare timestamp consumed as a strict
            // "UpdatedAt" < @before, so any session sharing the boundary timestamp with the last row of
            // this page would be excluded from the next page and become permanently unreachable. Rather
            // than widen the wire contract, the page is cut at a tie boundary: every row sharing the
            // first excluded row's timestamp is deferred to the next page, which the strict predicate
            // then admits in full.
            DateTimeOffset boundary = page[limit].UpdatedAt;

            List<Session> kept = page.Take(limit).Where(s => s.UpdatedAt != boundary).ToList();

            // Degenerate case: the entire page is one timestamp, so cutting at the tie boundary would
            // return nothing and leave the cursor exactly where it started. Widen to the complete tie
            // group instead — the page exceeds the requested limit, but it is whole and the cursor still
            // advances past it.
            page = kept.Count > 0
                ? kept
                : await LoadTieGroupAsync(conditions, conditionParameters, boundary, ct).ConfigureAwait(false);
        }

        Guid[] sessionIds = page.Select(s => s.Id).ToArray();

        Dictionary<Guid, int> entryCounts = [];

        if (sessionIds.Length > 0)
        {

            // Aggregated server-side: EF translates this to a single GROUP BY covered by
            // IX_Entries_SessionId_CreatedAt, returning one row per session. Counting in managed memory
            // instead would stream one Guid per Entry of every listed session — work proportional to the
            // sessions' whole transcript history rather than to the page size.
            entryCounts = await db.Entries
                .AsNoTracking()
                .Where(e => sessionIds.Contains(e.SessionId))
                .GroupBy(e => e.SessionId)
                .Select(g => new SessionEntryCountRow(g.Key, g.Count()))
                .ToDictionaryAsync(x => x.SessionId, x => x.Count, ct)
                .ConfigureAwait(false);

        }

        SessionSummaryDto[] summaries = page
            .Select(s => new SessionSummaryDto(
                s.Id,
                s.CampaignId,
                s.Title,
                s.Status,
                entryCounts.GetValueOrDefault(s.Id),
                s.CreatedAt,
                s.UpdatedAt,
                s.ForkedFromSessionId))
            .ToArray();

        DateTimeOffset? nextBefore = hasMore && page.Count > 0 ? page[^1].UpdatedAt : null;

        return new SessionQueryResult(summaries, nextBefore, hasMore);
    }

    /// <summary>
    /// Loads every session matching the current list filters whose <c>UpdatedAt</c> is exactly
    /// <paramref name="boundary"/>, so a page can never be cut through the middle of a group of sessions
    /// sharing one timestamp.
    /// </summary>
    /// <remarks>
    /// Only reached when a whole page turned out to be a single timestamp, which the bare-timestamp
    /// cursor cannot express a position inside of. Returning the complete group costs one extra query in
    /// that rare case and guarantees the strict <c>"UpdatedAt" &lt; @before</c> cursor advances past it
    /// without skipping a sibling.
    /// </remarks>
    private async Task<List<Session>> LoadTieGroupAsync(
        List<string> conditions,
        List<object> parameters,
        DateTimeOffset boundary,
        CancellationToken ct)
    {
        List<object> tieParameters = [.. parameters];

        List<string> tieConditions =
        [
            .. conditions,
            $"\"UpdatedAt\" = {{{tieParameters.Count.ToString(CultureInfo.InvariantCulture)}}}",
        ];

        tieParameters.Add(boundary.ToUniversalTime());

        // One more than the bound, so an oversized group is detected rather than silently clipped.
        // Clipping would leave the cursor on the boundary timestamp and make the rest of the group
        // permanently unreachable through the strict "UpdatedAt" < @before predicate, so the bound
        // has to fail loudly instead.
        string sql =
            "SELECT * FROM \"Sessions\" WHERE "
            + string.Join(" AND ", tieConditions)
            + " ORDER BY \"UpdatedAt\" DESC, \"Id\" DESC LIMIT "
            + (MaxTieGroupWidening + 1).ToString(CultureInfo.InvariantCulture);

        List<Session> group = await db.Sessions
            .FromSqlRaw(sql, tieParameters.ToArray())
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (group.Count > MaxTieGroupWidening)
        {
            throw new InvalidOperationException(
                $"More than {MaxTieGroupWidening.ToString(CultureInfo.InvariantCulture)} sessions share "
                + "the timestamp at this page boundary. The session-list cursor is a bare timestamp and "
                + "cannot express a position inside a tie group, so a group this large cannot be paged; "
                + "narrow the query with a status, campaign, or date filter.");
        }

        return group;
    }

    public async Task<SessionAnalytics> GetAnalyticsAsync(CancellationToken ct)
    {

        var sessionStats = await db.Sessions
            .AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new SessionStatsRow(
                g.Count(),
                g.Sum(s => s.Status == "active" ? 1 : 0),
                g.Sum(s => s.Status == "archived" ? 1 : 0),
                g.Sum(s => s.TotalTokensUsed)))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var entryStats = await db.Entries
            .AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new EntryStatsRow(
                g.Count(),
                g.Sum(e => e.Role == MessageRole.User ? 1 : 0),
                g.Sum(e => e.Role == MessageRole.Assistant ? 1 : 0),
                g.Sum(e => e.Role == MessageRole.Tool ? 1 : 0),
                g.Sum(e => e.Role == MessageRole.System ? 1 : 0)))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        Dictionary<string, int> entriesByModel = await db.Entries
            .AsNoTracking()
            .Where(e => e.ModelUsed != "")
            .GroupBy(e => e.ModelUsed)
            .Select(g => new ModelEntryCountRow(g.Key, g.Count()))
            .ToDictionaryAsync(x => x.Model, x => x.Count, StringComparer.OrdinalIgnoreCase, ct)
            .ConfigureAwait(false);

        return new SessionAnalytics(
            sessionStats?.TotalSessions ?? 0,
            sessionStats?.ActiveSessions ?? 0,
            sessionStats?.ArchivedSessions ?? 0,
            entryStats?.TotalEntries ?? 0,
            entryStats?.UserEntries ?? 0,
            entryStats?.AssistantEntries ?? 0,
            entryStats?.ToolEntries ?? 0,
            entryStats?.SystemEntries ?? 0,
            sessionStats?.TotalTokensUsed ?? 0L,
            entriesByModel);

    }

    public async Task<Result<SessionExportResult>> ExportAsync(Guid id, SessionExportFormat format, CancellationToken ct)
    {
        Session? session = await db.Sessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id, ct)
            .ConfigureAwait(false);

        if (session is null)
        {
            return Result<SessionExportResult>.Failure(
                new Error(ErrorCodes.Session.NotFound, "No session exists with that id."));
        }

        return format switch
        {
            SessionExportFormat.Json => Result<SessionExportResult>.Success(new SessionExportResult(
                id,
                "json",
                await SerializeJsonExportAsync(session, id, ct).ConfigureAwait(false),
                "application/json")),

            SessionExportFormat.Markdown => Result<SessionExportResult>.Success(new SessionExportResult(
                id,
                "markdown",
                await FormatMarkdownExportAsync(session, id, ct).ConfigureAwait(false),
                "text/markdown")),

            _ => Result<SessionExportResult>.Failure(
                new Error(ErrorCodes.Session.InvalidFormat, "The export format is not supported.")),
        };
    }

    public async Task<Result<Entry>> AddEntryAsync(Guid sessionId, Entry entry, CancellationToken ct)
    {

        using IDisposable _ = await SessionEntryPersistence.AcquireWriteLockAsync(sessionId, ct).ConfigureAwait(false);

        Session? session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct).ConfigureAwait(false);

        if (session is null)
        {

            return Result<Entry>.Failure(new Error(ErrorCodes.Session.NotFound, "Session was not found."));

        }

        if (string.Equals(session.Status, "archived", StringComparison.OrdinalIgnoreCase))
        {

            return Result<Entry>.Failure(
                new Error(ErrorCodes.Session.Archived, "Cannot append entries to an archived session."));

        }

        int entryCount = await _entryPersistence.GetEntryCountAsync(sessionId, ct).ConfigureAwait(false);

        SessionSettings sessionSettings = optionsMonitor.CurrentValue.ResolveSessions();

        Error? limitError = SessionEntryPersistence.CheckEntryLimits(entryCount, entriesToAdd: 1, sessionSettings, entry.Content);

        if (limitError is not null)
        {

            return Result<Entry>.Failure(limitError.Value);

        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        entry.SessionId = sessionId;

        if (entry.CreatedAt == default)
        {

            entry.CreatedAt = now;

        }

        entry.Sequence = await _entryPersistence
            .ReserveSequenceRangeAsync(sessionId, count: 1, ct)
            .ConfigureAwait(false);

        db.Entries.Add(entry);

        session.UpdatedAt = now;

        if (string.IsNullOrWhiteSpace(session.Title)
            && entry.Role == MessageRole.User
            && !string.IsNullOrWhiteSpace(entry.Content))
        {

            session.Title = TruncateTitle(entry.Content);

        }

        // Maintain the unsummarized-entry counter so The Forge append path no longer drifts
        // it (the inference path already does this). -1 means "unknown legacy"; leave it.
        // Both statements share one transaction so a crash between them cannot leave the entry
        // durable with the counter unbumped — the shape every sibling append path already uses.
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx =
            await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        try
        {

            await _entryPersistence.SaveChangesWithRetryAsync(ct).ConfigureAwait(false);

            await _entryPersistence.IncrementUnsummarizedEntryCountIfKnownAsync(sessionId, 1, ct).ConfigureAwait(false);

            await tx.CommitAsync(ct).ConfigureAwait(false);

        }
        catch
        {

            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

            throw;

        }

        return Result<Entry>.Success(entry);
    }

    public async Task<Result<Session>> ForkAsync(Guid sourceId, ForkSessionRequest request, CancellationToken ct)
    {

        Guid forkSessionId = Guid.NewGuid();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Acquire source then fork session gates (stable Guid order to avoid deadlocks with concurrent purge).
        Guid firstGate = sourceId.CompareTo(forkSessionId) <= 0 ? sourceId : forkSessionId;

        Guid secondGate = firstGate == sourceId ? forkSessionId : sourceId;

        using IDisposable gate1 = await attachments.AcquireSessionGateAsync(firstGate, ct).ConfigureAwait(false);

        using IDisposable gate2 = await attachments.AcquireSessionGateAsync(secondGate, ct).ConfigureAwait(false);

        Session? source = await db.Sessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sourceId, ct)
            .ConfigureAwait(false);

        if (source is null)
        {

            return Result<Session>.Failure(new Error(
                ErrorCodes.Session.NotFound,
                "No session exists with that id."));

        }

        Entry? cutoffEntry = null;

        if (request.UpToEntryId is Guid cutoffId)
        {

            cutoffEntry = await db.Entries
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.SessionId == sourceId && e.Id == cutoffId, ct)
                .ConfigureAwait(false);

            if (cutoffEntry is null)
            {

                return Result<Session>.Failure(new Error(
                    ErrorCodes.Session.EntryNotFound,
                    "upToEntryId does not identify an entry in the source session."));

            }

        }

        long maximumSourceEntrySequence = cutoffEntry?.Sequence
            ?? await db.Entries
                .AsNoTracking()
                .Where(entry => entry.SessionId == sourceId)
                .Select(entry => (long?)entry.Sequence)
                .MaxAsync(ct)
                .ConfigureAwait(false)
            ?? 0L;

        Session fork = new()
        {
            Id = forkSessionId,
            CampaignId = request.CampaignId ?? source.CampaignId,
            Title = string.IsNullOrWhiteSpace(request.Title) ? BuildForkTitle(source.Title) : request.Title.Trim(),
            Status = "active",
            CreatedAt = now,
            UpdatedAt = now,
            Summary = null,
            LastSummarizedMessageAt = null,
            TotalTokensUsed = 0,
            UnsummarizedEntryCount = 0,
            ForkedFromSessionId = sourceId,
        };

        try
        {

            await foreach (IReadOnlyList<SessionAttachmentRecord> sourcePage in attachments
                               .ReadBoundForForkPagesAsync(
                                   sourceId,
                                   maximumSourceEntrySequence,
                                   includeEntrylessAttachments: cutoffEntry is null,
                                   ct)
                               .ConfigureAwait(false))
            {

                List<SessionAttachmentForkCopyPlan> copyPlans = BuildForkAttachmentPlans(
                    forkSessionId,
                    sourcePage);

                await attachments
                    .CopyBytesForForkAsync(forkSessionId, copyPlans, ct)
                    .ConfigureAwait(false);

            }

            await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx =
                await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

            try
            {

                db.Sessions.Add(fork);

                await _entryPersistence.SaveChangesWithRetryAsync(ct).ConfigureAwait(false);

                int copiedEntryCount = 0;

                await foreach (List<Entry> sourcePage in ReadEntryBatchesAsync(
                                   sourceId,
                                   ct,
                                   ForkEntryBatchSize,
                                   maximumSourceEntrySequence).ConfigureAwait(false))
                {

                    List<Entry> entryCopies = new(sourcePage.Count);

                    foreach (Entry sourceEntry in sourcePage)
                    {

                        entryCopies.Add(new Entry
                        {
                            Id = CreateForkEntryId(forkSessionId, sourceEntry.Id),
                            SessionId = forkSessionId,
                            Role = sourceEntry.Role,
                            Content = sourceEntry.Content,
                            ModelUsed = sourceEntry.ModelUsed,
                            CreatedAt = sourceEntry.CreatedAt,
                            Sequence = sourceEntry.Sequence,
                            ToolCallId = sourceEntry.ToolCallId,
                            ToolName = sourceEntry.ToolName,
                            ToolArguments = sourceEntry.ToolArguments,
                        });

                    }

                    db.Entries.AddRange(entryCopies);

                    await _entryPersistence.SaveChangesWithRetryAsync(ct).ConfigureAwait(false);

                    copiedEntryCount = checked(copiedEntryCount + entryCopies.Count);

                    foreach (Entry entryCopy in entryCopies)
                    {

                        db.Entry(entryCopy).State = EntityState.Detached;

                    }

                }

                fork.UnsummarizedEntryCount = copiedEntryCount;

                await _entryPersistence.SaveChangesWithRetryAsync(ct).ConfigureAwait(false);

                await foreach (IReadOnlyList<SessionAttachmentRecord> sourcePage in attachments
                                   .ReadBoundForForkPagesAsync(
                                       sourceId,
                                       maximumSourceEntrySequence,
                                       includeEntrylessAttachments: cutoffEntry is null,
                                       ct)
                                   .ConfigureAwait(false))
                {

                    List<SessionAttachmentForkCopyPlan> insertPlans = BuildForkAttachmentPlans(
                        forkSessionId,
                        sourcePage);

                    await attachments
                        .InsertForkRowsInAmbientTransactionAsync(
                            forkSessionId,
                            insertPlans,
                            ct)
                        .ConfigureAwait(false);

                }

                await tx.CommitAsync(ct).ConfigureAwait(false);

            }
            catch
            {

                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

                DetachFailedForkEntities(forkSessionId, fork);

                throw;

            }

        }
        catch
        {

            _ = attachments.TryDeleteSessionDirectory(forkSessionId);

            throw;

        }

        if (attachmentIndexQueue is not null)
        {

            await foreach (IReadOnlyList<SessionAttachmentRecord> sourcePage in attachments
                               .ReadBoundForForkPagesAsync(
                                   sourceId,
                                   maximumSourceEntrySequence,
                                   includeEntrylessAttachments: cutoffEntry is null,
                                   CancellationToken.None)
                               .ConfigureAwait(false))
            {

                foreach (SessionAttachmentRecord sourceAttachment in sourcePage)
                {

                    _ = attachmentIndexQueue.TryEnqueue(new SessionAttachmentIndexRequest(
                        CreateForkAttachmentId(forkSessionId, sourceAttachment.Id),
                        forkSessionId));

                }

            }

        }

        return Result<Session>.Success(fork);

    }

    private static List<SessionAttachmentForkCopyPlan> BuildForkAttachmentPlans(
        Guid forkSessionId,
        IReadOnlyList<SessionAttachmentRecord> sourcePage)
    {

        List<SessionAttachmentForkCopyPlan> plans = new(sourcePage.Count);

        foreach (SessionAttachmentRecord sourceAttachment in sourcePage)
        {

            Guid? newEntryId = sourceAttachment.EntryId is Guid sourceEntryId
                ? CreateForkEntryId(forkSessionId, sourceEntryId)
                : null;

            plans.Add(new SessionAttachmentForkCopyPlan(
                sourceAttachment,
                CreateForkAttachmentId(forkSessionId, sourceAttachment.Id),
                newEntryId));

        }

        return plans;

    }

    private static Guid CreateForkEntryId(Guid forkSessionId, Guid sourceEntryId) =>
        CreateForkScopedId(forkSessionId, sourceEntryId, discriminator: 1);

    private static Guid CreateForkAttachmentId(Guid forkSessionId, Guid sourceAttachmentId) =>
        CreateForkScopedId(forkSessionId, sourceAttachmentId, discriminator: 2);

    private static Guid CreateForkScopedId(
        Guid forkSessionId,
        Guid sourceId,
        byte discriminator)
    {

        Span<byte> material = stackalloc byte[33];

        _ = forkSessionId.TryWriteBytes(material[..16]);

        _ = sourceId.TryWriteBytes(material[16..32]);

        material[32] = discriminator;

        Span<byte> digest = stackalloc byte[32];

        _ = SHA256.HashData(material, digest);

        return new Guid(digest[..16]);

    }

    private void DetachFailedForkEntities(Guid forkSessionId, Session fork)
    {

        foreach (Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<Entry> trackedEntry in db
                     .ChangeTracker
                     .Entries<Entry>()
                     .Where(entry => entry.Entity.SessionId == forkSessionId)
                     .ToArray())
        {

            trackedEntry.State = EntityState.Detached;

        }

        Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<Session> forkEntry = db.Entry(fork);

        if (forkEntry.State != EntityState.Detached)
        {

            forkEntry.State = EntityState.Detached;

        }

    }

    private static string BuildForkTitle(string? sourceTitle) =>
        string.IsNullOrWhiteSpace(sourceTitle) ? "Forked session" : $"Fork of {sourceTitle}";

    public async Task<List<Entry>> GetEntriesAscendingAsync(Guid sessionId, int takeLast, CancellationToken ct = default)
    {
        int clampedTake = EntryWindowPolicy.ResolveTake(
            EntryWindowPolicy.EntryWindowKind.RawTakeLast,
            maxMessages: 0,
            requestedTake: takeLast);

        List<Entry> recentDescending = await EntryTemporalQueries
            .LoadRecentDescending(db, sessionId, clampedTake)
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        recentDescending.Reverse();

        return recentDescending;
    }

    public async Task<Entry?> GetEntryAsync(Guid sessionId, Guid entryId, CancellationToken ct = default) =>
        await db.Entries
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.SessionId == sessionId && e.Id == entryId, ct)
            .ConfigureAwait(false);

    public async Task<List<Entry>> GetEntriesAfterAsync(
        Guid sessionId,
        long afterSequence,
        int limit,
        CancellationToken ct = default)
    {
        int clampedLimit = ArcanumSettingClamps.SessionStreamReplayLimit(limit);

        return await EntryTemporalQueries
            .LoadAfterSequence(db, sessionId, afterSequence, clampedLimit)
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<List<Entry>> GetEntriesAsync(
        Guid sessionId,
        int offset = 0,
        int limit = 100,
        DateTimeOffset? beforeCreatedAt = null,
        Guid? beforeId = null,
        CancellationToken ct = default)
    {
        int clampedLimit = Math.Clamp(limit, 1, 1000);

        if (beforeCreatedAt is DateTimeOffset beforeAt && beforeId is Guid beforeEntryId)
        {

            long? beforeSequence = await EntryTemporalQueries
                .SequenceOf(db, sessionId, beforeEntryId)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            // A cursor entry deleted since the client read it has no sequence to page from, so fall
            // back to its timestamp/id position rather than reporting the end of the transcript.
            IQueryable<Entry> page = beforeSequence is long cursorSequence
                ? EntryTemporalQueries.LoadBeforeSequence(db, sessionId, cursorSequence, clampedLimit)
                : EntryTemporalQueries.LoadBeforeDeletedKeyset(db, sessionId, beforeAt, beforeEntryId, clampedLimit);

            return await page
                .AsNoTracking()
                .ToListAsync(ct)
                .ConfigureAwait(false);

        }

        int clampedOffset = Math.Max(0, offset);

        return await EntryTemporalQueries
            .LoadDescendingPaged(db, sessionId, clampedLimit, clampedOffset)
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public Task<int> GetEntryCountAsync(Guid sessionId, CancellationToken ct) =>
        db.Entries.CountAsync(e => e.SessionId == sessionId, ct);

    public async Task UpdateSessionAsync(Session session, CancellationToken ct)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Patch only Forge-owned scalar fields; Grimoire-owned counters and rollups are excluded.
        _ = await db.Sessions
            .Where(s => s.Id == session.Id)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(x => x.Title, session.Title)
                    .SetProperty(x => x.Status, session.Status)
                    .SetProperty(x => x.UpdatedAt, now),
                ct)
            .ConfigureAwait(false);

        session.UpdatedAt = now;
    }

    public async Task ArchiveAsync(Guid id, CancellationToken ct)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        _ = await db.Sessions
            .Where(s => s.Id == id)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(x => x.Status, "archived")
                    .SetProperty(x => x.UpdatedAt, now),
                ct)
            .ConfigureAwait(false);
    }

    private async Task<string> SerializeJsonExportAsync(Session session, Guid sessionId, CancellationToken ct)
    {
        // W3.4 Group E #10: stream-serialize the export instead of accumulating every entry
        // batch into one List<Entry> before serializing. Each batch's entries are written to a
        // Utf8JsonWriter as they are read, so the peak managed-memory pressure is one batch
        // (ExportEntryBatchSize) rather than the whole session. The wire shape is identical to
        // the previous JsonSerializer.Serialize(SessionExportPayload) output: a camelCase
        // { "session": {...}, "entries": [...] } object. The endpoint buffers the result into a
        // string (SessionExportResult.Content), so the output string is still O(total) — that
        // is inherent to the wire contract, not the accumulation. Source-generated type infos
        // (ArcanumCoreJsonContext) keep this AOT-safe.
        using MemoryStream buffer = new();

        using Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = false });

        writer.WriteStartObject();

        writer.WritePropertyName("session");

        JsonSerializer.Serialize(writer, session, ArcanumCoreJsonContext.Default.Session);

        writer.WritePropertyName("entries");

        writer.WriteStartArray();

        await foreach (List<Entry> batch in ReadEntryBatchesAsync(sessionId, ct).ConfigureAwait(false))
        {

            foreach (Entry entry in batch)
            {

                JsonSerializer.Serialize(writer, entry, ArcanumCoreJsonContext.Default.Entry);

            }

        }

        writer.WriteEndArray();

        writer.WriteEndObject();

        await writer.FlushAsync(ct).ConfigureAwait(false);

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private async Task<string> FormatMarkdownExportAsync(Session session, Guid sessionId, CancellationToken ct)
    {
        StringBuilder builder = new();

        builder.Append("# ");

        builder.AppendLine(session.Title ?? "Untitled session");

        builder.AppendLine();

        await foreach (List<Entry> batch in ReadEntryBatchesAsync(sessionId, ct).ConfigureAwait(false))
        {
            AppendMarkdownEntries(builder, batch);
        }

        return builder.ToString();
    }

    private async IAsyncEnumerable<List<Entry>> ReadEntryBatchesAsync(
        Guid sessionId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct,
        int batchSize = ExportEntryBatchSize,
        long maximumSequence = long.MaxValue)
    {
        long cursorSequence = 0L;

        while (true)
        {
            List<Entry> batch = await EntryTemporalQueries
                .LoadAfterSequence(db, sessionId, cursorSequence, batchSize)
                .Where(entry => entry.Sequence <= maximumSequence)
                .AsNoTracking()
                .ToListAsync(ct)
                .ConfigureAwait(false);

            if (batch.Count == 0)
            {
                yield break;
            }

            yield return batch;

            Entry last = batch[^1];

            cursorSequence = last.Sequence;

            if (batch.Count < batchSize)
            {
                yield break;
            }
        }
    }

    private static void AppendMarkdownEntries(StringBuilder builder, IReadOnlyList<Entry> entries)
    {
        foreach (Entry entry in entries)
        {
            builder.Append("## ");

            builder.Append(entry.Role.ToString().ToLowerInvariant());

            builder.Append(" — ");

            builder.AppendLine(entry.CreatedAt.ToString("O"));

            builder.AppendLine();

            builder.AppendLine(entry.Content);

            builder.AppendLine();
        }
    }

    private static string TruncateTitle(string content)
    {
        string trimmed = content.Trim();

        const int maxLen = 80;

        if (trimmed.Length <= maxLen)
        {
            return trimmed;
        }

        int cut = trimmed.LastIndexOf(' ', maxLen);

        if (cut < 20)
        {
            cut = maxLen;
        }

        return trimmed[..cut].TrimEnd() + "...";
    }

    /// <summary>
    /// Named shapes for the aggregate projections above.
    /// </summary>
    /// <remarks>
    /// Not anonymous types, and not merely for style. <c>Select(g =&gt; new { ... })</c> compiles to
    /// the <c>Expression.New(ConstructorInfo, IEnumerable&lt;Expression&gt;, MemberInfo[])</c>
    /// overload, which carries <c>RequiresUnreferencedCode</c> because the member metadata it maps
    /// through can be trimmed — four IL2026 warnings out of this one file, and the reason the
    /// repository could not pass the Native AOT IL gate. Projecting into a named record uses the
    /// plain constructor overload instead, which has no such attribute. It is also what convention 1
    /// in AGENTS.md already required: no anonymous DTOs.
    /// </remarks>
    private sealed record SessionEntryCountRow(Guid SessionId, int Count);

    private sealed record ModelEntryCountRow(string Model, int Count);

    private sealed record SessionStatsRow(
        int TotalSessions,
        int ActiveSessions,
        int ArchivedSessions,
        long TotalTokensUsed);

    private sealed record EntryStatsRow(
        int TotalEntries,
        int UserEntries,
        int AssistantEntries,
        int ToolEntries,
        int SystemEntries);

    /// <summary>
    /// The placeholder a repository composed without connection admission carries.
    /// </summary>
    /// <remarks>
    /// It refuses by throwing. The version it replaces answered both members with a failed
    /// <see cref="Result" />, which is the shape that made an unconfigured repository look like a
    /// failing write instead of a missing dependency.
    ///
    /// <para>Exactly one composition passes the real factory: the <c>AddScoped&lt;ISessionRepository&gt;</c>
    /// registration in <c>ServiceCollectionExtensions.AddArcanumInfrastructure</c>, which resolves the
    /// singleton <c>IGrimoireOrdinaryConnectionFactory</c> into the internal constructor above. Every
    /// serving path therefore reaches admission; nothing that reaches this placeholder is serving.</para>
    ///
    /// <para>It is dormant rather than merely unused: the only two members that consult the factory
    /// are <c>SessionEntryPersistence.ReadProbeFreshAsync</c> and
    /// <c>ReadReceiptOnFreshConnectionAsync</c>, the mandatory-interaction fresh readbacks, and this
    /// type calls neither. A future caller of either from a repository built through the primary
    /// constructor throws at first use rather than at construction — deliberately loud, since the
    /// alternative is reporting a missing dependency as a storage outcome. Deleting the placeholder
    /// outright needs either a <c>SessionEntryPersistence</c> that can be built without a factory, or
    /// the factory as a required parameter of this type's primary constructor, which would make the
    /// type internal and change every hand-construction site.</para>
    /// </remarks>
    private sealed class UncomposedOrdinaryConnectionAdmission
        : IGrimoireOrdinaryConnectionFactory
    {

        internal static readonly UncomposedOrdinaryConnectionAdmission Instance = new();

        [GrimoireConnectionAcquisitionRoute]
        public Task<Result<IGrimoireOrdinaryConnectionLease>> AcquireScopedAsync(
            SqliteConnection connection,
            CovenantSqliteConnectionMode mode,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "This session repository was composed without ordinary Grimoire connection admission.");

        [GrimoireConnectionAcquisitionRoute]
        public Task<Result<IGrimoireOrdinaryConnectionLease>> OpenFreshAsync(
            GrimoireOrdinaryFreshConnectionKind kind,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "This session repository was composed without ordinary Grimoire connection admission.");

    }

}
