using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Repositories;

/// <summary>
/// Owns the cross-cutting invariants for session entry writes: per-session lock acquisition,
/// SQLite busy retry, entry-limit checks, unsummarized-entry counter maintenance, and UpdatedAt bumps.
/// </summary>
internal sealed class SessionEntryPersistence
{
    private readonly ArcanumDbContext _db;

    private readonly IGrimoireOrdinaryConnectionFactory _connections;

    public SessionEntryPersistence(
        ArcanumDbContext db,
        IGrimoireOrdinaryConnectionFactory connections)
    {
        _db = db;

        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    internal static Func<ToolInteractionReceipt, Exception?>?
        AfterMandatoryCommitForTests { get; set; }

    internal static Func<ToolInteractionReceipt, Exception?>?
        AfterMandatoryTransactionBeganForTests { get; set; }

    internal static Func<ToolInteractionReceipt, CancellationToken, ValueTask>?
        AfterMandatoryTransactionBeganAsyncForTests { get; set; }

    internal static Action<ToolInteractionReceipt>?
        BeforeMandatoryCancellationClassificationLockForTests { get; set; }

    internal static TimeSpan?
        MandatoryCancellationClassificationTimeoutForTests { get; set; }

    public static Task<IDisposable> AcquireWriteLockAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        return SessionWriteLock.AcquireAsync(sessionId, cancellationToken);
    }

    public static Error? CheckEntryLimits(
        int currentEntryCount,
        int entriesToAdd,
        SessionSettings? settings,
        params string?[] contents)
    {
        return GrimoireLimits.EnforceEntryLimits(currentEntryCount, entriesToAdd, settings, contents);
    }

