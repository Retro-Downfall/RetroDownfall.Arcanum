using System.Data.Common;
using System.Globalization;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Repositories;

/// <summary>
/// Owns the cross-cutting invariants for session entry writes: per-session lock acquisition,
/// SQLite busy retry, entry-limit checks, unsummarized-entry counter maintenance, and UpdatedAt bumps.
/// </summary>
internal sealed class SessionEntryPersistence
{

    private readonly ArcanumDbContext _db;

    public SessionEntryPersistence(ArcanumDbContext db)
    {

        _db = db;

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

        return _db.Entries.CountAsync(e => e.SessionId == sessionId, cancellationToken);

    }

    public Task BumpSessionUpdatedAtAsync(
        Guid sessionId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {

        return SqliteBusyRetry.ExecuteAsync(
            () => _db.Sessions
                .Where(s => s.Id == sessionId)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(x => x.UpdatedAt, updatedAt),
                    cancellationToken),
            cancellationToken);

    }

    public Task SaveChangesWithRetryAsync(CancellationToken cancellationToken = default)
    {

        return EfSaveChangesRetry.ExecuteAsync(_db, cancellationToken);

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
            () => _db.Sessions
                .Where(s => s.Id == sessionId && s.UnsummarizedEntryCount >= 0)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(x => x.UnsummarizedEntryCount, x => x.UnsummarizedEntryCount + delta),
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
            () => _db.Sessions
                .Where(s => s.Id == sessionId && s.UnsummarizedEntryCount > 0)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(x => x.UnsummarizedEntryCount, x => x.UnsummarizedEntryCount - delta),
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

        bool sessionExists = await _db.Sessions
            .AsNoTracking()
            .AnyAsync(
                session => session.Id == interaction.SessionId,
                cancellationToken)
            .ConfigureAwait(false);

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

            bool sessionExists = await _db.Sessions
                .AsNoTracking()
                .AnyAsync(
                    session => session.Id == interaction.SessionId,
                    cancellationToken)
                .ConfigureAwait(false);

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

            _db.Entries.Add(expectedCall);

            _db.Entries.Add(expectedResult);

            await BumpSessionUpdatedAtAsync(
                interaction.SessionId,
                interaction.CreatedAt,
                cancellationToken).ConfigureAwait(false);

            await SaveChangesWithRetryAsync(cancellationToken).ConfigureAwait(false);

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

            DetachReceiptEntries(interaction.Receipt);

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

            DetachReceiptEntries(interaction.Receipt);

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

        string? connectionString = _db.Database.GetConnectionString();

        if (string.IsNullOrWhiteSpace(connectionString))
        {

            return new MandatoryToolInteractionProbeResult(
                MandatoryToolInteractionProbeOutcome.Unavailable,
                Result: null);

        }

        await using SqliteConnection connection = new(connectionString);

        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

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
            probe.Receipt.CallEntryId);

        _ = command.Parameters.AddWithValue(
            "$resultId",
            probe.Receipt.ResultEntryId);

        List<EntryLogicalPayload> rows = [];

        await using DbDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            rows.Add(ReadLogicalPayload(reader));

        }

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

        string? connectionString = _db.Database.GetConnectionString();

        if (string.IsNullOrWhiteSpace(connectionString))
        {

            return new ReceiptReadback(ReceiptReadbackClassification.Unreadable);

        }

        await using SqliteConnection connection = new(connectionString);

        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT "Id", "SessionId", "Role", "Content", "ModelUsed", "CreatedAt",
                   "ToolCallId", "ToolName", "ToolArguments", "IsPinned"
            FROM "Entries"
            WHERE "Id" = $callId OR "Id" = $resultId;
            """;

        _ = command.Parameters.AddWithValue("$callId", expectedCall.Id);

        _ = command.Parameters.AddWithValue("$resultId", expectedResult.Id);

        List<EntryLogicalPayload> rows = [];

        await using DbDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            rows.Add(
                new EntryLogicalPayload(
                    Id: reader.GetGuid(0),
                    SessionId: reader.GetGuid(1),
                    Role: (MessageRole)reader.GetInt32(2),
                    Content: reader.GetString(3),
                    ModelUsed: reader.GetString(4),
                    CreatedAt: ReadDateTimeOffset(reader, 5),
                    ToolCallId: ReadNullableString(reader, 6),
                    ToolName: ReadNullableString(reader, 7),
                    ToolArguments: ReadNullableString(reader, 8),
                    IsPinned: reader.GetBoolean(9)));

        }

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

    private void DetachReceiptEntries(ToolInteractionReceipt receipt)
    {

        foreach (EntityEntry<Entry> tracked in _db.ChangeTracker
            .Entries<Entry>()
            .Where(entry =>
                entry.Entity.Id == receipt.CallEntryId
                || entry.Entity.Id == receipt.ResultEntryId)
            .ToArray())
        {

            tracked.State = EntityState.Detached;

        }

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
            or DbUpdateException
            or InvalidOperationException
            or IOException;

    private static MandatoryToolInteractionAppendResult Result(
        MandatoryToolInteractionAppendOutcome outcome,
        ToolInteractionReceipt receipt) =>
        new(outcome, receipt);

    private static DateTimeOffset ReadDateTimeOffset(DbDataReader reader, int ordinal)
    {

        object value = reader.GetValue(ordinal);

        return value is DateTimeOffset timestamp
            ? timestamp
            : DateTimeOffset.Parse(
                Convert.ToString(value, CultureInfo.InvariantCulture)!,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);

    }

    private static string? ReadNullableString(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : reader.GetString(ordinal);

    private static EntryLogicalPayload ReadLogicalPayload(
        DbDataReader reader) =>
        new(
            Id: reader.GetGuid(0),
            SessionId: reader.GetGuid(1),
            Role: (MessageRole)reader.GetInt32(2),
            Content: reader.GetString(3),
            ModelUsed: reader.GetString(4),
            CreatedAt: ReadDateTimeOffset(reader, 5),
            ToolCallId: ReadNullableString(reader, 6),
            ToolName: ReadNullableString(reader, 7),
            ToolArguments: ReadNullableString(reader, 8),
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
