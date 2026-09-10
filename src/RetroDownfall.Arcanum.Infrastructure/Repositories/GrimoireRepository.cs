using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Repositories;

public sealed partial class GrimoireRepository : IGrimoireRepository
{
    private const int MaxLegacyBackfillPerSweep = 200;

    private readonly ArcanumDbContext _db;

    private readonly SessionEntryPersistence _entryPersistence;

    private readonly IGrimoireOrdinaryConnectionFactory _connections;

    private readonly ISessionAttachmentStore _attachments;

    private readonly ILogger<GrimoireRepository> _logger;

    private readonly IOptionsSnapshot<ArcanumSettings> _arcOptions;

    private readonly ISessionAttachmentIndexMaintenance? _attachmentIndex;

    /// <summary>
    /// The Covenant publisher, absent in hosts that compose no Covenant tier.
    /// </summary>
    /// <remarks>
    /// Optional rather than required so a lightweight host, a migration path, or a suite with no
    /// Covenant schema can still finalize turns. Absence refuses publication explicitly instead of
    /// silently dropping a staged batch.
    /// </remarks>
    private readonly CovenantMutationKernel? _covenantKernel;

    /// <summary>
    /// The labelled-artifact check every raw delete on this repository passes first.
    /// </summary>
    /// <remarks>
    /// Required, unlike the Covenant kernel above it. It was optional once, and both factory
    /// registrations then simply stopped short of supplying it, so the refusal in
    /// <see cref="DeleteEntryAsync" /> was unreachable in every composed host while the design
    /// documented it as live. A guard whose absence is representable is a guard some composition will
    /// eventually be missing, and nothing about that composition will look wrong.
    /// </remarks>
    private readonly ICovenantLabeledArtifactGuard _labeledArtifactGuard;

    /// <summary>
    /// The durable finalization-guard capacity ledger.
    /// </summary>
    /// <remarks>
    /// Always present, unlike the Covenant kernel. Guard capacity lives in the always-installed core
    /// tier and its schema trigger refuses a guard with no consumed reservation, so a host with no
    /// Covenant capability still has to move these counters to finalize an ordinary turn.
    /// </remarks>
    private readonly CovenantQuotaGuard _finalizationCapacity;

    internal Func<Guid, CancellationToken, ValueTask>? AfterLegacyBackfillCountedForTesting { get; set; }

    internal Func<Guid, CancellationToken, ValueTask>? AfterRollupRemainingCountedForTesting { get; set; }

    /// <summary>
    /// The only composition. Internal because both the Covenant mutation kernel and the
    /// ordinary-connection factory are Infrastructure implementation details: public parameters of
    /// those types would put the canonical write path and connection admission on the assembly's
    /// public surface.
    /// </summary>
    /// <remarks>
    /// There is deliberately no constructor that omits <paramref name="connections" /> or
    /// <paramref name="labeledArtifactGuard" />, and none that resolves either from an
    /// <see cref="IServiceProvider" />. The pair that did — a public one with an
    /// optional provider, and an internal one that hard-coded a refusing stand-in — meant a caller
    /// that named neither received a factory that refused every acquisition, so once the turn-commit
    /// path began acquiring, every such construction became a run-time refusal at the commit instead
    /// of a compile error at the call site. Naming the dependency is now the only way to build one.
    /// </remarks>
    internal GrimoireRepository(
        ArcanumDbContext db,
        ISessionAttachmentStore attachments,
        ILogger<GrimoireRepository> logger,
        IOptionsSnapshot<ArcanumSettings> arcOptions,
        ISessionAttachmentIndexMaintenance? attachmentIndex,
        CovenantMutationKernel? covenantKernel,
        IGrimoireOrdinaryConnectionFactory connections,
        ICovenantLabeledArtifactGuard labeledArtifactGuard)
    {
        _db = db;

        _connections = connections;

        _entryPersistence = new SessionEntryPersistence(db, connections);

        _attachments = attachments;

        _logger = logger;

        _arcOptions = arcOptions;

        _attachmentIndex = attachmentIndex;

        _covenantKernel = covenantKernel;

        _labeledArtifactGuard = labeledArtifactGuard;

        _finalizationCapacity = new CovenantQuotaGuard();
    }

    public async Task<(Guid SessionId, Guid AssistantEntryId)> BeginAssistantReplyAsync(
        Guid? sessionId,
        string prompt,
        string model,
        CancellationToken cancellationToken = default)
    {
        Guid userEntryId = Guid.NewGuid();
        Guid assistantEntryId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool useExistingThread = sessionId is { } existingId
            && await SessionExistsCoreAsync(existingId, cancellationToken).ConfigureAwait(false);
        if (useExistingThread)
        {
            Guid sid = sessionId!.Value;

            using IDisposable _ = await SessionEntryPersistence.AcquireWriteLockAsync(sid, cancellationToken).ConfigureAwait(false);

            await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx =
                await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                int entryCount = await _entryPersistence.GetEntryCountAsync(sid, cancellationToken).ConfigureAwait(false);

                Error? limitError = SessionEntryPersistence.CheckEntryLimits(entryCount, entriesToAdd: 2, GetSessionSettings(), prompt, string.Empty);

                if (limitError is not null)
                {
                    throw new InvalidOperationException(limitError.Value.Message);
                }

                long turnSequence = await _entryPersistence
                    .ReserveSequenceRangeAsync(sid, count: 2, cancellationToken)
                    .ConfigureAwait(false);

                await _entryPersistence.InsertEntriesAsync(
                    [
                        new Entry
                        {
                            Id = userEntryId,
                            SessionId = sid,
                            Role = MessageRole.User,
                            Content = prompt,
                            ModelUsed = model,
                            CreatedAt = now,
                            Sequence = turnSequence,
                        },
                        new Entry
                        {
                            Id = assistantEntryId,
                            SessionId = sid,
                            Role = MessageRole.Assistant,
                            Content = string.Empty,
                            ModelUsed = model,
                            CreatedAt = now,
                            Sequence = turnSequence + 1L,
                        },
                    ],
                    cancellationToken).ConfigureAwait(false);

                await _entryPersistence.BumpSessionUpdatedAtAsync(sid, now, cancellationToken).ConfigureAwait(false);

                await _entryPersistence.IncrementUnsummarizedEntryCountIfKnownAsync(sid, 2, cancellationToken).ConfigureAwait(false);

                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

                return (sid, assistantEntryId);
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

                throw;
            }
        }

        Guid newSessionId = Guid.NewGuid();

        Error? newSessionLimitError = SessionEntryPersistence.CheckEntryLimits(0, entriesToAdd: 2, GetSessionSettings(), prompt, string.Empty);

        if (newSessionLimitError is not null)
        {
            throw new InvalidOperationException(newSessionLimitError.Value.Message);
        }

        await using IDbContextTransaction transaction =
            await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            _db.Sessions.Add(new Session
            {
                Id = newSessionId,
                CreatedAt = now,
                UpdatedAt = now,
                Status = "active",
                Title = TruncateTitle(prompt),

                // Seeded on the inserted row rather than bumped by a follow-up statement, so the counter
                // and the two entries land in the same SQLite transaction. A crash between the two
                // statements would otherwise leave a session the Campaign Logger never summarizes.
                UnsummarizedEntryCount = 2,
            });