    public Task<int> GetEntryCountAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        return ReadInt32Async(
            "SELECT COUNT(*) FROM \"Entries\" WHERE \"SessionId\" = $sessionId;",
            command => BindSessionId(command, sessionId),
            cancellationToken);
    }

    /// <summary>
    /// Reserves <paramref name="count"/> consecutive <see cref="Entry.Sequence"/> values for
    /// <paramref name="sessionId"/> and returns the first. Callers assign them in append order.
    /// Correct because every entry insert holds the per-session write lock, and the unique
    /// <c>(SessionId, Sequence)</c> index turns any escape into a write failure rather than a
    /// silently reordered transcript. Entry inserts use direct SQLite on this same connection and
    /// transaction, so the persisted maximum includes every earlier batch in the transaction.
    /// </summary>
    public async Task<long> ReserveSequenceRangeAsync(
        Guid sessionId,
        int count,
        CancellationToken cancellationToken = default)
    {
        if (count < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "At least one sequence value is required.");
        }

        long persistedMax = await SqliteBusyRetry.ExecuteAsync(
            () => ReadInt64Async(
                "SELECT COALESCE(MAX(\"Sequence\"), 0) FROM \"Entries\" WHERE \"SessionId\" = $sessionId;",
                command => BindSessionId(command, sessionId),
                cancellationToken),
            cancellationToken).ConfigureAwait(false);

        return persistedMax + 1L;
    }

    public Task BumpSessionUpdatedAtAsync(
        Guid sessionId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        return SqliteBusyRetry.ExecuteAsync(
            () => ExecuteNonQueryAsync(
                "UPDATE \"Sessions\" SET \"UpdatedAt\" = $updatedAt WHERE \"Id\" = $sessionId;",
                command =>
                {
                    BindSessionId(command, sessionId);
                    GrimoireEntitySql.AddParameter(
                        command,
                        "$updatedAt",
                        GrimoireEntitySql.Format(updatedAt));
                },
                cancellationToken),
            cancellationToken);
    }

    public Task InsertEntryAsync(
        Entry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return InsertEntriesAsync([entry], cancellationToken);
    }

    public async Task InsertEntriesAsync(
        IReadOnlyList<Entry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (entries.Count == 0)
        {
            return;
        }

        for (int index = 0; index < entries.Count; index++)
        {
            if (entries[index] is null)
            {
                throw new ArgumentException(
                    $"Entry batch element {index} is null.",
                    nameof(entries));
            }
        }

        await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            _db,
            """
            INSERT INTO "Entries"
                ("Id", "SessionId", "Role", "Content", "ModelUsed", "CreatedAt",
                 "Sequence", "ToolCallId", "ToolName", "ToolArguments", "IsPinned")
            VALUES
                ($id, $sessionId, $role, $content, $model, $createdAt,
                 $sequence, $toolCallId, $toolName, $toolArguments, $isPinned);
            """,
            cancellationToken).ConfigureAwait(false);
        BindEntry(command, entries[0]);

        foreach (Entry entry in entries)
        {
            SetEntryParameterValues(command, entry);

            await SqliteBusyRetry.ExecuteAsync(
                async () => _ = await command
                    .ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
        }
    }

    public Task IncrementUnsummarizedEntryCountIfKnownAsync(
        Guid sessionId,
        int delta,
        CancellationToken cancellationToken = default)
    {
        if (delta <= 0)
        {
            return Task.CompletedTask;
        }

        return SqliteBusyRetry.ExecuteAsync(
            () => ExecuteNonQueryAsync(
                """
                UPDATE "Sessions"
                SET "UnsummarizedEntryCount" = "UnsummarizedEntryCount" + $delta
                WHERE "Id" = $sessionId AND "UnsummarizedEntryCount" >= 0;
                """,
                command =>
                {
                    BindSessionId(command, sessionId);
                    GrimoireEntitySql.AddParameter(command, "$delta", delta);
                },
                cancellationToken),
            cancellationToken);
    }

    public Task DecrementUnsummarizedEntryCountIfKnownAsync(
        Guid sessionId,
        int delta,
        CancellationToken cancellationToken = default)
    {
        if (delta <= 0)
        {
            return Task.CompletedTask;
        }

        return SqliteBusyRetry.ExecuteAsync(
            () => ExecuteNonQueryAsync(
                """
                UPDATE "Sessions"
                SET "UnsummarizedEntryCount" = "UnsummarizedEntryCount" - $delta
                WHERE "Id" = $sessionId AND "UnsummarizedEntryCount" > 0;
                """,
                command =>
                {
                    BindSessionId(command, sessionId);
                    GrimoireEntitySql.AddParameter(command, "$delta", delta);
                },
                cancellationToken),
            cancellationToken);
    }

    internal async Task<MandatoryToolInteractionProbeResult>
        ProbeMandatoryToolInteractionAsync(
            MandatoryToolInteractionProbe probe,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probe);

        if (!HasValidProbe(probe))
        {
            return new MandatoryToolInteractionProbeResult(
                MandatoryToolInteractionProbeOutcome.Mismatched,
                Result: null);
        }

        using IDisposable writeLock = await AcquireWriteLockAsync(
            probe.SessionId,
            cancellationToken).ConfigureAwait(false);

        return await ReadProbeFreshAsync(
            probe,
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<MandatoryToolInteractionPreflightResult>
        PreflightMandatoryToolInteractionAsync(
            MandatoryToolInteraction interaction,
            SessionSettings? settings,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(interaction);

        if (!HasValidDeterministicIdentity(interaction)
            || !HasValidPayload(interaction))
        {
            return new MandatoryToolInteractionPreflightResult(
                MandatoryToolInteractionPreflightOutcome.Mismatched,
                Result: null);
        }

        Entry expectedCall = BuildCallEntry(interaction);

        Entry expectedResult = BuildResultEntry(interaction);

        using IDisposable writeLock = await AcquireWriteLockAsync(
            interaction.SessionId,
            cancellationToken).ConfigureAwait(false);

        ReceiptReadback existing = await ReadReceiptFreshAsync(
            expectedCall,
            expectedResult,
            cancellationToken).ConfigureAwait(false);

        if (existing.Classification == ReceiptReadbackClassification.Matching)
        {
            return new MandatoryToolInteractionPreflightResult(
                MandatoryToolInteractionPreflightOutcome.Replayed,
                interaction.Result);
        }

        if (existing.Classification is
            ReceiptReadbackClassification.PartialOrMismatched
            or ReceiptReadbackClassification.Unreadable)
        {
            return new MandatoryToolInteractionPreflightResult(
                existing.Classification
                    == ReceiptReadbackClassification.Unreadable
                        ? MandatoryToolInteractionPreflightOutcome.Unavailable
                        : MandatoryToolInteractionPreflightOutcome.Mismatched,
                Result: null);
        }

        bool sessionExists = await SessionExistsAsync(
            interaction.SessionId,
            cancellationToken).ConfigureAwait(false);

        if (!sessionExists)
        {
            return new MandatoryToolInteractionPreflightResult(
                MandatoryToolInteractionPreflightOutcome.Rejected,
                Result: null);
        }

        int entryCount = await GetEntryCountAsync(
            interaction.SessionId,
            cancellationToken).ConfigureAwait(false);

        Error? limitError = CheckEntryLimits(
            entryCount,
            entriesToAdd: 2,
            settings,
            expectedCall.Content,
            expectedResult.Content);

        return new MandatoryToolInteractionPreflightResult(
            limitError is null
                ? MandatoryToolInteractionPreflightOutcome.Admitted
                : MandatoryToolInteractionPreflightOutcome.Rejected,
            Result: null);
    }

    internal async Task<MandatoryToolInteractionAppendResult> AppendMandatoryToolInteractionAsync(
        MandatoryToolInteraction interaction,
        SessionSettings? settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(interaction);

        try
        {
            return await AppendMandatoryToolInteractionCoreAsync(
                interaction,
                settings,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException cancellation)
            when (!cancellation.Data.Contains(
                nameof(MandatoryToolInteractionAppendOutcome)))
        {
            cancellation.Data[nameof(MandatoryToolInteractionAppendOutcome)] =
                await ClassifyCancellationAsync(interaction).ConfigureAwait(false);

            throw;
        }
    }

    private async Task<MandatoryToolInteractionAppendResult> AppendMandatoryToolInteractionCoreAsync(
        MandatoryToolInteraction interaction,
        SessionSettings? settings,
        CancellationToken cancellationToken)
    {
        if (!HasValidDeterministicIdentity(interaction)
            || !HasValidPayload(interaction))
        {
            return Result(
                MandatoryToolInteractionAppendOutcome.Failed,
                interaction.Receipt);
        }

        Entry expectedCall = BuildCallEntry(interaction);

        Entry expectedResult = BuildResultEntry(interaction);

        using IDisposable writeLock = await AcquireWriteLockAsync(
            interaction.SessionId,
            cancellationToken).ConfigureAwait(false);

        ReceiptReadback existing = await ReadReceiptFreshAsync(
            expectedCall,
            expectedResult,
            cancellationToken).ConfigureAwait(false);

        switch (existing.Classification)
        {
            case ReceiptReadbackClassification.Matching:
                return Result(
                    MandatoryToolInteractionAppendOutcome.RecoveredCommitted,
                    interaction.Receipt);

            case ReceiptReadbackClassification.PartialOrMismatched:
            case ReceiptReadbackClassification.Unreadable:
                return Result(
                    MandatoryToolInteractionAppendOutcome.Ambiguous,
                    interaction.Receipt);

            case ReceiptReadbackClassification.None:
                break;

            default:
                return Result(
                    MandatoryToolInteractionAppendOutcome.Ambiguous,
                    interaction.Receipt);
        }

        IDbContextTransaction? transaction = null;

        try
        {
            transaction = await SqliteBusyRetry.ExecuteAsync(
                () => _db.Database.BeginTransactionAsync(cancellationToken),
                cancellationToken).ConfigureAwait(false);

            if (AfterMandatoryTransactionBeganForTests?.Invoke(interaction.Receipt)
                is Exception transactionInjected)
            {
                throw transactionInjected;
            }

            if (AfterMandatoryTransactionBeganAsyncForTests is not null)
            {
                await AfterMandatoryTransactionBeganAsyncForTests(
                    interaction.Receipt,
                    cancellationToken).ConfigureAwait(false);
            }

            bool sessionExists = await SessionExistsAsync(
                interaction.SessionId,
                cancellationToken).ConfigureAwait(false);

            if (!sessionExists)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return Result(
                    MandatoryToolInteractionAppendOutcome.Failed,
                    interaction.Receipt);
            }

            int entryCount = await GetEntryCountAsync(
                interaction.SessionId,
                cancellationToken).ConfigureAwait(false);

            Error? limitError = CheckEntryLimits(
                entryCount,
                entriesToAdd: 2,
                settings,
                expectedCall.Content,
                expectedResult.Content);

            if (limitError is not null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                return Result(
                    MandatoryToolInteractionAppendOutcome.Failed,
                    interaction.Receipt);
            }

            // The call and its result share one CreatedAt, so the sequence is what keeps the result
            // after its call.
            long firstSequence = await ReserveSequenceRangeAsync(
                interaction.SessionId,
                count: 2,
                cancellationToken).ConfigureAwait(false);

            expectedCall.Sequence = firstSequence;

            expectedResult.Sequence = firstSequence + 1L;

            await InsertEntriesAsync(
                [expectedCall, expectedResult],
                cancellationToken).ConfigureAwait(false);

            await BumpSessionUpdatedAtAsync(
                interaction.SessionId,
                interaction.CreatedAt,
                cancellationToken).ConfigureAwait(false);

            await IncrementUnsummarizedEntryCountIfKnownAsync(
                interaction.SessionId,
                delta: 2,
                cancellationToken).ConfigureAwait(false);

            await SqliteBusyRetry.ExecuteAsync(
                () => transaction.CommitAsync(cancellationToken),
                cancellationToken).ConfigureAwait(false);

            if (AfterMandatoryCommitForTests?.Invoke(interaction.Receipt) is Exception injected)
            {
                throw injected;
            }

            return Result(
                MandatoryToolInteractionAppendOutcome.NewlyCommitted,
                interaction.Receipt);
        }
        catch (OperationCanceledException cancellation)
        {
            using CancellationTokenSource classificationDeadline =
                new(TimeSpan.FromSeconds(30));

            bool transactionBegan = transaction is not null;

            bool rolledBack = await TryRollbackAsync(
                transaction,
                classificationDeadline.Token).ConfigureAwait(false);

            ReceiptReadback readback;

            try
            {
                readback = await ReadReceiptFreshAsync(
                    expectedCall,
                    expectedResult,
                    classificationDeadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (classificationDeadline.IsCancellationRequested)
            {
                readback = new ReceiptReadback(ReceiptReadbackClassification.Unreadable);
            }

            cancellation.Data[nameof(MandatoryToolInteractionAppendOutcome)] =
                ClassifyCancellationReadback(
                    readback,
                    definitiveNoCommit: !transactionBegan || rolledBack);

            throw;
        }
        catch (Exception exception) when (IsExpectedPersistenceFailure(exception))
        {
            using CancellationTokenSource classificationDeadline =
                new(TimeSpan.FromSeconds(30));

            bool rolledBack = await TryRollbackAsync(
                transaction,
                classificationDeadline.Token).ConfigureAwait(false);

            ReceiptReadback readback;

            try
            {
                readback = await ReadReceiptFreshAsync(
                    expectedCall,
                    expectedResult,
                    classificationDeadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (classificationDeadline.IsCancellationRequested)
            {
                readback = new ReceiptReadback(ReceiptReadbackClassification.Unreadable);
            }

            return readback.Classification switch
            {
                ReceiptReadbackClassification.Matching => Result(
                    MandatoryToolInteractionAppendOutcome.RecoveredCommitted,
                    interaction.Receipt),
                ReceiptReadbackClassification.None when rolledBack => Result(
                    MandatoryToolInteractionAppendOutcome.Failed,
                    interaction.Receipt),
                _ => Result(
                    MandatoryToolInteractionAppendOutcome.Ambiguous,
                    interaction.Receipt),
            };
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<MandatoryToolInteractionAppendOutcome> ClassifyCancellationAsync(
        MandatoryToolInteraction interaction)
    {
        if (!HasValidDeterministicIdentity(interaction)
            || !HasValidPayload(interaction))
        {
            return MandatoryToolInteractionAppendOutcome.Ambiguous;
        }

        TimeSpan timeout = MandatoryCancellationClassificationTimeoutForTests
            ?? TimeSpan.FromSeconds(30);

        using CancellationTokenSource deadline = new(timeout);

        try
        {
            BeforeMandatoryCancellationClassificationLockForTests?.Invoke(
                interaction.Receipt);

            // A definitive no-row result is only stable while this lock excludes every
            // in-process writer for the session. Lock acquisition and read-back share one
            // bounded cleanup token; inability to obtain that proof remains ambiguous.
            using IDisposable writeLock = await AcquireWriteLockAsync(
                interaction.SessionId,
                deadline.Token).ConfigureAwait(false);

            ReceiptReadback readback = await ReadReceiptFreshAsync(
                BuildCallEntry(interaction),
                BuildResultEntry(interaction),
                deadline.Token).ConfigureAwait(false);

            return ClassifyCancellationReadback(
                readback,
                definitiveNoCommit: true);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return MandatoryToolInteractionAppendOutcome.Ambiguous;
        }
    }

    private static MandatoryToolInteractionAppendOutcome ClassifyCancellationReadback(
        ReceiptReadback readback,
        bool definitiveNoCommit) =>
        readback.Classification switch
        {
            ReceiptReadbackClassification.Matching =>
                MandatoryToolInteractionAppendOutcome.RecoveredCommitted,
            ReceiptReadbackClassification.None when definitiveNoCommit =>
                MandatoryToolInteractionAppendOutcome.Failed,
            _ => MandatoryToolInteractionAppendOutcome.Ambiguous,
        };

    private async Task<MandatoryToolInteractionProbeResult> ReadProbeFreshAsync(
        MandatoryToolInteractionProbe probe,
        CancellationToken cancellationToken)
    {
        try
        {
            return await SqliteBusyRetry.ExecuteAsync(
                () => ReadProbeOnFreshConnectionAsync(
                    probe,
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is SqliteException
                or InvalidOperationException
                or FormatException
                or IOException)
        {
            return new MandatoryToolInteractionProbeResult(
                MandatoryToolInteractionProbeOutcome.Unavailable,
                Result: null);
        }
    }

    private async Task<MandatoryToolInteractionProbeResult>
        ReadProbeOnFreshConnectionAsync(
            MandatoryToolInteractionProbe probe,
            CancellationToken cancellationToken)
    {
        Result<IGrimoireOrdinaryConnectionLease> acquired = await _connections
            .OpenFreshAsync(
                GrimoireOrdinaryFreshConnectionKind.ReadOnly,
                cancellationToken)
            .ConfigureAwait(false);

        if (acquired.IsFailure)
        {
            return new MandatoryToolInteractionProbeResult(
                MandatoryToolInteractionProbeOutcome.Unavailable,
                Result: null);
        }

        await using IGrimoireOrdinaryConnectionLease lease = acquired.Value;

        SqliteConnection connection = lease.Connection;

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT "Id", "SessionId", "Role", "Content", "ModelUsed", "CreatedAt",
                   "ToolCallId", "ToolName", "ToolArguments", "IsPinned"
            FROM "Entries"
            WHERE "Id" = $callId OR "Id" = $resultId;
            """;

        _ = command.Parameters.AddWithValue(
            "$callId",
            Format(probe.Receipt.CallEntryId));

        _ = command.Parameters.AddWithValue(
            "$resultId",
            Format(probe.Receipt.ResultEntryId));

        List<EntryLogicalPayload> rows = [];

        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(ReadLogicalPayload(reader));
        }

        await GrimoireScopedConsumerTestSeam.PauseAsync(
            "SessionEntryPersistence.ReadProbeOnFreshConnectionAsync",
            GrimoireScopedConsumerFinalUseKind.ReaderMaterialized,
            rows.Count,
            cancellationToken).ConfigureAwait(false);

        EntryLogicalPayload? call = rows.SingleOrDefault(
            row => row.Id == probe.Receipt.CallEntryId);

        EntryLogicalPayload? result = rows.SingleOrDefault(
            row => row.Id == probe.Receipt.ResultEntryId);

        if (call is null && result is null)
        {
            return new MandatoryToolInteractionProbeResult(
                MandatoryToolInteractionProbeOutcome.NotFound,
                Result: null);
        }

        Entry expectedCall = BuildCallEntry(probe);

        if (call is not null
            && result is not null
            && call.Matches(expectedCall)
            && result.TryExtractResult(probe, out string? serializedResult))
        {
            return new MandatoryToolInteractionProbeResult(
                MandatoryToolInteractionProbeOutcome.Replayed,
                serializedResult);
        }

        return new MandatoryToolInteractionProbeResult(
            MandatoryToolInteractionProbeOutcome.Mismatched,
            Result: null);
    }

    private async Task<ReceiptReadback> ReadReceiptFreshAsync(
        Entry expectedCall,
        Entry expectedResult,
        CancellationToken cancellationToken)
    {
        try
        {
            return await SqliteBusyRetry.ExecuteAsync(
                () => ReadReceiptOnFreshConnectionAsync(
                    expectedCall,
                    expectedResult,
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is SqliteException
                or InvalidOperationException
                or FormatException
                or IOException)
        {
            return new ReceiptReadback(ReceiptReadbackClassification.Unreadable);
        }
    }

    private async Task<ReceiptReadback> ReadReceiptOnFreshConnectionAsync(
        Entry expectedCall,
        Entry expectedResult,
        CancellationToken cancellationToken)
    {
        Result<IGrimoireOrdinaryConnectionLease> acquired = await _connections
            .OpenFreshAsync(
                GrimoireOrdinaryFreshConnectionKind.ReadOnly,
                cancellationToken)
            .ConfigureAwait(false);

        if (acquired.IsFailure)
        {
            return new ReceiptReadback(ReceiptReadbackClassification.Unreadable);
        }

        await using IGrimoireOrdinaryConnectionLease lease = acquired.Value;

        SqliteConnection connection = lease.Connection;

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT "Id", "SessionId", "Role", "Content", "ModelUsed", "CreatedAt",
                   "ToolCallId", "ToolName", "ToolArguments", "IsPinned"
            FROM "Entries"
            WHERE "Id" = $callId OR "Id" = $resultId;
            """;

        _ = command.Parameters.AddWithValue("$callId", Format(expectedCall.Id));

        _ = command.Parameters.AddWithValue("$resultId", Format(expectedResult.Id));

        List<EntryLogicalPayload> rows = [];

        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(
                new EntryLogicalPayload(
                    Id: GrimoireEntitySql.ReadGuid(reader, 0),
                    SessionId: GrimoireEntitySql.ReadGuid(reader, 1),
                    Role: (MessageRole)reader.GetInt32(2),
                    Content: reader.GetString(3),
                    ModelUsed: reader.GetString(4),
                    CreatedAt: GrimoireEntitySql.ReadDateTimeOffset(reader, 5),
                    ToolCallId: GrimoireEntitySql.ReadNullableString(reader, 6),
                    ToolName: GrimoireEntitySql.ReadNullableString(reader, 7),
                    ToolArguments: GrimoireEntitySql.ReadNullableString(reader, 8),
                    IsPinned: reader.GetBoolean(9)));
        }

        await GrimoireScopedConsumerTestSeam.PauseAsync(
            "SessionEntryPersistence.ReadReceiptOnFreshConnectionAsync",
            GrimoireScopedConsumerFinalUseKind.ReaderMaterialized,
            rows.Count,
            cancellationToken).ConfigureAwait(false);

        EntryLogicalPayload? call = rows.SingleOrDefault(row => row.Id == expectedCall.Id);

        EntryLogicalPayload? result = rows.SingleOrDefault(row => row.Id == expectedResult.Id);

        if (call is null && result is null)
        {
            return new ReceiptReadback(ReceiptReadbackClassification.None);
        }

        if (call is not null
            && result is not null
            && call.Matches(expectedCall)
            && result.Matches(expectedResult))
        {
            return new ReceiptReadback(ReceiptReadbackClassification.Matching);
        }

        return new ReceiptReadback(ReceiptReadbackClassification.PartialOrMismatched);
    }

    private static async Task<bool> TryRollbackAsync(
        IDbContextTransaction? transaction,
        CancellationToken cleanupToken)
    {
        if (transaction is null)
        {
            return true;
        }

        try
        {
            await transaction.RollbackAsync(cleanupToken).ConfigureAwait(false);

            return true;
        }
        catch (Exception exception) when (
            exception is SqliteException
                or InvalidOperationException
                or ObjectDisposedException
                or OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<bool> SessionExistsAsync(
        Guid sessionId,
        CancellationToken cancellationToken) =>
        await ReadInt32Async(
            "SELECT EXISTS(SELECT 1 FROM \"Sessions\" WHERE \"Id\" = $sessionId);",
            command => BindSessionId(command, sessionId),
            cancellationToken).ConfigureAwait(false) != 0;

    private async Task ExecuteNonQueryAsync(
        string commandText,
        Action<SqliteCommand> bind,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            _db,
            commandText,
            cancellationToken).ConfigureAwait(false);
        bind(command);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> ReadInt32Async(
        string commandText,
        Action<SqliteCommand> bind,
        CancellationToken cancellationToken)
    {
        object? value = await ReadScalarAsync(commandText, bind, cancellationToken).ConfigureAwait(false);

        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<long> ReadInt64Async(
        string commandText,
        Action<SqliteCommand> bind,
        CancellationToken cancellationToken)
    {
        object? value = await ReadScalarAsync(commandText, bind, cancellationToken).ConfigureAwait(false);

        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<object?> ReadScalarAsync(
        string commandText,
        Action<SqliteCommand> bind,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = await GrimoireSqlCommandFactory.CreateAsync(
            _db,
            commandText,
            cancellationToken).ConfigureAwait(false);
        bind(command);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void BindEntry(SqliteCommand command, Entry entry)
    {
        GrimoireEntitySql.AddParameter(command, "$id", Format(entry.Id));
        GrimoireEntitySql.AddParameter(command, "$sessionId", Format(entry.SessionId));
        GrimoireEntitySql.AddParameter(command, "$role", (int)entry.Role);
        GrimoireEntitySql.AddParameter(command, "$content", entry.Content);
        GrimoireEntitySql.AddParameter(command, "$model", entry.ModelUsed);
        GrimoireEntitySql.AddParameter(command, "$createdAt", GrimoireEntitySql.Format(entry.CreatedAt));
        GrimoireEntitySql.AddParameter(command, "$sequence", entry.Sequence);
        GrimoireEntitySql.AddParameter(command, "$toolCallId", entry.ToolCallId);
        GrimoireEntitySql.AddParameter(command, "$toolName", entry.ToolName);
        GrimoireEntitySql.AddParameter(command, "$toolArguments", entry.ToolArguments);
        GrimoireEntitySql.AddParameter(command, "$isPinned", entry.IsPinned);
    }

    private static void SetEntryParameterValues(SqliteCommand command, Entry entry)
    {
        command.Parameters["$id"].Value = Format(entry.Id);
        command.Parameters["$sessionId"].Value = Format(entry.SessionId);
        command.Parameters["$role"].Value = (int)entry.Role;
        command.Parameters["$content"].Value = entry.Content;
        command.Parameters["$model"].Value = entry.ModelUsed;
        command.Parameters["$createdAt"].Value = GrimoireEntitySql.Format(entry.CreatedAt);
        command.Parameters["$sequence"].Value = entry.Sequence;
        command.Parameters["$toolCallId"].Value = entry.ToolCallId ?? (object)DBNull.Value;
        command.Parameters["$toolName"].Value = entry.ToolName ?? (object)DBNull.Value;
        command.Parameters["$toolArguments"].Value = entry.ToolArguments ?? (object)DBNull.Value;
        command.Parameters["$isPinned"].Value = entry.IsPinned;
    }

    private static void BindSessionId(SqliteCommand command, Guid sessionId) =>
        GrimoireEntitySql.AddParameter(command, "$sessionId", Format(sessionId));

    private static string Format(Guid value) => value.ToString("D").ToUpperInvariant();

    private static Entry BuildCallEntry(MandatoryToolInteraction interaction) =>
        new()
        {
            Id = interaction.Receipt.CallEntryId,
            SessionId = interaction.SessionId,
            Role = MessageRole.Assistant,
            Content = $"[ToolCall: {interaction.ToolName}({interaction.Arguments})]",
            ModelUsed = interaction.ModelUsed,
            CreatedAt = interaction.CreatedAt,
            ToolCallId = interaction.ToolCallId,
            ToolName = interaction.ToolName,
            ToolArguments = interaction.Arguments,
        };

    private static Entry BuildCallEntry(MandatoryToolInteractionProbe probe) =>
        new()
        {
            Id = probe.Receipt.CallEntryId,
            SessionId = probe.SessionId,
            Role = MessageRole.Assistant,
            Content = $"[ToolCall: {probe.ToolName}({probe.Arguments})]",
            ModelUsed = probe.ModelUsed,
            CreatedAt = probe.CreatedAt,
            ToolCallId = probe.ToolCallId,
            ToolName = probe.ToolName,
            ToolArguments = probe.Arguments,
        };

    private static Entry BuildResultEntry(MandatoryToolInteraction interaction) =>
        new()
        {
            Id = interaction.Receipt.ResultEntryId,
            SessionId = interaction.SessionId,
            Role = MessageRole.System,
            Content = $"[ToolResult: {interaction.Result}]",
            ModelUsed = interaction.ModelUsed,
            CreatedAt = interaction.CreatedAt,
        };

    private static bool HasValidDeterministicIdentity(MandatoryToolInteraction interaction) =>
        interaction.Receipt.Id != Guid.Empty
        && interaction.Receipt.CallEntryId
            == ToolInteractionReceiptDerivation.DeriveCallEntryId(interaction.Receipt.Id)
        && interaction.Receipt.ResultEntryId
            == ToolInteractionReceiptDerivation.DeriveResultEntryId(interaction.Receipt.Id)
        && interaction.Receipt.CallEntryId != interaction.Receipt.ResultEntryId;

    private static bool HasValidProbe(MandatoryToolInteractionProbe probe) =>
        probe.Receipt.Id != Guid.Empty
        && probe.Receipt.CallEntryId
            == ToolInteractionReceiptDerivation.DeriveCallEntryId(
                probe.Receipt.Id)
        && probe.Receipt.ResultEntryId
            == ToolInteractionReceiptDerivation.DeriveResultEntryId(
                probe.Receipt.Id)
        && probe.Receipt.CallEntryId != probe.Receipt.ResultEntryId
        && probe.SessionId != Guid.Empty
        && !string.IsNullOrWhiteSpace(probe.ToolName)
        && probe.ToolName.Length <= 256
        && (probe.ToolCallId is null || probe.ToolCallId.Length <= 256)
        && probe.Arguments is not null
        && !string.IsNullOrWhiteSpace(probe.ModelUsed)
        && probe.ModelUsed.Length <= 256;

    private static bool HasValidPayload(MandatoryToolInteraction interaction) =>
        interaction.SessionId != Guid.Empty
        && !string.IsNullOrWhiteSpace(interaction.ToolName)
        && interaction.ToolName.Length <= 256
        && (interaction.ToolCallId is null || interaction.ToolCallId.Length <= 256)
        && interaction.Arguments is not null
        && interaction.Result is not null
        && !string.IsNullOrWhiteSpace(interaction.ModelUsed)
        && interaction.ModelUsed.Length <= 256;

    private static bool IsExpectedPersistenceFailure(Exception exception) =>
        exception is SqliteException
            or InvalidOperationException
            or IOException;

    private static MandatoryToolInteractionAppendResult Result(
        MandatoryToolInteractionAppendOutcome outcome,
        ToolInteractionReceipt receipt) =>
        new(outcome, receipt);

    private static EntryLogicalPayload ReadLogicalPayload(
        SqliteDataReader reader) =>
        new(
            Id: GrimoireEntitySql.ReadGuid(reader, 0),
            SessionId: GrimoireEntitySql.ReadGuid(reader, 1),
            Role: (MessageRole)reader.GetInt32(2),
            Content: reader.GetString(3),
            ModelUsed: reader.GetString(4),
            CreatedAt: GrimoireEntitySql.ReadDateTimeOffset(reader, 5),
            ToolCallId: GrimoireEntitySql.ReadNullableString(reader, 6),
            ToolName: GrimoireEntitySql.ReadNullableString(reader, 7),
            ToolArguments: GrimoireEntitySql.ReadNullableString(reader, 8),
            IsPinned: reader.GetBoolean(9));

    private enum ReceiptReadbackClassification
    {
        None,
        Matching,
        PartialOrMismatched,
        Unreadable,
    }

    private readonly record struct ReceiptReadback(
        ReceiptReadbackClassification Classification);

    private sealed record EntryLogicalPayload(
        Guid Id,
        Guid SessionId,
        MessageRole Role,
        string Content,
        string ModelUsed,
        DateTimeOffset CreatedAt,
        string? ToolCallId,
        string? ToolName,
        string? ToolArguments,
        bool IsPinned)
    {
        internal bool Matches(Entry expected) =>
            Id == expected.Id
            && SessionId == expected.SessionId
            && Role == expected.Role
            && string.Equals(Content, expected.Content, StringComparison.Ordinal)
            && string.Equals(ModelUsed, expected.ModelUsed, StringComparison.Ordinal)
            && CreatedAt.EqualsExact(expected.CreatedAt)
            && string.Equals(ToolCallId, expected.ToolCallId, StringComparison.Ordinal)
            && string.Equals(ToolName, expected.ToolName, StringComparison.Ordinal)
            && string.Equals(ToolArguments, expected.ToolArguments, StringComparison.Ordinal)
            && IsPinned == expected.IsPinned;

        internal bool TryExtractResult(
            MandatoryToolInteractionProbe probe,
            out string? result)
        {
            const string prefix = "[ToolResult: ";

            result = null;

            if (Id != probe.Receipt.ResultEntryId
                || SessionId != probe.SessionId
                || Role != MessageRole.System
                || !string.Equals(
                    ModelUsed,
                    probe.ModelUsed,
                    StringComparison.Ordinal)
                || !CreatedAt.EqualsExact(probe.CreatedAt)
                || ToolCallId is not null
                || ToolName is not null
                || ToolArguments is not null
                || IsPinned
                || !Content.StartsWith(prefix, StringComparison.Ordinal)
                || !Content.EndsWith(']'))
            {
                return false;
            }

            result = Content[prefix.Length..^1];

            return true;
        }
    }
}