            await SqliteBusyRetry.ExecuteAsync(
                () => _db.SaveChangesAsync(cancellationToken),
                cancellationToken).ConfigureAwait(false);

            await _entryPersistence.InsertEntriesAsync(
                [
                    new Entry
                    {
                        Id = userEntryId,
                        SessionId = newSessionId,
                        Role = MessageRole.User,
                        Content = prompt,
                        ModelUsed = model,
                        CreatedAt = now,
                        Sequence = 1L,
                    },
                    new Entry
                    {
                        Id = assistantEntryId,
                        SessionId = newSessionId,
                        Role = MessageRole.Assistant,
                        Content = string.Empty,
                        ModelUsed = model,
                        CreatedAt = now,
                        Sequence = 2L,
                    },
                ],
                cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

            throw;
        }

        return (newSessionId, assistantEntryId);
    }

    public async Task FinalizeAssistantEntryAsync(
        Guid assistantEntryId,
        string fullContent,
        CancellationToken cancellationToken = default)
    {
        _ = await FinalizeAssistantEntryWithFrontierAsync(
            assistantEntryId,
            fullContent,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<long?> FinalizeAssistantEntryWithFrontierAsync(
        Guid assistantEntryId,
        string fullContent,
        CancellationToken cancellationToken = default)
    {
        Guid sessionId = await ReadEntrySessionIdAsync(assistantEntryId, cancellationToken)
            .ConfigureAwait(false);

        using IDisposable _ = await SessionEntryPersistence.AcquireWriteLockAsync(sessionId, cancellationToken).ConfigureAwait(false);

        long? throughEntrySequence = await ReadSessionLatestEntrySequenceAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);

        int updated = await SqliteBusyRetry.ExecuteAsync(
            () => ExecuteNonQueryAsync(
                """
                UPDATE "Entries"
                SET "Content" = $content
                WHERE "Id" = $entryId;
                """,
                command =>
                {
                    GrimoireEntitySql.AddParameter(command, "$content", fullContent);
                    GrimoireEntitySql.AddParameter(command, "$entryId", GrimoireEntitySql.Format(assistantEntryId));
                },
                cancellationToken),
            cancellationToken).ConfigureAwait(false);

        if (updated == 0)
        {
            _logger.LogWarning(
                "FinalizeAssistantEntryAsync updated 0 rows for assistant entry {AssistantEntryId}.",
                assistantEntryId);

            throw new InvalidOperationException(
                "Assistant entry could not be finalized; no matching row was updated in Grimoire.");
        }

        return throughEntrySequence;
    }

    public async Task DiscardAssistantEntryAsync(
        Guid assistantEntryId,
        CancellationToken cancellationToken = default)
    {
        Entry? entry = await ReadEntryAsync(assistantEntryId, cancellationToken).ConfigureAwait(false);

        if (entry is null)
        {
            return;
        }

        if (entry.Role != MessageRole.Assistant)
        {
            _logger.LogWarning(
                "DiscardAssistantEntryAsync skipped entry {AssistantEntryId} because it is not an assistant row.",
                assistantEntryId);

            return;
        }

        if (!string.IsNullOrEmpty(entry.Content))
        {
            return;
        }

        using IDisposable _ = await SessionEntryPersistence.AcquireWriteLockAsync(entry.SessionId, cancellationToken).ConfigureAwait(false);

        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx =
            await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            Guid sessionId = entry.SessionId;

            int deleted = await SqliteBusyRetry.ExecuteAsync(
                () => ExecuteNonQueryAsync(
                    """
                    DELETE FROM "Entries"
                    WHERE "Id" = $entryId
                      AND "Role" = $assistantRole
                      AND "Content" = '';
                    """,
                    command =>
                    {
                        GrimoireEntitySql.AddParameter(command, "$entryId", GrimoireEntitySql.Format(assistantEntryId));
                        GrimoireEntitySql.AddParameter(command, "$assistantRole", (int)MessageRole.Assistant);
                    },
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);

            if (deleted == 0)
            {
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

                return;
            }

            await _entryPersistence.DecrementUnsummarizedEntryCountIfKnownAsync(sessionId, 1, cancellationToken).ConfigureAwait(false);

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

            throw;
        }
    }

    public async Task AppendToolInteractionAsync(
        Guid sessionId,
        string toolName,
        string arguments,
        string result,
        string modelUsed,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        using IDisposable _ = await SessionEntryPersistence.AcquireWriteLockAsync(sessionId, cancellationToken).ConfigureAwait(false);

        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx =
            await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string callLine = $"[ToolCall: {toolName}({arguments})]";

            string resultLine = $"[ToolResult: {result}]";

            int entryCount = await _entryPersistence.GetEntryCountAsync(sessionId, cancellationToken).ConfigureAwait(false);

            Error? toolLimitError = SessionEntryPersistence.CheckEntryLimits(entryCount, entriesToAdd: 2, GetSessionSettings(), callLine, resultLine);

            if (toolLimitError is not null)
            {
                throw new InvalidOperationException(toolLimitError.Value.Message);
            }

            long toolSequence = await _entryPersistence
                .ReserveSequenceRangeAsync(sessionId, count: 2, cancellationToken)
                .ConfigureAwait(false);

            await _entryPersistence.InsertEntriesAsync(
                [
                    new Entry
                    {
                        Id = Guid.NewGuid(),
                        SessionId = sessionId,
                        Role = MessageRole.Assistant,
                        Content = callLine,
                        ModelUsed = modelUsed,
                        CreatedAt = now,
                        Sequence = toolSequence,
                        ToolName = toolName,
                        ToolArguments = arguments,
                    },
                    new Entry
                    {
                        Id = Guid.NewGuid(),
                        SessionId = sessionId,
                        Role = MessageRole.System,
                        Content = resultLine,
                        ModelUsed = modelUsed,
                        CreatedAt = now,
                        Sequence = toolSequence + 1L,
                    },
                ],
                cancellationToken).ConfigureAwait(false);

            await _entryPersistence.BumpSessionUpdatedAtAsync(sessionId, now, cancellationToken).ConfigureAwait(false);

            await _entryPersistence.IncrementUnsummarizedEntryCountIfKnownAsync(sessionId, 2, cancellationToken).ConfigureAwait(false);

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public Task<MandatoryToolInteractionProbeResult>
        ProbeMandatoryToolInteractionAsync(
            MandatoryToolInteractionProbe probe,
            CancellationToken cancellationToken = default) =>
        _entryPersistence.ProbeMandatoryToolInteractionAsync(
            probe,
            cancellationToken);

    public Task<MandatoryToolInteractionPreflightResult>
        PreflightMandatoryToolInteractionAsync(
            MandatoryToolInteraction interaction,
            CancellationToken cancellationToken = default) =>
        _entryPersistence.PreflightMandatoryToolInteractionAsync(
            interaction,
            GetSessionSettings(),
            cancellationToken);

    public Task<MandatoryToolInteractionAppendResult> AppendMandatoryToolInteractionAsync(
        MandatoryToolInteraction interaction,
        CancellationToken cancellationToken = default) =>
        _entryPersistence.AppendMandatoryToolInteractionAsync(
            interaction,
            GetSessionSettings(),
            cancellationToken);

    public async Task SaveCompletedExchangeAsync(
        string userPrompt,
        string assistantText,
        string modelUsed,
        CancellationToken cancellationToken = default)
    {
        Guid sessionId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx =
            await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Error? exchangeLimitError = SessionEntryPersistence.CheckEntryLimits(0, entriesToAdd: 2, GetSessionSettings(), userPrompt, assistantText);

            if (exchangeLimitError is not null)
            {
                throw new InvalidOperationException(exchangeLimitError.Value.Message);
            }

            _db.Sessions.Add(new Session
            {
                Id = sessionId,
                CreatedAt = now,
                UpdatedAt = now,
                Status = "active",
                Title = TruncateTitle(userPrompt),
            });
            _db.Entries.Add(new Entry
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                Role = MessageRole.User,
                Content = userPrompt,
                ModelUsed = modelUsed,
                CreatedAt = now,
                Sequence = 1L,
            });
            _db.Entries.Add(new Entry
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                Role = MessageRole.Assistant,
                Content = assistantText,
                ModelUsed = modelUsed,
                CreatedAt = now,
                Sequence = 2L,
            });
            await SqliteBusyRetry.ExecuteAsync(
                () => _db.SaveChangesAsync(cancellationToken),
                cancellationToken).ConfigureAwait(false);

            await _entryPersistence.IncrementUnsummarizedEntryCountIfKnownAsync(sessionId, 2, cancellationToken).ConfigureAwait(false);

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<int> PurgeSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        using IDisposable entryLock = await SessionEntryPersistence.AcquireWriteLockAsync(sessionId, cancellationToken).ConfigureAwait(false);

        using IDisposable attachmentGate = await _attachments
            .AcquireSessionGateAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);

        await using var tx = await _db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        if (_attachmentIndex is not null)
        {
            await _attachmentIndex.DeleteForSessionInAmbientTransactionAsync(
                sessionId,
                cancellationToken).ConfigureAwait(false);
        }

        await SqliteBusyRetry.ExecuteAsync(
            () => _attachments.DeleteRowsForSessionInAmbientTransactionAsync(sessionId, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        await DeleteEntryEmbeddingsForSessionInAmbientTransactionAsync(
            sessionId,
            cancellationToken).ConfigureAwait(false);

        await SqliteBusyRetry.ExecuteAsync(
            () => ExecuteNonQueryAsync(
                """
                DELETE FROM "Entries"
                WHERE "SessionId" = $sessionId;
                """,
                command => GrimoireEntitySql.AddParameter(
                    command,
                    "$sessionId",
                    GrimoireEntitySql.Format(sessionId)),
                cancellationToken),
            cancellationToken).ConfigureAwait(false);

        int removed = await DeleteSessionRowInAmbientTransactionAsync(
            sessionId,
            cancellationToken).ConfigureAwait(false);

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

        if (!_attachments.TryDeleteSessionDirectory(sessionId))
        {
            _logger.LogWarning(
                "Purged session {SessionId} from Grimoire but attachment directory cleanup failed; reconcile will retry.",
                sessionId);
        }

        return removed;
    }

    /// <summary>
    /// Removes the Session row itself, under the retention authorization its cascade requires.
    /// </summary>
    /// <remarks>
    /// The Session owns its row in the turn capacity ledger, and that row leaves only through an
    /// authorized retention or capacity transaction. Its delete guard begins denied on every
    /// connection, including a pooled one handed back out, so the parent delete has to hold the
    /// scope itself. The scope covers the delete alone and is released before the caller commits,
    /// so nothing later in this transaction inherits it.
    /// </remarks>
    private async Task<int> DeleteSessionRowInAmbientTransactionAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
                    _db,
                    """
                    DELETE FROM "Sessions"
                    WHERE "Id" = $sessionId;
                    """,
                    cancellationToken).ConfigureAwait(false);

                GrimoireEntitySql.AddParameter(
                    command,
                    "$sessionId",
                    GrimoireEntitySql.Format(sessionId));

                using CovenantSqliteAuthorizationScope retention =
                    CovenantSqliteConnectionInitializer.Instance.Authorize(
                        command.Connection!,
                        CovenantSqliteAuthorizationKind.SessionRetention);

                return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteEntryEmbeddingsForSessionInAmbientTransactionAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        IDbContextTransaction? ambient = _db.Database.CurrentTransaction;

        if (ambient is null)
        {
            throw new InvalidOperationException("Entry embedding purge requires an ambient transaction.");
        }

        foreach (string table in new[] { "entry_embeddings_vec", "entry_embeddings" })
        {
            await using SqliteCommand exists = await GrimoireSqlCommandFactory.CreateAsync(
                _db,
                "SELECT 1 FROM sqlite_master WHERE type IN ('table', 'view') AND name = $table LIMIT 1;",
                cancellationToken).ConfigureAwait(false);

            GrimoireEntitySql.AddParameter(exists, "$table", table);

            if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
            {
                continue;
            }

            await using SqliteCommand delete = await GrimoireSqlCommandFactory.CreateAsync(
                _db,
                $"""
                DELETE FROM "{table}"
                WHERE lower(replace("EntryId", '-', '')) IN (
                    SELECT lower(replace(CAST("Id" AS TEXT), '-', ''))
                    FROM "Entries"
                    WHERE lower(replace(CAST("SessionId" AS TEXT), '-', '')) = @sessionId
                )
                """,
                cancellationToken).ConfigureAwait(false);

            GrimoireEntitySql.AddParameter(delete, "@sessionId", sessionId.ToString("N"));

            _ = await SqliteBusyRetry.ExecuteAsync(
                () => delete.ExecuteNonQueryAsync(cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<Session?> GetSessionAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        Session? session = await ReadSessionAsync(id, cancellationToken).ConfigureAwait(false);

        if (session is null)
        {
            return null;
        }

        int maxMessages = ArcanumSettingClamps.MaxMessagesPerConversationLoad(
            ArcanumRuntimeDefaults.Grimoire.MaxMessagesPerConversationLoad);

        DateTime? watermark = session.LastSummarizedMessageAt;

        int afterWatermarkCount = 0;

        if (watermark is { } watermarkValue)
        {
            afterWatermarkCount = await CountEntriesAfterAsync(
                id,
                new DateTimeOffset(watermarkValue, TimeSpan.Zero),
                cancellationToken).ConfigureAwait(false);
        }

        int take = EntryWindowPolicy.ResolveTake(
            EntryWindowPolicy.EntryWindowKind.WatermarkAware,
            maxMessages,
            hasWatermark: watermark is not null,
            afterWatermarkCount: afterWatermarkCount);

        List<Entry> recent = await EntryTemporalQueries
            .LoadRecentDescendingAsync(_db, id, take, cancellationToken)
            .ConfigureAwait(false);

        recent.Reverse();

        session.Entries = recent;

        return session;
    }

    public async Task<Session?> GetSessionHeaderAsync(
        Guid id,
        CancellationToken cancellationToken = default) =>
        await ReadSessionAsync(id, cancellationToken).ConfigureAwait(false);

    public async Task<List<GrimoireEntryDto>?> GetSessionEntriesAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        bool exists = await SessionExistsCoreAsync(sessionId, cancellationToken).ConfigureAwait(false);

        if (!exists)
        {
            return null;
        }

        int maxMessages = ArcanumSettingClamps.MaxMessagesPerConversationLoad(
            ArcanumRuntimeDefaults.Grimoire.MaxMessagesPerConversationLoad);

        int take = EntryWindowPolicy.ResolveTake(
            EntryWindowPolicy.EntryWindowKind.MaxMessagesOnly,
            maxMessages);

        List<Entry> recent = await EntryTemporalQueries
            .LoadRecentDescendingAsync(_db, sessionId, take, cancellationToken)
            .ConfigureAwait(false);

        recent.Reverse();

        return recent
            .Select(m => new GrimoireEntryDto(m.Id, m.Role, m.Content, m.ModelUsed, m.CreatedAt, m.IsPinned))
            .ToList();
    }

    public async Task<List<GrimoireEntryDto>?> GetRecentSessionEntriesAsync(
        Guid sessionId,
        int takeLast,
        CancellationToken cancellationToken = default)
    {
        bool exists = await SessionExistsCoreAsync(sessionId, cancellationToken).ConfigureAwait(false);

        if (!exists)
        {
            return null;
        }

        int maxMessages = ArcanumSettingClamps.MaxMessagesPerConversationLoad(
            ArcanumRuntimeDefaults.Grimoire.MaxMessagesPerConversationLoad);

        int clampedTake = EntryWindowPolicy.ResolveTake(
            EntryWindowPolicy.EntryWindowKind.ClampedTakeLast,
            maxMessages,
            requestedTake: takeLast);

        List<Entry> recent = await EntryTemporalQueries
            .LoadRecentDescendingAsync(_db, sessionId, clampedTake, cancellationToken)
            .ConfigureAwait(false);

        recent.Reverse();

        return recent
            .Select(m => new GrimoireEntryDto(m.Id, m.Role, m.Content, m.ModelUsed, m.CreatedAt, m.IsPinned))
            .ToList();
    }

    public async Task<GrimoireEntryDto?> GetEntryByIdAsync(
        Guid sessionId,
        Guid entryId,
        CancellationToken cancellationToken = default)
    {
        Entry? entry = await ReadEntryAsync(sessionId, entryId, cancellationToken).ConfigureAwait(false);

        return entry is null
            ? null
            : new GrimoireEntryDto(
                entry.Id,
                entry.Role,
                entry.Content,
                entry.ModelUsed,
                entry.CreatedAt,
                entry.IsPinned);
    }

    public async Task<bool> DeleteEntryAsync(
        Guid sessionId,
        Guid entryId,
        CancellationToken cancellationToken = default)
    {
        // The guard, not the purge. This method is reachable from anywhere in the process, and a caller
        // that skipped the sensitivity purge boundary would remove a labelled Entry without appending
        // its erasure receipt — leaving a finalization guard pointing at nothing, which is the one
        // integrity state that cannot be told apart from data loss (§10.20.2).
        Result unlabeled = await _labeledArtifactGuard
            .EnsureUnlabeledAsync(SensitiveArtifactKind.AssistantEntry, entryId, cancellationToken)
            .ConfigureAwait(false);

        if (unlabeled.IsFailure)
        {
            throw new InvalidOperationException(unlabeled.Error.Message);
        }

        using IDisposable entryLock = await SessionEntryPersistence.AcquireWriteLockAsync(sessionId, cancellationToken).ConfigureAwait(false);

        using IDisposable attachmentGate = await _attachments
            .AcquireSessionGateAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);

        await using var tx = await _db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            Entry? entry = await ReadEntryAsync(sessionId, entryId, cancellationToken)
                .ConfigureAwait(false);

            if (entry is null)
            {
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

                return false;
            }

            DateTime? watermark = await ReadSessionWatermarkAsync(sessionId, cancellationToken)
                .ConfigureAwait(false);

            bool isUnsummarized = watermark is null
                || entry.CreatedAt > new DateTimeOffset(watermark.Value, TimeSpan.Zero);

            await SqliteBusyRetry.ExecuteAsync(
                () => _attachments.ClearEntryIdsInAmbientTransactionAsync(sessionId, [entryId], cancellationToken),
                cancellationToken).ConfigureAwait(false);

            int deleted = await SqliteBusyRetry.ExecuteAsync(
                () => ExecuteNonQueryAsync(
                    """
                    DELETE FROM "Entries"
                    WHERE "SessionId" = $sessionId
                      AND "Id" = $entryId;
                    """,
                    command =>
                    {
                        GrimoireEntitySql.AddParameter(command, "$sessionId", GrimoireEntitySql.Format(sessionId));
                        GrimoireEntitySql.AddParameter(command, "$entryId", GrimoireEntitySql.Format(entryId));
                    },
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);

            if (deleted > 0 && isUnsummarized)
            {
                await _entryPersistence.DecrementUnsummarizedEntryCountIfKnownAsync(sessionId, 1, cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);

            return deleted > 0;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

            throw;
        }
    }

    public async Task<bool> SetEntryPinnedAsync(
        Guid sessionId,
        Guid entryId,
        bool pinned,
        CancellationToken cancellationToken = default)
    {
        using IDisposable _ = await SessionEntryPersistence.AcquireWriteLockAsync(sessionId, cancellationToken).ConfigureAwait(false);

        int updated = await SqliteBusyRetry.ExecuteAsync(
            () => ExecuteNonQueryAsync(
                """
                UPDATE "Entries"
                SET "IsPinned" = $pinned
                WHERE "SessionId" = $sessionId
                  AND "Id" = $entryId;
                """,
                command =>
                {
                    GrimoireEntitySql.AddParameter(command, "$pinned", pinned ? 1 : 0);
                    GrimoireEntitySql.AddParameter(command, "$sessionId", GrimoireEntitySql.Format(sessionId));
                    GrimoireEntitySql.AddParameter(command, "$entryId", GrimoireEntitySql.Format(entryId));
                },
                cancellationToken),
            cancellationToken).ConfigureAwait(false);

        return updated > 0;
    }

    public async Task<int> GetPinnedEntryCountAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            _db,
            """
            SELECT COUNT(*)
            FROM "Entries"
            WHERE "SessionId" = $sessionId
              AND "IsPinned" = 1;
            """,
            cancellationToken).ConfigureAwait(false);

        GrimoireEntitySql.AddParameter(command, "$sessionId", GrimoireEntitySql.Format(sessionId));

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    public async Task<string?> ReadLoreAsync(string key, CancellationToken cancellationToken = default)
    {
        await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            _db,
            """
            SELECT "Value"
            FROM "MageSettings"
            WHERE "Key" = $key
            LIMIT 1;
            """,
            cancellationToken).ConfigureAwait(false);

        GrimoireEntitySql.AddParameter(command, "$key", key);

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    public async Task<LoreDto> ScribeLoreAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        DateTime now = DateTime.UtcNow;

        await SqliteBusyRetry.ExecuteAsync(
            () => ExecuteNonQueryAsync(
                """
                INSERT INTO "MageSettings" ("Key", "Value", "UpdatedAt")
                VALUES ($key, $value, $updatedAt)
                ON CONFLICT("Key") DO UPDATE SET
                    "Value" = excluded."Value",
                    "UpdatedAt" = excluded."UpdatedAt";
                """,
                command =>
                {
                    GrimoireEntitySql.AddParameter(command, "$key", key);
                    GrimoireEntitySql.AddParameter(command, "$value", value);
                    GrimoireEntitySql.AddParameter(command, "$updatedAt", GrimoireEntitySql.Format(now));
                },
                cancellationToken),
            cancellationToken).ConfigureAwait(false);

        return new LoreDto(key, value, now);
    }

    public async Task<bool> DeleteLoreAsync(string key, CancellationToken cancellationToken = default)
    {
        return await SqliteBusyRetry.ExecuteAsync(
            () => ExecuteNonQueryAsync(
                """
                DELETE FROM "MageSettings"
                WHERE "Key" = $key;
                """,
                command => GrimoireEntitySql.AddParameter(command, "$key", key),
                cancellationToken),
            cancellationToken).ConfigureAwait(false) > 0;
    }

    public async Task<ListPageResult<LoreDto>> ListLoreAsync(
        int? limit = null,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        GrimoireSettings settings = ArcanumRuntimeDefaults.Grimoire;

        int pageSize = ArcanumSettingClamps.ListQueryLimit(
            limit ?? settings.DefaultLoreListLimit);

        int skip = Math.Max(0, offset);

        await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            _db,
            """
            SELECT "Key", "Value", "UpdatedAt"
            FROM "MageSettings"
            ORDER BY "Key"
            LIMIT $limit OFFSET $offset;
            """,
            cancellationToken).ConfigureAwait(false);

        GrimoireEntitySql.AddParameter(command, "$limit", pageSize + 1);
        GrimoireEntitySql.AddParameter(command, "$offset", skip);

        List<LoreDto> page = [];

        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                page.Add(ReadLore(reader));
            }
        }

        bool hasMore = page.Count > pageSize;

        if (hasMore)
        {
            page = page.Take(pageSize).ToList();
        }

        int? nextOffset = hasMore ? skip + pageSize : null;

        return new ListPageResult<LoreDto>(page.ToArray(), hasMore, nextOffset);
    }

    public async Task<LoreDto?> GetLoreAsync(string key, CancellationToken cancellationToken = default)
    {
        await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            _db,
            """
            SELECT "Key", "Value", "UpdatedAt"
            FROM "MageSettings"
            WHERE "Key" = $key
            LIMIT 1;
            """,
            cancellationToken).ConfigureAwait(false);

        GrimoireEntitySql.AddParameter(command, "$key", key);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadLore(reader)
            : null;
    }

    public async Task<string> SearchArchivesAsync(string query, int maxResults, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "No matching archives found.";
        }

        string trimmed = query.Trim();

        int maxQueryLen = ArcanumSettingClamps.ArchiveSearchMaxQueryLength(
            _arcOptions.Value.ResolveIntelligence().ArchiveSearchMaxQueryLength);

        if (trimmed.Length > maxQueryLen)
        {
            return "Archive search query is too long. Use a shorter phrase.";
        }

        string matchQuery = FtsMatchQuerySanitizer.Sanitize(trimmed);

        if (string.IsNullOrEmpty(matchQuery))
        {
            return "No matching archives found.";
        }

        int limit = Math.Clamp(maxResults, 1, 500);

        try
        {
            await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
                _db,
                """
                SELECT c."Role", c."Content", c."CreatedAt"
                FROM "Entries_fts"
                INNER JOIN "Entries" AS c ON c."Id" = "Entries_fts"."Id"
                WHERE "Entries_fts" MATCH @query
                ORDER BY rank
                LIMIT @limit
                """,
                cancellationToken).ConfigureAwait(false);

            GrimoireEntitySql.AddParameter(command, "@query", matchQuery);
            GrimoireEntitySql.AddParameter(command, "@limit", limit);

            await using SqliteDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            // LIMIT bounds the row count but not the row sizes, and the caller's tool-output cap only
            // rejects the finished string — so building past the cap allocates hundreds of megabytes
            // purely to have them thrown away. The budget is enforced while building instead, and a
            // row past it is counted without its Content ever being materialized.
            int budget = (int)Math.Min(
                ArcanumSettingClamps.ToolOutputCapBytes(
                    _arcOptions.Value.ResolveIntelligence().ToolOutputCapBytes),
                int.MaxValue / 2);

            StringBuilder sb = new();

            bool any = false;

            int omitted = 0;

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                any = true;

                if (sb.Length >= budget)
                {
                    omitted++;

                    continue;
                }

                MessageRole role = (MessageRole)reader.GetInt32(0);

                string content = reader.GetString(1);

                DateTimeOffset timestamp = UtcInstantText.Parse(reader.GetString(2));

                int room = budget - sb.Length;

                bool clipped = content.Length > room;

                _ = sb.Append('[')
                    .Append(timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                    .Append("] ")
                    .Append(role)
                    .Append(": ")
                    .Append(clipped ? ClipToBudget(content, room) : content);

                if (clipped)
                {
                    _ = sb.Append(" ... [TRUNCATED: this archived entry is larger than the remaining archive-search budget.]");
                }

                _ = sb.AppendLine();
            }

            if (omitted > 0)
            {
                _ = sb.Append("... [TRUNCATED: ")
                    .Append(omitted.ToString(CultureInfo.InvariantCulture))
                    .AppendLine(" further matches were not read; narrow 'query' with more specific keywords to reach them.]");
            }

            return any ? sb.ToString().TrimEnd() : "No matching archives found.";
        }
        catch (SqliteException ex)
        {
            _logger.LogWarning(ex, "FTS archive search failed for sanitized query.");

            return "Archive search could not run for that input. Try simpler keywords (letters, numbers, spaces).";
        }
    }

    /// <summary>Clips to the remaining budget without splitting a surrogate pair.</summary>
    private static string ClipToBudget(string content, int room)
    {
        int length = Math.Clamp(room, 0, content.Length);

        if (length > 0 && char.IsHighSurrogate(content[length - 1]))
        {
            length--;
        }

        return content[..length];
    }

    public async Task<List<Guid>> GetSessionsNeedingSummarizationAsync(
        int threshold,
        DateTime idleCutoff,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // These scalar queries keep filtering and ordering on SQLite's sortable UTC TEXT
        // columns. No session-count cap is applied here because CampaignLoggerQueue's capacity
        // limits pending work, not discovery.
        List<Guid> unknownIds = await ReadSessionIdsAsync(
            """
            SELECT "Id"
            FROM "Sessions"
            WHERE "UnsummarizedEntryCount" = -1
            ORDER BY "UpdatedAt", "Id"
            LIMIT $limit;
            """,
            command => GrimoireEntitySql.AddParameter(command, "$limit", MaxLegacyBackfillPerSweep),
            cancellationToken).ConfigureAwait(false);

        foreach (Guid sessionId in unknownIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using IDisposable sessionLock =
                await SessionWriteLock.AcquireAsync(sessionId, cancellationToken).ConfigureAwait(false);

            await SqliteBusyRetry.ExecuteAsync(
                async () =>
                {
                    await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
                        await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                    DateTime? watermark = await ReadSessionWatermarkAsync(sessionId, cancellationToken)
                        .ConfigureAwait(false);

                    DateTimeOffset cutoff = watermark is { } value
                        ? new DateTimeOffset(value, TimeSpan.Zero)
                        : DateTimeOffset.MinValue;

                    int count = await CountEntriesAfterAsync(
                        sessionId,
                        cutoff,
                        cancellationToken).ConfigureAwait(false);

                    if (AfterLegacyBackfillCountedForTesting is { } afterLegacyBackfillCounted)
                    {
                        await afterLegacyBackfillCounted(sessionId, cancellationToken).ConfigureAwait(false);
                    }

                    _ = await ExecuteNonQueryAsync(
                        """
                        UPDATE "Sessions"
                        SET "UnsummarizedEntryCount" = $count
                        WHERE "Id" = $sessionId;
                        """,
                        command =>
                        {
                            GrimoireEntitySql.AddParameter(command, "$count", count);
                            GrimoireEntitySql.AddParameter(command, "$sessionId", GrimoireEntitySql.Format(sessionId));
                        },
                        cancellationToken).ConfigureAwait(false);

                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        DateTime idleCutoffUtc = idleCutoff.Kind switch
        {
            DateTimeKind.Utc => idleCutoff,
            DateTimeKind.Local => idleCutoff.ToUniversalTime(),
            _ => DateTime.SpecifyKind(idleCutoff, DateTimeKind.Utc),
        };

        DateTimeOffset idleCutoffOffset = new(idleCutoffUtc);

        int effectiveThreshold = Math.Max(0, threshold);

        return await ReadSessionIdsAsync(
            """
            SELECT "Id"
            FROM "Sessions"
            WHERE "UnsummarizedEntryCount" = -1
               OR "UnsummarizedEntryCount" > $threshold
               OR
               (
                   "UnsummarizedEntryCount" > 0
                   AND "UpdatedAt" < $idleCutoff
               )
            ORDER BY "UpdatedAt", "Id";
            """,
            command =>
            {
                GrimoireEntitySql.AddParameter(command, "$threshold", effectiveThreshold);
                GrimoireEntitySql.AddParameter(command, "$idleCutoff", GrimoireEntitySql.Format(idleCutoffOffset));
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<Entry>> GetUnsummarizedEntriesAsync(
        Guid sessionId,
        DateTime watermark,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        DateTime watermarkUtc = watermark.Kind switch
        {
            DateTimeKind.Utc => watermark,
            DateTimeKind.Local => watermark.ToUniversalTime(),
            _ => DateTime.SpecifyKind(watermark, DateTimeKind.Utc),
        };

        DateTimeOffset watermarkOffset = new(watermarkUtc);

        int target = Math.Max(1, batchSize);

        int selectedCount = await EntryTemporalQueries
            .CountAfterWatermarkThroughTimestampGroupAsync(
                _db,
                sessionId,
                watermarkOffset,
                target,
                cancellationToken)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        List<Entry> entries = await EntryTemporalQueries
            .LoadAfterWatermarkThroughTimestampGroupAsync(
                _db,
                sessionId,
                watermarkOffset,
                target,
                selectedCount,
                cancellationToken)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        if (entries.Count != selectedCount)
        {
            throw new InvalidOperationException(
                $"Campaign log consolidation for session {sessionId} changed while its bounded "
                + $"window was being read (expected {selectedCount} entries, materialized "
                + $"{entries.Count}). The watermark was not advanced; retry after active writes finish.");
        }

        return entries;
    }

    public async Task<List<Entry>> GetSagaExtractionEntriesAsync(
        Guid sessionId,
        long afterSequence,
        long throughSequence,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (throughSequence <= afterSequence)
        {
            return [];
        }

        int target = Math.Max(1, batchSize);

        int selectedCount = await EntryTemporalQueries
            .CountSagaExtractionPageAsync(
                _db,
                sessionId,
                afterSequence,
                throughSequence,
                target,
                cancellationToken)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        List<Entry> entries = await EntryTemporalQueries
            .LoadSagaExtractionPageAsync(
                _db,
                sessionId,
                afterSequence,
                throughSequence,
                target,
                selectedCount,
                cancellationToken)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        if (entries.Count != selectedCount)
        {
            throw new InvalidOperationException(
                $"Saga extraction for session {sessionId} changed while its sequence-bounded "
                + $"window was being read (expected {selectedCount} entries, materialized "
                + $"{entries.Count}). The cursor was not advanced; retry after active writes finish.");
        }

        return entries;
    }

    public Task<bool> SessionExistsAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        SessionExistsCoreAsync(sessionId, cancellationToken);

    public async Task IncrementSessionTokensAsync(
        Guid sessionId,
        long totalTokens,
        CancellationToken cancellationToken = default)
    {
        if (totalTokens <= 0)
        {
            return;
        }

        _ = await SqliteBusyRetry.ExecuteAsync(
            () => ExecuteNonQueryAsync(
                """
                UPDATE "Sessions"
                SET "TotalTokensUsed" = "TotalTokensUsed" + $totalTokens
                WHERE "Id" = $sessionId;
                """,
                command =>
                {
                    GrimoireEntitySql.AddParameter(command, "$totalTokens", totalTokens);
                    GrimoireEntitySql.AddParameter(command, "$sessionId", GrimoireEntitySql.Format(sessionId));
                },
                cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task IncrementSessionTokensAndCostAsync(
        Guid sessionId,
        long totalTokens,
        decimal costUsd,
        CancellationToken cancellationToken = default)
    {
        long clampedTokens = Math.Max(0L, totalTokens);

        decimal clampedCost = Math.Max(0m, costUsd);

        if (clampedTokens == 0 && clampedCost == 0)
        {
            return;
        }

        _ = await SqliteBusyRetry.ExecuteAsync(
            () => IncrementSessionTokensAndCostWithinImmediateTransactionAsync(
                sessionId,
                clampedTokens,
                clampedCost,
                cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<decimal> GetTodaySpendAsync(CancellationToken cancellationToken = default)
    {
        return await SqliteBusyRetry.ExecuteAsync(async () =>
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;

            DateTimeOffset dayStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);

            DateTimeOffset dayEnd = dayStart.AddDays(1);

            await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
                _db,
                """
                SELECT "TotalCostUsd"
                FROM "Sessions"
                WHERE "CreatedAt" >= $dayStart AND "CreatedAt" < $dayEnd;
                """,
                cancellationToken).ConfigureAwait(false);

            GrimoireEntitySql.AddParameter(
                command,
                "$dayStart",
                UtcInstantText.Format(dayStart));
            GrimoireEntitySql.AddParameter(
                command,
                "$dayEnd",
                UtcInstantText.Format(dayEnd));

            decimal total = 0m;

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!reader.IsDBNull(0))
                {
                    total = ExactUsdText.CheckedAdd(total, ExactUsdText.Read(reader, 0));
                }
            }

            return total;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> IncrementSessionTokensAndCostWithinImmediateTransactionAsync(
        Guid sessionId,
        long tokens,
        decimal costUsd,
        CancellationToken cancellationToken)
    {
        if (_db.Database.GetDbConnection() is not SqliteConnection connection)
        {
            throw new InvalidOperationException("The Grimoire requires a SQLCipher connection.");
        }

        Result<IGrimoireOrdinaryConnectionLease> acquired = await _connections
            .AcquireScopedAsync(
                connection,
                CovenantSqliteConnectionMode.ReadWrite,
                cancellationToken)
            .ConfigureAwait(false);

        if (acquired.IsFailure)
        {
            throw new InvalidOperationException(acquired.Error.Message);
        }

        await using IGrimoireOrdinaryConnectionLease lease = acquired.Value;

        connection = lease.Connection;

        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        long currentTokens;

        decimal currentCost;

        await using (SqliteCommand read = connection.CreateCommand())
        {
            read.Transaction = transaction;

            read.CommandText =
                "SELECT \"TotalTokensUsed\", \"TotalCostUsd\" FROM \"Sessions\" WHERE \"Id\" = $sessionId;";

            GrimoireEntitySql.AddParameter(read, "$sessionId", GrimoireEntitySql.Format(sessionId));

            await using SqliteDataReader reader = await read
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return 0;
            }

            currentTokens = reader.GetInt64(0);

            currentCost = ExactUsdText.Read(reader, 1);
        }

        long updatedTokens = checked(currentTokens + tokens);

        decimal updatedCost = ExactUsdText.CheckedAdd(currentCost, costUsd);

        await using SqliteCommand update = connection.CreateCommand();

        update.Transaction = transaction;

        update.CommandText =
            "UPDATE \"Sessions\" SET \"TotalTokensUsed\" = $tokens, \"TotalCostUsd\" = $cost WHERE \"Id\" = $sessionId;";

        GrimoireEntitySql.AddParameter(update, "$tokens", updatedTokens);

        _ = ExactUsdText.AddParameter(update, "$cost", updatedCost);

        GrimoireEntitySql.AddParameter(update, "$sessionId", GrimoireEntitySql.Format(sessionId));

        int affected = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return affected;
    }

    public async Task RecordWorkspaceContextAsync(WorkspaceContext context, CancellationToken cancellationToken = default)
    {
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx =
            await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            _db.WorkspaceContexts.Add(context);

            await SqliteBusyRetry.ExecuteAsync(
                () => _db.SaveChangesAsync(cancellationToken),
                cancellationToken).ConfigureAwait(false);

            int retain = ArcanumSettingClamps.WorkspaceContextRetentionCount(
                ArcanumRuntimeDefaults.Grimoire.WorkspaceContextRetentionCount);

            List<WorkspaceContext> candidates = await ReadWorkspaceContextsAsync(
                context.WorkspacePath,
                cancellationToken).ConfigureAwait(false);

            List<Guid> idsToKeep = candidates
                .OrderByDescending(w => w.CreatedAt)
                .Take(retain)
                .Select(w => w.Id)
                .ToList();

            if (idsToKeep.Count >= retain)
            {
                _ = await SqliteBusyRetry.ExecuteAsync(
                    () => DeleteWorkspaceContextsExceptAsync(
                        context.WorkspacePath,
                        idsToKeep,
                        cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

            throw;
        }
    }

    public async Task<WorkspaceContext?> GetLatestWorkspaceContextAsync(
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        List<WorkspaceContext> rows = await ReadWorkspaceContextsAsync(
            workspacePath,
            cancellationToken).ConfigureAwait(false);

        return rows
            .OrderByDescending(w => w.CreatedAt)
            .FirstOrDefault();
    }

    public async Task AdvanceCampaignLogWatermarkAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        DateTime utcNow = DateTime.UtcNow;

        _ = await SqliteBusyRetry.ExecuteAsync(
            () => ExecuteNonQueryAsync(
                """
                UPDATE "Sessions"
                SET "LastSummarizedMessageAt" = COALESCE(
                        (
                            -- Core v9 makes every Entry instant fixed-width UTC text, so ordinal MAX
                            -- is chronological and preserves all seven fractional digits.
                            SELECT MAX("CreatedAt")
                            FROM "Entries"
                            WHERE "SessionId" = $sessionId
                        ),
                        $utcNow
                    ),
                    "UnsummarizedEntryCount" = 0
                WHERE "Id" = $sessionId;
                """,
                command =>
                {
                    GrimoireEntitySql.AddParameter(command, "$sessionId", GrimoireEntitySql.Format(sessionId));
                    GrimoireEntitySql.AddParameter(command, "$utcNow", GrimoireEntitySql.Format(utcNow));
                },
                cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateSessionCampaignRollupAsync(
        Guid sessionId,
        string summary,
        DateTime lastSummarizedMessageAt,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset watermark = new(lastSummarizedMessageAt, TimeSpan.Zero);

        using IDisposable sessionLock =
            await SessionWriteLock.AcquireAsync(sessionId, cancellationToken).ConfigureAwait(false);

        await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
                    await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                bool exists = await SessionExistsCoreAsync(sessionId, cancellationToken)
                    .ConfigureAwait(false);

                if (!exists)
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                    return;
                }

                int remaining = await CountEntriesAfterAsync(
                    sessionId,
                    watermark,
                    cancellationToken).ConfigureAwait(false);

                if (AfterRollupRemainingCountedForTesting is { } afterRollupRemainingCounted)
                {
                    await afterRollupRemainingCounted(sessionId, cancellationToken).ConfigureAwait(false);
                }

                _ = await ExecuteNonQueryAsync(
                    """
                    UPDATE "Sessions"
                    SET "Summary" = $summary,
                        "LastSummarizedMessageAt" = $watermark,
                        "UnsummarizedEntryCount" = $remaining
                    WHERE "Id" = $sessionId;
                    """,
                    command =>
                    {
                        GrimoireEntitySql.AddParameter(command, "$summary", summary);
                        GrimoireEntitySql.AddParameter(
                            command,
                            "$watermark",
                            GrimoireEntitySql.Format(lastSummarizedMessageAt));
                        GrimoireEntitySql.AddParameter(command, "$remaining", remaining);
                        GrimoireEntitySql.AddParameter(command, "$sessionId", GrimoireEntitySql.Format(sessionId));
                    },
                    cancellationToken).ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> ExecuteNonQueryAsync(
        string commandText,
        Action<SqliteCommand> bind,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            _db,
            commandText,
            cancellationToken).ConfigureAwait(false);

        bind(command);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> SessionExistsCoreAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            _db,
            """
            SELECT EXISTS(
                SELECT 1
                FROM "Sessions"
                WHERE "Id" = $sessionId
            );
            """,
            cancellationToken).ConfigureAwait(false);

        GrimoireEntitySql.AddParameter(command, "$sessionId", GrimoireEntitySql.Format(sessionId));

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0L;
    }

    private async Task<Guid> ReadEntrySessionIdAsync(
        Guid entryId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            _db,
            """
            SELECT "SessionId"
            FROM "Entries"
            WHERE "Id" = $entryId
            LIMIT 1;
            """,
            cancellationToken).ConfigureAwait(false);

        GrimoireEntitySql.AddParameter(command, "$entryId", GrimoireEntitySql.Format(entryId));

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull
            ? Guid.Empty
            : Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!);
    }

    private async Task<long?> ReadSessionLatestEntrySequenceAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            _db,
            """
            SELECT MAX("Sequence")
            FROM "Entries"
            WHERE "SessionId" = $sessionId;
            """,
            cancellationToken).ConfigureAwait(false);

        GrimoireEntitySql.AddParameter(command, "$sessionId", GrimoireEntitySql.Format(sessionId));

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull
            ? null
            : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private Task<Entry?> ReadEntryAsync(
        Guid entryId,
        CancellationToken cancellationToken) =>
        ReadEntryAsync(sessionId: null, entryId, cancellationToken);

    private async Task<Entry?> ReadEntryAsync(
        Guid? sessionId,
        Guid entryId,
        CancellationToken cancellationToken)
    {
        string commandText = sessionId is null
            ? $"SELECT {GrimoireEntitySql.EntryColumns} FROM \"Entries\" WHERE \"Id\" = $entryId LIMIT 1;"
            : $"SELECT {GrimoireEntitySql.EntryColumns} FROM \"Entries\" WHERE \"SessionId\" = $sessionId AND \"Id\" = $entryId LIMIT 1;";

        await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            _db,
            commandText,
            cancellationToken).ConfigureAwait(false);

        if (sessionId is { } boundSessionId)
        {
            GrimoireEntitySql.AddParameter(command, "$sessionId", GrimoireEntitySql.Format(boundSessionId));
        }

        GrimoireEntitySql.AddParameter(command, "$entryId", GrimoireEntitySql.Format(entryId));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? GrimoireEntitySql.ReadEntry(reader)
            : null;
    }

    private async Task<Session?> ReadSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            _db,
            $"SELECT {GrimoireEntitySql.SessionColumns} FROM \"Sessions\" WHERE \"Id\" = $sessionId LIMIT 1;",
            cancellationToken).ConfigureAwait(false);

        GrimoireEntitySql.AddParameter(command, "$sessionId", GrimoireEntitySql.Format(sessionId));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? GrimoireEntitySql.ReadSession(reader)
            : null;
    }

    private async Task<DateTime?> ReadSessionWatermarkAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            _db,
            """
            SELECT "LastSummarizedMessageAt"
            FROM "Sessions"
            WHERE "Id" = $sessionId
            LIMIT 1;
            """,
            cancellationToken).ConfigureAwait(false);

        GrimoireEntitySql.AddParameter(command, "$sessionId", GrimoireEntitySql.Format(sessionId));

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull
            ? null
            : UtcInstantText.ParseDateTime(Convert.ToString(value, CultureInfo.InvariantCulture)!);
    }

    private async Task<List<Guid>> ReadSessionIdsAsync(
        string commandText,
        Action<SqliteCommand> bind,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            _db,
            commandText,
            cancellationToken).ConfigureAwait(false);

        bind(command);

        List<Guid> ids = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(GrimoireEntitySql.ReadGuid(reader, 0));
        }

        return ids;
    }

    private async Task<List<WorkspaceContext>> ReadWorkspaceContextsAsync(
        string workspacePath,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            _db,
            """
            SELECT "Id", "CreatedAt", "RootPath", "SerializedSnapshot"
            FROM "WorkspaceContexts"
            WHERE "RootPath" = $workspacePath;
            """,
            cancellationToken).ConfigureAwait(false);

        GrimoireEntitySql.AddParameter(command, "$workspacePath", workspacePath);

        List<WorkspaceContext> rows = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new WorkspaceContext
            {
                Id = GrimoireEntitySql.ReadGuid(reader, 0),
                CreatedAt = GrimoireEntitySql.ReadDateTimeOffset(reader, 1),
                WorkspacePath = reader.GetString(2),
                SerializedSnapshot = reader.GetString(3),
            });
        }

        return rows;
    }

    private async Task<int> DeleteWorkspaceContextsExceptAsync(
        string workspacePath,
        IReadOnlyList<Guid> idsToKeep,
        CancellationToken cancellationToken)
    {
        if (idsToKeep.Count == 0)
        {
            return await ExecuteNonQueryAsync(
                """
                DELETE FROM "WorkspaceContexts"
                WHERE "RootPath" = $workspacePath;
                """,
                command => GrimoireEntitySql.AddParameter(command, "$workspacePath", workspacePath),
                cancellationToken).ConfigureAwait(false);
        }

        string[] placeholders = new string[idsToKeep.Count];

        for (int index = 0; index < idsToKeep.Count; index++)
        {
            placeholders[index] = "$keep" + index.ToString(CultureInfo.InvariantCulture);
        }

        string commandText =
            "DELETE FROM \"WorkspaceContexts\" WHERE \"RootPath\" = $workspacePath AND \"Id\" NOT IN ("
            + string.Join(", ", placeholders)
            + ");";

        return await ExecuteNonQueryAsync(
            commandText,
            command =>
            {
                GrimoireEntitySql.AddParameter(command, "$workspacePath", workspacePath);

                for (int index = 0; index < idsToKeep.Count; index++)
                {
                    GrimoireEntitySql.AddParameter(
                        command,
                        placeholders[index],
                        GrimoireEntitySql.Format(idsToKeep[index]));
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static LoreDto ReadLore(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            UtcInstantText.ParseDateTime(reader.GetString(2)));

    private async Task<int> CountEntriesAfterAsync(
        Guid sessionId,
        DateTimeOffset afterExclusive,
        CancellationToken cancellationToken)
    {
        return await EntryTemporalQueries
            .CountAfterAsync(_db, sessionId, afterExclusive, cancellationToken)
            .ConfigureAwait(false);
    }

    private SessionSettings GetSessionSettings() =>
        _arcOptions.Value.ResolveSessions();

    private static string TruncateTitle(string prompt)
    {
        string trimmed = prompt.Trim();
        const int maxLen = 200;
        return trimmed.Length <= maxLen ? trimmed : trimmed[..maxLen];
    }
}
